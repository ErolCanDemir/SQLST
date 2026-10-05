using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Hızlı filtre ilişkisi (grid hücresinden sağ tık).</summary>
public enum FiltreIliskisi
{
    Esit,
    EsitDegil,
    BuyukEsit,
    KucukEsit,
}

/// <summary>
/// v20-S21 m.10 fikir 3 ("hücreden hızlı filtre"): gridde bir hücreye sağ tıklayınca sorgunun
/// WHERE'ine koşul EKLER — kullanıcı editöre dönüp elle WHERE yazmaz. SQL yeniden ÜRETİLMEZ;
/// ScriptDom yalnız NEREYE ekleneceğini söyler, metin orijinalinden kesilip yapıştırılır
/// (alias/hint/biçim/yorumlar bozulmaz — m.28'in dersi). Koşul WHERE'in sonuna AND'lenir; WHERE
/// yoksa FROM'un hemen ardına yazılır — böylece GROUP BY/ORDER BY'ın ÖNÜNDE kalır.
/// T-SQL'e özgüdür (ScriptDom); çağıran yalnız MSSQL'de gösterir.
/// </summary>
public static class HizliFiltre
{
    public static (string? Sql, string? Hata) Uygula(
        string sql, string kolonAd, FiltreIliskisi iliski, object? deger, ILehce lehce)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (null, "Filtrelenecek sorgu yok.");
        if (string.IsNullOrWhiteSpace(kolonAd))
            return (null, "Kolon adı çözülemedi.");

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        if (parser.Parse(new StringReader(sql), out IList<ParseError> hatalar) is not TSqlScript script
            || hatalar.Count > 0)
            return (null, hatalar.Count > 0
                ? $"Hızlı filtre: sorgu çözümlenemedi (Satır {hatalar[0].Line}): {hatalar[0].Message}"
                : "Hızlı filtre: sorgu çözümlenemedi.");

        List<TSqlStatement> ifadeler = [.. script.Batches.SelectMany(b => b.Statements)];
        if (ifadeler.Count != 1 || ifadeler[0] is not SelectStatement select)
            return (null, "Hızlı filtre TEK bir SELECT sorgusunda çalışır.");
        if (select.QueryExpression is not QuerySpecification spec)
            return (null, "Hızlı filtre UNION'lu/parantezli sorgularda çalışmaz — koşulu elle ekleyin.");
        if (spec.FromClause is null)
            return (null, "Sorguda FROM yok — filtrelenecek kaynak belirsiz.");

        string kosul = KosulYaz(lehce, kolonAd, iliski, deger);

        // Ekleme noktası: WHERE varsa sonuna " AND …", yoksa FROM'un sonuna " WHERE …".
        // (GROUP BY/HAVING/ORDER BY her iki durumda da EKLEMENİN SONRASINDA kalır.)
        TSqlFragment capa = spec.WhereClause ?? (TSqlFragment)spec.FromClause;
        int nokta = capa.StartOffset + capa.FragmentLength;
        string ek = spec.WhereClause is null ? $" WHERE {kosul}" : $" AND {kosul}";

        return (sql[..nokta] + ek + sql[nokta..], null);
    }

    private static string KosulYaz(ILehce lehce, string kolonAd, FiltreIliskisi iliski, object? deger)
    {
        string kolon = lehce.TirnaklaTanimlayici(kolonAd);

        // NULL'da eşitlik kurulamaz (NULL = NULL yanlıştır) — IS [NOT] NULL'a çevrilir.
        if (deger is null or DBNull)
            return iliski == FiltreIliskisi.EsitDegil ? $"{kolon} IS NOT NULL" : $"{kolon} IS NULL";

        string literal = LiteralYazici.Yaz(deger, lehce.LiteralKurallari);
        string islec = iliski switch
        {
            FiltreIliskisi.EsitDegil => "<>",
            FiltreIliskisi.BuyukEsit => ">=",
            FiltreIliskisi.KucukEsit => "<=",
            _ => "=",
        };
        return $"{kolon} {islec} {literal}";
    }

    /// <summary>≥ / ≤ yalnız sıralanabilir tiplerde anlamlı (metin/bool/guid'de menüde gizlenir).</summary>
    public static bool SiralanabilirMi(object? deger) => deger is
        sbyte or byte or short or ushort or int or uint or long or ulong
        or float or double or decimal or DateTime or DateTimeOffset or TimeSpan;
}
