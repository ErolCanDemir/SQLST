using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLST.App.ViewModels;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 🔬 GERÇEK PENCEREDEN kaydırıcı ölçümü (27 Ağu 2026).
///
/// NEDEN: kullanıcı sahada tutamağı "ufacık bir nokta" görüyor; benim SENTETİK testlerim
/// (elle kurulmuş DataGrid) asgari boyun DOĞRU uygulandığını gösteriyor. Dört tur tahmin
/// yürüttüm ve her seferinde ölçümüm kullanıcının gördüğüyle çelişti — demek ki sentetik kurulum
/// gerçeği temsil etmiyor. Bu test <b>DI'dan kurulmuş GERÇEK MainWindow</b>'u açar, LocalDB'ye
/// bağlanır, gerçek sorguyu çalıştırır ve sonuç grid'inin kaydırıcılarını ölçer.
///
/// Kritik alan: <c>MinHeight</c> bağlamasının DURUMU. WPF bağlama hatalarını SESSİZCE yutar;
/// bağlama çözülmediyse asgari hiç uygulanmaz ve tutamak doğal boyuna (10.000 satırda ~%0,1) düşer.
///
/// Ölçüm <c>%TEMP%\sqlst-gercek-kaydirici.txt</c>'ye yazılır. Test DEĞİL, tanı aracı — bir şey
/// iddia etmez, ölçtüğünü yazar.
/// </summary>
public class GercekPencereKaydiriciTanisi
{
    private static bool Etkin => Environment.GetEnvironmentVariable("SQLST_GERCEK_TANI") == "1";

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

    private static string BaglamaDurumu(FrameworkElement oge, DependencyProperty ozellik)
    {
        BindingExpressionBase? ifade = BindingOperations.GetBindingExpressionBase(oge, ozellik);
        return ifade is null ? "bağlama YOK (düz değer)" : ifade.Status.ToString();
    }

    [Fact]
    public void Gercek_pencerede_kaydiricilari_olc()
    {
        if (!Etkin)
            return;

        string tempData = Path.Combine(Path.GetTempPath(), "sqlst-gercek-tani-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempData);
        var sb = new StringBuilder();
        Dispatcher d = StaOrtak.Sta();

        try
        {
            (MainWindow win, MainViewModel vm) = d.Invoke(() =>
            {
                StaOrtak.Birlestir("PaletKoyuKor.xaml"); // kullanıcının GERÇEK teması (ekran görüntüsünde kırmızı vurgu = Kor)
                StaOrtak.Birlestir("Tema.xaml");

                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();

                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1920; w.Height = 1040;     // kullanıcı büyütülmüş pencerede çalışıyor
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
                return (w, (MainViewModel)w.DataContext);
            });

            d.Invoke(() => win.ProfilUygula(new ConnectionProfile
            {
                Ad = "Local", Sunucu = @"(localdb)\MSSQLLocalDB",
                Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
            }));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(2500)));

            // Kullanıcının GERÇEK sorgusu (2M satırlık tablo; ürün 10.000'e sınırlar)
            d.Invoke(() => vm.SekmeAcVeCalistir("tani", "SELECT * FROM talep.Talepler", "KdsDemo"));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(6000)));

            d.Invoke(() =>
            {
                DataGrid? grid = Bul<DataGrid>(win).FirstOrDefault(g => g.ItemsSource is System.Data.DataView);
                sb.AppendLine($"grid bulundu: {(grid is null ? "HAYIR" : "evet")}");
                if (grid is not null)
                {
                    sb.AppendLine($"grid: {grid.ActualWidth:F0} x {grid.ActualHeight:F0}  "
                                + $"Height={grid.Height:F0} MaxHeight={grid.MaxHeight:F0}  "
                                + $"satır={((System.Data.DataView)grid.ItemsSource).Count}");
                    sb.AppendLine(new string('-', 74));

                    int n = 0;
                    foreach (ScrollBar cubuk in Bul<ScrollBar>(grid))
                    {
                        n++;
                        Thumb? t = Bul<Thumb>(cubuk).FirstOrDefault();
                        Track? ray = Bul<Track>(cubuk).FirstOrDefault();
                        sb.AppendLine($"[{n}] {cubuk.Orientation}  görünür={cubuk.Visibility}");
                        sb.AppendLine($"    çubuk : {cubuk.ActualWidth:F1} x {cubuk.ActualHeight:F1}");
                        sb.AppendLine($"    ray   : {(ray is null ? "YOK" : $"{ray.ActualWidth:F1} x {ray.ActualHeight:F1}")}");
                        sb.AppendLine($"    değer : Min={cubuk.Minimum:F0} Max={cubuk.Maximum:F0} "
                                    + $"Viewport={cubuk.ViewportSize:F0}");
                        if (t is null) { sb.AppendLine("    tutamaç: YOK"); continue; }
                        sb.AppendLine($"    tutamaç: {t.ActualWidth:F1} x {t.ActualHeight:F1}  görünür={t.Visibility}");
                        sb.AppendLine($"    MinHeight={t.MinHeight:F1} ({BaglamaDurumu(t, FrameworkElement.MinHeightProperty)})");
                        sb.AppendLine($"    MinWidth ={t.MinWidth:F1} ({BaglamaDurumu(t, FrameworkElement.MinWidthProperty)})");
                    }
                    if (n == 0)
                        sb.AppendLine("(grid içinde HİÇ ScrollBar yok)");
                }

                // 🗺 PENCERENİN TAMAMINDAKİ kaydırıcılar, PENCERE koordinatlarıyla (27 Ağu, 2. tur):
                // zoom görüntüsü grid'in sağında İKİ dikey öğe gösterdi — biri 59 px (sağlıklı),
                // biri ~8 px'lik yumru (kullanıcının "noktası"). Hangisi hangi kontrole ait,
                // tahminle değil koordinatla eşleştirilir.
                sb.AppendLine();
                sb.AppendLine("── PENCEREDEKİ TÜM KAYDIRICILAR (pencere koordinatları) ──");
                foreach (ScrollBar cubuk in Bul<ScrollBar>(win))
                {
                    if (cubuk.Visibility != Visibility.Visible || cubuk.ActualWidth <= 0)
                        continue;
                    Point konum = cubuk.TransformToAncestor(win).Transform(new Point(0, 0));
                    Thumb? t = Bul<Thumb>(cubuk).FirstOrDefault();
                    // Sahibini bul: görsel ağaçta yukarı çıkıp ilk tanınabilir kontrol
                    string sahip = "?";
                    DependencyObject? ata = cubuk;
                    while ((ata = VisualTreeHelper.GetParent(ata!)) is not null)
                    {
                        if (ata is DataGrid) { sahip = "DataGrid"; break; }
                        if (ata is ScrollViewer sv && VisualTreeHelper.GetParent(sv) is not DataGrid)
                        { sahip = $"ScrollViewer({sv.Name})"; break; }
                        if (ata is ICSharpCode.AvalonEdit.TextEditor) { sahip = "Editör"; break; }
                    }
                    sb.AppendLine($"{cubuk.Orientation,-10} konum=({konum.X:F0},{konum.Y:F0}) "
                                + $"boy={cubuk.ActualWidth:F0}x{cubuk.ActualHeight:F0} sahip={sahip}");
                    if (t is not null)
                        sb.AppendLine($"           tutamaç {t.ActualWidth:F1}x{t.ActualHeight:F1} "
                                    + $"MinH={t.MinHeight:F1}({BaglamaDurumu(t, FrameworkElement.MinHeightProperty)}) "
                                    + $"MinW={t.MinWidth:F1}");
                }

                // 📸 GÖZLE doğrulama: sayılar "tutamaç 59 px, Active" derken kullanıcı "nokta"
                // görüyor — kalan şüpheli GÖRÜNÜM (renk/kontrast). RenderTargetBitmap ekran
                // kilidinden etkilenmez; pencereyi olduğu gibi PNG'ye basar.
                win.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)win.ActualWidth, (int)win.ActualHeight, 96, 96,
                    PixelFormats.Pbgra32);
                rtb.Render(win);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (FileStream fs = File.Create(Path.Combine(Path.GetTempPath(), "sqlst-gercek.png")))
                    enc.Save(fs);

                // 🎯 PİKSEL SAYIMI (27 Ağu, 4. tur — ARTIK TEK GEÇERLİ KANIT BU): Thumb.ActualHeight
                // üç tur boyunca beni yanılttı (Track elemanı şişirip KIRPIYOR; 59 ölçülür, 4 çizilir).
                // Ekranda ne olduğunu yalnız ekrana çizilen piksel söyler: grid kaydırıcısının orta
                // sütununda tutamaç renginde kaç piksel var, o sayılır.
                DataGrid? gp = Bul<DataGrid>(win).FirstOrDefault(x => x.ItemsSource is System.Data.DataView);
                ScrollBar? dvp = gp is null ? null : Bul<ScrollBar>(gp)
                    .FirstOrDefault(s => s.Orientation == Orientation.Vertical);
                if (dvp is not null)
                {
                    Point k = dvp.TransformToAncestor(win).Transform(new Point(dvp.ActualWidth / 2, 0));
                    var olcumRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    win.UpdateLayout();
                    olcumRtb.Render(win);
                    int sutunX = (int)k.X, basY = (int)k.Y, boyH = (int)dvp.ActualHeight;
                    var piksel = new byte[4];
                    int boyali = 0;
                    Color hedef = ((SolidColorBrush)win.FindResource("KaydiriciTutamacFircasi")).Color;
                    for (int y = basY; y < basY + boyH && y < (int)win.ActualHeight; y++)
                    {
                        olcumRtb.CopyPixels(new Int32Rect(sutunX, y, 1, 1), piksel, 4, 0);
                        // BGRA sırası; tutamaç rengine yakınlık (±12) sayılır
                        if (Math.Abs(piksel[2] - hedef.R) <= 12 && Math.Abs(piksel[1] - hedef.G) <= 12
                            && Math.Abs(piksel[0] - hedef.B) <= 12)
                            boyali++;
                    }
                    sb.AppendLine();
                    sb.AppendLine($"── PİKSEL SAYIMI (x={sutunX}, y={basY}..{basY + boyH}) ──");
                    sb.AppendLine($"EKRANA ÇİZİLEN tutamaç: {boyali} px (hedef renk {hedef})");
                }

                // 🔎 RENDER SONRASI yeniden ölç (27 Ağu, 3. tur): zoom görüntüsünde grid tutamağının
                // yalnız ~10 px'i boyalı — oysa ölçüm 59 px demişti. İki ihtimal: (a) tutamağın
                // İÇİNDEKİ Border onu doldurmuyor, (b) render anında tutamak ölçüm anındakinden
                // küçük. İkisini de sayıya bağla: Border'ın gerçek boyu + render sonrası tutamak boyu.
                sb.AppendLine();
                sb.AppendLine("── RENDER SONRASI (grid kaydırıcısı) ──");
                DataGrid? g2 = Bul<DataGrid>(win).FirstOrDefault(x => x.ItemsSource is System.Data.DataView);
                if (g2 is not null)
                {
                    ScrollBar? dv = Bul<ScrollBar>(g2).FirstOrDefault(s => s.Orientation == Orientation.Vertical);
                    Thumb? t2 = dv is null ? null : Bul<Thumb>(dv).FirstOrDefault();
                    if (t2 is not null)
                    {
                        sb.AppendLine($"tutamaç: {t2.ActualWidth:F1}x{t2.ActualHeight:F1} MinH={t2.MinHeight:F1}");
                        Border? govde = Bul<Border>(t2).FirstOrDefault();
                        sb.AppendLine(govde is null
                            ? "Border YOK (şablon uygulanmamış?)"
                            : $"Border : {govde.ActualWidth:F1}x{govde.ActualHeight:F1} "
                              + $"zemin={((govde.Background as SolidColorBrush)?.Color.ToString() ?? "?")}");
                    }
                }

                win.Close();
            });
        }
        catch (Exception ex)
        {
            sb.AppendLine("TANI SIRASINDA HATA: " + ex.Message);
        }
        finally
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "sqlst-gercek-kaydirici.txt"), sb.ToString());
            try { Directory.Delete(tempData, recursive: true); } catch { /* geçici klasör */ }
        }
    }
}
