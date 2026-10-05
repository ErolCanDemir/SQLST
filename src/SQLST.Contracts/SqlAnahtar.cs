namespace SQLST.Contracts;

/// <summary>
/// Sorgu metninin ilk anlamlı anahtar sözcüğünü bulur (V4-S2). Amaç %100 SQL ayrıştırma
/// DEĞİL, sınıflandırma sigortası (02-mimari §4.4) — lehçeler "bu ifade örtük commit yapar mı"
/// sorusunu bununla yanıtlar.
///
/// Contracts'ta durur çünkü hem Application hem Infrastructure (lehçeler) kullanır —
/// bağımlılık yönü <c>Application → Contracts ← Infrastructure</c>.
/// </summary>
public static class SqlAnahtar
{
    /// <summary>
    /// Baştaki boşlukları, <c>--</c> satır yorumlarını ve <c>/* */</c> blok yorumlarını
    /// atlayarak ilk anahtar sözcüğü BÜYÜK harfle döndürür; bulunamazsa boş dize.
    /// </summary>
    public static string IlkKelime(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int satirSonu = sql.IndexOf('\n', i);
                if (satirSonu < 0) return "";
                i = satirSonu + 1;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int kapanis = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (kapanis < 0) return "";
                i = kapanis + 2;
                continue;
            }

            int bas = i;
            while (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_'))
                i++;
            return i > bas ? sql[bas..i].ToUpperInvariant() : "";
        }

        return "";
    }
}
