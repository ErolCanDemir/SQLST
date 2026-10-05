using System.IO;
using Microsoft.Data.Sqlite;

namespace SQLST.Infrastructure;

/// <summary>
/// Yerel SQLite veritabanının (V2-S1) sahibi: %APPDATA%\SQLST\sqlst.db'yi
/// açar, şema sürümünü yönetir ve bağlantı üretir.
///
/// Yolu <see cref="UygulamaVeriYolu"/> verir; 0.5.0 ve öncesindeki
/// %APPDATA%\MiniSSMS\minissms.db'den göç oradadır (WAL yan dosyalarıyla birlikte). Sonraki dilimlerin (V2-S2 sorgu
/// geçmişi, V2-S8 nesne tarihçesi) tabloları buradaki göç listesine eklenir.
///
/// SQLite tek yazar destekler; WAL modu + BeklemeZamanı ile çok-sekmeli okuma/yazma
/// çakışmaları yumuşatılır. Microsoft.Data.Sqlite (MIT); e_sqlite3 native tek-dosya
/// publish'te gömülü gelir (07-r2 §1).
/// </summary>
public sealed class YerelDepo
{
    private readonly string _dosyaYolu;
    private readonly string _baglantiDizesi;

    public YerelDepo() : this(UygulamaVeriYolu.VeritabaniDosyasi)
    {
    }

    public YerelDepo(string dosyaYolu)
    {
        _dosyaYolu = dosyaYolu;
        _baglantiDizesi = new SqliteConnectionStringBuilder
        {
            DataSource = dosyaYolu,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ConnectionString;

        Hazirla();
    }

    /// <summary>Her çağrı yeni açık bağlantı döndürür (using ile kapatılmalı); pragma'lar uygulanır.</summary>
    public SqliteConnection BaglantiAc()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dosyaYolu)!);
        var baglanti = new SqliteConnection(_baglantiDizesi);
        baglanti.Open();
        using (SqliteCommand pragma = baglanti.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return baglanti;
    }

    /// <summary>Göç listesini uygular; her göç bir kez, sürüm sırasıyla çalışır.</summary>
    private void Hazirla()
    {
        using SqliteConnection baglanti = BaglantiAc();

        using (SqliteCommand olustur = baglanti.CreateCommand())
        {
            olustur.CommandText = "CREATE TABLE IF NOT EXISTS SemaSurum (Surum INTEGER NOT NULL);";
            olustur.ExecuteNonQuery();
        }

        int mevcut;
        using (SqliteCommand oku = baglanti.CreateCommand())
        {
            oku.CommandText = "SELECT COALESCE(MAX(Surum), 0) FROM SemaSurum;";
            mevcut = Convert.ToInt32(oku.ExecuteScalar());
        }

        for (int i = mevcut; i < Gocler.Length; i++)
        {
            using SqliteTransaction tran = baglanti.BeginTransaction();
            using (SqliteCommand goc = baglanti.CreateCommand())
            {
                goc.Transaction = tran;
                goc.CommandText = Gocler[i];
                goc.ExecuteNonQuery();
            }
            using (SqliteCommand isaretle = baglanti.CreateCommand())
            {
                isaretle.Transaction = tran;
                isaretle.CommandText = "INSERT INTO SemaSurum (Surum) VALUES ($s);";
                isaretle.Parameters.AddWithValue("$s", i + 1);
                isaretle.ExecuteNonQuery();
            }
            tran.Commit();
        }
    }

    /// <summary>
    /// Göç listesi — SIRA ASLA DEĞİŞMEZ, yalnız SONA eklenir (her indeks bir şema sürümü).
    /// Sürüm 1: ayarlar. Sürüm 2: sorgu geçmişi + oturum sekmeleri (V2-S2).
    /// Sonraki dilimler tarihçe tablolarını buraya ekleyecek (V2-S8).
    /// </summary>
    private static readonly string[] Gocler =
    [
        // 1
        """
        CREATE TABLE Ayar (
            Anahtar TEXT PRIMARY KEY,
            Deger   TEXT NOT NULL
        );
        """,
        // 3 (aşağıda, listenin SONUNDA) — V2-S8 nesne tarihçesi (içerik-hash'li sürümler).
        // 2 — FG-3.7 sorgu geçmişi + FG-3.6 oturum kurtarma.
        //     Zaman UTC ISO-8601 ("o") saklanır, gösterim yerelde Türkçe'ye çevrilir (FOG-9).
        """
        CREATE TABLE SorguGecmisi (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            Sunucu       TEXT NOT NULL,
            Veritabani   TEXT NULL,
            Sql          TEXT NOT NULL,
            BaslangicUtc TEXT NOT NULL,
            SureMs       INTEGER NOT NULL,
            SatirSayisi  INTEGER NOT NULL,
            Durum        TEXT NOT NULL,
            HataMesaji   TEXT NULL
        );
        CREATE INDEX IX_SorguGecmisi_Id ON SorguGecmisi(Id DESC);

        CREATE TABLE OturumSekmesi (
            Sira       INTEGER NOT NULL,
            Baslik     TEXT NOT NULL,
            Veritabani TEXT NULL,
            Sql        TEXT NOT NULL,
            SeciliMi   INTEGER NOT NULL DEFAULT 0,
            OtomatikAd INTEGER NOT NULL DEFAULT 1
        );
        """,
        // 3 — V2-S8 nesne tarihçesi (Ö2): içerik-hash'li sürümler; yalnız bu araçla
        //     görülen anlar (denetim günlüğü değil — 06-r1 Ö2 dürüst sınırı).
        """
        CREATE TABLE NesneTarihcesi (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            Sunucu     TEXT NOT NULL,
            Veritabani TEXT NOT NULL,
            Sema       TEXT NOT NULL,
            Ad         TEXT NOT NULL,
            IcerikHash TEXT NOT NULL,
            Tanim      TEXT NOT NULL,
            GorulmeUtc TEXT NOT NULL,
            Kaynak     TEXT NOT NULL
        );
        CREATE INDEX IX_NesneTarihcesi_Nesne ON NesneTarihcesi(Sunucu, Veritabani, Sema, Ad, Id DESC);
        """,
        // 4 — V3: sorgu geçmişi PROFİL bazlı (kullanıcı isteği 2026-07-18). Her bağlantı
        //     profili kendi geçmişini görür; eski (v2) kayıtlar ProfilId NULL kalır ve
        //     hiçbir profile ait olmadıklarından listede görünmezler (silinmezler).
        """
        ALTER TABLE SorguGecmisi ADD COLUMN ProfilId TEXT NULL;
        CREATE INDEX IX_SorguGecmisi_Profil ON SorguGecmisi(ProfilId, Id DESC);
        """,
        // 5 — V3: açık sekmeler de PROFİL bazlı (kullanıcı isteği 2026-07-18). Bir motorda
        //     açılan sekmeler başka bağlantıya geçince görünmez; her profil kendi çalışma
        //     alanını korur. Eski (profilsiz) kayıtlar hiçbir profile ait olmadıklarından
        //     geri yüklenmez — silinmezler.
        """
        ALTER TABLE OturumSekmesi ADD COLUMN ProfilId TEXT NULL;
        CREATE INDEX IX_OturumSekmesi_Profil ON OturumSekmesi(ProfilId, Sira);
        """,
        // 6 — V3: AYARLAR da profil kapsamlı (kullanıcı kuralı 2026-07-18). Anahtar artık
        //     (ProfilId, Anahtar); ProfilId '' = GENEL değer. Mevcut tercihler GENEL'e taşınır,
        //     böylece eski davranış varsayılan olarak sürer ve profil kendi değerini yazınca
        //     onu ezmeden üzerine biner. SQLite PK değiştirilemediği için tablo yeniden kurulur.
        """
        CREATE TABLE Ayar_yeni (
            ProfilId TEXT NOT NULL DEFAULT '',
            Anahtar  TEXT NOT NULL,
            Deger    TEXT NOT NULL,
            PRIMARY KEY (ProfilId, Anahtar)
        );
        INSERT INTO Ayar_yeni (ProfilId, Anahtar, Deger) SELECT '', Anahtar, Deger FROM Ayar;
        DROP TABLE Ayar;
        ALTER TABLE Ayar_yeni RENAME TO Ayar;
        """,
        // 7 — V3: NESNE TARİHÇESİ de profil kapsamlı. Aynı sunucuya bakan iki profil
        //     (farklı kimlik/motor) birbirinin sürüm zincirini görmemeli ve bozmamalı.
        """
        ALTER TABLE NesneTarihcesi ADD COLUMN ProfilId TEXT NULL;
        DROP INDEX IF EXISTS IX_NesneTarihcesi_Nesne;
        CREATE INDEX IX_NesneTarihcesi_Nesne
            ON NesneTarihcesi(ProfilId, Sunucu, Veritabani, Sema, Ad, Id DESC);
        """,
        // 8 — V5-S4: kod parçaları (snippet). Ayar deposu yalnız SKALER tutar; snippet bir
        //     koleksiyondur, o yüzden evin koleksiyon deseni (ayrı tablo + göç) uygulandı.
        //     Motor NULL = her motorda geçerli; dolu = yalnız o motorda önerilir (çoklu motor
        //     kuralı: T-SQL kalıbı Mongo sekmesinde çıkmamalı).
        //     Yerlesik=1 olanlar kutudan çıkan örneklerdir; kullanıcı düzenleyebilir/silebilir.
        """
        CREATE TABLE Snippet (
            Id       INTEGER PRIMARY KEY AUTOINCREMENT,
            Kisayol  TEXT NOT NULL,
            Baslik   TEXT NOT NULL,
            Govde    TEXT NOT NULL,
            Motor    TEXT NULL,
            Yerlesik INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IX_Snippet_Kisayol ON Snippet(Kisayol, IFNULL(Motor, ''));

        INSERT INTO Snippet (Kisayol, Baslik, Govde, Motor, Yerlesik) VALUES
          ('sel100', 'İlk 100 satır', 'SELECT TOP 100 *' || char(10) || 'FROM $0;', 'Mssql', 1),
          ('sel100', 'İlk 100 satır', 'SELECT *' || char(10) || 'FROM $0' || char(10) || 'LIMIT 100;', 'Postgres', 1),
          ('sel100', 'İlk 100 satır', 'SELECT *' || char(10) || 'FROM $0' || char(10) || 'LIMIT 100;', 'MySql', 1),
          ('sel100', 'İlk 100 satır', 'SELECT *' || char(10) || 'FROM $0' || char(10) || 'FETCH FIRST 100 ROWS ONLY', 'Oracle', 1),
          ('find', 'Belge ara', 'db.$0.find({}).limit(100)', 'Mongo', 1),
          ('agg', 'Toplulaştırma', 'db.$0.aggregate([' || char(10) || '  { "$match": {} },' || char(10) || '  { "$group": { "_id": null, "adet": { "$sum": 1 } } }' || char(10) || '])', 'Mongo', 1),
          ('cte', 'CTE iskeleti', 'WITH veri AS (' || char(10) || '    SELECT $0' || char(10) || ')' || char(10) || 'SELECT * FROM veri;', 'Mssql', 1),
          ('cte', 'CTE iskeleti', 'WITH veri AS (' || char(10) || '    SELECT $0' || char(10) || ')' || char(10) || 'SELECT * FROM veri;', 'Postgres', 1),
          -- NOT: bu kalıp "her motorda" (Motor NULL) OLAMAZ — SELECT COUNT(*) MongoDB'de
          -- geçersizdir. Motor NULL yalnız gerçekten motordan bağımsız metinler içindir;
          -- SQL ailesine özgü kalıplar dört motora AYRI AYRI yazılır.
          ('sayac', 'Satır sayısı', 'SELECT COUNT(*) FROM $0;', 'Mssql', 1),
          ('sayac', 'Satır sayısı', 'SELECT COUNT(*) FROM $0;', 'Postgres', 1),
          ('sayac', 'Satır sayısı', 'SELECT COUNT(*) FROM $0;', 'MySql', 1),
          ('sayac', 'Satır sayısı', 'SELECT COUNT(*) FROM $0', 'Oracle', 1),
          ('sayac', 'Belge sayısı', 'db.$0.countDocuments({})', 'Mongo', 1);
        """,
        // 9 — v10 denetim (2026-07-23): sorgu geçmişine "kim" (Kullanici) eklenir. Eski kayıtlarda
        //     NULL kalır (o dönemde yakalanmadı). İşlem türü SQL'den TÜRETİLİR, sütun tutulmaz.
        """
        ALTER TABLE SorguGecmisi ADD COLUMN Kullanici TEXT NULL;
        """,
        // 10 — kullanıcı isteği 2026-07-23: geçmişte hangi sekmeden/pencereden çalıştırıldığı da
        //      görünsün (SQLST3 gibi otomatik ad ya da kullanıcının kaydettiği ad). Eskilerde NULL.
        """
        ALTER TABLE SorguGecmisi ADD COLUMN SekmeAdi TEXT NULL;
        """,
        // 11 — v14-S3 SOAP İstemcisi: istek geçmişi + ortam profilleri (test/canlı uçları).
        """
        CREATE TABLE SoapGecmisi (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            ZamanUtc  TEXT    NOT NULL,
            Adres     TEXT    NOT NULL,
            Aksiyon   TEXT    NOT NULL,
            Zarf      TEXT    NOT NULL,
            HttpDurum INTEGER NOT NULL,
            SureMs    INTEGER NOT NULL,
            FaultMu   INTEGER NOT NULL
        );
        CREATE TABLE SoapOrtam (
            Ad      TEXT PRIMARY KEY,
            WsdlUrl TEXT NOT NULL,
            Adres   TEXT NOT NULL
        );
        """,
        // 12 — V15-S3 Geri Al paketi (BF-1): Güvenli Yazma COMMIT'inden önce yakalanan eski satırlar.
        """
        CREATE TABLE GeriAlPaketi (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            TarihUtc    TEXT    NOT NULL,
            Sunucu      TEXT    NOT NULL,
            Veritabani  TEXT    NOT NULL,
            Tablo       TEXT    NOT NULL,
            Fiil        TEXT    NOT NULL,
            SqlMetni    TEXT    NOT NULL,
            SatirSayisi INTEGER NOT NULL,
            KolonlarJson TEXT   NOT NULL,
            PkJson      TEXT    NOT NULL,
            SatirlarJson TEXT   NOT NULL
        );
        CREATE INDEX IX_GeriAlPaketi_Tarih ON GeriAlPaketi (TarihUtc);
        """,
        // 13 — v16 SOAP Basic auth: ortam profiline kullanıcı adı + ŞİFRELİ parola (DPAPI).
        """
        ALTER TABLE SoapOrtam ADD COLUMN KullaniciAdi TEXT NULL;
        ALTER TABLE SoapOrtam ADD COLUMN ParolaSifreli TEXT NULL;
        """,
        // 14 — v20-S8 REST İstemcisi: istek geçmişi + ortamlar ({{değişken}}'ler; blob DPAPI ile şifreli,
        //      token gibi sırlar diskte düz durmasın) + adlı kayıtlı istekler.
        """
        CREATE TABLE RestGecmisi (
            Id       INTEGER PRIMARY KEY AUTOINCREMENT,
            ZamanUtc TEXT    NOT NULL,
            Metod    TEXT    NOT NULL,
            Url      TEXT    NOT NULL,
            Durum    INTEGER NOT NULL,
            SureMs   INTEGER NOT NULL
        );
        CREATE TABLE RestOrtam (
            Ad                 TEXT PRIMARY KEY,
            DegiskenlerSifreli TEXT NOT NULL
        );
        CREATE TABLE RestKayitliIstek (
            Ad    TEXT PRIMARY KEY,
            Metod TEXT NOT NULL,
            Url   TEXT NOT NULL,
            Govde TEXT NULL
        );
        """,
    ];
}
