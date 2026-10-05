using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;

namespace SQLST.App.ViewModels;

/// <summary>
/// "Tablo oluştur/düzenle" sihirbazında (v8-S1 + borç kapanışı) tek bir kolon satırı — grid'de
/// düzenlenir. Çekirdek: ad · tip · NULL · PK · IDENTITY · varsayılan. Kısıtlar: UNIQUE (Benzersiz) ·
/// CHECK (Kontrol ifadesi) · FK (hedef tablo + kolon). <see cref="YeniKolon"/>'a çevrilip üretime girer.
/// </summary>
public sealed partial class KolonGirdisi : ObservableObject
{
    [ObservableProperty] private string _ad = "";
    [ObservableProperty] private string _tip = "int";
    [ObservableProperty] private bool _nullOlabilir;
    [ObservableProperty] private bool _pkMi;
    [ObservableProperty] private bool _identityMi;
    [ObservableProperty] private string _varsayilan = "";
    [ObservableProperty] private bool _benzersizMi;
    [ObservableProperty] private string _kontrol = "";
    [ObservableProperty] private string _fkTablo = "";
    [ObservableProperty] private string _fkKolon = "";

    /// <summary>
    /// JSON→tablo akışında (v20-S12) bu kolonun KAYNAK (orijinal JSON alanı) adı — kullanıcı görsel
    /// tasarımcıda hedef adını (<see cref="Ad"/>) değiştirse bile veri eşlemesi bununla kurulur.
    /// Grid'de gösterilmez/düzenlenmez; elle eklenen kolonda null (yalnız CREATE'e girer, INSERT'e girmez).
    /// </summary>
    public string? KaynakAd { get; init; }

    public YeniKolon Yap() => new(
        Ad.Trim(), Tip.Trim(), NullOlabilir, PkMi, IdentityMi,
        string.IsNullOrWhiteSpace(Varsayilan) ? null : Varsayilan.Trim(),
        BenzersizMi,
        string.IsNullOrWhiteSpace(Kontrol) ? null : Kontrol.Trim(),
        string.IsNullOrWhiteSpace(FkTablo) ? null : FkTablo.Trim(),
        string.IsNullOrWhiteSpace(FkKolon) ? null : FkKolon.Trim());
}
