using System.Text.Json;

namespace SQLST.Application;

/// <summary>
/// JSON yanıtını TABLOYA (kolon + satır) düzleştirir (REST İstemcisi v20-S8 "Yanıt → grid"). Kök bir
/// DİZİ ise doğrudan; kök bir NESNE ise ilk dizi-değerli alan (ör. <c>{"toplam":1,"firmalar":[…]}</c> →
/// <c>firmalar</c>) satır kaynağıdır; dizi yoksa nesnenin kendisi tek satırdır. Kolonlar tüm elemanların
/// alan adlarının BİRLEŞİMİdir (ilk görülme sırası). Skaler hücreler düz metin; iç nesne/dizi kompakt
/// JSON olarak yazılır. SAF (UI yok) — pencere bunu DataTable'a çevirir.
/// </summary>
public static class JsonTabloCikar
{
    public sealed record Tablo(IReadOnlyList<string> Kolonlar, IReadOnlyList<IReadOnlyList<string?>> Satirlar);

    /// <summary>JSON metnini tabloya düzleştirir. Ayrıştırılamazsa/uygun değilse boş tablo döner.</summary>
    public static Tablo Coz(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Tablo([], []);
        try
        {
            using JsonDocument belge = JsonDocument.Parse(json);
            JsonElement kok = belge.RootElement;

            JsonElement dizi = kok.ValueKind switch
            {
                JsonValueKind.Array => kok,
                JsonValueKind.Object => IlkDizi(kok) ?? default,
                _ => default,
            };

            // Kök nesne ama içinde dizi yoksa → nesnenin kendisi tek satır.
            if (kok.ValueKind == JsonValueKind.Object && dizi.ValueKind != JsonValueKind.Array)
                return NesneTablosu(kok);

            if (dizi.ValueKind != JsonValueKind.Array)
                return new Tablo([], []); // skaler ya da boş

            return DiziTablosu(dizi);
        }
        catch (JsonException)
        {
            return new Tablo([], []);
        }
    }

    private static JsonElement? IlkDizi(JsonElement nesne)
    {
        foreach (JsonProperty p in nesne.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array)
                return p.Value;
        return null;
    }

    private static Tablo NesneTablosu(JsonElement nesne)
    {
        var kolonlar = new List<string>();
        var satir = new List<string?>();
        foreach (JsonProperty p in nesne.EnumerateObject())
        {
            kolonlar.Add(p.Name);
            satir.Add(Hucre(p.Value));
        }
        return new Tablo(kolonlar, [satir]);
    }

    private static Tablo DiziTablosu(JsonElement dizi)
    {
        var kolonlar = new List<string>();
        var kolonKume = new HashSet<string>(StringComparer.Ordinal);
        var satirlar = new List<IReadOnlyList<string?>>();

        // Nesne elemanları → kolon birleşimi; skaler elemanlar → tek "değer" kolonu.
        bool skalerVar = false;
        foreach (JsonElement e in dizi.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in e.EnumerateObject())
                    if (kolonKume.Add(p.Name))
                        kolonlar.Add(p.Name);
            else
                skalerVar = true;

        if (kolonlar.Count == 0 && skalerVar)
            kolonlar.Add("değer");

        foreach (JsonElement e in dizi.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var satir = new List<string?>(kolonlar.Count);
                foreach (string k in kolonlar)
                    satir.Add(e.TryGetProperty(k, out JsonElement v) ? Hucre(v) : null);
                satirlar.Add(satir);
            }
            else
            {
                // skaler eleman: ilk kolona yaz, kalanı boş.
                var satir = new List<string?>(kolonlar.Count) { Hucre(e) };
                while (satir.Count < kolonlar.Count) satir.Add(null);
                satirlar.Add(satir);
            }
        }
        return new Tablo(kolonlar, satirlar);
    }

    /// <summary>Bir JSON değerini hücre metnine çevirir: skaler düz; iç nesne/dizi kompakt JSON; null → null.</summary>
    private static string? Hucre(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => v.GetRawText(),
        _ => v.GetRawText(), // nesne/dizi → kompakt ham JSON
    };
}
