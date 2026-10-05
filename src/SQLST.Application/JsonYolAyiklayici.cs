using System.Text.Json;

namespace SQLST.Application;

/// <summary>
/// Basit JSON yol (path) ayıklayıcı (v20-S13, madde 12 istek zinciri + madde 14 alan çıkar/filtre) — SAF,
/// birim testli. Desteklenen yol: nokta ile alan (<c>a.b.c</c>), köşeli ile dizi indeksi (<c>a[0].b</c>,
/// kök dizi <c>[0]</c>), iç içe indeks (<c>a[0][1]</c>). Skaler değer metin olarak; nesne/dizi HAM JSON
/// olarak döner (madde 14 alt-ağaç çıkarımı). Yol bulunamazsa null + Türkçe <c>hata</c>. UI/IO yok.
/// </summary>
public static class JsonYolAyiklayici
{
    public static string? Ayikla(string? json, string? yol, out string? hata)
    {
        hata = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            hata = "Yanıt boş.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(yol))
        {
            hata = "JSON yol boş (ör. data.id ya da items[0].ad).";
            return null;
        }

        JsonDocument belge;
        try
        {
            belge = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            hata = $"Geçerli JSON değil: {ex.Message}";
            return null;
        }

        using (belge)
        {
            JsonElement mevcut = belge.RootElement;
            foreach ((string? ad, int? indeks) in YolSegmentleri(yol))
            {
                if (ad is not null)
                {
                    if (mevcut.ValueKind != JsonValueKind.Object
                        || !mevcut.TryGetProperty(ad, out JsonElement alt))
                    {
                        hata = $"Alan bulunamadı: {ad}";
                        return null;
                    }
                    mevcut = alt;
                }
                else if (indeks is int i)
                {
                    if (mevcut.ValueKind != JsonValueKind.Array || i < 0 || i >= mevcut.GetArrayLength())
                    {
                        hata = $"Dizi indeksi geçersiz: [{i}]";
                        return null;
                    }
                    mevcut = mevcut[i];
                }
            }
            return DegerMetni(mevcut);
        }
    }

    /// <summary>Yolu segmentlere böler: her segment ya alan adı (Ad) ya da dizi indeksidir (Indeks).</summary>
    private static IEnumerable<(string? Ad, int? Indeks)> YolSegmentleri(string yol)
    {
        foreach (string parca in yol.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = parca.Trim();
            int koseli = p.IndexOf('[');
            string ad = koseli >= 0 ? p[..koseli] : p;
            if (ad.Length > 0)
                yield return (ad, null);

            while (koseli >= 0)
            {
                int kapa = p.IndexOf(']', koseli);
                if (kapa < 0)
                    break;
                if (int.TryParse(p[(koseli + 1)..kapa], out int n))
                    yield return (null, n);
                koseli = p.IndexOf('[', kapa);
            }
        }
    }

    private static string DegerMetni(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => e.GetRawText(), // nesne/dizi → ham JSON (madde 14 alt-ağaç)
    };
}
