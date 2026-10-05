namespace SQLST.Contracts;

/// <summary>Bir SOAP operasyonunun girdi parametresi (görüntü + zarf üretimi için).</summary>
public sealed record SoapParametre(string Ad, string Tip, bool Dizi, bool SecimlikMi);

/// <summary>
/// WSDL'den çözülen bir operasyon (v14-S1). <paramref name="SoapAction"/> HTTP başlığına gider;
/// <paramref name="OrnekZarf"/> document/literal-wrapped gövdesiyle hazır SOAP 1.1 zarfıdır —
/// kullanıcı ekranda düzenleyip gönderir (S2).
/// </summary>
public sealed record SoapOperasyon(
    string Ad,
    string SoapAction,
    IReadOnlyList<SoapParametre> Parametreler,
    string OrnekZarf);

/// <summary>
/// Çözülmüş WSDL servisi: uç adres + operasyonlar. <paramref name="Uyarilar"/> çözümleyicinin
/// atladığı/anlayamadığı kısımlardır (girift XSD uçları) — sessiz kalınmaz, ekranda gösterilir;
/// zarf elle düzenlenebildiğinden bunlar çoğu zaman engel değildir.
/// </summary>
public sealed record SoapServis(
    string Ad,
    string Adres,
    IReadOnlyList<SoapOperasyon> Operasyonlar,
    IReadOnlyList<string> Uyarilar);

/// <summary>SOAP çağrısının sonucu: HTTP durumu + gövde + süre; Fault ayrıca işaretlenir.</summary>
public sealed record SoapCevap(
    int HttpDurum,
    string Govde,
    TimeSpan Sure,
    bool FaultMu,
    string? Hata);

/// <summary>Gönderilmiş bir SOAP isteğinin kalıcı kaydı (v14-S3) — çift tıkla geri yüklenir.</summary>
public sealed record SoapGecmisKaydi(
    long Id,
    DateTime ZamanUtc,
    string Adres,
    string Aksiyon,
    string Zarf,
    int HttpDurum,
    long SureMs,
    bool FaultMu);

/// <summary>
/// HTTP kimlik doğrulama (v16 Basic; v19-S2'de Bearer eklendi). Hem WSDL indirmede hem SOAP
/// çağrısında Authorization başlığına gider. Tür ÖRTÜK kodlanır (depo şeması değişmesin):
/// kullanıcı adı doluysa Basic; kullanıcı adı BOŞ + parola alanı doluysa parola TOKEN'dır
/// (Bearer) — Basic tanımı gereği kullanıcı istediğinden çakışma olmaz. İkisi de boşsa
/// başlık eklenmez.
/// </summary>
public sealed record SoapKimlik(string? KullaniciAdi, string? Parola)
{
    public bool Dolu => !string.IsNullOrEmpty(KullaniciAdi) || !string.IsNullOrEmpty(Parola);

    /// <summary>Bearer mi: kullanıcı adı yok, yalnız token (Parola alanında) var (v19-S2).</summary>
    public bool BearerMi => string.IsNullOrEmpty(KullaniciAdi) && !string.IsNullOrEmpty(Parola);
}

/// <summary>
/// Ortam profili (v14-S3): aynı servisin test/canlı uçları arasında tek seçimle geçiş.
/// v16: Basic auth kullanıcı adı + ŞİFRELİ parola da taşınır (parola DPAPI ile saklanır).
/// </summary>
public sealed record SoapOrtamKaydi(
    string Ad, string WsdlUrl, string Adres, string? KullaniciAdi = null, string? ParolaSifreli = null);

/// <summary>SOAP istemcisinin kalıcı deposu: istek geçmişi + ortam profilleri.</summary>
public interface ISoapDeposu
{
    Task GecmisEkleAsync(SoapGecmisKaydi kayit, int enCok = 200, CancellationToken ct = default);

    Task<IReadOnlyList<SoapGecmisKaydi>> GecmisAsync(int enCok = 200, CancellationToken ct = default);

    /// <summary>Aynı adla varsa ÜZERİNE yazar (ortam güncelleme = yeniden kaydetme).</summary>
    Task OrtamKaydetAsync(SoapOrtamKaydi ortam, CancellationToken ct = default);

    Task<IReadOnlyList<SoapOrtamKaydi>> OrtamlarAsync(CancellationToken ct = default);
}
