using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SQLST.App.Views;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// Find yardımcısı otomatik doldurma UI testi (v11-öncesi #3, 2026-07-25). Gerçek pencerede
/// GERÇEK klavye olayları (TextInput/PreviewKeyDown) ile Compass akışını sürer: Sort kutusuna
/// "_i" yaz → Enter → <c>"_id": </c> tamamlanır → "-" yaz → Enter → <c>"_id": -1</c>.
/// Popup'un açık/doğru olduğunun kanıtı, Enter'ın tam bu dönüşümü yapmasıdır.
/// </summary>
public class MongoBulPenceresiStaTests
{
    [Fact]
    public void Sort_kutusunda_id_yaz_enter_eksi_enter_compass_akisi()
    {
        // Paylaşımlı STA (StaOrtak): kendi thread/Application kurmak sınıf paralelliğinde çakışıyordu.
        string sonMetin = StaOrtak.Sta().Invoke(Kosu);
        Assert.Equal("\"_id\": -1", sonMetin);
    }

    [Fact]
    public void Odak_cikisinda_kutu_sarilir_bicimlenir_bozuksa_aninda_uyarir()
    {
        // v12-S6 (kullanıcı isteği: "sorttaki json işini tüm alanlara ekleyelim"): odak çıkışında
        // çıplak çift {}'ye sarılıp girintilenir; bozuk JSON hatası Üret'i beklemeden görünür.
        (string filterMetin, string hataBandi, string sonrakiHata) = StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoBulPenceresi((_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            Pump(TimeSpan.FromMilliseconds(300));

            var filterKutusu = (TextBox)w.FindName("FilterKutusu");
            var hataMetni = (System.Windows.Controls.TextBlock)w.FindName("HataMetni");

            filterKutusu.Text = "\"KullaniciId\": true"; // çıplak çift — kullanıcıya {} yazdırılmaz
            filterKutusu.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            string duzelen = filterKutusu.Text;
            string temizBant = hataMetni.Text;

            filterKutusu.Text = "\"KullaniciId\": : true"; // canlı yakalanan gerçek hata (çift ':')
            filterKutusu.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            string hatali = hataMetni.Text;

            w.Close();
            return (duzelen, temizBant, hatali);
        });

        Assert.StartsWith("{", filterMetin, StringComparison.Ordinal); // sarıldı + biçimlendi
        Assert.Contains("\"KullaniciId\": true", filterMetin, StringComparison.Ordinal);
        Assert.Equal("", hataBandi);                                   // geçerliyken bant temiz
        Assert.Contains("filter", sonrakiHata, StringComparison.Ordinal); // bozukta anında uyarı
    }

    private static string Kosu()
    {
        StaOrtak.Birlestir("PaletAcik.xaml");
        StaOrtak.Birlestir("Tema.xaml");

        var alanlar = new List<SemaKolonu>
        {
            new("_id", "objectId", false, true),
            new("Mesaj", "string", true, false),
            new("Zaman", "date", true, false),
        };
        var koleksiyonlar = new List<SemaNesnesi>
        {
            new("LogDb", "LogDb", "loglar", SemaNesneTuru.Koleksiyon, alanlar, []),
        };

        var w = new MongoBulPenceresi(
            (_, _) => { },
            mevcutSorgu: null,
            koleksiyonlariGetir: () => Task.FromResult<IReadOnlyList<SemaNesnesi>>(koleksiyonlar))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w.Show();
        Pump(TimeSpan.FromMilliseconds(500)); // Loaded → koleksiyon/alan envanteri dolar

        var koleksiyonKutusu = (TextBox)w.FindName("KoleksiyonKutusu");
        var sortKutusu = (TextBox)w.FindName("SortKutusu");

        koleksiyonKutusu.Text = "loglar"; // alan sözlüğü bu ada bakar
        Pump(TimeSpan.FromMilliseconds(100));

        Yaz(sortKutusu, "_i");
        Pump(TimeSpan.FromMilliseconds(150));
        Tus(sortKutusu, Key.Enter);          // "_id" uygula → "_id":
        Pump(TimeSpan.FromMilliseconds(150));
        Yaz(sortKutusu, "-");
        Pump(TimeSpan.FromMilliseconds(150));
        Tus(sortKutusu, Key.Enter);          // "-1" uygula
        Pump(TimeSpan.FromMilliseconds(150));

        string metin = sortKutusu.Text;
        w.Close();
        return metin;
    }

    /// <summary>Gerçek TextInput olayı — TextBox metni imleçte ekler, TextChanged (adaptör) tetiklenir.</summary>
    private static void Yaz(TextBox kutu, string metin)
    {
        var olay = new TextCompositionEventArgs(
            InputManager.Current.PrimaryKeyboardDevice,
            new TextComposition(InputManager.Current, kutu, metin))
        { RoutedEvent = TextCompositionManager.TextInputEvent };
        kutu.RaiseEvent(olay);
    }

    /// <summary>PreviewKeyDown olayı — adaptörün tuş yakalayıcısına gider (Enter/Tab/ok tuşları).</summary>
    private static void Tus(TextBox kutu, Key tus)
    {
        var kaynak = PresentationSource.FromVisual(kutu)
            ?? throw new InvalidOperationException("PresentationSource yok — pencere gösterilmemiş.");
        var olay = new KeyEventArgs(Keyboard.PrimaryDevice, kaynak, 0, tus)
        { RoutedEvent = UIElement.PreviewKeyDownEvent };
        kutu.RaiseEvent(olay);
    }

    private static void Pump(TimeSpan sure) => StaOrtak.Pump(sure);
}
