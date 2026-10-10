using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLST.App.ViewModels;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v23-S23 (kullanıcı 10 Eki: "kolona tıklayınca sıralama yapıyoruz, tüm kolonu seçme özelliğimiz yok";
/// karar B — SSMS gibi): GERÇEK MainWindow + LocalDB sonucu. Başlık tıklaması kolonun TÜM hücrelerini
/// seçer ve SIRALAMAZ; Ctrl+C kolonu ham kopyalar; sıralama başlık menüsünden (artan/azalan/kaldır, ok
/// görünür); tavan üstünde görsel seçim yapılmaz ama Ctrl+C kolonun tamamını verir.
/// </summary>
public class KolonSecimiStaTests
{
    private static IEnumerable<T> Bul<T>(DependencyObject kok) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(kok); i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(kok, i);
            if (c is T t)
                yield return t;
            foreach (T alt in Bul<T>(c))
                yield return alt;
        }
    }

    private static string Pano()
    {
        for (int i = 0; ; i++)
        {
            try { return Clipboard.GetText(); }
            catch (System.Runtime.InteropServices.COMException) when (i < 10) { Thread.Sleep(150); }
        }
    }

    [Fact]
    public void Baslik_tik_kolonu_secer_menu_siralar_buyukte_tamamini_kopyalar()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "sqlst-kolon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        (MainWindow win, MainViewModel vm) = d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcikVS.xaml");
            StaOrtak.Birlestir("Tema.xaml");
            var services = new ServiceCollection();
            SQLST.App.App.HizmetleriKur(services);
            services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
            MainWindow w = services.BuildServiceProvider().GetRequiredService<MainWindow>();
            w.Width = 1200; w.Height = 800;
            w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            return (w, (MainViewModel)w.DataContext);
        });
        try
        {
            d.Invoke(() => win.ProfilUygula(new ConnectionProfile
            {
                Ad = "Local", Sunucu = @"(localdb)\MSSQLLocalDB", Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
            }));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(2000)));
            d.Invoke(() => vm.SekmeAcVeCalistir("kolon",
                "SELECT v.Ad, v.No FROM (VALUES (N'Çınar', 3), (N'Ali', 1), (N'Berk', 2)) v(Ad, No);", "master"));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(2500)));

            d.Invoke(() =>
            {
                DataGrid grid = Bul<DataGrid>(win).First(g => System.Windows.Automation.AutomationProperties.GetAutomationId(g) == "GridSonuc");
                var gorunum = (DataView)grid.ItemsSource;
                DataGridColumnHeader no = Bul<DataGridColumnHeader>(grid).First(h => (string?)h.Column?.Header == "No");

                // 1) Başlık TIK → kolonun 3 hücresi seçili, sıralama YOK
                no.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                StaOrtak.Pump(TimeSpan.FromMilliseconds(100));
                Assert.Equal(3, grid.SelectedCells.Count);
                Assert.All(grid.SelectedCells, c => Assert.Same(no.Column, c.Column));
                Assert.Equal("", gorunum.Sort);
                Assert.Null(no.Column.SortDirection);

                // Ctrl+C → kolon değerleri, grid sırasında, ham
                grid.Focus();
                ApplicationCommands.Copy.Execute(null, grid);
                Assert.Equal("3\r\n1\r\n2", Pano());

                // 2) Menüden azalan sırala → görünüm sıralı + başlıkta ok
                ContextMenu menu = no.ContextMenu!;
                menu.PlacementTarget = no;
                MenuItem azalan = menu.Items.OfType<MenuItem>().First(m => ((string)m.Header).Contains("Azalan"));
                azalan.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(System.ComponentModel.ListSortDirection.Descending, no.Column.SortDirection);
                Assert.Equal([3, 2, 1], gorunum.Cast<DataRowView>().Select(r => Convert.ToInt32(r["No"])));
                MenuItem kaldir = menu.Items.OfType<MenuItem>().First(m => ((string)m.Header).Contains("kaldır"));
                kaldir.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Null(no.Column.SortDirection);
                Assert.Equal("", gorunum.Sort);

                // 3) Tavan üstü (testte 2 hücre): görsel seçim YOK, Ctrl+C yine kolonun TAMAMI
                int eski = MainWindow.SecimHucreTavani;
                MainWindow.SecimHucreTavani = 2;
                try
                {
                    DataGridColumnHeader ad = Bul<DataGridColumnHeader>(grid).First(h => (string?)h.Column?.Header == "Ad");
                    ad.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                    Assert.Empty(grid.SelectedCells);
                    grid.Focus();
                    ApplicationCommands.Copy.Execute(null, grid);
                    Assert.Equal("Çınar\r\nAli\r\nBerk", Pano());
                }
                finally
                {
                    MainWindow.SecimHucreTavani = eski;
                }
            });
        }
        finally
        {
            d.Invoke(() => win.Close());
        }
    }
}
