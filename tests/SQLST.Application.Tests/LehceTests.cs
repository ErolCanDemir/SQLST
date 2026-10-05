using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Lehçe sözleşmesi testleri (V3-S1). Canlı sunucu gerektirmez — tırnaklama, tip biçimleme,
/// istisna yorumu, kanonik şema-sorgu şekli ve motor yeteneklerini doğrular.
/// </summary>
public class LehceTests
{
    private static readonly MssqlLehcesi Mssql = new(new DpapiSecretProtector());
    private static readonly PostgresLehcesi Postgres = new(new DpapiSecretProtector());

    [Fact]
    public void MotorId_dogru()
    {
        Assert.Equal("mssql", Mssql.MotorId);
        Assert.Equal("postgres", Postgres.MotorId);
    }

    [Theory]
    [InlineData("Musteri", "[Musteri]")]
    [InlineData("kö]tü", "[kö]]tü]")] // ']' ikilenir
    public void Mssql_tirnaklama_koseli(string ad, string beklenen)
        => Assert.Equal(beklenen, Mssql.TirnaklaTanimlayici(ad));

    [Theory]
    [InlineData("musteri", "\"musteri\"")]
    [InlineData("kö\"tü", "\"kö\"\"tü\"")] // '\"' ikilenir
    public void Postgres_tirnaklama_cift_tirnak(string ad, string beklenen)
        => Assert.Equal(beklenen, Postgres.TirnaklaTanimlayici(ad));

    [Fact]
    public void Veritabani_olustur_sql_motoruna_gore() // ＋ Yeni veritabanı (2026-07-31)
    {
        Assert.Equal("CREATE DATABASE [Yeni Db];", Mssql.VeritabaniOlusturSql("Yeni Db"));
        Assert.Equal("CREATE DATABASE \"yeni\";", Postgres.VeritabaniOlusturSql("yeni"));
        Assert.Equal("CREATE DATABASE `yeni`;",
            new MySqlLehcesi(new DpapiSecretProtector()).VeritabaniOlusturSql("yeni"));
        // Oracle'da "database" örnek düzeyidir — özellik null'la GİZLENİR (çoklu-motor kuralı).
        // (default interface metodu — somut tipten değil ILehce'den erişilir)
        Assert.Null(((ILehce)new OracleLehcesi(new DpapiSecretProtector())).VeritabaniOlusturSql("yeni"));
    }

    [Fact]
    public void Mssql_veritabani_acik_baglantida_degisir_postgres_degismez()
    {
        Assert.True(Mssql.AcikBaglantidaVeritabaniDegisir);
        Assert.False(Postgres.AcikBaglantidaVeritabaniDegisir);
        // Postgres'te USE çağrılırsa net hata (oturum bu dala hiç girmez)
        Assert.Throws<NotSupportedException>(() => Postgres.VeritabaniSecSql("db"));
    }

    [Fact]
    public void Postgres_edit_modu_v4s1_ile_desteklenir()
    {
        // V4-S1'e kadar NotSupportedException atardı; artık pg_attribute meta sorgusu üretir.
        var tablo = new SemaNesnesi("db", "public", "musteri", SemaNesneTuru.Tablo, [], []);

        Assert.True(Postgres.DuzenlemeDestekler);
        string sorgu = Postgres.DuzenlemeMetaSorgusu(tablo);

        Assert.Contains("pg_attribute", sorgu);
        Assert.Contains("'public'", sorgu);
        Assert.Contains("'musteri'", sorgu);
        Assert.Contains("attidentity", sorgu);   // identity + serial ayrımı
        Assert.Contains("attgenerated", sorgu);  // üretilmiş kolon
        Assert.Contains("indisprimary", sorgu);  // PK
    }

    [Theory]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("numeric", 0, 18, 2, "numeric(18,2)")]
    [InlineData("int4", 0, 0, 0, "int4")]
    [InlineData("bool", 0, 0, 0, "bool")]
    public void Postgres_tip_bicimleme(string tip, int uzunluk, int kesinlik, int olcek, string beklenen)
        => Assert.Equal(beklenen, Postgres.TipYaz(tip, uzunluk, kesinlik, olcek));

    [Fact]
    public void Postgres_tur_cevirme()
    {
        Assert.Equal(SemaNesneTuru.Tablo, Postgres.TurCevir("U"));
        Assert.Equal(SemaNesneTuru.View, Postgres.TurCevir("V"));
        Assert.Equal(SemaNesneTuru.StoredProcedure, Postgres.TurCevir("P"));
        Assert.Equal(SemaNesneTuru.Fonksiyon, Postgres.TurCevir("FN"));
    }

    [Fact]
    public void Postgres_tanim_view_ve_fonksiyon_farkli_sorar()
    {
        var view = new SemaNesnesi("db", "public", "vw", SemaNesneTuru.View, [], []);
        var fn = new SemaNesnesi("db", "public", "fn", SemaNesneTuru.Fonksiyon, [], []);
        Assert.Contains("pg_get_viewdef", Postgres.TanimSorgusu(view));
        Assert.Contains("pg_get_functiondef", Postgres.TanimSorgusu(fn));
    }

    [Fact]
    public void Postgres_tanim_adi_tek_tirnak_kacirir()
    {
        var fn = new SemaNesnesi("db", "pub'lic", "f'n", SemaNesneTuru.Fonksiyon, [], []);
        string sql = Postgres.TanimSorgusu(fn);
        Assert.Contains("pub''lic", sql);
        Assert.Contains("f''n", sql);
    }

    [Fact]
    public void Mssql_hata_yorumu_sqlexception_disi_de_mesaji_korur()
    {
        SqlHata h = Mssql.HataYorumla(new InvalidOperationException("bağlantı yok"));
        Assert.Equal("bağlantı yok", h.Mesaj);
        Assert.Equal(0, h.Numara);
    }

    [Fact]
    public void Postgres_semasorgusu_dort_sonuc_kumesi_isterip_kanonik_kolonlar()
    {
        string sql = Assert.Single(Postgres.SemaSorgulari); // PG tek batch — tek round-trip
        // Dört ';' ile ayrılmış ifade (DB adı + nesneler + kolonlar + parametreler)
        Assert.Equal(4, sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length);
        Assert.Contains("current_database()", sql);
        Assert.Contains("information_schema.columns", sql);
        Assert.Contains("information_schema.routines", sql);
    }

    /// <summary>Postgres istisnası SQLSTATE'i mesaja katar, Numara'yı 0 bırakır (SqlHata modeline sıkıştırır).</summary>
    [Fact]
    public void Postgres_hata_yorumu_sqlstate_mesaja_katar()
    {
        // PostgresException doğrudan kurulamaz (internal ctor); istisna-dışı yolu doğrula:
        SqlHata h = Postgres.HataYorumla(new InvalidOperationException("host yok"));
        Assert.Equal("host yok", h.Mesaj);
        Assert.Equal(0, h.Numara);
    }

    // ── MySQL / MariaDB (V3-S1 Faz 2) ────────────────────────────────────────

    private static readonly MySqlLehcesi MySql = new(new DpapiSecretProtector());

    [Theory]
    [InlineData("musteri", "`musteri`")]
    [InlineData("kö`tü", "`kö``tü`")] // '`' ikilenir
    public void Mysql_tirnaklama_backtick(string ad, string beklenen)
        => Assert.Equal(beklenen, MySql.TirnaklaTanimlayici(ad));

    [Fact]
    public void Mysql_use_calisir_ve_backtickli_uretilir()
    {
        Assert.True(MySql.AcikBaglantidaVeritabaniDegisir); // şema=veritabanı, USE var
        Assert.Equal("USE `demo`;", MySql.VeritabaniSecSql("demo"));
    }

    [Fact]
    public void Mysql_semasorgusu_tek_batch_truthy_kanonik()
    {
        string sql = Assert.Single(MySql.SemaSorgulari);
        Assert.Contains("DATABASE()", sql);
        Assert.Contains("information_schema.COLUMNS", sql);
        Assert.Contains("COLUMN_KEY = 'PRI'", sql);       // PK tespiti
        Assert.DoesNotContain("THEN true", sql);          // boolean yok — truthy 1/0
    }

    [Theory]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("decimal", 0, 18, 2, "decimal(18,2)")]
    [InlineData("int", 0, 10, 0, "int")]
    public void Mysql_tip_bicimleme(string tip, int uzunluk, int kesinlik, int olcek, string beklenen)
        => Assert.Equal(beklenen, MySql.TipYaz(tip, uzunluk, kesinlik, olcek));

    [Fact]
    public void Mysql_tanim_view_ve_routine_tek_kolon_doner()
    {
        var view = new SemaNesnesi("demo", "demo", "vw", SemaNesneTuru.View, [], []);
        var sp = new SemaNesnesi("demo", "demo", "sp", SemaNesneTuru.StoredProcedure, [], []);
        Assert.Contains("VIEW_DEFINITION", MySql.TanimSorgusu(view));
        Assert.Contains("ROUTINE_DEFINITION", MySql.TanimSorgusu(sp));
        Assert.DoesNotContain("SHOW CREATE", MySql.TanimSorgusu(sp)); // çok kolonlu — kanonik dışı
    }

    // ── Oracle (V3-S1 Faz 2 — canlı sunucu yok; SQL şekli düzeyinde) ─────────

    private static readonly OracleLehcesi Oracle = new(new DpapiSecretProtector());

    [Theory]
    [InlineData("MUSTERI", "\"MUSTERI\"")]
    [InlineData("kö\"tü", "\"kö\"\"tü\"")]
    public void Oracle_tirnaklama_cift_tirnak(string ad, string beklenen)
        => Assert.Equal(beklenen, Oracle.TirnaklaTanimlayici(ad));

    [Fact]
    public void Oracle_sema_gecisi_alter_session_noktali_virgulsuz()
    {
        Assert.True(Oracle.AcikBaglantidaVeritabaniDegisir);
        string sql = Oracle.VeritabaniSecSql("HR");
        Assert.Equal("ALTER SESSION SET CURRENT_SCHEMA = \"HR\"", sql);
        Assert.DoesNotContain(";", sql); // Oracle düz SQL'de trailing ';' kabul etmez
    }

    [Fact]
    public void Oracle_semasorgulari_dort_ayri_sorgu_noktali_virgulsuz()
    {
        IReadOnlyList<string> sorgular = Oracle.SemaSorgulari;
        Assert.Equal(4, sorgular.Count); // tek komut = tek SELECT → 4 ayrı round-trip
        Assert.All(sorgular, s => Assert.DoesNotContain(";", s));
        Assert.Contains("CURRENT_SCHEMA", sorgular[0]);
        Assert.Contains("all_objects", sorgular[1]);
        Assert.Contains("all_tab_columns", sorgular[2]);
        Assert.Contains("all_arguments", sorgular[3]);
    }

    [Fact]
    public void Oracle_tanim_view_long_sp_all_source_satirlari()
    {
        var view = new SemaNesnesi("HR", "HR", "VW", SemaNesneTuru.View, [], []);
        var sp = new SemaNesnesi("HR", "hr", "SP", SemaNesneTuru.StoredProcedure, [], []);
        Assert.Contains("all_views", Oracle.TanimSorgusu(view));
        string spSql = Oracle.TanimSorgusu(sp);
        Assert.Contains("all_source", spSql);
        Assert.Contains("ORDER BY line", spSql);  // okuyucu satırları birleştirir
        Assert.Contains("'HR'", spSql);           // owner UPPERCASE katlanır
    }

    [Theory]
    [InlineData("VARCHAR2", 50, 0, 0, "VARCHAR2(50)")]
    [InlineData("NUMBER", 0, 18, 2, "NUMBER(18,2)")]
    [InlineData("DATE", 7, 0, 0, "DATE")]
    public void Oracle_tip_bicimleme(string tip, int uzunluk, int kesinlik, int olcek, string beklenen)
        => Assert.Equal(beklenen, Oracle.TipYaz(tip, uzunluk, kesinlik, olcek));

    // ── Motor seçimi (V3-S1 UI dilimi) ───────────────────────────────────────

    [Fact]
    public void Saglayici_motora_gore_dogru_lehceyi_verir()
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        Assert.IsType<MssqlLehcesi>(saglayici.Getir(MotorTuru.Mssql));
        Assert.IsType<PostgresLehcesi>(saglayici.Getir(MotorTuru.Postgres));
        Assert.IsType<MySqlLehcesi>(saglayici.Getir(MotorTuru.MySql));
        Assert.IsType<OracleLehcesi>(saglayici.Getir(MotorTuru.Oracle));
    }

    [Fact]
    public void Dort_motor_da_index_sorgusu_uretir_pk_haric() // v7 borç kapanışı: index farkı 4 motor
    {
        // v7'de yalnız MSSQL index okurdu; artık dördü de okur (şema farkında index satırı üretilir).
        Assert.All(new ILehce[] { Mssql, Postgres, MySql, Oracle },
            l => Assert.False(string.IsNullOrWhiteSpace(l.IndeksSorgusu)));

        Assert.Contains("sys.indexes", Mssql.IndeksSorgusu);
        Assert.Contains("is_primary_key = 0", Mssql.IndeksSorgusu);
        Assert.Contains("pg_index", Postgres.IndeksSorgusu);
        Assert.Contains("indisprimary = false", Postgres.IndeksSorgusu);
        Assert.Contains("information_schema.STATISTICS", MySql.IndeksSorgusu);
        Assert.Contains("<> 'PRIMARY'", MySql.IndeksSorgusu);
        Assert.Contains("all_indexes", Oracle.IndeksSorgusu);
        Assert.DoesNotContain(";", Oracle.IndeksSorgusu); // Oracle düz SQL'de trailing ';' yok

        // STRING_AGG/LISTAGG KULLANILMAZ (eski sunucu dersi) — dördünde de satır-satır döner
        Assert.All(new[] { Mssql.IndeksSorgusu, Postgres.IndeksSorgusu, MySql.IndeksSorgusu, Oracle.IndeksSorgusu },
            s =>
            {
                Assert.DoesNotContain("STRING_AGG", s);
                Assert.DoesNotContain("LISTAGG", s);
            });
    }

    [Fact]
    public void Saglayici_kayitsiz_motorda_net_hata_firlatir()
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        Assert.Throws<NotSupportedException>(() => saglayici.Getir((MotorTuru)99));
    }

    [Fact]
    public async Task Profil_json_geriye_uyum_motor_alani_yoksa_mssql()
    {
        string dosya = Path.Combine(Path.GetTempPath(), $"sqlst-profil-{Guid.NewGuid():N}.json");
        try
        {
            // v2 biçimi: Motor alanı hiç yok — eski kullanıcı dosyası simülasyonu
            await File.WriteAllTextAsync(dosya, """
                [{ "Id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "Ad": "eski", "Sunucu": "srv" }]
                """);
            IReadOnlyList<ConnectionProfile> profiller = await new JsonProfileStore(dosya).GetAllAsync();

            ConnectionProfile p = Assert.Single(profiller);
            Assert.Equal(MotorTuru.Mssql, p.Motor); // varsayılan: mevcut davranış korunur
        }
        finally
        {
            File.Delete(dosya);
        }
    }

    [Fact]
    public async Task Profil_postgres_motoru_kaydedilir_ve_geri_okunur()
    {
        string dosya = Path.Combine(Path.GetTempPath(), $"sqlst-profil-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonProfileStore(dosya);
            var profil = new ConnectionProfile { Ad = "pg", Sunucu = "localhost:5433", Motor = MotorTuru.Postgres };
            await store.SaveAsync(profil);

            ConnectionProfile geri = Assert.Single(await new JsonProfileStore(dosya).GetAllAsync());
            Assert.Equal(MotorTuru.Postgres, geri.Motor);
            Assert.Equal("localhost:5433", geri.Sunucu);
        }
        finally
        {
            File.Delete(dosya);
        }
    }
}
