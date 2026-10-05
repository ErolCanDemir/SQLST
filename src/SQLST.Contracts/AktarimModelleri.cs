namespace SQLST.Contracts;

/// <summary>Bir kolon eşleşmesi: kaynak sorgu/tablo kolonu → hedef tablo kolonu (v12-S1).</summary>
public sealed record AktarimEslesmesi(string KaynakKolon, string HedefKolon);

/// <summary>Satır hatasında ne yapılır (v12 planı, 2026-07-26).</summary>
public enum AktarimHataPolitikasi
{
    /// <summary>Varsayılan: ilk hatada dur — aktif parti geri alınır, o ana dek COMMIT'lenen kalır.</summary>
    IlkHatadaDur,

    /// <summary>Hatalı satırı atla, saymaya devam et; ilk örnek hatalar raporda listelenir.</summary>
    AtlaVeRaporla,
}

/// <summary>Yazma kipi (v12-S4).</summary>
public enum AktarimYazmaKipi
{
    /// <summary>Varsayılan: her kaynak satırı INSERT edilir.</summary>
    YalnizEkle,

    /// <summary>
    /// UPSERT: anahtar kolonlara göre önce UPDATE denenir, satır yoksa INSERT. İki-adımlı yol
    /// BİLE BİLE motor-bağımsızdır (MERGE/ON CONFLICT/ON DUPLICATE lehçe lehçe ayrışırdı —
    /// çoklu-motor kuralı gereği ortak yol seçildi); parti transaction'ı tutarlılığı korur.
    /// </summary>
    EkleGuncelle,
}

/// <summary>
/// Aktarım isteği (v12-S1). <paramref name="KaynakSelectSql"/> iki kipin ortak yolu: Tablo→Tablo
/// kipinde üretilmiş SELECT, Sorgu→Tablo kipinde kullanıcının serbest sorgusudur.
/// <paramref name="OnceTemizle"/> hedefi DELETE ile boşaltır (TRUNCATE değil — yetki/FK dostu) ve
/// UI'da AÇIK ONAY ister. <paramref name="AnahtarKolonlar"/> yalnız EkleGuncelle kipinde gerekir:
/// HEDEF kolon adlarıdır, hepsi eşlenmiş olmalı ve anahtar dışında en az bir kolon kalmalıdır.
/// </summary>
public sealed record AktarimIstegi(
    ConnectionProfile Kaynak,
    string? KaynakVeritabani,
    string KaynakSelectSql,
    ConnectionProfile Hedef,
    string? HedefVeritabani,
    string HedefTablo,
    IReadOnlyList<AktarimEslesmesi> Eslesmeler,
    bool OnceTemizle = false,
    AktarimHataPolitikasi HataPolitikasi = AktarimHataPolitikasi.IlkHatadaDur,
    AktarimYazmaKipi YazmaKipi = AktarimYazmaKipi.YalnizEkle,
    IReadOnlyList<string>? AnahtarKolonlar = null);

/// <summary>
/// Mongo→Mongo aktarım isteği (v12-S5, kullanıcı senaryosu: log koleksiyonunu arşiv DB'sine
/// taşıma). Belgeler OLDUĞU GİBİ kopyalanır — şemasız ailede kolon eşleme yoktur.
/// <paramref name="SuzgecJson"/> isteğe bağlı find filtresi (boş/null = tüm belgeler).
/// EkleGuncelle kipi <c>_id</c>'ye göre replace-upsert yapar. Hedef veritabanı/koleksiyon var
/// olmak zorunda değildir — Mongo örtük oluşturur (arşiv senaryosunun kendisi).
/// <paramref name="Eslesmeler"/> (v12-S6) null/boş = belge OLDUĞU GİBİ kopyalanır (örneklem
/// dışı alanlar dahil — güvenli varsayılan); doluysa belge yalnız listedeki ÜST DÜZEY alanlardan
/// yeniden kurulur (yeniden adlandırma/atlama) ve belgede olmayan alan o belgede sessizce atlanır.
/// </summary>
public sealed record MongoAktarimIstegi(
    ConnectionProfile Kaynak,
    string KaynakVeritabani,
    string KaynakKoleksiyon,
    string? SuzgecJson,
    ConnectionProfile Hedef,
    string HedefVeritabani,
    string HedefKoleksiyon,
    bool OnceTemizle = false,
    AktarimHataPolitikasi HataPolitikasi = AktarimHataPolitikasi.IlkHatadaDur,
    AktarimYazmaKipi YazmaKipi = AktarimYazmaKipi.YalnizEkle,
    IReadOnlyList<AktarimEslesmesi>? Eslesmeler = null);

/// <summary>İlerleme raporu (UI çubuğu): okunan/eklenen/güncellenen/atlanan satır.</summary>
public sealed record AktarimIlerleme(long Okunan, long Yazilan, long Guncellenen, long Atlanan);

/// <summary>
/// Aktarım sonucu (v12-S4'te rapor için genişledi). <paramref name="Yazilan"/> (eklenen) ve
/// <paramref name="Guncellenen"/> COMMIT'lenmiş satırdır — yarıda kesilmede bile dürüst sayı.
/// <paramref name="HataOrnekleri"/> Atla politikasında hata ayrıntıları — paket aktarım
/// servislerinde en çok 10.000 (v22-S16; ekran ilk 20'yi gösterir, tamamı hata raporu dosyasına
/// ve özetle günlüğe gider), dosya içe aktarmada 20.
/// </summary>
public sealed record AktarimSonucu(
    bool Basarili,
    long Okunan,
    long Yazilan,
    long Guncellenen,
    long Atlanan,
    string? Hata,
    TimeSpan Sure,
    IReadOnlyList<string> HataOrnekleri,
    bool IptalEdildi = false);
