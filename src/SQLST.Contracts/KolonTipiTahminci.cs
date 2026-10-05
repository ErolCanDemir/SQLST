using System.Globalization;

namespace SQLST.Contracts;

/// <summary>
/// Kolon tipi tahmini (v13-S1) — SAF: önizleme örnekleminden kolon başına
/// <see cref="DosyaTipi"/> çıkarır. TXT değerleri string gelir ve SEÇİLEN KÜLTÜRLE parse edilir
/// (TR: <c>1.250,75</c> ve <c>24.07.2026</c>; Invariant: <c>1,250.75</c>); Excel değerleri hücre
/// tipiyle gelir (double/DateTime/bool) ve doğrudan sınıflanır — Excel TÜM sayıları double verir,
/// ondalıksız double tam sayı sayılır. Karışım kuralı: TamSayi+Ondalik→Ondalik; başka her karışım
/// → Metin (asla veri kaybettirecek dar tip önerilmez). Boş/null değerler tipi ETKİLEMEZ, yalnız
/// <see cref="DosyaKolonu.BosVar"/>'ı işaretler; tüm değerleri boş kolon Metin kalır.
/// </summary>
public static class KolonTipiTahminci
{
    public static IReadOnlyList<DosyaKolonu> Tahmin(
        IReadOnlyList<string> adlar, IReadOnlyList<object?[]> satirlar, CultureInfo kultur)
    {
        var sonuc = new List<DosyaKolonu>(adlar.Count);
        for (int k = 0; k < adlar.Count; k++)
        {
            DosyaTipi? tip = null; // ilk dolu değere dek bilinmez
            int enUzun = 0;
            bool bosVar = false;

            foreach (object?[] satir in satirlar)
            {
                object? deger = k < satir.Length ? satir[k] : null;
                if (deger is null || (deger is string bos && bos.Length == 0))
                {
                    bosVar = true; // eksik alan (kısa satır) da boş sayılır
                    continue;
                }

                string metin = Convert.ToString(deger, kultur) ?? "";
                enUzun = Math.Max(enUzun, metin.Length);
                tip = Birlestir(tip, DegerTipi(deger, kultur));
            }

            sonuc.Add(new DosyaKolonu(adlar[k], tip ?? DosyaTipi.Metin, enUzun, bosVar));
        }

        return sonuc;
    }

    private static DosyaTipi DegerTipi(object deger, CultureInfo kultur) => deger switch
    {
        bool => DosyaTipi.Bool,
        sbyte or byte or short or ushort or int or uint or long => DosyaTipi.TamSayi,
        // Excel bütün sayıları double taşır — ondalıksız ve long aralığındaysa tam sayıdır.
        double d => d == Math.Floor(d) && d is >= long.MinValue and <= long.MaxValue
            ? DosyaTipi.TamSayi : DosyaTipi.Ondalik,
        float or decimal => DosyaTipi.Ondalik,
        DateTime => DosyaTipi.Tarih,
        string s => MetinTipi(s.Trim(), kultur),
        _ => DosyaTipi.Metin,
    };

    private static DosyaTipi MetinTipi(string s, CultureInfo kultur)
    {
        if (long.TryParse(s, NumberStyles.Integer, kultur, out _))
            return DosyaTipi.TamSayi;

        // Sayı/tarih BELİRSİZLİĞİ (TR gerçeği): "3,10" hem ondalık hem tarih (3 Ekim),
        // "24.07.2026" hem tarih hem sayı (decimal.TryParse binlik grup uzunluğunu DENETLEMEZ →
        // 24072026) parse edilir. Kural: metin kültürün ONDALIK AYRACINI içeriyorsa sayı kazanır
        // (fiyat/oran görünümü), içermiyorsa önce tarih denenir (gg.aa.yyyy görünümü).
        bool sayiParse = decimal.TryParse(s, NumberStyles.Number, kultur, out _);
        if (sayiParse && s.Contains(kultur.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal))
            return DosyaTipi.Ondalik;
        if (DateTime.TryParse(s, kultur, DateTimeStyles.None, out _))
            return DosyaTipi.Tarih;
        if (sayiParse)
            return DosyaTipi.Ondalik;
        if (bool.TryParse(s, out _))
            return DosyaTipi.Bool;
        return DosyaTipi.Metin;
    }

    private static DosyaTipi Birlestir(DosyaTipi? onceki, DosyaTipi yeni)
    {
        if (onceki is null || onceki == yeni)
            return yeni;
        // Tek anlamlı genişleme: tam sayı ↔ ondalık → ondalık. Gerisi metine düşer.
        bool sayiKarisimi = onceki is DosyaTipi.TamSayi or DosyaTipi.Ondalik
            && yeni is DosyaTipi.TamSayi or DosyaTipi.Ondalik;
        return sayiKarisimi ? DosyaTipi.Ondalik : DosyaTipi.Metin;
    }
}
