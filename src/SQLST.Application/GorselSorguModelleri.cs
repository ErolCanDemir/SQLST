using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Görsel Sorgu Tasarımcısı'nda (v6-S2) iki tablo arasındaki bağın türü.</summary>
public enum JoinTuru
{
    /// <summary>Yalnız eşleşen satırlar (varsayılan).</summary>
    Inner,

    /// <summary>Sol tablonun tüm satırları + eşleşen sağ satırlar.</summary>
    Left,

    /// <summary>Sağ tablonun tüm satırları + eşleşen sol satırlar.</summary>
    Right,

    /// <summary>Her iki tablonun tüm satırları (MySQL'de YOK — UI'da gizlenir).</summary>
    Full,

    /// <summary>Kartezyen çarpım — ON yok (bağsız tablolar S1'deki gibi).</summary>
    Cross,
}

/// <summary>Bir join'in ON eşleşmesindeki tek kolon çifti: <c>sol.SolKolon = sag.SagKolon</c>.</summary>
public sealed record GorselKolonEsi(string SolKolon, string SagKolon);

/// <summary>
/// İki tablo arasındaki bağ (v6-S2). <paramref name="Sol"/> ve <paramref name="Sag"/> tuvaldeki
/// kutuların şema nesneleridir; <paramref name="Kolonlar"/> ON eşleşmesidir (birden çok kolon =
/// bileşik anahtar → AND'lenir). <see cref="JoinTuru.Cross"/> için kolon aranmaz.
///
/// LEFT/RIGHT yönlüdür: "sol" korunan taraftır. Üretici, FROM zincirini kurarken tabloyu ters
/// sırada tanıtmak zorunda kalırsa LEFT↔RIGHT'ı ve kolon çiftlerini otomatik çevirir.
/// </summary>
public sealed record GorselJoin(
    SemaNesnesi Sol, SemaNesnesi Sag, JoinTuru Tur, IReadOnlyList<GorselKolonEsi> Kolonlar);

/// <summary>
/// SELECT listesine girecek bir kolon (v6-S4): <c>tablo.kolon</c>. Hiç seçilmezse üretici
/// <c>SELECT *</c> yazar; en az bir seçim varsa yalnız seçilenler yazılır (tam nitelenmiş).
/// </summary>
public sealed record GorselKolonAlani(SemaNesnesi Tablo, string Kolon);

/// <summary>WHERE koşulundaki operatör (v6-S3). Tip-farkındalık: metin operatörleri LIKE üretir.</summary>
public enum KosulOperatoru
{
    Esit,       // =
    Esitsiz,    // <>
    Buyuk,      // >
    Kucuk,      // <
    BuyukEsit,  // >=
    KucukEsit,  // <=
    Icerir,     // LIKE '%v%'
    Baslar,     // LIKE 'v%'
    Biter,      // LIKE '%v'
    Bos,        // IS NULL
    DoluDegil,  // IS NOT NULL
}

/// <summary>
/// Tuvaldeki bir WHERE koşulu (v6-S3): <c>tablo.kolon OP değer</c>. <paramref name="VeyaMi"/>
/// bir ÖNCEKİ koşula OR (true) mı AND (false) mi ile bağlandığını söyler — ilk koşulda yok sayılır.
/// <see cref="KosulOperatoru.Bos"/>/<see cref="KosulOperatoru.DoluDegil"/> için değer aranmaz.
/// </summary>
public sealed record GorselKosul(
    SemaNesnesi Tablo, string Kolon, KosulOperatoru Operator, string Deger, bool VeyaMi);

// ── Script → Görsel (v6-S5): ayrıştırılmış model. Tablolar TAKMA AD ile anılır; App katmanı
//    takma adı şema önbelleğindeki gerçek SemaNesnesi'ye bağlayıp tuvali kurar. ──────────────

/// <summary>Ayrıştırılmış bir FROM tablosu: şema (varsa), ad, kullanılan takma ad.</summary>
public sealed record CozumlenmisTablo(string? Sema, string Ad, string Takma);

/// <summary>Ayrıştırılmış bir JOIN: iki takma ad + tür + ON kolon çiftleri.</summary>
public sealed record CozumlenmisJoin(
    string SolTakma, string SagTakma, JoinTuru Tur, IReadOnlyList<GorselKolonEsi> Kolonlar);

/// <summary>Ayrıştırılmış bir WHERE koşulu (takma ad ile).</summary>
public sealed record CozumlenmisKosul(
    string Takma, string Kolon, KosulOperatoru Operator, string Deger, bool VeyaMi);

/// <summary>Ayrıştırılmış bir SELECT kolonu (takma ad ile). SELECT * ise liste boş kalır.</summary>
public sealed record CozumlenmisSecim(string Takma, string Kolon);

/// <summary>Bir SELECT sorgusunun görsel karşılığı. <see cref="Uyarilar"/>: ayrıştırılamayan parçalar.</summary>
public sealed record CozumlenmisSorgu(
    IReadOnlyList<CozumlenmisTablo> Tablolar,
    IReadOnlyList<CozumlenmisJoin> Joinler,
    IReadOnlyList<CozumlenmisKosul> Kosullar,
    IReadOnlyList<CozumlenmisSecim> Secimler,
    IReadOnlyList<string> Uyarilar);
