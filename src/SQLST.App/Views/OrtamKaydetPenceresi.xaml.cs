using System.Windows;

namespace SQLST.App.Views;

/// <summary>Ortam profiline ad soran mini pencere (v14-S3). Aynı ad varsa üzerine yazılacağı
/// bilgide söylenir; DialogResult true = kaydet (ad <see cref="OrtamAdi"/>'nda).</summary>
public partial class OrtamKaydetPenceresi : Window
{
    public string OrtamAdi => Ad.Text.Trim();

    public OrtamKaydetPenceresi(string wsdlUrl, string adres)
    {
        InitializeComponent();
        Bilgi.Text = $"WSDL: {wsdlUrl}\nAdres: {adres}\nAynı adla kayıt varsa üzerine yazılır.";
        Ad.Focus();
    }

    private void Kaydet_Click(object sender, RoutedEventArgs e)
    {
        if (OrtamAdi.Length == 0)
            return;
        DialogResult = true;
        Close();
    }

    private void Iptal_Click(object sender, RoutedEventArgs e) => Close();
}
