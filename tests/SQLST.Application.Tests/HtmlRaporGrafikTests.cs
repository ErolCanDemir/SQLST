using System.Data;
using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>📊 S2 "rapora dönüştür": açık grafiği olan setin PNG'si tablonun üstüne gömülür; olmayan set eskisi gibi.</summary>
public class HtmlRaporGrafikTests
{
    private static DataTable Tablo(string ad)
    {
        var t = new DataTable();
        t.Columns.Add("K", typeof(string));
        t.Rows.Add(ad);
        return t;
    }

    [Fact]
    public void Grafik_verilen_set_img_alir_digerleri_almaz()
    {
        string html = HtmlRaporYazici.Yaz("srv", "db", "SELECT 1", "0,01 sn",
            [("Sonuç 1", Tablo("a")), ("Sonuç 2", Tablo("b"))], new DateTime(2026, 10, 12),
            new Dictionary<int, string> { [1] = "iVBORw0KGgo=" });

        int ilkTablo = html.IndexOf("<h2>Sonuç 1</h2>", StringComparison.Ordinal);
        int ikinciBaslik = html.IndexOf("<h2>Sonuç 2</h2>", StringComparison.Ordinal);
        int img = html.IndexOf("<img class=\"grafik\"", StringComparison.Ordinal);

        Assert.True(ilkTablo >= 0 && ikinciBaslik > ilkTablo);
        Assert.True(img > ikinciBaslik, "grafik ikinci setin başlığından sonra gelmeli");
        Assert.Equal(1, html.Split("<img class=\"grafik\"").Length - 1);
        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgo=\"", html);
        Assert.Contains("alt=\"Grafik — Sonuç 2\"", html);
        Assert.Contains("img.grafik", html);
    }

    [Fact]
    public void Grafik_yoksa_rapor_degismez()
    {
        string html = HtmlRaporYazici.Yaz("srv", null, "SELECT 1", "0,01 sn", [("Sonuç 1", Tablo("a"))], DateTime.Now);
        string bos = HtmlRaporYazici.Yaz("srv", null, "SELECT 1", "0,01 sn", [("Sonuç 1", Tablo("a"))], DateTime.Now, new Dictionary<int, string>());

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<img", bos);
    }
}
