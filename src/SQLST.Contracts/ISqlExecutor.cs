namespace SQLST.Contracts;

public sealed class ExecuteOptions
{
    public static readonly ExecuteOptions Varsayilan = new();

    /// <summary>Grid'e akıtılacak azami satır (FOG-6); aşılırsa okuma durur, sorgu iptal edilir.</summary>
    public int SatirSiniri { get; set; } = 100_000;

    /// <summary>
    /// Grid'e alınacak azami HAM hücre verisi (bayt) — satır sınırının bellek karşılığı
    /// (2026-07-23, kullanıcı bulgusu: "zor sorgularda donuyor/patlıyor"). Sebep: yalnız
    /// satır sınırı geniş satırları (nvarchar(max)/varbinary/XML/çok kolonlu SELECT *)
    /// kısıtlamaz; 100.000 satır × MB'lık değerler = GB'larca bellek → GC çırpınması (donma)
    /// → OutOfMemory (patlama). Satır sınırı VEYA bu bayt bütçesi — hangisi önce dolarsa okuma
    /// durur. Yalnız grid materyalizasyonunu bağlar; "Tümünü dışa aktar" akışı (diske) sınırsız.
    /// <b>256MB→96MB (2026-07-30 crash düzeltmesi):</b> ham List + DataTable + DataView KOPYALARI
    /// aynı anda yaşar (~2-3x) ve <see cref="SonucOkuyucu"/>.SatirBayt gerçekçileşti → gerçek tepe
    /// ~200-300MB'da güvenli kalsın (kısıtlı makinede OOM'u önle); büyük sonuçta "ilk N satır" bandı
    /// görünür, veri kaybı yok.
    /// </summary>
    public long BellekSiniriBayt { get; set; } = 96L * 1024 * 1024;

    /// <summary>
    /// 🧱 AKIŞLI ALICI (v22-S3; kullanıcı: "mantığımızı MSSQL'deki gibi yapalım, gereksiz yüklerden
    /// arındıralım"). Verilirse okuyucu satırları BİRİKTİRMEZ: her satır doğrudan alıcıya verilir ve
    /// <see cref="QueryResult.ResultSetler"/> yalnız kolon/sayaç taşır. Neden: eskiden aynı veri İKİ
    /// KEZ yaşıyordu — önce okuyucunun <c>List&lt;object?[]&gt;</c>'i, sonra onun ÜSTÜNE kurulan
    /// DataTable. ÖLÇÜM (200.000 satır × 10 kolon): iki kopya birlikte satır başına <b>708 bayt</b>,
    /// tek kopya (tipli DataTable) <b>457 bayt</b> — 1 GB'lık tepede 1,5M yerine 2,3M satır.
    /// null → eski davranış (karşılaştırma, dışa aktarım, log analizi gibi satır listesi bekleyen
    /// çağıranlar böyle koşar).
    /// </summary>
    public ISonucAlici? SonucAlici { get; set; }

    /// <summary>Editör üstündeki veritabanı seçicisi profili ezmek isterse (FG-3.12).</summary>
    public string? VeritabaniOverride { get; set; }

    /// <summary>
    /// Bu çalıştırma için komut timeout'unu (saniye) ezer (madde 2, 2026-07-30). Null ise profilin
    /// timeout'u kullanılır (varsayılan 0 = SINIRSIZ). Kaçak tam-tarama riski olan yüzeyler (Log
    /// Analizi: indekssiz zaman kolonunda WHERE >= → devasa tabloda full scan) 30 verir → sonsuz
    /// donma yerine 30 sn'de iptal + kibar hata.
    /// </summary>
    public int? KomutTimeoutSnOverride { get; set; }

    /// <summary>
    /// Kirli okuma (FG-6.4): batch READ UNCOMMITTED izolasyonuyla çalışır — her tabloya
    /// NOLOCK yazmakla aynı etki, SQL yeniden yazılmadan. Bedeli: dirty read, satır
    /// atlama/çiftleme, Msg 601. Varsayılan kapalı.
    /// </summary>
    public bool KirliOkuma { get; set; }

    /// <summary>
    /// İç metadata okumaları (şema/tanım/FK/indeks) için: satır/bayt bütçesini ve bellek-baskısı
    /// küçültmesini UYGULAMA — batch'in TÜM sonuç kümelerini sonuna kadar oku. Sebep (canlı bulgu
    /// 2026-08-09): şema batch'i 4 küme döndürür (db/nesne/kolon/parametre) ama bütçe SETLER ARASI
    /// kümülatif harcanıyor; büyük veritabanında "kolonlar" kümesi bütçeyi doldurunca NextResult
    /// döngüsü duruyor ve 4. küme hiç okunmuyor → SchemaService "4 küme gelmedi" fırlatıyordu. Bu
    /// okumalar şema boyutuyla SINIRLIDIR (OOM riski yok); yalnız iç okumalarda true, kullanıcı grid
    /// sorgularında DAİMA false (grid bütçesi/donma kalkanı orada korunur).
    /// </summary>
    public bool TumKumeler { get; set; }
}

public interface ISqlExecutor
{
    /// <summary>
    /// SQL metnini olduğu gibi sunucuya gönderir, sonucu akışla okur.
    /// Exception fırlatmaz da yutmaz da: SQL kaynaklı her durum QueryResult'a çevrilir;
    /// yalnız programlama hataları exception olarak yükselir.
    /// </summary>
    Task<QueryResult> ExecuteAsync(ConnectionProfile profil, string sql, ExecuteOptions opts, CancellationToken ct);

    Task<(bool Basarili, string? HataMesaji)> TestConnectionAsync(ConnectionProfile profil, CancellationToken ct);
}

/// <summary>Parola şifreleme sözleşmesi; üretim gerçeklemesi DPAPI-CurrentUser (FG-1.3).</summary>
public interface ISecretProtector
{
    string Sifrele(string duzMetin);
    string Coz(string sifreliBase64);
}

public interface IProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(ConnectionProfile profil, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
