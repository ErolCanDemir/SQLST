namespace SQLST.Contracts;

/// <summary>HTTP metodu (REST İstemcisi, v20-S8).</summary>
public enum HttpMetodu { GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS }

/// <summary>
/// Anahtar/değer satırı — REST istemcisinde query parametresi, HTTP başlığı VE ortam değişkeni için ortak
/// (hepsi ad→değer + etkin bayrağı). <paramref name="Etkin"/> false ise gönderilmez/uygulanmaz.
/// </summary>
public sealed record RestSatir(string Anahtar, string Deger, bool Etkin = true);

/// <summary>
/// Bir REST isteği (v20-S8): metod + URL (ham, <c>{{değişken}}</c> içerebilir) + query parametreleri +
/// başlıklar + gövde + kimlik. Kimlik SOAP ile ORTAK <see cref="SoapKimlik"/>'tir (Basic/Bearer). Query
/// parametreleri URL'e ayrıca eklenir; URL'de zaten <c>?</c> varsa çözümleyici birleştirir.
/// </summary>
public sealed record RestIstek(
    HttpMetodu Metod,
    string Url,
    IReadOnlyList<RestSatir> QueryParametreleri,
    IReadOnlyList<RestSatir> Basliklar,
    string? Govde,
    SoapKimlik? Kimlik);

/// <summary>
/// REST yanıtı: HTTP durumu + durum metni + başlıklar + gövde + süre + boyut. <paramref name="Hata"/>
/// yalnız ağ/istisna durumunda doludur (HTTP hata KODU değil — 4xx/5xx gövdeyle normal döner, asıl
/// bilgi çoğu zaman gövdededir).
/// </summary>
public sealed record RestCevap(
    int Durum,
    string DurumMetni,
    IReadOnlyList<RestSatir> Basliklar,
    string Govde,
    TimeSpan Sure,
    long Boyut,
    string? IcerikTipi,
    string? Hata);

/// <summary>
/// REST ortamı (v20-S8): <c>{{değişken}}</c> çözümü için ad→değer eşlemesi (ör. <c>baseUrl</c>, <c>token</c>).
/// Değer bir <see cref="RestSatir"/>'dir; hassas değerler (token) depoda DPAPI ile şifreli saklanabilir.
/// </summary>
public sealed record RestOrtam(string Ad, IReadOnlyList<RestSatir> Degiskenler);

/// <summary>Gönderilmiş bir REST isteğinin kalıcı kaydı — çift tıkla geri yüklenir.</summary>
public sealed record RestGecmisKaydi(
    long Id, DateTime ZamanUtc, string Metod, string Url, int Durum, long SureMs);

/// <summary>Kaydedilmiş (adlı) bir REST isteği — sol paneldeki "Kayıtlı istekler".</summary>
public sealed record RestKayitliIstek(string Ad, string Metod, string Url, string? Govde);

/// <summary>REST istemcisinin kalıcı deposu: istek geçmişi + ortamlar + kayıtlı istekler.</summary>
public interface IRestDeposu
{
    Task GecmisEkleAsync(RestGecmisKaydi kayit, int enCok = 200, CancellationToken ct = default);
    Task<IReadOnlyList<RestGecmisKaydi>> GecmisAsync(int enCok = 200, CancellationToken ct = default);

    /// <summary>Aynı adla varsa ÜZERİNE yazar (ortam güncelleme = yeniden kaydetme).</summary>
    Task OrtamKaydetAsync(RestOrtam ortam, CancellationToken ct = default);
    Task<IReadOnlyList<RestOrtam>> OrtamlarAsync(CancellationToken ct = default);

    Task IstekKaydetAsync(RestKayitliIstek istek, CancellationToken ct = default);
    Task<IReadOnlyList<RestKayitliIstek>> KayitliIsteklerAsync(CancellationToken ct = default);
    Task IstekSilAsync(string ad, CancellationToken ct = default);
}
