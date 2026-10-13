using System.Windows;
using SQLST.App.ViewModels;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>💾 Yedek al penceresi kod-arkası (v23-S26 S1): açılışta sunucu bilgisi, sunucu klasör gezgini, kapatma.</summary>
public partial class YedekPenceresi : Window
{
    private readonly YedekPenceresiViewModel _vm;

    public YedekPenceresi(YedekPenceresiViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await _vm.YukleAsync();
    }

    private void Kapat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Calisiyor && !Iletisim.Sor(this, "SQLST — Yedek al", "Yedekleme sürüyor",
                "Pencere kapansa da sunucudaki yedekleme devam eder; ilerlemeyi göremezsiniz. Kapatılsın mı?", "Kapat"))
            return;
        Close();
    }

    /// <summary>K4: sunucu tarafı klasör gezgini — yerel dosya diyaloğu KULLANILMAZ.</summary>
    private async void SunucudaSec_Click(object sender, RoutedEventArgs e)
    {
        string baslangic = KlasorKismi(_vm.Dosya);
        if (string.IsNullOrWhiteSpace(baslangic))
            baslangic = _vm.Bilgi?.VarsayilanKlasor ?? "";
        var gezgin = new SunucuKlasorPenceresi(_vm.KlasorListeleAsync, baslangic, _vm.Dosya.Contains('\\') || _vm.Dosya.Contains('/')
            ? _vm.Dosya[(Math.Max(_vm.Dosya.LastIndexOf('\\'), _vm.Dosya.LastIndexOf('/')) + 1)..] : "") { Owner = this };
        await gezgin.YukleAsync();
        if (gezgin.ShowDialog() == true && gezgin.SecilenYol is { } yol)
            _vm.Dosya = yol;
    }

    private static string KlasorKismi(string yol)
    {
        int i = Math.Max(yol.LastIndexOf('\\'), yol.LastIndexOf('/'));
        return i > 0 ? yol[..i] : "";
    }
}
