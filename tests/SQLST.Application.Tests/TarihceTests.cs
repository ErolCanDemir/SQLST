using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

public class TarihceDeposuTests : IDisposable
{
    private readonly string _dosya = Path.Combine(Path.GetTempPath(), $"sqlst-{Guid.NewGuid():N}.db");

    private ITarihceDeposu Depo() => new SqliteTarihceDeposu(new YerelDepo(_dosya));

    private static TarihceKaydi Kayit(string tanim, string kaynak = "okuma")
        => TarihceYardimcisi.KayitKur(@"(localdb)\X", "db", "dbo", "spTest", tanim, kaynak);

    [Fact]
    public async Task Yeni_icerik_surum_acar_ayni_icerik_acmaz()
    {
        ITarihceDeposu depo = Depo();

        Assert.True(await depo.EkleAsync(Kayit("CREATE PROC v1 AS SELECT 1")));
        Assert.False(await depo.EkleAsync(Kayit("CREATE PROC v1 AS SELECT 1"))); // aynı hash — gürültü yok
        Assert.True(await depo.EkleAsync(Kayit("CREATE PROC v2 AS SELECT 2", "alter-öncesi")));

        IReadOnlyList<TarihceKaydi> liste = await depo.ListeAsync(null, @"(localdb)\X", "db", "dbo", "spTest");
        Assert.Equal(2, liste.Count);
        Assert.Contains("v2", liste[0].Tanim); // en yeni önce
        Assert.Equal("alter-öncesi", liste[0].Kaynak);
    }

    [Fact]
    public async Task Son_kayit_nesne_bazinda_gelir_baska_nesne_karismaz()
    {
        ITarihceDeposu depo = Depo();
        await depo.EkleAsync(Kayit("A"));
        await depo.EkleAsync(TarihceYardimcisi.KayitKur(@"(localdb)\X", "db", "dbo", "spDiger", "B", "okuma"));

        TarihceKaydi? son = await depo.SonAsync(null, @"(localdb)\X", "db", "dbo", "spTest");
        Assert.Equal("A", son!.Tanim);
        Assert.Null(await depo.SonAsync(null, @"(localdb)\X", "db", "dbo", "spYok"));
    }

    /// <summary>V3: aynı sunucu+nesneye bakan iki PROFİL birbirinin sürüm zincirini görmez/bozmaz.</summary>
    [Fact]
    public async Task Tarihce_profillere_gore_yalitilir()
    {
        ITarihceDeposu depo = Depo();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        Assert.True(await depo.EkleAsync(Kayit("CREATE PROC vA AS SELECT 1") with { ProfilId = a }));
        // Aynı nesne, BAŞKA profil: içerik farklı olduğu için kendi zincirinde sürüm açar
        Assert.True(await depo.EkleAsync(Kayit("CREATE PROC vB AS SELECT 2") with { ProfilId = b }));
        // A'nın zinciri kendi son kaydıyla kıyaslanır — B'ninki karışmaz
        Assert.False(await depo.EkleAsync(Kayit("CREATE PROC vA AS SELECT 1") with { ProfilId = a }));

        TarihceKaydi? aSon = await depo.SonAsync(a, @"(localdb)\X", "db", "dbo", "spTest");
        TarihceKaydi? bSon = await depo.SonAsync(b, @"(localdb)\X", "db", "dbo", "spTest");
        Assert.Contains("vA", aSon!.Tanim);
        Assert.Contains("vB", bSon!.Tanim);
        Assert.Single(await depo.ListeAsync(a, @"(localdb)\X", "db", "dbo", "spTest"));
        Assert.Single(await depo.ListeAsync(b, @"(localdb)\X", "db", "dbo", "spTest"));
    }

    [Fact]
    public void Disarida_degisti_hash_farkiyla_anlasilir()
    {
        TarihceKaydi kayit = Kayit("eski tanım");
        Assert.True(TarihceYardimcisi.DisaridaDegisti(kayit, "yeni tanım"));
        Assert.False(TarihceYardimcisi.DisaridaDegisti(kayit, "eski tanım"));
        Assert.False(TarihceYardimcisi.DisaridaDegisti(null, "ilk görüş")); // kayıt yoksa rozet yok
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string ek in new[] { "", "-wal", "-shm" })
        {
            string y = _dosya + ek;
            if (File.Exists(y)) File.Delete(y);
        }
    }
}

public class AlterHedefleriTests
{
    [Theory]
    [InlineData("ALTER PROCEDURE dbo.spRapor AS SELECT 1", "dbo", "spRapor")]
    [InlineData("ALTER PROC [satis].[spOzet] AS SELECT 1", "satis", "spOzet")]
    [InlineData("CREATE OR ALTER VIEW dbo.vwX AS SELECT 1 a", "dbo", "vwX")]
    [InlineData("ALTER VIEW vwSemasiz AS SELECT 1 a", "dbo", "vwSemasiz")] // şema yoksa dbo
    [InlineData("ALTER FUNCTION dbo.fnTop() RETURNS INT AS BEGIN RETURN 1 END", "dbo", "fnTop")]
    public void Alter_hedefi_bulunur(string sql, string sema, string ad)
    {
        IReadOnlyList<(string Sema, string Ad)> hedefler = SqlCozumleyici.AlterHedefleri(sql);
        Assert.Equal([(sema, ad)], hedefler);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("CREATE PROCEDURE dbo.spYeni AS SELECT 1")] // yeni nesne — yedeklenecek eski hâli yok
    [InlineData("ALTER TABLE dbo.T ADD X INT")]             // tablo tarihçe kapsamı dışı
    [InlineData("bozuk ((")]
    public void Hedef_olmayan_metinler_bos_doner(string sql)
        => Assert.Empty(SqlCozumleyici.AlterHedefleri(sql));
}

public class ScriptAsTests
{
    private static SemaNesnesi Sp() => new("db", "dbo", "spRapor",
        SemaNesneTuru.StoredProcedure, [], []);

    [Fact]
    public void Drop_ve_create_if_exists_ve_go_ile()
    {
        string script = NesneScriptleyici.DropVeCreate(Sp(), "CREATE PROCEDURE dbo.spRapor AS SELECT 1");

        Assert.Contains("DROP PROCEDURE IF EXISTS [dbo].[spRapor];", script);
        Assert.Contains("GO", script);
        Assert.Contains("CREATE PROCEDURE dbo.spRapor", script);
        // GO, CREATE'ten ÖNCE olmalı (CREATE batch'in ilk statement'ı — 07-r2 §5)
        Assert.True(script.IndexOf("GO", StringComparison.Ordinal) < script.IndexOf("CREATE", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_table_scripti_kolon_pk_index_iceriyor()
    {
        var meta = new DuzenlemeMetasi("db", "dbo", "Musteri",
        [
            new DuzenlemeKolonu("Id", "int", "int", false, true, true, false, false, true),
            new DuzenlemeKolonu("Ad", "nvarchar", "nvarchar(50)", false, false, false, false, false, true),
            new DuzenlemeKolonu("Not", "nvarchar", "nvarchar(max)", true, false, false, false, false, false),
        ]);
        MevcutIndex[] indexler =
        [
            new("[dbo].[Musteri]", "IX_Musteri_Ad", true, false, ["Ad"], ["Not"]),
            new("[dbo].[Musteri]", "PK_Musteri", true, true, ["Id"], []),   // PK index tekrar yazılmaz
            new("[dbo].[Baska]", "IX_Baska", false, false, ["X"], []),      // başka tablo karışmaz
        ];

        string script = NesneScriptleyici.CreateTableScripti(meta, indexler);

        Assert.Contains("CREATE TABLE [dbo].[Musteri] (", script);
        Assert.Contains("[Id] int IDENTITY(1,1) NOT NULL", script);
        Assert.Contains("[Ad] nvarchar(50) NOT NULL", script);
        Assert.Contains("[Not] nvarchar(max) NULL", script);
        Assert.Contains("PRIMARY KEY ([Id])", script);
        Assert.Contains("CREATE UNIQUE NONCLUSTERED INDEX [IX_Musteri_Ad]", script);
        Assert.Contains("INCLUDE ([Not])", script);
        Assert.DoesNotContain("IX_Baska", script);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "NONCLUSTERED INDEX"));
    }
}
