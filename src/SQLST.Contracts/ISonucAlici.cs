namespace SQLST.Contracts;

/// <summary>
/// 🧱 Okunan satırların AKIŞLI alıcısı (v22-S3, saha turu-3 — kullanıcı üçüncü kez çökme bildirdi:
/// "mantığımızı MSSQL'deki gibi yapalım, gereksiz yüklerden arındıralım").
///
/// Sorun: aynı veri iki kez yaşıyordu — önce okuyucunun <c>List&lt;object?[]&gt;</c>'i, sonra onun
/// ÜSTÜNE kurulan DataTable. ÖLÇÜM (200.000 satır × 10 kolon; 4 sayı + 1 tarih + 5 metin):
/// iki kopya birlikte satır başına <b>708 bayt</b>; tek kopya (tipli DataTable) <b>457 bayt</b>.
/// Tepe bellek okuma bittikten SONRA ikiye katlandığı için çökme çoğu kez orada oluyordu.
///
/// Bu arayüzle satır, okunduğu anda hedef yapıya (grid tablosu) yazılır; ara liste hiç oluşmaz.
/// Uygulayan taraf UI'a ait olduğundan <b>bloklamamalı</b>dır: çağrılar okuma thread'indendir.
/// </summary>
public interface ISonucAlici
{
    /// <summary>Yeni sonuç kümesi başlıyor — kolonlar (tipleriyle) burada bildirilir.</summary>
    void KumeBasladi(int kumeIndex, IReadOnlyList<KolonBilgisi> kolonlar);

    /// <summary>
    /// Tek satır. Dizi çağrıdan sonra SAKLANMAZ varsayılmalıdır (alıcı değerleri kendi yapısına
    /// kopyalar). <b>false</b> dönerse okuma DURUR — alıcı bellek tavanını böyle uygular.
    /// </summary>
    bool Satir(object?[] satir);

    /// <summary>Küme bitti (satır sayısı alıcıda zaten birikti).</summary>
    void KumeBitti(int kumeIndex);
}
