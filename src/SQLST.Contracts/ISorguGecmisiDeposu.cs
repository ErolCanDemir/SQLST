namespace SQLST.Contracts;

public enum GecmisDurumu
{
    Basarili,
    Hata,
    IptalEdildi
}

/// <summary>Tek çalıştırmanın geçmiş kaydı (FG-3.7). Yalnız yerel diskte yaşar (06-r1 Ö3 gizlilik notu).</summary>
public sealed record GecmisKaydi
{
    public long Id { get; init; }
    /// <summary>
    /// Kaydın ait olduğu bağlantı profili (V3, kullanıcı isteği 2026-07-18): geçmiş
    /// motora göre DEĞİL, profile göre ayrılır — aynı motorda iki ayrı sunucu/profil
    /// birbirinin geçmişini görmez. Eski kayıtlarda (v2) null'dır.
    /// </summary>
    public Guid? ProfilId { get; init; }
    public required string Sunucu { get; init; }
    public string? Veritabani { get; init; }

    /// <summary>
    /// İşlemi yapan kimlik (v10 denetim — "kim"): SQL kimlik doğrulamada bağlantı kullanıcı adı,
    /// integrated'da Windows kullanıcısı. Eski kayıtlarda ve yakalanamayınca null.
    /// </summary>
    public string? Kullanici { get; init; }

    /// <summary>
    /// Sorgunun çalıştırıldığı sekmenin adı (kullanıcı isteği 2026-07-23: "geçmişte pencere adı
    /// da görünsün") — SQLST3 gibi otomatik ya da kullanıcının kaydettiği ad. Eski kayıtlarda null.
    /// </summary>
    public string? SekmeAdi { get; init; }

    public required string Sql { get; init; }
    public DateTime BaslangicUtc { get; init; }
    public int SureMs { get; init; }
    public int SatirSayisi { get; init; }
    public GecmisDurumu Durum { get; init; }
    public string? HataMesaji { get; init; }
}

/// <summary>
/// Kalıcı, aranabilir sorgu geçmişi (V2-S2, FG-3.7). Hassas literal içerebileceği
/// için tamamı silinebilir ve kayıt tümden kapatılabilir (ayar: gecmis.kayit_acik).
/// </summary>
public interface ISorguGecmisiDeposu
{
    /// <summary>
    /// Kaydı ekler ve depoyu son <paramref name="enCok"/> kayda budar.
    /// Budama PROFİL BAŞINA işler — çok kullanılan bir profil, az kullanılanın geçmişini silmez.
    /// </summary>
    Task EkleAsync(GecmisKaydi kayit, int enCok = 1000, CancellationToken ct = default);

    /// <summary>
    /// Son kayıtlar, yeniden eskiye; <paramref name="metin"/> SQL içinde geçenleri süzer.
    /// Kapsam HER ZAMAN tek profildir (V3): <paramref name="profilId"/> null ise yalnız
    /// profilsiz (v2'den kalan) kayıtlar döner — "tüm profiller" diye bir mod yoktur.
    /// </summary>
    Task<IReadOnlyList<GecmisKaydi>> AraAsync(
        string? metin, int limit = 200, Guid? profilId = null, CancellationToken ct = default);

    /// <summary>
    /// Verilen profilin geçmişini siler (gizlilik: hassas literal temizliği);
    /// diğer profillerin kayıtlarına dokunmaz.
    /// </summary>
    Task TemizleAsync(Guid? profilId = null, CancellationToken ct = default);
}
