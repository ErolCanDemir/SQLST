using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Gerçek LocalDB'ye karşı entegrasyon testleri (bu makinede LocalDB kurulu;
/// SQLEXPRESS yok). Amaç: SqlExecutor'ın gerçek sunucuyla akış/mesaj/hata
/// davranışını kanıtlamak — S1 "Test Et" düğmesinin dayandığı yol budur.
/// </summary>
public class SqlExecutorLocalDbTests
{
    private static ConnectionProfile LocalDbProfili() => new()
    {
        Ad = "localdb",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 60, // LocalDB ilk açılışta yavaş olabilir
    };

    private static SqlExecutor Yeni() => new(new DpapiSecretProtector());

    [Fact]
    public async Task Sunucuya_veritabanisiz_baglanilir_ve_override_calisir()
    {
        // FG-1.1: profilde veritabanı yok — bağlantı sunucu varsayılanına gider
        QueryResult varsayilan = await Yeni().ExecuteAsync(
            LocalDbProfili(), "SELECT DB_NAME() AS Aktif;", ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.True(varsayilan.Basarili, varsayilan.Hata?.Mesaj);
        Assert.False(string.IsNullOrWhiteSpace((string?)varsayilan.ResultSetler[0].Satirlar[0][0]));

        // sorgu bazında VeritabaniOverride ile hedef DB seçilebilir (ağaç/DB seçici bunu kullanır)
        QueryResult tempdb = await Yeni().ExecuteAsync(
            LocalDbProfili(), "SELECT DB_NAME() AS Aktif;",
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        Assert.True(tempdb.Basarili, tempdb.Hata?.Mesaj);
        Assert.Equal("tempdb", (string?)tempdb.ResultSetler[0].Satirlar[0][0]);
    }

    /// <summary>
    /// FG-6.4 kanıtı: kirli okuma açıkken oturum gerçekten READ UNCOMMITTED'a geçiyor mu,
    /// ve kapalıyken havuzdan gelen bağlantı önceki seviyeyi taşıyor mu?
    /// (1 = ReadUncommitted, 2 = ReadCommitted — sys.dm_exec_sessions)
    /// </summary>
    [Fact]
    public async Task Kirli_okuma_kalici_oturumda_izolasyonu_degistirir_ve_geri_alir()
    {
        // Kalıcı oturum (V2-S1): izolasyon aynı bağlantıda kalır → READ COMMITTED ön eki
        // önceki READ UNCOMMITTED'ı geri almalı (kirli okuma açık kalmasın).
        var servis = new QueryService();
        await using IDbOturum oturum =
            new OturumFabrikasi(new DpapiSecretProtector()).Olustur(LocalDbProfili());
        const string sorgu = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID;";

        QueryResult kirli = await servis.RunAsync(oturum, sorgu,
            new ExecuteOptions { KirliOkuma = true }, CancellationToken.None);
        Assert.True(kirli.Basarili, kirli.Hata?.Mesaj);
        Assert.Equal((short)1, kirli.ResultSetler[0].Satirlar[0][0]); // ReadUncommitted

        QueryResult temiz = await servis.RunAsync(oturum, sorgu,
            ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.True(temiz.Basarili, temiz.Hata?.Mesaj);
        Assert.Equal((short)2, temiz.ResultSetler[0].Satirlar[0][0]); // ReadCommitted (ön ek geri aldı)
    }

    [Fact]
    public async Task Ulasilamayan_sunucu_cokme_yerine_hata_nesnesi_dondurur()
    {
        // Dayanıklılık (S6): bağlantı kurulamazsa exception fırlamamalı — QueryResult.Hata dolmalı
        var profil = new ConnectionProfile
        {
            Ad = "yok",
            Sunucu = @"(localdb)\OlmayanOrnek_XYZ",
            Kimlik = KimlikTuru.Windows,
            BaglantiTimeoutSn = 5,
        };

        QueryResult sonuc = await Yeni().ExecuteAsync(
            profil, "SELECT 1", ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.False(sonuc.Basarili);
        Assert.NotNull(sonuc.Hata);
    }

    [Fact]
    public async Task TestConnection_localdb_ile_basarili()
    {
        (bool basarili, string? hata) = await Yeni()
            .TestConnectionAsync(LocalDbProfili(), CancellationToken.None);
        Assert.True(basarili, hata);
    }

    [Fact]
    public async Task Select_ve_print_sonuc_ve_mesaj_uretir()
    {
        QueryResult sonuc = await Yeni().ExecuteAsync(
            LocalDbProfili(),
            "PRINT 'merhaba sqlst'; SELECT 1 AS Bir, N'iki' AS Iki;",
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.Single(sonuc.ResultSetler);
        Assert.Equal(["Bir", "Iki"], sonuc.ResultSetler[0].Kolonlar.Select(k => k.Ad));
        Assert.Equal(1, sonuc.ToplamSatir);
        Assert.Contains(sonuc.Mesajlar, m => m.Contains("merhaba sqlst"));
    }

    [Fact]
    public async Task Hatali_sorgu_null_degil_hata_nesnesi_doner()
    {
        QueryResult sonuc = await Yeni().ExecuteAsync(
            LocalDbProfili(), "SELECT * FROM OlmayanTablo_XYZ;",
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.False(sonuc.Basarili);
        Assert.NotNull(sonuc.Hata);
        Assert.Equal(208, sonuc.Hata!.Numara); // invalid object name
    }

    [Fact]
    public async Task Satir_siniri_akisi_durdurur_ve_isaretler()
    {
        QueryResult sonuc = await Yeni().ExecuteAsync(
            LocalDbProfili(),
            "SELECT TOP 50 o1.object_id FROM sys.objects o1 CROSS JOIN sys.objects o2;",
            new ExecuteOptions { SatirSiniri = 10 }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.True(sonuc.SatirSiniriAsildi);
        Assert.Equal(10, sonuc.ToplamSatir);
    }

    /// <summary>
    /// 2026-07-23 kök neden ("zor sorgularda donuyor/patlıyor"): yalnız SATIR sınırı geniş
    /// satırları (nvarchar(max)/LOB) kısıtlamaz — 100.000 satır × MB = GB → OOM. Bayt bütçesi
    /// bunun kalkanı. Her satır ~8 KB; 100 KB bütçe ~13 satırda dolar; satır sınırı (100.000)
    /// çok yüksek → okumayı DURDURAN bellek bütçesidir. Kanıt: ToplamSatir 1000'e ulaşmadan durur.
    /// </summary>
    [Fact]
    public async Task Bellek_siniri_genis_satirlarda_akisi_durdurur_ve_isaretler()
    {
        const string genisSatirlar =
            "SELECT TOP 1000 REPLICATE(CAST(N'x' AS nvarchar(max)), 4000) AS Genis " +
            "FROM sys.objects o1 CROSS JOIN sys.objects o2;";

        QueryResult sonuc = await Yeni().ExecuteAsync(
            LocalDbProfili(), genisSatirlar,
            new ExecuteOptions { SatirSiniri = 100_000, BellekSiniriBayt = 100 * 1024 },
            CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.True(sonuc.BellekSiniriAsildi);          // kesilme nedeni BELLEK (satır değil)
        Assert.True(sonuc.SatirSiniriAsildi);           // genel "kesildi" bayrağı da dolar
        Assert.InRange(sonuc.ToplamSatir, 1, 200);      // satır sınırından (100.000) çok önce durdu
    }

    /// <summary>Karşı kanıt: geniş bütçede AYNI sorgu bellek yüzünden kesilmez (bütçe gerçekten bağlayıcı).</summary>
    [Fact]
    public async Task Genis_butcede_bellek_siniri_tetiklenmez()
    {
        const string genisSatirlar =
            "SELECT TOP 20 REPLICATE(CAST(N'x' AS nvarchar(max)), 4000) AS Genis " +
            "FROM sys.objects o1 CROSS JOIN sys.objects o2;";

        QueryResult sonuc = await Yeni().ExecuteAsync(
            LocalDbProfili(), genisSatirlar,
            new ExecuteOptions { SatirSiniri = 100_000, BellekSiniriBayt = 256L * 1024 * 1024 },
            CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.False(sonuc.BellekSiniriAsildi);
        Assert.False(sonuc.SatirSiniriAsildi);
        Assert.Equal(20, sonuc.ToplamSatir); // hepsi okundu
    }
}
