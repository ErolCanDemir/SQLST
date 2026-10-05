using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// 🌐 URL ↔ Params çift yönlü senkronun SAF çekirdeği (v23 REST yeniden tasarımı, kullanıcı isteği
/// 1 Eki 2026: "URL'ye ?ad=değer yazınca aşağıda parametre tablosu dolmalı — Postman gibi").
///
/// Metinler OLDUĞU GİBİ taşınır: ayrıştırma/kurma sırasında URL-encode/decode YAPILMAZ —
/// kullanıcının yazdığı <c>{{değişken}}</c> yer tutucuları ve ham değerler bozulmaz; kodlama
/// GÖNDERİM anında <c>RestIstemcisi.UrlBirlestir</c>'de yapılır (tek nokta). Pencere, gönderirken
/// TABAN URL + grid satırlarını kullanır — URL'deki query bir daha eklenmez (çiftleme olmaz).
/// </summary>
public static class RestUrlSenkron
{
    /// <summary>
    /// URL'yi taban + query çiftlerine ayırır. İlk <c>?</c> sınırdır; çiftler <c>&amp;</c> ile,
    /// anahtar/değer İLK <c>=</c> ile ayrılır (<c>=</c> yoksa değer boş; değerin içindeki
    /// <c>=</c> değere aittir — ör. base64). Boş bölütler (<c>&amp;&amp;</c>, sondaki <c>&amp;</c>) atlanır.
    /// </summary>
    public static (string Taban, IReadOnlyList<RestSatir> Paramlar) Ayristir(string url)
    {
        url ??= "";
        int soru = url.IndexOf('?');
        if (soru < 0)
            return (url, []);

        string taban = url[..soru];
        var paramlar = new List<RestSatir>();
        foreach (string bolut in url[(soru + 1)..].Split('&'))
        {
            if (bolut.Length == 0)
                continue;
            int esit = bolut.IndexOf('=');
            paramlar.Add(esit < 0
                ? new RestSatir(bolut, "", true)
                : new RestSatir(bolut[..esit], bolut[(esit + 1)..], true));
        }
        return (taban, paramlar);
    }

    /// <summary>
    /// Taban + satırlardan URL kurar: yalnız <b>etkin ve anahtarı dolu</b> satırlar yazılır
    /// (✓ kaldırılan satır URL'den düşer ama grid'de kalır — Postman davranışı). Değer boşsa
    /// <c>anahtar=</c> yazılır (sunucuya "boş değerli parametre" gider — bilinçli).
    /// </summary>
    public static string Kur(string taban, IEnumerable<RestSatir> paramlar)
    {
        string[] etkin = [.. paramlar
            .Where(p => p.Etkin && !string.IsNullOrWhiteSpace(p.Anahtar))
            .Select(p => $"{p.Anahtar}={p.Deger}")];
        return etkin.Length == 0 ? taban : $"{taban}?{string.Join("&", etkin)}";
    }
}
