using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SQLST.App.Views;

/// <summary>İletişim kutusunun türü — ikon ve ana düğmenin rengi buna göre.</summary>
public enum IletisimTuru
{
    Soru,
    Bilgi,
    Uyari,
    Hata,

    /// <summary>Geri alınamaz işlem: ana düğme KIRMIZI, varsayılan odak ve Enter "Vazgeç"te.</summary>
    Tehlike,
}

/// <summary>
/// 🪟 SQLST iletişim kutusu (v23-S20 — mockup docs/mockup/iletisim-kutulari.html ONAYLI). Windows
/// MessageBox'ın temalı yerine: açık/koyu palet, kalın başlık + mesaj, anlamlı düğmeler ("▶ Başlat",
/// "Yine de çalıştır"), hatalarda kopyalanabilir ayrıntı. Çağrılar <see cref="Iletisim"/> üzerinden.
/// </summary>
public partial class IletisimKutusu : Window
{
    private readonly string? _ayrinti;

    /// <summary>Tehlikeli işlemin ana düğmesi (#C42B1C).</summary>
    public static readonly SolidColorBrush TehlikeDolgusu = Dondur(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));

    private static SolidColorBrush Dondur(SolidColorBrush f)
    {
        f.Freeze();
        return f;
    }

    /// <summary>true = ana (onay) düğmesine basıldı.</summary>
    public bool Onaylandi { get; private set; }

    public IletisimKutusu(IletisimTuru tur, string pencereBasligi, string baslik, string mesaj,
        string? ayrinti, string anaDugme, string? vazgecDugmesi)
    {
        InitializeComponent();
        Title = pencereBasligi;
        PencereBasligiMetni.Text = pencereBasligi;
        BaslikMetni.Text = baslik;
        MesajMetni.Text = mesaj;
        MesajMetni.Visibility = string.IsNullOrWhiteSpace(mesaj) ? Visibility.Collapsed : Visibility.Visible;
        _ayrinti = string.IsNullOrWhiteSpace(ayrinti) ? null : ayrinti;
        if (_ayrinti is not null)
        {
            AyrintiMetni.Text = _ayrinti;
            AyrintiAlani.Visibility = Visibility.Visible;
            KopyalaDugmesi.Visibility = Visibility.Visible;
        }

        (string ikon, string zemin, string renk) = tur switch
        {
            IletisimTuru.Soru => ("?", "BilgiZeminFircasi", "VurguFircasi"),
            IletisimTuru.Bilgi => ("i", "BilgiZeminFircasi", "BilgiMetinFircasi"),
            IletisimTuru.Uyari => ("!", "UyariZeminFircasi", "UyariMetinFircasi"),
            IletisimTuru.Tehlike => ("!", "FarkSilinenZeminFircasi", "TehlikeFircasi"),
            _ => ("✕", "FarkSilinenZeminFircasi", "TehlikeFircasi"),
        };
        IkonMetni.Text = ikon;
        IkonDairesi.SetResourceReference(Border.BackgroundProperty, zemin);
        IkonMetni.SetResourceReference(TextBlock.ForegroundProperty, renk);

        bool tehlike = tur == IletisimTuru.Tehlike;
        Button? vazgec = null;
        if (vazgecDugmesi is not null)
        {
            vazgec = Dugme(vazgecDugmesi, birincil: false);
            vazgec.IsCancel = true;
            vazgec.IsDefault = tehlike;                       // tehlikede Enter = Vazgeç
            vazgec.Click += (_, _) => Kapat(false);
            Dugmeler.Children.Add(vazgec);
        }
        Button ana = Dugme(anaDugme, birincil: true);
        ana.IsDefault = !tehlike;
        if (vazgec is null)
            ana.IsCancel = true;                              // tek düğmede Esc de kapatır
        if (tehlike)
        {
            // Sabit koyu kırmızı: koyu paletin TehlikeFircasi'si METİN için açık tondur — dolgu olunca
            // beyaz yazı okunmuyordu (piksel kontrolü). Bu ton iki temada da beyazla yeterli kontrastta.
            ana.Background = TehlikeDolgusu;
            ana.BorderBrush = TehlikeDolgusu;
            ana.Foreground = Brushes.White;
        }
        ana.Click += (_, _) => Kapat(true);
        Dugmeler.Children.Add(ana);

        Loaded += (_, _) => (tehlike ? vazgec ?? ana : ana).Focus();
    }

    private Button Dugme(string metin, bool birincil)
    {
        var d = new Button
        {
            Content = metin,
            MinWidth = 88,
            Padding = new Thickness(16, 5, 16, 5),
            Margin = new Thickness(8, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(d, birincil ? "BtnIletisimAna" : "BtnIletisimVazgec");
        if (birincil && TryFindResource("BirincilDugme") is Style s)
            d.Style = s;
        return d;
    }

    private void Kapat(bool onay)
    {
        Onaylandi = onay;
        Close();
    }

    private void Kapat_Click(object sender, RoutedEventArgs e) => Kapat(false);

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        if (_ayrinti is null)
            return;
        Clipboard.SetText(_ayrinti);
        KopyalaDugmesi.Content = "✓ Kopyalandı";
    }
}

/// <summary>
/// İletişim kutusu çağrı yüzeyi (v23-S20) — uygulamadaki tüm onay/bilgi/hata kutuları buradan.
/// Tema kaynakları henüz yüklenmemişse (açılıştaki veri göçü uyarısı) Windows MessageBox'a düşer:
/// boyasız WPF penceresi göstermektense okunur kutu.
/// </summary>
public static class Iletisim
{
    /// <summary>Onay sorusu — ana düğmeye basılırsa true.</summary>
    public static bool Sor(Window? sahip, string pencereBasligi, string baslik, string mesaj,
        string onayMetni, IletisimTuru tur = IletisimTuru.Soru, string vazgecMetni = "Vazgeç")
        => Goster(sahip, tur, pencereBasligi, baslik, mesaj, null, onayMetni, vazgecMetni);

    public static void Bilgi(Window? sahip, string pencereBasligi, string baslik, string mesaj = "")
        => Goster(sahip, IletisimTuru.Bilgi, pencereBasligi, baslik, mesaj, null, "Tamam", null);

    public static void Uyari(Window? sahip, string pencereBasligi, string baslik, string mesaj = "")
        => Goster(sahip, IletisimTuru.Uyari, pencereBasligi, baslik, mesaj, null, "Tamam", null);

    public static void Hata(Window? sahip, string pencereBasligi, string baslik, string mesaj, string? ayrinti = null)
        => Goster(sahip, IletisimTuru.Hata, pencereBasligi, baslik, mesaj, ayrinti, "Tamam", null);

    /// <summary>İstisnadan hata kutusu: kullanıcıya türüne göre ne yapacağını söyleyen kısa açıklama,
    /// teknik metin "Ayrıntı"da.</summary>
    public static void Hata(Window? sahip, string pencereBasligi, string baslik, Exception ex)
        => Hata(sahip, pencereBasligi, baslik, KullaniciAciklamasi(ex), $"{ex.GetType().Name}: {ex.Message}");

    public static string KullaniciAciklamasi(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Bu konuma yazma/okuma izniniz yok. Başka bir klasör seçin ya da dosyanın salt-okunur olmadığından emin olun.",
        DirectoryNotFoundException => "Klasör bulunamadı. Yolun doğru olduğundan ve sürücünün bağlı olduğundan emin olun.",
        FileNotFoundException => "Dosya bulunamadı — taşınmış ya da silinmiş olabilir.",
        IOException => "Dosya başka bir program tarafından kullanılıyor olabilir (ör. Excel'de açık). Kapatıp yeniden deneyin.",
        _ => ex.Message,
    };

    private static bool Goster(Window? sahip, IletisimTuru tur, string pencereBasligi, string baslik, string mesaj,
        string? ayrinti, string anaDugme, string? vazgec)
    {
        sahip ??= System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                  ?? System.Windows.Application.Current?.MainWindow;
        if (System.Windows.Application.Current?.TryFindResource("PencereZeminFircasi") is null)
            return YedekMessageBox(tur, pencereBasligi, baslik, mesaj, ayrinti, vazgec is not null);

        var kutu = new IletisimKutusu(tur, pencereBasligi, baslik, mesaj, ayrinti, anaDugme, vazgec);
        if (sahip is { IsLoaded: true } && !ReferenceEquals(sahip, kutu))
            kutu.Owner = sahip;
        else
            kutu.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        kutu.ShowDialog();
        return kutu.Onaylandi;
    }

    private static bool YedekMessageBox(IletisimTuru tur, string pencereBasligi, string baslik, string mesaj,
        string? ayrinti, bool soru)
    {
        string metin = string.Join("\n\n", new[] { baslik, mesaj, ayrinti }.Where(s => !string.IsNullOrWhiteSpace(s)));
        MessageBoxImage ikon = tur switch
        {
            IletisimTuru.Hata => MessageBoxImage.Error,
            IletisimTuru.Uyari or IletisimTuru.Tehlike => MessageBoxImage.Warning,
            IletisimTuru.Bilgi => MessageBoxImage.Information,
            _ => MessageBoxImage.Question,
        };
        return MessageBox.Show(metin, pencereBasligi, soru ? MessageBoxButton.YesNo : MessageBoxButton.OK, ikon,
                   soru && tur == IletisimTuru.Tehlike ? MessageBoxResult.No : MessageBoxResult.Yes)
               is MessageBoxResult.Yes or MessageBoxResult.OK;
    }
}
