using System.IO;

namespace SQLST.Infrastructure;

/// <summary>
/// Uygulamanın kullanıcı verisi klasörü: <c>%APPDATA%\SQLST</c>. Profiller
/// (<c>profiles.json</c>), yerel SQLite veritabanı (<c>sqlst.db</c>) ve günlükler burada.
///
/// <b>Neden ayrı bir sınıf?</b> Ürün adı 2026-07-20'de MiniSSMS → SQLST oldu. Veri klasörü
/// eskiden <c>%APPDATA%\MiniSSMS</c> idi; adı değiştirmek tek başına yapılırsa kullanıcının
/// <b>kayıtlı profilleri, parolaları, sorgu geçmişi, nesne tarihçesi ve kod parçaları</b>
/// bir anda "yok olur". Veri silinmez ama uygulama artık ona bakmaz — ki bu, gerçek veri
/// kaybından daha kötüdür: kullanıcı sildiğimizi sanır.
///
/// Bu yüzden yol üç yerde tekrarlanmak yerine burada toplandı ve <b>tek seferlik GÖÇ</b>
/// buraya bağlandı: yeni klasör yoksa ve eskisi varsa, eski klasör olduğu gibi taşınır.
/// </summary>
public static class UygulamaVeriYolu
{
    private const string EskiKlasorAdi = "MiniSSMS";   // 0.5.0 ve öncesi
    private const string KlasorAdi = "SQLST";
    private const string EskiVeritabaniAdi = "minissms.db";
    private const string VeritabaniAdi = "sqlst.db";

    /// <summary>
    /// Göç denendi ve BAŞARISIZ olduysa sebebi. Uygulama bunu günlüğe yazar ve
    /// kullanıcıya gösterir — sessizce boş bir profil listesiyle açılmak, kullanıcıya
    /// "verilerim silinmiş" dedirtirdi.
    /// </summary>
    public static string? GocHatasi { get; private set; }

    static UygulamaVeriYolu() => GocuUygula();

    /// <summary>Veri klasörü (yoksa oluşturulur).</summary>
    public static string Klasor
    {
        get
        {
            string yol = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), KlasorAdi);
            Directory.CreateDirectory(yol);
            return yol;
        }
    }

    public static string ProfilDosyasi => Path.Combine(Klasor, "profiles.json");
    public static string VeritabaniDosyasi => Path.Combine(Klasor, VeritabaniAdi);
    public static string GunlukKlasoru => Path.Combine(Klasor, "logs");

    /// <summary>
    /// Tek seferlik göç: <c>%APPDATA%\MiniSSMS</c> → <c>%APPDATA%\SQLST</c>, ardından
    /// <c>minissms.db</c> → <c>sqlst.db</c>.
    ///
    /// <b>SQLite yan dosyaları da taşınır (-wal, -shm) ve bu şart:</b> WAL modundayız,
    /// yani en son yazılan veriler henüz ana dosyaya işlenmemiş olabilir. Yalnız
    /// <c>.db</c>'yi yeniden adlandırmak, temiz kapanmamış bir oturumdan sonra son
    /// sorgu geçmişini/tarihçeyi sessizce düşürürdü — SQLite WAL'ı dosya adına göre
    /// eşler (<c>ad.db-wal</c>).
    ///
    /// Hata yutulur ama KAYDEDİLİR: göç edemezsek uygulama boş veriyle açılmalı, çünkü
    /// eski klasör diskte durur ve kurtarılabilir; ama kullanıcı bunu bilmeli.
    /// </summary>
    private static void GocuUygula()
    {
        try
        {
            string kok = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string eski = Path.Combine(kok, EskiKlasorAdi);
            string yeni = Path.Combine(kok, KlasorAdi);

            if (!Directory.Exists(yeni) && Directory.Exists(eski))
                Directory.Move(eski, yeni);

            if (!Directory.Exists(yeni))
                return;

            // Veritabanı adı: ana dosya + WAL + paylaşımlı bellek dosyası birlikte.
            foreach (string ek in new[] { "", "-wal", "-shm" })
            {
                string eskiDb = Path.Combine(yeni, EskiVeritabaniAdi + ek);
                string yeniDb = Path.Combine(yeni, VeritabaniAdi + ek);
                if (File.Exists(eskiDb) && !File.Exists(yeniDb))
                    File.Move(eskiDb, yeniDb);
            }
        }
        catch (IOException ex)
        {
            GocHatasi = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            GocHatasi = ex.Message;
        }
    }
}
