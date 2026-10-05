using System.Text.Json;

namespace SQLST.Application;

/// <summary>
/// Harita kart konumlarının SAF serileştiricisi (v9-S3 kalıcılık): TamAd → konum sözlüğünü JSON'a
/// çevirir ve geri okur. Konumlar DB başına <see cref="Contracts.IAyarDeposu"/>'da saklanır. Bozuk
/// JSON'da boş döner (kullanıcı verisini kaybetmektense ızgaraya düşmek) — dar kapsamlı parse adaptörü.
/// </summary>
public static class HaritaKonumSerisi
{
    private sealed record Kayit(string A, double X, double Y);

    public static string Serile(IReadOnlyDictionary<string, Nokta> konumlar)
        => JsonSerializer.Serialize(konumlar.Select(kv => new Kayit(kv.Key, kv.Value.X, kv.Value.Y)));

    public static IReadOnlyDictionary<string, Nokta> Coz(string? json)
    {
        var sonuc = new Dictionary<string, Nokta>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return sonuc;
        try
        {
            foreach (Kayit k in JsonSerializer.Deserialize<List<Kayit>>(json) ?? [])
                sonuc[k.A] = new Nokta(k.X, k.Y);
        }
        catch (JsonException)
        {
            // bozuk/eski biçim → boş (çağıran ızgaraya düşer)
        }
        return sonuc;
    }
}
