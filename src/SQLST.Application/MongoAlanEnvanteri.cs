using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// v22-S2 (saha turu-2 m.2, İKİNCİ tur): Mongo koleksiyonunun ALAN ENVANTERİNİ ucuz çıkarır.
///
/// Neden: Log Analizi'nde koleksiyon seçilince alan listesi için <c>{"find": X, "limit": 50}</c>
/// koşuyordu — yani <b>50 TAM BELGE</b> ağdan geçiyordu. Kullanıcının QueryLog'u gibi mesaj/gövde
/// alanları KB'larca olan koleksiyonlarda bu tek başına megabaytlar demek (VPN arkasında dakikalar).
/// Oysa gereken yalnız <b>alan adları + tipleri</b>. <c>$objectToArray</c> + <c>$type</c> ile sunucu
/// sadece bunu döndürür; taşınan veri belge boyutundan bağımsız hâle gelir.
///
/// Bu sınıf saf kısımdır: sorgu metni + dönen JSON'un envantere çevrilmesi.
/// </summary>
public static class MongoAlanEnvanteri
{
    /// <summary>Örneklenecek belge sayısı — alan kümesi ilk birkaç belgede zaten oturur.</summary>
    public const int OrneklemBelge = 50;

    /// <summary>
    /// Alan adı + tipi sorgusu: her belgenin anahtarları <c>{k, t}</c> çiftlerine indirgenir; DEĞERLER
    /// taşınmaz. Sonuç tek kolonlu ("alanlar") ve satır başına küçük bir dizidir.
    /// </summary>
    public static string Sorgu(string koleksiyon, int enFazla = OrneklemBelge)
    {
        var pipeline = new List<object>
        {
            new Dictionary<string, object> { ["$limit"] = enFazla },
            new Dictionary<string, object>
            {
                ["$project"] = new Dictionary<string, object>
                {
                    ["_id"] = 0,
                    ["alanlar"] = new Dictionary<string, object>
                    {
                        ["$map"] = new Dictionary<string, object>
                        {
                            ["input"] = new Dictionary<string, object> { ["$objectToArray"] = "$$ROOT" },
                            ["as"] = "a",
                            ["in"] = new Dictionary<string, object>
                            {
                                ["k"] = "$$a.k",
                                ["t"] = new Dictionary<string, object> { ["$type"] = "$$a.v" },
                            },
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(
            new Dictionary<string, object> { ["aggregate"] = koleksiyon, ["pipeline"] = pipeline });
    }

    /// <summary>
    /// Dönen hücreleri (her biri <c>[{"k":"Zaman","t":"date"}, …]</c> JSON'u) envantere çevirir:
    /// alanlar İLK GÖRÜLME sırasında, tip olarak da ilk anlamlı (null/missing olmayan) tip.
    /// Bozuk/boş hücre atlanır — envanter bir kolaylıktır, ekranı çökertmez.
    /// </summary>
    public static IReadOnlyList<SemaKolonu> Coz(IEnumerable<object?> hucreler)
    {
        var sira = new List<string>();
        var tipler = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (object? hucre in hucreler)
        {
            if (hucre?.ToString() is not { Length: > 0 } json)
                continue;
            JsonElement kok;
            try
            {
                kok = JsonDocument.Parse(json).RootElement;
            }
            catch (JsonException)
            {
                continue; // beklenmedik biçim — bu satırı atla (envanter yine kurulur)
            }
            if (kok.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement alan in kok.EnumerateArray())
            {
                if (alan.ValueKind != JsonValueKind.Object
                    || !alan.TryGetProperty("k", out JsonElement ad) || ad.GetString() is not { Length: > 0 } k)
                    continue;
                string tip = alan.TryGetProperty("t", out JsonElement t) ? t.GetString() ?? "" : "";
                if (!tipler.ContainsKey(k))
                {
                    sira.Add(k);
                    tipler[k] = TipEsle(tip);
                }
                else if (tipler[k] is "" or "null" or "missing" && TipEsle(tip) is { Length: > 0 } yeni)
                {
                    tipler[k] = yeni; // ilk belgede null'dı, sonraki belgede gerçek tip göründü
                }
            }
        }

        return [.. sira.Select(k => new SemaKolonu(k, tipler[k], true, false))];
    }

    /// <summary>
    /// <c>$type</c> çıktısını uygulamanın kullandığı tip sözlüğüne çevirir. Kritik: sayısal tipler
    /// "int"/"long" gelir ama <see cref="MongoIdIzi"/> ve tip tahminleri "int32"/"int64" bekler —
    /// eşlenmezse ObjectId izi adayları ve zaman/mesaj tahminleri sessizce bozulur.
    /// </summary>
    private static string TipEsle(string tip) => tip switch
    {
        "int" => "int32",
        "long" => "int64",
        "decimal" => "decimal128",
        "bool" => "boolean",
        _ => tip,   // objectId · string · date · double · object · array · null · missing …
    };
}
