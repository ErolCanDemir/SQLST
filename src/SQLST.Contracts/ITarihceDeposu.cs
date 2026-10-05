namespace SQLST.Contracts;

/// <summary>
/// Nesne tanımının yerel sürüm kaydı (V2-S8, Ö2). DÜRÜST SINIR: yalnız BU araçla
/// görülen anlar kaydedilir — veritabanının gerçek denetim günlüğü DEĞİLDİR (06-r1 Ö2).
/// </summary>
public sealed record TarihceKaydi
{
    public long Id { get; init; }
    /// <summary>
    /// Kaydın ait olduğu bağlantı profili (V3 kuralı 2026-07-18): aynı sunucuya bakan iki
    /// profil birbirinin sürüm zincirini görmez ve bozmaz. Eski (v2) kayıtlarda null.
    /// </summary>
    public Guid? ProfilId { get; init; }
    public required string Sunucu { get; init; }
    public required string Veritabani { get; init; }
    public required string Sema { get; init; }
    public required string Ad { get; init; }
    /// <summary>Tanımın SHA-256 hex hash'i — aynı içerik ikinci kez sürüm açmaz.</summary>
    public required string IcerikHash { get; init; }
    public required string Tanim { get; init; }
    public DateTime GorulmeUtc { get; init; }
    /// <summary>"okuma" (tanım açıldı) | "alter-öncesi" (FG-5.5 sigortası).</summary>
    public required string Kaynak { get; init; }
}

/// <summary>İçerik-hash'li nesne tarihçesi deposu (V2-S8; 07-r2: lokal SQLite).</summary>
public interface ITarihceDeposu
{
    /// <summary>Son sürümle aynı hash'se sürüm AÇMAZ ve false döner; yeni içerikte kaydeder.</summary>
    Task<bool> EkleAsync(TarihceKaydi kayit, CancellationToken ct = default);

    /// <summary>Nesnenin sürümleri, yeniden eskiye — yalnız verilen profilin kayıtları.</summary>
    Task<IReadOnlyList<TarihceKaydi>> ListeAsync(
        Guid? profilId, string sunucu, string veritabani, string sema, string ad,
        int limit = 50, CancellationToken ct = default);

    /// <summary>Nesnenin son sürümü (yoksa null) — "dışarıda değişti" kıyası için.</summary>
    Task<TarihceKaydi?> SonAsync(
        Guid? profilId, string sunucu, string veritabani, string sema, string ad, CancellationToken ct = default);
}
