using System.Text.Json;

namespace SQLST.Application;

/// <summary>AI'ın "tarifle → istek üret" (v20-S13) çıktısından çözülen HTTP isteği.</summary>
public sealed record AiUretilenIstek(
    string Metod,
    string Url,
    IReadOnlyList<AiBaslik> Basliklar,
    string? Govde);

/// <summary>Üretilen istekteki tek HTTP başlığı.</summary>
public sealed record AiBaslik(string Ad, string Deger);

/// <summary>
/// AI'ın "tarifle → REST isteği üret" cevabını (metod/url/basliklar/govde JSON'u) çözer (v20-S13). SAF;
/// birim testli. Yerel model çevre metin ya da <c>```json</c> çiti eklese bile İLK DENGELİ <c>{…}</c>
/// bloğu alınıp ayrıştırılır (metin içi tırnak/kaçış gözetilir). Eksik/yanlış alanlar bağışlanır:
/// metod yoksa GET, gövde yoksa null, başlıklar yoksa boş. URL yoksa <paramref name="hata"/> doldurulup
/// null dönülür — çağıran editörü doldurmaz, kullanıcıya net mesaj verir.
/// </summary>
public static class AiIstekAyristirici
{
    private static readonly HashSet<string> GecerliMetodlar =
        new(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], StringComparer.OrdinalIgnoreCase);

    public static AiUretilenIstek? Ayristir(string? aiCevabi, out string? hata)
    {
        hata = null;
        if (string.IsNullOrWhiteSpace(aiCevabi))
        {
            hata = "AI boş cevap verdi.";
            return null;
        }

        string? json = JsonMetinAraci.IlkDengeliNesne(aiCevabi);
        if (json is null)
        {
            hata = "AI cevabında JSON nesnesi bulunamadı.";
            return null;
        }

        JsonDocument belge;
        try
        {
            belge = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            hata = $"AI cevabı geçerli JSON değil: {ex.Message}";
            return null;
        }

        using (belge)
        {
            JsonElement kok = belge.RootElement;
            if (kok.ValueKind != JsonValueKind.Object)
            {
                hata = "AI cevabı bir JSON nesnesi değil.";
                return null;
            }

            string url = Metin(kok, "url");
            if (url.Length == 0)
            {
                hata = "AI bir URL üretmedi.";
                return null;
            }

            string metod = Metin(kok, "metod") is { Length: > 0 } m && GecerliMetodlar.Contains(m)
                ? m.ToUpperInvariant()
                : "GET";
            string? govde = Metin(kok, "govde") is { Length: > 0 } g ? g : null;

            var basliklar = new List<AiBaslik>();
            if (kok.TryGetProperty("basliklar", out JsonElement basEl) && basEl.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in basEl.EnumerateObject())
                {
                    string deger = p.Value.ValueKind == JsonValueKind.String
                        ? p.Value.GetString() ?? ""
                        : p.Value.GetRawText();
                    if (!string.IsNullOrWhiteSpace(p.Name))
                        basliklar.Add(new AiBaslik(p.Name, deger));
                }

            return new AiUretilenIstek(metod, url, basliklar, govde);
        }
    }

    private static string Metin(JsonElement kok, string ad)
        => kok.TryGetProperty(ad, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";
}
