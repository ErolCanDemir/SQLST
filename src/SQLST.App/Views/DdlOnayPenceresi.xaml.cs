using System.Windows;

namespace SQLST.App.Views;

/// <summary>
/// Yeni tablo DDL onayı (v13-S4): İçe Aktar'ın "yeni tablo" yolunda üretilen CREATE script'i
/// ÇALIŞTIRILMADAN önce burada gösterilir — kopyalanabilir; onay olmadan hiçbir DDL koşmaz
/// (v8 sihirbazıyla aynı ilke). ShowDialog true = "Oluştur ve aktar".
/// </summary>
public partial class DdlOnayPenceresi : Window
{
    public DdlOnayPenceresi(string script)
    {
        InitializeComponent();
        Script.Text = script;
    }

    private void Onayla_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Iptal_Click(object sender, RoutedEventArgs e) => Close();
}
