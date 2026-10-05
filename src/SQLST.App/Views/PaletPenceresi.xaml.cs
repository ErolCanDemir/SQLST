using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SQLST.App.ViewModels;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// Ctrl+P "her yere atla" paleti (V2-S10, FG-2.9): yazdıkça açık sekmeler, yüklü
/// nesneler, veritabanları ve komutlar süzülür; Enter seçileni çalıştırır.
/// Odak kaybında kendini kapatır (palet davranışı).
/// </summary>
public partial class PaletPenceresi : Window
{
    private readonly IReadOnlyList<MainViewModel.PaletOgesi> _havuz;
    private bool _kapaniyor;

    public PaletPenceresi(IReadOnlyList<MainViewModel.PaletOgesi> havuz)
    {
        InitializeComponent();
        _havuz = havuz;
        Suz("");
        Loaded += (_, _) => Arama.Focus();
    }

    private void Suz(string sorgu)
    {
        Sonuclar.ItemsSource = PaletEslestirici.Sirala(sorgu, _havuz, o => o.Gosterim);
        if (Sonuclar.Items.Count > 0)
            Sonuclar.SelectedIndex = 0;
    }

    private void Arama_TextChanged(object sender, TextChangedEventArgs e) => Suz(Arama.Text);

    private async void Arama_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Kapat();
                e.Handled = true;
                break;
            case Key.Down when Sonuclar.Items.Count > 0:
                Sonuclar.SelectedIndex = Math.Min(Sonuclar.SelectedIndex + 1, Sonuclar.Items.Count - 1);
                Sonuclar.ScrollIntoView(Sonuclar.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when Sonuclar.Items.Count > 0:
                Sonuclar.SelectedIndex = Math.Max(Sonuclar.SelectedIndex - 1, 0);
                Sonuclar.ScrollIntoView(Sonuclar.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await SeciliyiCalistirAsync();
                break;
        }
    }

    private async void Sonuc_CiftTik(object sender, MouseButtonEventArgs e) => await SeciliyiCalistirAsync();

    private async Task SeciliyiCalistirAsync()
    {
        if (Sonuclar.SelectedItem is not MainViewModel.PaletOgesi oge)
            return;
        Kapat(); // önce kapan — açılacak sekme/pencere odağı alsın
        await oge.Calistir();
    }

    /// <summary>Close sırasında Deactivated tekrar Close çağırabilir — tek giriş.</summary>
    private void Kapat()
    {
        if (_kapaniyor)
            return;
        _kapaniyor = true;
        Close();
    }

    private void Pencere_Deactivated(object sender, EventArgs e) => Kapat();
}
