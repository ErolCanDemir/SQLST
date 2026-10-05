using System.Windows;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// JSON/uzun hücre görüntüleyici (V2-S6, FG-4.7): grid hücresine sığmayan içerik
/// tam metin olarak; geçerli JSON girintili biçimlenir.
/// </summary>
public partial class HucrePenceresi : Window
{
    private readonly Func<Task<string>>? _lookupArastir; // v20-S14 madde 3: FK ise referans açıklaması

    public HucrePenceresi(object? deger, Func<Task<string>>? lookupArastir = null, string? sqlTip = null)
    {
        InitializeComponent();
        (string metin, bool jsonMu) = HucreGoruntuleyici.Bicimlendir(deger, sqlTip); // v23-S13: ham
        Icerik.Text = metin;
        BicimNotu.Text = jsonMu ? "✔ JSON algılandı — girintili gösteriliyor" : $"{metin.Length:N0} karakter";

        _lookupArastir = lookupArastir;
        if (_lookupArastir is not null)
            LookupDugmesi.Visibility = Visibility.Visible; // yalnız FK-lookup delegesi verildiğinde
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
        => Clipboard.SetText(Icerik.Text);

    /// <summary>madde 3: hücrenin kolonu FK ise referans (lookup) tablonun açıklamasını getirip gösterir.</summary>
    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        if (_lookupArastir is null)
            return;
        LookupDugmesi.IsEnabled = false;
        LookupSonucKutu.Visibility = Visibility.Visible;
        LookupSonuc.Text = "Araştırılıyor…";
        try { LookupSonuc.Text = await _lookupArastir(); }
        catch (Exception ex) { LookupSonuc.Text = $"⚠ {ex.Message}"; }
        finally { LookupDugmesi.IsEnabled = true; }
    }
}
