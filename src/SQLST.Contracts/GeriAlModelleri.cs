namespace SQLST.Contracts;

/// <summary>
/// Geri Al paketi (V15-S3, BF-1 "uçuş kaydedici"): Güvenli Yazma COMMIT'inden önce etkilenen
/// satırların ESKİ hali. Kolon/PK listeleri ve satırlar JSON'dur (yerel SQLite'ta saklanır);
/// ters DML üretimi bu paketten yapılır (S4). Değerler kültürden bağımsız serileştirilir.
/// </summary>
public sealed record GeriAlPaketi
{
    public long Id { get; init; }
    public DateTime TarihUtc { get; init; }
    public required string Sunucu { get; init; }
    public required string Veritabani { get; init; }
    public required string Tablo { get; init; }

    /// <summary>"UPDATE" | "DELETE" — ters DML'in yönünü belirler (UPDATE→UPDATE, DELETE→INSERT).</summary>
    public required string Fiil { get; init; }

    /// <summary>Paketi doğuran orijinal DML — listede "ne geri alınıyor" bağlamı.</summary>
    public required string SqlMetni { get; init; }

    public int SatirSayisi { get; init; }

    /// <summary>[{"ad":"...","tip":"..."}] — LOB/rowversion kolonları pakete alınmaz.</summary>
    public required string KolonlarJson { get; init; }

    /// <summary>["Id",...] — ters UPDATE'in WHERE anahtarı; boşsa yalnız görüntülenebilir.</summary>
    public required string PkJson { get; init; }

    /// <summary>[[hücre|null,...],...] — hücreler kültürden bağımsız STRING temsil.</summary>
    public required string SatirlarJson { get; init; }
}

/// <summary>Geri Al paketlerinin yerel deposu. Budama depo tarafındadır (yaş + toplam boyut).</summary>
public interface IGeriAlDeposu
{
    Task<long> EkleAsync(GeriAlPaketi paket, CancellationToken ct = default);

    /// <summary>En yeni üstte; <b>SatirlarJson BOŞ döner</b> (liste hafif kalsın) — tam paket için <see cref="GetirAsync"/>.</summary>
    Task<IReadOnlyList<GeriAlPaketi>> ListeleAsync(int limit = 200, CancellationToken ct = default);

    Task<GeriAlPaketi?> GetirAsync(long id, CancellationToken ct = default);
    Task SilAsync(long id, CancellationToken ct = default);
}
