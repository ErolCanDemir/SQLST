using System.Windows;

namespace SQLST.App.Views;

/// <summary>
/// Edit modu "Show Script" önizlemesi (V2-S5, FG-4.9): üretilen DML uygulanmadan
/// önce olduğu gibi gösterilir — kullanıcı ne çalışacağını görmeden onay vermez.
/// </summary>
public partial class DmlOnizlemePenceresi : Window
{
    public DmlOnizlemePenceresi(string script)
    {
        InitializeComponent();
        ScriptKutusu.Text = script;
        KomutSayisi.Text = $"{script.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} komut";
    }

    private void Uygula_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
