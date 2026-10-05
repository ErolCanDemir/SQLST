using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

public class BatchYurutucuTests
{
    private static readonly QueryService Servis = new();

    private static IReadOnlyList<SqlBatch> Bol(string sql) => SqlCozumleyici.BatchlereBol(sql);

    [Fact]
    public async Task Tek_batch_dogrudan_calisir_ve_sarmalanmaz()
    {
        var oturum = new SayanOturum();

        QueryResult sonuc = await BatchYurutucu.CalistirAsync(
            Servis, oturum, Bol("SELECT 1"), ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(sonuc.Basarili);
        Assert.Equal(1, oturum.CagriSayisi);
    }

    [Fact]
    public async Task Coklu_batch_sirayla_ayni_oturumda_calisir()
    {
        var oturum = new SayanOturum();

        await BatchYurutucu.CalistirAsync(
            Servis, oturum, Bol("SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3"),
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.Equal(3, oturum.CagriSayisi);
        Assert.Contains("SELECT 1", oturum.Gonderilenler[0]);
        Assert.Contains("SELECT 3", oturum.Gonderilenler[2]);
    }

    [Fact]
    public async Task Go_n_batchi_n_kez_calistirir()
    {
        var oturum = new SayanOturum();

        await BatchYurutucu.CalistirAsync(
            Servis, oturum, Bol("INSERT INTO t DEFAULT VALUES\nGO 4"),
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.Equal(4, oturum.CagriSayisi);
    }

    [Fact]
    public async Task Hatali_batch_sonrakini_durdurmaz_ve_satir_orijinale_eslenir()
    {
        // 3. satırda başlayan batch'in 2. satırında hata → orijinal script satırı 4
        var oturum = new SayanOturum(hataVerenCagri: 2, hataSatiri: 2);

        QueryResult sonuc = await BatchYurutucu.CalistirAsync(
            Servis, oturum, Bol("SELECT 1\nGO\nSELECT 2\nHATALI SATIR\nGO\nSELECT 3"),
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.Equal(3, oturum.CagriSayisi); // hata sonrası 3. batch de çalıştı (SSMS davranışı)
        Assert.False(sonuc.Basarili);
        Assert.NotNull(sonuc.Hata);
        Assert.Equal(4, sonuc.Hata!.Satir); // batch başı 3 + sunucu satırı 2 - 1
    }

    [Fact]
    public async Task Tek_batchte_satir_tabani_kaydirir()
    {
        // İmleçteki statement belge ortasından gönderilir: sunucu satırı 1 + taban 7 - 1 = 7
        var oturum = new SayanOturum(hataVerenCagri: 1, hataSatiri: 1);

        QueryResult sonuc = await BatchYurutucu.CalistirAsync(
            Servis, oturum, Bol("HATALI"), ExecuteOptions.Varsayilan,
            CancellationToken.None, satirTabani: 7);

        Assert.Equal(7, sonuc.Hata!.Satir);
    }

    [Fact]
    public async Task Iptal_edilen_batch_kalanlari_calistirmaz()
    {
        var oturum = new SayanOturum(iptalEdilenCagri: 1);

        QueryResult sonuc = await BatchYurutucu.CalistirAsync(
            Servis, oturum, Bol("SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3"),
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(sonuc.IptalEdildi);
        Assert.Equal(1, oturum.CagriSayisi);
    }

    [Fact]
    public async Task Sonuclar_ve_satir_sayilari_toplanir()
    {
        var oturum = new SayanOturum(); // her çağrı 1 set + 2 satır döner

        QueryResult sonuc = await BatchYurutucu.CalistirAsync(
            Servis, oturum, Bol("SELECT 1\nGO\nSELECT 2"),
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.Equal(2, sonuc.ResultSetler.Count);
        Assert.Equal(4, sonuc.ToplamSatir);
    }

    [Fact]
    public async Task Bellek_butcesi_batchler_arasi_tasinir_ve_bayrak_korunur()
    {
        // İnceleme bulgusu (2026-07-23): bütçe batch başına SIFIRLANIYORDU — "GO 20"li geniş
        // sorgu 20 × 256 MB toplayıp OOM kalkanını deliyordu; BellekSiniriAsildi bayrağı da
        // çok-batch yolunda kayboluyordu. Artık kalan bütçe sonraki batch'e devredilir
        // (taban 1 — batch yine ÇALIŞIR, yalnız materyalizasyon kısılır) ve bayrak toplanır.
        var oturum = new ButceliOturum(baytlar: [200, 60, 40]);
        var opts = new ExecuteOptions { BellekSiniriBayt = 256, SatirSiniri = 1000 };

        QueryResult sonuc = await BatchYurutucu.CalistirAsync(
            Servis, oturum, Bol("SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3"), opts, CancellationToken.None);

        Assert.Equal([256, 56, 1], oturum.AlinanButceler); // tam → kalan → taban(1); hep çalıştı
        Assert.True(sonuc.BellekSiniriAsildi);             // 2. batch'te dolan bütçenin bayrağı yaşıyor
        Assert.Equal(300, sonuc.ToplamBayt);               // muhasebe toplamı doğru
    }

    /// <summary>Her çağrıda aldığı bellek bütçesini kaydedip verilen baytı "tüketen" sahte oturum.</summary>
    private sealed class ButceliOturum(long[] baytlar) : IDbOturum
    {
        private int _cagri;
        public ConnectionProfile Profil { get; } = new();
        public List<long> AlinanButceler { get; } = [];

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
        {
            if (sql.StartsWith("SET TRANSACTION ISOLATION LEVEL", StringComparison.Ordinal))
                return Task.FromResult(new QueryResult { Basarili = true });

            long bayt = baytlar[_cagri++];
            AlinanButceler.Add(opts.BellekSiniriBayt);
            bool asildi = bayt >= opts.BellekSiniriBayt;
            return Task.FromResult(new QueryResult
            {
                Basarili = true,
                ToplamBayt = bayt,
                SatirSiniriAsildi = asildi,
                BellekSiniriAsildi = asildi,
            });
        }

        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => Task.FromResult(IslemDurumu.Yok);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Çağrıları sayan/kaydeden sahte oturum; istenirse N. çağrıda hata ya da iptal üretir.</summary>
    /// <summary>
    /// Bu testler "kaç BATCH gönderildi" sorusunu ölçer. MSSQL'de her batch'ten önce ayrı
    /// bir izolasyon komutu gider (2026-07-19 kullanıcı bulgusu düzeltmesi: ön ek artık
    /// kullanıcının SQL'iyle aynı batch'e konmuyor); o komut BATCH DEĞİLDİR, sayılmaz.
    /// </summary>
    private sealed class SayanOturum(int hataVerenCagri = 0, int hataSatiri = 1, int iptalEdilenCagri = 0) : IDbOturum
    {
        public ConnectionProfile Profil { get; } = new();
        public int CagriSayisi { get; private set; }
        public List<string> Gonderilenler { get; } = [];

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
        {
            // İzolasyon komutu sayıma ve listeye girmez — testlerin ölçtüğü şey batch'ler.
            if (sql.StartsWith("SET TRANSACTION ISOLATION LEVEL", StringComparison.Ordinal))
                return Task.FromResult(new QueryResult { Basarili = true });

            CagriSayisi++;
            Gonderilenler.Add(sql);

            if (CagriSayisi == iptalEdilenCagri)
                return Task.FromResult(new QueryResult { IptalEdildi = true });

            if (CagriSayisi == hataVerenCagri)
            {
                return Task.FromResult(new QueryResult
                {
                    Hata = new SqlHata("sahte hata", 102, hataSatiri, 15),
                });
            }

            return Task.FromResult(new QueryResult
            {
                Basarili = true,
                ToplamSatir = 2,
                ResultSetler = [new ResultSetData { Kolonlar = [new KolonBilgisi("x", "int")], Satirlar = [[1], [2]] }],
            });
        }

        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => Task.FromResult(IslemDurumu.Yok);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
