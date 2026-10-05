using System.Data;
using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>Kolon araçları (2026-07-28): "boş kolonları gizle" için tümü-boş kolon tespiti.</summary>
public class GridKolonAraclariTests
{
    private static DataTable Kisi()
    {
        var t = new DataTable();
        foreach (string ad in new[] { "Id", "EskiId", "Aktif", "TcKimlikNo", "PasaportNo", "Adi", "Soyadi" })
            t.Columns.Add(ad, typeof(object));
        // Kullanıcının Kişi senaryosu: Id/TcKimlikNo/Adi/Soyadi dolu, gerisi NULL.
        t.Rows.Add(1, DBNull.Value, DBNull.Value, "12345678901", DBNull.Value, "EROLCAN", "DEMİR");
        return t;
    }

    [Fact]
    public void Tumu_null_kolonlar_bos_sayilir()
    {
        IReadOnlyList<string> bos = GridKolonAraclari.BosKolonlar(Kisi());

        Assert.Contains("EskiId", bos);
        Assert.Contains("Aktif", bos);
        Assert.Contains("PasaportNo", bos);
        Assert.DoesNotContain("Id", bos);
        Assert.DoesNotContain("TcKimlikNo", bos);
        Assert.DoesNotContain("Adi", bos);
    }

    [Fact]
    public void Bos_metin_de_bos_sayilir_bir_satir_doluysa_sayilmaz()
    {
        var t = new DataTable();
        t.Columns.Add("A", typeof(string));
        t.Columns.Add("B", typeof(string));
        t.Rows.Add("", DBNull.Value);      // A boş metin, B null
        t.Rows.Add("", "dolu");            // A hâlâ boş, B artık dolu

        IReadOnlyList<string> bos = GridKolonAraclari.BosKolonlar(t);
        Assert.Contains("A", bos);          // her iki satırda da boş metin
        Assert.DoesNotContain("B", bos);    // bir satır dolu
    }

    [Fact]
    public void Sifir_satirda_hicbir_kolon_bos_sayilmaz()
    {
        var t = new DataTable();
        t.Columns.Add("A", typeof(string));
        t.Columns.Add("B", typeof(int));

        Assert.Empty(GridKolonAraclari.BosKolonlar(t));
    }

    [Fact]
    public void Sifir_degeri_bos_degildir()
    {
        var t = new DataTable();
        t.Columns.Add("Sayi", typeof(int));
        t.Rows.Add(0);

        Assert.Empty(GridKolonAraclari.BosKolonlar(t)); // 0, "boş" değildir
    }
}
