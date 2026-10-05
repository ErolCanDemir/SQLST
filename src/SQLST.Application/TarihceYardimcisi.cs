using System.Security.Cryptography;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Nesne tarihçesi yardımcıları (V2-S8, Ö2): içerik hash'i + kayıt kurucu.</summary>
public static class TarihceYardimcisi
{
    /// <summary>Tanımın SHA-256 hex hash'i — sürüm tekilleştirme anahtarı.</summary>
    public static string Hash(string tanim)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tanim)));

    public static TarihceKaydi KayitKur(
        string sunucu, string veritabani, string sema, string ad, string tanim, string kaynak) => new()
    {
        Sunucu = sunucu,
        Veritabani = veritabani,
        Sema = sema,
        Ad = ad,
        IcerikHash = Hash(tanim),
        Tanim = tanim,
        GorulmeUtc = DateTime.UtcNow,
        Kaynak = kaynak,
    };

    /// <summary>
    /// "Dışarıda değişti" rozeti (Ö2): son kayıtlı sürümün hash'i sunucudaki güncel
    /// tanımdan farklıysa nesne bu araç dışında değiştirilmiş demektir.
    /// </summary>
    public static bool DisaridaDegisti(TarihceKaydi? sonKayit, string guncelTanim)
        => sonKayit is not null && sonKayit.IcerikHash != Hash(guncelTanim);
}
