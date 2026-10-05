using System.Collections.ObjectModel;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// B2/A3 — sorgu sekmesinin KOMUT KAPILARI. Bunlar kolaylık değil güvenlik kuralıdır ve
/// hiçbiri saf mantık katmanında yaşamadığı için Application.Tests onları göremiyordu:
/// auto-refresh'in yazma sorgusunu reddetmesi, plan yolunun sekmenin durum makinesine
/// tabi olması, sekme kapanınca zamanlayıcının durması.
/// </summary>
public class SorguSekmesiKapiTests
{
    private static SorguSekmesiViewModel Kur(
        ConnectionProfile? profil = null, ISqlExecutor? executor = null)
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        ISqlExecutor gercekExecutor = executor ?? new SahteExecutor();

        return new SorguSekmesiViewModel(
            new QueryService(),
            new OturumFabrikasi(saglayici),
            saglayici,
            () => profil ?? Profiller.Yap(),
            kirliOkumaGetir: () => false,
            guvenliYazmaGetir: () => false,
            rollbackSnGetir: () => 300,
            new ObservableCollection<string>(["db"]),
            "test.sql");
    }

    // ── Auto-refresh yazma kapısı (V5-S4) ──────────────────────────────────

    [Theory]
    [InlineData("DELETE FROM Musteri WHERE Id = 1;")]
    [InlineData("UPDATE Musteri SET Ad = 'x' WHERE Id = 1;")]
    [InlineData("INSERT INTO Musteri (Ad) VALUES ('x');")]
    [InlineData("TRUNCATE TABLE Musteri;")]
    // CTE içine gizlenmiş DML — ilk sözcük WITH olduğu için naif kontrol "okuma" derdi
    [InlineData("WITH x AS (DELETE FROM Musteri RETURNING *) SELECT * FROM x;")]
    public void Oto_yenile_YAZMA_sorgusunda_ACILMAZ(string sql)
    {
        SorguSekmesiViewModel sekme = Kur();
        sekme.Belge.Text = sql;

        sekme.OtoYenileAcikDegistir(true);

        Assert.False(sekme.OtoYenileAcik);
        Assert.Contains("YAZMA", sekme.OtoYenileBandi, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT * FROM Musteri;")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT * FROM x;")]
    public void Oto_yenile_OKUMA_sorgusunda_acilir(string sql)
    {
        SorguSekmesiViewModel sekme = Kur();
        sekme.Belge.Text = sql;

        sekme.OtoYenileAcikDegistir(true);

        Assert.True(sekme.OtoYenileAcik);
    }

    [Fact]
    public void Oto_yenile_kapatilinca_bant_temizlenir()
    {
        SorguSekmesiViewModel sekme = Kur();
        sekme.Belge.Text = "SELECT 1;";

        sekme.OtoYenileAcikDegistir(true);
        Assert.True(sekme.OtoYenileAcik);

        sekme.OtoYenileAcikDegistir(false);

        Assert.False(sekme.OtoYenileAcik);
        Assert.Equal("", sekme.OtoYenileBandi);
    }

    [Fact]
    public async Task Sekme_kapaninca_oto_yenile_DURUR()
    {
        // Sayaç durdurulmazsa kapanan sekmenin sorgusu koşmaya devam ederdi.
        SorguSekmesiViewModel sekme = Kur();
        sekme.Belge.Text = "SELECT 1;";
        sekme.OtoYenileAcikDegistir(true);

        await sekme.KapatAsync();

        Assert.False(sekme.OtoYenileAcik);
    }

    // ── Plan yolu durum kapıları (B1/D5) ───────────────────────────────────

    [Fact]
    public async Task Baglanti_yokken_plan_ALINMAZ()
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        var sekme = new SorguSekmesiViewModel(
            new QueryService(),
            new OturumFabrikasi(saglayici), saglayici,
            profilGetir: () => null,
            kirliOkumaGetir: () => false, guvenliYazmaGetir: () => false, rollbackSnGetir: () => 300,
            new ObservableCollection<string>(), "test.sql");

        (SorguPlani? plan, string? hata) = await sekme.PlanAlAsync(gercek: true);

        Assert.Null(plan);
        Assert.NotNull(hata);
    }

    [Fact]
    public async Task Salt_okunur_profilde_YAZMA_sorgusunun_plani_ALINMAZ()
    {
        // V5-S1b'de canlı kanıtlanan güvenlik açığının regresyonu: PG'de plan sorguyu
        // EXPLAIN ile sarar, sarılmış metnin ilk sözcüğü EXPLAIN olduğu için salt-okunur
        // kapısı onu yazma saymazdı — ANALYZE ise sorguyu gerçekten çalıştırır.
        var executor = new SahteExecutor();
        ConnectionProfile profil = Profiller.Yap(MotorTuru.Postgres, saltOkunur: true);
        SorguSekmesiViewModel sekme = Kur(profil, executor);
        sekme.Belge.Text = "DELETE FROM Musteri;";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        (SorguPlani? plan, string? hata) = await sekme.PlanAlAsync(gercek: true);

        Assert.Null(plan);
        Assert.NotNull(hata);
        Assert.Empty(executor.Cagrilar);          // sunucuya HİÇ gitmedi
    }

    [Fact]
    public async Task Salt_okunur_profilde_CTE_icine_gizlenmis_DML_de_REDDEDILIR()
    {
        var executor = new SahteExecutor();
        ConnectionProfile profil = Profiller.Yap(MotorTuru.Postgres, saltOkunur: true);
        SorguSekmesiViewModel sekme = Kur(profil, executor);
        sekme.Belge.Text = "WITH x AS (DELETE FROM Musteri RETURNING *) SELECT * FROM x;";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        (SorguPlani? plan, string? hata) = await sekme.PlanAlAsync(gercek: true);

        Assert.Null(plan);
        Assert.NotNull(hata);
        Assert.Empty(executor.Cagrilar);
    }

    [Fact]
    public async Task Bos_sorguda_plan_ALINMAZ()
    {
        var executor = new SahteExecutor();
        SorguSekmesiViewModel sekme = Kur(executor: executor);
        sekme.MetinSaglayici = () => "   ";

        (SorguPlani? plan, string? hata) = await sekme.PlanAlAsync(gercek: true);

        Assert.Null(plan);
        Assert.NotNull(hata);
        Assert.Empty(executor.Cagrilar);
    }
}
