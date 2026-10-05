using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>BF-3 (2026-07-27): hedef tablo okuma gerçek LocalDB doğrulaması — seçili kolonlar, NULL (DBNull değil), tempdb.</summary>
public class FarkOkumaLocalDbTests : IAsyncLifetime
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 60,
    };

    private readonly string _tablo = $"FarkOku_{Guid.NewGuid():N}";
    private readonly SqlExecutor _executor = new(new MssqlLehcesi(new DpapiSecretProtector()));
    private static readonly ExecuteOptions Tempdb = new() { VeritabaniOverride = "tempdb" };

    public async Task InitializeAsync()
        => await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{_tablo}] (Id INT PRIMARY KEY, Ad NVARCHAR(50) NULL, Bakiye DECIMAL(18,2) NULL);
            INSERT INTO dbo.[{_tablo}] (Id, Ad, Bakiye) VALUES (1, N'Ali', 10.50), (2, NULL, NULL);
            """, Tempdb, CancellationToken.None);

    public async Task DisposeAsync()
        => await _executor.ExecuteAsync(Profil(),
            $"DROP TABLE IF EXISTS dbo.[{_tablo}];", Tempdb, CancellationToken.None);

    [Fact]
    public async Task Tabloyu_seçilen_kolonlarla_okur_NULL_gerçek_null()
    {
        var servis = new FarkOkumaServisi(new LehceSaglayici(new DpapiSecretProtector()));

        IReadOnlyList<IReadOnlyDictionary<string, object?>> satirlar =
            await servis.TabloyuOkuAsync(Profil(), "tempdb", "dbo", _tablo, ["Id", "Ad", "Bakiye"], CancellationToken.None);

        Assert.Equal(2, satirlar.Count);

        IReadOnlyDictionary<string, object?> s1 = satirlar.Single(r => Convert.ToInt32(r["Id"]) == 1);
        Assert.Equal("Ali", s1["Ad"]);
        Assert.Equal(10.50m, Convert.ToDecimal(s1["Bakiye"]));

        IReadOnlyDictionary<string, object?> s2 = satirlar.Single(r => Convert.ToInt32(r["Id"]) == 2);
        Assert.Null(s2["Ad"]);     // DBNull değil, gerçek null (ExcelFarkKarsilastirici NULL'ı böyle bekler)
        Assert.Null(s2["Bakiye"]);
    }
}
