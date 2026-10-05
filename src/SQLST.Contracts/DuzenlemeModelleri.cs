namespace SQLST.Contracts;

/// <summary>
/// Edit modu (V2-S5, FG-4.9) için kolon üst verisi — 07-r2 §4 kuralları bu bilgiyle işler.
/// </summary>
/// <param name="SysTip">Ham sys tip adı: int, nvarchar, rowversion…</param>
/// <param name="GosterimTipi">Tam görünüm: nvarchar(50), decimal(18,2)…</param>
/// <param name="KiyasGuvenliMi">
/// Eski-değer WHERE kıyasına girebilir mi — float/real/text/ntext/image/xml ve (max)
/// tipleri kırılgandır, kıyasa girmez (07-r2 §4).
/// </param>
public sealed record DuzenlemeKolonu(
    string Ad,
    string SysTip,
    string GosterimTipi,
    bool NullOlabilir,
    bool PkMi,
    bool IdentityMi,
    bool ComputedMi,
    bool RowversionMi,
    bool KiyasGuvenliMi)
{
    /// <summary>SET/INSERT'e girebilir mi — identity/computed/rowversion sunucu malıdır.</summary>
    public bool Yazilabilir => !IdentityMi && !ComputedMi && !RowversionMi;

    public string AdKoseli => $"[{Ad.Replace("]", "]]")}]";
}

/// <summary>Düzenlenecek tablonun kimliği + kolon üst verisi.</summary>
public sealed record DuzenlemeMetasi(
    string Veritabani,
    string Sema,
    string Tablo,
    IReadOnlyList<DuzenlemeKolonu> Kolonlar)
{
    public string TamAd => $"{Sema}.{Tablo}";

    public string TamAdKoseli => $"[{Koseli(Sema)}].[{Koseli(Tablo)}]";

    public IReadOnlyList<DuzenlemeKolonu> PkKolonlari => [.. Kolonlar.Where(k => k.PkMi)];

    /// <summary>Varsa iyimser eşzamanlılık kolonu — WHERE'e girer, SET/INSERT'e girmez (07-r2 §4).</summary>
    public DuzenlemeKolonu? Rowversion => Kolonlar.FirstOrDefault(k => k.RowversionMi);

    /// <summary>07-r2 §4: PK yoksa satır kimliği belirsiz → grid salt-okunur.</summary>
    public bool DuzenlenebilirMi => PkKolonlari.Count > 0;

    private static string Koseli(string ad) => ad.Replace("]", "]]");
}
