using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// SOAP/WSDL ağ katmanı (v14-S1). İki iş:
///  • <see cref="WsdlIndirAsync"/> — WSDL'i GET'ler; WCF'nin ÇOK PARÇALI yayınında (xsd:import /
///    wsdl:import schemaLocation) parçaları da izleyip indirir (sınır 20 — döngü/şişme koruması).
///    Önce <c>?singleWsdl</c> denenir (WCF 4.5+ tek belge verir; başarısızsa verilen URL kullanılır).
///  • <see cref="CagirAsync"/> — SOAP 1.1 POST (text/xml; charset=utf-8 + SOAPAction başlığı);
///    yanıt gövdesi + süre + Fault tespiti döner. HTTP hatası da GÖVDEYLE döner — Fault gövdesi
///    çoğu zaman 500'le gelir ve kullanıcının göreceği asıl bilgi odur.
/// </summary>
public class SoapIstemcisi
{
    // Ağa iki istemci: PROXY'li (uzak adresler) ve DOĞRUDAN (yerel/loopback — proxy'siz).
    // Seçim IstemciSec(url) ile hedef adrese göre yapılır (v20-S2).
    private static readonly HttpClient Http = HttpKur(kullanProxy: true);
    private static readonly HttpClient HttpYerel = HttpKur(kullanProxy: false);

    /// <summary>
    /// Paylaşılan HttpClient kurucu — proxy'li ve proxy'siz iki örnek üretilir.
    /// • PooledConnectionLifetime (inceleme 2026-07-30): süresiz havuz DNS/failover değişimini hiç
    ///   görmüyordu — uzun ömürlü masaüstü süreci eski IP'ye yapışık kalırdı. 2 dk'da tazelenir.
    /// • v19-S20 (kullanıcı bulgusu 2026-08-04: URL'den WSDL yüklerken HTTP 407 "Proxy Authentication
    ///   Required" — "kullanıcı/parola girdim yine de"): girilen kimlik SERVİSE 'Authorization' ile
    ///   gider; aradaki KURUMSAL PROXY ayrı 'Proxy-Authorization' ister. Sistem proxy'si + oturum
    ///   açmış Windows kullanıcısının kimliğiyle (NTLM/Negotiate) proxy geçilir — tarayıcının yaptığı.
    ///   Proxy yoksa doğrudan çıkışa düşer (zararsız).
    /// • v20-S2 (kullanıcı bulgusu 2026-08-05): YEREL (localhost/loopback) adresler proxy'ye
    ///   YOLLANMAMALI — ayrı proxy'siz istemci (kullanProxy:false); seçim <see cref="IstemciSec"/>'te.
    /// </summary>
    private static HttpClient HttpKur(bool kullanProxy)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        if (kullanProxy)
        {
            IWebProxy proxy = WebRequest.GetSystemWebProxy();
            proxy.Credentials = CredentialCache.DefaultCredentials; // entegre Windows kimliği (NTLM/Negotiate)
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false; // yerel adres: proxy atlanır (tarayıcının "yerel için proxy'yi atla"sı)
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        // v20-S1 (kullanıcı bulgusu 2026-08-05: URL'den WSDL yüklerken HTTP 403 "urlblocked"): kurumsal
        // web filtresi/WAF, TARAYICI OLMAYAN (User-Agent'sız) istekleri 403 ile engelliyordu. Tarayıcı
        // benzeri User-Agent + Accept → bot koruması geçilir (tarayıcının açabildiği durumlar).
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 SQLST");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/xml,application/xml,application/soap+xml,text/html,*/*");
        return client;
    }

    /// <summary>
    /// v20-S2 (kullanıcı bulgusu 2026-08-05: localhost WCF servisi — URL'den 403 "URLBlocked" VE
    /// Dosya'dan yüklerken HER operasyon "öğesi şemada bulunamadı"): İKİ belirti TEK kök. S20'de eklenen
    /// sistem proxy'si LOCALHOST isteğini de proxy'ye yolluyordu; kurumsal filtre localhost'u 403
    /// URLBlocked ile reddediyordu. Dosya yolunda da WCF'nin '?xsd=xsdN' şema parçaları localhost http
    /// import'u olduğundan aynı 403'e düşüyor → şema inmiyor → hiçbir operasyonun zarfı üretilemiyor.
    /// Tarayıcı/SoapUI yerel adresleri proxy'den MUAF tutar (WinINET '&lt;local&gt;'); biz de tutarız:
    /// loopback (localhost / 127.* / ::1) veya noktasız (intranet) ana bilgisayar → proxy'siz istemci.
    /// </summary>
    // v20-S14: yerel/intranet tespiti ortak AgYardimcisi'na taşındı (LAN IP + link-local dahil — WSDL'den
    // gelen özel-IP'li SOAP adresi kurumsal proxy'de asılmasın; kullanıcı bulgusu 2026-08-10).
    private static HttpClient IstemciSec(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && AgYardimcisi.YerelMi(u) ? HttpYerel : Http;

    /// <summary>Authorization başlığı (v16 Basic; v19-S2 Bearer): kullanıcı adı doluysa
    /// "Basic base64(kullanıcı:parola)", yalnız token varsa "Bearer token".</summary>
    private static void KimlikEkle(HttpRequestMessage istek, SoapKimlik? kimlik)
    {
        if (kimlik is not { Dolu: true })
            return;

        if (kimlik.BearerMi)
        {
            istek.Headers.TryAddWithoutValidation("Authorization", "Bearer " + kimlik.Parola);
            return;
        }

        string cift = $"{kimlik.KullaniciAdi}:{kimlik.Parola}";
        istek.Headers.TryAddWithoutValidation("Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(cift)));
    }

    /// <summary>GET + isteğe bağlı Basic auth; gövdeyi string döner (WSDL/şema indirme).</summary>
    private static async Task<string> IndirAsync(string url, SoapKimlik? kimlik, CancellationToken ct)
    {
        using var istek = new HttpRequestMessage(HttpMethod.Get, url);
        KimlikEkle(istek, kimlik);
        using HttpResponseMessage yanit = await IstemciSec(url).SendAsync(istek, ct); // yerel → proxy'siz (v20-S2)
        // v19-S20: 407/401'de jenerik "status code does not indicate success" yerine yönlendiren mesaj.
        if (yanit.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            throw new HttpRequestException(
                "Proxy kimlik doğrulaması gerekiyor (HTTP 407). Girdiğiniz kullanıcı/parola SERVİSE gider; "
                + "aradaki proxy Windows oturum kimliğinizle geçilmeye çalışıldı ama kabul etmedi. "
                + "Proxy ayrı bir hesap istiyorsa ağ yöneticinize danışın ya da WSDL'i 📁 Dosya ile yükleyin.");
        if (yanit.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException(
                "Servis kimlik doğrulaması reddedildi (HTTP 401). Kimlik türü (Basic/Bearer) ve kullanıcı/parola "
                + "ya da token'ı kontrol edin.");
        // v20-S1: 403 çoğunlukla kurumsal web filtresi/WAF'ın URL'i (ya da tarayıcı-dışı isteği) engellemesidir.
        if (yanit.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException(
                $"Erişim engellendi (HTTP 403{(string.IsNullOrWhiteSpace(yanit.ReasonPhrase) ? "" : " " + yanit.ReasonPhrase)}). "
                + "Genellikle kurumsal web filtresi/güvenlik duvarı bu URL'i (ya da tarayıcı dışı isteği) engeller. "
                + "WSDL'i tarayıcıdan indirip 📁 Dosya ile yükleyebilir, ya da ağ yöneticinizden URL'in izinli listeye "
                + "eklenmesini isteyebilirsiniz.");
        yanit.EnsureSuccessStatusCode();
        return await yanit.Content.ReadAsStringAsync(ct);
    }

    public virtual async Task<(string AnaWsdl, IReadOnlyList<string> EkBelgeler)> WsdlIndirAsync(
        string url, SoapKimlik? kimlik, CancellationToken ct)
    {
        // WCF 4.5+ tek belge: ?wsdl → ?singleWsdl dene; olmadıysa orijinal URL.
        string ana;
        if (url.EndsWith("?wsdl", StringComparison.OrdinalIgnoreCase))
        {
            string tekli = url[..^5] + "?singleWsdl";
            try
            {
                ana = await IndirAsync(tekli, kimlik, ct);
            }
            catch (HttpRequestException)
            {
                ana = await IndirAsync(url, kimlik, ct);
            }
        }
        else
        {
            ana = await IndirAsync(url, kimlik, ct);
        }

        // Çok parçalı yayın: schemaLocation'ları genişlik-öncelikli izle (en çok 20 belge).
        var ekler = new List<string>();
        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { url };
        var kuyruk = new Queue<(string Icerik, Uri Taban)>();
        kuyruk.Enqueue((ana, new Uri(url)));
        while (kuyruk.Count > 0 && ekler.Count < 20)
        {
            (string icerik, Uri taban) = kuyruk.Dequeue();
            foreach (string konum in SemaKonumlari(icerik))
            {
                if (!Uri.TryCreate(taban, konum, out Uri? tam) || !gorulen.Add(tam.AbsoluteUri))
                    continue;
                try
                {
                    string ek = await IndirAsync(tam.AbsoluteUri, kimlik, ct);
                    ekler.Add(ek);
                    kuyruk.Enqueue((ek, tam));
                }
                catch (HttpRequestException)
                {
                    // Parça inmezse çözümleyici uyarı üretir — indirme burada susturulmaz ama
                    // tüm işlemi de düşürmez (kalan parçalarla devam edilir).
                }
            }
        }

        return (ana, ekler);
    }

    /// <summary>
    /// #12 (kullanıcı isteği 2026-07-29): WSDL'i YEREL DOSYADAN okur. <see cref="WsdlIndirAsync"/>'in
    /// dosya ikizi: ana dosyayı okur, xsd:import/include schemaLocation'larını dosyanın KLASÖRÜNE göre
    /// çözüp yerel dosyaları (mutlak http import'ları yine ağdan) izler — aynı 20 sınır + döngü koruması.
    /// </summary>
    public virtual async Task<(string AnaWsdl, IReadOnlyList<string> EkBelgeler)> WsdlDosyadanOkuAsync(
        string dosyaYolu, CancellationToken ct)
    {
        string ana = await File.ReadAllTextAsync(dosyaYolu, ct);
        var tabanUri = new Uri(Path.GetFullPath(dosyaYolu)); // file:// taban — göreli import'lar buna göre

        var ekler = new List<string>();
        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tabanUri.AbsoluteUri };
        var kuyruk = new Queue<(string Icerik, Uri Taban)>();
        kuyruk.Enqueue((ana, tabanUri));
        while (kuyruk.Count > 0 && ekler.Count < 20)
        {
            (string icerik, Uri taban) = kuyruk.Dequeue();
            foreach (string konum in SemaKonumlari(icerik))
            {
                if (!Uri.TryCreate(taban, konum, out Uri? tam) || !gorulen.Add(tam.AbsoluteUri))
                    continue;
                try
                {
                    string ek = tam.IsFile
                        ? await File.ReadAllTextAsync(tam.LocalPath, ct)
                        : await IndirAsync(tam.AbsoluteUri, null, ct);
                    ekler.Add(ek);
                    kuyruk.Enqueue((ek, tam));
                }
                catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException)
                {
                    // parça okunamazsa çözümleyici uyarı üretir; kalan parçalarla devam
                }
            }
        }

        return (ana, ekler);
    }

    private static IEnumerable<string> SemaKonumlari(string xml)
    {
        XDocument belge;
        try
        {
            belge = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }

        foreach (XElement e in belge.Descendants())
        {
            if (e.Name.LocalName is "import" or "include"
                && (e.Attribute("schemaLocation")?.Value ?? e.Attribute("location")?.Value) is { } konum
                && konum.Length > 0)
            {
                yield return konum;
            }
        }
    }

    public virtual async Task<SoapCevap> CagirAsync(
        string adres, string soapAction, string zarf, SoapKimlik? kimlik, CancellationToken ct)
    {
        var sure = Stopwatch.StartNew();
        try
        {
            using var istek = new HttpRequestMessage(HttpMethod.Post, adres)
            {
                Content = new StringContent(zarf, Encoding.UTF8, "text/xml"),
            };
            istek.Headers.TryAddWithoutValidation("SOAPAction", $"\"{soapAction}\"");
            KimlikEkle(istek, kimlik);

            using HttpResponseMessage yanit = await IstemciSec(adres).SendAsync(istek, ct); // yerel → proxy'siz (v20-S2)
            string govde = await yanit.Content.ReadAsStringAsync(ct);
            bool fault = govde.Contains(":Fault>", StringComparison.Ordinal)
                || govde.Contains("<Fault>", StringComparison.Ordinal);
            return new SoapCevap((int)yanit.StatusCode, govde, sure.Elapsed, fault, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            string neden = ex is TaskCanceledException && !ct.IsCancellationRequested
                ? "Zaman aşımı (100 sn)." : ex.Message;
            return new SoapCevap(0, "", sure.Elapsed, false, neden);
        }
    }
}
