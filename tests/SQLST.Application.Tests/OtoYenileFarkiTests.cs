using System.Data;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>V5-S4 · Auto-refresh konumsal fark bulucusu.</summary>
public class OtoYenileFarkiTests
{
    private static DataTable Tablo(string[] kolonlar, params object?[][] satirlar)
    {
        var t = new DataTable();
        foreach (string k in kolonlar)
            t.Columns.Add(k, typeof(object));
        foreach (object?[] s in satirlar)
            t.Rows.Add(s);
        return t;
    }

    [Fact]
    public void Ilk_kosuda_fark_yoktur()
    {
        YenilemeFarki fark = OtoYenileFarki.Karsilastir(null, Tablo(["a"], [1]));

        Assert.False(fark.FarkVar);
        Assert.Equal("Değişiklik yok.", fark.Ozet);
    }

    [Fact]
    public void Ayni_tablo_fark_uretmez()
    {
        DataTable a = Tablo(["id", "ad"], [1, "x"], [2, "y"]);
        DataTable b = Tablo(["id", "ad"], [1, "x"], [2, "y"]);

        Assert.False(OtoYenileFarki.Karsilastir(a, b).FarkVar);
    }

    [Fact]
    public void Eklenen_ve_silinen_satirlar_sayilir()
    {
        DataTable a = Tablo(["id"], [1], [2]);
        DataTable b = Tablo(["id"], [1], [2], [3]);

        YenilemeFarki eklendi = OtoYenileFarki.Karsilastir(a, b);
        Assert.Equal(1, eklendi.Eklenen);
        Assert.Equal(0, eklendi.Silinen);
        Assert.Contains("+1 satır", eklendi.Ozet);

        YenilemeFarki silindi = OtoYenileFarki.Karsilastir(b, a);
        Assert.Equal(1, silindi.Silinen);
        Assert.Equal(0, silindi.Eklenen);
    }

    [Fact]
    public void Degisen_hucre_satiri_isaretler()
    {
        DataTable a = Tablo(["id", "tutar"], [1, 100], [2, 200]);
        DataTable b = Tablo(["id", "tutar"], [1, 100], [2, 999]);

        YenilemeFarki fark = OtoYenileFarki.Karsilastir(a, b);

        Assert.Equal(1, fark.Degisen);
        Assert.Equal([1], fark.DegisenSatirlar);   // ikinci satır (0 tabanlı)
        Assert.DoesNotContain(0, fark.DegisenSatirlar);
    }

    [Fact]
    public void Kolonlar_degisirse_satir_karsilastirmasi_YAPILMAZ()
    {
        DataTable a = Tablo(["id"], [1]);
        DataTable b = Tablo(["id", "yeni"], [1, 2]);

        YenilemeFarki fark = OtoYenileFarki.Karsilastir(a, b);

        Assert.True(fark.SekilDegisti);
        Assert.Equal(0, fark.Degisen);
        Assert.Contains("Kolonlar değişti", fark.Ozet);
    }

    [Fact]
    public void Kolon_ADI_degisirse_de_sekil_degisti_sayilir()
    {
        DataTable a = Tablo(["id"], [1]);
        DataTable b = Tablo(["kimlik"], [1]);

        Assert.True(OtoYenileFarki.Karsilastir(a, b).SekilDegisti);
    }

    [Fact]
    public void Bos_sonuca_dusen_sorgu_silinen_olarak_gorunur()
    {
        DataTable a = Tablo(["id"], [1], [2]);
        DataTable b = Tablo(["id"]);

        YenilemeFarki fark = OtoYenileFarki.Karsilastir(a, b);
        Assert.Equal(2, fark.Silinen);
        Assert.True(fark.FarkVar);
    }
}

/// <summary>
/// V5-S4 · Auto-refresh GÜVENLİK KAPISI. Bu, kolaylık değil veri güvenliği meselesidir:
/// bir DELETE/UPDATE'in aralıklı olarak kendiliğinden tekrar çalışması veri kaybıdır.
/// Kapı <see cref="QueryService.PlanIcinYazmaSayilir"/> ile kurulur.
/// </summary>
public class OtoYenileYazmaKapisiTests
{
    [Theory]
    [InlineData("DELETE FROM Musteri WHERE Id = 1;")]
    [InlineData("UPDATE Musteri SET Ad = 'x';")]
    [InlineData("INSERT INTO Musteri (Ad) VALUES ('x');")]
    [InlineData("TRUNCATE TABLE Musteri;")]
    [InlineData("DROP TABLE Musteri;")]
    // CTE içine gizlenmiş DML: ilk sözcük WITH olduğu için naif kontrol "okuma" derdi.
    [InlineData("WITH x AS (DELETE FROM Musteri RETURNING *) SELECT * FROM x;")]
    public void Yazma_sorgularinda_oto_yenile_ACILMAZ(string sql)
        => Assert.True(QueryService.PlanIcinYazmaSayilir(sql, MotorTuru.Mssql),
            $"bu sorgu yazma sayılmalıydı: {sql}");

    [Theory]
    [InlineData("SELECT * FROM Musteri;")]
    [InlineData("SELECT COUNT(*) FROM sys.objects;")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT * FROM x;")]
    public void Okuma_sorgularinda_oto_yenile_acilabilir(string sql)
        => Assert.False(QueryService.PlanIcinYazmaSayilir(sql, MotorTuru.Mssql),
            $"bu sorgu okuma sayılmalıydı: {sql}");
}
