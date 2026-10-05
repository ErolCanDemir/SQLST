using System.Windows;

namespace SQLST.App.Views;

/// <summary>Kolon bazlı toplu güncelleme girdisi (V5-S5). Yalnız SORAR — uygulamaz.</summary>
public partial class TopluGuncellemePenceresi : Window
{
    public TopluGuncellemePenceresi(IReadOnlyList<string> kolonlar)
    {
        InitializeComponent();
        KolonKutusu.ItemsSource = kolonlar;
        KolonKutusu.SelectedIndex = kolonlar.Count > 0 ? 0 : -1;
    }

    /// <summary>Seçilen kolon ve değer; <c>Deger</c> null ise NULL atanacak demektir.</summary>
    public (string Kolon, string? Deger)? Sonuc { get; private set; }

    private void Tamam_Click(object sender, RoutedEventArgs e)
    {
        if (KolonKutusu.SelectedItem is not string kolon)
            return;

        Sonuc = (kolon, NullKutusu.IsChecked == true ? null : DegerKutusu.Text);
        DialogResult = true;
    }
}
