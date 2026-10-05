using System.Text.RegularExpressions;

namespace SQLST.Application;

/// <summary>
/// Editör "Bul ve Değiştir" (v10 öncesi, kullanıcı isteği 2026-07-23) SAF çekirdeği: arama desenini
/// (düz/regex · büyük-küçük · tam sözcük) kurar ve tümünü-değiştirmeyi metin üzerinde yapar. UI yok —
/// pencere yalnız editör seçimi/kaydırma yapar; eşleştirme/değiştirme burada, test edilebilir.
/// </summary>
public static class BulDegistirci
{
    /// <summary>Arama desenini kurar. Boş metin ya da geçersiz regex → null + <paramref name="hata"/>.</summary>
    public static Regex? Desen(string bul, bool buyukKucuk, bool tamSozcuk, bool regex, out string? hata)
    {
        hata = null;
        if (string.IsNullOrEmpty(bul))
        {
            hata = "Aranacak metin boş.";
            return null;
        }

        string desen = regex ? bul : Regex.Escape(bul);
        if (tamSozcuk)
            desen = $@"\b(?:{desen})\b";

        RegexOptions secenek = RegexOptions.CultureInvariant | (buyukKucuk ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(desen, secenek);
        }
        catch (ArgumentException ex)
        {
            hata = $"Geçersiz regex: {ex.Message}";
            return null;
        }
    }

    /// <summary>Değiştirme metnini kipe göre hazırlar: düz modda <c>$</c> literal ($$), regex modda olduğu gibi.</summary>
    public static string Yerlestirme(string degistir, bool regex)
        => regex ? degistir : degistir.Replace("$", "$$");

    /// <summary>Kaynaktaki tüm eşleşmeleri değiştirir; (yeni metin, değiştirilen sayı) döner.</summary>
    public static (string Metin, int Sayi) TumunuDegistir(string kaynak, Regex desen, string yerlestirme)
    {
        int sayi = desen.Matches(kaynak).Count;
        return sayi == 0 ? (kaynak, 0) : (desen.Replace(kaynak, yerlestirme), sayi);
    }
}
