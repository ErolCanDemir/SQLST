using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// V4-S5: Oracle çok-ifadeli script bölme (SQL*Plus modeli). En kritik davranış,
/// PL/SQL bloğunun İÇİNDEKİ ';' karakterlerinin ayırıcı SAYILMAMASI — naif bir
/// <c>sql.Split(';')</c> bloğu paramparça edip her parçayı ayrı ayrı gönderirdi.
/// </summary>
public class OracleBatchBolmeTests
{
    private static IReadOnlyList<SqlBatch> Bol(string sql)
        => SqlCozumleyici.BatchlereBol(sql, MotorTuru.Oracle);

    [Fact]
    public void Noktali_virgul_ifadeleri_ayirir_ve_sunucuya_gonderilmez()
    {
        IReadOnlyList<SqlBatch> b = Bol("SELECT 1 FROM dual;\nSELECT 2 FROM dual;");

        Assert.Equal(2, b.Count);
        Assert.Equal("SELECT 1 FROM dual", b[0].Metin.Trim());
        Assert.Equal("SELECT 2 FROM dual", b[1].Metin.Trim());
        Assert.All(b, x => Assert.DoesNotContain(";", x.Metin));   // ORA-00933
    }

    [Fact]
    public void Son_ifadede_noktali_virgul_olmasa_da_calisir()
    {
        IReadOnlyList<SqlBatch> b = Bol("SELECT 1 FROM dual;\nSELECT 2 FROM dual");

        Assert.Equal(2, b.Count);
        Assert.Equal("SELECT 2 FROM dual", b[1].Metin.Trim());
    }

    [Fact]
    public void Plsql_blogu_icindeki_noktali_virguller_BOLMEZ()
    {
        // Bu, dilimin can alıcı testi: blok tek parça gitmezse Oracle söz dizimi hatası verir
        const string sql = """
            BEGIN
              INSERT INTO t (x) VALUES (1);
              UPDATE t SET x = 2 WHERE x = 1;
              COMMIT;
            END;
            /
            SELECT 1 FROM dual;
            """;

        IReadOnlyList<SqlBatch> b = Bol(sql);

        Assert.Equal(2, b.Count);
        Assert.StartsWith("BEGIN", b[0].Metin.Trim());
        Assert.EndsWith("END;", b[0].Metin.Trim());          // blok kendi sonlandırıcısını korur
        Assert.Contains("INSERT INTO t", b[0].Metin);
        Assert.Contains("UPDATE t SET", b[0].Metin);
        Assert.Equal("SELECT 1 FROM dual", b[1].Metin.Trim());
    }

    [Fact]
    public void Create_or_replace_procedure_tek_parca_kalir()
    {
        const string sql = """
            CREATE OR REPLACE PROCEDURE musteri_ekle(p_ad IN VARCHAR2) AS
            BEGIN
              INSERT INTO musteri (ad) VALUES (p_ad);
              COMMIT;
            END;
            /
            """;

        SqlBatch tek = Assert.Single(Bol(sql));
        Assert.StartsWith("CREATE OR REPLACE PROCEDURE", tek.Metin.Trim());
        Assert.Contains("INSERT INTO musteri", tek.Metin);
        Assert.EndsWith("END;", tek.Metin.Trim());
    }

    [Fact]
    public void Duz_ddl_plsql_sayilmaz_noktali_virgulle_biter()
    {
        // CREATE TABLE bir PL/SQL bloğu DEĞİLDİR → ';' onu bitirir
        IReadOnlyList<SqlBatch> b = Bol("CREATE TABLE t (id NUMBER);\nSELECT 1 FROM dual;");

        Assert.Equal(2, b.Count);
        Assert.Equal("CREATE TABLE t (id NUMBER)", b[0].Metin.Trim());
    }

    [Fact]
    public void Dize_ve_yorum_icindeki_noktali_virgul_ayirici_degildir()
    {
        const string sql = """
            SELECT 'a;b' AS metin FROM dual;
            -- yorumda ; var
            /* blok
               yorumda da ; var */
            SELECT 2 FROM dual;
            """;

        IReadOnlyList<SqlBatch> b = Bol(sql);

        Assert.Equal(2, b.Count);
        Assert.Contains("'a;b'", b[0].Metin);
        Assert.Contains("SELECT 2", b[1].Metin);
    }

    [Fact]
    public void Tek_tirnak_kacisi_dizeyi_erken_bitirmez()
    {
        IReadOnlyList<SqlBatch> b = Bol("SELECT 'O''Brien; hala dize' FROM dual;\nSELECT 2 FROM dual;");

        Assert.Equal(2, b.Count);
        Assert.Contains("O''Brien; hala dize", b[0].Metin);
    }

    [Fact]
    public void Oracle_alternatif_tirnaklama_icindeki_noktali_virgul_ayirici_degildir()
    {
        // q'[...]' içinde ';' geçer — naif ayrıştırıcı burada bölerdi
        IReadOnlyList<SqlBatch> b = Bol("SELECT q'[a;b;c]' FROM dual;\nSELECT 2 FROM dual;");

        Assert.Equal(2, b.Count);
        Assert.Contains("q'[a;b;c]'", b[0].Metin);
    }

    [Fact]
    public void Yorum_icindeki_tek_slash_satiri_sonlandirmaz()
    {
        // '/*' ile başlayan blok yorumu, '/' sonlandırıcısıyla karıştırılmamalı
        const string sql = """
            BEGIN
              /* burada / var ama yorum içinde */
              NULL;
            END;
            /
            """;

        SqlBatch tek = Assert.Single(Bol(sql));
        Assert.Contains("NULL;", tek.Metin);
    }

    [Fact]
    public void Slash_satiri_sunucuya_gonderilmez()
    {
        SqlBatch tek = Assert.Single(Bol("BEGIN NULL; END;\n/\n"));
        Assert.DoesNotContain("\n/", tek.Metin);
        Assert.EndsWith("END;", tek.Metin.Trim());
    }

    [Fact]
    public void Bos_ve_yalniz_ayirici_metin_batch_uretmez()
    {
        Assert.Empty(Bol(""));
        Assert.Empty(Bol("   \n  \n"));
        Assert.Empty(Bol(";\n;\n"));
        Assert.Empty(Bol("/\n"));
    }

    [Fact]
    public void Satir_hizasi_korunur_sunucu_hata_satiri_dogru_eslesir()
    {
        // Batch metni ';' sonrasından başlar, yani ARADAKİ SATIR SONLARINI İÇERİR.
        // Bu bilinçlidir: sunucunun bildirdiği göreli satır + BaslangicSatiri - 1
        // kullanıcının editördeki gerçek satırını verir (GO bölücüsündeki kuralın aynısı).
        //   satır 1: SELECT 1 FROM dual;
        //   satır 2: (boş)
        //   satır 3: SELECT 2 FROM dual;
        IReadOnlyList<SqlBatch> b = Bol("SELECT 1 FROM dual;\n\nSELECT 2 FROM dual;");

        Assert.Equal(1, b[0].BaslangicSatiri);

        // İkinci batch satır 1'de başlar ama içinde iki satır sonu taşır
        Assert.Equal(1, b[1].BaslangicSatiri);
        int govdeninGoreliSatiri = b[1].Metin.TakeWhile(c => c is '\n' or '\r' or ' ').Count(c => c == '\n') + 1;
        Assert.Equal(3, b[1].BaslangicSatiri + govdeninGoreliSatiri - 1);   // gerçek satır: 3
    }

    [Fact]
    public void Diger_motorlarda_bolme_yapilmaz_metin_opak_kalir()
    {
        // PostgreSQL/MySQL sürücüleri çok-ifadeli metni tek komutta kabul eder;
        // MongoDB'de metin JSON'dur — T-SQL çözümleyicisine sokmak yanlış olurdu.
        foreach (MotorTuru motor in new[] { MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Mongo })
        {
            SqlBatch tek = Assert.Single(SqlCozumleyici.BatchlereBol("SELECT 1; SELECT 2;", motor));
            Assert.Equal("SELECT 1; SELECT 2;", tek.Metin);
        }

        SqlBatch json = Assert.Single(
            SqlCozumleyici.BatchlereBol("""{ "find": "musteri", "limit": 10 }""", MotorTuru.Mongo));
        Assert.Contains("\"find\"", json.Metin);
    }

    [Fact]
    public void Mssql_yolu_go_ile_bolmeye_devam_eder()
    {
        IReadOnlyList<SqlBatch> b = SqlCozumleyici.BatchlereBol("SELECT 1\nGO\nSELECT 2", MotorTuru.Mssql);
        Assert.Equal(2, b.Count);
    }
}

public class SqlCozumleyiciTests
{
    // ---- GO batch bölme (FG-3.11, 07-r2 §5) ----

    [Fact]
    public void Go_batchleri_ayirir_ve_satir_numaralarini_tasir()
    {
        const string sql = "SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3";

        IReadOnlyList<SqlBatch> b = SqlCozumleyici.BatchlereBol(sql);

        Assert.Equal(3, b.Count);
        Assert.Equal("SELECT 1", b[0].Metin.Trim());
        Assert.Equal("SELECT 2", b[1].Metin.Trim());
        Assert.Equal("SELECT 3", b[2].Metin.Trim());
        Assert.Equal(1, b[0].BaslangicSatiri);
        Assert.Equal(3, b[1].BaslangicSatiri);
        Assert.Equal(5, b[2].BaslangicSatiri);
    }

    [Fact]
    public void Go_buyuk_kucuk_duyarsiz_ve_cevresi_bosluklu_olabilir()
    {
        IReadOnlyList<SqlBatch> b = SqlCozumleyici.BatchlereBol("SELECT 1\n  go  \nSELECT 2");
        Assert.Equal(2, b.Count);
    }

    [Fact]
    public void String_ve_yorum_icindeki_go_ayirici_degildir()
    {
        // En sık yapılan hata (07-r2 §5) — naif split burada kırılır
        const string sql = """
            SELECT 'once GO sonra' AS x
            -- GO yorumda
            /* GO
               blokta */
            SELECT 2
            """;

        Assert.Single(SqlCozumleyici.BatchlereBol(sql));
    }

    [Fact]
    public void Satir_ortasindaki_go_adi_ayirici_degildir()
    {
        // "go" bir kolon/tablo adı olabilir — yalnız satırın İLK anlamlı token'ı GO ise ayırıcıdır
        Assert.Single(SqlCozumleyici.BatchlereBol("SELECT x go FROM t"));
    }

    [Fact]
    public void Go_n_tekrar_sayisini_okur()
    {
        IReadOnlyList<SqlBatch> b = SqlCozumleyici.BatchlereBol("INSERT INTO t DEFAULT VALUES\nGO 5\nSELECT 1");

        Assert.Equal(2, b.Count);
        Assert.Equal(5, b[0].Tekrar);
        Assert.Equal(1, b[1].Tekrar);
    }

    [Fact]
    public void Sondaki_go_bos_batch_uretmez()
    {
        IReadOnlyList<SqlBatch> b = SqlCozumleyici.BatchlereBol("SELECT 1\nGO\n");
        Assert.Single(b);
    }

    [Fact]
    public void Gosuz_metin_tek_batchtir()
    {
        IReadOnlyList<SqlBatch> b = SqlCozumleyici.BatchlereBol("SELECT 1;\nSELECT 2;");
        Assert.Single(b);
        Assert.Equal(1, b[0].Tekrar);
    }

    // ---- İmleçteki statement (07-r2 §5) ----

    private const string UcStatement = "SELECT 1;\r\nUPDATE t SET x = 2 WHERE id = 5;\r\nSELECT 3;";

    [Theory]
    [InlineData(0, "SELECT 1;")]                 // ilk statement'ın başı
    [InlineData(15, "UPDATE t SET x = 2 WHERE id = 5;")] // ortası
    [InlineData(60, "SELECT 3;")]                // son statement (ve ötesi)
    public void Imlec_ofsetindeki_ust_seviye_statement_bulunur(int ofset, string beklenen)
    {
        (SqlAralik? aralik, string? hata) = SqlCozumleyici.ImlectekiStatement(UcStatement, ofset);

        Assert.Null(hata);
        Assert.NotNull(aralik);
        Assert.Equal(beklenen, aralik!.Metin.Trim());
    }

    [Fact]
    public void Imlec_statementlar_arasindaysa_oncekini_secer()
    {
        // "SELECT 1;" 9 karakter; ofset 10 = aradaki \r\n bölgesi
        (SqlAralik? aralik, _) = SqlCozumleyici.ImlectekiStatement(UcStatement, 10);
        Assert.Equal("SELECT 1;", aralik!.Metin.Trim());
    }

    [Fact]
    public void Imlecteki_ic_ice_blokta_en_distaki_statement_gelir()
    {
        const string sql = "IF 1 = 1\nBEGIN\n    SELECT 42;\nEND";
        // ofset SELECT'in üzerinde — ama SSMS gibi tüm IF bloğu dönmeli
        int ofset = sql.IndexOf("42", StringComparison.Ordinal);

        (SqlAralik? aralik, _) = SqlCozumleyici.ImlectekiStatement(sql, ofset);

        Assert.StartsWith("IF 1 = 1", aralik!.Metin.Trim());
        Assert.EndsWith("END", aralik.Metin.Trim());
        Assert.Equal(1, aralik.BaslangicSatiri);
    }

    [Fact]
    public void Imlec_soz_dizimi_hatasinda_null_ve_mesaj_doner()
    {
        (SqlAralik? aralik, string? hata) = SqlCozumleyici.ImlectekiStatement("SELECT FROM WHERE", 3);

        Assert.Null(aralik);
        Assert.NotNull(hata);
        Assert.Contains("Satır", hata);
    }

    [Fact]
    public void Imlec_statement_satir_numarasi_dogru()
    {
        const string sql = "SELECT 1;\n\n\nSELECT 4;";
        (SqlAralik? aralik, _) = SqlCozumleyici.ImlectekiStatement(sql, sql.Length - 1);
        Assert.Equal(4, aralik!.BaslangicSatiri);
    }

    // ---- Tam biçimlendirme (FG-3.9) ----

    [Fact]
    public void Bicimlendir_anahtar_sozcukleri_buyutur_ve_satirlara_boler()
    {
        (string? sonuc, string? hata) = SqlCozumleyici.Bicimlendir(
            "select a.Ad, b.Tutar from Musteri a inner join Siparis b on a.Id = b.MusteriId where b.Tutar > 100 order by b.Tutar desc");

        Assert.Null(hata);
        Assert.NotNull(sonuc);
        Assert.Contains("SELECT", sonuc);
        Assert.Contains("INNER JOIN", sonuc);
        Assert.Contains("WHERE", sonuc);
        Assert.True(sonuc!.Split('\n').Length >= 4, $"satırlara bölünmeli:\n{sonuc}");
        Assert.DoesNotContain("select", sonuc); // küçük harfli anahtar sözcük kalmadı
    }

    [Fact]
    public void Bicimlendir_soz_dizimi_hatasinda_null_ve_satirli_mesaj()
    {
        (string? sonuc, string? hata) = SqlCozumleyici.Bicimlendir("SELECT * FORM t");

        Assert.Null(sonuc);
        Assert.Contains("Satır", hata);
    }

    [Fact]
    public void Bicimlendir_yorumlu_metinde_yorumlari_koruyarak_bicimlendirir() // kullanıcı isteği 2026-07-23
    {
        // ScriptDom yorumu düşürürdü → yorum varsa yorumları koruyan hafif biçimlendiriciye düşülür.
        (string? sonuc, string? hata) = SqlCozumleyici.Bicimlendir("SELECT a, b FROM t -- önemli not\nWHERE a = 1");

        Assert.Null(hata);
        Assert.NotNull(sonuc);
        Assert.Contains("-- önemli not", sonuc!, StringComparison.Ordinal); // yorum aynen korunur
        Assert.Contains("\n", sonuc!, StringComparison.Ordinal);            // yine de satırlara bölündü (FROM/WHERE)
    }

    [Fact]
    public void Bicimlendir_blok_yorumu_da_korunur()
    {
        (string? sonuc, string? hata) = SqlCozumleyici.Bicimlendir("/* başlık */ SELECT 1 FROM t");
        Assert.Null(hata);
        Assert.Contains("/* başlık */", sonuc!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bicimlendir_go_batchli_metinde_go_korunur()
    {
        (string? sonuc, string? hata) = SqlCozumleyici.Bicimlendir("select 1\ngo\nselect 2");

        Assert.Null(hata);
        Assert.Contains("GO", sonuc);
    }

    [Fact]
    public void Bicimlendir_JOIN_ve_ON_TEK_satirda_kalir()
    {
        // Kullanıcı isteği 2026-07-21: ScriptDom ON'u ayrı satıra atıyordu; join + ON tek satır olmalı.
        (string? sonuc, string? hata) = SqlCozumleyici.Bicimlendir(
            "select a.x from dbo.A a inner join dbo.B b on a.id = b.aid and a.k = b.k");

        Assert.Null(hata);
        string[] satirlar = sonuc!.Replace("\r\n", "\n").Split('\n');

        // Hiçbir satır "ON " ile BAŞLAMAZ (ON, JOIN satırına toplandı).
        Assert.DoesNotContain(satirlar, s => s.TrimStart().StartsWith("ON ", StringComparison.OrdinalIgnoreCase));
        // JOIN satırı hem ON hem de ON'un ikinci koşulunu (AND) aynı satırda taşır.
        string joinSatiri = Assert.Single(satirlar, s => s.Contains("JOIN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(" ON ", joinSatiri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" AND ", joinSatiri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bicimlendir_WHERE_AND_satirlari_JOINe_karismaz()
    {
        // ON'a ait olmayan (WHERE'in) AND'leri join satırına toplanmamalı.
        (string? sonuc, _) = SqlCozumleyici.Bicimlendir(
            "select a.x from dbo.A a inner join dbo.B b on a.id = b.aid where a.x = 1 and a.y = 2");

        string[] satirlar = sonuc!.Replace("\r\n", "\n").Split('\n');
        string joinSatiri = Assert.Single(satirlar, s => s.Contains("JOIN", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(" AND ", joinSatiri, StringComparison.OrdinalIgnoreCase); // WHERE'in AND'i join'e girmedi
    }

    // Edit modda DML'in gerçek kaynak tabloya üretilmesi (kullanıcı bulgusu 2026-07-27: sorgu başka
    // tabloyu hedeflerken UPDATE _tablo'ya gidip 0 satır/yanlış tablo ediyordu).
    [Fact]
    public void TekTabloSelectKaynagi_sema_ve_tabloyu_cozer()
    {
        SqlCozumleyici.TekTabloKaynak? k = SqlCozumleyici.TekTabloSelectKaynagi(
            "SELECT * FROM Yonetim.KullaniciRolleri where KullaniciId = 4431624");
        Assert.NotNull(k);
        Assert.Null(k.Veritabani);
        Assert.Equal("Yonetim", k.Sema);
        Assert.Equal("KullaniciRolleri", k.Ad);
    }

    [Fact]
    public void TekTabloSelectKaynagi_semasiz_dbo_varsayar_ve_uc_parcali_db_cozer()
    {
        Assert.Equal("dbo", SqlCozumleyici.TekTabloSelectKaynagi("SELECT Id FROM Musteri")!.Sema);
        SqlCozumleyici.TekTabloKaynak? uc = SqlCozumleyici.TekTabloSelectKaynagi("SELECT * FROM DemoDb.Yonetim.Kullanici");
        Assert.Equal("DemoDb", uc!.Veritabani);
        Assert.Equal("Yonetim", uc.Sema);
        Assert.Equal("Kullanici", uc.Ad);
    }

    [Theory]
    [InlineData("SELECT a.x FROM dbo.A a JOIN dbo.B b ON a.id=b.aid")]  // JOIN — güvenle düzenlenemez
    [InlineData("SELECT * FROM dbo.A, dbo.B")]                          // çoklu tablo
    [InlineData("SELECT * FROM (SELECT 1 x) t")]                        // türev tablo
    [InlineData("UPDATE dbo.A SET x=1")]                                // SELECT değil
    [InlineData("SELECT 1")]                                            // FROM yok
    [InlineData("SELECT * FROM A; SELECT * FROM B")]                    // çok ifade
    public void TekTabloSelectKaynagi_karmasik_sorguda_null(string sql)
        => Assert.Null(SqlCozumleyici.TekTabloSelectKaynagi(sql));
}
