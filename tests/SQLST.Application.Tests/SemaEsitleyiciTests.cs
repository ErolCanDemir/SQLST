using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>🔀 Şema Eşitleme üreticisi (madde 4'ün ikinci yarısı, 2026-08-03): fark → DDL;
/// kurucular önce, DROP'lar YIKICI bölümde; FK/Index/gövdeler dürüst notla düşer.</summary>
public class SemaEsitleyiciTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static SemaNesnesi Tablo(string sema, string ad, params SemaKolonu[] kolonlar)
        => new("db", sema, ad, SemaNesneTuru.Tablo, kolonlar, []);

    [Fact]
    public void Yalniz_kaynakta_tablo_CREATE_uretir()
    {
        SemaNesnesi yeni = Tablo("dbo", "Yeni",
            new SemaKolonu("Id", "int", false, true), new SemaKolonu("Ad", "nvarchar(50)", true, false));

        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.Yeni", "Tablo", SemaDegisim.YalnizSol, "yalnız kaynakta")],
            [yeni], []);

        Assert.Contains("CREATE TABLE [dbo].[Yeni]", script);
        Assert.Contains("[Id] int NOT NULL", script);
        Assert.Contains("[Ad] nvarchar(50) NULL", script);
        Assert.Contains("FK/index'ler CREATE'e dahil DEĞİL", script); // dürüst sınır notu
    }

    [Fact]
    public void Yalniz_hedefte_tablo_DROP_yikici_bolumde()
    {
        SemaNesnesi fazla = Tablo("dbo", "Eski", new SemaKolonu("Id", "int", false, true));

        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.Eski", "Tablo", SemaDegisim.YalnizSag, "yalnız hedefte")],
            [], [fazla]);

        Assert.Contains("YIKICI BÖLÜM", script);
        Assert.Contains("DROP TABLE [dbo].[Eski];", script);
        int uyari = script.IndexOf("YIKICI BÖLÜM", StringComparison.Ordinal);
        int drop = script.IndexOf("DROP TABLE", StringComparison.Ordinal);
        Assert.True(uyari < drop); // uyarı DROP'tan önce gelir
    }

    [Fact]
    public void Kolon_farklari_ADD_DROP_ALTER_uretir()
    {
        SemaNesnesi sol = Tablo("dbo", "Musteri",
            new SemaKolonu("Id", "int", false, true),
            new SemaKolonu("Eposta", "nvarchar(100)", true, false),   // yalnız kaynakta → ADD
            new SemaKolonu("Yas", "bigint", false, false));            // tip değişti → ALTER
        SemaNesnesi sag = Tablo("dbo", "Musteri",
            new SemaKolonu("Id", "int", false, true),
            new SemaKolonu("Eski", "int", true, false),                // yalnız hedefte → DROP
            new SemaKolonu("Yas", "int", true, false));

        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
        [
            new SemaFarkSatiri("dbo.Musteri", "Kolon: Eposta", SemaDegisim.YalnizSol, "nvarchar(100) NULL"),
            new SemaFarkSatiri("dbo.Musteri", "Kolon: Eski", SemaDegisim.YalnizSag, "int NULL"),
            new SemaFarkSatiri("dbo.Musteri", "Kolon: Yas", SemaDegisim.Degisti, "bigint NOT NULL  →  int NULL"),
        ], [sol], [sag]);

        Assert.Contains("ALTER TABLE [dbo].[Musteri] ADD [Eposta] nvarchar(100) NULL;", script);
        Assert.Contains("ALTER TABLE [dbo].[Musteri] DROP COLUMN [Eski];", script);
        Assert.Contains("ALTER TABLE [dbo].[Musteri] ALTER COLUMN [Yas] bigint NOT NULL;", script);
    }

    [Fact]
    public void Postgres_tip_degisimi_type_ve_not_null_ayri_ifadeler()
    {
        var pg = new PostgresLehcesi(new DpapiSecretProtector());
        SemaNesnesi sol = Tablo("public", "musteri", new SemaKolonu("yas", "bigint", false, false));
        SemaNesnesi sag = Tablo("public", "musteri", new SemaKolonu("yas", "integer", true, false));

        string script = SemaEsitleyici.ScriptUret(pg, "h [db]",
            [new SemaFarkSatiri("public.musteri", "Kolon: yas", SemaDegisim.Degisti, "bigint NOT NULL  →  integer NULL")],
            [sol], [sag]);

        Assert.Contains("ALTER TABLE \"public\".\"musteri\" ALTER COLUMN \"yas\" TYPE bigint;", script);
        Assert.Contains("ALTER TABLE \"public\".\"musteri\" ALTER COLUMN \"yas\" SET NOT NULL;", script);
    }

    [Fact]
    public void Listeler_verilmeyince_fk_index_govde_notla_duser() // v2'de bile: eşleşme yoksa dürüst not
    {
        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
        [
            new SemaFarkSatiri("dbo.Siparis", "FK", SemaDegisim.YalnizSol, "MusteriId → dbo.Musteri(Id)"),
            new SemaFarkSatiri("dbo.VwOzet", "View", SemaDegisim.YalnizSol, "yalnız kaynakta"),
            new SemaFarkSatiri("dbo.Musteri", "Index", SemaDegisim.YalnizSag, "UNIQUE(Eposta)"),
        ], [], []);

        Assert.Contains("kaynak FK listesinde bulunamadı", script);
        Assert.Contains("hedef listede bulunamadı", script);
        Assert.Contains("tanımı okunamadı", script);
        Assert.Contains("Şema Kopyalama", script);              // gövdeli nesne için yönlendirme
        Assert.DoesNotContain("ALTER TABLE", script);           // hiçbir DDL üretilmedi
    }

    // --- v2 (2026-08-03 devamı): FK ADD + Index CREATE/DROP + gövde taşıma ---

    [Fact]
    public void Fk_yalniz_kaynakta_add_constraint_uretir()
    {
        var fk = new YabanciAnahtar("dbo", "Siparis", ["MusteriId"], "dbo", "Musteri", ["Id"]);
        string imza = SemaKarsilastirici.FkImza(fk);

        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.Siparis", "FK", SemaDegisim.YalnizSol, imza)],
            [], [], solFkler: [fk]);

        Assert.Contains("ALTER TABLE [dbo].[Siparis] ADD CONSTRAINT [FK_Siparis_Musteri_esitle] "
            + "FOREIGN KEY ([MusteriId]) REFERENCES [dbo].[Musteri] ([Id]);", script);
    }

    [Fact]
    public void Fk_yalniz_hedefte_drop_uretilmez_ad_sorgusu_verilir()
    {
        var fk = new YabanciAnahtar("dbo", "Siparis", ["EskiId"], "dbo", "Eski", ["Id"]);
        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.Siparis", "FK", SemaDegisim.YalnizSag, SemaKarsilastirici.FkImza(fk))],
            [], []);

        Assert.DoesNotContain("DROP CONSTRAINT [", script);      // körlemesine DROP yok (ad bilinmiyor)
        Assert.Contains("sys.foreign_keys", script);             // adı bulan hazır sorgu yorumda
    }

    [Fact]
    public void Index_yalniz_kaynakta_create_yalniz_hedefte_drop()
    {
        var solIx = new Indeks("dbo", "Musteri", "IX_Musteri_Eposta", true, ["Eposta"]);
        var sagIx = new Indeks("dbo", "Musteri", "IX_Eski", false, ["Ad", "Soyad"]);

        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
        [
            new SemaFarkSatiri("dbo.Musteri", "Index", SemaDegisim.YalnizSol, SemaKarsilastirici.IndeksImza(solIx)),
            new SemaFarkSatiri("dbo.Musteri", "Index", SemaDegisim.YalnizSag, SemaKarsilastirici.IndeksImza(sagIx)),
        ], [], [], solIndeksler: [solIx], sagIndeksler: [sagIx]);

        Assert.Contains("CREATE UNIQUE INDEX [IX_Musteri_Eposta] ON [dbo].[Musteri] ([Eposta]);", script);
        Assert.Contains("DROP INDEX [IX_Eski] ON [dbo].[Musteri];", script); // MSSQL biçimi + yıkıcı bölümde
        int yikici = script.IndexOf("YIKICI BÖLÜM", StringComparison.Ordinal);
        Assert.True(yikici >= 0 && script.IndexOf("DROP INDEX", StringComparison.Ordinal) > yikici);
    }

    [Fact]
    public void Postgres_index_drop_tablosuz()
    {
        var pg = new PostgresLehcesi(new DpapiSecretProtector());
        var ix = new Indeks("public", "musteri", "ix_eski", false, ["ad"]);

        string script = SemaEsitleyici.ScriptUret(pg, "h [db]",
            [new SemaFarkSatiri("public.musteri", "Index", SemaDegisim.YalnizSag, SemaKarsilastirici.IndeksImza(ix))],
            [], [], sagIndeksler: [ix]);

        Assert.Contains("DROP INDEX \"ix_eski\";", script); // PG'de ON tablo yazılmaz
    }

    [Fact]
    public void Govdeli_nesne_tanimi_verilirse_scripte_girer_pg_view_sarmalanir()
    {
        string mssqlScript = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.SpHesap", "StoredProcedure", SemaDegisim.YalnizSol, "yalnız kaynakta")],
            [], [], govdeler: new Dictionary<string, string>
                { ["dbo.SpHesap"] = "CREATE PROCEDURE dbo.SpHesap AS SELECT 1;" });
        Assert.Contains("CREATE PROCEDURE dbo.SpHesap AS SELECT 1;", mssqlScript);

        // PG view tanımı çıplak SELECT gelebilir → CREATE VIEW … AS sarmalanır
        var pg = new PostgresLehcesi(new DpapiSecretProtector());
        string pgScript = SemaEsitleyici.ScriptUret(pg, "h [db]",
            [new SemaFarkSatiri("public.v_ozet", "View", SemaDegisim.YalnizSol, "yalnız kaynakta")],
            [], [], govdeler: new Dictionary<string, string> { ["public.v_ozet"] = "SELECT id, ad FROM musteri" });
        Assert.Contains("CREATE VIEW \"public\".\"v_ozet\" AS\nSELECT id, ad FROM musteri;", pgScript);
    }

    [Fact]
    public void Govdeli_nesne_yalniz_hedefte_drop_uretir()
    {
        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
        [
            new SemaFarkSatiri("dbo.VwEski", "View", SemaDegisim.YalnizSag, "yalnız hedefte"),
            new SemaFarkSatiri("dbo.SpEski", "StoredProcedure", SemaDegisim.YalnizSag, "yalnız hedefte"),
            new SemaFarkSatiri("dbo.FnEski", "Fonksiyon", SemaDegisim.YalnizSag, "yalnız hedefte"),
        ], [], []);

        Assert.Contains("DROP VIEW [dbo].[VwEski];", script);
        Assert.Contains("DROP PROCEDURE [dbo].[SpEski];", script);
        Assert.Contains("DROP FUNCTION [dbo].[FnEski];", script);
        Assert.Contains("YIKICI", script);
    }

    // --- v19-S4 (2026-08-03): FK DROP (ad modelde) + PK bloğu + gövde değişimi ---

    [Fact]
    public void Hedefteki_fazla_fk_adiyla_drop_edilir()
    {
        var fk = new YabanciAnahtar("dbo", "Siparis", ["EskiId"], "dbo", "Eski", ["Id"], "FK_Siparis_Eski");

        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.Siparis", "FK", SemaDegisim.YalnizSag, SemaKarsilastirici.FkImza(fk))],
            [], [], sagFkler: [fk]);

        Assert.Contains("ALTER TABLE [dbo].[Siparis] DROP CONSTRAINT [FK_Siparis_Eski];", script);
        Assert.Contains("YIKICI", script); // DROP yıkıcı bölümde
    }

    [Fact]
    public void Mysql_fk_dropu_foreign_key_soz_dizimiyle()
    {
        var my = new MySqlLehcesi(new DpapiSecretProtector());
        var fk = new YabanciAnahtar("sirket", "siparis", ["eski_id"], "sirket", "eski", ["id"], "fk_siparis_eski");

        string script = SemaEsitleyici.ScriptUret(my, "h [db]",
            [new SemaFarkSatiri("sirket.siparis", "FK", SemaDegisim.YalnizSag, SemaKarsilastirici.FkImza(fk))],
            [], [], sagFkler: [fk]);

        Assert.Contains("DROP FOREIGN KEY `fk_siparis_eski`;", script); // MySQL: DROP CONSTRAINT değil
    }

    [Fact]
    public void Fk_adi_yoksa_eski_davranis_ad_bulma_sorgusu()
    {
        var fk = new YabanciAnahtar("dbo", "Siparis", ["EskiId"], "dbo", "Eski", ["Id"]); // Ad = null

        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.Siparis", "FK", SemaDegisim.YalnizSag, SemaKarsilastirici.FkImza(fk))],
            [], [], sagFkler: [fk]);

        Assert.DoesNotContain("DROP CONSTRAINT [", script);
        Assert.Contains("sys.foreign_keys", script); // adı bulan sorgu notta
    }

    [Fact]
    public void PK_degisimi_mssql_tam_blok_uretir()
    {
        SemaNesnesi sol = Tablo("dbo", "T",
            new SemaKolonu("Id", "bigint", false, true), new SemaKolonu("Ad", "nvarchar(50)", true, false));
        SemaNesnesi sag = Tablo("dbo", "T",
            new SemaKolonu("Id", "int", false, false), new SemaKolonu("Ad", "nvarchar(50)", true, false));

        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.T", "Kolon: Id", SemaDegisim.Degisti, "bigint NOT NULL PK  →  int NOT NULL")],
            [sol], [sag]);

        Assert.Contains("sys.key_constraints", script);                      // mevcut PK adı dinamik bulunur
        Assert.Contains("DROP CONSTRAINT ' + QUOTENAME(@pk)", script);
        Assert.Contains("ALTER TABLE [dbo].[T] ALTER COLUMN [Id] bigint NOT NULL;", script); // tip DROP-ADD arasında
        Assert.Contains("ADD CONSTRAINT [PK_T_esitle] PRIMARY KEY ([Id]);", script);
        int drop = script.IndexOf("DROP CONSTRAINT", StringComparison.Ordinal);
        int alter = script.IndexOf("ALTER COLUMN", StringComparison.Ordinal);
        int add = script.IndexOf("ADD CONSTRAINT [PK_T_esitle]", StringComparison.Ordinal);
        Assert.True(drop < alter && alter < add); // sıra: DROP PK → ALTER → ADD PK
    }

    [Fact]
    public void PK_degisimi_postgres_not_ve_kalip_verir()
    {
        var pg = new PostgresLehcesi(new DpapiSecretProtector());
        SemaNesnesi sol = Tablo("public", "t", new SemaKolonu("id", "bigint", false, true));
        SemaNesnesi sag = Tablo("public", "t", new SemaKolonu("id", "integer", false, false));

        string script = SemaEsitleyici.ScriptUret(pg, "h [db]",
            [new SemaFarkSatiri("public.t", "Kolon: id", SemaDegisim.Degisti, "bigint NOT NULL PK  →  integer NOT NULL")],
            [sol], [sag]);

        Assert.Contains("contype = 'p'", script);          // PK adını bulan sorgu
        Assert.Contains("ADD PRIMARY KEY (\"id\")", script); // kalıp DDL notta
        Assert.DoesNotContain("sys.key_constraints", script);
    }

    [Fact]
    public void Govdesi_degisen_sp_mssql_create_or_alter_ile_degistirilir()
    {
        string script = SemaEsitleyici.ScriptUret(Mssql, "h [db]",
            [new SemaFarkSatiri("dbo.SpHesap", "StoredProcedure", SemaDegisim.Degisti, "gövde farklı")],
            [], [], govdeler: new Dictionary<string, string>
                { ["dbo.SpHesap"] = "CREATE PROCEDURE dbo.SpHesap AS SELECT 2;" });

        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.SpHesap AS SELECT 2;", script);
        Assert.DoesNotContain("DROP PROCEDURE", script); // OR ALTER — izinler korunur, DROP gerekmez
    }

    [Fact]
    public void Govdesi_degisen_view_mysql_or_replace_sp_drop_create()
    {
        var my = new MySqlLehcesi(new DpapiSecretProtector());

        string viewScript = SemaEsitleyici.ScriptUret(my, "h [db]",
            [new SemaFarkSatiri("sirket.v_ozet", "View", SemaDegisim.Degisti, "gövde farklı")],
            [], [], govdeler: new Dictionary<string, string>
                { ["sirket.v_ozet"] = "CREATE VIEW v_ozet AS SELECT 1;" });
        Assert.Contains("CREATE OR REPLACE VIEW v_ozet AS SELECT 1;", viewScript);

        string spScript = SemaEsitleyici.ScriptUret(my, "h [db]",
            [new SemaFarkSatiri("sirket.sp_x", "StoredProcedure", SemaDegisim.Degisti, "gövde farklı")],
            [], [], govdeler: new Dictionary<string, string>
                { ["sirket.sp_x"] = "CREATE PROCEDURE sp_x() SELECT 1;" });
        Assert.Contains("DROP PROCEDURE IF EXISTS `sp_x`;", spScript); // MySQL'de OR REPLACE yok
        Assert.Contains("CREATE PROCEDURE sp_x() SELECT 1;", spScript);
    }
}
