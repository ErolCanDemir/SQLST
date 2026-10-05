using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>🔎 Veri Arama çekirdeği (2026-08-03): tip süzgeci + literal + kolon-yok null.
/// Özellik eşitliği: üretici ILehce ile motor-parametrik — PG/MySQL çıktıları da sabitlenir.</summary>
public class VeriArayiciTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());
    private static readonly ILehce Postgres = new PostgresLehcesi(new DpapiSecretProtector());
    private static readonly ILehce MySql = new MySqlLehcesi(new DpapiSecretProtector());

    private static SemaNesnesi Tablo(params SemaKolonu[] k)
        => new("db", "dbo", "Kisi", SemaNesneTuru.Tablo, k, []);

    [Fact]
    public void Sayi_degeri_sayisal_ve_metin_kolonlarda_aranir()
    {
        var t = Tablo(
            new SemaKolonu("Id", "int", false, true),
            new SemaKolonu("TcKimlikNo", "decimal(18,0)", true, false),
            new SemaKolonu("Ad", "nvarchar(70)", false, false),
            new SemaKolonu("Dogum", "datetime", true, false)); // tarih taranmaz

        string? sql = VeriArayici.SorguUret(t, "12345678901", Mssql);

        Assert.NotNull(sql);
        Assert.Contains("[Id] = 12345678901", sql);
        Assert.Contains("[TcKimlikNo] = 12345678901", sql);
        Assert.Contains("[Ad] = N'12345678901'", sql);   // metin kolonda metin olarak da
        Assert.DoesNotContain("[Dogum]", sql!);
        Assert.Contains("TOP (100)", sql);
        Assert.Contains("AS [Tablo]", sql);
    }

    [Fact]
    public void Metin_degeri_yalniz_metin_kolonlarda_ve_kacisli()
    {
        var t = Tablo(
            new SemaKolonu("Id", "int", false, true),
            new SemaKolonu("Ad", "nvarchar(70)", false, false));

        string? sql = VeriArayici.SorguUret(t, "O'Brien", Mssql);

        Assert.NotNull(sql);
        Assert.Contains("[Ad] = N'O''Brien'", sql); // ' ikilenir
        Assert.DoesNotContain("[Id] =", sql!);       // sayı değil — sayısal kolon taranmaz
    }

    [Fact]
    public void Uygun_kolon_yoksa_null()
    {
        var t = Tablo(new SemaKolonu("Dogum", "datetime", true, false));
        Assert.Null(VeriArayici.SorguUret(t, "abc", Mssql));
    }

    // --- Özellik eşitliği (2026-08-03): PG/MySQL çıktıları ---

    [Fact]
    public void Postgres_cikti_cift_tirnak_limit_ve_n_oneksiz()
    {
        var t = new SemaNesnesi("db", "public", "kisi", SemaNesneTuru.Tablo,
            [new SemaKolonu("id", "integer", false, true),
             new SemaKolonu("ad", "character varying(70)", false, false)], []);

        string? sql = VeriArayici.SorguUret(t, "42", Postgres);

        Assert.NotNull(sql);
        Assert.Contains("\"id\" = 42", sql);
        Assert.Contains("\"ad\" = '42'", sql);            // N öneki YOK
        Assert.Contains("FROM \"public\".\"kisi\"", sql);
        Assert.Contains("LIMIT 100;", sql);               // TOP değil, kuyruk LIMIT
        Assert.DoesNotContain("TOP", sql!);
        Assert.Contains("AS \"Tablo\"", sql);
    }

    [Fact]
    public void Mysql_cikti_backtick_limit_ve_ters_bolen_kacisli()
    {
        var t = new SemaNesnesi("db", "sirket", "kisi", SemaNesneTuru.Tablo,
            [new SemaKolonu("id", "bigint", false, true),
             new SemaKolonu("yol", "varchar(200)", false, false)], []);

        string? sql = VeriArayici.SorguUret(t, @"C:\temp", MySql);

        Assert.NotNull(sql);
        Assert.Contains(@"`yol` = 'C:\\temp'", sql);      // MySQL ters bölen ikilenir
        Assert.Contains("FROM `kisi`", sql);              // MySQL'de şema = veritabanı → yalnız tablo
        Assert.Contains("LIMIT 100;", sql);
        Assert.DoesNotContain("[", sql!);
    }

    [Fact]
    public void Oracle_cikti_fetch_first_ve_clob_haric() // madde 3 (2026-08-03)
    {
        var t = new SemaNesnesi("db", "SATIS", "MUSTERI", SemaNesneTuru.Tablo,
            [new SemaKolonu("ID", "NUMBER(10)", false, true),
             new SemaKolonu("AD", "VARCHAR2(70)", false, false),
             new SemaKolonu("NOTLAR", "CLOB", true, false)], []); // CLOB '=' kıyası ORA-00932 → taranmaz

        string? sql = VeriArayici.SorguUret(t, "42", new OracleLehcesi(new DpapiSecretProtector()));

        Assert.NotNull(sql);
        Assert.Contains("\"ID\" = 42", sql);
        Assert.Contains("\"AD\" = '42'", sql);
        Assert.DoesNotContain("NOTLAR", sql!);
        Assert.Contains("FETCH FIRST 100 ROWS ONLY;", sql);
        Assert.DoesNotContain("TOP", sql!);
        Assert.DoesNotContain("LIMIT", sql!);
    }

    // ── v23 S3 (K3 kararı): FTS'li tabloda metin araması CONTAINS'e geçer ──

    [Fact]
    public void Fts_kolonlari_tek_contains_kosulunda_digerleri_esitlikle()
    {
        var t = Tablo(
            new SemaKolonu("Id", "int", false, true),
            new SemaKolonu("Baslik", "nvarchar(200)", false, false),   // FTS'li
            new SemaKolonu("Icerik", "nvarchar(max)", true, false),    // FTS'li
            new SemaKolonu("Kod", "varchar(20)", false, false));       // FTS'siz metin

        string? sql = VeriArayici.SorguUret(t, "motor arızası", Mssql,
            ftsKolonlar: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Baslik", "Icerik" });

        Assert.NotNull(sql);
        // FTS kolonları TEK CONTAINS'te (kelime index'i; '='in bulamadığı iç geçişleri de bulur)
        Assert.Contains("CONTAINS(([Baslik], [Icerik]), N'\"motor arızası\"')", sql);
        Assert.Contains("[Kod] = N'motor arızası'", sql);  // FTS'siz metin '=' ile sürer
        Assert.DoesNotContain("[Baslik] =", sql!);          // FTS'li kolon '='e DÜŞMEZ
        Assert.DoesNotContain("[Icerik] =", sql!);
    }

    [Fact]
    public void Fts_tek_kolonda_parantezsiz_ve_kacislar_birlikte()
    {
        var t = Tablo(new SemaKolonu("Icerik", "nvarchar(max)", true, false));

        string? sql = VeriArayici.SorguUret(t, "o'reilly \"x\"", Mssql,
            ftsKolonlar: new HashSet<string> { "Icerik" });

        Assert.NotNull(sql);
        // çift tırnak FTS kaçışı ("") + tek tırnak SQL kaçışı ('') aynı anda
        Assert.Contains("CONTAINS([Icerik], N'\"o''reilly \"\"x\"\"\"')", sql);
    }

    [Fact]
    public void Fts_listesi_bos_veya_null_ise_eski_davranis_birebir()
    {
        var t = Tablo(new SemaKolonu("Ad", "nvarchar(70)", false, false));

        Assert.Equal(
            VeriArayici.SorguUret(t, "x", Mssql),
            VeriArayici.SorguUret(t, "x", Mssql, ftsKolonlar: new HashSet<string>()));
        Assert.DoesNotContain("CONTAINS", VeriArayici.SorguUret(t, "x", Mssql)!);
    }

    [Fact]
    public void Mysql_metin_tipleri_de_taranir()
    {
        var t = new SemaNesnesi("db", "sirket", "notlar", SemaNesneTuru.Tablo,
            [new SemaKolonu("icerik", "longtext", true, false),
             new SemaKolonu("sayi", "mediumint", true, false)], []);

        string? sql = VeriArayici.SorguUret(t, "7", MySql);

        Assert.NotNull(sql);
        Assert.Contains("`icerik` = '7'", sql);
        Assert.Contains("`sayi` = 7", sql);
    }
}
