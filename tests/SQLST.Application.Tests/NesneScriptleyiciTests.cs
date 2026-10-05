using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

public class NesneScriptleyiciTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static SemaNesnesi Nesne(
        SemaNesneTuru tur = SemaNesneTuru.Tablo,
        string sema = "dbo",
        string ad = "Musteri",
        IReadOnlyList<SemaKolonu>? kolonlar = null,
        IReadOnlyList<SemaParametresi>? parametreler = null)
        => new("DemoDb", sema, ad, tur, kolonlar ?? [], parametreler ?? []);

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.spX AS SELECT 1", "ALTER PROCEDURE dbo.spX AS SELECT 1")]
    [InlineData("create proc dbo.spX AS SELECT 1", "ALTER proc dbo.spX AS SELECT 1")]
    [InlineData("CREATE   PROC dbo.spX", "ALTER   PROC dbo.spX")]
    [InlineData("\r\n\tCREATE VIEW v AS SELECT 1", "\r\n\tALTER VIEW v AS SELECT 1")]
    public void Alter_ilk_anahtar_sozcugu_cevirir(string tanim, string beklenen)
        => Assert.Equal(beklenen, NesneScriptleyici.AlterEDonustur(tanim));

    [Fact]
    public void Alter_bastaki_satir_yorumlarini_atlar()
    {
        const string tanim = "-- açıklama: burada CREATE yazıyor ama yorumda\r\nCREATE PROCEDURE dbo.spX AS SELECT 1";
        string sonuc = NesneScriptleyici.AlterEDonustur(tanim);

        Assert.Contains("-- açıklama: burada CREATE yazıyor ama yorumda", sonuc); // yorum korunur
        Assert.Contains("ALTER PROCEDURE dbo.spX", sonuc);
    }

    [Fact]
    public void Alter_ic_ice_blok_yorumlari_atlar()
    {
        const string tanim = "/* dış /* iç */ hâlâ yorum */ CREATE FUNCTION dbo.fnX() RETURNS INT AS BEGIN RETURN 1 END";
        Assert.StartsWith("/* dış /* iç */ hâlâ yorum */ ALTER FUNCTION", NesneScriptleyici.AlterEDonustur(tanim));
    }

    [Theory]
    [InlineData("CREATEX PROC dbo.spX")]          // anahtar sözcük değil
    [InlineData("SELECT 1")]                       // CREATE yok
    [InlineData("-- yalnız yorum")]                // anlamlı token yok
    [InlineData("")]
    public void Alter_uygun_olmayan_govdeye_dokunmaz(string tanim)
        => Assert.Equal(tanim, NesneScriptleyici.AlterEDonustur(tanim));

    [Fact]
    public void Select_scripti_adi_koseli_parantezler()
    {
        Assert.Equal("SELECT TOP 200 * FROM [satis].[Sipariş];",
            NesneScriptleyici.SelectScripti(Nesne(sema: "satis", ad: "Sipariş"), 200));

        // ']' içeren ad kaçırılır (QUOTENAME mantığı)
        Assert.Equal("SELECT TOP 10 * FROM [dbo].[Garip]]Ad];",
            NesneScriptleyici.SelectScripti(Nesne(ad: "Garip]Ad"), 10));
    }

    [Fact]
    public void Exec_iskeleti_parametresiz_sp_icin_tek_satir()
    {
        Assert.Equal("EXEC [dbo].[spHepsi];",
            NesneScriptleyici.ExecIskeleti(Nesne(SemaNesneTuru.StoredProcedure, ad: "spHepsi")));
    }

    [Fact]
    public void Exec_iskeleti_parametreleri_null_ve_tip_yorumuyla_yazar()
    {
        SemaNesnesi sp = Nesne(SemaNesneTuru.StoredProcedure, "satis", "spGetSiparisler", parametreler:
        [
            new("@MusteriId", "int", CikisMi: false),
            new("@Baslangic", "datetime2(7)", CikisMi: false),
            new("@Toplam", "decimal(18,2)", CikisMi: true),
        ]);

        string script = NesneScriptleyici.ExecIskeleti(sp);

        Assert.Equal(
            """
            EXEC [satis].[spGetSiparisler]
                 @MusteriId = NULL,   -- int
                 @Baslangic = NULL,   -- datetime2(7)
                 @Toplam    = NULL OUTPUT;   -- decimal(18,2)
            """.ReplaceLineEndings(), script.ReplaceLineEndings());
    }

    [Fact]
    public void Kolon_listesi_virgullu_yazilir()
    {
        SemaNesnesi tablo = Nesne(kolonlar:
        [
            new("Id", "int", false, true),
            new("Ad", "nvarchar(50)", false, false),
            new("Bakiye", "decimal(18,2)", true, false),
        ]);

        Assert.Equal("Id, Ad, Bakiye", NesneScriptleyici.KolonListesi(tablo));
        Assert.Equal("", NesneScriptleyici.KolonListesi(Nesne()));
    }

    // ── #11 Kolay INSERT (örnek INSERT, 2026-07-29) ──────────────────────────

    private static DuzenlemeMetasi InsertMeta() => new("DemoDb", "dbo", "Musteri",
    [
        new DuzenlemeKolonu("Id", "int", "int", false, PkMi: true, IdentityMi: true, ComputedMi: false, RowversionMi: false, KiyasGuvenliMi: true),
        new DuzenlemeKolonu("Ad", "nvarchar", "nvarchar(50)", false, false, false, false, false, true),
        new DuzenlemeKolonu("Yas", "int", "int", true, false, false, false, false, true),
        new DuzenlemeKolonu("Kayit", "datetime", "datetime", false, false, false, false, false, true),
        new DuzenlemeKolonu("Guid", "uniqueidentifier", "uniqueidentifier", false, false, false, false, false, true),
        new DuzenlemeKolonu("Hesap", "int", "int", true, false, false, ComputedMi: true, false, true),
        new DuzenlemeKolonu("Surum", "rowversion", "rowversion", false, false, false, false, RowversionMi: true, false),
    ]);

    [Fact]
    public void Insert_sablonu_identity_computed_rowversion_atlar() // #11
    {
        string s = NesneScriptleyici.InsertSablonu(InsertMeta(), 3, Mssql);

        Assert.Contains("INSERT INTO [dbo].[Musteri] ([Ad], [Yas], [Kayit], [Guid])", s);
        Assert.DoesNotContain("[Id]", s);    // identity — sunucu malı
        Assert.DoesNotContain("[Hesap]", s); // computed
        Assert.DoesNotContain("[Surum]", s); // rowversion
    }

    [Fact]
    public void Insert_sablonu_tip_farkinda_deger_uretir() // #11
    {
        string s = NesneScriptleyici.InsertSablonu(InsertMeta(), 1, Mssql);
        Assert.Contains("('', 0, GETDATE(), NEWID())", s); // metin '' · sayı 0 · tarih GETDATE() · guid NEWID()
    }

    [Fact]
    public void Insert_sablonu_N_hizali_satir_uretir() // #11 — çoklu insert kolaylığı
    {
        string s = NesneScriptleyici.InsertSablonu(InsertMeta(), 3, Mssql);

        Assert.Equal(3, s.Split("('', 0, GETDATE(), NEWID())").Length - 1); // 3 VALUES satırı
        Assert.Contains("VALUES", s);
        Assert.EndsWith(";", s);
    }

    [Fact]
    public void Insert_sablonu_yazilabilir_kolon_yoksa_yorum() // #11
    {
        var meta = new DuzenlemeMetasi("DemoDb", "dbo", "T",
            [new DuzenlemeKolonu("Id", "int", "int", false, true, IdentityMi: true, false, false, true)]);
        Assert.Contains("INSERT'e girebilecek kolon yok", NesneScriptleyici.InsertSablonu(meta, 3, Mssql));
    }

    // ── v20-S21 saha m.29: UPDATE örneği (INSERT'in eşi) ─────────────────────

    [Fact]
    public void Update_sablonu_set_ornek_degerler_where_pk() // m.29
    {
        string s = NesneScriptleyici.UpdateSablonu(InsertMeta(), Mssql);

        Assert.Contains("UPDATE [dbo].[Musteri]", s);
        Assert.Contains("[Ad] = ''", s);
        Assert.Contains("[Kayit] = GETDATE()", s);
        Assert.Contains("WHERE [Id] = 0;", s);   // PK identity olsa da WHERE anahtarıdır
        Assert.DoesNotContain("[Hesap] =", s);   // computed SET'e girmez
        Assert.DoesNotContain("[Surum] =", s);   // rowversion SET'e girmez
    }

    [Fact]
    public void Update_sablonu_pk_yoksa_guvenli_where() // m.29 — filtresiz kaza şablondan çıkmaz
    {
        var meta = new DuzenlemeMetasi("DemoDb", "dbo", "T",
            [new DuzenlemeKolonu("Ad", "nvarchar", "nvarchar(50)", true, false, false, false, false, true)]);
        string s = NesneScriptleyici.UpdateSablonu(meta, Mssql);

        Assert.Contains("WHERE 1 = 0", s);
        Assert.Contains("PK yok", s);
    }

    // --- Özellik eşitliği (2026-08-03): INSERT örneği PG/MySQL ---

    [Fact]
    public void Insert_sablonu_postgres_fonksiyonlari_ve_cift_tirnak() // now()/gen_random_uuid()/CURRENT_DATE
    {
        var meta = new DuzenlemeMetasi("db", "public", "musteri",
        [
            new DuzenlemeKolonu("ad", "character varying", "varchar(50)", false, false, false, false, false, true),
            new DuzenlemeKolonu("dogum", "date", "date", true, false, false, false, false, true),
            new DuzenlemeKolonu("kayit", "timestamptz", "timestamptz", false, false, false, false, false, true),
            new DuzenlemeKolonu("no", "uuid", "uuid", false, false, false, false, false, true),
            new DuzenlemeKolonu("aktif", "boolean", "boolean", false, false, false, false, false, true),
        ]);

        string s = NesneScriptleyici.InsertSablonu(meta, 1, new PostgresLehcesi(new DpapiSecretProtector()));

        Assert.Contains("INSERT INTO \"public\".\"musteri\" (\"ad\", \"dogum\", \"kayit\", \"no\", \"aktif\")", s);
        Assert.Contains("('', CURRENT_DATE, now(), gen_random_uuid(), FALSE)", s);
    }

    [Fact]
    public void Insert_sablonu_oracle_sysdate_ve_cift_tirnak() // madde 3: SYSDATE/SYSTIMESTAMP/NUMBER
    {
        var meta = new DuzenlemeMetasi("db", "SATIS", "MUSTERI",
        [
            new DuzenlemeKolonu("AD", "VARCHAR2", "VARCHAR2(50)", false, false, false, false, false, true),
            new DuzenlemeKolonu("TUTAR", "NUMBER", "NUMBER(18,2)", true, false, false, false, false, true),
            new DuzenlemeKolonu("KAYIT", "DATE", "DATE", false, false, false, false, false, true),
            new DuzenlemeKolonu("ZAMAN", "TIMESTAMP(6)", "TIMESTAMP(6)", true, false, false, false, false, true),
        ]);

        string s = NesneScriptleyici.InsertSablonu(meta, 1, new OracleLehcesi(new DpapiSecretProtector()));

        Assert.Contains("INSERT INTO \"SATIS\".\"MUSTERI\" (\"AD\", \"TUTAR\", \"KAYIT\", \"ZAMAN\")", s);
        Assert.Contains("('', 0, SYSDATE, SYSTIMESTAMP)", s);
    }

    [Fact]
    public void Insert_sablonu_mysql_fonksiyonlari_ve_backtick() // NOW()/CURDATE()/UUID(), şemasız ad
    {
        var meta = new DuzenlemeMetasi("db", "sirket", "musteri",
        [
            new DuzenlemeKolonu("ad", "varchar", "varchar(50)", false, false, false, false, false, true),
            new DuzenlemeKolonu("dogum", "date", "date", true, false, false, false, false, true),
            new DuzenlemeKolonu("kayit", "datetime", "datetime", false, false, false, false, false, true),
            new DuzenlemeKolonu("saat", "time", "time", true, false, false, false, false, true),
        ]);

        string s = NesneScriptleyici.InsertSablonu(meta, 2, new MySqlLehcesi(new DpapiSecretProtector()));

        Assert.Contains("INSERT INTO `musteri` (`ad`, `dogum`, `kayit`, `saat`)", s); // MySQL'de şema öneki yok
        Assert.Equal(2, s.Split("('', CURDATE(), NOW(), CURTIME())").Length - 1);
    }
}
