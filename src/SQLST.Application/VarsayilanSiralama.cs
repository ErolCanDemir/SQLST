using System.Text.RegularExpressions;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// v20-S21 saha m.23 ("ORDER BY yokken sıralama karışık"): SQL sırayı garanti etmez ama kullanıcı
/// Id sırası bekler. Çözüm SQL'e DOKUNMAZ (otomatik ORDER BY büyük tabloda sunucuyu sıralamaya
/// zorlar, TOP/semantik değiştirebilirdi): tek-tablolu, ORDER BY'sız SELECT'in ZATEN BELLEKTEKİ
/// sonucu görüntüde PK'ya göre dizilir + bilgi notu düşülür. ORDER BY tespiti kasıtlı kaba —
/// alt sorgudaki ORDER BY da "var" sayılır; yanlış pozitifin bedeli sıralamamak (eski davranış).
/// </summary>
public static class VarsayilanSiralama
{
    public static bool OrderByIceriyorMu(string sql)
        => Regex.IsMatch(sql, @"\border\s+by\b", RegexOptions.IgnoreCase);

    /// <summary>PK kolon adlarının sonuç kümesindeki indeksleri — HERHANGİ biri sonuçta yoksa null
    /// (eksik anahtarla sıralamak yanıltıcı olur; SELECT'te PK yoksa hiç karışılmaz).</summary>
    public static int[]? PkIndeksleri(IReadOnlyList<KolonBilgisi> kolonlar, IReadOnlyList<string> pkAdlari)
    {
        if (pkAdlari.Count == 0)
            return null;
        var indeksler = new int[pkAdlari.Count];
        for (int i = 0; i < pkAdlari.Count; i++)
        {
            int bulunan = -1;
            for (int k = 0; k < kolonlar.Count; k++)
                if (string.Equals(kolonlar[k].Ad, pkAdlari[i], StringComparison.OrdinalIgnoreCase))
                {
                    bulunan = k;
                    break;
                }
            if (bulunan < 0)
                return null;
            indeksler[i] = bulunan;
        }
        return indeksler;
    }

    /// <summary>Satırları verilen kolon indekslerine göre ARTAN sıralar (null/DBNull en sonda).
    /// PK benzersiz olduğundan kararlılık meselesi yoktur.</summary>
    public static void Sirala(List<object?[]> satirlar, IReadOnlyList<int> indeksler)
        => satirlar.Sort((a, b) =>
        {
            foreach (int i in indeksler)
            {
                int fark = Kiyasla(a[i], b[i]);
                if (fark != 0)
                    return fark;
            }
            return 0;
        });

    private static int Kiyasla(object? a, object? b)
    {
        bool aBos = a is null or DBNull, bBos = b is null or DBNull;
        if (aBos || bBos)
            return aBos && bBos ? 0 : aBos ? 1 : -1; // null en sonda
        if (a!.GetType() == b!.GetType() && a is IComparable k)
            return k.CompareTo(b);
        return string.CompareOrdinal(a.ToString(), b.ToString()); // tip karışıksa son çare
    }
}
