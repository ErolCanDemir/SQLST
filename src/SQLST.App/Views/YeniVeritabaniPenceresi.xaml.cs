using System.Windows;

namespace SQLST.App.Views;

/// <summary>
/// Yeni veritabanı adı soran mini pencere (kullanıcı isteği 2026-07-31: "yeni database
/// oluşturacağımız alan yok"). ShowDialog true dönerse <see cref="Ad"/> geçerli bir addır;
/// oluşturma işi çağırandadır (MainViewModel.YeniVeritabaniOlusturAsync).
/// </summary>
public partial class YeniVeritabaniPenceresi : Window
{
    public string Ad { get; private set; } = "";

    public YeniVeritabaniPenceresi()
    {
        InitializeComponent();
        Loaded += (_, _) => AdKutusu.Focus();
    }

    private void Tamam_Click(object sender, RoutedEventArgs e)
    {
        string ad = AdKutusu.Text.Trim();
        if (ad.Length == 0)
        {
            Uyari.Text = "Ad boş olamaz.";
            Uyari.Visibility = Visibility.Visible;
            return;
        }

        Ad = ad; // özel karakterler lehçenin tırnaklamasıyla güvenle taşınır
        DialogResult = true;
    }

    private void Iptal_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
