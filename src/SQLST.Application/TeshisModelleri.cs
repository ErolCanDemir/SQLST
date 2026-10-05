namespace SQLST.Application;

/// <summary>Skor yalnız SIRALAMA içindir (06-r1 R1.1 kural 1) — UI yüzde/hızlanma VAADETMEZ.</summary>
public enum OneriGucu
{
    Dusuk,
    Orta,
    Yuksek
}

/// <summary>sys.dm_db_missing_index_* birleşiminden tek öneri satırı (V2-S7, R1.1).</summary>
public sealed record EksikIndexOnerisi(
    string Veritabani,
    string Tablo,
    string? EsitlikKolonlari,
    string? EsitsizlikKolonlari,
    string? IncludeKolonlari,
    double Skor,
    long KullanimSayisi,
    DateTime? SonKullanim,
    string? IsteyenSorgu)
{
    public OneriGucu Guc => IndexAnalizcisi.SkorKovasi(Skor);

    /// <summary>Anahtar kolonlar: eşitlik + eşitsizlik (DMV sırası — script yorumu uyarır).</summary>
    public IReadOnlyList<string> AnahtarKolonlar =>
        [.. KolonlariAyir(EsitlikKolonlari), .. KolonlariAyir(EsitsizlikKolonlari)];

    public IReadOnlyList<string> IncludeKolonlar => KolonlariAyir(IncludeKolonlari);

    /// <summary>DMV "[a], [b]" biçimini kolon adlarına ayırır.</summary>
    internal static IReadOnlyList<string> KolonlariAyir(string? liste)
        => string.IsNullOrWhiteSpace(liste)
            ? []
            : [.. liste.Split(',').Select(k => k.Trim().Trim('[', ']'))];
}

/// <summary>Örtüşme analizi için mevcut index (sys.indexes + sys.index_columns).</summary>
public sealed record MevcutIndex(
    string Tablo,
    string Ad,
    bool UniqueMi,
    bool PkMi,
    IReadOnlyList<string> AnahtarKolonlar,
    IReadOnlyList<string> IncludeKolonlar);

/// <summary>Öneri ↔ mevcut index örtüşme sonucu (07-r2 §6: rolüne göre, ham metinle DEĞİL).</summary>
public sealed record OrtusmeSonucu(string? Not)
{
    public static readonly OrtusmeSonucu Yok = new((string?)null);
}

/// <summary>Kullanılmayan index adayı (06-r1 R1.1 kural 5: sil değil DISABLE önerilir).</summary>
public sealed record KullanilmayanIndex(
    string Tablo,
    string Ad,
    long Okuma,       // seek + scan + lookup
    long Guncelleme,  // bakım maliyeti
    string DisableScript);
