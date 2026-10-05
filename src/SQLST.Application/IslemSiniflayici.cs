using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Denetim (v10) için işlem türü: "ne iş yapılmış".</summary>
public enum IslemTuru
{
    Select,
    Insert,
    Update,
    Delete,
    Ddl,   // CREATE/ALTER/DROP/TRUNCATE
    Exec,  // EXEC/EXECUTE/CALL
    Diger,
}

/// <summary>
/// Bir sorgunun işlem türünü SAF olarak sınıflandırır (v10-S1 denetim çekirdeği): denetim ekranı
/// "kim ne iş yapmış"ta türü gösterir/süzer. Yorum ve string sabitleri
/// (<see cref="YazmaSigortasi.YorumVeMetinleriAyikla"/>) ELENDİKTEN sonra <b>parantez derinliği 0</b>
/// olan İLK anahtar kelimeye bakar — böylece CTE'ye gizlenmiş DML doğru yakalanır
/// (<c>WITH x AS (SELECT…) INSERT…</c> → INSERT, CTE içindeki SELECT değil). MongoDB'de komut belgesinin
/// ilk anahtarından (find/insert/update/delete…) türetir.
/// </summary>
public static class IslemSiniflayici
{
    public static IslemTuru Sinifla(string sql, MotorTuru motor = MotorTuru.Mssql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return IslemTuru.Diger;
        if (motor == MotorTuru.Mongo)
            return MongoSinifla(sql);

        string temiz = YazmaSigortasi.YorumVeMetinleriAyikla(sql);
        foreach ((string kelime, int derinlik) in Kelimeler(temiz))
        {
            if (derinlik != 0)
                continue; // CTE/altsorgu gövdesi (parantez içi) — asıl işlem değil
            switch (kelime)
            {
                case "SELECT": return IslemTuru.Select;
                case "INSERT": return IslemTuru.Insert;
                case "UPDATE": return IslemTuru.Update;
                case "DELETE": return IslemTuru.Delete;
                case "MERGE": return IslemTuru.Update;   // upsert ~ güncelleme
                case "TRUNCATE" or "CREATE" or "ALTER" or "DROP": return IslemTuru.Ddl;
                case "EXEC" or "EXECUTE" or "CALL": return IslemTuru.Exec;
            }
        }
        return IslemTuru.Diger;
    }

    /// <summary>Türkçe etiket (denetim ekranı gösterimi).</summary>
    public static string Etiket(IslemTuru t) => t switch
    {
        IslemTuru.Select => "Okuma",
        IslemTuru.Insert => "Ekleme",
        IslemTuru.Update => "Güncelleme",
        IslemTuru.Delete => "Silme",
        IslemTuru.Ddl => "Yapı (DDL)",
        IslemTuru.Exec => "Yürütme",
        _ => "Diğer",
    };

    /// <summary>Temizlenmiş metni kelime kelime gezer; her kelime için (BÜYÜK kelime, başındaki paren derinliği).</summary>
    private static IEnumerable<(string Kelime, int Derinlik)> Kelimeler(string metin)
    {
        int derinlik = 0, i = 0, n = metin.Length;
        while (i < n)
        {
            char c = metin[i];
            if (c == '(') { derinlik++; i++; continue; }
            if (c == ')') { derinlik = Math.Max(0, derinlik - 1); i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                int bas = i;
                while (i < n && (char.IsLetterOrDigit(metin[i]) || metin[i] == '_'))
                    i++;
                yield return (metin[bas..i].ToUpperInvariant(), derinlik);
                continue;
            }
            i++;
        }
    }

    private static IslemTuru MongoSinifla(string json)
    {
        try
        {
            using JsonDocument belge = JsonDocument.Parse(
                json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (belge.RootElement.ValueKind != JsonValueKind.Object)
                return IslemTuru.Diger;

            foreach (JsonProperty p in belge.RootElement.EnumerateObject())
                return p.Name.ToLowerInvariant() switch
                {
                    "find" or "aggregate" or "count" or "distinct" => IslemTuru.Select,
                    "insert" => IslemTuru.Insert,
                    "update" or "findandmodify" => IslemTuru.Update,
                    "delete" => IslemTuru.Delete,
                    "create" or "createindexes" or "drop" or "dropindexes" or "dropdatabase" => IslemTuru.Ddl,
                    _ => IslemTuru.Diger,
                };
            return IslemTuru.Diger; // boş nesne
        }
        catch (JsonException)
        {
            return IslemTuru.Diger;
        }
    }
}
