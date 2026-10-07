using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SQLST.App.ViewModels;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// Code-behind incedir: parolayı PasswordBox'tan alıp VM metotlarına geçirmek ve
/// pencere geçişini yapmak dışında mantık içermez (parola VM'de alan olarak tutulmaz).
/// </summary>
public partial class BaglantiPenceresi : Window
{
    private readonly BaglantiEkraniViewModel _vm;
    private readonly Func<MainWindow> _anaPencereGetir;

    /// <summary>Bu pencereden başarıyla bağlanıldı mı (değiştirme akışında "vazgeçti" ayrımı için).</summary>
    public bool BaglantiKuruldu { get; private set; }

    public BaglantiPenceresi(BaglantiEkraniViewModel vm, Func<MainWindow> anaPencereGetir)
    {
        InitializeComponent();
        _vm = vm;
        _anaPencereGetir = anaPencereGetir;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.YukleAsync();
    }

    private void Yeni_Click(object sender, RoutedEventArgs e)
    {
        ListProfiller.SelectedItem = null;
        _vm.YeniForm();
        ParolaKutusu.Clear();
    }

    /// <summary>FG-3.14: yapıştırılan connection string formu doldurur; parola PasswordBox'a gider.</summary>
    private void CsIceAktar_Click(object sender, RoutedEventArgs e)
    {
        var pencere = new CsPenceresi { Owner = this };
        if (pencere.ShowDialog() != true || pencere.Sonuc is not { } dize)
            return;

        ListProfiller.SelectedItem = null;
        (bool basarili, string? parola) = _vm.ConnectionStringdenDoldur(dize);
        ParolaKutusu.Password = basarili && parola is not null ? parola : "";
    }

    private async void Sil_Click(object sender, RoutedEventArgs e)
    {
        if (ListProfiller.SelectedItem is not ConnectionProfile secili)
            return;
        if (Iletisim.Sor(this, "SQLST — Bağlantılar", "Bağlantı profili silinsin mi?",
                $"'{secili.Ad}' bağlantı profili silinecek.", "🗑 Sil", IletisimTuru.Tehlike))
            await _vm.SilAsync(secili);
    }

    private void Profil_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListProfiller.SelectedItem is ConnectionProfile secili)
        {
            _vm.FormaYukle(secili);
            ParolaKutusu.Clear();
        }
    }

    private async void Profil_CiftTik(object sender, MouseButtonEventArgs e)
    {
        if (ListProfiller.SelectedItem is ConnectionProfile)
            await BaglanVeAc();
    }

    private async void TestEt_Click(object sender, RoutedEventArgs e)
        => await _vm.TestEtAsync(ParolaKutusu.Password);

    private async void Kaydet_Click(object sender, RoutedEventArgs e)
        => await _vm.KaydetAsync(ParolaKutusu.Password);

    private async void Baglan_Click(object sender, RoutedEventArgs e)
        => await BaglanVeAc();

    private async Task BaglanVeAc()
    {
        ConnectionProfile? profil = await _vm.BaglanAsync(ParolaKutusu.Password);
        if (profil is null)
            return;

        BaglantiKuruldu = true;
        MainWindow ana = _anaPencereGetir();
        ana.ProfilUygula(profil);
        ana.Show(); // açılışta ilk kez, değiştirme akışında Hide'dan geri — iki yol da AYNI (kanıtlı) hat
        Close();
    }
}
