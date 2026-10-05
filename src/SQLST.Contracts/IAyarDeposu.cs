namespace SQLST.Contracts;

/// <summary>
/// Uygulama ayarlarının kalıcı deposu (V2-S1). Yerel SQLite'ta anahtar-değer;
/// sorgu timeout'u, satır sınırı gibi tercihler burada tutulur.
/// Değerler kültürden bağımsız (invariant) serileştirilir.
/// </summary>
/// <remarks>
/// V3 (kullanıcı kuralı 2026-07-18 — "tüm projede tuttuğumuz özellikler profil bazlı olmalı"):
/// ayarlar PROFİL kapsamlıdır. <c>profilId</c> verilirse o bağlantıya özel değer okunur/yazılır;
/// null ise GENEL (tüm bağlantılar) değer. Okumada profil değeri yoksa genel değere,
/// o da yoksa varsayılana düşülür — böylece eski (v2) genel tercihler varsayılan olarak sürer.
/// </remarks>
public interface IAyarDeposu
{
    Task<string?> OkuAsync(string anahtar, Guid? profilId = null, CancellationToken ct = default);
    Task YazAsync(string anahtar, string deger, Guid? profilId = null, CancellationToken ct = default);

    Task<int> IntOkuAsync(string anahtar, int varsayilan, Guid? profilId = null, CancellationToken ct = default);
    Task<bool> BoolOkuAsync(string anahtar, bool varsayilan, Guid? profilId = null, CancellationToken ct = default);
}

/// <summary>Bilinen ayar anahtarları — dağınık string'leri tek yerde topla.</summary>
public static class AyarAnahtari
{
    /// <summary>Sorgu (komut) zaman aşımı, saniye; 0 = sınırsız (FG-3.15).</summary>
    public const string KomutTimeoutSn = "sorgu.komut_timeout_sn";

    /// <summary>Grid'e akıtılacak azami satır (FOG-6).</summary>
    public const string SatirSiniri = "sorgu.satir_siniri";

    /// <summary>Sorgu geçmişi kaydı açık mı (FG-3.7 gizlilik anahtarı); varsayılan açık.</summary>
    public const string GecmisAcik = "gecmis.kayit_acik";

    /// <summary>Güvenli Yazma Modu açık mı (V2-S4, Ö1); varsayılan kapalı — kullanıcı isterse açar.</summary>
    public const string GuvenliYazmaAcik = "guvenli_yazma.acik";

    /// <summary>Güvenli Yazma'da karar beklerken otomatik ROLLBACK süresi, sn (varsayılan 300).</summary>
    public const string GuvenliYazmaRollbackSn = "guvenli_yazma.rollback_sn";

    /// <summary>Karanlık tema açık mı (V2-S10); varsayılan açık tema.</summary>
    public const string KoyuTema = "gorunum.koyu_tema";

    /// <summary>Açık tema palet varyantı (v19-S9): indigo · grafit · slate · petrol · amber.</summary>
    public const string AcikPalet = "gorunum.acik_palet";

    /// <summary>Koyu tema palet varyantı (v19-S9): indigo · grafit · slate · petrol · amber.</summary>
    public const string KoyuPalet = "gorunum.koyu_palet";

    // --- Asistan (v11-S1, 2026-07-25) — GENEL kapsam: sağlayıcı/anahtar bağlantıya göre değişmez ---

    /// <summary>Asistan sağlayıcısı: "Yerel" (varsayılan — Ollama) | "OpenAiUyumlu".
    /// v22-S6'da "Gemini" KALDIRILDI; eski kayıtta o değer varsa okuyan taraf Yerel'e göçürür.</summary>
    public const string AsistanSaglayici = "asistan.saglayici";

    /// <summary>Model adı (Yerel varsayılanı: qwen2.5-coder:7b).</summary>
    public const string AsistanModel = "asistan.model";

    /// <summary>Ucun taban adresi — Yerel için http://127.0.0.1:11434, OpenAI-uyumlu için sağlayıcının adresi.</summary>
    public const string AsistanTabanAdres = "asistan.taban_adres";

    /// <summary>API anahtarı — DPAPI ile ŞİFRELİ, Base64 kodlu. Düz metin ASLA yazılmaz.</summary>
    public const string AsistanAnahtarSifreli = "asistan.anahtar_sifreli";

    // --- Log Analizi seçim hatırlama (v20-S3): son analiz edilen DB/tablo/kolonlar profil kapsamında ---

    /// <summary>Log Analizi'nde son seçilen veritabanı adı.</summary>
    public const string LogSonVeritabani = "loganaliz.son_veritabani";

    /// <summary>Log Analizi'nde son seçilen tablo/koleksiyon (TamAd).</summary>
    public const string LogSonTablo = "loganaliz.son_tablo";

    /// <summary>Log Analizi'nde son seçilen mesaj kolonu/alanı.</summary>
    public const string LogSonMesajKolon = "loganaliz.son_mesaj_kolon";

    /// <summary>
    /// GENEL (profil-üstü) tutulan anahtarlar: uygulama görünümü bağlantıya göre değişmez.
    /// Bunlar dışındaki her ayar profil kapsamında saklanır (V3 kuralı).
    /// </summary>
    public static readonly string[] GenelAnahtarlar =
        [KoyuTema, AsistanSaglayici, AsistanModel, AsistanTabanAdres, AsistanAnahtarSifreli];
}
