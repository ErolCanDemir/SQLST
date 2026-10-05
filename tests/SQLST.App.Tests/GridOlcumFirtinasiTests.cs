using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S4 saha turu-4 m.8 — ÇÖKMENİN KANITLI DÜZELTMESİ.
///
/// Kanıt zinciri (dört turun sonunda ilk kez elde edildi):
/// <list type="bullet">
///   <item>Saha WER dökümleri (27 Tem, beş adet; ikisi okundu): ikisi de
///     <b>StackOverflowException</b>, UI thread'i WPF MEASURE zincirinde
///     (TextBlock.MeasureOverride → FormatLine → GetGlyphs). SO yakalanamaz → log yazılamaz —
///     "hiçbir yerde kayıt yok"un açıklaması.</item>
///   <item>Saha uçuş kaydı (20 Ağu): 33.964 satır × 43 kolon KonfigurasyonParametre'de
///     "Grid hazır"dan hemen sonra <b>UI 832 sn bloke + yığın 6,4 GB</b>.</item>
///   <item>Saha faz izi: <c>grid-hazir</c>'da kalıyor, <c>yerlesim-bitti</c> hiç gelmiyor.</item>
/// </list>
/// Kök: kolon genişliği SINIRSIZDI (depoda MaxColumnWidth hiç geçmiyordu) — dev config metinleri
/// Auto genişlikte ölçüm fırtınası yaratıyor. Düzeltme Tema.xaml'de: MaxColumnWidth=400 +
/// VirtualizationMode=Recycling. Bu testler o iki mekanizmayı kilitler.
/// </summary>
public class GridOlcumFirtinasiTests
{
    private static readonly bool Etkin = OperatingSystem.IsWindows();

    /// <summary>Sahadaki tabloya benzeyen sonuç: çok satır + bir kolonda ÇOK uzun metin.</summary>
    private static DataTable DevTablo(int satir, int uzunluk)
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Ad", typeof(string));
        t.Columns.Add("Deger", typeof(string));   // KonfigurasyonParametre.Deger — dev metin
        t.Columns.Add("Tarih", typeof(DateTime));
        string dev = new('x', uzunluk);
        t.BeginLoadData();
        for (int i = 0; i < satir; i++)
            t.Rows.Add(i, $"param{i}", dev, new DateTime(2026, 8, 20));
        t.EndLoadData();
        return t;
    }

    private static (DataGrid Grid, Window Pencere) GridKur(DataTable tablo)
    {
        StaOrtak.Birlestir("PaletKoyu.xaml");
        StaOrtak.Birlestir("Tema.xaml");

        // Ana penceredeki SetGrid ile aynı kritik ayarlar; stil Tema.xaml'den (örtük) gelir.
        var grid = new DataGrid
        {
            ItemsSource = tablo.DefaultView,
            IsReadOnly = true,
            AutoGenerateColumns = true,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            Height = 600,
        };
        var w = new Window
        {
            Width = 1200, Height = 700, Content = grid,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        w.Show();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(700));
        return (grid, w);
    }

    private static int Say<T>(DependencyObject kok) where T : DependencyObject
    {
        int n = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(kok); i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(kok, i);
            if (c is T)
                n++;
            n += Say<T>(c);
        }
        return n;
    }

    [Fact]
    public void Dev_metinli_kolon_400px_tavaninda_kalir()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            (DataGrid grid, Window w) = GridKur(DevTablo(satir: 200, uzunluk: 100_000));

            // Tema stili uygulanmış olmalı — kök düzeltmenin kendisi.
            Assert.Equal(400, grid.MaxColumnWidth);
            // 100.000 karakterlik kolon bile 400'ü aşamaz (eskiden Auto → on binlerce px).
            foreach (DataGridColumn kolon in grid.Columns)
                Assert.True(kolon.ActualWidth <= 400,
                    $"kolon {kolon.Header} {kolon.ActualWidth:N0} px — tavan delindi");
            w.Close();
        });
    }

    [Fact]
    public void Cok_satirli_sonucta_yalniz_gorunur_satirlar_kurulur()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            // Sahadaki ölçekte satır sayısı; metin kısa (bu test SANALLAŞTIRMAYI ölçer).
            (DataGrid grid, Window w) = GridKur(DevTablo(satir: 30_000, uzunluk: 40));

            int kurulan = Say<DataGridRow>(grid);
            // 600 px / 25 px satır ≈ 24 görünür satır; pay bırakıyoruz. 30.000'e yaklaşan her
            // değer sanallaştırmanın öldüğü anlamına gelir — sahadaki 6,4 GB'ın mekanizması.
            Assert.InRange(kurulan, 1, 200);
            w.Close();
        });
    }

    /// <summary>
    /// Ölçüm fırtınasının bütünü: dev metin + çok satır birlikte, pencere makul sürede açılmalı.
    /// Süre assert'i bilerek CÖMERT — amaç performans ölçmek DEĞİL, "832 sn / sonsuz" sınıfı bir
    /// gerilemeyi yakalamak. Sahada aynı desen 14 dakikada bitmemişti.
    ///
    /// ⚠ BÜTÇE 30 → 150 sn (24 Ağu 2026): 30 sn kararsızdı — tam süitte (önceki testlerin bıraktığı
    /// GC baskısıyla) aynı kuruluş meşru olarak 55 sn sürdü ve test kırmızı verdi; izole koşuda
    /// geçiyordu. Bu bir gerileme değil, süit yükü sapmasıydı. 150 sn testin GERÇEK amacını hâlâ
    /// koruyor (832 sn / sonsuz sınıfını yakalar) ama makinenin o anki yüküne bahis oynamıyor.
    /// Bu testte PumpUntil işe yaramaz: burada beklenen bir sinyal değil, ölçülen ŞEYİN KENDİSİ süre.
    /// </summary>
    [Fact]
    public void Dev_metin_ve_cok_satir_birlikte_makul_surede_acilir()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            var kronometre = System.Diagnostics.Stopwatch.StartNew();
            (DataGrid _, Window w) = GridKur(DevTablo(satir: 30_000, uzunluk: 100_000));
            kronometre.Stop();

            Assert.True(kronometre.Elapsed < TimeSpan.FromSeconds(150),
                $"grid {kronometre.Elapsed.TotalSeconds:N0} sn'de açıldı — ölçüm fırtınası geri döndü");
            w.Close();
        });
    }
}
