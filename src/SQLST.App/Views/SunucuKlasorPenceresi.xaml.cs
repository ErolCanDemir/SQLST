using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>📂 Sunucu klasör gezgini (K4) — listeyi köprü verir (xp_dirtree); pencere yalnız gezer ve yol üretir.</summary>
public partial class SunucuKlasorPenceresi : Window
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<KlasorGirisi>>> _listele;
    private string _klasor;

    public string? SecilenYol { get; private set; }

    public SunucuKlasorPenceresi(Func<string, CancellationToken, Task<IReadOnlyList<KlasorGirisi>>> listele, string baslangic, string dosyaAdi)
    {
        _listele = listele;
        _klasor = baslangic;
        InitializeComponent();
        YolKutusu.Text = baslangic;
        DosyaAdi.Text = dosyaAdi;
    }

    public async Task YukleAsync()
    {
        Durum.Text = "Listeleniyor…";
        try
        {
            IReadOnlyList<KlasorGirisi> girisler = await _listele(_klasor, CancellationToken.None);
            Liste.ItemsSource = girisler.Select(g => (g.KlasorMu ? "📁 " : "📄 ") + g.Ad).ToList();
            Durum.Text = $"{girisler.Count(g => g.KlasorMu)} klasör · {girisler.Count(g => !g.KlasorMu)} dosya";
            YolKutusu.Text = _klasor;
        }
        catch (InvalidOperationException ex)
        {
            Liste.ItemsSource = null;
            Durum.Text = "✖ " + ex.Message;
        }
    }

    private async void Ust_Click(object sender, RoutedEventArgs e)
    {
        string k = _klasor.TrimEnd('\\', '/');
        int i = Math.Max(k.LastIndexOf('\\'), k.LastIndexOf('/'));
        if (i <= 0) return;
        _klasor = k[..i];
        if (_klasor.EndsWith(':')) _klasor += "\\";
        await YukleAsync();
    }

    private async void Git_Click(object sender, RoutedEventArgs e)
    {
        _klasor = YolKutusu.Text.Trim();
        await YukleAsync();
    }

    private void Yol_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Git_Click(sender, e); e.Handled = true; }
    }

    private async void Liste_CiftTik(object sender, MouseButtonEventArgs e)
    {
        if (Liste.SelectedItem is string s && s.StartsWith("📁 "))
        {
            _klasor = YedekSorgulari.YolBirlestir(_klasor, s[2..].Trim());
            await YukleAsync();
        }
    }

    private void Liste_Secim(object sender, SelectionChangedEventArgs e)
    {
        if (Liste.SelectedItem is string s && s.StartsWith("📄 "))
            DosyaAdi.Text = s[2..].Trim();
    }

    private void Sec_Click(object sender, RoutedEventArgs e)
    {
        string ad = DosyaAdi.Text.Trim();
        if (ad.Length == 0)
        {
            Durum.Text = "Dosya adı girin.";
            return;
        }
        SecilenYol = YedekSorgulari.YolBirlestir(_klasor, ad);
        DialogResult = true;
    }
}
