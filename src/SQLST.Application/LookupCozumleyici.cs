using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// FK lookup çözümü (v20-S14, madde 3 "FK lookup açıklaması"). Tıklanan hücrenin kolonu bir yabancı
/// anahtarsa, hedef tabloyu + anahtar kolonu + TAHMİNİ açıklama kolonunu çözer ve tablonun "tanım/lookup"
/// tablosu olup olmadığını SORGUSUZ sinyallerle tahmin eder.
/// </summary>
public sealed record LookupCozumu(
    bool FkVar,
    string? HedefSema,
    string? HedefTablo,
    string? AnahtarKolon,
    string? AciklamaKolon,
    bool TanimTablosu,
    string Mesaj);

/// <summary>
/// FK lookup çözümleyici — SAF, birim testli. Girdi: kaynak tablo + tıklanan kolon + şema önbelleğinin
/// FK'ları ve tablo listesi. Çıktı: <see cref="LookupCozumu"/>. Sinyaller SORGUSUZ (kullanıcı kararı
/// 2026-08-10): (1) kolonun GİDEN FK'sı var mı (<see cref="KolonBaglari"/>); (2) "tanım tablosu mu" =
/// hedefe GELEN FK sayısı ≥2 VEYA hedef ≤5 kolon, VE bariz bir açıklama kolonu var. Açıklama kolonu
/// tahmini: ad-listesi (Ad/Adı/Açıklama/Tanım/İsim/Başlık/Kod/Name/Description/…), yoksa ilk PK-olmayan
/// metin kolonu (Türkçe diakritik-duyarsız eşleşme). COUNT sorgusu KULLANILMAZ.
/// </summary>
public static class LookupCozumleyici
{
    private static readonly string[] AdKolonAdaylari =
    [
        "ad", "adi", "aciklama", "tanim", "isim", "baslik", "etiket", "kod",
        "name", "description", "title", "label", "text", "metin", "value", "deger",
    ];

    /// <summary>
    /// Sorgudan çıkarılan tablo adını ("Mersis.Talep" · "Talep") şema önbelleğindeki nesneyle eşler
    /// (v22-S1, saha turu-2 m.4). Önce şema+ad; bulunamazsa YALNIZ ad ile ve ANCAK tek eşleşme varsa
    /// — sorgu 'veritabani.tablo' ya da farklı şema adıyla yazılmış olabilir; birden çok şemada aynı
    /// ad varsa hangisi olduğu belirsizdir, yanlış tanım tablosu göstermek yerine null döner.
    /// </summary>
    public static SemaNesnesi? KaynakTabloBul(IReadOnlyList<SemaNesnesi> tablolar, string tabloAdi)
    {
        string[] parcalar = tabloAdi.Split('.');
        string ad = parcalar[^1];
        string? sema = parcalar.Length > 1 ? parcalar[^2] : null;

        List<SemaNesnesi> adEsleseni = [.. tablolar.Where(n => n.Tur == SemaNesneTuru.Tablo
            && n.Ad.Equals(ad, StringComparison.OrdinalIgnoreCase))];
        if (sema is not null
            && adEsleseni.FirstOrDefault(n => n.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase)) is { } tam)
            return tam;
        return adEsleseni is [{ } tek] ? tek : null;
    }

    public static LookupCozumu Coz(
        SemaNesnesi kaynak, string kolonAd,
        IReadOnlyList<YabanciAnahtar> fkler,
        IReadOnlyList<SemaNesnesi> tablolar)
    {
        KolonBaglari.Bag? giden = KolonBaglari
            .Bul(kaynak.Sema, kaynak.Ad, kolonAd, fkler)
            .FirstOrDefault(b => b.Giden);

        if (giden is null)
            return new(false, null, null, null, null, false,
                $"'{kolonAd}' bir yabancı anahtar değil — referans verecek tablo yok.");

        YabanciAnahtar fk = giden.Fk;
        string anahtarKolon = giden.KarsiKolon; // hedefteki eşlenik anahtar kolon
        string hedefTamAd = string.IsNullOrEmpty(fk.HedefSema) ? fk.HedefTablo : $"{fk.HedefSema}.{fk.HedefTablo}";

        SemaNesnesi? hedef = tablolar.FirstOrDefault(t =>
            t.Tur == SemaNesneTuru.Tablo
            && t.Ad.Equals(fk.HedefTablo, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(fk.HedefSema) || t.Sema.Equals(fk.HedefSema, StringComparison.OrdinalIgnoreCase)));

        if (hedef is null)
            return new(true, fk.HedefSema, fk.HedefTablo, anahtarKolon, null, false,
                $"Referans: {hedefTamAd}({anahtarKolon}) — hedef tablo şemada bulunamadı, açıklama getirilemiyor.");

        bool tanim = TanimTablosuMu(hedef, fkler, out string? aciklamaKolon, out int gelenFk, anahtarKolon);

        string mesaj = aciklamaKolon is null
            ? $"{hedefTamAd} tanım tablosu gibi değil (açıklama kolonu bulunamadı). Yalnız referans: {hedefTamAd}({anahtarKolon})."
            : tanim
                ? $"✓ {hedefTamAd} tanım tablosu (▲{gelenFk} referans) · açıklama kolonu: {aciklamaKolon}."
                : $"{hedefTamAd} tanım tablosu gibi görünmüyor ama açıklama denenecek: {aciklamaKolon}.";

        return new(true, hedef.Sema, hedef.Ad, anahtarKolon, aciklamaKolon, tanim, mesaj);
    }

    /// <summary>
    /// 🏷 "Bu tablo bir TANIM/LOOKUP tablosu mu?" — ÜRÜNÜN TEK TANIMI (v22-S9'da buraya taşındı).
    ///
    /// Kural kullanıcı kararıdır (2026-08-10) ve SORGUSUZDUR: hedefe GELEN FK sayısı ≥2 VEYA hedef
    /// ≤5 kolon, VE bariz bir açıklama kolonu var. COUNT kullanılmaz — tanım tablosu olmak satır
    /// sayısıyla değil, şemadaki ROLLE belirlenir (boş bir tanım tablosu da tanım tablosudur).
    ///
    /// Kayıt Haritası da (v22-S9, kullanıcı isteği "lookup tablolarını getirmesin") bu kuralı
    /// kullanır. İki yerde iki ayrı "lookup" tanımı olsaydı ürün kendi kendisiyle çelişirdi:
    /// hücrede "tanım tablosu" denen bir tablo haritada getirilmeye devam ederdi.
    /// </summary>
    /// <param name="aciklamaKolon">Bulunan açıklama kolonu (yoksa null — o zaman tanım tablosu değildir).</param>
    /// <param name="gelenFk">Hedefe gelen FK sayısı (mesajlarda gösterilir).</param>
    /// <param name="anahtarKolon">
    /// Açıklama ararken DIŞLANACAK anahtar kolon. Hücre bağlamı varsa FK'nın hedef kolonu verilir;
    /// verilmezse PK'ya düşülür. Bu ayrım önemli: FK hedefi PK olmayan bir tabloda iki farklı kolon
    /// dışlanır ve "açıklama kolonu var mı" sonucu değişebilir — <c>Coz</c> ile Kayıt Haritası'nın
    /// aynı tabloya farklı karar vermesini bu parametre engelliyor.
    /// </param>
    public static bool TanimTablosuMu(
        SemaNesnesi hedef, IReadOnlyList<YabanciAnahtar> fkler,
        out string? aciklamaKolon, out int gelenFk, string? anahtarKolon = null)
    {
        gelenFk = fkler.Count(f =>
            f.HedefTablo.Equals(hedef.Ad, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(f.HedefSema)
                || f.HedefSema.Equals(hedef.Sema, StringComparison.OrdinalIgnoreCase)));

        string anahtar = anahtarKolon ?? hedef.Kolonlar.FirstOrDefault(k => k.PkMi)?.Ad ?? "";
        aciklamaKolon = AciklamaKolonuBul(hedef, anahtar);
        // v23-S9 KURAL DARALDI (canlı tanı 1 Eki 2026 — kullanıcı: "bağlantıları getir tüm
        // bağlantıları listelemiyor; Talep ile bile eksiklerimiz oluyor"): eski kural
        // "gelenFk >= 2 VEYA ≤5 kolon" idi — oysa ÇOK referans almak tam da ANA/GÖVDE tablonun
        // özelliğidir (KdsDemo'da talep.Talepler'e 5 FK gelir, TalepNo metin kolonu var → lookup
        // sanılıp Kayıt Haritası'nın ▲ yönünden ATILIYORDU; Fatura satırından Talep'e
        // çıkılamıyordu). Lookup'ı lookup yapan KÜÇÜKLÜK + açıklama kolonudur; yanlış pozitif
        // GERÇEK VERİYİ GİZLER, yanlış negatif yalnız fazladan bir ▲ ilişki getirir — asimetri
        // dar kuraldan yana. gelenFk mesajlarda bilgi olarak kalır.
        return aciklamaKolon is not null && hedef.Kolonlar.Count <= 5;
    }

    private static string? AciklamaKolonuBul(SemaNesnesi hedef, string anahtarKolon)
    {
        List<SemaKolonu> adaylar = [.. hedef.Kolonlar.Where(k =>
            !k.PkMi
            && !k.Ad.Equals(anahtarKolon, StringComparison.OrdinalIgnoreCase)
            && MetinTipi(k.Tip))];

        // Öncelik: ad-listesi eşleşmesi (diakritik-duyarsız).
        foreach (string aday in AdKolonAdaylari)
            if (adaylar.FirstOrDefault(k => Sadeles(k.Ad) == aday) is { } es)
                return es.Ad;

        // Yoksa ilk PK-olmayan metin kolonu.
        return adaylar.FirstOrDefault()?.Ad;
    }

    private static bool MetinTipi(string tip) =>
        tip.Contains("char", StringComparison.OrdinalIgnoreCase)   // char/nchar/varchar/nvarchar
        || tip.Contains("text", StringComparison.OrdinalIgnoreCase)
        || tip.Contains("string", StringComparison.OrdinalIgnoreCase); // PostgreSQL/diğer

    /// <summary>Türkçe diakritikleri ASCII'ye indirger + küçük harf — kolon adı eşleşmesi tr-duyarsız olsun.</summary>
    private static string Sadeles(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.ToLowerInvariant(c switch
            {
                'ç' or 'Ç' => 'c', 'ğ' or 'Ğ' => 'g', 'ı' => 'i', 'İ' => 'I',
                'ö' or 'Ö' => 'o', 'ş' or 'Ş' => 's', 'ü' or 'Ü' => 'u',
                'â' => 'a', 'î' => 'i', 'û' => 'u', _ => c,
            }));
        return sb.ToString();
    }
}
