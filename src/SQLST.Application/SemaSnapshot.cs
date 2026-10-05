using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Bir veritabanı şemasının anlık görüntüsü (V2-S9, Ö5) — JSON'a serileşir.</summary>
public sealed record SemaSnapshotu(
    string Sunucu,
    string Veritabani,
    DateTime AlinmaUtc,
    IReadOnlyList<SemaNesnesi> Nesneler);

/// <summary>Snapshot dosya okuma/yazma — insan okuyabilir girintili JSON, Türkçe kaçışsız.</summary>
public static class SemaSnapshotYazici
{
    private static readonly JsonSerializerOptions Ayar = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Yaz(SemaSnapshotu snapshot) => JsonSerializer.Serialize(snapshot, Ayar);

    public static SemaSnapshotu? Oku(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SemaSnapshotu>(json, Ayar);
        }
        catch (JsonException)
        {
            return null; // bozuk/yabancı dosya — çağıran dürüst mesaj verir
        }
    }

    public static Task DosyayaYazAsync(SemaSnapshotu snapshot, string yol, CancellationToken ct = default)
        => File.WriteAllTextAsync(yol, Yaz(snapshot), new UTF8Encoding(false), ct);

    public static async Task<SemaSnapshotu?> DosyadanOkuAsync(string yol, CancellationToken ct = default)
        => Oku(await File.ReadAllTextAsync(yol, ct));
}

/// <summary>
/// Şema farkı (Ö5): eklendi / silindi / değişti. Kimlik = tür + şema.ad;
/// "değişti" = kolon ya da parametre imzası değişti (imza metni gösterilir).
/// </summary>
public static class SemaFarkAlici
{
    public enum DegisimTuru
    {
        Eklendi,
        Silindi,
        Degisti
    }

    public sealed record Fark(DegisimTuru Tur, SemaNesneTuru NesneTuru, string TamAd, string Detay);

    public static IReadOnlyList<Fark> Karsilastir(SemaSnapshotu eski, SemaSnapshotu yeni)
    {
        var farklar = new List<Fark>();
        Dictionary<string, SemaNesnesi> eskiler = Indexle(eski.Nesneler);
        Dictionary<string, SemaNesnesi> yeniler = Indexle(yeni.Nesneler);

        foreach ((string anahtar, SemaNesnesi nesne) in yeniler)
        {
            if (!eskiler.TryGetValue(anahtar, out SemaNesnesi? eskisi))
            {
                farklar.Add(new Fark(DegisimTuru.Eklendi, nesne.Tur, nesne.TamAd, Imza(nesne)));
                continue;
            }

            string eskiImza = Imza(eskisi);
            string yeniImza = Imza(nesne);
            if (eskiImza != yeniImza)
                farklar.Add(new Fark(DegisimTuru.Degisti, nesne.Tur, nesne.TamAd, $"{eskiImza}  →  {yeniImza}"));
        }

        foreach ((string anahtar, SemaNesnesi nesne) in eskiler)
        {
            if (!yeniler.ContainsKey(anahtar))
                farklar.Add(new Fark(DegisimTuru.Silindi, nesne.Tur, nesne.TamAd, Imza(nesne)));
        }

        return [.. farklar
            .OrderBy(f => f.Tur)
            .ThenBy(f => f.NesneTuru)
            .ThenBy(f => f.TamAd, StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, SemaNesnesi> Indexle(IReadOnlyList<SemaNesnesi> nesneler)
    {
        var index = new Dictionary<string, SemaNesnesi>(StringComparer.OrdinalIgnoreCase);
        foreach (SemaNesnesi n in nesneler)
            index[$"{n.Tur}|{n.TamAd}"] = n;
        return index;
    }

    /// <summary>Kıyas imzası: kolonlar (ad+tip+null) ya da parametreler (ad+tip+output) sıralı.</summary>
    internal static string Imza(SemaNesnesi nesne)
        => nesne.Kolonlar.Count > 0
            ? string.Join(", ", nesne.Kolonlar.Select(k => $"{k.Ad} {k.Tip}{(k.NullOlabilir ? " null" : "")}"))
            : nesne.Parametreler.Count > 0
                ? string.Join(", ", nesne.Parametreler.Select(p => $"{p.Ad} {p.Tip}{(p.CikisMi ? " OUTPUT" : "")}"))
                : "(imzasız)";
}
