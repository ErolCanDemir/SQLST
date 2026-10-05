using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLST.Application;

/// <summary>Yakalama adayı: eski hali çekecek SELECT + hedefin PK kolonlarını soran sorgu.</summary>
public sealed record GeriAlAdayi(string Fiil, string Tablo, string YakalamaSql, string PkSql);

/// <summary>
/// Geri Al yakalayıcı (V15-S3, BF-1): Güvenli Yazma'daki DML çalışmadan ÖNCE etkilenecek
/// satırların eski halini çekecek SELECT'i üretir. BİLİNÇLİ MVP SINIRLARI (BF-1 kararları):
/// tek ifadeli, CTE'siz, FROM/JOIN'süz UPDATE/DELETE (hedef doğrudan tablo) — aksi halde
/// null döner ve paket alınmaz (sorgu yine normal çalışır, kimse engellenmez).
/// Satır sınırı: <see cref="SatirSiniri"/> üstü paket ALINMAZ (SELECT TOP sınır+1 ile anlaşılır).
/// </summary>
public static class GeriAlYakalayici
{
    public const int SatirSiniri = 50_000;

    public static GeriAlAdayi? Uret(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        if (parser.Parse(new StringReader(sql), out IList<ParseError> hatalar) is not TSqlScript script
            || hatalar.Count > 0)
            return null;

        List<TSqlStatement> ifadeler = [.. script.Batches.SelectMany(b => b.Statements)];
        if (ifadeler.Count != 1)
            return null;

        (string fiil, UpdateDeleteSpecificationBase spec) = ifadeler[0] switch
        {
            UpdateStatement { WithCtesAndXmlNamespaces: null } u => ("UPDATE", (UpdateDeleteSpecificationBase)u.UpdateSpecification),
            DeleteStatement { WithCtesAndXmlNamespaces: null } d => ("DELETE", d.DeleteSpecification),
            _ => ("", null!),
        };
        if (spec is null || spec.FromClause is not null || spec.Target is not NamedTableReference hedefRef)
            return null;

        string tablo = string.Join(".", hedefRef.SchemaObject.Identifiers.Select(i => i.Value));
        string hedefMetni = sql.Substring(hedefRef.StartOffset, hedefRef.FragmentLength);
        string whereMetni = spec.WhereClause is null
            ? "" : " " + sql.Substring(spec.WhereClause.StartOffset, spec.WhereClause.FragmentLength);

        // Sınır+1 çekilir: sonuç sınırı aşarsa "paket alınamadı (sınır)" kararı çağırana kalır.
        string yakalama = $"SELECT TOP ({SatirSiniri + 1}) * FROM {hedefMetni}{whereMetni};";

        string pkSql = $"""
            SELECT c.name FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(N'{tablo.Replace("'", "''")}')
            ORDER BY ic.key_ordinal;
            """;

        return new GeriAlAdayi(fiil, tablo, yakalama, pkSql);
    }
}
