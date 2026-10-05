namespace SQLST.Contracts;

/// <summary>Dosya kolonunun örneklemden TAHMİN edilen tipi (v13-S1). SQL tipine eşleme motor
/// lehçesine göre S4'te yapılır — burada motor-nötr kavram tutulur.</summary>
public enum DosyaTipi
{
    /// <summary>Varsayılan/karışık — her değer metin olarak taşınabilir.</summary>
    Metin,

    /// <summary>Tüm dolu değerler tam sayı (long aralığında).</summary>
    TamSayi,

    /// <summary>Tüm dolu değerler sayı, en az biri ondalıklı.</summary>
    Ondalik,

    /// <summary>Tüm dolu değerler tarih/saat.</summary>
    Tarih,

    /// <summary>Tüm dolu değerler true/false.</summary>
    Bool,
}

/// <summary>
/// Dosyadan okunan bir kolonun envanteri: ad (başlıktan ya da Kolon1, Kolon2…), tahmin edilen tip,
/// görülen en uzun metin (nvarchar boyutlandırması) ve boş değer görülüp görülmediği (NULL kararı).
/// </summary>
public sealed record DosyaKolonu(string Ad, DosyaTipi Tip, int EnUzunMetin, bool BosVar);

/// <summary>
/// Dosya önizlemesi (v13): kolon envanteri + ilk N satır (ekrandaki tablo görünümü).
/// <paramref name="Kesildi"/> true ise dosyada gösterilenden fazla satır vardır.
/// Değerler TXT'de string, Excel'de hücre tipiyle gelir (double/DateTime/bool/string).
/// </summary>
public sealed record DosyaOnizleme(
    IReadOnlyList<DosyaKolonu> Kolonlar,
    IReadOnlyList<object?[]> Satirlar,
    bool Kesildi);

/// <summary>
/// Dosyadan tabloya aktarım isteği (v13-S3). Kaynak satırlar isteğe DEĞİL motora ayrıca verilir
/// (akış — dosyanın tamamı belleğe alınmaz); <paramref name="KaynakKolonlar"/> dosya önizlemesinin
/// kolon envanteridir (eşleşmedeki KaynakKolon adları + TXT değer dönüşümünde tip tahmini buradan).
/// <paramref name="KulturAdi"/> TXT string değerlerinin sayı/tarih parse kültürü (null=Invariant).
/// <paramref name="OnceDdl"/> (v13-S4 "yeni tablo" yolu): doluysa aktarımdan önce hedef bağlantıda
/// çalıştırılır (CREATE TABLE) — UI script'i kullanıcıya ONAYLATMADAN doldurmaz.
/// Diğer alanlar v12 Paket Aktarım'la aynı anlamdadır (temizle onayı, hata politikası, UPSERT).
/// </summary>
public sealed record DosyaAktarimIstegi(
    ConnectionProfile Hedef,
    string? HedefVeritabani,
    string HedefTablo,
    IReadOnlyList<AktarimEslesmesi> Eslesmeler,
    IReadOnlyList<DosyaKolonu> KaynakKolonlar,
    string? KulturAdi,
    bool OnceTemizle = false,
    AktarimHataPolitikasi HataPolitikasi = AktarimHataPolitikasi.IlkHatadaDur,
    AktarimYazmaKipi YazmaKipi = AktarimYazmaKipi.YalnizEkle,
    IReadOnlyList<string>? AnahtarKolonlar = null,
    string? OnceDdl = null);
