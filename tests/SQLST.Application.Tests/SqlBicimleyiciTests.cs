using SQLST.Application;

namespace SQLST.Application.Tests;

public class SqlBicimleyiciTests
{
    [Fact]
    public void Tek_satirlik_sp_govdesi_anahtar_sozcuklerden_bolunur()
    {
        const string tanim = "ALTER PROCEDURE satis.spGetSiparisler @MusteriId INT AS SELECT * FROM satis.Siparis WHERE MusteriId = @MusteriId";

        string sonuc = SqlBicimleyici.Bicimlendir(tanim);

        Assert.Equal(
            """
            ALTER PROCEDURE satis.spGetSiparisler @MusteriId INT AS
            SELECT *
            FROM satis.Siparis
            WHERE MusteriId = @MusteriId
            """.ReplaceLineEndings().Replace(" \r\n", "\r\n").Replace(" \n", "\n"),
            sonuc.ReplaceLineEndings());
    }

    [Fact]
    public void Begin_end_govdesi_girintilenir()
    {
        const string tanim = "ALTER PROCEDURE dbo.spX AS BEGIN DECLARE @a INT SELECT @a = 1 END";

        string sonuc = SqlBicimleyici.Bicimlendir(tanim);
        string[] satirlar = sonuc.ReplaceLineEndings("\n").Split('\n');

        Assert.Contains(satirlar, s => s == "BEGIN");
        Assert.Contains(satirlar, s => s.StartsWith("    DECLARE"));
        Assert.Contains(satirlar, s => s.StartsWith("    SELECT"));
        Assert.Contains(satirlar, s => s == "END");
    }

    [Fact]
    public void Dize_ve_koseli_ad_icindeki_anahtar_sozcukler_bolunmez()
    {
        const string tanim = "SELECT 'metin icinde FROM ve WHERE var' AS [Kolon FROM Adi] FROM dbo.T";

        string sonuc = SqlBicimleyici.Bicimlendir(tanim);

        Assert.Contains("'metin icinde FROM ve WHERE var'", sonuc);   // dize bozulmadı
        Assert.Contains("[Kolon FROM Adi]", sonuc);                    // köşeli ad bozulmadı
        Assert.Equal(2, sonuc.ReplaceLineEndings("\n").Split('\n').Length); // yalnız gerçek FROM böldü
    }

    [Fact]
    public void Yorumlar_korunur_ve_icindekiler_bolmez()
    {
        const string tanim = "-- burada SELECT ve FROM geciyor\r\nSELECT 1 /* blok FROM yorumu */ FROM dbo.T";

        string sonuc = SqlBicimleyici.Bicimlendir(tanim);

        Assert.Contains("-- burada SELECT ve FROM geciyor", sonuc);
        Assert.Contains("/* blok FROM yorumu */", sonuc);
        Assert.Contains("FROM dbo.T", sonuc);
    }

    [Fact]
    public void Dizedeki_ikilenmis_tirnak_yutulmaz()
    {
        const string tanim = "SELECT 'O''Brien FROM x' FROM dbo.T";
        string sonuc = SqlBicimleyici.Bicimlendir(tanim);

        Assert.Contains("'O''Brien FROM x'", sonuc);
        Assert.Equal(2, sonuc.ReplaceLineEndings("\n").Split('\n').Length);
    }

    [Fact]
    public void Cok_kelimeli_anahtarlar_tek_parca_kalir()
    {
        string sonuc = SqlBicimleyici.Bicimlendir(
            "SELECT * FROM a INNER JOIN b ON a.id=b.id LEFT OUTER JOIN c ON c.id=a.id GROUP BY a.x ORDER BY a.y");

        string[] satirlar = sonuc.ReplaceLineEndings("\n").Split('\n');
        Assert.Contains(satirlar, s => s.StartsWith("INNER JOIN b ON"));        // JOIN tek satır, ON bölünmedi
        Assert.Contains(satirlar, s => s.StartsWith("LEFT OUTER JOIN c ON"));
        Assert.Contains(satirlar, s => s.StartsWith("GROUP BY"));
        Assert.Contains(satirlar, s => s.StartsWith("ORDER BY"));
        Assert.DoesNotContain(satirlar, s => s.Trim() == "ON" || s.Trim() == "BY");
    }

    [Fact]
    public void Takma_ad_AS_bolunmez()
    {
        string sonuc = SqlBicimleyici.Bicimlendir("SELECT name AS VeritabaniAdi FROM sys.databases");
        Assert.Contains("name AS VeritabaniAdi", sonuc);
    }

    [Theory]
    [InlineData("SELECT 1", false)]                                     // bölünecek yer yok
    [InlineData("SELECT * FROM t", false)]                              // 2 satır → kazanç yok
    [InlineData("SELECT 1\nFROM t\nWHERE x = 1\nORDER BY y", false)]     // zaten biçimli → dokunma
    [InlineData("", false)]
    public void Kisa_veya_bicimli_govde_bicimlendirilmez(string sql, bool beklenen)
        => Assert.Equal(beklenen, SqlBicimleyici.BicimlendirmeyeDegerMi(sql));

    [Fact]
    public void Tek_satirlik_gercek_sp_bicimlendirmeye_degerdir()
    {
        // 112 karakter — eski uzunluk eşiği (120) bunu kaçırıyordu
        const string sp = "ALTER PROCEDURE satis.spGetSiparisler @MusteriId INT AS SELECT * FROM satis.Siparis WHERE MusteriId = @MusteriId";
        Assert.True(sp.Length < 120);
        Assert.True(SqlBicimleyici.BicimlendirmeyeDegerMi(sp));
    }

    // ---- SELECT listesi alt alta (kullanıcı isteği 2026-07-23) ----

    [Fact]
    public void Select_listesi_kolonlari_alt_alta_bolunur()
    {
        string sonuc = SqlBicimleyici.Bicimlendir("SELECT a, b, c FROM t");
        string[] satirlar = sonuc.ReplaceLineEndings("\n").Split('\n');

        Assert.Equal("SELECT a,", satirlar[0]);
        Assert.Equal("    b,", satirlar[1]);
        Assert.Equal("    c", satirlar[2]);
        Assert.Equal("FROM t", satirlar[3]);
    }

    [Fact]
    public void Fonksiyon_ve_over_icindeki_virguller_bolunmez()
    {
        // CONVERT(x,y,z) ve OVER(PARTITION BY a ORDER BY b) argüman virgülleri AYNI satırda kalır;
        // yalnız kolonlar arası (parantez dışı) virgüller böler — test4 ekran görüntüsü senaryosu.
        string sonuc = SqlBicimleyici.Bicimlendir(
            "SELECT CONVERT(nvarchar(30), k.Tarih, 104) AS KararTarihi, ROW_NUMBER() OVER (PARTITION BY f.Id ORDER BY t.Id DESC) AS rn FROM t");
        string[] satirlar = sonuc.ReplaceLineEndings("\n").Split('\n');

        Assert.Contains("CONVERT(nvarchar(30), k.Tarih, 104) AS KararTarihi,", satirlar[0]);
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY f.Id", satirlar[1]); // ikinci kolon alt satırda
        Assert.StartsWith("    ", satirlar[1]); // kolon girintisi
    }

    [Fact]
    public void In_listesi_ve_update_set_virgulleri_bolunmez()
    {
        // WHERE'deki IN(...) virgülleri parantez içindedir; SET listesi SELECT değildir — ikisi de bölünmez.
        string kosul = SqlBicimleyici.Bicimlendir("SELECT a FROM t WHERE x IN (1, 2, 3)");
        Assert.Contains("IN (1, 2, 3)", kosul);

        string guncelle = SqlBicimleyici.Bicimlendir("UPDATE t SET a = 1, b = 2 WHERE id = 5");
        Assert.Contains("SET a = 1, b = 2", guncelle);
    }

    [Fact]
    public void Over_icindeki_order_by_ve_partition_bolunmez()
    {
        // İnceleme bulgusu (2026-07-23, canlı SP kanıtı): OVER(...) içindeki ORDER BY satır
        // kırıyordu. Parantez içi anahtar sözcükler (alt sorgu SELECT'i hariç) olduğu gibi kalır.
        string sonuc = SqlBicimleyici.Bicimlendir(
            "SELECT a, ROW_NUMBER() OVER (PARTITION BY x ORDER BY y DESC) AS rn FROM t");

        Assert.Contains("OVER (PARTITION BY x ORDER BY y DESC) AS rn", sonuc);
    }

    [Fact]
    public void Case_ifadesi_select_listesini_bozmaz()
    {
        // İnceleme bulgusu (2026-07-23): CASE'in ELSE/END'i blok anahtar sözcüğü sanılıp
        // listeyi kapatıyordu — sonraki kolon virgülü bölünmüyor, ELSE/END sütun 0'a fırlıyordu.
        string sonuc = SqlBicimleyici.Bicimlendir(
            "SELECT a, CASE WHEN x = 1 THEN 'bir' ELSE 'iki' END AS t, b FROM m");
        string[] satirlar = sonuc.ReplaceLineEndings("\n").Split('\n');

        Assert.Equal("SELECT a,", satirlar[0]);
        Assert.Equal("    CASE WHEN x = 1 THEN 'bir' ELSE 'iki' END AS t,", satirlar[1]); // CASE tek parça
        Assert.Equal("    b", satirlar[2]);   // CASE'ten SONRAKİ kolon da bölündü (liste yaşıyor)
        Assert.Equal("FROM m", satirlar[3]);
    }

    [Fact]
    public void Alt_sorgu_kapaninca_dis_select_listesi_devam_eder()
    {
        string sonuc = SqlBicimleyici.Bicimlendir("SELECT a, (SELECT MAX(x) FROM q) AS m, c FROM t");
        string[] satirlar = [.. sonuc.ReplaceLineEndings("\n").Split('\n').Select(s => s.Trim())];

        Assert.Contains("SELECT a,", satirlar);          // ilk kolon SELECT satırında kalır
        Assert.Contains(satirlar, s => s.EndsWith("AS m,")); // alt sorgu kapandı, dış virgül böldü
        Assert.Contains("c", satirlar);                  // son kolon kendi satırında
    }
}
