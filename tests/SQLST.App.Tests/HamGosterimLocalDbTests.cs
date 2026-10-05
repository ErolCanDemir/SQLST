using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 🧾 Ham gösterim UÇTAN UCA (v23-S13 — kullanıcı kararı 5 Eki 2026: "hiçbir alanı formatlama,
/// db nasıl ise öyle kalsın; MSSQL'de tarih '-'li, bizde '.'lı — rezil oluyorum"): gerçek SQL
/// tipleri LocalDB'den gerçek pencereye gelir; EKRANDA görünen metin, Ctrl+C ve CSV, SSMS'in
/// gösterdiği metinle BİREBİR olmalı — uygulama tr-TR kültürde çalışırken.
/// </summary>
public class HamGosterimLocalDbTests
{
    /// <summary>Kolon → SSMS'in grid'de gösterdiği metin.</summary>
    private static readonly (string Kolon, string Ssms)[] Beklenen =
    [
        ("Dt", "2026-10-05 14:23:11.123"),
        ("Dt2_3", "2026-10-05 14:23:11.123"),
        ("Dt2", "2026-10-05 14:23:11.1234567"),
        ("Gun", "2026-10-05"),
        ("Kisa", "2026-10-05 14:23:00"),
        ("Tutar", "1250.75"),
        ("Para", "1250.7500"),
        ("Oran", "3.14"),
        ("Bayrak", "1"),
        ("Kimlik", "6F9619FF-8B86-D011-B42D-00C04FC964FF"),
        ("Buyuk", "1234567"),
        ("Saat", "14:23:11"),
        ("Ofset", "2026-10-05 14:23:11.1234567 +03:00"),
    ];

    private const string Sorgu = """
        SELECT CAST('2026-10-05T14:23:11.123' AS datetime) AS Dt,
               CAST('2026-10-05T14:23:11.123' AS datetime2(3)) AS Dt2_3,
               CAST('2026-10-05T14:23:11.1234567' AS datetime2) AS Dt2,
               CAST('2026-10-05' AS date) AS Gun,
               CAST('2026-10-05T14:23:11' AS smalldatetime) AS Kisa,
               CAST(1250.75 AS decimal(18,2)) AS Tutar,
               CAST(1250.75 AS money) AS Para,
               CAST(3.14 AS float) AS Oran,
               CAST(1 AS bit) AS Bayrak,
               CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier) AS Kimlik,
               CAST(1234567 AS int) AS Buyuk,
               CAST('14:23:11' AS time(0)) AS Saat,
               CAST('2026-10-05T14:23:11.1234567+03:00' AS datetimeoffset) AS Ofset
        """;

    [Fact]
    public void Ekran_ctrl_c_ve_csv_ssms_ile_birebir()
    {
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-ham-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        try
        {
            d.Invoke(() =>
            {
                // Ürün koşulu: tr-TR kültür + FrameworkElement dili tr (App.KulturuKur'un etkisi).
                var tr = new System.Globalization.CultureInfo("tr-TR");
                System.Globalization.CultureInfo.CurrentCulture = tr;

                StaOrtak.Birlestir("PaletAcik.xaml");
                StaOrtak.Birlestir("Tema.xaml");
                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Language = System.Windows.Markup.XmlLanguage.GetLanguage("tr-TR");
                w.Width = 1600; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                var vm = (MainViewModel)w.DataContext;
                w.ProfilUygula(new ConnectionProfile
                {
                    Ad = "Ham", Sunucu = @"(localdb)\MSSQLLocalDB",
                    Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
                });
                StaOrtak.Pump(TimeSpan.FromMilliseconds(800));

                vm.SekmeAcVeCalistir("Ham", Sorgu, "tempdb");
                StaOrtak.PumpUntil(() => GridBul(w) is { Items.Count: > 0, Columns.Count: > 0 }, TimeSpan.FromSeconds(20));
                DataGrid grid = GridBul(w)!;
                grid.UpdateLayout();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

                // 1) EKRANDA görünen metin (TextBlock) — kullanıcının gördüğü
                var hatalar = new List<string>();
                foreach ((string kolon, string ssms) in Beklenen)
                {
                    DataGridColumn sutun = grid.Columns.First(c => (c.Header?.ToString() ?? "") == kolon);
                    grid.ScrollIntoView(grid.Items[0], sutun);
                    grid.UpdateLayout();
                    string ekran = (sutun.GetCellContent(grid.Items[0]) as TextBlock)?.Text ?? "<yok>";
                    if (ekran != ssms)
                        hatalar.Add($"EKRAN {kolon}: '{ekran}' ≠ SSMS '{ssms}'");
                }

                // 2) Ctrl+C (tek hücre) — tarih ve para
                foreach (string kolon in new[] { "Dt", "Tutar", "Bayrak" })
                {
                    DataGridColumn sutun = grid.Columns.First(c => (c.Header?.ToString() ?? "") == kolon);
                    grid.SelectedCells.Clear();
                    grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[0], sutun));
                    ApplicationCommands.Copy.Execute(null, grid);
                    string pano = PanoOku();
                    string ssms = Beklenen.First(b => b.Kolon == kolon).Ssms;
                    if (pano != ssms)
                        hatalar.Add($"CTRL+C {kolon}: '{pano}' ≠ '{ssms}'");
                }

                // 3) CSV (⬇ CSV / Kopyala düğmeleriyle aynı tablo) — ikinci satır = değerler
                var set = (SonucSetiGorunumu)grid.DataContext;
                string csvSatir = CsvYazici.Metin(set.Tablo).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
                string csvBeklenen = string.Join(';', Beklenen.Select(b => b.Ssms));
                if (csvSatir != csvBeklenen)
                    hatalar.Add($"CSV: '{csvSatir}' ≠ '{csvBeklenen}'");

                w.Close();
                Assert.True(hatalar.Count == 0, string.Join("\n", hatalar));
            });
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { }
        }
    }

    private static string PanoOku()
    {
        for (int deneme = 1; ; deneme++)
        {
            try { return Clipboard.GetText(); }
            catch (System.Runtime.InteropServices.COMException) when (deneme < 10)
            {
                System.Threading.Thread.Sleep(150);
            }
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
