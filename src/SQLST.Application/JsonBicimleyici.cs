using System.Text.Json;

namespace SQLST.Application;

/// <summary>
/// MongoDB sekmesi için JSON biçimlendirici (V3 — kullanıcı bulgusu 2026-07-18:
/// "Biçimlendir" Mongo'da T-SQL çözümleyicisine gidip söz dizimi hatası veriyordu).
/// Sorgu metni tek JSON belgesi olduğundan girintileme yeterli; ScriptDom kullanılmaz.
/// </summary>
public static class JsonBicimleyici
{
    private static readonly JsonSerializerOptions Ayar = new()
    {
        WriteIndented = true,
        // Türkçe karakterler ve Mongo operatörleri ($gt vb.) kaçışsız kalsın
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Girintili JSON döner; çözümlenemezse (null, hata) — metne dokunulmaz.</summary>
    public static (string? Sonuc, string? Hata) Bicimlendir(string metin)
    {
        if (string.IsNullOrWhiteSpace(metin))
            return (null, "Boş metin.");

        try
        {
            using JsonDocument belge = JsonDocument.Parse(metin, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return (JsonSerializer.Serialize(belge.RootElement, Ayar), null);
        }
        catch (JsonException ex)
        {
            return (null, $"Geçerli JSON değil (Satır {ex.LineNumber + 1}): {ex.Message}");
        }
    }
}
