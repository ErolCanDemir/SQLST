using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SQLST.Application;

/// <summary>
/// v20-S21 saha m.21 — REST Body yardımcısı: anahtar/değer satırları ⇄ JSON gövde.
/// Kullanıcı tabloya yazar → düz JSON nesnesi üretilir (sayı/true/false/null otomatik tiplenir,
/// hücreye JSON nesne/dizi yazılmışsa aynen gömülür, {{Değişken}} metin kalır); kullanıcı JSON'u
/// elle düzenlerse düz nesne olduğu sürece tabloya geri çözülür. İç içe/karmaşık JSON'da tablo
/// tarafı null döner — editör esastır, veri kaybolmaz.
/// </summary>
public static class RestGovdeDonusturucu
{
    private static readonly JsonSerializerOptions YazimSecenekleri = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Türkçe karakterler \u ile bozulmasın
    };

    /// <summary>Anahtar/değer satırlarından girintili JSON nesnesi üretir (boş anahtarlar atlanır).</summary>
    public static string KvdenJson(IEnumerable<(string Anahtar, string Deger)> satirlar)
    {
        var kok = new JsonObject();
        foreach ((string anahtar, string deger) in satirlar)
        {
            if (string.IsNullOrWhiteSpace(anahtar))
                continue;
            kok[anahtar.Trim()] = DegerYorumla(deger);
        }
        return kok.ToJsonString(YazimSecenekleri);
    }

    /// <summary>
    /// JSON gövdeyi tabloya çözer. Düz nesne değilse (dizi/skaler/iç içe olsa da her değer
    /// hücrede JSON metni olarak taşınabilir) — parse edilemiyorsa ya da kök nesne değilse
    /// <c>null</c> döner: tabloya dokunulmaz, editör esastır. Boş metin boş liste döner.
    /// </summary>
    public static IReadOnlyList<(string Anahtar, string Deger)>? JsondanKv(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        JsonNode? kok;
        try
        {
            kok = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null; // yarım yazılmış / {{değişkenli}} gövde — tabloya karışma
        }

        if (kok is not JsonObject nesne)
            return null;

        var liste = new List<(string, string)>(nesne.Count);
        foreach ((string ad, JsonNode? deger) in nesne)
            liste.Add((ad, deger switch
            {
                null => "null",
                JsonValue v => v.ToString(),      // "abc" → abc · 42 → 42 · true → true
                _ => deger.ToJsonString(),        // iç içe nesne/dizi hücrede JSON metni olarak durur
            }));
        return liste;
    }

    /// <summary>Hücre metnini JSON değerine tipler — SIRA ÖNEMLİ: null/bool/sayı/JSON parçası/metin.
    /// "{{Ad}}" değişken yer tutucusu JSON parçası sanılmaz (metin kalır, gönderimde çözülür).</summary>
    private static JsonNode? DegerYorumla(string? deger)
    {
        if (deger is null || deger.Length == 0)
            return "";
        string t = deger.Trim();
        if (t == "null")
            return null;
        if (t == "true")
            return true;
        if (t == "false")
            return false;
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long tam))
            return tam;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double ondalik))
            return ondalik;
        if ((t.StartsWith('{') && !t.StartsWith("{{")) || t.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(t); // hücreye yazılmış JSON nesne/dizi aynen gömülür
            }
            catch (JsonException)
            {
                // JSON değilmiş — metin olarak devam
            }
        }
        return deger;
    }
}
