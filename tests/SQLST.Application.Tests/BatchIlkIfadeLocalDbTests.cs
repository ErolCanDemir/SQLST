using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// KULLANICI BULGUSU — CANLI REGRESYON (2026-07-19).
///
/// "Script as DROP + CREATE" çıktısı çalıştırıldığında sunucu şunu döndürüyordu:
/// <c>Msg 111: 'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.</c>
///
/// Sebep üretilen script değildi — <c>GO</c> doğru yerdeydi ve bölücü doğru bölüyordu.
/// Sebep, <see cref="QueryService"/>'in izolasyon ifadesini kullanıcının SQL'iyle AYNI
/// BATCH'E ön ek olarak koymasıydı; <c>CREATE PROCEDURE</c> böylece batch'in ilk ifadesi
/// olamıyordu.
///
/// <b>Aynı hata sınıfı daha önce de görülmüştü</b> (<c>SET SHOWPLAN_XML</c> — tahmini plan).
/// O zaman özellik kaldırılmış ama kök neden düzeltilmemişti; bu test kök nedenin geri
/// gelmesini engeller.
///
/// <b>Ürünün GERÇEK yolundan geçer:</b> SqlCozumleyici → BatchYurutucu → QueryService.
/// V5-S1'de canlı testler bu katmanı atladığı için tam bu hata kaçmıştı.
/// </summary>
public class BatchIlkIfadeLocalDbTests : IAsyncLifetime
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly ExecuteOptions Tempdb = new() { VeritabaniOverride = "tempdb" };

    private readonly string _sp = $"spBatchTest_{Guid.NewGuid():N}"[..24];
    private readonly OturumFabrikasi _fabrika = new(new DpapiSecretProtector());
    private IDbOturum? _oturum;

    public Task InitializeAsync()
    {
        _oturum = _fabrika.Olustur(Profil());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_oturum is not null)
        {
            await _oturum.CalistirAsync(
                $"DROP PROCEDURE IF EXISTS dbo.[{_sp}];", Tempdb, CancellationToken.None);
            await _oturum.DisposeAsync();
        }
    }

    /// <summary>
    /// Kullanıcının ekranındaki script'in birebir aynısı: DROP + GO + CREATE PROCEDURE.
    /// Düzeltmeden ÖNCE bu test Msg 111 ile düşerdi.
    /// </summary>
    [Fact]
    public async Task DROP_GO_CREATE_PROCEDURE_scripti_CALISIR()
    {
        string script = $"""
            DROP PROCEDURE IF EXISTS dbo.[{_sp}];
            GO
            CREATE PROCEDURE dbo.{_sp} @MusteriId INT AS SELECT @MusteriId AS Id
            """;

        QueryResult sonuc = await BatchYurutucu.CalistirAsync(
            new QueryService(), _oturum!, SqlCozumleyici.BatchlereBol(script, MotorTuru.Mssql),
            Tempdb, CancellationToken.None);

        Assert.True(sonuc.Basarili,
            $"Msg {sonuc.Hata?.Numara}, Satır {sonuc.Hata?.Satir}: {sonuc.Hata?.Mesaj}");
        Assert.Null(sonuc.Hata);

        // Prosedür GERÇEKTEN oluştu mu — "hata vermedi" yetmez
        QueryResult kontrol = await _oturum!.CalistirAsync(
            $"SELECT COUNT(*) FROM tempdb.sys.objects WHERE name = '{_sp}' AND type = 'P';",
            Tempdb, CancellationToken.None);

        Assert.Equal(1, Convert.ToInt32(kontrol.ResultSetler[0].Satirlar[0][0]));
    }

    /// <summary>
    /// "Batch'in ilk ifadesi olmalı" kuralı yalnız CREATE PROCEDURE'de değil; VIEW ve
    /// FUNCTION da aynı kurala tabidir. Tek bir kaçak kalmasın.
    /// </summary>
    [Theory]
    [InlineData("VIEW", "CREATE VIEW dbo.{0} AS SELECT 1 AS x")]
    [InlineData("FUNCTION", "CREATE FUNCTION dbo.{0}() RETURNS int AS BEGIN RETURN 1 END")]
    public async Task Ilk_ifade_kuralina_tabi_nesneler_de_CALISIR(string tur, string kalip)
    {
        string ad = $"{tur}_{Guid.NewGuid():N}"[..20];
        string script = string.Format(System.Globalization.CultureInfo.InvariantCulture, kalip, ad);

        try
        {
            QueryResult sonuc = await BatchYurutucu.CalistirAsync(
                new QueryService(), _oturum!, SqlCozumleyici.BatchlereBol(script, MotorTuru.Mssql),
                Tempdb, CancellationToken.None);

            Assert.True(sonuc.Basarili,
                $"Msg {sonuc.Hata?.Numara}: {sonuc.Hata?.Mesaj}");
        }
        finally
        {
            await _oturum!.CalistirAsync(
                $"DROP {tur} IF EXISTS dbo.[{ad}];", Tempdb, CancellationToken.None);
        }
    }

    /// <summary>
    /// Kirli okuma AYARI hâlâ etkili olmalı — düzeltme özelliği bozmamalı.
    /// Ayrı komut olarak gitse de oturumun izolasyon seviyesini gerçekten değiştirmeli.
    /// </summary>
    [Fact]
    public async Task Kirli_okuma_ayari_oturumda_GERCEKTEN_etkili()
    {
        var servis = new QueryService();

        QueryResult kirli = await servis.RunAsync(
            _oturum!, "SELECT TRANSACTION_ISOLATION_LEVEL FROM sys.dm_exec_sessions WHERE session_id = @@SPID;",
            new ExecuteOptions { VeritabaniOverride = "tempdb", KirliOkuma = true }, CancellationToken.None);

        // 1 = READ UNCOMMITTED
        Assert.Equal(1, Convert.ToInt32(kirli.ResultSetler[0].Satirlar[0][0]));

        QueryResult temiz = await servis.RunAsync(
            _oturum!, "SELECT TRANSACTION_ISOLATION_LEVEL FROM sys.dm_exec_sessions WHERE session_id = @@SPID;",
            Tempdb, CancellationToken.None);

        // 2 = READ COMMITTED
        Assert.Equal(2, Convert.ToInt32(temiz.ResultSetler[0].Satirlar[0][0]));
    }
}
