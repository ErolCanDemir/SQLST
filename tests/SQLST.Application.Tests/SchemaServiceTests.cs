using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

public class SchemaServiceTests
{
    [Theory]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 30, 0, 0, "varchar(30)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18,2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void TipYaz_goruntuluk_adi_dogru_kurar(string tip, int maxLen, int prec, int scale, string beklenen)
    {
        Assert.Equal(beklenen, MssqlLehcesi.Bicimle(tip, maxLen, prec, scale));
    }

    /// <summary>Tip biçimleme artık lehçe işi — MSSQL örneğiyle doğrula (V3-S1 Faz 1).</summary>
    private static readonly ILehce Lehce = new MssqlLehcesi(new DpapiSecretProtector());

    private static QueryResult SahteSema() => new()
    {
        Basarili = true,
        ResultSetler =
        [
            new ResultSetData
            {
                Kolonlar = [new("db", "nvarchar")],
                Satirlar = [["DemoDb"]],
            },
            new ResultSetData
            {
                Kolonlar = [new("sema", "sysname"), new("ad", "sysname"), new("tur", "char")],
                Satirlar =
                [
                    ["dbo", "Musteri", "U "],
                    ["dbo", "vwAktifMusteri", "V "],
                    ["dbo", "spGetMusteri", "P "],
                    ["dbo", "fnToplam", "FN"],
                ],
            },
            new ResultSetData
            {
                Kolonlar = [new("sema", "sysname"), new("ad", "sysname"), new("kolon", "sysname"),
                            new("tip", "sysname"), new("max_length", "smallint"), new("precision", "tinyint"),
                            new("scale", "tinyint"), new("is_nullable", "bit"), new("pk", "int")],
                Satirlar =
                [
                    ["dbo", "Musteri", "Id", "int", 4, 10, 0, false, 1],
                    ["dbo", "Musteri", "Ad", "nvarchar", 100, 0, 0, false, 0],
                    ["dbo", "Musteri", "Bakiye", "decimal", 9, 18, 2, true, 0],
                    ["dbo", "vwAktifMusteri", "Id", "int", 4, 10, 0, false, 0],
                ],
            },
            new ResultSetData
            {
                Kolonlar = [new("sema", "sysname"), new("ad", "sysname"), new("parametre", "sysname"),
                            new("tip", "sysname"), new("max_length", "smallint"), new("precision", "tinyint"),
                            new("scale", "tinyint"), new("is_output", "bit")],
                Satirlar =
                [
                    ["dbo", "spGetMusteri", "@Id", "int", 4, 10, 0, false],
                    ["dbo", "spGetMusteri", "@Ad", "nvarchar", 100, 0, 0, true],
                ],
            },
        ],
    };

    [Fact]
    public async Task Esleme_nesneleri_turlerine_kolonlarina_ve_parametrelerine_baglar()
    {
        var sahte = new SahteExecutor(SahteSema());

        SemaOnbellegi sema = await new SchemaService(sahte, Lehce)
            .YukleAsync(new ConnectionProfile(), veritabani: "HedefDb", CancellationToken.None);

        Assert.Equal("HedefDb", sahte.SonSecenekler?.VeritabaniOverride); // override sorguya taşınır
        Assert.Equal(4, sema.Nesneler.Count);
        Assert.All(sema.Nesneler, n => Assert.Equal("DemoDb", n.Veritabani)); // DB adı DB_NAME()'den

        SemaNesnesi musteri = Assert.Single(sema.Nesneler, n => n.Tur == SemaNesneTuru.Tablo);
        Assert.Equal("dbo.Musteri", musteri.TamAd);
        Assert.Equal("[dbo].[Musteri]", musteri.TamAdKoseli);
        Assert.Equal(3, musteri.Kolonlar.Count);
        Assert.True(musteri.Kolonlar[0].PkMi);
        Assert.Equal("nvarchar(50)", musteri.Kolonlar[1].Tip);
        Assert.True(musteri.Kolonlar[2].NullOlabilir);
        Assert.Empty(musteri.Parametreler);

        SemaNesnesi sp = Assert.Single(sema.Nesneler, n => n.Tur == SemaNesneTuru.StoredProcedure);
        Assert.Empty(sp.Kolonlar);
        Assert.Equal(2, sp.Parametreler.Count);
        Assert.Equal(new SemaParametresi("@Id", "int", false), sp.Parametreler[0]);
        Assert.Equal(new SemaParametresi("@Ad", "nvarchar(50)", true), sp.Parametreler[1]);

        Assert.Single(sema.Nesneler, n => n.Tur == SemaNesneTuru.View);
        Assert.Single(sema.Nesneler, n => n.Tur == SemaNesneTuru.Fonksiyon);
    }

    [Fact]
    public async Task Hatali_sonucta_aciklayici_istisna_firlatir()
    {
        var sahte = new SahteExecutor(new QueryResult
        {
            Hata = new SqlHata("yetki yok", 229, 1, 14),
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SchemaService(sahte, Lehce).YukleAsync(new ConnectionProfile(), veritabani: null, CancellationToken.None));
        Assert.Contains("yetki yok", ex.Message);
    }

    [Fact]
    public async Task Tanim_getir_adi_kacirarak_sorar_ve_sifreli_nesnede_null_doner()
    {
        var sahte = new SahteExecutor(new QueryResult
        {
            Basarili = true,
            ResultSetler = [new ResultSetData
            {
                Kolonlar = [new("tanim", "nvarchar")],
                Satirlar = [[DBNull.Value]], // WITH ENCRYPTION → OBJECT_DEFINITION NULL
            }],
        });
        var nesne = new SemaNesnesi("DemoDb", "dbo", "Garip'Ad", SemaNesneTuru.StoredProcedure, [], []);

        string? tanim = await new SchemaService(sahte, Lehce).TanimGetirAsync(new ConnectionProfile(), nesne, CancellationToken.None);

        Assert.Null(tanim);
        Assert.Contains("N'[dbo].[Garip''Ad]'", sahte.SonSql); // tek tırnak ikilenir
        Assert.Equal("DemoDb", sahte.SonSecenekler?.VeritabaniOverride); // nesnenin DB'sinde sorulur
    }

    private sealed class SahteExecutor(QueryResult sonuc) : ISqlExecutor
    {
        public ExecuteOptions? SonSecenekler { get; private set; }
        public string SonSql { get; private set; } = "";

        public Task<QueryResult> ExecuteAsync(
            ConnectionProfile profil, string sql, ExecuteOptions opts, CancellationToken ct)
        {
            SonSecenekler = opts;
            SonSql = sql;
            return Task.FromResult(sonuc);
        }

        public Task<(bool Basarili, string? HataMesaji)> TestConnectionAsync(
            ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult((true, (string?)null));
    }
}

/// <summary>Gerçek LocalDB'ye karşı: nesne kur, şemada/gövdesinde doğrula, temizle.</summary>
public class SchemaServiceLocalDbTests
{
    private static ConnectionProfile LocalDbProfili() => new()
    {
        Ad = "localdb",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 60,
    };

    [Fact]
    public async Task Veritabani_listesi_sistem_dblerini_ve_erisilebilenleri_dondurur()
    {
        ConnectionProfile profil = LocalDbProfili();

        var lehce = new MssqlLehcesi(new DpapiSecretProtector());
        IReadOnlyList<VeritabaniBilgisi> bilgiler = await new SchemaService(new SqlExecutor(lehce), lehce)
            .VeritabanlariAsync(profil, CancellationToken.None);

        VeritabaniBilgisi master = Assert.Single(bilgiler, b => b.Ad == "master");
        Assert.True(master.SistemMi);
        Assert.True(Assert.Single(bilgiler, b => b.Ad == "tempdb").SistemMi);
        Assert.Equal(4, bilgiler.Count(b => b.SistemMi)); // master, model, msdb, tempdb
        // sistem DB'leri listenin sonunda sıralanır (kullanıcı DB'leri önce)
        List<VeritabaniBilgisi> liste = [.. bilgiler];
        Assert.True(liste.IndexOf(master) >= liste.Count - 4,
            $"master sona sıralanmalıydı: {string.Join(", ", liste.Select(b => b.Ad))}");
    }

    [Fact]
    public async Task Tempdb_de_kurulan_tablo_semada_pk_ve_tipleriyle_gorunur()
    {
        ConnectionProfile profil = LocalDbProfili();
        var lehce = new MssqlLehcesi(new DpapiSecretProtector());
        var executor = new SqlExecutor(lehce);
        var tempdbSecenegi = new ExecuteOptions { VeritabaniOverride = "tempdb" };
        string tablo = $"SemaTest_{Guid.NewGuid():N}";

        QueryResult kur = await executor.ExecuteAsync(profil,
            $"CREATE TABLE dbo.[{tablo}] (Id INT NOT NULL PRIMARY KEY, Ad NVARCHAR(50) NOT NULL, Bakiye DECIMAL(18,2) NULL);",
            tempdbSecenegi, CancellationToken.None);
        Assert.True(kur.Basarili, kur.Hata?.Mesaj);

        try
        {
            SemaOnbellegi sema = await new SchemaService(executor, lehce)
                .YukleAsync(profil, veritabani: "tempdb", CancellationToken.None);

            SemaNesnesi nesne = Assert.Single(sema.Nesneler, n => n.Ad == tablo);
            Assert.Equal(SemaNesneTuru.Tablo, nesne.Tur);
            Assert.Equal("tempdb", nesne.Veritabani);
            Assert.Equal(3, nesne.Kolonlar.Count);
            Assert.True(nesne.Kolonlar.Single(k => k.Ad == "Id").PkMi);
            Assert.Equal("nvarchar(50)", nesne.Kolonlar.Single(k => k.Ad == "Ad").Tip);
            Assert.True(nesne.Kolonlar.Single(k => k.Ad == "Bakiye").NullOlabilir);
        }
        finally
        {
            await executor.ExecuteAsync(profil, $"DROP TABLE dbo.[{tablo}];",
                tempdbSecenegi, CancellationToken.None);
        }
    }

    /// <summary>FG-5.1/5.2 uçtan uca: SP kur → tanımı oku → ALTER'a çevir → çalıştır → değişimi doğrula.</summary>
    [Fact]
    public async Task Sp_tanimi_okunur_alter_edilir_ve_parametreleri_semada_gorunur()
    {
        ConnectionProfile profil = LocalDbProfili();
        var lehce = new MssqlLehcesi(new DpapiSecretProtector());
        var executor = new SqlExecutor(lehce);
        var tempdbSecenegi = new ExecuteOptions { VeritabaniOverride = "tempdb" };
        var schema = new SchemaService(executor, lehce);
        string spAdi = $"spSemaTest_{Guid.NewGuid():N}";

        QueryResult kur = await executor.ExecuteAsync(profil,
            $"EXEC('CREATE PROCEDURE dbo.[{spAdi}] @Id INT, @Ad NVARCHAR(50) OUTPUT AS SELECT 1 AS Eski');",
            tempdbSecenegi, CancellationToken.None);
        Assert.True(kur.Basarili, kur.Hata?.Mesaj);

        try
        {
            SemaOnbellegi sema = await schema.YukleAsync(profil, "tempdb", CancellationToken.None);
            SemaNesnesi sp = Assert.Single(sema.Nesneler, n => n.Ad == spAdi);
            Assert.Equal(SemaNesneTuru.StoredProcedure, sp.Tur);
            Assert.Equal(["@Id", "@Ad"], sp.Parametreler.Select(p => p.Ad));
            Assert.Equal("nvarchar(50)", sp.Parametreler[1].Tip);
            Assert.True(sp.Parametreler[1].CikisMi);

            string? tanim = await schema.TanimGetirAsync(profil, sp, CancellationToken.None);
            Assert.NotNull(tanim);
            Assert.Contains("CREATE PROCEDURE", tanim!);

            // ALTER'a çevir, gövdeyi değiştir, çalıştır (FG-5.2)
            string alter = NesneScriptleyici.AlterEDonustur(tanim!).Replace("SELECT 1 AS Eski", "SELECT 2 AS Yeni");
            Assert.StartsWith("ALTER PROCEDURE", alter);

            QueryResult guncelle = await executor.ExecuteAsync(profil, alter, tempdbSecenegi, CancellationToken.None);
            Assert.True(guncelle.Basarili, guncelle.Hata?.Mesaj);

            string? yeniTanim = await schema.TanimGetirAsync(profil, sp, CancellationToken.None);
            Assert.Contains("SELECT 2 AS Yeni", yeniTanim);
        }
        finally
        {
            await executor.ExecuteAsync(profil, $"DROP PROCEDURE dbo.[{spAdi}];",
                tempdbSecenegi, CancellationToken.None);
        }
    }
}
