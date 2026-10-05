using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Asistan HTTP istemcileri (v11-S1): tek soyutlama (<see cref="IAsistanServisi"/>), iki sürücü —
/// <b>🏠 Yerel</b> (Ollama'nın kendi <c>/api/chat</c> ucu; varsayılan, anahtar/internet gerekmez,
/// veri makineden çıkmaz) ve <b>OpenAI-uyumlu</b> (kurumun kendi ucu, Groq/OpenRouter, LM Studio).
///
/// ⛔ v22-S6: <b>Gemini sürücüsü kaldırıldı</b> (kullanıcı kararı 24 Ağu 2026 — "kendi ajanımız ile
/// yapay zeka kullanıyoruz"). SQLST artık Google'a hiçbir istek atmaz.
///
/// İstek/yanıt GÖVDE kurulum ve çözümü SAF static metotlardadır (birim test edilir);
/// HTTP çağrısı ince bir IO adaptörüdür. 429 = ücretsiz katman limiti → hata değil,
/// <see cref="AsistanCevabi.Limit"/> ("biraz bekleyin").
/// </summary>
public sealed class AsistanYonlendirici(ISecretProtector protector) : IAsistanServisi
{
    // Tek HttpClient (socket tükenmesin). Zaman aşımı SAĞLAYICIYA GÖRE istek başına verilir
    // (v21-S1): bulut 120 sn; Yerel 300 sn — CPU'da ilk istek model yüklemesi + ~11 tok/sn üretim
    // (S0 ölçümü) uzun cevaplarda 120'yi aşabiliyordu.
    //
    // 🔌 PROXY (saha bulgusu 25 Ağu 2026: kurulum yapılan makinede AI "Sağlayıcı 407 döndürdü" +
    // proxy oturum açma sayfası). 407 = Proxy Authentication Required. Kurumsal ağda .NET varsayılan
    // olarak SİSTEM PROXY'sini uygular; muafiyet listesinde 127.0.0.1 yoksa YEREL Ollama isteği bile
    // proxy'ye gider ve orada takılır. Bu yüzden iki istemci var:
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // Uzak uçlar: sistem proxy'si kullanılır AMA Windows kimliğiyle — kurumsal proxy'lerin
        // çoğu entegre kimlik doğrulamayı kabul eder ve 407 kendiliğinden çözülür.
        Proxy = ProxyKimlikli(),
        UseProxy = true,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Loopback (127.0.0.1 / localhost) için PROXY'SİZ istemci. Yerel Ollama'ya giden isteğin
    /// kurumsal proxy'den geçmesinin hiçbir anlamı yok; geçerse 407 alır ve yerel AI hiç çalışmaz.
    /// </summary>
    private static readonly HttpClient YerelHttp = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Sistem proxy'sine oturum açmış Windows kullanıcısının kimliğini takar (407 çaresi).</summary>
    private static IWebProxy? ProxyKimlikli()
    {
        IWebProxy? p = HttpClient.DefaultProxy;
        if (p is not null)
            p.Credentials = CredentialCache.DefaultCredentials;
        return p;
    }

    /// <summary>İstek loopback'e mi gidiyor — öyleyse proxy'siz istemci kullanılır.</summary>
    private static HttpClient IstemciSec(HttpRequestMessage istek)
        => istek.RequestUri?.IsLoopback == true ? YerelHttp : Http;

    /// <summary>
    /// 407 için yol gösteren mesaj — ham proxy HTML'i basmak yerine NE OLDUĞUNU ve NE YAPILACAĞINI
    /// söyler. İki durum çok farklı olduğu için ayrı ayrı anlatılır: Yerel'de proxy'nin loopback'i
    /// yakalaması bir YAPILANDIRMA hatasıdır (SQLST artık kendi tarafında baypas ediyor; hâlâ
    /// geliyorsa araya giren bir güvenlik yazılımı vardır), bulutta ise proxy'nin kimlik istemesi
    /// normaldir ve yetki gerekir.
    /// </summary>
    /// <summary>Test kapısı — 407 mesajı saf metin üretimidir, HTTP'siz sınanır.</summary>
    public static string ProxyMesajiTest(AsistanAyarlari ayarlar) => ProxyMesaji(ayarlar);

    private static string ProxyMesaji(AsistanAyarlari ayarlar)
        => ayarlar.Saglayici == AsistanSaglayici.Yerel
            ? "🔌 Ağdaki proxy sunucusu isteği yakaladı ve kimlik doğrulaması istedi (HTTP 407).\n\n"
              + "Bu bir AI hatası değil: 🏠 Yerel AI kendi makinenizdeki Ollama'ya "
              + $"({ayarlar.TabanAdres ?? YerelVarsayilanTaban}) bağlanır ve bu adresin proxy'ye "
              + "GİTMEMESİ gerekir.\n\n"
              + "SQLST artık yerel adresler için proxy'yi kendisi devre dışı bırakıyor. Bu mesajı "
              + "yine de görüyorsanız araya giren bir güvenlik/proxy yazılımı var demektir; sistem "
              + "yöneticinizden 127.0.0.1 ve localhost adreslerini proxy MUAFİYET listesine "
              + "eklemesini isteyin."
            : "🔌 Ağdaki proxy sunucusu isteği yakaladı ve kimlik doğrulaması istedi (HTTP 407).\n\n"
              + "Bu bir AI hatası değil, ağ erişimi sorunudur: bulut sağlayıcıya çıkmak için "
              + "proxy'den geçmeniz gerekiyor ve proxy sizi tanımadı.\n\n"
              + "Sistem yöneticinizden sağlayıcı adresine erişim izni isteyin — ya da internete hiç "
              + "çıkmadan çalışmak için 🏠 Yerel AI'a geçin (Ollama, makinenizde çalışır).";

    /// <summary>Yerel (Ollama) varsayılanları — v21-S0 kararları + S1 lisans düzeltmesi.
    /// Model qwen2.5-coder:7b: 3B "Qwen Research" (ticari dağıtımda riskli) çıktığından ve
    /// Coder ailesinde 7B Apache-2.0 olduğundan (ollama show ile doğrulandı) 7B seçildi.</summary>
    public const string YerelVarsayilanTaban = "http://127.0.0.1:11434";
    public const string YerelVarsayilanModel = "qwen2.5-coder:7b";

    /// <summary>
    /// 🔥 Model bellekte kalma süresi (v22-S5; KDS S30c iyileştirmesinin SQLST karşılığı).
    /// Ollama varsayılanı 5 dk: iki soru arasında bu süre geçerse model bellekten düşer ve sonraki
    /// soru YÜKLEME bekler (KDS ölçümü: soğuk 108 sn ↔ sıcak 24 sn). 30 dk bir çalışma oturumunu
    /// kapsar; RAM'de ~5,5 GB tutar, makinede yer kalmazsa Ollama kendisi tahliye eder.
    ///
    /// ⚠ NEDEN İSTEK GÖVDESİNDE, ortam değişkeninde DEĞİL: <c>OLLAMA_KEEP_ALIVE</c> MAKİNE geneli
    /// bir ayardır — aynı makinedeki diğer Ollama tüketicilerinin (ör. KDS) davranışını da değiştirir
    /// ve Ollama zaten çalışıyorsa yeniden başlatılana kadar HİÇ etki etmez.
    /// </summary>
    public const string YerelBellekteKalma = "30m";

    /// <summary>
    /// Kaçak üretim sigortası (KDS S30c'de gerçek olay: model kendini tekrar edip 145 yerine 542
    /// token üretti — 24 sn → 194 sn). SQLST istemleri "kısa, madde madde" cevap ister; 1.500 token
    /// uzun bir açıklama + SQL için fazlasıyla yeter ve en kötü hâli ~11 tok/sn'de ~2,3 dk'da keser
    /// (300 sn'lik zaman aşımını sonuna kadar beklemeden).
    /// </summary>
    public const int YerelAzamiUretim = 1_500;

    /// <summary>Geçici hatada toplam deneme sayısı (ilk istek dahil) — v22-S5.</summary>
    public const int AzamiDeneme = 3;

    /// <summary>
    /// Sağlayıcı hatası GEÇİCİ mi (yeniden denemeye değer mi)? 5xx sunucu tarafıdır ve ücretsiz
    /// bulut modellerinde "model şu an yoğun" (503) sık görülür; 408 istek zaman aşımıdır.
    /// 429 BURAYA GİRMEZ — o kota sinyalidir, hemen tekrarlamak kotayı daha da yakar; çağıran onu
    /// ayrıca <see cref="AsistanCevabi.Limit"/> olarak ele alır (v11'den beri).
    /// 4xx'in geri kalanı (401 anahtar, 404 model adı) tekrarla düzelmez — denenmez.
    /// </summary>
    public static bool GeciciMi(HttpStatusCode kod)
        => kod == HttpStatusCode.RequestTimeout || (int)kod >= 500;

    /// <summary>Denemeler arası bekleme: 2 sn, sonra 5 sn (kullanıcı ekranda bekliyor — uzun tutulmaz).</summary>
    public static TimeSpan BeklemeSuresi(int deneme)
        => TimeSpan.FromSeconds(deneme == 1 ? 2 : 5);

    /// <summary>
    /// Isıtma gövdesi (v22-S8): öneki GERÇEK bir mesaj olarak gönderir ama <c>num_predict=1</c> ile —
    /// model istemi İŞLER (asıl amaç: önbelleğe girsin), üretim yapmaz.
    ///
    /// ⚠ Rol/içerik, asıl soruda kullanılanla AYNI olmalı (<c>role: user</c>): sohbet şablonu
    /// istemi sarmalar, sarmalama farklı olursa token dizisi baştan farklılaşır ve önbellek ıskalar.
    /// </summary>
    public static string YerelIsitmaGovdesi(string onek, string model) => JsonSerializer.Serialize(new
    {
        model,
        stream = false,
        keep_alive = YerelBellekteKalma,
        options = new { num_predict = 1 },
        messages = new[] { new { role = "user", content = onek } },
    });

    /// <inheritdoc />
    public async Task IsitAsync(string onek, AsistanAyarlari ayarlar, CancellationToken ct)
    {
        // Bulut sağlayıcıda ısıtılacak bir şey yok (model sağlayıcının sunucusunda) — no-op.
        // Boş önek de anlamsız: şema kapısı kapalıysa (sohbet) ısıtacak bir istem yoktur.
        if (ayarlar.Saglayici != AsistanSaglayici.Yerel || string.IsNullOrWhiteSpace(onek))
            return;

        string taban = string.IsNullOrWhiteSpace(ayarlar.TabanAdres)
            ? YerelVarsayilanTaban : ayarlar.TabanAdres;
        string model = string.IsNullOrWhiteSpace(ayarlar.Model)
            ? YerelVarsayilanModel : ayarlar.Model;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{taban.TrimEnd('/')}/api/chat")
            {
                Content = new StringContent(
                    YerelIsitmaGovdesi(onek, model), Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage _ = await IstemciSec(req).SendAsync(req, ct);
        }
        // AGENTS §4.6 izinli bölge (IO adaptörü) + kasıtlı SESSİZLİK: Ollama kapalıysa, model adı
        // yanlışsa ya da kullanıcı sekmeyi hemen kapattıysa ön yükleme başarısız olur — bu kullanıcıyı
        // İLGİLENDİRMEZ. Asıl soru sorulduğunda aynı hata yol gösteren mesajla zaten yüzeye çıkar;
        // burada uyarmak, hiçbir şey istememiş kullanıcıya sebepsiz hata göstermek olurdu.
        catch (HttpRequestException) { }
        catch (OperationCanceledException) { }
    }

    public async Task<AsistanCevabi> SorAsync(
        string istem, AsistanAyarlari ayarlar, CancellationToken ct)
    {
        // DAYANIKLILIK (kullanıcı bulgusu 2026-07-27: kurulumda config dosyası + anahtar yerinde
        // OLDUĞU HÂLDE "yapılandırılmamış" çıkıyor — üst katman/köprü plumbing'i sorunlu). AI servisi
        // artık üst kablolamaya GÜVENMEZ: gelen ayar hazır değilse config dosyasını DOĞRUDAN burada
        // okur. Böylece dosya gerçekten yerindeyse AI çalışır; değilse tam yol EKRANDA gösterilir.
        var configDosyasi = new AsistanConfigDosyasi(protector);
        if (!ayarlar.Hazir && configDosyasi.Oku() is { Hazir: true } dosyadan)
            ayarlar = dosyadan;

        if (!ayarlar.Hazir)
            return AsistanCevabi.Hata(
                "AI asistanı bu kurulumda yapılandırılmamış — yapılandırma dosyası okunamadı.\n\n"
                + $"Uygulama şu yola baktı:\n{configDosyasi.DosyaYolu}\n"
                + $"Dosya orada {(configDosyasi.DosyaVar ? "VAR ama anahtar okunamadı" : "YOK")}.\n\n"
                + "Bu yolu ilettiğinizde config'i doğru yere koyup AI'ı kalıcı çözeriz.");

        string anahtar = string.IsNullOrEmpty(ayarlar.AnahtarSifreli)
            ? "" : protector.Coz(ayarlar.AnahtarSifreli);

        int zamanAsimiSn = ayarlar.Saglayici == AsistanSaglayici.Yerel ? 300 : 120;
        try
        {
            // 🔁 GEÇİCİ HATADA YENİDEN DENEME (v22-S5, kullanıcı bulgusu: "sorulara neden cevap
            // verememiş — verebilmesi gerekiyor"). Ekran görüntüsünde AYNI oturumda ilk iki soru
            // cevaplanmış, sonrakiler "Sağlayıcı 503 döndürdü: This model is currently experiencing
            // high demand" almıştı — yani yapılandırma değil, ücretsiz bulut modelinin o anki
            // DOLULUĞU. Eskiden ilk 503'te pes ediliyordu; 5xx/408 geçicidir, kısa beklemeyle
            // çoğu kez ikinci denemede geçer.
            for (int deneme = 1; ; deneme++)
            {
                using HttpRequestMessage req = ayarlar.Saglayici switch
                {
                    AsistanSaglayici.Yerel => YerelIstek(istem, ayarlar.Model, ayarlar.TabanAdres),
                    _ => OpenAiIstek(istem, ayarlar.Model, ayarlar.TabanAdres ?? "", anahtar),
                };
                using var zamanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                zamanCts.CancelAfter(TimeSpan.FromSeconds(zamanAsimiSn));
                using HttpResponseMessage yanit = await IstemciSec(req).SendAsync(req, zamanCts.Token);
                string govde = await yanit.Content.ReadAsStringAsync(zamanCts.Token);

                if (yanit.StatusCode == HttpStatusCode.TooManyRequests)
                    return AsistanCevabi.Limit(HataOzeti(govde)); // gerekçe gizlenmez (ilk-soru 429 teşhisi)

                // 🔌 407 = PROXY kimlik doğrulaması (saha bulgusu 25 Ağu 2026). Eskiden ham HTML
                // proxy oturum sayfası ekrana dökülüyordu — kullanıcı "AI çalışmıyor" görüyor ama
                // sebebi anlaşılmıyordu. Bu bir AI/sağlayıcı hatası DEĞİL, ağ katmanı hatasıdır.
                if (yanit.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                    return AsistanCevabi.Hata(ProxyMesaji(ayarlar));

                if (GeciciMi(yanit.StatusCode) && deneme < AzamiDeneme)
                {
                    await Task.Delay(BeklemeSuresi(deneme), ct); // 2 sn, sonra 5 sn
                    continue;
                }

                if (!yanit.IsSuccessStatusCode)
                    return AsistanCevabi.Hata(GeciciMi(yanit.StatusCode)
                        // Geçici hatada kullanıcı ne yapacağını bilsin: suç onda değil.
                        ? $"Sağlayıcı şu an yoğun ({(int)yanit.StatusCode}) — {AzamiDeneme} deneme "
                            + "yapıldı, hepsi meşgul döndü.\n\nBu geçici bir durumdur (ücretsiz bulut "
                            + "modelleri yoğun saatlerde dolabiliyor): birkaç dakika sonra aynı soruyu "
                            + "tekrar sorun ya da kesintisiz çalışmak için 🏠 Yerel AI'a geçin.\n\n"
                            + $"Sağlayıcının dediği: {HataOzeti(govde)}"
                        : $"Sağlayıcı {(int)yanit.StatusCode} döndürdü: {HataOzeti(govde)}");

                string? metin = ayarlar.Saglayici switch
                {
                    // v22-S5: Yerel artık Ollama'nın kendi ucunda — yanıt sarmalı farklı (message.content).
                    AsistanSaglayici.Yerel => YerelCevapCoz(govde),
                    _ => OpenAiCevapCoz(govde),
                };
                return metin is { Length: > 0 }
                    ? AsistanCevabi.Basari(metin)
                    : AsistanCevabi.Hata("Sağlayıcı boş cevap döndürdü.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AsistanCevabi.Hata($"Sağlayıcı zaman aşımına uğradı ({zamanAsimiSn} sn).");
        }
        catch (HttpRequestException ex)
        {
            // Yerel'de bağlantı reddi = Ollama ayakta değil → yol gösteren mesaj (v21-S1)
            if (ayarlar.Saglayici == AsistanSaglayici.Yerel)
                return AsistanCevabi.Hata(
                    "🏠 Yerel AI'a (Ollama) ulaşılamadı — büyük olasılıkla çalışmıyor.\n\n"
                    + "1) Ollama kurulu mu? Kurulum admin GEREKTİRMEZ; kapalı ağda OllamaSetup.exe "
                    + "dosyasını elle taşıyıp çalıştırın (ollama.com'dan indirilir).\n"
                    + "2) Çalışıyor mu? Başlat menüsünden Ollama'yı açın ya da komut satırında: ollama serve\n"
                    + $"3) Model çekili mi? Komut satırında: ollama pull {YerelVarsayilanModel}\n\n"
                    + $"Teknik ayrıntı: {ex.Message}");
            return AsistanCevabi.Hata($"Sağlayıcıya ulaşılamadı: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return AsistanCevabi.Hata($"Sağlayıcı yanıtı çözülemedi: {ex.Message}");
        }
    }

    // ---- SAF kurucular/çözücüler (birim testli) ----

    // ⛔ Gemini sürücüsü (GeminiIstek/GeminiGovde/GeminiCevapCoz) v22-S6'da SİLİNDİ — kullanıcı
    // kararı 24 Ağu 2026: "Biz Gemini kullanmıyoruz, kendi ajanımız ile yapay zeka kullanıyoruz."
    // generativelanguage.googleapis.com'a giden tek yol buydu; artık SQLST'nin Google'a hiç isteği yok.

    /// <summary>
    /// 🏠 Yerel (Ollama) isteği — v22-S5'te Ollama'nın KENDİ ucuna (<c>/api/chat</c>) taşındı.
    ///
    /// Eskiden OpenAI-uyumlu uç (<c>/v1/chat/completions</c>) kullanılıyordu. ÖLÇÜLDÜ (yerel Ollama,
    /// gerçek istekler): o uç <c>keep_alive</c> alanını <b>sessizce yutuyor</b> — 2 dk gönderildiğinde
    /// modelin düşme süresi hiç değişmedi; aynı değer <c>/api/chat</c>'e gönderilince anında
    /// 1 dakikaya indi. Yani ayar OpenAI ucunda "eklenmiş ama işlemiyor" olurdu.
    /// (<c>max_tokens</c> orada çalışıyordu, ama tek uçta toplamak hem doğru hem ileride
    /// <c>num_ctx</c>/<c>stop</c>/<c>seed</c> gibi ayarları da mümkün kılıyor.)
    ///
    /// Yerel sağlayıcı SQLST'de kesin olarak Ollama'dır (kurulum Ollama'yı getirir, kılavuz da öyle
    /// yazar); Groq/OpenRouter/LM Studio AYRI sağlayıcıdır ve OpenAI ucunda kalmaya devam eder.
    /// </summary>
    public static HttpRequestMessage YerelIstek(string istem, string? model, string? tabanAdres)
    {
        string taban = string.IsNullOrWhiteSpace(tabanAdres) ? YerelVarsayilanTaban : tabanAdres;
        string kullanilanModel = string.IsNullOrWhiteSpace(model) ? YerelVarsayilanModel : model;
        return new HttpRequestMessage(HttpMethod.Post, $"{taban.TrimEnd('/')}/api/chat")
        {
            Content = new StringContent(
                YerelGovde(istem, kullanilanModel), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Ollama <c>/api/chat</c> gövdesi: akışsız + bellekte kalma + üretim tavanı.</summary>
    public static string YerelGovde(string istem, string model) => JsonSerializer.Serialize(new
    {
        model,
        stream = false,
        keep_alive = YerelBellekteKalma,
        options = new { num_predict = YerelAzamiUretim },
        messages = new[] { new { role = "user", content = istem } },
    });

    /// <summary>Ollama yerel yanıtı: <c>message.content</c> (OpenAI'daki choices[0] sarmalı yok).</summary>
    public static string? YerelCevapCoz(string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        return d.RootElement.TryGetProperty("message", out JsonElement mesaj)
            && mesaj.TryGetProperty("content", out JsonElement icerik)
            ? icerik.GetString() : null;
    }

    /// <summary>OpenAI-uyumlu chat/completions isteği (Groq/OpenRouter/Ollama aynı sözleşme).</summary>
    public static HttpRequestMessage OpenAiIstek(
        string istem, string model, string tabanAdres, string anahtar)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{tabanAdres.TrimEnd('/')}/v1/chat/completions");
        if (anahtar.Length > 0)
            req.Headers.Add("Authorization", $"Bearer {anahtar}");
        req.Content = new StringContent(OpenAiGovde(istem, model), Encoding.UTF8, "application/json");
        return req;
    }

    public static string OpenAiGovde(string istem, string model) => JsonSerializer.Serialize(new
    {
        model,
        messages = new[] { new { role = "user", content = istem } },
    });

    /// <summary>OpenAI-uyumlu yanıt: choices[0].message.content.</summary>
    public static string? OpenAiCevapCoz(string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        if (!d.RootElement.TryGetProperty("choices", out JsonElement secimler)
            || secimler.GetArrayLength() == 0)
            return null;
        return secimler[0].TryGetProperty("message", out JsonElement mesaj)
            && mesaj.TryGetProperty("content", out JsonElement icerik)
            ? icerik.GetString() : null;
    }

    /// <summary>Hata gövdesinden kısa, okunur bir özet (ham JSON duvarı basılmaz).</summary>
    public static string HataOzeti(string govde)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(govde);
            if (d.RootElement.TryGetProperty("error", out JsonElement e))
            {
                if (e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty("message", out JsonElement m))
                    return Kisalt(m.GetString() ?? govde);
                return Kisalt(e.ToString());
            }
        }
        catch (JsonException)
        {
            // JSON değil — ham metni kısalt
        }
        return Kisalt(govde);
    }

    private static string Kisalt(string s) => s.Length <= 220 ? s : s[..220] + "…";
}
