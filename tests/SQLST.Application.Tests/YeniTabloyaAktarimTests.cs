using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 📦 "Yeni tabloya aktar" (v23-S32, SELECT INTO gibi) — SAF çekirdek: var olan ad çarpışması, CREATE TABLE
/// (kaynak tipleri birebir + PK), kestirme ifadeleri (MSSQL INTO enjeksiyonu CTE/ORDER BY ile, PG/MySQL/
/// Oracle CTAS, PG farklı DB → yok, çok ifadeli → yok) ve CANLI LocalDB: kestirme (SELECT INTO + PK tek
/// işlemde; hata → hiçbir şey kalmaz) ve akışlı yol (OnceDdl → CREATE + parti INSERT).
/// </summary>
public class YeniTabloyaAktarimTests
{
    private static readonly DpapiSecretProtector Koruyucu = new();
    private static readonly MssqlLehcesi Mssql = new(Koruyucu);
    private static readonly PostgresLehcesi Postgres = new(Koruyucu);
    private static readonly MySqlLehcesi MySql = new(Koruyucu);
    private static readonly OracleLehcesi Oracle = new(Koruyucu);

    private static readonly IReadOnlyList<SemaKolonu> Kolonlar =
    [
        new("Id", "int", false, true), new("Ad", "nvarchar(50)", true, false),
        new("Tutar", "decimal(18,2)", false, false),
    ];

    [Theory]
    [InlineData("dbo.Siparis", "dbo.Siparis")]       // tam ad
    [InlineData("DBO.SIPARIS", "dbo.Siparis")]       // harf duyarsız
    [InlineData("Siparis", "dbo.Siparis")]           // yalnız ad → şemalı tabloya çarpar
    [InlineData("Siparis_2025", null)]               // yeni
    [InlineData("arsiv.Siparis", null)]              // başka şema → yeni
    [InlineData("  ", null)]
    public void Var_olan_tablo_carpismasi(string yazilan, string? beklenen)
        => Assert.Equal(beklenen, YeniTabloyaAktarim.VarOlanTablo(yazilan, ["dbo.Siparis", "dbo.Musteri"]));

    [Fact]
    public void Create_table_kaynak_tipleri_birebir_pk_secmeli()
    {
        (string? sql, string? hata) = YeniTabloyaAktarim.CreateTableSql(Mssql, "arsiv.Siparis_2025", Kolonlar, ["Id"]);
        Assert.Null(hata);
        Assert.Contains("CREATE TABLE [arsiv].[Siparis_2025] (", sql);
        Assert.Contains("[Id] int NOT NULL", sql);
        Assert.Contains("[Ad] nvarchar(50) NULL", sql);
        Assert.Contains("[Tutar] decimal(18,2) NOT NULL", sql);
        Assert.Contains("PRIMARY KEY", sql);

        // PK istenmedi → kısıt yok; şemasız ad tek parça tırnaklı.
        (sql, _) = YeniTabloyaAktarim.CreateTableSql(Mssql, "Siparis_2025", Kolonlar, []);
        Assert.StartsWith("CREATE TABLE [Siparis_2025] (", sql);
        Assert.DoesNotContain("PRIMARY KEY", sql);

        // Boş ad reddedilir.
        Assert.NotNull(YeniTabloyaAktarim.CreateTableSql(Mssql, " ", Kolonlar, []).Hata);
    }

    [Fact]
    public void Mssql_kestirme_into_enjeksiyonu_cte_ve_order_by_korunur()
    {
        const string sql = """
            WITH c AS (SELECT Id, Ad FROM dbo.Siparis WHERE Yil = 2025)
            SELECT c.Id, c.Ad FROM c ORDER BY c.Id
            """;
        (YeniTabloyaAktarim.KestirmePlani? plan, string? neden) = YeniTabloyaAktarim.KestirmeIfadeleri(
            Mssql, sql, "Satis", "Arsiv", "dbo.Siparis_2025", ["Id"]);

        Assert.Null(neden);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Ifadeler.Count);
        string into = plan.Ifadeler[0].Replace("\r\n", " ").Replace("\n", " ");
        Assert.StartsWith("WITH c AS", into);
        Assert.Contains("INTO [Arsiv].[dbo].[Siparis_2025]", into);
        Assert.Contains("FROM c", into);
        Assert.Contains("ORDER BY c.Id", into);
        Assert.Equal("[Arsiv].[dbo].[Siparis_2025]", plan.HedefTamAd);
        Assert.Equal("ALTER TABLE [Arsiv].[dbo].[Siparis_2025] ADD CONSTRAINT [PK_Siparis_2025] PRIMARY KEY ([Id]);", plan.Ifadeler[1]);
    }

    [Fact]
    public void Mssql_kestirme_ayni_db_ve_semasiz_hedef()
    {
        // Aynı DB: yalnız [şema].[ad]; şemasız farklı DB: [Db]..[ad] (varsayılan şema).
        (YeniTabloyaAktarim.KestirmePlani? ayni, _) = YeniTabloyaAktarim.KestirmeIfadeleri(
            Mssql, "SELECT 1 AS x", "Satis", "Satis", "dbo.T", []);
        Assert.Contains("INTO [dbo].[T]", ayni!.Ifadeler[0]);
        Assert.Single(ayni.Ifadeler);

        (YeniTabloyaAktarim.KestirmePlani? semasiz, _) = YeniTabloyaAktarim.KestirmeIfadeleri(
            Mssql, "SELECT 1 AS x", "Satis", "Arsiv", "T", []);
        Assert.Contains("INTO [Arsiv]..[T]", semasiz!.Ifadeler[0]);
    }

    [Theory]
    [InlineData("SELECT 1 AS x; SELECT 2 AS y", "TEK bir SELECT")]      // çok ifadeli
    [InlineData("UPDATE t SET a = 1", "TEK bir SELECT")]                 // SELECT değil
    [InlineData("SELECT * INTO z FROM t", "zaten INTO")]                 // zaten INTO
    [InlineData("SELECT FROM WHERE", "çözümlenemedi")]                   // parse hatası
    public void Mssql_kestirme_uygun_olmayan_sorguda_akisli_yola_duser(string sql, string nedenParcasi)
    {
        (YeniTabloyaAktarim.KestirmePlani? plan, string? neden) = YeniTabloyaAktarim.KestirmeIfadeleri(
            Mssql, sql, "Satis", "Satis", "dbo.T", []);
        Assert.Null(plan);
        Assert.Contains(nedenParcasi, neden);
    }

    [Fact]
    public void Diger_motorlarda_ctas_pg_farkli_db_yok()
    {
        (YeniTabloyaAktarim.KestirmePlani? pg, string? pgNeden) = YeniTabloyaAktarim.KestirmeIfadeleri(
            Postgres, "SELECT id, ad FROM siparis;", "satis", "satis", "arsiv.siparis_2025", ["id"]);
        Assert.Null(pgNeden);
        Assert.Equal("CREATE TABLE \"arsiv\".\"siparis_2025\" AS\nSELECT id, ad FROM siparis;", pg!.Ifadeler[0]);
        Assert.Equal("ALTER TABLE \"arsiv\".\"siparis_2025\" ADD CONSTRAINT \"PK_siparis_2025\" PRIMARY KEY (\"id\");", pg.Ifadeler[1]);

        // PG'de başka veritabanı aynı bağlantıdan görülemez → kestirme yok (akışlı yol).
        (YeniTabloyaAktarim.KestirmePlani? pgFarkli, string? neden) = YeniTabloyaAktarim.KestirmeIfadeleri(
            Postgres, "SELECT 1", "satis", "arsiv", "t", []);
        Assert.Null(pgFarkli);
        Assert.Contains("PostgreSQL", neden);

        // MySQL: şema = veritabanı → `db`.`tablo`.
        (YeniTabloyaAktarim.KestirmePlani? my, _) = YeniTabloyaAktarim.KestirmeIfadeleri(
            MySql, "SELECT 1", "satis", "arsiv", "t", []);
        Assert.Equal("CREATE TABLE `arsiv`.`t` AS\nSELECT 1;", my!.Ifadeler[0]);

        // Oracle: "db" zaten şemadır; ';' yok.
        (YeniTabloyaAktarim.KestirmePlani? ora, _) = YeniTabloyaAktarim.KestirmeIfadeleri(
            Oracle, "SELECT 1 FROM dual", "SATIS", "ARSIV", "T", ["ID"]);
        Assert.Equal("CREATE TABLE \"ARSIV\".\"T\" AS\nSELECT 1 FROM dual", ora!.Ifadeler[0]);
        Assert.EndsWith("PRIMARY KEY (\"ID\")", ora.Ifadeler[1]);
    }

    [Fact]
    public void Ayni_sunucu_motor_ve_ad_harf_duyarsiz()
    {
        var a = new ConnectionProfile { Motor = MotorTuru.Mssql, Sunucu = "SRV1" };
        Assert.True(YeniTabloyaAktarim.AyniSunucuMu(a, new ConnectionProfile { Motor = MotorTuru.Mssql, Sunucu = " srv1 " }));
        Assert.False(YeniTabloyaAktarim.AyniSunucuMu(a, new ConnectionProfile { Motor = MotorTuru.Mssql, Sunucu = "SRV2" }));
        Assert.False(YeniTabloyaAktarim.AyniSunucuMu(a, new ConnectionProfile { Motor = MotorTuru.Postgres, Sunucu = "SRV1" }));
    }

    // ---- Canlı LocalDB ----

    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static Task<QueryResult> Kos(string sql)
        => new SqlExecutor(Koruyucu).ExecuteAsync(Profil(), sql,
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);

    [Fact]
    public async Task Canli_kestirme_select_into_ve_pk_tek_islemde_hata_olursa_hicbir_sey_kalmaz()
    {
        var servis = new AktarimServisi(new LehceSaglayici(Koruyucu));
        QueryResult hazir = await Kos("""
            IF OBJECT_ID('tempdb.dbo.SqlstYtKaynak') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtKaynak;
            IF OBJECT_ID('tempdb.dbo.SqlstYtHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtHedef;
            CREATE TABLE tempdb.dbo.SqlstYtKaynak (Id int NOT NULL PRIMARY KEY, Ad nvarchar(50) NULL, Tutar decimal(18,2) NOT NULL);
            INSERT INTO tempdb.dbo.SqlstYtKaynak VALUES (1, N'Ali', 10.5), (2, N'Ayşe', 20), (3, NULL, 30.25);
            """);
        Assert.True(hazir.Basarili, hazir.Hata?.Mesaj);
        try
        {
            (YeniTabloyaAktarim.KestirmePlani? plan, string? neden) = YeniTabloyaAktarim.KestirmeIfadeleri(
                Mssql, "SELECT Id, Ad, Tutar FROM dbo.SqlstYtKaynak WHERE Id <= 2 ORDER BY Id", "tempdb", "tempdb", "dbo.SqlstYtHedef", ["Id"]);
            Assert.Null(neden);

            AktarimSonucu sonuc = await servis.KestirmeAktarAsync(Profil(), "tempdb", plan!.Ifadeler, plan.HedefTamAd, CancellationToken.None);

            Assert.True(sonuc.Basarili, sonuc.Hata);
            Assert.Equal(2, sonuc.Yazilan);
            QueryResult kontrol = await Kos("""
                SELECT (SELECT COUNT(*) FROM dbo.SqlstYtHedef) AS Satir,
                       (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('dbo.SqlstYtHedef') AND type = 'PK') AS Pk,
                       (SELECT TYPE_NAME(system_type_id) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SqlstYtHedef') AND name = 'Ad') AS AdTipi;
                """);
            object?[] satir = kontrol.ResultSetler[0].Satirlar[0];
            Assert.Equal(2, (int)satir[0]!);
            Assert.Equal(1, (int)satir[1]!);
            Assert.Equal("nvarchar", (string)satir[2]!);

            // İkinci adım (PK) düşerse — PK kolonu NULL içeriyor — SELECT INTO da geri alınır: tablo KALMAZ.
            (YeniTabloyaAktarim.KestirmePlani? kotu, _) = YeniTabloyaAktarim.KestirmeIfadeleri(
                Mssql, "SELECT Id, Ad FROM dbo.SqlstYtKaynak", "tempdb", "tempdb", "dbo.SqlstYtHedef2", ["Ad"]);
            AktarimSonucu basarisiz = await servis.KestirmeAktarAsync(Profil(), "tempdb", kotu!.Ifadeler, kotu.HedefTamAd, CancellationToken.None);
            Assert.False(basarisiz.Basarili);
            Assert.Contains("Adım 2", basarisiz.Hata);
            QueryResult yok = await Kos("SELECT OBJECT_ID('tempdb.dbo.SqlstYtHedef2') AS Oid;");
            Assert.True(yok.ResultSetler[0].Satirlar[0][0] is null or DBNull);
        }
        finally
        {
            await Kos("""
                IF OBJECT_ID('tempdb.dbo.SqlstYtKaynak') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtKaynak;
                IF OBJECT_ID('tempdb.dbo.SqlstYtHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtHedef;
                IF OBJECT_ID('tempdb.dbo.SqlstYtHedef2') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtHedef2;
                """);
        }
    }

    [Fact]
    public async Task Canli_akisli_yol_once_ddl_ile_tablo_olusturup_doldurur()
    {
        var servis = new AktarimServisi(new LehceSaglayici(Koruyucu));
        QueryResult hazir = await Kos("""
            IF OBJECT_ID('tempdb.dbo.SqlstYtAkKaynak') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtAkKaynak;
            IF OBJECT_ID('tempdb.dbo.SqlstYtAkHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtAkHedef;
            CREATE TABLE tempdb.dbo.SqlstYtAkKaynak (Id int NOT NULL PRIMARY KEY, Ad nvarchar(50) NULL);
            INSERT INTO tempdb.dbo.SqlstYtAkKaynak VALUES (1, N'Ali'), (2, N'Ayşe'), (3, N'Can');
            """);
        Assert.True(hazir.Basarili, hazir.Hata?.Mesaj);
        try
        {
            IReadOnlyList<SemaKolonu> kolonlar = [new("Id", "int", false, true), new("Ad", "nvarchar(50)", true, false)];
            (string? create, _) = YeniTabloyaAktarim.CreateTableSql(Mssql, "dbo.SqlstYtAkHedef", kolonlar, ["Id"]);
            var istek = new AktarimIstegi(
                Profil(), "tempdb", "SELECT [Id], [Ad] FROM [dbo].[SqlstYtAkKaynak]",
                Profil(), "tempdb", "dbo.SqlstYtAkHedef",
                [new("Id", "Id"), new("Ad", "Ad")], OnceDdl: create);

            AktarimSonucu sonuc = await servis.AktarAsync(istek, null, CancellationToken.None);

            Assert.True(sonuc.Basarili, sonuc.Hata);
            Assert.Equal(3, sonuc.Yazilan);
            QueryResult kontrol = await Kos("SELECT COUNT(*) AS N FROM dbo.SqlstYtAkHedef WHERE Ad = N'Ayşe';");
            Assert.Equal(1, (int)kontrol.ResultSetler[0].Satirlar[0][0]!);

            // DDL düşerse (tablo zaten var) aktarım HİÇ başlamaz, mesaj nedeni söyler.
            AktarimSonucu tekrar = await servis.AktarAsync(istek, null, CancellationToken.None);
            Assert.False(tekrar.Basarili);
            Assert.StartsWith("Hedef tablo oluşturulamadı", tekrar.Hata);
            Assert.Equal(0, tekrar.Okunan);
        }
        finally
        {
            await Kos("""
                IF OBJECT_ID('tempdb.dbo.SqlstYtAkKaynak') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtAkKaynak;
                IF OBJECT_ID('tempdb.dbo.SqlstYtAkHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstYtAkHedef;
                """);
        }
    }
}
