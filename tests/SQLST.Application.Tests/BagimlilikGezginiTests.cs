using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>🔗 Bağımlılık Gezgini üreticileri (2026-08-03): iki yönlü script + ağaç yön sorgusu.</summary>
public class BagimlilikGezginiTests
{
    private static SemaNesnesi Nesne(string sema = "dbo", string ad = "Musteri")
        => new("db", sema, ad, SemaNesneTuru.Tablo, [], []);

    [Fact]
    public void ScriptUret_iki_yonu_de_icerir()
    {
        string script = BagimlilikGezgini.ScriptUret(Nesne());

        Assert.Contains("OBJECT_ID(N'dbo.Musteri')", script);
        Assert.Contains("referenced_id = OBJECT_ID", script);   // ⬅ kullananlar
        Assert.Contains("referencing_id = OBJECT_ID", script);  // ➡ kullandıkları
        Assert.Contains("sys.sql_expression_dependencies", script);
    }

    [Fact]
    public void YonSorgusu_kullananlar_referenced_uzerinden_suzer()
    {
        string sql = BagimlilikGezgini.YonSorgusu("dbo.Musteri", kullananlar: true);

        Assert.Contains("d.referenced_id = OBJECT_ID(N'dbo.Musteri')", sql);
        Assert.Contains("d.referencing_id", sql); // dönen nesne: başvuran taraf
        Assert.Contains("[Nesne]", sql);
        Assert.Contains("[Tür]", sql);
    }

    [Fact]
    public void YonSorgusu_kullandiklari_referencing_uzerinden_suzer()
    {
        string sql = BagimlilikGezgini.YonSorgusu("dbo.SiparisIsle", kullananlar: false);

        Assert.Contains("d.referencing_id = OBJECT_ID(N'dbo.SiparisIsle')", sql);
        Assert.Contains("referenced_entity_name", sql); // çözülemeyen (cross-db) ad da listelenir
    }

    [Fact]
    public void YonSorgusu_apostroflu_ad_kacislanir()
    {
        string sql = BagimlilikGezgini.YonSorgusu("dbo.O'Brien", kullananlar: true);
        Assert.Contains("OBJECT_ID(N'dbo.O''Brien')", sql);
    }

    [Fact]
    public void Agac_dugumu_scripti_metin_adla_uretilir()
    {
        string script = BagimlilikGezgini.ScriptUret("satis.Rapor", "VIEW");
        Assert.Contains("satis.Rapor (VIEW)", script);
        Assert.Contains("OBJECT_ID(N'satis.Rapor')", script);
    }

    // --- Madde 2 (2026-08-03): PostgreSQL yönü ---

    [Fact]
    public void Pg_kullananlar_pg_depend_ve_govde_taramasi()
    {
        string sql = BagimlilikGezgini.YonSorgusu("public.musteri", kullananlar: true, "postgres");

        Assert.Contains("to_regclass('public.musteri')", sql);   // bilinmeyen ad hata değil BOŞ sonuç
        Assert.Contains("pg_rewrite", sql);                      // view bağımlılıkları KESİN
        Assert.Contains("d.refobjid = to_regclass", sql);        // ⬅ yön: bu nesneye başvuranlar
        Assert.Contains("prosrc ILIKE '%musteri%'", sql);        // fonksiyonlar ~ yaklaşık (şemasız ad)
        Assert.Contains("FUNCTION ~", sql);                      // yaklaşıklık Tür'de işaretli
        Assert.DoesNotContain("sys.sql_expression_dependencies", sql);
    }

    [Fact]
    public void Pg_kullandiklari_rewrite_uzerinden_ve_fonksiyon_taramasiz()
    {
        string sql = BagimlilikGezgini.YonSorgusu("public.v_ozet", kullananlar: false, "postgres");

        Assert.Contains("r.ev_class = to_regclass('public.v_ozet')", sql); // ➡ yön: bu view'ın kullandıkları
        Assert.Contains("c.oid = d.refobjid", sql);
        Assert.DoesNotContain("prosrc", sql); // gövde taraması yalnız ⬅ yönünde (tersini bilemeyiz)
    }

    [Fact]
    public void Pg_apostroflu_ad_kacislanir()
    {
        string sql = BagimlilikGezgini.YonSorgusu("public.o'brien", kullananlar: true, "postgres");
        Assert.Contains("to_regclass('public.o''brien')", sql);
        Assert.Contains("ILIKE '%o''brien%'", sql);
    }

    [Fact]
    public void Pg_scripti_iki_yonu_ve_yaklasiklik_notunu_icerir()
    {
        string script = BagimlilikGezgini.ScriptUret("public.musteri", "Tablo", "postgres");

        Assert.Contains("PostgreSQL", script);
        Assert.Contains("⬅ Bunu KULLANANLAR", script);
        Assert.Contains("➡ Bunun KULLANDIKLARI", script);
        Assert.Contains("YAKLAŞIK", script); // fonksiyon eşleşmesinin sınırı script başında dürüstçe yazar
        Assert.DoesNotContain("OBJECT_ID", script);
    }

    [Fact]
    public void Motor_belirtilmezse_mssql_kalir() // geriye uyum — mevcut çağıranlar kırılmaz
        => Assert.Contains("sys.sql_expression_dependencies",
            BagimlilikGezgini.YonSorgusu("dbo.X", kullananlar: true));

    // --- MySQL yönü (2026-08-03 devamı) ---

    [Fact]
    public void Mysql_kullananlar_view_table_usage_ve_rutin_taramasi()
    {
        string sql = BagimlilikGezgini.YonSorgusu("sirket.musteri", kullananlar: true, "mysql");

        Assert.Contains("information_schema.VIEW_TABLE_USAGE", sql);   // view'lar KESİN (8.0.13+)
        Assert.Contains("TABLE_NAME = 'musteri'", sql);                // yalın ad — şema DATABASE()
        Assert.Contains("TABLE_SCHEMA = DATABASE()", sql);
        Assert.Contains("ROUTINE_DEFINITION LIKE '%musteri%'", sql);   // rutinler ~ yaklaşık
        Assert.Contains("~", sql);
        Assert.DoesNotContain("pg_depend", sql);
    }

    [Fact]
    public void Mysql_kullandiklari_view_uzerinden()
    {
        string sql = BagimlilikGezgini.YonSorgusu("v_ozet", kullananlar: false, "mysql");

        Assert.Contains("VIEW_NAME = 'v_ozet'", sql);
        Assert.Contains("VIEW_SCHEMA = DATABASE()", sql);
        Assert.DoesNotContain("ROUTINE_DEFINITION", sql); // gövde taraması yalnız ⬅ yönünde
    }

    [Fact]
    public void Mysql_scripti_surum_notunu_icerir()
    {
        string script = BagimlilikGezgini.ScriptUret("sirket.musteri", "Tablo", "mysql");
        Assert.Contains("MySQL 8.0.13+", script); // MariaDB sınırı script başında dürüstçe yazar
        Assert.Contains("⬅ Bunu KULLANANLAR", script);
        Assert.Contains("➡ Bunun KULLANDIKLARI", script);
    }

    // --- Oracle yönü (2026-08-03 devamı) ---

    [Fact]
    public void Oracle_kullananlar_all_dependencies_ve_buyuk_harf()
    {
        string sql = BagimlilikGezgini.YonSorgusu("satis.musteri", kullananlar: true, "oracle");

        Assert.Contains("all_dependencies", sql);
        Assert.Contains("d.referenced_name = 'MUSTERI'", sql);   // Oracle sözlüğü BÜYÜK harf
        Assert.Contains("d.referenced_owner = 'SATIS'", sql);
        Assert.DoesNotContain("~", sql);                          // kesin veri — yaklaşıklık işareti yok
        Assert.DoesNotContain(";", sql);                          // tek ifade, sonda ';' yok (ORA-00933)
    }

    [Fact]
    public void Oracle_kullandiklari_sistem_semalari_suzulur()
    {
        string sql = BagimlilikGezgini.YonSorgusu("satis.sp_hesap", kullananlar: false, "oracle");

        Assert.Contains("d.name = 'SP_HESAP'", sql);
        Assert.Contains("d.owner = 'SATIS'", sql);
        Assert.Contains("NOT IN ('SYS', 'SYSTEM', 'PUBLIC')", sql); // STANDARD paketi vb. gürültü olmasın
    }

    [Fact]
    public void Oracle_scripti_iki_yonu_icerir()
    {
        string script = BagimlilikGezgini.ScriptUret("SATIS.MUSTERI", "Tablo", "oracle");
        Assert.Contains("ALL_DEPENDENCIES'ten KESİNDİR", script);
        Assert.Contains("⬅ Bunu KULLANANLAR", script);
        Assert.Contains("➡ Bunun KULLANDIKLARI", script);
    }
}
