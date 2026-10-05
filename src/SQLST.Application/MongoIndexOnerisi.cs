using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Bir index önerisi: hangi koleksiyona, hangi alanlar, neden.</summary>
public sealed record IndexOnerisi(
    string Koleksiyon,
    IReadOnlyList<string> Alanlar,
    int KacKez,
    long ToplamMs,
    string Komut);

/// <summary>
/// v20-S21 m.26 fikir 5 ("eksik index önerisi" — Yönetim Paneli): MongoDB'de SQL Server'ın
/// "missing index" DMV'si YOKTUR; tek dürüst kaynak <c>system.profile</c>'daki GERÇEK yavaş
/// sorgulardır. Bu sınıf saf kısımdır: COLLSCAN yapan kayıtları (koleksiyon + alan kümesi) bazında
/// gruplar, en çok acıtandan başlayarak <c>createIndex</c> komutu üretir. ÇALIŞTIRMAZ — öneri.
/// </summary>
public static class MongoIndexOnerisi
{
    /// <summary>Bileşik index'te makul alan tavanı — daha fazlası pratikte işe yaramaz/pahalıdır.</summary>
    public const int AzamiAlan = 4;

    public static IReadOnlyList<IndexOnerisi> Uret(IEnumerable<ProfilKaydi> kayitlar, int azamiOneri = 10)
    {
        var gruplar = new Dictionary<string, (string Koleksiyon, List<string> Alanlar, int Sayi, long Ms)>(
            StringComparer.Ordinal);

        foreach (ProfilKaydi k in kayitlar)
        {
            // Yalnız index KULLANMAYAN sorgular önerilir; index'li ama yavaş olan başka bir dert.
            if (!k.TaranmisMi || k.Koleksiyon.Length == 0)
                continue;

            // Eşitlik/aralık alanları önce, sıralama alanları sonra (ESR kuralının pratik hâli).
            List<string> alanlar =
            [
                .. k.FiltreAlanlari.Where(a => a.Length > 0).Distinct(StringComparer.Ordinal),
                .. k.SiralamaAlanlari.Where(a => a.Length > 0),
            ];
            alanlar = [.. alanlar.Distinct(StringComparer.Ordinal).Take(AzamiAlan)];
            if (alanlar.Count == 0)
                continue; // filtresiz tarama (tüm koleksiyonu okuyan sorgu) — index çözmez

            string anahtar = $"{k.Koleksiyon}|{string.Join(",", alanlar)}";
            if (gruplar.TryGetValue(anahtar, out var g))
                gruplar[anahtar] = (g.Koleksiyon, g.Alanlar, g.Sayi + 1, g.Ms + k.Milisaniye);
            else
                gruplar[anahtar] = (k.Koleksiyon, alanlar, 1, k.Milisaniye);
        }

        return [.. gruplar.Values
            .OrderByDescending(g => g.Ms)          // en çok zaman yiyen önce
            .ThenByDescending(g => g.Sayi)
            .Take(azamiOneri)
            .Select(g => new IndexOnerisi(g.Koleksiyon, g.Alanlar, g.Sayi, g.Ms,
                KomutYaz(g.Koleksiyon, g.Alanlar)))];
    }

    /// <summary>Çalıştırılabilir öneri metni — kullanıcı kopyalayıp kendi kontrolüyle koşar.</summary>
    public static string KomutYaz(string koleksiyon, IReadOnlyList<string> alanlar)
        => $"db.{koleksiyon}.createIndex({{ {string.Join(", ", alanlar.Select(a => $"{J(a)}: 1"))} }})";

    private static string J(string deger) => JsonSerializer.Serialize(deger);
}
