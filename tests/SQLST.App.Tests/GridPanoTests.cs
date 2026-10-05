using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLST.App.ViewModels;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// ⧉ Sonuç grid'i Ctrl+C sözleşmesi (v23-S8 — kullanıcı bulgusu 1 Eki 2026: "hücreye tıklayıp
/// Ctrl+C yapınca kopyalayamıyorum, illa açmam gerekiyor"). WPF'in yerleşik kopyası görüntü
/// binding'inden geçiyordu: uzun hücre panoya "… (N karakter — çift tık)" KIRPIĞIYLA, çok
/// satırlı hücre ⏎'li tek satır olarak gidiyor, tek hücrede bile sona \r\n ekleniyordu.
/// Artık Copy devralındı (Grid_Kopyala): tek hücre = HAM değer satır sonu EKLENMEDEN (SSMS),
/// çoklu seçim = HAM TSV. Gerçek pencere + LocalDB ile uçtan uca ölçülür (piksel-kanıt ailesi).
/// </summary>
public class GridPanoTests
{
    [Fact]
    public void Ctrl_c_tek_hucre_HAM_satir_sonsuz_coklu_secim_HAM_tsv()
    {
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-pano-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        try
        {
            d.Invoke(() =>
            {
                StaOrtak.Birlestir("PaletAcik.xaml");
                StaOrtak.Birlestir("Tema.xaml");
                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1100; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                var vm = (MainViewModel)w.DataContext;
                w.ProfilUygula(new ConnectionProfile
                {
                    Ad = "Diag", Sunucu = @"(localdb)\MSSQLLocalDB",
                    Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
                });
                StaOrtak.Pump(TimeSpan.FromMilliseconds(800));

                string uzun = new string('A', 3000);
                vm.SekmeAcVeCalistir("PanoTest",
                    $"SELECT N'kisa deger' AS Kisa, N'satir1' + CHAR(13) + CHAR(10) + N'satir2' AS CokSatir, "
                    + $"N'{uzun}' AS Uzun, CAST(NULL AS nvarchar(10)) AS Bos", "tempdb");
                StaOrtak.PumpUntil(() => GridBul(w) is { Items.Count: > 0 }, TimeSpan.FromSeconds(20));

                DataGrid grid = GridBul(w)!;
                grid.UpdateLayout();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

                // Pano paylaşımlı kaynak — başka süreç tutarsa CLIPBRD_E_CANT_OPEN (süitte ölçülen
                // flake). Testin kendi pano erişimleri de kısa yeniden denemeyle yapılır.
                static T PanoDene<T>(Func<T> islem)
                {
                    // 10×150 ms: süit koşusunda pano kilidi 300 ms'den uzun tutulabiliyor
                    // (5×60 bir koşuda yetmedi — CLIPBRD_E_CANT_OPEN, 5 Eki 2026).
                    for (int deneme = 1; ; deneme++)
                    {
                        try { return islem(); }
                        catch (System.Runtime.InteropServices.COMException) when (deneme < 10)
                        {
                            System.Threading.Thread.Sleep(150);
                        }
                    }
                }

                string Kopyala(params int[] kolonlar)
                {
                    grid.SelectedCells.Clear();
                    foreach (int k in kolonlar)
                        grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[0], grid.Columns[k]));
                    PanoDene<object?>(() => { Clipboard.SetText("(sentinel)"); return null; });
                    ApplicationCommands.Copy.Execute(null, grid);
                    return PanoDene(Clipboard.GetText);
                }

                Assert.Equal("kisa deger", Kopyala(0));             // sona \r\n EKLENMEZ
                Assert.Equal("satir1\r\nsatir2", Kopyala(1));       // gerçek satır sonları, ⏎ değil
                Assert.Equal(uzun, Kopyala(2));                     // 3000 karakterin TAMAMI — kırpık "(… çift tık)" yok
                Assert.Equal("NULL", Kopyala(3));                   // SSMS sözleşmesi
                Assert.Equal("kisa deger\tsatir1\r\nsatir2", Kopyala(0, 1)); // çoklu: HAM TSV

                w.Close();
            });
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { }
        }
    }

    private static DataGrid? GridBul(DependencyObject kok)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(kok); i++)
        {
            DependencyObject cocuk = VisualTreeHelper.GetChild(kok, i);
            if (cocuk is DataGrid dg && dg.ItemsSource is System.Data.DataView)
                return dg;
            if (GridBul(cocuk) is { } bulunan)
                return bulunan;
        }
        return null;
    }
}
