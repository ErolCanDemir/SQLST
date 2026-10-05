using System.Globalization;
using SQLST.App.Converters;

namespace SQLST.App.Tests;

/// <summary>
/// Hücre görüntü metni (v20-S21 saha m.9/14/25 ÇÖKME düzeltmesi): dev metin artık 2000 karaktere
/// kısaltılır — TextBlock ölçümü MB'lık string işleyip donduramaz/OOM'a süremez. Tam değer çift
/// tık/kopyala/CSV yollarında korunur (converter yalnız görüntü).
/// </summary>
public class HucreMetniConverterTests
{
    private static object? Cevir(object? deger) =>
        HucreMetniConverter.Ornek.Convert(deger, typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Dev_metin_2000_karaktere_kisaltilir()
    {
        string dev = new('x', 3_000_000); // 3M karakter ≈ MB'lık JSON değeri

        string sonuc = Assert.IsType<string>(Cevir(dev));

        Assert.True(sonuc.Length < 2100, $"kısaltılmadı: {sonuc.Length}");
        Assert.Contains("3.000.000", sonuc.Replace(",", ".")); // boyut notu (kültür ayracı esnek)
        Assert.Contains("çift tık", sonuc);
    }

    [Fact]
    public void Kisa_metin_aynen_kalir()
    {
        Assert.Equal("merhaba", Cevir("merhaba"));
    }

    [Fact]
    public void Cok_satirli_kisa_metin_tek_satira_iner()
    {
        Assert.Equal("a ⏎ b", Cevir("a\r\nb"));
    }

    [Fact]
    public void Dbnull_null_yazar()
    {
        Assert.Equal("NULL", Cevir(DBNull.Value));
    }
}
