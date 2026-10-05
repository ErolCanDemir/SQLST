using System.Globalization;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// v20-S21 m.10 fikir 2 ("otomatik lookup tooltip'i"): tanım tablosunun ANAHTAR→AÇIKLAMA sözlüğü.
/// Lookup penceresi (v20-S14) hücre başına TEK scalar sorgu atıyordu; tooltip her hücrede
/// tetikleneceği için o model sürdürülemez — tanım tablosu KÜÇÜKTÜR, bir kez toptan okunup
/// önbelleğe alınır, sonraki hücreler sorgusuz çözülür. Bu sınıf saf kısımdır: sorgu metni +
/// satırların sözlüğe çevrilmesi + anahtar normalleştirme.
/// </summary>
public static class LookupSozlugu
{
    /// <summary>Sözlüğe alınacak azami satır: tanım tablosu bundan büyükse "tanım tablosu"
    /// sayılmaz — tooltip kapatılır (dev tabloyu belleğe çekmek yok).</summary>
    public const int Tavan = 5000;

    public static string SorguYaz(
        ILehce lehce, string sema, string tablo, string anahtarKolon, string aciklamaKolon, int tavan = Tavan)
        => $"SELECT {lehce.SatirSinirBasi(tavan + 1)}{lehce.TirnaklaTanimlayici(anahtarKolon)}, "
         + $"{lehce.TirnaklaTanimlayici(aciklamaKolon)} "
         + $"FROM {lehce.TamAdYaz(sema, tablo)}{lehce.SatirSinirSonu(tavan + 1)}";

    /// <summary>İki kolonlu sonucu sözlüğe çevirir. Tavan aşılırsa <c>null</c> — çağıran tooltip'i
    /// kapatır (tanım tablosu değilmiş). Yinelenen anahtarda İLK açıklama kalır; null anahtar atlanır.</summary>
    public static Dictionary<string, string>? Coz(ResultSetData veri, int tavan = Tavan)
    {
        if (veri.Satirlar.Count > tavan)
            return null;

        var sozluk = new Dictionary<string, string>(veri.Satirlar.Count, StringComparer.OrdinalIgnoreCase);
        foreach (object?[] satir in veri.Satirlar)
        {
            if (satir.Length < 2)
                continue;
            string anahtar = Anahtar(satir[0]);
            if (anahtar.Length == 0)
                continue;
            sozluk.TryAdd(anahtar, Metin(satir[1]));
        }
        return sozluk;
    }

    /// <summary>Hücre değerini sözlük anahtarına çevirir (kültürden bağımsız — 3 ≡ "3").</summary>
    public static string Anahtar(object? deger) => Metin(deger).Trim();

    private static string Metin(object? deger) => deger switch
    {
        null or DBNull => "",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => deger.ToString() ?? "",
    };
}
