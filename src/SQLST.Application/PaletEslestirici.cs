namespace SQLST.Application;

/// <summary>
/// Ctrl+P "her yere atla" paleti eşleştiricisi (V2-S10, FG-2.9). Saf ve UI'sız:
/// önek > içerir > alt-dizi (fuzzy) öncelik sırasıyla puanlar; puan büyük = üstte.
/// </summary>
public static class PaletEslestirici
{
    /// <summary>Eşleşmiyorsa null; yoksa sıralama puanı (büyük daha iyi).</summary>
    public static int? Puan(string sorgu, string aday)
    {
        if (string.IsNullOrWhiteSpace(sorgu))
            return 0; // boş sorgu: hepsi eşit — çağıran kendi doğal sırasını korur

        string s = sorgu.Trim();
        int indeks = aday.IndexOf(s, StringComparison.OrdinalIgnoreCase);
        if (indeks == 0)
            return 10_000 - aday.Length;              // önek: en güçlü
        if (indeks > 0)
            return 5_000 - indeks * 10 - aday.Length; // içerir: erken geçen üstte

        // alt-dizi: karakterler sırayla geçiyor mu ("spmk" → spMusteriKaydet)
        int a = 0, bosluk = 0, son = -1;
        for (int i = 0; i < aday.Length && a < s.Length; i++)
        {
            if (char.ToUpperInvariant(aday[i]) == char.ToUpperInvariant(s[a]))
            {
                if (son >= 0)
                    bosluk += i - son - 1;
                son = i;
                a++;
            }
        }
        return a == s.Length ? 1_000 - bosluk * 5 - aday.Length : null;
    }

    /// <summary>Adayları puanla süzüp sıralar; en çok <paramref name="enCok"/> sonuç.</summary>
    public static IReadOnlyList<T> Sirala<T>(
        string sorgu, IReadOnlyList<T> adaylar, Func<T, string> metin, int enCok = 12)
        => [.. adaylar
            .Select(aday => (Aday: aday, Puan: Puan(sorgu, metin(aday))))
            .Where(x => x.Puan is not null)
            .OrderByDescending(x => x.Puan)
            .Take(enCok)
            .Select(x => x.Aday)];
}
