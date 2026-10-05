namespace SQLST.Contracts;

/// <summary>
/// Asistan sağlayıcısı (v11-S1; Yerel v21-S1).
///
/// ⛔ GEMINI KALDIRILDI (v22-S6, kullanıcı kararı 24 Ağu 2026: <i>"Biz Gemini kullanmıyoruz, kendi
/// ajanımız ile yapay zeka kullanıyoruz — Gemini'yi tamamen kaldır"</i>). Google'a giden tek yol
/// buydu; artık SQLST yalnız KENDİ ajanına (Yerel/Ollama) ya da kurumun kendi OpenAI-uyumlu ucuna
/// konuşur. Eski <c>"saglayici":"Gemini"</c> yazan config dosyaları kırılmaz —
/// <c>AsistanConfigDosyasi</c> onları <see cref="Yerel"/>'e göçürür.
/// </summary>
public enum AsistanSaglayici
{
    /// <summary>
    /// 🏠 Yerel — Ollama (v21-S1, kullanıcı kararı 2026-08-08): anahtar/internet GEREKMEZ, veri
    /// makineden çıkmaz (kapalı ağ hedefi). Varsayılan uç http://127.0.0.1:11434, varsayılan model
    /// qwen2.5-coder:7b (Apache-2.0). v22-S6'dan beri VARSAYILAN sağlayıcı: "kendi ajanımız" budur
    /// ve enum'un ilk üyesi olması <c>default(AsistanSaglayici)</c>'nin de Yerel olmasını sağlar.
    /// </summary>
    Yerel,

    /// <summary>OpenAI-uyumlu her uç: kurumun kendi ucu, Groq/OpenRouter, LM Studio.</summary>
    OpenAiUyumlu,
}

/// <summary>
/// Asistan yapılandırması (v11-S1). Anahtar burada ŞİFRELİ taşınır (DPAPI —
/// <see cref="ISecretProtector"/>); düz metin anahtar yalnız istek anında çözülür.
/// <paramref name="TabanAdres"/> her iki sürücüde de kullanılır
/// (Yerel/Ollama için http://127.0.0.1:11434 — OpenAI-uyumlu için ör. https://api.groq.com/openai).
/// </summary>
public sealed record AsistanAyarlari(
    AsistanSaglayici Saglayici,
    string Model,
    string? TabanAdres,
    string? AnahtarSifreli)
{
    /// <summary>Kullanılabilir mi: uzak uç anahtar ister; yerel (localhost/Yerel) uç istemez.</summary>
    public bool Hazir => Saglayici == AsistanSaglayici.Yerel
        || !string.IsNullOrEmpty(AnahtarSifreli)
        || (Saglayici == AsistanSaglayici.OpenAiUyumlu
            && TabanAdres?.Contains("localhost", StringComparison.OrdinalIgnoreCase) == true);
}

/// <summary>
/// Asistan cevabı. <see cref="LimitAsildi"/>: ücretsiz katman istek sınırı (HTTP 429) —
/// hata değil, "biraz bekleyin" mesajıdır (ücretsiz yapı kararı, 2026-07-25).
/// </summary>
public sealed record AsistanCevabi(bool Basarili, string Metin, bool LimitAsildi = false)
{
    public static AsistanCevabi Basari(string metin) => new(true, metin);
    public static AsistanCevabi Hata(string mesaj) => new(false, mesaj);
    public static AsistanCevabi Limit(string? saglayiciNedeni = null) => new(false,
        "Ücretsiz kullanım sınırına takıldık. Birkaç saniye bekleyip yeniden deneyin."
        + (string.IsNullOrWhiteSpace(saglayiciNedeni)
            ? ""
            : $"\n\nSağlayıcının gerekçesi: {saglayiciNedeni}\n\nNot: İLK soruda limit görmek genelde "
              + "dakika limiti değil, yapılandırılan MODELİN o hesaptaki kotasının 0 olması demektir "
              + "— yapılandırmadaki model adını kontrol edin.\n\nLimit tanımayan seçenek: 🏠 Yerel AI "
              + "(Ollama) — anahtar, internet ve kota gerektirmez."),
        LimitAsildi: true);
}

/// <summary>
/// Asistan servisi sözleşmesi (v11-S1). Tek iş: hazır İSTEM metnini modele götürüp cevabı getirmek.
/// İstem kurulumu SAF katmandadır (<c>AsistanIstemleri</c>) — "Show Prompt" rayı oradan beslenir;
/// bu servis içerik üretmez, yalnız taşır (IO adaptörü).
/// </summary>
public interface IAsistanServisi
{
    Task<AsistanCevabi> SorAsync(string istem, AsistanAyarlari ayarlar, CancellationToken ct);

    /// <summary>
    /// 🔥 ISITMA (v22-S8) — <paramref name="onek"/>'i modele bir kez işletir (<c>num_predict=1</c>:
    /// üretim yok, amaç yalnız istemin ÖNBELLEĞE girmesi).
    ///
    /// v22-S6'daki "ön yükleme" bunun yerini tutuyordu ama YANLIŞ SORUNU hedefliyordu: o yalnız
    /// modeli belleğe alıyordu (6,8 sn) — oysa asıl bedel istemi İŞLEMEK. Ölçüm (24 Ağu 2026,
    /// gerçek Ollama): istem işleme 15 tok/sn, 1.267 token'lık önek soğukta ~70 sn; ısıtıldıktan
    /// sonra AYNI önekle gelen 6 farklı soruda istem-eval 0,4–1,2 sn — 6/6 isabet.
    /// Isıtma modeli de yüklediği için ön yüklemenin yerine GEÇER, üstüne eklenmez.
    ///
    /// Yalnız <see cref="AsistanSaglayici.Yerel"/> için anlamlıdır; bulut sağlayıcıda no-op
    /// (model orada değil, sağlayıcıda). SESSİZDİR: başarısızlığı kullanıcıya yansımaz — kullanıcı
    /// yalnız sekmeyi açtı, hiçbir şey istemedi; gerçek hata asıl soruda zaten yüzeye çıkar.
    /// </summary>
    Task IsitAsync(string onek, AsistanAyarlari ayarlar, CancellationToken ct);
}
