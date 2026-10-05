using System.Globalization;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// v20-S21 m.10 fikir 9 ("editörde tablo önizleme"): editörde tablo adının üstünde Ctrl+hover →
/// kolonlar + ilk satırlar. Bu sınıf saf kısımdır: imleçteki tanımlayıcıyı metinden çıkarma,
/// şemada eşleştirme ve önizleme içeriğinin (kolon özeti + hizalı mini tablo) metne dökülmesi.
/// </summary>
public static class TabloOnizleme
{
    /// <summary>SQL anahtar kelimeleri tablo adı sanılmasın (FROM'un üstünde hover → önizleme yok).</summary>
    private static readonly HashSet<string> Anahtarlar = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "join", "inner", "left", "right", "full", "outer", "cross", "apply",
        "on", "and", "or", "not", "in", "exists", "group", "order", "by", "having", "insert", "update",
        "delete", "set", "values", "into", "as", "with", "nolock", "top", "distinct", "union", "all",
        "case", "when", "then", "else", "end", "null", "is", "like", "between", "asc", "desc", "declare",
        "exec", "execute", "begin", "commit", "rollback", "tran", "transaction", "create", "alter", "drop",
    };

    /// <summary>
    /// Verilen konumdaki nitelenmiş adı çıkarır: <c>[dbo].[Talep]</c> → "dbo.Talep", <c>Talep</c> → "Talep".
    /// Anahtar kelimede / boşlukta / sayıda null döner. Üç parçalı adda (db.şema.tablo) son iki parça alınır.
    /// </summary>
    public static string? TanimlayiciCikar(string metin, int ofset)
    {
        if (string.IsNullOrEmpty(metin) || ofset < 0 || ofset >= metin.Length || !AdKarakteri(metin[ofset]))
            return null;

        int bas = ofset, son = ofset;
        while (bas > 0 && AdKarakteri(metin[bas - 1]))
            bas--;
        while (son + 1 < metin.Length && AdKarakteri(metin[son + 1]))
            son++;

        string ham = metin[bas..(son + 1)].Replace("[", "").Replace("]", "").Trim('.');
        if (ham.Length == 0)
            return null;

        string[] parcalar = ham.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parcalar.Length == 0 || Anahtarlar.Contains(parcalar[^1]) || char.IsDigit(parcalar[^1][0]))
            return null;

        return parcalar.Length >= 2 ? $"{parcalar[^2]}.{parcalar[^1]}" : parcalar[^1];
    }

    private static bool AdKarakteri(char c) => char.IsLetterOrDigit(c) || c is '_' or '[' or ']' or '.' or '$' or '#';

    /// <summary>Şemada tablo/view eşleştirir: "dbo.Talep" tam eşleşme, "Talep" şemasız (ilk eşleşen).</summary>
    public static SemaNesnesi? TabloBul(IReadOnlyList<SemaNesnesi> nesneler, string ad)
    {
        if (string.IsNullOrWhiteSpace(ad))
            return null;
        string[] p = ad.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0)
            return null;
        string tablo = p[^1];
        string? sema = p.Length >= 2 ? p[^2] : null;

        return nesneler.FirstOrDefault(n =>
            n.Tur is SemaNesneTuru.Tablo or SemaNesneTuru.View or SemaNesneTuru.Koleksiyon
            && n.Ad.Equals(tablo, StringComparison.OrdinalIgnoreCase)
            && (sema is null || n.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Kolon özeti: "🔑 Id · int" satırları (uzun tabloda ilk N + "…").</summary>
    public static string KolonOzeti(SemaNesnesi tablo, int azami = 14)
    {
        if (tablo.Kolonlar.Count == 0)
            return "(kolon bilgisi yok)";

        var sb = new StringBuilder();
        foreach (SemaKolonu k in tablo.Kolonlar.Take(azami))
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(k.PkMi ? "🔑 " : k.FkMi ? "🔗 " : "   ").Append(k.Ad).Append("  ·  ").Append(k.Tip);
            if (k.NullOlabilir)
                sb.Append(", null");
        }
        if (tablo.Kolonlar.Count > azami)
            sb.Append($"\n… +{tablo.Kolonlar.Count - azami} kolon daha");
        return sb.ToString();
    }

    /// <summary>Sonucu tek boşluklu (monospace) hizalı metin tabloya döker — mini önizleme gridi.</summary>
    public static string MiniTablo(ResultSetData veri, int azamiKolon = 8, int azamiHucre = 18)
    {
        if (veri.Kolonlar.Count == 0)
            return "(kolon yok)";
        if (veri.Satirlar.Count == 0)
            return "(tablo boş — 0 satır)";

        int kolonSayisi = Math.Min(veri.Kolonlar.Count, azamiKolon);
        string[] basliklar = [.. veri.Kolonlar.Take(kolonSayisi).Select(k => Kis(k.Ad, azamiHucre))];
        List<string[]> satirlar =
        [
            .. veri.Satirlar.Select(s => Enumerable.Range(0, kolonSayisi)
                .Select(i => i < s.Length ? Kis(Hucre(s[i]), azamiHucre) : "")
                .ToArray()),
        ];

        var genislik = new int[kolonSayisi];
        for (int i = 0; i < kolonSayisi; i++)
            genislik[i] = Math.Max(basliklar[i].Length, satirlar.Count == 0 ? 0 : satirlar.Max(s => s[i].Length));

        var sb = new StringBuilder();
        sb.Append(Hizala(basliklar, genislik));
        sb.Append('\n').Append(string.Join("  ", genislik.Select(g => new string('─', g))));
        foreach (string[] satir in satirlar)
            sb.Append('\n').Append(Hizala(satir, genislik));
        if (veri.Kolonlar.Count > kolonSayisi)
            sb.Append($"\n… +{veri.Kolonlar.Count - kolonSayisi} kolon daha");
        return sb.ToString();
    }

    private static string Hizala(string[] hucreler, int[] genislik)
        => string.Join("  ", hucreler.Select((h, i) => h.PadRight(genislik[i]))).TrimEnd();

    private static string Hucre(object? deger) => deger switch
    {
        null or DBNull => "NULL",
        DateTime t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => deger.ToString() ?? "",
    };

    private static string Kis(string metin, int azami)
    {
        metin = metin.ReplaceLineEndings(" ").Trim();
        return metin.Length <= azami ? metin : metin[..(azami - 1)] + "…";
    }
}
