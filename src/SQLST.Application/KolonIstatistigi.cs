using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// v20-S21 m.10 fikir 5 ("kolon istatistiği"): kolon başlığına sağ tık → MIN/MAX/AVG/SUM/DISTINCT/NULL
/// tek sorguda. Sorgu SIFIRDAN yazılmaz — çalışan sorgunun FROM+WHERE parçaları ORİJİNAL metinden
/// kesilir, yalnız SELECT listesi değiştirilir; böylece kullanıcının süzgeci (alias/JOIN/hint dahil)
/// aynen korunur ve istatistik gerçekten "ekranda gördüğün kümenin" istatistiği olur.
/// Gridde görünen 100.000 satırla değil, eşleşen TÜM satırlarla hesaplanır (bilinçli — sayı doğru olsun).
/// </summary>
public static class KolonIstatistigi
{
    public static (string? Sql, string? Not, string? Hata) SorguYaz(
        string sql, string kolonAd, bool sayisal, ILehce lehce)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (null, null, "İstatistik için çalışan bir sorgu yok.");

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        if (parser.Parse(new StringReader(sql), out IList<ParseError> hatalar) is not TSqlScript script
            || hatalar.Count > 0)
            return (null, null, "İstatistik: sorgu çözümlenemedi.");

        List<TSqlStatement> ifadeler = [.. script.Batches.SelectMany(b => b.Statements)];
        if (ifadeler.Count != 1 || ifadeler[0] is not SelectStatement select)
            return (null, null, "İstatistik TEK bir SELECT sorgusunda çalışır.");
        if (select.QueryExpression is not QuerySpecification spec)
            return (null, null, "İstatistik UNION'lu/parantezli sorgularda çalışmaz.");
        if (spec.FromClause is null)
            return (null, null, "Sorguda FROM yok — istatistiğin kaynağı belirsiz.");
        if (spec.GroupByClause is not null)
            return (null, null, "Gruplu (GROUP BY) sorguda kolon istatistiği yanıltıcı olurdu — çalışmaz.");

        string kolon = lehce.TirnaklaTanimlayici(kolonAd);
        string kaynak = Metin(sql, spec.FromClause)
            + (spec.WhereClause is null ? "" : " " + Metin(sql, spec.WhereClause));

        var secimler = new List<string>
        {
            $"COUNT_BIG(*) AS {lehce.TirnaklaTanimlayici("satır")}",
            $"COUNT_BIG({kolon}) AS {lehce.TirnaklaTanimlayici("dolu")}",
            $"COUNT_BIG(DISTINCT {kolon}) AS {lehce.TirnaklaTanimlayici("farklı")}",
            $"MIN({kolon}) AS {lehce.TirnaklaTanimlayici("en küçük")}",
            $"MAX({kolon}) AS {lehce.TirnaklaTanimlayici("en büyük")}",
        };
        if (sayisal)
        {
            // CAST: int kolonda AVG tam sayıya KESER, SUM ise taşabilir — ikisi de sessiz yanlış olurdu.
            secimler.Add($"AVG(CAST({kolon} AS decimal(38,6))) AS {lehce.TirnaklaTanimlayici("ortalama")}");
            secimler.Add($"SUM(CAST({kolon} AS decimal(38,6))) AS {lehce.TirnaklaTanimlayici("toplam")}");
        }

        string? not = spec.TopRowFilter is not null
            ? "Sorguda TOP var — istatistik TOP'suz, eşleşen TÜM satırlar üzerinden hesaplandı."
            : null;

        return ($"SELECT {string.Join(", ", secimler)} {kaynak};", not, null);
    }

    /// <summary>Tek satırlık sonucu "etiket → değer" listesine çevirir (NULL sayısı türetilir).</summary>
    public static IReadOnlyList<(string Etiket, string Deger)> Bicimle(ResultSetData veri)
    {
        var liste = new List<(string, string)>();
        if (veri.Satirlar.Count == 0)
            return liste;

        object?[] satir = veri.Satirlar[0];
        long? satirSayisi = null, doluSayisi = null;
        for (int i = 0; i < veri.Kolonlar.Count && i < satir.Length; i++)
        {
            string ad = veri.Kolonlar[i].Ad;
            object? deger = satir[i];
            if (ad == "satır")
                satirSayisi = Sayi(deger);
            if (ad == "dolu")
            {
                doluSayisi = Sayi(deger);
                continue; // "dolu" ham hâliyle gösterilmez — NULL sayısına dönüşür
            }
            liste.Add((ad, Metinlestir(deger)));
        }

        if (satirSayisi is { } toplam && doluSayisi is { } dolu)
            liste.Add(("NULL", $"{toplam - dolu:N0} / {toplam:N0}"));
        return liste;
    }

    private static long? Sayi(object? deger) => deger is null or DBNull
        ? null
        : Convert.ToInt64(deger, CultureInfo.InvariantCulture);

    private static string Metinlestir(object? deger) => deger switch
    {
        null or DBNull => "NULL",
        decimal d => d.ToString("N2", CultureInfo.CurrentCulture),
        double or float => Convert.ToDouble(deger, CultureInfo.InvariantCulture).ToString("N2", CultureInfo.CurrentCulture),
        long or int or short or byte => Convert.ToInt64(deger, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.CurrentCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
        _ => deger.ToString() ?? "",
    };

    private static string Metin(string sql, TSqlFragment parca)
        => sql.Substring(parca.StartOffset, parca.FragmentLength);
}
