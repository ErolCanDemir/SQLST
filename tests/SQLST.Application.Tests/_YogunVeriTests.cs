using System.Data;
using System.Diagnostics;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;
using Xunit.Abstractions;

namespace SQLST.Application.Tests;

/// <summary>
/// GEÇİCİ yoğun-veri stres turu (kullanıcı isteği 2026-07-30: "tüm özellikleri yoğun datalarla test et").
/// LocalDB üzerinde gerçek yürütme hattını büyük hacimle zorlar; süre/bellek ölçümleri test çıktısına
/// yazılır. Zaman assert'leri BİLEREK gevşek (amaç ölçüm + patlama yakalamak, mikro-benchmark değil).
/// </summary>
public class _YogunVeriTests
{
    private readonly ITestOutputHelper _out;
    public _YogunVeriTests(ITestOutputHelper o) => _out = o;

    private static ConnectionProfile Profil() => new()
    {
        Ad = "stres", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
    };

    private static SqlExecutor Executor() => new(new LehceSaglayici(new DpapiSecretProtector()));

    // ── 1) 250k dar satır: satır sınırı keser, bellek makul kalır ────────────
    [Fact]
    public async Task Dev_sonuc_satir_sinirinda_kesilir_bellek_makul()
    {
        long once = GC.GetTotalMemory(forceFullCollection: true);
        var sure = Stopwatch.StartNew();

        QueryResult sonuc = await Executor().ExecuteAsync(Profil(),
            """
            SELECT TOP (250000)
                   ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS Id,
                   a.name AS Ad, b.name AS Ad2, a.object_id AS N1, b.column_id AS N2
            FROM sys.all_columns a CROSS JOIN sys.all_columns b
            """,
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);

        sure.Stop();
        long sonra = GC.GetTotalMemory(forceFullCollection: false);
        _out.WriteLine($"süre={sure.ElapsedMilliseconds} ms · satır={sonuc.ToplamSatir} · " +
                       $"tahminiBayt={sonuc.ToplamBayt / 1024 / 1024} MB · yığınDelta≈{(sonra - once) / 1024 / 1024} MB");

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.True(sonuc.SatirSiniriAsildi);           // 250k istendi → en geç 100k'da kesildi
        // v20-S15/S16 sonrası sözleşme ÜÇ geçerli sonuç tanır (bu 16GB süit makinesi gerçekten
        // bellek baskısında çalışıyor — 2026-08-08; çökme/donma yerine kesme TASARIMDIR):
        //  • makine rahat: tam 100k satır, kesen SATIR sınırı
        //  • makine BASKIDA (yük ≥ eşiğin %85'i): uyarlanır bütçe → 10k satırda kesim + açık mesaj
        //  • okuma sırasında KRİTİK eşiğe varıldı: nöbetçi kesti, BellekSiniriAsildi + açık mesaj
        if (sonuc.Mesajlar.Any(m => m.Contains("bellek baskısı")))
            Assert.InRange(sonuc.ToplamSatir, 1, 10_000);
        else if (sonuc.BellekSiniriAsildi)
        {
            Assert.InRange(sonuc.ToplamSatir, 1, 100_000);
            Assert.Contains(sonuc.Mesajlar, m => m.Contains("kritik eşiğe"));
        }
        else
        {
            Assert.Equal(100_000, sonuc.ToplamSatir);   // dar satırlar — kesen SATIR sınırı olmalı
        }
        Assert.True(sure.ElapsedMilliseconds < 60_000);
    }

    // ── 2) MB'lık hücreler: bellek bütçesi keser (OOM kalkanı — dünkü crash düzeltmesinin kanıtı) ──
    [Fact]
    public async Task MB_hucreli_genis_satirlar_bellek_butcesinde_kesilir()
    {
        var sure = Stopwatch.StartNew();
        QueryResult sonuc = await Executor().ExecuteAsync(Profil(),
            // hücre başına ~1MB (500k nchar) × 300 satır = ~300MB ham → 96MB bütçe erken kesmeli
            "SELECT TOP (300) REPLICATE(CAST('x' AS nvarchar(max)), 500000) AS Buyuk FROM sys.all_columns;",
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        sure.Stop();

        _out.WriteLine($"süre={sure.ElapsedMilliseconds} ms · satır={sonuc.ToplamSatir} · " +
                       $"tahminiBayt={sonuc.ToplamBayt / 1024 / 1024} MB · bellekAsildi={sonuc.BellekSiniriAsildi}");

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.True(sonuc.BellekSiniriAsildi);           // kesen BELLEK bütçesi
        Assert.InRange(sonuc.ToplamSatir, 1, 299);       // 300'ün tamamı ASLA materyalize edilmedi
        // v20-S16 uyarlanır bütçe: baskılı makinede bütçe 24MB'a iner — dolan bütçe ona göre ölçülür
        long beklenenButce = sonuc.Mesajlar.Any(m => m.Contains("bellek baskısı"))
            ? 24L * 1024 * 1024
            : 96L * 1024 * 1024;
        Assert.True(sonuc.ToplamBayt >= beklenenButce);
    }

    // ── 3) Çakışan/boş kolon adları grid paketini ÇÖKERTMEZ (DataTable DuplicateName regresyonu) ──
    [Fact]
    public async Task Ayni_adli_ve_adsiz_kolonlar_grid_paketine_sorunsuz_cevrilir()
    {
        QueryResult sonuc = await Executor().ExecuteAsync(Profil(),
            "SELECT 1 AS X, 2 AS X, 3, 4 AS X;",
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        SonucSeti seti = SonucBicimleyici.TabloyaCevir(sonuc.ResultSetler[0]); // çökmemeli
        var adlar = seti.Tablo.Columns.Cast<DataColumn>().Select(k => k.ColumnName).ToList();
        _out.WriteLine("kolonlar: " + string.Join(" | ", adlar));

        Assert.Equal(4, adlar.Count);
        Assert.Equal(adlar.Count, adlar.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // hepsi tekil
        Assert.Contains("X", adlar);
        Assert.Contains("X_2", adlar);
    }

    // ── 4) 2MB editör metninde otomatik tamamlama gecikmesi (her tuşta çağrılır!) ──
    [Fact]
    public void Tamamlama_2MB_metinde_tus_basina_hizli_kalmali()
    {
        var onbellek = new SemaOnbellegi
        {
            YuklenmeZamaniUtc = DateTime.UtcNow,
            Nesneler = [.. Enumerable.Range(1, 60).Select(i => new SemaNesnesi(
                "db", "dbo", $"Tablo{i}", SemaNesneTuru.Tablo,
                [.. Enumerable.Range(1, 15).Select(k => new SemaKolonu($"Kolon{k}", "int", false, k == 1))], []))],
        };

        // ~2MB sorgu defteri: 10.000 ifade, boş satırla ayrık (gerçek kullanım deseni)
        string blok = "SELECT Kolon1, Kolon2 FROM dbo.Tablo1 t1 INNER JOIN dbo.Tablo2 t2 ON t2.Kolon1 = t1.Kolon1 WHERE t1.Kolon3 > 100 ORDER BY t1.Kolon1 DESC;\r\n\r\n";
        string metin = string.Concat(Enumerable.Repeat(blok, 10_000)) + "SELECT * FROM dbo.Tablo5 WHERE ";
        _out.WriteLine($"metin={metin.Length / 1024} KB");

        // ısınma + ölçüm: imleç SONDA (en kötü durum — yorum/dize taraması tüm metni gezer)
        OtoTamamlama.Oner(metin, metin.Length, onbellek, out _);
        var sure = Stopwatch.StartNew();
        const int N = 20;
        for (int i = 0; i < N; i++)
            OtoTamamlama.Oner(metin, metin.Length, onbellek, out _);
        sure.Stop();
        double onerMs = sure.Elapsed.TotalMilliseconds / N;

        var sure2 = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
            OtoTamamlama.OtoAcilmali(metin, metin.Length);
        sure2.Stop();
        double acilmaliMs = sure2.Elapsed.TotalMilliseconds / N;

        _out.WriteLine($"Oner ortalama={onerMs:F1} ms · OtoAcilmali ortalama={acilmaliMs:F1} ms");
        Assert.True(onerMs < 500, $"Oner {onerMs:F1} ms — 2MB metinde tuş başına ÇOK yavaş");
        Assert.True(acilmaliMs < 500, $"OtoAcilmali {acilmaliMs:F1} ms — boşluk başına ÇOK yavaş");
    }

    // ── 5) 100k log mesajı gruplama ──────────────────────────────────────────
    [Fact]
    public void Log_gruplama_100k_mesajda_dogru_ve_hizli()
    {
        var rnd = new Random(42);
        string[] sablonlar =
        [
            "Timeout expired waiting for connection {0}",
            "Tablo [{0}] bulunamadı",
            "NULL reference at line {0} in module {0}",
            "Kullanıcı {0} yetkisiz erişim denedi (IP 10.0.0.{0})",
            "Deadlock victim: process {0}",
        ];
        var mesajlar = new List<object?>(100_000);
        for (int i = 0; i < 100_000; i++)
            mesajlar.Add(string.Format(sablonlar[i % sablonlar.Length], rnd.Next(1, 99999)));

        var sure = Stopwatch.StartNew();
        IReadOnlyList<TabloLogGrubu> gruplar = LogTabloAnaliz.Grupla(mesajlar, enFazla: 50);
        sure.Stop();
        _out.WriteLine($"süre={sure.ElapsedMilliseconds} ms · grup={gruplar.Count} · ilk={gruplar[0].Sayi}");

        Assert.Equal(5, gruplar.Count);                  // 5 şablon → 5 imza (parametreler normalize)
        Assert.Equal(20_000, gruplar[0].Sayi);           // eşit dağılım
        Assert.True(sure.ElapsedMilliseconds < 30_000);
    }

    // ── 6) 100k satırlık CSV üretimi + kaçışlama doğruluğu ───────────────────
    [Fact]
    public void Csv_100k_satirda_hizli_ve_kacislar_dogru()
    {
        var tablo = new DataTable();
        tablo.Columns.Add("Id", typeof(object));
        tablo.Columns.Add("Ad", typeof(object));
        tablo.Columns.Add("Not", typeof(object));
        for (int i = 0; i < 100_000; i++)
            tablo.Rows.Add(i, $"Müşteri {i}", i % 1000 == 0 ? "çok; \"özel\"\nsatır" : "normal");

        var sure = Stopwatch.StartNew();
        string csv = CsvYazici.Metin(tablo);
        sure.Stop();
        _out.WriteLine($"süre={sure.ElapsedMilliseconds} ms · boyut={csv.Length / 1024} KB");

        Assert.Contains("\"çok; \"\"özel\"\"\nsatır\"", csv); // RFC 4180: tırnak ikileme + sarma
        Assert.True(sure.ElapsedMilliseconds < 30_000);
    }
}
