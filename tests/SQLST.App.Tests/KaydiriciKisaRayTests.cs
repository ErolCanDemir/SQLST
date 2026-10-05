using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace SQLST.App.Tests;

/// <summary>
/// 🖱 Kaydırıcı · KISA RAY (kullanıcı bulgusu 26 Ağu 2026: <i>"pencerenin altındaki gridi büyütüp
/// küçülttükçe scroll yok oluyor"</i>).
///
/// ⚠ BU BİR GERİLEME VE BENİM YAPTIĞIM: tutamağın asgari boyunu 36 → 72 px'e çıkarırken
/// "kısa rayda WPF tutamağı raya kırpar, kaydırma bozulmaz" diye yazmıştım — ÖLÇMEDEN. Ray 72 px'in
/// altına inince tutamak sığmıyor ve kayboluyor; kullanıcı grid'i küçülttüğünde scroll'u yitiriyor.
///
/// Test rayı KISALTIP tutamağın hâlâ görünür ve tutulabilir olduğunu doğrular.
/// </summary>
public class KaydiriciKisaRayTests
{
    private static bool Etkin => Environment.GetEnvironmentVariable("SQLST_SS") == "1"
                                 || Environment.GetEnvironmentVariable("SQLST_STA") == "1";

    private static IEnumerable<T> Bul<T>(DependencyObject kok) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(kok);
        for (int i = 0; i < n; i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(kok, i);
            if (c is T t)
                yield return t;
            foreach (T alt in Bul<T>(c))
                yield return alt;
        }
    }

    /// <summary>
    /// Ray kısaldıkça tutamak KAYBOLMAMALI. 300 → 40 px arası her boyda tutamağın gerçek yüksekliği
    /// 0'dan büyük olmalı; aksi hâlde kullanıcı kaydırma çubuğunu göremez/tutamaz.
    /// </summary>
    [Theory]
    [InlineData(300)]
    [InlineData(200)]
    [InlineData(120)]
    [InlineData(90)]
    [InlineData(70)]
    [InlineData(50)]
    [InlineData(40)]
    public void Kisa_rayda_tutamac_kaybolmaz(double rayYuksekligi)
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            // Çok içerik + az görünüm → tutamak doğal olarak asgariye dayanır (gerçek senaryo).
            var sb = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Minimum = 0, Maximum = 10_000, ViewportSize = 20, Value = 0,
                Height = rayYuksekligi,
            };
            var w = new Window
            {
                Width = 200, Height = rayYuksekligi + 40, Content = sb,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            w.UpdateLayout();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(120));

            Thumb? tutamac = Bul<Thumb>(sb).FirstOrDefault();
            double yukseklik = tutamac?.ActualHeight ?? 0;
            bool gorunur = tutamac is { Visibility: Visibility.Visible };
            w.Close();

            Assert.True(tutamac is not null, $"ray {rayYuksekligi}px — tutamaç HİÇ YOK");
            Assert.True(gorunur, $"ray {rayYuksekligi}px — tutamaç görünmez");
            Assert.True(yukseklik > 0,
                $"ray {rayYuksekligi}px — tutamaç yüksekliği {yukseklik:F0}px; grid küçülünce "
                + "kaydırıcı KAYBOLUYOR (kullanıcı bulgusu 26 Ağu 2026).");
        });
    }

    /// <summary>
    /// YATAY ray — değişikliğimin test edilmemiş yarısı. <c>MinWidth=72</c> dar grid'de tutamağı
    /// kaybettiriyor mu? Kullanıcı "büyütüp küçülttükçe" dedi; bu genişlik de olabilir.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(150)]
    [InlineData(90)]
    [InlineData(60)]
    [InlineData(40)]
    public void Dar_rayda_yatay_tutamac_kaybolmaz(double rayGenisligi)
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var sb = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Minimum = 0, Maximum = 10_000, ViewportSize = 20, Value = 0,
                Width = rayGenisligi,
            };
            var w = new Window
            {
                Width = rayGenisligi + 40, Height = 120, Content = sb,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            w.UpdateLayout();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(120));

            Thumb? tutamac = Bul<Thumb>(sb).FirstOrDefault();
            double genislik = tutamac?.ActualWidth ?? 0;
            bool gorunur = tutamac is { Visibility: Visibility.Visible };
            w.Close();

            Assert.True(tutamac is not null, $"ray {rayGenisligi}px — yatay tutamaç HİÇ YOK");
            Assert.True(gorunur, $"ray {rayGenisligi}px — yatay tutamaç görünmez");
            Assert.True(genislik > 0, $"ray {rayGenisligi}px — yatay tutamaç {genislik:F0}px");
        });
    }
}
