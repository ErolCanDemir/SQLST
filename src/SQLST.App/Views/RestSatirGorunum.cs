using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// REST istemcisindeki DÜZENLENEBİLİR anahtar/değer satırı (v20-S8): query parametreleri, HTTP başlıkları
/// ve ortam değişkenleri grid'lerinde ortak. DataGrid iki-yön düzenleme + satır ekleme için düz sınıf
/// (parametresiz kurucu = "yeni satır" için gerekli). <see cref="Model"/> çekirdek <see cref="RestSatir"/>'e döner.
/// </summary>
public sealed class RestSatirGorunum
{
    public bool Etkin { get; set; } = true;
    public string Anahtar { get; set; } = "";
    public string Deger { get; set; } = "";

    /// <summary>Postman'daki Description kolonu (v23 yeniden tasarım) — yalnız ekranda yaşar,
    /// <see cref="Model"/>'e girmez (sunucuya gitmez); URL senkronu satırı güncellerken korur.</summary>
    public string Aciklama { get; set; } = "";

    public RestSatirGorunum() { }

    public RestSatirGorunum(RestSatir s)
    {
        Etkin = s.Etkin;
        Anahtar = s.Anahtar;
        Deger = s.Deger;
    }

    public RestSatir Model() => new(Anahtar ?? "", Deger ?? "", Etkin);
}
