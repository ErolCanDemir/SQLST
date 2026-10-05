using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace SQLST.App.Views;

/// <summary>
/// #11 (kullanıcı isteği 2026-07-29): "INSERT örneği" için kaç satır üretileceğini soran mini pencere.
/// Sonuç <see cref="Satir"/>'da (1–1000); ShowDialog true dönerse geçerli değer alınmıştır.
/// </summary>
public partial class InsertSatirPenceresi : Window
{
    public int Satir { get; private set; } = 1;

    public InsertSatirPenceresi()
    {
        InitializeComponent();
        // Seçili gelmesin (seçiliyken sayı okunmuyordu — kullanıcı bulgusu 2026-07-29); imleç sona.
        Loaded += (_, _) => { SatirKutusu.Focus(); SatirKutusu.CaretIndex = SatirKutusu.Text.Length; };
    }

    private void SadeceRakam(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void Tamam_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SatirKutusu.Text, out int n) && n is >= 1 and <= 1000)
        {
            Satir = n;
            DialogResult = true;
        }
        else
        {
            Uyari.Text = "1 ile 1000 arası bir sayı girin.";
            Uyari.Visibility = Visibility.Visible;
        }
    }

    private void Iptal_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
