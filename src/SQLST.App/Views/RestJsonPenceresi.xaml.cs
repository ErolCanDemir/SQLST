using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace SQLST.App.Views;

/// <summary>
/// REST yanıtını (biçimlenmiş JSON) ayrı, KÜÇÜK ve yeniden boyutlandırılabilir bir pencerede gösterir
/// (v20-S8 "JSON Olarak Aç"). Salt-okunur; JSON sözdizimi vurgulu. Bilgi penceresi olduğundan büyümez
/// açılır — kullanıcı pencere köşesinden büyütür ya da büyüt düğmesiyle tam ekran yapar.
/// </summary>
public partial class RestJsonPenceresi : Window
{
    public RestJsonPenceresi(string json)
    {
        InitializeComponent();
        JsonTema(Metin);
        Metin.Text = json;
        Ozet.Text = $"{Metin.Document.LineCount:N0} satır · {json.Length:N0} karakter";
    }

    private static void JsonTema(TextEditor ed)
    {
        bool koyu = App.KoyuTemaAcik;
        ed.SyntaxHighlighting = EditorTema.JsonTanim(koyu);
        ed.Background = koyu ? new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1a)) : Brushes.White;
        ed.Foreground = koyu ? new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xD6)) : Brushes.Black;
        ed.Options.HighlightCurrentLine = false;
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Metin.Text); Ozet.Text = "Panoya kopyalandı."; }
        catch (Exception ex) { Ozet.Text = $"Kopyalanamadı: {ex.Message}"; }
    }
}
