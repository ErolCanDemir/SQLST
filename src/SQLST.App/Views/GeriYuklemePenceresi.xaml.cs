using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SQLST.App.ViewModels;

namespace SQLST.App.Views;

/// <summary>⏪ Geri yükle penceresi kod-arkası (v23-S26 S2): açılışta sunucu bilgisi + tarihçe, sunucu gezgini, tarihçeden seçim, kapatma.</summary>
public partial class GeriYuklemePenceresi : Window
{
    private readonly GeriYuklemeViewModel _vm;

    public GeriYuklemePenceresi(GeriYuklemeViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await _vm.YukleAsync();
    }

    private void Kapat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Calisiyor && !Iletisim.Sor(this, "SQLST — Geri yükle", "Geri yükleme sürüyor",
                "Pencere kapansa da sunucudaki geri yükleme devam eder; ilerlemeyi göremezsiniz. Kapatılsın mı?", "Kapat"))
            return;
        Close();
    }

    private async void SunucudaSec_Click(object sender, RoutedEventArgs e)
    {
        string yol = _vm.Dosya.Trim();
        int i = Math.Max(yol.LastIndexOf('\\'), yol.LastIndexOf('/'));
        string klasor = i > 0 ? yol[..i] : (_vm.Bilgi?.VeriKlasoru ?? "");
        string ad = i > 0 ? yol[(i + 1)..] : "";
        var gezgin = new SunucuKlasorPenceresi(KlasorListeleAsync, klasor, ad) { Owner = this };
        await gezgin.YukleAsync();
        if (gezgin.ShowDialog() == true && gezgin.SecilenYol is { } secilen)
        {
            _vm.Dosya = secilen;
            await _vm.OkuAsync(null);
        }
    }

    private async Task<IReadOnlyList<Application.KlasorGirisi>> KlasorListeleAsync(string yol, CancellationToken ct)
    {
        Contracts.QueryResult r = await _vm.KlasorKomutuAsync(yol, ct);
        if (r.Hata is not null)
            throw new InvalidOperationException("Klasör listelenemedi: " + r.Hata.Mesaj);
        return Application.YedekSorgulari.KlasorOku(r);
    }

    private async void Tarihce_CiftTik(object sender, MouseButtonEventArgs e)
    {
        if (((DataGrid)sender).SelectedItem is TarihceGorunum kayit)
            await _vm.TarihcedenSecAsync(kayit);
    }
}
