using System.Data;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Grid'den script üretimi (V2-S6): sonuç satırlarını INSERT'e, seçili hücreleri
/// IN listesine çevirir. Literaller <see cref="LiteralYazici"/> ile (invariant);
/// sonuç panoya gider ve insan gözden geçirir — bilinmeyen tip N'…' olarak düşer.
/// </summary>
public static class GridScriptleyici
{
    /// <summary>SQL Server tek INSERT'te en çok 1000 VALUES satırı kabul eder.</summary>
    private const int ValuesSiniri = 1000;

    /// <summary>
    /// Satırları çok satırlı INSERT'e çevirir; kaynak tablo adı sonuçtan bilinemez —
    /// kullanıcı [Tablo] yer tutucusunu düzenler (araç ipucunda söylenir).
    /// </summary>
    public static string InsertOlarak(DataTable tablo, IReadOnlyList<DataRow>? satirlar, string tabloAdi = "[Tablo]")
    {
        List<DataRow> kaynak = satirlar is { Count: > 0 } ? [.. satirlar] : [.. tablo.Rows.Cast<DataRow>()];
        if (kaynak.Count == 0)
            return "";

        string kolonlar = string.Join(", ",
            tablo.Columns.Cast<DataColumn>().Select(k => $"[{k.ColumnName.Replace("]", "]]")}]"));

        var sb = new StringBuilder();
        for (int bas = 0; bas < kaynak.Count; bas += ValuesSiniri)
        {
            sb.AppendLine($"INSERT INTO {tabloAdi} ({kolonlar}) VALUES");
            IEnumerable<string> degerler = kaynak.Skip(bas).Take(ValuesSiniri).Select(satir =>
                "    (" + string.Join(", ", satir.ItemArray.Select(GuvenliLiteral)) + ")");
            sb.AppendLine(string.Join("," + Environment.NewLine, degerler) + ";");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Seçili hücre değerlerinden IN listesi: tekilleştirilmiş; NULL'lar atlanır (IN NULL eşleşmez).</summary>
    public static string InListesi(IEnumerable<object?> degerler)
    {
        List<string> tekiller = [.. degerler
            .Where(d => d is not (null or DBNull))
            .Select(GuvenliLiteral)
            .Distinct()];
        return tekiller.Count == 0 ? "IN (NULL)" : $"IN ({string.Join(", ", tekiller)})";
    }

    /// <summary>Pano hedefli üretimde bilinmeyen tip akışı kırmasın — metin literaline düşer.</summary>
    private static string GuvenliLiteral(object? deger)
    {
        try
        {
            return LiteralYazici.Yaz(deger);
        }
        catch (NotSupportedException)
        {
            return "N'" + (deger?.ToString() ?? "").Replace("'", "''") + "'";
        }
    }
}
