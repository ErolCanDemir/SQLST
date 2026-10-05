using System.Globalization;
using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Geri Al paketinin serileştiricisi (V15-S3, saf): yakalanan eski satırları kültürden bağımsız
/// JSON'a çevirir; S4'te ters DML aynı temsillerden üretilir. LOB/rowversion kolonları pakete
/// ALINMAZ (BF-1 kararı — disk şişirir; rowversion zaten geri yazılamaz).
/// </summary>
public static class GeriAlSerilestirici
{
    /// <summary>Pakete alınmayan tipler: büyük ikili/metin + sunucu yönetimli sürüm damgası.</summary>
    private static readonly string[] DislananTipler = ["varbinary", "image", "text", "ntext", "xml", "rowversion", "timestamp"];

    private static readonly JsonSerializerOptions JsonAyar = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record KolonTanimi(string Ad, string Tip);

    public static bool KolonDislanir(string tipAdi)
        => DislananTipler.Contains(tipAdi, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Yakalanan sonuç setinden paket kurar. Dışlanan kolonlar hem listeden hem satır
    /// hücrelerinden düşülür — ters INSERT'te bu kolonlar hiç geçmez (NULL/varsayılan kalır).
    /// </summary>
    public static GeriAlPaketi PaketKur(
        string sunucu, string veritabani, GeriAlAdayi aday, string sqlMetni,
        ResultSetData eskiHal, IReadOnlyList<string> pkKolonlari, DateTime tarihUtc)
    {
        int[] alinan = [.. Enumerable.Range(0, eskiHal.Kolonlar.Count)
            .Where(i => !KolonDislanir(eskiHal.Kolonlar[i].TipAdi))];

        List<KolonTanimi> kolonlar = [.. alinan.Select(i =>
            new KolonTanimi(eskiHal.Kolonlar[i].Ad, eskiHal.Kolonlar[i].TipAdi))];

        List<string?[]> satirlar = [.. eskiHal.Satirlar.Select(satir =>
        {
            var hucreler = new string?[alinan.Length];
            for (int k = 0; k < alinan.Length; k++)
                hucreler[k] = HucreYaz(satir[alinan[k]]);
            return hucreler;
        })];

        return new GeriAlPaketi
        {
            TarihUtc = tarihUtc,
            Sunucu = sunucu,
            Veritabani = veritabani,
            Tablo = aday.Tablo,
            Fiil = aday.Fiil,
            SqlMetni = sqlMetni,
            SatirSayisi = satirlar.Count,
            KolonlarJson = JsonSerializer.Serialize(kolonlar, JsonAyar),
            PkJson = JsonSerializer.Serialize(pkKolonlari, JsonAyar),
            SatirlarJson = JsonSerializer.Serialize(satirlar, JsonAyar),
        };
    }

    public static IReadOnlyList<KolonTanimi> KolonlariOku(GeriAlPaketi paket)
        => JsonSerializer.Deserialize<List<KolonTanimi>>(paket.KolonlarJson, JsonAyar) ?? [];

    public static IReadOnlyList<string> PkOku(GeriAlPaketi paket)
        => JsonSerializer.Deserialize<List<string>>(paket.PkJson, JsonAyar) ?? [];

    public static IReadOnlyList<string?[]> SatirlariOku(GeriAlPaketi paket)
        => JsonSerializer.Deserialize<List<string?[]>>(paket.SatirlarJson, JsonAyar) ?? [];

    /// <summary>Hücre → kültürden bağımsız string; null → null. Ters DML literali bunlardan üretilir.</summary>
    public static string? HucreYaz(object? deger) => deger switch
    {
        null or DBNull => null,
        DateTime t => t.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        byte[] b => Convert.ToBase64String(b),
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => deger.ToString(),
    };
}
