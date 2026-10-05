using System.Globalization;

namespace SQLST.Application;

/// <summary>Bir karşılaştırma satırının durumu (v20-S13 "Yanıt ↔ DB karşılaştır").</summary>
public enum KarsilastirmaDurumu
{
    /// <summary>Anahtar yalnız API yanıtında var (DB'de yok).</summary>
    YalnizApi,
    /// <summary>Anahtar yalnız DB sonucunda var (API yanıtında yok).</summary>
    YalnizDb,
    /// <summary>İki tarafta da var, ORTAK kolonların hepsi eşit.</summary>
    Esit,
    /// <summary>İki tarafta da var ama en az bir ortak kolon farklı.</summary>
    Farkli,
}

/// <summary>Tek anahtar için karşılaştırma sonucu; <see cref="FarkliKolonlar"/> yalnız <see cref="KarsilastirmaDurumu.Farkli"/>'da dolu.</summary>
public sealed record KarsilastirmaSatiri(
    string Anahtar,
    KarsilastirmaDurumu Durum,
    IReadOnlyList<string> FarkliKolonlar);

/// <summary>Tüm karşılaştırmanın sonucu + özet sayaçlar. <see cref="Hata"/> doluysa geçersiz girdi (anahtar kolon yok vb.).</summary>
public sealed record KarsilastirmaSonucu(
    IReadOnlyList<KarsilastirmaSatiri> Satirlar,
    IReadOnlyList<string> OrtakKolonlar,
    int YalnizApi,
    int YalnizDb,
    int Esit,
    int Farkli)
{
    public string? Hata { get; init; }

    public static KarsilastirmaSonucu HataliSonuc(string hata) =>
        new([], [], 0, 0, 0, 0) { Hata = hata };
}

/// <summary>
/// "Yanıt ↔ DB karşılaştır" (v20-S13): bir API yanıtından (JSON → kolon+satır) ve bir SQL sonucundan
/// gelen iki tablo kümesini ANAHTAR KOLONDA eşler; her anahtarı yalnız-API / yalnız-DB / eşit / farklı
/// olarak sınıflar (ortak kolonların değerini karşılaştırarak). SAF; UI/IO yok, birim testli. Değer
/// karşılaştırması Invariant metin üzerinden Ordinal'dir — tam sayılar güvenle eşleşir (JSON tam sayısı
/// long kalır, madde 1 düzeltmesi); ondalık/tarih BİÇİM farkı yanlış "farklı" verebilir (ilk sürüm sınırı).
/// Anahtar/kolon adı eşleşmesi büyük/küçük harf DUYARSIZ; yinelenen anahtarda İLK satır esas alınır.
/// </summary>
public static class YanitDbKarsilastirici
{
    public static KarsilastirmaSonucu Karsilastir(
        IReadOnlyList<string> apiKolonlar, IReadOnlyList<object?[]> apiSatirlar,
        IReadOnlyList<string> dbKolonlar, IReadOnlyList<object?[]> dbSatirlar,
        string anahtarKolon)
    {
        if (string.IsNullOrWhiteSpace(anahtarKolon))
            return KarsilastirmaSonucu.HataliSonuc("Anahtar kolon boş — hangi kolonda eşleşeceği belirtilmeli.");

        int apiAnahtarIdx = KolonIndeksi(apiKolonlar, anahtarKolon);
        int dbAnahtarIdx = KolonIndeksi(dbKolonlar, anahtarKolon);
        if (apiAnahtarIdx < 0)
            return KarsilastirmaSonucu.HataliSonuc($"Anahtar kolon API yanıtında yok: {anahtarKolon}");
        if (dbAnahtarIdx < 0)
            return KarsilastirmaSonucu.HataliSonuc($"Anahtar kolon DB sonucunda yok: {anahtarKolon}");

        // Ortak kolonlar (anahtar hariç), API sırasında; iki tarafta da bulunanlar.
        var dbKolonSet = new HashSet<string>(dbKolonlar, StringComparer.OrdinalIgnoreCase);
        List<string> ortakKolonlar = [.. apiKolonlar.Where(k =>
            !k.Equals(anahtarKolon, StringComparison.OrdinalIgnoreCase) && dbKolonSet.Contains(k))];

        // Ortak kolonların iki taraftaki indeksleri (tekrar tekrar aramamak için).
        (int api, int db)[] ortakIdx = [.. ortakKolonlar.Select(k =>
            (KolonIndeksi(apiKolonlar, k), KolonIndeksi(dbKolonlar, k)))];

        Dictionary<string, object?[]> apiHarita = AnahtarHaritasi(apiSatirlar, apiAnahtarIdx);
        Dictionary<string, object?[]> dbHarita = AnahtarHaritasi(dbSatirlar, dbAnahtarIdx);

        // Anahtar birleşimi: önce API sırası, sonra yalnız-DB olanlar (kararlı çıktı).
        var tumAnahtarlar = new List<string>();
        var gorulen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in apiHarita.Keys) if (gorulen.Add(k)) tumAnahtarlar.Add(k);
        foreach (string k in dbHarita.Keys) if (gorulen.Add(k)) tumAnahtarlar.Add(k);

        var satirlar = new List<KarsilastirmaSatiri>(tumAnahtarlar.Count);
        int yApi = 0, yDb = 0, esit = 0, farkli = 0;

        foreach (string anahtar in tumAnahtarlar)
        {
            bool apiVar = apiHarita.TryGetValue(anahtar, out object?[]? apiSatir);
            bool dbVar = dbHarita.TryGetValue(anahtar, out object?[]? dbSatir);

            if (apiVar && !dbVar)
            {
                satirlar.Add(new(anahtar, KarsilastirmaDurumu.YalnizApi, []));
                yApi++;
            }
            else if (!apiVar && dbVar)
            {
                satirlar.Add(new(anahtar, KarsilastirmaDurumu.YalnizDb, []));
                yDb++;
            }
            else
            {
                var farkliKolonlar = new List<string>();
                for (int i = 0; i < ortakKolonlar.Count; i++)
                {
                    string a = HucreMetni(apiSatir!, ortakIdx[i].api);
                    string b = HucreMetni(dbSatir!, ortakIdx[i].db);
                    if (!a.Equals(b, StringComparison.Ordinal))
                        farkliKolonlar.Add(ortakKolonlar[i]);
                }
                if (farkliKolonlar.Count == 0)
                {
                    satirlar.Add(new(anahtar, KarsilastirmaDurumu.Esit, []));
                    esit++;
                }
                else
                {
                    satirlar.Add(new(anahtar, KarsilastirmaDurumu.Farkli, farkliKolonlar));
                    farkli++;
                }
            }
        }

        return new(satirlar, ortakKolonlar, yApi, yDb, esit, farkli);
    }

    private static int KolonIndeksi(IReadOnlyList<string> kolonlar, string ad)
    {
        for (int i = 0; i < kolonlar.Count; i++)
            if (kolonlar[i].Equals(ad, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static Dictionary<string, object?[]> AnahtarHaritasi(IReadOnlyList<object?[]> satirlar, int anahtarIdx)
    {
        var harita = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        foreach (object?[] satir in satirlar)
            harita.TryAdd(HucreMetni(satir, anahtarIdx), satir); // yinelenen anahtar → ilk oluşum kazanır
        return harita;
    }

    private static string HucreMetni(object?[] satir, int idx)
    {
        if (idx < 0 || idx >= satir.Length)
            return "";
        object? deger = satir[idx];
        return deger is null or DBNull ? "" : Convert.ToString(deger, CultureInfo.InvariantCulture) ?? "";
    }
}
