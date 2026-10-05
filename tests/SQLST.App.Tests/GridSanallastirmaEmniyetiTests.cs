using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S4, 5. çökme bildirimi — donma.txt kanıtı: "grid-hazir"da 151 sn UI bloğu + 4.574 MB yığın.
///
/// MEKANİZMA (yerelde yeniden üretildi): sonuç grid'inin Height bağlaması değer üretemezse grid,
/// dış ScrollViewer içinde SONSUZ yükseklik alır → satır sanallaştırması ölür → tüm satırların
/// görseli kurulur. Ölçüm: sanallaştırma ölüyken 3.000 satır × 43 kolon = 46,2 sn + 1.454 MB
/// (sahadaki imzanın birebir aynısı, ~11× ölçekle 33.964 satır ≈ dakikalar + ~5 GB);
/// canlıyken aynı veri 0,9 sn + 24 satır görseli.
///
/// EMNİYET KEMERİ: SetGrid'e MaxHeight=1600 + Height MultiBinding'ine FallbackValue=600 —
/// Height hangi yolla bozulursa bozulsun ölçüm sonlu kalır, sanallaştırma yaşar.
/// Bu test kemeri kilitler: MaxHeight'lİ grid, Height'siz ve ScrollViewer içindeyken bile
/// yalnız görünür satırları kurar.
/// </summary>
public class GridSanallastirmaEmniyetiTests
{
    private static readonly bool Etkin = OperatingSystem.IsWindows();

    private static DataTable KirkUcKolon(int satir)
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        for (int k = 1; k < 40; k++)
            t.Columns.Add($"Kolon{k}", typeof(string));
        t.Columns.Add("Deger", typeof(string));
        t.Columns.Add("Tarih", typeof(DateTime));
        t.Columns.Add("Aktif", typeof(bool));

        string devMetin = new('x', 4_000);
        string kisa = "orta boy bir metin degeri";
        t.BeginLoadData();
        for (int i = 0; i < satir; i++)
        {
            var s = new object[43];
            s[0] = i;
            for (int k = 1; k < 40; k++)
                s[k] = kisa;
            s[40] = devMetin;
            s[41] = new DateTime(2026, 8, 21);
            s[42] = i % 2 == 0;
            t.Rows.Add(s);
        }
        t.EndLoadData();
        return t;
    }

    private static int Say<T>(DependencyObject kok) where T : DependencyObject
    {
        int n = 0;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(kok); i++)
        {
            DependencyObject c = System.Windows.Media.VisualTreeHelper.GetChild(kok, i);
            if (c is T)
                n++;
            n += Say<T>(c);
        }
        return n;
    }

    /// <summary>
    /// SAHA SENARYOSU: Height YOK (bağlama bozulmuş gibi), grid ScrollViewer içinde — ama
    /// MaxHeight kemeri takılı. Sanallaştırma YAŞAMALI: kurulan satır görseli satır sayısının
    /// çok altında kalmalı ve pencere saniyeler içinde açılmalı.
    /// </summary>
    [Fact]
    public void MaxHeight_kemeri_scrollviewer_icinde_sanallastirmayi_yasatir()
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
                ItemsSource = KirkUcKolon(33_964).DefaultView,
                IsReadOnly = true,
                AutoGenerateColumns = true,
                EnableRowVirtualization = true,
                EnableColumnVirtualization = true,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
                HeadersVisibility = DataGridHeadersVisibility.All,
                RowHeaderWidth = 18,
                MaxHeight = 1_600, // SetGrid'deki emniyet kemerinin aynısı — Height bilerek YOK
            };
            var w = new Window
            {
                Width = 1600, Height = 700,
                Content = new ScrollViewer { Content = grid },
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            var kron = System.Diagnostics.Stopwatch.StartNew();
            w.Show();

            // ⚠ KARARSIZLIK DÜZELTMESİ (24 Ağu 2026): eskiden SABİT 300 ms pump edilirdi. İzole
            // koşuda yetiyordu ama TAM SÜİTTE makine meşgulken yerleşim o süreye sığmıyor, satır
            // görseli 0 kalıyor ve InRange(1,300) düşüyordu (süitte kırmızı, tek başına yeşil).
            // Artık satır belirene KADAR pump edilir; asıl kapı zaten aşağıdaki 20 sn'lik süre
            // sınırı — sanallaştırma gerçekten ölürse orada yakalanır, dolayısıyla beklemek testin
            // koruduğu şeyi zayıflatmaz.
            int gorsel = 0;
            while (kron.Elapsed < TimeSpan.FromSeconds(20)
                   && (gorsel = Say<DataGridRow>(grid)) == 0)
                StaOrtak.Pump(TimeSpan.FromMilliseconds(100));
            kron.Stop();
            // Kemersiz ölçüm: 3.000 satırda 3.000 görsel + 46 sn. Kemerle 1.600 px / 25 px ≈ 64
            // görünür satır beklenir; cömert pay bırakıyoruz.
            Assert.InRange(gorsel, 1, 300);
            Assert.True(kron.Elapsed < TimeSpan.FromSeconds(20),
                $"emniyet kemerine rağmen açılış {kron.Elapsed.TotalSeconds:N0} sn — sanallaştırma ölmüş");
            w.Close();
        });
    }

    /// <summary>Kemerin XAML'de durduğunu kilitle — biri MaxHeight/FallbackValue'yu silerse kırmızı.</summary>
    [Fact]
    public void SetGrid_emniyet_kemerleri_xamlde_durur()
    {
        var dizin = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !System.IO.File.Exists(System.IO.Path.Combine(dizin.FullName, "SQLST.slnx")))
            dizin = dizin.Parent;
        Assert.NotNull(dizin);
        string xaml = System.IO.File.ReadAllText(
            System.IO.Path.Combine(dizin!.FullName, "src", "SQLST.App", "MainWindow.xaml"));

        int setGrid = xaml.IndexOf("x:Name=\"SetGrid\"", StringComparison.Ordinal);
        Assert.True(setGrid >= 0, "SetGrid bulunamadı");
        // 4.000: v23-S8'in Copy CommandBinding'i (yorumuyla) araya girince 2.500'lük pencere
        // FallbackValue'yu dışarıda bırakıp yanlış kırmızı verdi — kemerler hâlâ yerindeydi.
        string blok = xaml[setGrid..(setGrid + 4_000)];

        Assert.Contains("MaxHeight=\"1600\"", blok);
        Assert.Contains("FallbackValue=\"600\"", blok);
    }
}
