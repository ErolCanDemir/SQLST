using System.Globalization;
using SQLST.App.Converters;

namespace SQLST.App.Tests;

/// <summary>
/// ✏ Düzenleme grid'i ham dönüşüm (v23-S13): gösterim SSMS biçimi; GERİ YAZIM tr-TR kültürde
/// bile doğru tipe çözülür — "1250.75" ASLA 125075 olmaz (DataRow'a bırakılsaydı olurdu).
/// </summary>
public class DuzenlemeHamConverterTests
{
    private static object? Geri(Type tip, string metin)
    {
        CultureInfo eski = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            return new DuzenlemeHamConverter(tip, null)
                .ConvertBack(metin, typeof(object), null, CultureInfo.InvariantCulture);
        }
        finally { CultureInfo.CurrentCulture = eski; }
    }

    [Fact]
    public void Gosterim_ham_null_ve_metin_dokunulmaz()
    {
        var c = new DuzenlemeHamConverter(typeof(DateTime), "datetime");
        Assert.Equal("2026-10-05 14:23:11.123",
            c.Convert(new DateTime(2026, 10, 5, 14, 23, 11, 123), typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Same(DBNull.Value, c.Convert(DBNull.Value, typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Equal("x", new DuzenlemeHamConverter(typeof(string), "nvarchar")
            .Convert("x", typeof(object), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Geri_yazim_nokta_ondalik_tr_kulturde_dogru_cozulur() // 125075 tuzağı
    {
        Assert.Equal(1250.75m, Geri(typeof(decimal), "1250.75"));
        Assert.Equal(3.14m, Geri(typeof(decimal), "3,14"));      // Türkçe yazan da kazanır (yedek kültür)
        Assert.Equal(3.14d, Geri(typeof(double), "3.14"));
        Assert.Equal(42, Geri(typeof(int), "42"));
    }

    [Fact]
    public void Geri_yazim_tarih_guid_binary()
    {
        Assert.Equal(new DateTime(2026, 10, 5, 14, 23, 11, 123), Geri(typeof(DateTime), "2026-10-05 14:23:11.123"));
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
            Geri(typeof(Guid), "6F9619FF-8B86-D011-B42D-00C04FC964FF"));
        Assert.Equal(new byte[] { 0x0A, 0xFF }, Geri(typeof(byte[]), "0x0AFF"));
    }

    [Fact]
    public void Cozulemeyen_ve_bos_metin_eski_yola_birakilir()
    {
        Assert.Equal("abc", Geri(typeof(decimal), "abc")); // DataRow doğrulaması eskisi gibi uyarır
        Assert.Equal("", Geri(typeof(DateTime), ""));
    }
}
