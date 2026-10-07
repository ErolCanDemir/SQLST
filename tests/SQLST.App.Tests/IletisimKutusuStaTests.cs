using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SQLST.App.Views;

namespace SQLST.App.Tests;

/// <summary>
/// 🪟 SQLST iletişim kutusu (v23-S20 — mockup onaylı): Windows MessageBox'ın temalı yeri. Düğme metinleri
/// ve sırası (Vazgeç solda, ana sağda), Enter/Esc sözleşmesi (TEHLİKEDE Enter = Vazgeç, odak Vazgeç'te),
/// tür ikonu rengi temadan, ayrıntı alanı + kopyala yalnız ayrıntı varken, ana düğme → Onaylandi.
/// </summary>
public class IletisimKutusuStaTests
{
    private static T Bul<T>(DependencyObject kok, string ad) where T : FrameworkElement
        => (T)((FrameworkElement)kok).FindName(ad)!;

    private static Button[] Dugmeler(IletisimKutusu k) => [.. Bul<StackPanel>(k, "Dugmeler").Children.OfType<Button>()];

    private static IletisimKutusu Ac(IletisimTuru tur, string ana, string? vazgec, string? ayrinti = null)
    {
        StaOrtak.Birlestir("PaletAcikVS.xaml");
        StaOrtak.Birlestir("Tema.xaml");
        var k = new IletisimKutusu(tur, "SQLST — SQL Agent", "Job başlatılsın mı?", "\"Fatura\" şimdi başlatılacak.",
            ayrinti, ana, vazgec)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        k.Show();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(150));
        return k;
    }

    [Fact]
    public void Soru_anlamli_dugmeler_enter_ana_esc_vazgec()
    {
        StaOrtak.Sta().Invoke(() =>
        {
            IletisimKutusu k = Ac(IletisimTuru.Soru, "▶ Başlat", "Vazgeç");
            Button[] d = Dugmeler(k);
            Assert.Equal(["Vazgeç", "▶ Başlat"], d.Select(x => (string)x.Content));
            Assert.True(d[1].IsDefault);
            Assert.True(d[0].IsCancel);
            Assert.False(d[0].IsDefault);
            Assert.Equal("Job başlatılsın mı?", Bul<TextBlock>(k, "BaslikMetni").Text);
            Assert.Equal("SQLST — SQL Agent", k.Title);
            Assert.Equal(Visibility.Collapsed, Bul<Expander>(k, "AyrintiAlani").Visibility);

            d[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(k.Onaylandi);
        });
    }

    [Fact]
    public void Tehlike_kirmizi_ana_dugme_enter_ve_odak_vazgecte()
    {
        StaOrtak.Sta().Invoke(() =>
        {
            IletisimKutusu k = Ac(IletisimTuru.Tehlike, "Yine de çalıştır", "Vazgeç");
            Button[] d = Dugmeler(k);
            Assert.True(d[0].IsDefault);                     // Enter → Vazgeç
            Assert.False(d[1].IsDefault);
            Assert.True(d[0].IsKeyboardFocused || d[0].IsFocused);
            Assert.Same(IletisimKutusu.TehlikeDolgusu, d[1].Background);

            d[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(k.Onaylandi);
        });
    }

    [Fact]
    public void Hata_ayrintili_tek_dugme_esc_de_kapatir_ikon_temadan()
    {
        StaOrtak.Sta().Invoke(() =>
        {
            IletisimKutusu k = Ac(IletisimTuru.Hata, "Tamam", null, "IOException: dosya kilitli");
            Button d = Assert.Single(Dugmeler(k));
            Assert.True(d.IsDefault && d.IsCancel);
            Assert.Equal(Visibility.Visible, Bul<Expander>(k, "AyrintiAlani").Visibility);
            Assert.Equal(Visibility.Visible, Bul<Button>(k, "KopyalaDugmesi").Visibility);
            Assert.Equal("IOException: dosya kilitli", Bul<TextBox>(k, "AyrintiMetni").Text);
            Assert.Equal(k.FindResource("TehlikeFircasi"), Bul<TextBlock>(k, "IkonMetni").Foreground);
            k.Close();
        });
    }

    [Fact]
    public void Istisna_turune_gore_kullanici_aciklamasi()
    {
        Assert.Contains("başka bir program", Iletisim.KullaniciAciklamasi(new IOException("x")));
        Assert.Contains("izniniz yok", Iletisim.KullaniciAciklamasi(new UnauthorizedAccessException()));
        Assert.Contains("bulunamadı", Iletisim.KullaniciAciklamasi(new FileNotFoundException()));
        Assert.Equal("özel", Iletisim.KullaniciAciklamasi(new InvalidOperationException("özel")));
    }
}
