using System.Windows;

namespace SQLST.App.Views;

/// <summary>Connection string yapıştırma penceresi (FG-3.14) — yalnız metni toplar, çözümleme VM'de.</summary>
public partial class CsPenceresi : Window
{
    public string? Sonuc { get; private set; }

    public CsPenceresi()
    {
        InitializeComponent();
        Loaded += (_, _) => Dize.Focus();
    }

    private void Doldur_Click(object sender, RoutedEventArgs e)
    {
        Sonuc = Dize.Text;
        DialogResult = true;
    }
}
