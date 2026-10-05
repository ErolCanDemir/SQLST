using System.ComponentModel;

namespace SQLST.App;

/// <summary>
/// 🎨 Etkin temanın DÜZEN bayrakları (v23-S14). Renk paleti tek başına sekme şeklini, düğme
/// dilini ya da anahtar kutusunu değiştiremez — bunlar şablon işidir. Antrasit ailesi eski tema
/// çalışmasındaki "B" ekranının KLASİK düzenini ister (kullanıcı onaylı mockup:
/// docs/mockup/antrasit-tema.html): kutulu sekmeler · düz renksiz düğmeler · onay kutusu
/// anahtarlar · Segoe UI ızgaralı grid · antrasit bağlantı bandında beyaz metin.
///
/// Tema.xaml stilleri bu özelliğe <c>{Binding Path=(app:TemaDurumu.Klasik)}</c> ile DataTrigger
/// kurar; <see cref="StaticPropertyChanged"/> sayesinde tema canlı değişince ANINDA güncellenir.
/// Diğer aileler için false — o stillerin görünümü birebir eskisi.
/// </summary>
public static class TemaDurumu
{
    private static bool _klasik;

    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    /// <summary>Klasik (B) düzen etkin mi — yalnız Antrasit ailesinde true.</summary>
    public static bool Klasik
    {
        get => _klasik;
        set
        {
            if (_klasik == value)
                return;
            _klasik = value;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Klasik)));
        }
    }

    /// <summary>Klasik düzeni kullanan aile adları (palet seçimindeki ad).</summary>
    public static bool KlasikAileMi(string? aile) => aile == "antrasit";
}
