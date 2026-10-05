namespace SQLST.Contracts;

/// <summary>
/// Zengin sonuç nesnesi (02-mimari §3.2). Kural: hiçbir katman null dönmez,
/// SQL kaynaklı her durum bu nesneye çevrilir — MersisMSSQL'in
/// "catch { return null; }" deseninin panzehiri.
/// </summary>
public sealed record QueryResult
{
    public bool Basarili { get; init; }
    public bool IptalEdildi { get; init; }
    public IReadOnlyList<ResultSetData> ResultSetler { get; init; } = [];
    /// <summary>PRINT çıktıları, "N rows affected" ve bilgi mesajları.</summary>
    public IReadOnlyList<string> Mesajlar { get; init; } = [];
    public SqlHata? Hata { get; init; }
    /// <summary>İstisnadaki TÜM hatalar (2026-07-30, canlı denetim "bütün hatalar görünsün").
    /// Hata null iken boştur; dolu iken en az Hata'yı içerir (MSSQL'de SqlException.Errors'ın tamamı).</summary>
    public IReadOnlyList<SqlHata> TumHatalar { get; init; } = [];
    public TimeSpan Sure { get; init; }
    public int ToplamSatir { get; init; }
    /// <summary>Satır sınırına takıldıysa true — UI "ilk N gösteriliyor" bandı basar (FG-4.5).</summary>
    public bool SatirSiniriAsildi { get; init; }
    /// <summary>
    /// Okuma SATIR değil BELLEK (bayt) bütçesi dolduğu için kesildiyse true (2026-07-23).
    /// SatirSiniriAsildi da true olur; bu bayrak UI'ın "bellek sınırı — büyük satırlar"
    /// bandını basıp nedenin geniş satırlar olduğunu dürüstçe söylemesi için ayrıştırır.
    /// </summary>
    public bool BellekSiniriAsildi { get; init; }
    /// <summary>
    /// Materyalize edilen hücre verisinin kaba bayt toplamı (SonucOkuyucu tahmini). BatchYurutucu
    /// bunu, GO'lu script'lerde bellek bütçesini BATCH'LER ARASI taşımak için kullanır (inceleme
    /// bulgusu 2026-07-23: bütçe batch başına sıfırlanınca 20×GO toplamda kalkanı deliyordu).
    /// </summary>
    public long ToplamBayt { get; init; }
    /// <summary>DML'in etkilediği satır sayısı (RecordsAffected); DML yoksa null. Güvenli Yazma bandı bunu gösterir (V2-S4).</summary>
    public int? EtkilenenSatir { get; init; }
}

public sealed class ResultSetData
{
    public required IReadOnlyList<KolonBilgisi> Kolonlar { get; init; }
    public required IReadOnlyList<object?[]> Satirlar { get; init; }
}

/// <param name="TipAdi">SQL tip adı (ör. nvarchar) — kolon başlığı tooltip'i (FG-4.10).</param>
/// <param name="ClrTip">Okuyucunun .NET tipi — Edit modu grid'i tipli kolon kurar (V2-S5); eski üreticiler null bırakabilir.</param>
/// <param name="Olcek">Kolon şemasındaki ölçek (v23-S13) — tarih/saat ailesinde KESİR hanesi
/// (datetime2(3) → 3): ham gösterim SSMS gibi o kadar hane yazar. Bilinmiyorsa null.</param>
public sealed record KolonBilgisi(string Ad, string TipAdi, Type? ClrTip = null, int? Olcek = null);

public sealed record SqlHata(string Mesaj, int Numara, int Satir, int Onem);
