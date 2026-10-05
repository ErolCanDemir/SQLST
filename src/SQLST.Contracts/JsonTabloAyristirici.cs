using System.Globalization;
using System.Text.Json;

namespace SQLST.Contracts;

/// <summary>
/// JSON yanıtını (REST istemcisi) tablo önizlemesine çevirir (v20-S12) — SAF; dosya okuyucunun
/// (DosyaOkuyucu) JSON kardeşi. Girdi bir NESNE DİZİSİ (<c>[{…},{…}]</c>) ya da tek nesnedir
/// (<c>{…}</c> → tek elemanlı diziye sarılır); üst düzey skaler alanlar (string/number/bool/null)
/// KOLON olur, iç içe nesne/dizi alanları ham JSON metnine serileştirilip metin kolona düşer (ilk
/// sürüm sınırı — böylece motor-farkında CREATE+INSERT hattı Excel/TXT ile AYNEN paylaşılır).
/// Skaler dizi (<c>[1,2,3]</c>) tek "deger" kolonuna sarılır. Değerler .NET tipiyle taşınır
/// (Excel gibi: tam sayı long, ondalık double, bool; tarih/metin string) ve tip tahmini
/// <see cref="KolonTipiTahminci"/> ile Invariant kültürde yapılır (JSON sayıları '.' ondalıklıdır).
/// Kolon adları TÜM nesnelerdeki anahtarların birleşimidir (ilk görülme sırasında); bir nesnede
/// olmayan alan o satırda null'dır. UI/IO yok — birim testli.
/// </summary>
public static class JsonTabloAyristirici
{
    /// <summary>
    /// JSON metnini kolon envanteri + satırlara çevirir. Boş/geçersiz/desteklenmeyen girdilerde null
    /// döner ve <paramref name="hata"/>'yı Türkçe doldurur — çağıran kullanıcıya net mesaj verir, çökmez.
    /// </summary>
    public static DosyaOnizleme? Ayristir(string? json, out string? hata)
    {
        hata = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            hata = "Yanıt boş — tabloya kaydedilecek JSON yok.";
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
            JsonElement kok = belge.RootElement;

            // Tek nesne → tek elemanlı dizi; nesne dizisi olduğu gibi; skaler dizi tek kolona sarılır.
            List<JsonElement> nesneler;
            switch (kok.ValueKind)
            {
                case JsonValueKind.Object:
                    nesneler = [kok];
                    break;
                case JsonValueKind.Array:
                    nesneler = [.. kok.EnumerateArray()];
                    if (nesneler.Count == 0)
                    {
                        hata = "JSON dizisi boş — kaydedilecek satır yok.";
                        return null;
                    }
                    if (nesneler.Any(e => e.ValueKind != JsonValueKind.Object))
                        return SkalerDizi(nesneler); // [1,2,3] / ["a","b"] → tek "deger" kolonu
                    break;
                default:
                    hata = "JSON bir nesne ya da nesne dizisi olmalı (tabloya kaydetmek için).";
                    return null;
            }

            // Kolon adları: tüm nesnelerdeki anahtarların BİRLEŞİMİ, ilk görülme sırasında (satırlar
            // arasında farklı alan kümesi olabilir — hepsi kolon olur, eksik alan o satırda null).
            var adlar = new List<string>();
            var gorulen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement n in nesneler)
            {
                foreach (JsonProperty p in n.EnumerateObject())
                {
                    if (gorulen.Add(p.Name))
                        adlar.Add(p.Name);
                }
            }

            if (adlar.Count == 0)
            {
                hata = "JSON nesnelerinde alan yok — kolon üretilemez.";
                return null;
            }

            var satirlar = new List<object?[]>(nesneler.Count);
            foreach (JsonElement n in nesneler)
            {
                object?[] satir = new object?[adlar.Count];
                for (int i = 0; i < adlar.Count; i++)
                    satir[i] = n.TryGetProperty(adlar[i], out JsonElement d) ? Deger(d) : null;
                satirlar.Add(satir);
            }

            return new DosyaOnizleme(
                KolonTipiTahminci.Tahmin(adlar, satirlar, CultureInfo.InvariantCulture),
                satirlar, Kesildi: false);
        }
    }

    /// <summary>Skaler dizi (<c>[…]</c> nesne olmayan öğeler): tek "deger" kolonuna sarılır.</summary>
    private static DosyaOnizleme SkalerDizi(IReadOnlyList<JsonElement> ogeler)
    {
        var satirlar = new List<object?[]>(ogeler.Count);
        foreach (JsonElement e in ogeler)
            satirlar.Add([Deger(e)]);

        return new DosyaOnizleme(
            KolonTipiTahminci.Tahmin(["deger"], satirlar, CultureInfo.InvariantCulture),
            satirlar, Kesildi: false);
    }

    /// <summary>Tek JSON değerini .NET tipine indirger; iç içe nesne/dizi → ham JSON metni (metin kolona düşer).</summary>
    private static object? Deger(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString(),
        // (object) cast ŞART: ternary'de long ve double karışınca C# İKİSİNİ de double'a yükseltir →
        // tam sayılar bile double olur (id=1 → 1.0), JSON tam sayıları yanlışlıkla ondalık/float kolona
        // düşerdi. object'e kutulayınca long long kalır, double double (canlı bug 2026-08-10, test yakaladı).
        JsonValueKind.Number => e.TryGetInt64(out long l) ? (object)l : e.GetDouble(),
        _ => e.GetRawText(), // Object / Array → serileştirilmiş JSON (nvarchar) — ilk sürüm sınırı
    };
}
