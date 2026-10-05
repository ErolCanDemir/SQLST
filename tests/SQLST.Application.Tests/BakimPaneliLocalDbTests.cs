using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S3 Bakım paneli CANLI doğrulaması (LocalDB). Bu dilimin sorguları katalog değil
/// <b>DMV</b> okur; şekilleri ancak gerçek sunucuda doğrulanır.
///
/// Bloklama zinciri testi bilerek <b>gerçek bir bloklama üretir</b>: "sorgu hata vermeden
/// çalıştı" demek burada yeterli değildir — zincir mantığı yanlışsa sorgu yine başarıyla
/// ama BOŞ döner. Nitekim bu testi tasarlarken ilk sürümdeki hata bulundu: kök bloklayan
/// uykudayken <c>dm_exec_requests</c>'te satırı olmadığından zincir boş kalıyordu.
/// </summary>
/// <summary>
/// Bu testler SUNUCU GENELİ DMV'lere bakar (dm_exec_requests, tempdb kullanımı), dolayısıyla
/// paralel koşan diğer LocalDB testlerinin ürettiği gürültüden etkilenirler — tam süitte
/// ~5 koşuda 1 düşüyorlardı, izole koşuda hep geçiyorlardı. Doğru çözüm iddiaları gevşetmek
/// değil (o zaman test bir şey kanıtlamaz), koleksiyonu paralellikten ayırmaktır.
/// </summary>
[CollectionDefinition(TekBasinaKosanBakim.Ad, DisableParallelization = true)]
public sealed class TekBasinaKosanBakim
{
    public const string Ad = "bakim-paneli-tek-basina";
}

[Collection(TekBasinaKosanBakim.Ad)]
public class BakimPaneliLocalDbTests : IAsyncLifetime
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly ExecuteOptions Tempdb = new() { VeritabaniOverride = "tempdb" };

    private readonly string _tablo = $"Bakim_{Guid.NewGuid():N}";
    private readonly SqlExecutor _executor = new(new DpapiSecretProtector());
    private TeshisServisi Servis() => new(_executor);

    public async Task InitializeAsync()
    {
        QueryResult r = await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{_tablo}] (Id INT PRIMARY KEY, Grup INT NOT NULL, Ad NVARCHAR(100));
            INSERT INTO dbo.[{_tablo}] (Id, Grup, Ad)
            SELECT TOP 5000 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 50, REPLICATE(N'x', 100)
            FROM sys.all_objects o1 CROSS JOIN sys.all_objects o2;
            CREATE INDEX IX_{_tablo}_Grup ON dbo.[{_tablo}] (Grup) INCLUDE (Ad);
            UPDATE STATISTICS dbo.[{_tablo}];
            """, Tempdb, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);   // sessiz kurulum hatası testi yanlış yönlendirir
    }

    public async Task DisposeAsync()
        => await _executor.ExecuteAsync(Profil(),
            $"DROP TABLE IF EXISTS dbo.[{_tablo}];", Tempdb, CancellationToken.None);

    /// <summary>
    /// Panel sadeleştirmesi (kullanıcı kararı 2026-07-19) — set sayıları sabitlenir ki
    /// kaldırılan mükerrer setler sessizce geri gelmesin.
    ///
    /// <b>RCSI tek set kalmalı:</b> "tempdb version store" ve "en eski açık işlemler"
    /// kaldırıldı — ikisi de Bakım sekmesindeki daha iyi karşılıklarıyla mükerrerdi.
    /// <b>Havuz üç set kalmalı:</b> "bloklanan oturumlar" kaldırıldı — Bakım'daki bloklama
    /// zinciri kökü de buluyor, eski set yalnız doğrudan bloklayanı gösteriyordu.
    /// </summary>
    [Fact]
    public async Task Sadelestirme_KORUNUR_mukerrer_setler_geri_gelmez()
    {
        QueryResult rcsi = await Servis().RcsiVerileriAsync(Profil(), CancellationToken.None);
        Assert.True(rcsi.Basarili, rcsi.Hata?.Mesaj);
        Assert.Single(rcsi.ResultSetler);

        QueryResult havuz = await Servis().HavuzVerileriAsync(Profil(), CancellationToken.None);
        Assert.True(havuz.Basarili, havuz.Hata?.Mesaj);
        Assert.Equal(3, havuz.ResultSetler.Count);

        // Havuz'un ilk seti hâlâ "en pahalı sorgular" olmalı (sıra kayması olmasın)
        Assert.Contains(havuz.ResultSetler[0].Kolonlar, k => k.Ad.Contains("CPU", StringComparison.Ordinal));
    }

    /// <summary>
    /// v22-S1 saha turu-2 m.14 (kullanıcı: "index önerileri ve yavaş sorgular alanı SQL'de neden yok,
    /// olması gerekiyor"): Mongo panelindeki karşılığın SQL'i. İKİ set döner — ortalama süreye ve
    /// toplam süreye göre. GERÇEK SQL Server'da (LocalDB) koşar: DMV adı/CROSS APPLY/kolon takma adı
    /// yanlışsa burada kırmızı yanar (saf test bunu yakalayamaz).
    /// </summary>
    [Fact]
    public async Task Yavas_sorgular_iki_set_doner_ve_sureye_gore_sirali()
    {
        // Plan önbelleği SUNUCU GENELİdir ve komşu testlerin DROP DATABASE'i onu TÜMDEN boşaltır
        // (SQL Server'da tam flush yapan işlemlerden biri). Isıtma ile okuma arasına denk gelirse
        // sorgu başarıyla ama BOŞ döner — tam süitte kırmızı, izole koşuda yeşil. İddiaları
        // gevşetmek yerine (o zaman sıralama hiç doğrulanmaz) ısıt-oku turunu tekrarlıyoruz.
        QueryResult r = null!;
        for (int tur = 0; tur < 4; tur++)
        {
            await _executor.ExecuteAsync(Profil(),
                $"SELECT COUNT(*) FROM dbo.[{_tablo}] a JOIN dbo.[{_tablo}] b ON a.Grup = b.Grup;",
                Tempdb, CancellationToken.None);

            r = await Servis().YavasSorgularAsync(Profil(), CancellationToken.None);
            if (r.Basarili && r.ResultSetler.Count == 2 && r.ResultSetler[0].Satirlar.Count > 0)
                break;
        }

        Assert.True(r.Basarili, r.Hata?.Mesaj);
        Assert.Equal(2, r.ResultSetler.Count);

        ResultSetData ortalama = r.ResultSetler[0];
        Assert.Contains(ortalama.Kolonlar, k => k.Ad == "ort süre (ms)");
        Assert.Contains(ortalama.Kolonlar, k => k.Ad == "en uzun (ms)");
        Assert.Contains(ortalama.Kolonlar, k => k.Ad == "çalıştırma");
        Assert.Contains(ortalama.Kolonlar, k => k.Ad == "sorgu");
        Assert.True(ortalama.Satirlar.Count > 0, "plan önbelleğinden hiç sorgu gelmedi");

        // ORTALAMA süreye göre AZALAN sıralı olmalı (kullanıcı en yavaşı üstte görsün)
        double[] ortalamalar = [.. ortalama.Satirlar.Select(s => Convert.ToDouble(s[0]))];
        Assert.True(ortalamalar.SequenceEqual(ortalamalar.OrderByDescending(x => x)),
            $"ortalama süre sıralaması bozuk: {string.Join(", ", ortalamalar.Take(5))}");

        ResultSetData toplam = r.ResultSetler[1];
        Assert.Contains(toplam.Kolonlar, k => k.Ad == "toplam süre (ms)");
        double[] toplamlar = [.. toplam.Satirlar.Select(s => Convert.ToDouble(s[0]))];
        Assert.True(toplamlar.SequenceEqual(toplamlar.OrderByDescending(x => x)),
            "toplam süre sıralaması bozuk");
    }

    [Fact]
    public async Task Bakim_alti_set_doner_ve_tempdb_ozeti_dolu()
    {
        QueryResult r = await Servis().BakimVerileriAsync(Profil(), "tempdb", CancellationToken.None);

        Assert.True(r.Basarili, r.Hata?.Mesaj);
        Assert.Equal(6, r.ResultSetler.Count); // V5-S3 4 set + R1.3 bekleme analizi 2 set (2026-07-26)

        // 1. set: tempdb dağılımı — tek satır, dört ölçü kolonu
        ResultSetData tempdbOzeti = r.ResultSetler[0];
        Assert.Equal(4, tempdbOzeti.Kolonlar.Count);
        Assert.Single(tempdbOzeti.Satirlar);

        // 5-6. setler (R1.3 bekleme analizi): bekleyen istekler + sunucu geneli wait stats.
        // LocalDB boşta olduğundan 5. set boş olabilir — KOLONLARI doğrulanır; 6. set uptime
        // boyunca birikmiş beklemelerden en az bir satır döndürür (yüzde kolonu dahil).
        Assert.Contains(r.ResultSetler[4].Kolonlar, k => k.Ad == "bekleme türü");
        Assert.Contains(r.ResultSetler[4].Kolonlar, k => k.Ad == "bloklayan");
        Assert.Contains(r.ResultSetler[5].Kolonlar, k => k.Ad == "yüzde");
        Assert.True(r.ResultSetler[5].Satirlar.Count > 0, "wait stats boş dönmemeli");
    }

    [Fact]
    public async Task Istatistik_tazeligi_yeni_guncellenen_tabloyu_taze_gosterir()
    {
        QueryResult r = await Servis().BakimVerileriAsync(Profil(), "tempdb", CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);

        ResultSetData istatistik = r.ResultSetler[3];
        int tabloKolonu = Kolon(istatistik, "tablo");
        int yasKolonu = Kolon(istatistik, "yaş (gün)");

        // InitializeAsync'te UPDATE STATISTICS koştuk → tablomuz listede ve YAŞI 0 olmalı.
        // Bu, sorgunun gerçekten dm_db_stats_properties okuduğunu kanıtlar; "çalıştı" demez.
        object?[]? bizim = istatistik.Satirlar
            .FirstOrDefault(s => s[tabloKolonu]?.ToString()?.Contains(_tablo, StringComparison.Ordinal) == true);

        Assert.True(bizim is not null, $"yeni oluşturulan {_tablo} istatistik listesinde yok");
        Assert.Equal(0, Convert.ToInt32(bizim![yasKolonu]));
    }

    /// <summary>
    /// GERÇEK bloklama üretir: A oturumu satırı kilitleyip AÇIK işlemle uykuya geçer,
    /// B oturumu aynı satırı güncellemeye çalışıp bloklanır. Panel, kök bloklayan olarak
    /// A'yı göstermelidir — A uykuda olduğu için <c>dm_exec_requests</c>'te satırı yoktur.
    /// </summary>
    [Fact]
    public async Task Bloklama_zinciri_UYUYAN_kok_bloklayani_bulur()
    {
        var fabrika = new OturumFabrikasi(new DpapiSecretProtector());
        await using IDbOturum a = fabrika.Olustur(Profil());
        await using IDbOturum b = fabrika.Olustur(Profil());

        QueryResult spidSonuc = await a.CalistirAsync("SELECT @@SPID;", Tempdb, CancellationToken.None);
        int aSpid = Convert.ToInt32(spidSonuc.ResultSetler[0].Satirlar[0][0]);

        // A: satırı kilitle, işlemi AÇIK bırak → komut biter, oturum UYKUYA geçer.
        QueryResult kilit = await a.CalistirAsync(
            $"BEGIN TRAN; UPDATE dbo.[{_tablo}] SET Ad = N'kilitli' WHERE Id = 1;",
            Tempdb, CancellationToken.None);
        Assert.True(kilit.Basarili, kilit.Hata?.Mesaj);

        // B: aynı satır → bloklanır. BEKLEMEDEN başlat; bu görev A rollback edene dek asılı kalır.
        using var iptal = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<QueryResult> bloklanan = b.CalistirAsync(
            $"UPDATE dbo.[{_tablo}] SET Ad = N'bekleyen' WHERE Id = 1;", Tempdb, iptal.Token);

        try
        {
            Assert.True(await BloklamaOlusanaKadarBekleAsync(aSpid, iptal.Token),
                "test bloklaması 30 sn içinde oluşmadı — ortam sorunu");

            QueryResult r = await Servis().BakimVerileriAsync(Profil(), "tempdb", CancellationToken.None);
            Assert.True(r.Basarili, r.Hata?.Mesaj);

            ResultSetData zincir = r.ResultSetler[2];
            int oturumK = Kolon(zincir, "oturum");
            int derinlikK = Kolon(zincir, "derinlik");
            int durumK = Kolon(zincir, "durum");

            object?[]? kok = zincir.Satirlar.FirstOrDefault(s =>
                Convert.ToInt32(s[oturumK]) == aSpid && Convert.ToInt32(s[derinlikK]) == 0);

            Assert.True(kok is not null,
                $"kök bloklayan (spid {aSpid}) zincirde yok — uyuyan kök yakalanamıyor");
            // Kökün UYKUDA olduğunu da doğrula: sorunun tam olarak bu yüzden zor olduğunu kanıtlar.
            Assert.Equal("sleeping", kok![durumK]?.ToString());

            // Bloklanan oturum bir alt derinlikte görünmeli.
            Assert.Contains(zincir.Satirlar, s => Convert.ToInt32(s[derinlikK]) == 1);
        }
        finally
        {
            await a.CalistirAsync("IF @@TRANCOUNT > 0 ROLLBACK;", Tempdb, CancellationToken.None);
            try { await bloklanan; } catch (Exception) { /* iptal/rollback sonrası önemsiz */ }
        }
    }

    /// <summary>Kolon adından sıra numarası; kolon yoksa test anlamlı bir mesajla düşer.</summary>
    private static int Kolon(ResultSetData set, string ad)
    {
        for (int i = 0; i < set.Kolonlar.Count; i++)
            if (set.Kolonlar[i].Ad == ad)
                return i;

        Assert.Fail($"'{ad}' kolonu yok. Gelen kolonlar: {string.Join(", ", set.Kolonlar.Select(k => k.Ad))}");
        return -1;
    }

    /// <summary>Bloklama oluşana dek kısa aralıklarla yoklar (sunucu tarafı zamanlama).</summary>
    private async Task<bool> BloklamaOlusanaKadarBekleAsync(int bloklayanSpid, CancellationToken ct)
    {
        for (int deneme = 0; deneme < 60 && !ct.IsCancellationRequested; deneme++)
        {
            QueryResult r = await _executor.ExecuteAsync(Profil(),
                $"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = {bloklayanSpid};",
                Tempdb, CancellationToken.None);

            if (r.Basarili && Convert.ToInt32(r.ResultSetler[0].Satirlar[0][0]) > 0)
                return true;

            await Task.Delay(250, ct);
        }
        return false;
    }
}
