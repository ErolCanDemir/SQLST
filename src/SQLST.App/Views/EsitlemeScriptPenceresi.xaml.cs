using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SQLST.App.Views;

/// <summary>
/// 🔀 Eşitleme script'i penceresi (madde 4, 2026-08-03): üretilen DML SEKMEDE AÇILMAZ — sekme
/// AKTİF profile bağlıdır, eşitleme hedefi ise ayrı bir bağlantı olabilir (yanlış sunucuda koşma
/// riski). Script burada gösterilir; kullanıcı kopyalayıp/kaydedip HEDEF bağlantıda çalıştırır.
/// </summary>
public partial class EsitlemeScriptPenceresi : Window
{
    public EsitlemeScriptPenceresi(string baslik, string script)
    {
        InitializeComponent();
        Baslik.Text = baslik;
        Metin.Text = script;
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(Metin.Text);
        Baslik.Text = Baslik.Text.EndsWith("  ✓ kopyalandı", StringComparison.Ordinal)
            ? Baslik.Text : Baslik.Text + "  ✓ kopyalandı";
    }

    private void Kaydet_Click(object sender, RoutedEventArgs e)
    {
        var diyalog = new SaveFileDialog
        {
            Filter = "SQL dosyası (*.sql)|*.sql|Tüm dosyalar (*.*)|*.*",
            FileName = "esitleme.sql",
        };
        if (diyalog.ShowDialog(this) == true)
            File.WriteAllText(diyalog.FileName, Metin.Text);
    }
}
