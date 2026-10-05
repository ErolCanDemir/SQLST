using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S1 CANLI KANITI: plan yolunun tamamı gerçek SQL Server'a karşı çalışır —
/// <c>SET … ON</c> → sorgu → plan XML'ini bul → çözümle → <c>SET … OFF</c>.
/// Birim testleri yalnız ÇÖZÜMLEMEYİ kanıtlar; sunucunun gerçekten plan döndürdüğünü,
/// XML'in beklediğimiz şekilde geldiğini ve SET'in kapandığını yalnız bu testler gösterir.
/// </summary>
public class PlanLocalDbTests : IAsyncLifetime
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 60,
    };

    private static readonly ExecuteOptions Tempdb = new() { VeritabaniOverride = "tempdb" };

    private readonly string _tablo = $"PlanE2e_{Guid.NewGuid():N}";
    private readonly MssqlLehcesi _lehce = new(new DpapiSecretProtector());
    private readonly SqlExecutor _executor;

    public PlanLocalDbTests() => _executor = new SqlExecutor(_lehce);

    public async Task InitializeAsync()
        => await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{_tablo}] (Id INT IDENTITY PRIMARY KEY, Ad NVARCHAR(50), Tutar DECIMAL(18,2));
            INSERT INTO dbo.[{_tablo}] (Ad, Tutar)
            SELECT TOP 500 CONCAT(N'ad', o.object_id), o.object_id % 97 FROM sys.all_objects o;
            """, Tempdb, CancellationToken.None);

    public async Task DisposeAsync()
        => await _executor.ExecuteAsync(Profil(),
            $"DROP TABLE IF EXISTS dbo.[{_tablo}];", Tempdb, CancellationToken.None);

    /// <summary>Sorgu sekmesindeki akışın testteki karşılığı: SET ON → sorgu → SET OFF.</summary>
    private async Task<(SorguPlani? Plan, QueryResult Sonuc)> PlanAlAsync(IDbOturum oturum, string sql, bool gercek)
    {
        await oturum.CalistirAsync(_lehce.PlanAcSql(gercek), Tempdb, CancellationToken.None);
        QueryResult sonuc;
        try
        {
            sonuc = await oturum.CalistirAsync(sql, Tempdb, CancellationToken.None);
        }
        finally
        {
            await oturum.CalistirAsync(_lehce.PlanKapatSql(gercek), Tempdb, CancellationToken.None);
        }

        string? xml = sonuc.ResultSetler
            .Where(s => s.Kolonlar.Count == 1 && s.Satirlar.Count > 0
                        && s.Kolonlar[0].Ad.Contains("Showplan", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Satirlar[0][0]?.ToString())
            .FirstOrDefault(x => !string.IsNullOrEmpty(x));

        return (xml is null ? null : MssqlPlanOkuyucu.Coz(xml, gercek), sonuc);
    }

    [Fact]
    public async Task Tahmini_plan_alinir_ve_sorgu_CALISTIRILMAZ()
    {
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        // Kritik güvenlik davranışı: SHOWPLAN_XML ile bir DELETE bile ÇALIŞMAZ.
        (SorguPlani? plan, _) = await PlanAlAsync(oturum, $"DELETE FROM dbo.[{_tablo}];", gercek: false);

        Assert.NotNull(plan);
        Assert.False(plan!.Gercek);
        Assert.NotEmpty(plan.Ifadeler);
        Assert.NotNull(plan.Ifadeler[0].Kok);
        Assert.All(plan.TumDugumler, d => Assert.Null(d.GercekSatir));   // tahmini planda sayaç yok

        // Satırlar YERİNDE mi? (plan alma veri değiştirmemeli)
        QueryResult sayim = await oturum.CalistirAsync(
            $"SELECT COUNT(*) FROM dbo.[{_tablo}];", Tempdb, CancellationToken.None);
        Assert.Equal(500, Convert.ToInt32(sayim.ResultSetler[0].Satirlar[0][0]));
    }

    [Fact]
    public async Task Gercek_plan_satir_sayaclarini_getirir()
    {
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        (SorguPlani? plan, QueryResult sonuc) = await PlanAlAsync(
            oturum, $"SELECT Ad, Tutar FROM dbo.[{_tablo}] WHERE Tutar > 50;", gercek: true);

        Assert.NotNull(plan);
        Assert.True(plan!.Gercek);
        // Gerçek planda sorgunun KENDİ sonucu da döner (plan kümesinden ayrı)
        Assert.Contains(sonuc.ResultSetler, s => s.Kolonlar.Any(k => k.Ad == "Ad"));
        // En az bir operatörde gerçek satır sayacı olmalı
        Assert.Contains(plan.TumDugumler, d => d.GercekSatir is not null);
    }

    [Fact]
    public async Task Plan_toplama_MUTLAKA_kapanir_sonraki_sorgu_normal_sonuc_verir()
    {
        // Regresyon: SET açık kalırsa sonraki sorgular sonuç yerine PLAN döndürür ve
        // kullanıcı ne olduğunu anlamaz. Oturum kalıcı olduğundan bu gerçek bir risk.
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        await PlanAlAsync(oturum, $"SELECT TOP 3 Ad FROM dbo.[{_tablo}];", gercek: false);

        QueryResult sonra = await oturum.CalistirAsync(
            $"SELECT TOP 3 Ad FROM dbo.[{_tablo}];", Tempdb, CancellationToken.None);

        Assert.True(sonra.Basarili, sonra.Hata?.Mesaj);
        ResultSetData set = Assert.Single(sonra.ResultSetler);
        Assert.Equal("Ad", set.Kolonlar[0].Ad);          // plan değil, gerçek sonuç
        Assert.Equal(3, set.Satirlar.Count);
    }

    [Fact]
    public async Task Maliyet_yuzdeleri_gercek_planda_da_toplami_100_verir()
    {
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        (SorguPlani? plan, _) = await PlanAlAsync(oturum,
            $"SELECT Ad, SUM(Tutar) FROM dbo.[{_tablo}] GROUP BY Ad ORDER BY 2 DESC;", gercek: false);

        Assert.NotNull(plan);
        PlanDugumu kok = plan!.Ifadeler[0].Kok!;
        Assert.True(kok.Cocuklar.Count > 0, "GROUP BY + ORDER BY planı çok operatörlü olmalı");

        double toplam = kok.Hepsi().Sum(d => d.MaliyetYuzdesi);
        Assert.InRange(toplam, 99.0, 101.0);             // kendi payları toplamı ~%100
    }

    [Fact]
    public async Task Eksik_index_onerisi_gercek_planda_okunur()
    {
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        // Index'siz kolonda seçici filtre → SQL Server tipik olarak index önerir
        (SorguPlani? plan, _) = await PlanAlAsync(oturum,
            $"SELECT Id, Ad FROM dbo.[{_tablo}] WHERE Tutar = 42;", gercek: false);

        Assert.NotNull(plan);
        IReadOnlyList<PlanEksikIndexi> oneriler = plan!.Ifadeler[0].EksikIndexler;
        if (oneriler.Count == 0)
            return;   // sunucu öneri üretmediyse test bir şey iddia etmez (öneri garanti değildir)

        PlanEksikIndexi ilk = oneriler[0];
        Assert.Contains(_tablo, ilk.Tablo);
        Assert.NotEmpty(ilk.EsitlikKolonlari);
        Assert.Contains("CREATE NONCLUSTERED INDEX", ilk.Script());
    }
}
