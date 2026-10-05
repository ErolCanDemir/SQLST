using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// v20-S21 m.10 fikir 10 ("WHERE'de tanım değeri önerisi"): imleç bir kolonla karşılaştırmanın
/// SAĞINDAYSA (<c>WHERE t.DurumId = ▮</c>, <c>IN (1, ▮</c>) o kolonun tanım tablosundaki değerleri
/// anlamıyla öneririz — "3 müydü 4 müydü?" diye tanım tablosuna SELECT atmak biter. Bu sınıf saf
/// kısımdır: bağlam tespiti ve kolonun hedefini FK grafından bulma.
/// </summary>
public static class TanimDegeriBaglami
{
    /// <summary>
    /// İmleçten geriye bakar; karşılaştırma/liste bağlamındaysa karşılaştırılan KOLON adını döner
    /// (nitelenmişse son parça: <c>t.DurumId</c> → "DurumId"), değilse null.
    /// </summary>
    public static string? KolonCikar(string metin, int ofset)
    {
        if (string.IsNullOrEmpty(metin) || ofset <= 0 || ofset > metin.Length)
            return null;

        int i = ofset - 1;
        i = BoslukAtla(metin, i);
        // Kısmen yazılmış değer (ör. "= 3▮") önerileri kapatmaz — üstünden geçilir.
        while (i >= 0 && (char.IsLetterOrDigit(metin[i]) || metin[i] is '_' or '\''))
            i--;
        i = BoslukAtla(metin, i);
        if (i < 0)
            return null;

        // Kabul edilen bağlamlar: "=", "<>", ">=", "<=", "," ve "(" (IN listesi).
        if (metin[i] == '=')
        {
            i--;
            if (i >= 0 && metin[i] is '<' or '>')
                i--;
        }
        else if (metin[i] == '>' && i > 0 && metin[i - 1] == '<') // <>
            i -= 2;
        else if (metin[i] is ',' or '(')
        {
            i = ListeninKoluneGit(metin, i);
            if (i < 0)
                return null;
        }
        else
        {
            return null;
        }

        i = BoslukAtla(metin, i);
        int son = i;
        while (i >= 0 && (char.IsLetterOrDigit(metin[i]) || metin[i] is '_' or '.' or '[' or ']'))
            i--;
        if (son <= i)
            return null;

        string ham = metin[(i + 1)..(son + 1)].Replace("[", "").Replace("]", "").Trim('.');
        if (ham.Length == 0)
            return null;
        string[] parcalar = ham.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parcalar.Length == 0 ? null : parcalar[^1];
    }

    /// <summary>IN listesinde geriye doğru "(" ve virgülleri/değerleri atlayıp IN'in soluna geçer.</summary>
    private static int ListeninKoluneGit(string metin, int i)
    {
        int derinlik = 0;
        for (; i >= 0; i--)
        {
            if (metin[i] == ')')
                derinlik++;
            else if (metin[i] == '(')
            {
                if (derinlik == 0)
                    break;
                derinlik--;
            }
        }
        if (i < 0)
            return -1;

        i = BoslukAtla(metin, i - 1);
        // "(" öncesinde IN olmalı — fonksiyon çağrısı parantezinde öneri açmayalım.
        if (i < 1 || char.ToUpperInvariant(metin[i]) != 'N' || char.ToUpperInvariant(metin[i - 1]) != 'I')
            return -1;
        return i - 2;
    }

    private static int BoslukAtla(string metin, int i)
    {
        while (i >= 0 && char.IsWhiteSpace(metin[i]))
            i--;
        return i;
    }

    /// <summary>
    /// Kolon adının FK hedefini bulur. Sorgudan kaynak tablo çıkarılamadığında (JOIN'li sorgu)
    /// kolon ADINA göre FK grafında aranır; farklı hedefe giden birden çok eşleşme varsa
    /// <c>null</c> döner — yanlış tablonun değerlerini önermektense hiç önermemek yeğdir.
    /// </summary>
    public static (string Sema, string Tablo, string AnahtarKolon)? HedefBul(
        IReadOnlyList<YabanciAnahtar> fkler, string kolonAd, string? kaynakTablo = null)
    {
        var adaylar = new List<(string Sema, string Tablo, string Anahtar)>();
        foreach (YabanciAnahtar fk in fkler)
        {
            if (kaynakTablo is not null
                && !fk.KaynakTablo.Equals(kaynakTablo, StringComparison.OrdinalIgnoreCase))
                continue;
            for (int i = 0; i < fk.KaynakKolonlar.Count && i < fk.HedefKolonlar.Count; i++)
            {
                if (!fk.KaynakKolonlar[i].Equals(kolonAd, StringComparison.OrdinalIgnoreCase))
                    continue;
                (string, string, string) aday = (fk.HedefSema, fk.HedefTablo, fk.HedefKolonlar[i]);
                if (!adaylar.Contains(aday))
                    adaylar.Add(aday);
            }
        }
        return adaylar.Count == 1 ? adaylar[0] : null;
    }
}
