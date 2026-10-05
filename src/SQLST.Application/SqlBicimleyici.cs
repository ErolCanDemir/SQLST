using System.Text;

namespace SQLST.Application;

/// <summary>
/// Hafif okunabilirlik biçimleyicisi (FG-3.9'dan erken alınan dilim, kullanıcı isteği 2026-07-16):
/// tek satıra sıkışmış SQL'i ana anahtar sözcüklerden alt satıra böler, BEGIN/END girintiler.
///
/// Bilinçli sınırlar — SQL'i yeniden yazmak değil, sadece satır sonu eklemek:
/// • Dizeler ('...'), köşeli/çift tırnaklı adlar ([..], "..") ve yorumlar (--, /* */) OLDUĞU GİBİ korunur.
/// • "AS" ve "ON" listede yok: takma ad (col AS x) ve "SET ANSI_NULLS ON" gibi yerlerde bölmek okunabilirliği bozar.
/// • Zaten biçimli gövdeye dokunulmaz (bkz. <see cref="BicimlendirmeyeDegerMi"/>) — kullanıcının
///   düzenini ALTER sırasında ezmeyelim.
/// </summary>
public static class SqlBicimleyici
{
    /// <summary>Uzunluk sırası önemli: "DELETE FROM" önce eşleşmeli ki "DELETE" + "FROM" diye ikiye bölünmesin.</summary>
    private static readonly string[] SatirBaslatanlar =
    [
        "LEFT OUTER JOIN", "RIGHT OUTER JOIN", "FULL OUTER JOIN",
        "INSERT INTO", "DELETE FROM", "CROSS APPLY", "OUTER APPLY",
        "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "INNER JOIN",
        "GROUP BY", "ORDER BY", "UNION ALL",
        "SELECT", "FROM", "WHERE", "HAVING", "UNION", "JOIN", "VALUES",
        "UPDATE", "DELETE", "DECLARE", "BEGIN", "END", "EXECUTE", "EXEC",
        "RETURN", "WHILE", "ELSE", "IF",
    ];

    private const string Girinti = "    ";

    /// <summary>
    /// Biçimlendirmeye değer mi: gövde tek satıra sıkışmış VE bölününce gerçekten
    /// satır kazanıyor mu. Uzunluk eşiği yok — 112 karakterlik tek satırlık bir SP de
    /// okunabilirlik kazanır. Zaten satırlara yayılmış gövdeye dokunulmaz (geliştiricinin
    /// düzeni ALTER sırasında ezilmesin).
    /// </summary>
    public static bool BicimlendirmeyeDegerMi(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Count(c => c == '\n') > 2)
            return false;

        return SatirSayisi(Bicimlendir(sql)) >= 3;
    }

    private static int SatirSayisi(string metin) => metin.Count(c => c == '\n') + 1;

    public static string Bicimlendir(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var sb = new StringBuilder(sql.Length + 64);
        int derinlik = 0;
        int i = 0;

        // SELECT listesi kolonları ALT ALTA (kullanıcı isteği 2026-07-23): SELECT görülünce o anki
        // parantez derinliği yığına alınır; AYNI derinlikteki virgüller satır böler. Alt sorgu /
        // fonksiyon argümanı virgülleri — CONVERT(x,y), OVER(PARTITION BY a, b) — daha derin
        // parantezde olduğundan bölünmez. FROM/WHERE gibi sonraki anahtar sözcük veya kapanan
        // parantez listeyi bitirir (yığın: iç içe SELECT'lerde dış liste kaybolmasın).
        int parantez = 0;
        int caseDerinlik = 0; // CASE…END ifade sayacı: içindeki ELSE/END blok değil İFADEdir
        var secimDerinlikleri = new Stack<int>();

        while (i < sql.Length)
        {
            char c = sql[i];

            // Opak bölgeler: dizeler, adlar, yorumlar — içeriklerine dokunulmaz
            if (c is '\'' or '"')
            {
                i = OpakKopyala(sql, i, sb, c, c);
                continue;
            }
            if (c == '[')
            {
                i = OpakKopyala(sql, i, sb, '[', ']');
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int son = sql.IndexOf('\n', i);
                son = son < 0 ? sql.Length : son + 1;
                sb.Append(sql, i, son - i);
                i = son;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int son = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                son = son < 0 ? sql.Length : son + 2;
                sb.Append(sql, i, son - i);
                i = son;
                continue;
            }

            // CASE ifadesi başlıyor (SatirBaslatanlar'da değil — burada elle yakalanır): sayacı
            // artır ki içindeki ELSE/END blok anahtar sözcüğü sanılmasın (inceleme bulgusu
            // 2026-07-23: ELSE, SELECT listesini POP edip düzeni bozuyordu).
            if (SozcukBasiMi(sql, i) && SozcukMu(sql, i, "CASE"))
            {
                caseDerinlik++;
                sb.Append(sql, i, 4);
                i += 4;
                continue;
            }

            // Sözcük başındaysak anahtar sözcük mü diye bak
            if (SozcukBasiMi(sql, i) && Eslesen(sql, i) is { } kelime)
            {
                // CASE içindeki ELSE/END ifadenin parçasıdır: satır bölme/pop/girinti uygulanmaz.
                if (caseDerinlik > 0
                    && (kelime.Equals("END", StringComparison.OrdinalIgnoreCase)
                     || kelime.Equals("ELSE", StringComparison.OrdinalIgnoreCase)))
                {
                    if (kelime.Equals("END", StringComparison.OrdinalIgnoreCase))
                        caseDerinlik--;
                    sb.Append(sql, i, kelime.Length);
                    i += kelime.Length;
                    continue;
                }

                // OVER(PARTITION BY a ORDER BY b) / fonksiyon argümanı / IN(...) İÇİNDEKİ anahtar
                // sözcükler satır BÖLMEZ (2026-07-23 canlı SP kanıtı: OVER içi ORDER BY kırılıyordu).
                // Ölçüt: SELECT'in kayıtlı derinliğinden DAHA DERİN parantezdeyiz ve sözcük SELECT
                // değil (alt sorgu SELECT'i her zaman işlenir — yığına girip kendi FROM'unu bölmeli).
                int aktifSecim = secimDerinlikleri.Count > 0 ? secimDerinlikleri.Peek() : 0;
                if (parantez > aktifSecim && !kelime.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(sql, i, kelime.Length); // olduğu gibi kopyala, düzen değişmez
                    i += kelime.Length;
                    continue;
                }

                bool bitis = kelime.Equals("END", StringComparison.OrdinalIgnoreCase);
                if (bitis)
                    derinlik = Math.Max(0, derinlik - 1);

                // SELECT listesi biter: aynı derinlikte SELECT-olmayan anahtar sözcük geldi (FROM/WHERE/…)
                if (secimDerinlikleri.Count > 0 && parantez == secimDerinlikleri.Peek()
                    && !kelime.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    secimDerinlikleri.Pop();

                SatirBasinaGec(sb, derinlik);
                sb.Append(sql, i, kelime.Length);
                i += kelime.Length;

                if (kelime.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                    derinlik++;
                if (kelime.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    secimDerinlikleri.Push(parantez);

                continue;
            }

            // Parantez takibi + SELECT listesi virgülü: liste derinliğindeki ',' satır böler
            if (c == '(')
                parantez++;
            else if (c == ')')
            {
                parantez--;
                // Alt sorgu FROM'suz kapandıysa ((SELECT 1) gibi) listesi de kapanır
                while (secimDerinlikleri.Count > 0 && parantez < secimDerinlikleri.Peek())
                    secimDerinlikleri.Pop();
            }
            else if (c == ';')
            {
                // Statement bitti: bu derinlikte açık SELECT listesi kalmasın (sonraki statement
                // listede olmayan bir sözcükle başlarsa virgülleri yanlışlıkla bölünmesin)
                while (secimDerinlikleri.Count > 0 && secimDerinlikleri.Peek() >= parantez)
                    secimDerinlikleri.Pop();
            }
            else if (c == ',' && secimDerinlikleri.Count > 0 && parantez == secimDerinlikleri.Peek())
            {
                sb.Append(',');
                SatirBasinaGec(sb, derinlik + 1); // kolonlar SELECT'ten bir içeride alt alta
                i++;
                while (i < sql.Length && sql[i] is ' ' or '\t')
                    i++; // eski boşluklar girintiye eklenmesin
                continue;
            }

            // Satır sonlarını tek boşluğa indir — yeni düzeni biz kuruyoruz
            if (c is '\r' or '\n')
            {
                if (sb.Length > 0 && sb[^1] is not (' ' or '\n'))
                    sb.Append(' ');
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        // Fazla boşlukları topla: satır sonlarındaki boşluklar ve çift boşluklar
        return string.Join(Environment.NewLine,
            sb.ToString()
              .Split('\n')
              .Select(s => s.TrimEnd())
              .Where((s, idx) => idx == 0 || s.Length > 0))
            .Trim();
    }

    /// <summary>Açılış-kapanış arasını (kaçışlı ikilemeler dahil) olduğu gibi kopyalar.</summary>
    private static int OpakKopyala(string sql, int i, StringBuilder sb, char ac, char kapa)
    {
        sb.Append(sql[i]);
        i++;
        while (i < sql.Length)
        {
            sb.Append(sql[i]);
            if (sql[i] == kapa)
            {
                // '' veya ]] gibi ikilenmiş kaçış: bir sonrakini de yut, kapanma sayma
                if (i + 1 < sql.Length && sql[i + 1] == kapa)
                {
                    sb.Append(sql[i + 1]);
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return i;
    }

    private static bool SozcukBasiMi(string sql, int i)
        => i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '@' or '#' or '.');

    /// <summary>Bu konumda tam olarak <paramref name="kelime"/> mi var (sözcük sınırına saygılı)?</summary>
    private static bool SozcukMu(string sql, int i, string kelime)
    {
        if (i + kelime.Length > sql.Length
            || !sql.AsSpan(i, kelime.Length).Equals(kelime, StringComparison.OrdinalIgnoreCase))
            return false;
        int son = i + kelime.Length;
        return son >= sql.Length || !(char.IsLetterOrDigit(sql[son]) || sql[son] == '_');
    }

    /// <summary>Bu konumda satır başlatan bir anahtar sözcük varsa onu döner (sözcük sınırına saygılı).</summary>
    private static string? Eslesen(string sql, int i)
    {
        foreach (string kelime in SatirBaslatanlar)
        {
            if (i + kelime.Length > sql.Length)
                continue;
            if (!sql.AsSpan(i, kelime.Length).Equals(kelime, StringComparison.OrdinalIgnoreCase))
                continue;

            int son = i + kelime.Length;
            if (son < sql.Length && (char.IsLetterOrDigit(sql[son]) || sql[son] == '_'))
                continue; // "SELECTX" gibi

            return kelime;
        }

        return null;
    }

    private static void SatirBasinaGec(StringBuilder sb, int derinlik)
    {
        while (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        if (sb.Length > 0)
            sb.Append('\n');
        for (int d = 0; d < derinlik; d++)
            sb.Append(Girinti);
    }
}
