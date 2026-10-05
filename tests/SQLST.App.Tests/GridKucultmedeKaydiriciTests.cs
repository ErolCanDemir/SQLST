using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace SQLST.App.Tests;

/// <summary>
/// 🖱 Grid küçültülünce kaydırıcı (kullanıcı bulgusu 26 Ağu 2026: <i>"pencerenin altındaki gridi
/// büyütüp küçülttükçe scroll yok oluyor"</i>).
///
/// GERÇEK YAPI taklit edilir: dış ScrollViewer → sabit Height'li, VerticalAlignment=Top DataGrid
/// (ürün SetGrid'i böyle; yükseklik OranliYukseklik ile hesaplanır ve asgari 140'ta çakılır).
/// Dış alan küçüldükçe grid'in KENDİ dikey kaydırıcısının görünür ve tutulabilir kalması gerekir.
///
/// ⚠ Şüpheli benim değişikliğim: tutamağın asgari boyunu 36 → 72 px'e çıkarırken "kısa rayda WPF
/// kırpar" diye YAZDIM ama ÖLÇMEDİM. Bu test o iddiayı gerçek DataGrid üzerinde sınar.
/// </summary>
public class GridKucultmedeKaydiriciTests
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
    /// ⚠ ÇOK KOLON ŞART (kullanıcı ekran görüntüsü 26 Ağu 2026): ilk denememde 2 kolon kullandım,
    /// yatay kaydırıcı hiç oluşmadı ve test kusuru GÖREMEDİ. Sahadaki grid çok kolonlu — yatay
    /// kaydırıcı dikey rayın 14 px'ini yiyor; kısa grid'de tutamağın sığmasını asıl bu belirliyor.
    /// </summary>
    private static DataTable Tablo(int satir, int kolon = 40)
    {
        var t = new DataTable();
        for (int k = 0; k < kolon; k++)
            t.Columns.Add($"CikisTarihiKolonu{k}", typeof(string));
        for (int i = 0; i < satir; i++)
        {
            DataRow r = t.NewRow();
            for (int k = 0; k < kolon; k++)
                r[k] = $"23.08.2026 14:48:{i % 60:00}";
            t.Rows.Add(r);
        }
        return t;
    }

    /// <summary>
    /// 📏 TANI: her grid boyunda ray ve tutamak ÖLÇÜLERİNİ dosyaya döker
    /// (%TEMP%\sqlst-kaydirici-olcum.txt). "0'dan büyük" iddiası yetmiyor — 1 px'lik tutamak da
    /// testi geçer ama kullanıcı onu göremez/tutamaz. Gerçek sayılar lazım.
    /// </summary>
    [Fact]
    public void Olculeri_dok()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            // ⚠ 34.000 SATIR (kullanıcı ölçeği 27 Ağu 2026: "ufacık bir nokta görünüyor sadece").
            // 5.000 satırla ölçmüştüm; o ölçekte doğal tutamaç zaten görünür boyda kalabiliyor ve
            // asgarinin uygulanıp uygulanmadığı ayırt edilemiyor. Sahadaki sonuç 34.000 satır:
            // doğal boy ~0,3 px — asgari devreye girmezse ekranda TAM OLARAK bir nokta görünür.
            // MinHeight'ın GERÇEKTEN atanıp atanmadığı da yazılıyor; bağlama çözülmezse 0 kalır.
            var satirlar = new List<string> { "gridBoy | rayBoy | tutamacBoy | minHeight | yatayVar" };
            foreach (double boy in new[] { 700d, 500, 400, 240, 160, 140, 120, 100, 80, 60 })
            {
                var grid = new DataGrid
                {
                    ItemsSource = Tablo(34_000, kolon: 43).DefaultView,
                    IsReadOnly = true, AutoGenerateColumns = true,
                    EnableRowVirtualization = true,
                    VerticalAlignment = VerticalAlignment.Top,
                    Height = boy,
                    HeadersVisibility = DataGridHeadersVisibility.All,
                };
                var w = new Window
                {
                    Width = 600, Height = Math.Max(120, boy + 40),
                    Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000, Top = -32000, ShowInTaskbar = false,
                };
                w.Show();
                StaOrtak.PumpUntil(() => Bul<DataGridRow>(grid).Any());
                w.UpdateLayout();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(150));

                ScrollBar? dikey = Bul<ScrollBar>(grid).FirstOrDefault(s => s.Orientation == Orientation.Vertical);
                ScrollBar? yatay = Bul<ScrollBar>(grid).FirstOrDefault(s => s.Orientation == Orientation.Horizontal);
                Thumb? tut = dikey is null ? null : Bul<Thumb>(dikey).FirstOrDefault();
                satirlar.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0,7:F0} | {1,6:F0} | {2,10:F1} | {3,9:F1} | {4}",
                    boy, dikey?.ActualHeight ?? 0, tut?.ActualHeight ?? 0, tut?.MinHeight ?? -1,
                    yatay is { Visibility: Visibility.Visible, ActualHeight: > 0 } ? "evet" : "hayır"));
                w.Close();
            }

            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlst-kaydirici-olcum.txt"),
                satirlar);
        });
    }

    /// <summary>
    /// Grid yüksekliği küçüldükçe DİKEY kaydırıcı görünür ve tutamağı tutulabilir kalmalı.
    /// 140 = ürünün asgarisi; altındakiler kullanıcının bölmeyi daha da kısması hâli.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(240)]
    [InlineData(160)]
    [InlineData(140)]
    [InlineData(120)]
    [InlineData(100)]
    [InlineData(90)]
    [InlineData(80)]
    public void Grid_kuculunce_dikey_kaydirici_kaybolmaz(double gridYuksekligi)
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var grid = new DataGrid
            {
                ItemsSource = Tablo(5_000, kolon: 40).DefaultView,
                IsReadOnly = true, AutoGenerateColumns = true,
                EnableRowVirtualization = true,
                VerticalAlignment = VerticalAlignment.Top,
                Height = gridYuksekligi,
                HeadersVisibility = DataGridHeadersVisibility.All,
            };
            var w = new Window
            {
                Width = 600, Height = Math.Max(120, gridYuksekligi + 40),
                Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.PumpUntil(() => Bul<DataGridRow>(grid).Any());
            w.UpdateLayout();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(150));

            ScrollBar? dikey = Bul<ScrollBar>(grid)
                .FirstOrDefault(s => s.Orientation == Orientation.Vertical);
            Thumb? tutamac = dikey is null ? null : Bul<Thumb>(dikey).FirstOrDefault();
            double tutamacBoy = tutamac?.ActualHeight ?? 0;
            bool gorunur = dikey is { Visibility: Visibility.Visible } && tutamac is { Visibility: Visibility.Visible };
            double rayBoy = dikey?.ActualHeight ?? 0;
            w.Close();

            Assert.True(dikey is not null, $"grid {gridYuksekligi}px — dikey kaydırıcı HİÇ YOK");
            Assert.True(gorunur, $"grid {gridYuksekligi}px — kaydırıcı/tutamaç görünmez (ray {rayBoy:F0}px)");
            Assert.True(tutamacBoy > 0,
                $"grid {gridYuksekligi}px — tutamaç {tutamacBoy:F0}px (ray {rayBoy:F0}px); "
                + "grid küçülünce SCROLL KAYBOLUYOR (kullanıcı bulgusu 26 Ağu 2026).");
        });
    }
}
