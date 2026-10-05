using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// REST/HTTP gönderim katmanı (v20-S8). SOAP istemcisiyle AYNI ağ davranışı: kurumsal proxy + oturum
/// Windows kimliği (NTLM/Negotiate) + <b>localhost/yerel adres muafiyeti</b> (proxy'siz) + tarayıcı
/// benzeri User-Agent/Accept. Aldığı <see cref="RestIstek"/> ZATEN çözülmüştür (<c>{{değişken}}</c>'ler
/// App katmanında <see cref="DegiskenCozucu"/> ile değiştirilmiştir) — burada yalnız istek kurulur ve
/// gönderilir. HTTP hata KODU (4xx/5xx) gövdeyle NORMAL döner (asıl bilgi gövdededir); yalnız ağ/istisna
/// <see cref="RestCevap.Hata"/>'ya düşer. Süre sınırsız değildir ama iptali <paramref name="ct"/> keser.
/// </summary>
public class RestIstemcisi
{
    private static readonly HttpClient Http = Kur(kullanProxy: true);
    private static readonly HttpClient HttpYerel = Kur(kullanProxy: false);

    /// <summary>Hedefe göre istemci: yerel/loopback/özel-IP → proxy'siz (ortak <see cref="AgYardimcisi.YerelMi"/>, v20-S14).</summary>
    private static HttpClient Sec(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && AgYardimcisi.YerelMi(u) ? HttpYerel : Http;

    private static HttpClient Kur(bool kullanProxy)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        if (kullanProxy)
        {
            IWebProxy proxy = WebRequest.GetSystemWebProxy();
            proxy.Credentials = CredentialCache.DefaultCredentials;
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 SQLST");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }

    /// <summary>İsteği gönderir (çözülmüş URL/başlık/gövde ile) ve zengin yanıtı döner.</summary>
    public virtual async Task<RestCevap> GonderAsync(RestIstek istek, CancellationToken ct)
    {
        var sure = Stopwatch.StartNew();
        string url = UrlBirlestir(istek.Url, istek.QueryParametreleri);
        try
        {
            using var mesaj = new HttpRequestMessage(new HttpMethod(istek.Metod.ToString()), url);

            // Başlıklar: Content-* olanlar İÇERİK başlığıdır (Headers'a eklenemez) → gövdeye uygulanır.
            string? icerikTipi = null;
            foreach (RestSatir h in istek.Basliklar)
            {
                if (!h.Etkin || string.IsNullOrWhiteSpace(h.Anahtar))
                    continue;
                if (h.Anahtar.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    icerikTipi = h.Deger;
                else if (!h.Anahtar.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    mesaj.Headers.TryAddWithoutValidation(h.Anahtar, h.Deger);
            }

            if (GovdeOlabilir(istek.Metod) && !string.IsNullOrEmpty(istek.Govde))
                mesaj.Content = YeniIcerik(istek.Govde, icerikTipi);

            KimlikEkle(mesaj, istek.Kimlik);

            using HttpResponseMessage yanit = await Sec(url).SendAsync(mesaj, ct);
            byte[] bayt = await yanit.Content.ReadAsByteArrayAsync(ct);
            string govde = Encoding.UTF8.GetString(bayt);

            var basliklar = new List<RestSatir>();
            foreach (var b in yanit.Headers)
                basliklar.Add(new RestSatir(b.Key, string.Join(", ", b.Value)));
            foreach (var b in yanit.Content.Headers)
                basliklar.Add(new RestSatir(b.Key, string.Join(", ", b.Value)));

            return new RestCevap(
                (int)yanit.StatusCode, yanit.ReasonPhrase ?? "", basliklar, govde,
                sure.Elapsed, bayt.LongLength, yanit.Content.Headers.ContentType?.ToString(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            string neden = ex is TaskCanceledException && !ct.IsCancellationRequested
                ? "Zaman aşımı (100 sn)."
                : ex.Message;
            return new RestCevap(0, "", [], "", sure.Elapsed, 0, null, neden);
        }
    }

    private static StringContent YeniIcerik(string govde, string? icerikTipi)
    {
        // "application/json; charset=utf-8" gibi tam tip verilmişse onu koru; yoksa JSON varsay.
        string tip = string.IsNullOrWhiteSpace(icerikTipi) ? "application/json" : icerikTipi!;
        string medya = tip.Split(';')[0].Trim();
        var icerik = new StringContent(govde, Encoding.UTF8, medya);
        return icerik;
    }

    /// <summary>GET/HEAD dışında gövde taşınabilir (DELETE/PATCH gövdeli API'ler için de açık).</summary>
    private static bool GovdeOlabilir(HttpMetodu m) => m is not (HttpMetodu.GET or HttpMetodu.HEAD);

    /// <summary>Authorization: Basic base64(kul:parola) ya da Bearer token (SOAP ile aynı kural, SoapKimlik).</summary>
    private static void KimlikEkle(HttpRequestMessage mesaj, SoapKimlik? kimlik)
    {
        if (kimlik is not { Dolu: true })
            return;
        if (kimlik.BearerMi)
        {
            mesaj.Headers.TryAddWithoutValidation("Authorization", "Bearer " + kimlik.Parola);
            return;
        }
        string cift = $"{kimlik.KullaniciAdi}:{kimlik.Parola}";
        mesaj.Headers.TryAddWithoutValidation("Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(cift)));
    }

    /// <summary>URL'e etkin query parametrelerini ekler (URL'de <c>?</c> varsa <c>&amp;</c> ile).</summary>
    private static string UrlBirlestir(string url, IReadOnlyList<RestSatir> query)
    {
        var etkin = query.Where(q => q.Etkin && !string.IsNullOrWhiteSpace(q.Anahtar)).ToList();
        if (etkin.Count == 0)
            return url;
        string ek = string.Join("&", etkin.Select(q =>
            $"{Uri.EscapeDataString(q.Anahtar)}={Uri.EscapeDataString(q.Deger)}"));
        return url + (url.Contains('?') ? "&" : "?") + ek;
    }
}
