using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Gerçek LocalDB'ye karşı kalıcı oturum (V2-S1): bağlantının canlı kaldığını
/// (SET/#temp korunur), iptali ve kapanışta açık transaction'ın geri alındığını kanıtlar.
/// </summary>
public class DbOturumTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static ConnectionProfile LocalDbProfili() => new()
    {
        Ad = "localdb",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 60,
    };

    private static IDbOturum Oturum() =>
        new OturumFabrikasi(new DpapiSecretProtector()).Olustur(LocalDbProfili());

    [Fact]
    public async Task Ayni_oturumda_temp_tablo_ve_SET_sorgular_arasi_korunur()
    {
        await using IDbOturum oturum = Oturum();
        var opt = ExecuteOptions.Varsayilan;

        QueryResult kur = await oturum.CalistirAsync(
            "CREATE TABLE #t (x INT); INSERT INTO #t VALUES (42);", opt, CancellationToken.None);
        Assert.True(kur.Basarili, kur.Hata?.Mesaj);

        // AYNI oturum: #temp hâlâ görünür olmalı (bağlantı canlı kaldı)
        QueryResult oku = await oturum.CalistirAsync("SELECT x FROM #t;", opt, CancellationToken.None);
        Assert.True(oku.Basarili, oku.Hata?.Mesaj);
        Assert.Equal(42, oku.ResultSetler[0].Satirlar[0][0]);
    }

    [Fact]
    public async Task Farkli_oturum_temp_tabloyu_gormez()
    {
        await using IDbOturum a = Oturum();
        await oturumTempKur(a);

        await using IDbOturum b = Oturum();
        QueryResult oku = await b.CalistirAsync("SELECT x FROM #t;", ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.False(oku.Basarili); // #temp oturuma özeldir
        Assert.NotNull(oku.Hata);

        static async Task oturumTempKur(IDbOturum o) =>
            await o.CalistirAsync("CREATE TABLE #t (x INT); INSERT INTO #t VALUES (1);",
                ExecuteOptions.Varsayilan, CancellationToken.None);
    }

    [Fact]
    public async Task Veritabani_override_ile_gecis_ve_geri_donus()
    {
        await using IDbOturum oturum = Oturum();

        QueryResult tempdb = await oturum.CalistirAsync(
            "SELECT DB_NAME();", new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        Assert.Equal("tempdb", tempdb.ResultSetler[0].Satirlar[0][0]);

        QueryResult master = await oturum.CalistirAsync(
            "SELECT DB_NAME();", new ExecuteOptions { VeritabaniOverride = "master" }, CancellationToken.None);
        Assert.Equal("master", master.ResultSetler[0].Satirlar[0][0]);
    }

    [Fact]
    public async Task Acik_transaction_XACT_STATE_ile_gorunur()
    {
        await using IDbOturum oturum = Oturum();

        Assert.Equal(IslemDurumu.Yok, await oturum.IslemDurumuAsync());

        await oturum.CalistirAsync("BEGIN TRAN;", ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.Equal(IslemDurumu.Acik, await oturum.IslemDurumuAsync());

        await oturum.CalistirAsync("ROLLBACK;", ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.Equal(IslemDurumu.Yok, await oturum.IslemDurumuAsync());
    }

    [Fact]
    public async Task Kapanista_acik_transaction_rollback_edilir()
    {
        var profil = LocalDbProfili();
        var fabrika = new OturumFabrikasi(new DpapiSecretProtector());
        var tempdb = new ExecuteOptions { VeritabaniOverride = "tempdb" };
        string tablo = $"OturumTx_{Guid.NewGuid():N}";

        // Kalıcı bir tabloya, açık transaction içinde satır ekleyip oturumu KAPAT (commit yok)
        await using (IDbOturum yazan = fabrika.Olustur(profil))
        {
            await yazan.CalistirAsync($"CREATE TABLE dbo.[{tablo}] (x INT);", tempdb, CancellationToken.None);
            await yazan.CalistirAsync($"BEGIN TRAN; INSERT INTO dbo.[{tablo}] VALUES (1);", tempdb, CancellationToken.None);
            Assert.Equal(IslemDurumu.Acik, await yazan.IslemDurumuAsync());
        } // DisposeAsync → açık TRAN ROLLBACK edilmeli

        try
        {
            // Yeni oturum: INSERT geri alınmış olmalı (0 satır)
            await using IDbOturum okuyan = fabrika.Olustur(profil);
            QueryResult sonuc = await okuyan.CalistirAsync(
                $"SELECT COUNT(*) FROM dbo.[{tablo}];", tempdb, CancellationToken.None);
            Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
            Assert.Equal(0, sonuc.ResultSetler[0].Satirlar[0][0]);
        }
        finally
        {
            await using IDbOturum temizle = fabrika.Olustur(profil);
            await temizle.CalistirAsync($"DROP TABLE IF EXISTS dbo.[{tablo}];", tempdb, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Guvenli_yazma_akisi_gercek_tranda_rollback_ve_commit()
    {
        // V2-S4 uçtan uca: DML açık TRAN'da çalışır, etkilenen satır sayılır,
        // ROLLBACK veriyi geri alır, COMMIT kalıcılaştırır — gerçek LocalDB'de.
        var servis = new QueryService();
        var fabrika = new OturumFabrikasi(new DpapiSecretProtector());
        var tempdb = new ExecuteOptions { VeritabaniOverride = "tempdb" };
        string tablo = $"GuvenliYazma_{Guid.NewGuid():N}";

        await using IDbOturum oturum = fabrika.Olustur(LocalDbProfili());
        await oturum.CalistirAsync(
            $"CREATE TABLE dbo.[{tablo}] (x INT); INSERT INTO dbo.[{tablo}] VALUES (1), (2);",
            tempdb, CancellationToken.None);
        try
        {
            // 1) ROLLBACK yolu: 2 satırlık DELETE geri alınır
            QueryResult silme = await GuvenliYazmaYurutucu.CalistirAsync(servis, Mssql, oturum,
                SqlCozumleyici.BatchlereBol($"DELETE FROM dbo.[{tablo}] WHERE x <= 2"),
                tempdb, CancellationToken.None);
            Assert.True(silme.Basarili, silme.Hata?.Mesaj);
            Assert.Equal(2, silme.EtkilenenSatir);                        // banner'ın sayısı
            Assert.Equal(IslemDurumu.Acik, await oturum.IslemDurumuAsync()); // karar bekliyor

            string geriAl = await GuvenliYazmaYurutucu.KararUygulaAsync(servis, Mssql, oturum, commit: false, opts: tempdb);
            Assert.Contains("ROLLBACK", geriAl);
            QueryResult sayim = await oturum.CalistirAsync(
                $"SELECT COUNT(*) FROM dbo.[{tablo}];", tempdb, CancellationToken.None);
            Assert.Equal(2, sayim.ResultSetler[0].Satirlar[0][0]); // veri değişmedi

            // 2) COMMIT yolu: aynı DELETE onaylanınca kalıcı
            await GuvenliYazmaYurutucu.CalistirAsync(servis, Mssql, oturum,
                SqlCozumleyici.BatchlereBol($"DELETE FROM dbo.[{tablo}] WHERE x <= 2"),
                tempdb, CancellationToken.None);
            string onay = await GuvenliYazmaYurutucu.KararUygulaAsync(servis, Mssql, oturum, commit: true, opts: tempdb);
            Assert.Contains("COMMIT", onay);
            Assert.Equal(IslemDurumu.Yok, await oturum.IslemDurumuAsync());
            QueryResult son = await oturum.CalistirAsync(
                $"SELECT COUNT(*) FROM dbo.[{tablo}];", tempdb, CancellationToken.None);
            Assert.Equal(0, son.ResultSetler[0].Satirlar[0][0]); // silme kalıcı
        }
        finally
        {
            await oturum.CalistirAsync($"IF @@TRANCOUNT > 0 ROLLBACK; DROP TABLE IF EXISTS dbo.[{tablo}];",
                tempdb, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Uzun_sorgu_iptal_edilebilir()
    {
        await using IDbOturum oturum = Oturum();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(400));

        QueryResult sonuc = await oturum.CalistirAsync(
            "WAITFOR DELAY '00:00:30';", ExecuteOptions.Varsayilan, cts.Token);

        Assert.True(sonuc.IptalEdildi);
        // Oturum iptalden sonra hâlâ kullanılabilir olmalı
        QueryResult sonra = await oturum.CalistirAsync("SELECT 1;", ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.True(sonra.Basarili, sonra.Hata?.Mesaj);
    }
}
