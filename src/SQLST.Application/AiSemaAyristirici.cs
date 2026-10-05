using System.Text.Json;

namespace SQLST.Application;

/// <summary>AI'ın "CREATE TABLE öner" (v20-S13, madde 6) çıktısındaki tek kolon önerisi.</summary>
public sealed record AiSemaKolonu(string Ad, string Tip, bool Pk, bool NullOlabilir);

/// <summary>AI'ın önerdiği tablo şeması: opsiyonel tablo adı + kolon önerileri.</summary>
public sealed record AiSema(string? TabloAdi, IReadOnlyList<AiSemaKolonu> Kolonlar);

/// <summary>
/// AI'ın "yanıttan CREATE TABLE öner" cevabını çözer (v20-S13, madde 6). SAF; birim testli. Yerel model
/// önsöz/<c>```json</c> çiti eklese bile İLK DENGELİ <c>{…}</c> bloğu (<see cref="JsonMetinAraci"/>) alınıp
/// ayrıştırılır. Her kolon: <c>ad</c> (mevcut kolon adı — DEĞİŞTİRİLMEZ, eşleme buna göre), <c>tip</c>
/// (motor SQL tipi), <c>pk</c>, <c>null</c>. Adı boş kolon atlanır; hiç geçerli kolon yoksa
/// <paramref name="hata"/> doldurulur. <c>pk</c>/<c>null</c> bool değilse güvenli varsayılan (pk=false,
/// null=true). Çağıran öneriyi MEVCUT kolonlara ADA GÖRE eşler (yeni ad/kolon uydurulmuşsa yok sayılır).
/// </summary>
public static class AiSemaAyristirici
{
    public static AiSema? Ayristir(string? aiCevabi, out string? hata)
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
            if (kok.ValueKind != JsonValueKind.Object
                || !kok.TryGetProperty("kolonlar", out JsonElement kolonlarEl)
                || kolonlarEl.ValueKind != JsonValueKind.Array)
            {
                hata = "AI cevabında 'kolonlar' dizisi yok.";
                return null;
            }

            var kolonlar = new List<AiSemaKolonu>();
            foreach (JsonElement k in kolonlarEl.EnumerateArray())
            {
                if (k.ValueKind != JsonValueKind.Object)
                    continue;
                string ad = Metin(k, "ad");
                string tip = Metin(k, "tip");
                if (ad.Length == 0 || tip.Length == 0)
                    continue; // ad/tip olmadan öneri uygulanamaz
                kolonlar.Add(new AiSemaKolonu(ad, tip, Bayrak(k, "pk", false), Bayrak(k, "null", true)));
            }

            if (kolonlar.Count == 0)
            {
                hata = "AI hiçbir geçerli kolon önerisi vermedi.";
                return null;
            }

            string tabloAdi = Metin(kok, "tabloAdi");
            return new AiSema(tabloAdi.Length > 0 ? tabloAdi : null, kolonlar);
        }
    }

    private static string Metin(JsonElement kok, string ad)
        => kok.TryGetProperty(ad, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static bool Bayrak(JsonElement kok, string ad, bool varsayilan)
    {
        if (!kok.TryGetProperty(ad, out JsonElement el))
            return varsayilan;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            // Model bazen "true"/"1"/"evet" gibi string döndürür — bağışla.
            JsonValueKind.String => el.GetString()?.Trim().ToLowerInvariant() is "true" or "1" or "evet" or "yes",
            JsonValueKind.Number => el.TryGetInt64(out long n) && n != 0,
            _ => varsayilan,
        };
    }
}
