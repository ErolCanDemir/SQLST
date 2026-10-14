using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SQLST.App.ViewModels;

namespace SQLST.App.Views;

/// <summary>📊 Grafik paneli kod-arkası (v23-S24; v23-S30 pencereye taşındı): PNG (pano/dosya — K6), tuval genişliği,
/// açıklamadan gizleme, grafik → grid satır seçimi (<see cref="HedefGrid"/>).</summary>
public partial class GrafikPaneli : UserControl
{
    /// <summary>XAML'deki ComboBox için statik liste (VM'deki ile aynı nesne).</summary>
    public static IReadOnlyList<ToplamaSecenegi> ToplamaSecenekleri => GrafikPaneliViewModel.ToplamaSecenekleri;

    public GrafikPaneli()
    {
        InitializeComponent();
    }

    /// <summary>Çubuğa tıklayınca satırların seçileceği veri tablosu — pencere verir.</summary>
    public DataGrid? HedefGrid { get; set; }

    /// <summary>DataContext'in Grafik'i (GrafikPenceresiViewModel).</summary>
    private GrafikPaneliViewModel? Grafik => (DataContext as GrafikPenceresiViewModel)?.Grafik;

    private void Tuval_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Grafik is { } g)
            g.EkranGenisligi = (int)e.NewSize.Width;
    }

    /// <summary>S2: açıklama öğesine tık → seri/dilim gizle-göster.</summary>
    private void Aciklama_Tik(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AciklamaOgesi o && Grafik is { } g)
            g.AciklamaDegistir(o.Ad);
    }

    /// <summary>S2: çubuğa/dilime/noktaya tık → veri tablosunda o kategorinin satırları seçilir.</summary>
    private void Tuval_Tiklandi(GrafikVurusu v) => KategoriSec(v.Kategori, v.Seri);

    /// <summary>Grafik ↔ grid köprüsü: vuruşun satırları grid'de seçilir, ilki görünür yapılır. Dönüş: seçilen sayısı.</summary>
    public int KategoriSec(int kategori, int seri)
    {
        if (Grafik is not { } g || HedefGrid is not { } grid)
            return 0;
        (List<System.Data.DataRowView> satirlar, int toplam) = g.SatirlariBul(kategori, seri);
        grid.SelectedCells.Clear();
        grid.SelectedItems.Clear();
        foreach (System.Data.DataRowView s in satirlar)
            grid.SelectedItems.Add(s);
        if (satirlar.Count > 0)
            grid.ScrollIntoView(satirlar[0]);
        g.Durum = toplam == 0 ? "Grid'de eşleşen satır yok"
            : toplam > satirlar.Count ? $"{satirlar.Count:N0} / {toplam:N0} satır seçildi (tavan)"
            : $"{toplam:N0} satır grid'de seçildi";
        return satirlar.Count;
    }

    /// <summary>Gövdeyi (tuval + açıklama) tema zemini üzerine %100 ölçekte bitmap'e çizer.</summary>
    public RenderTargetBitmap? GoruntuAl()
    {
        int w = (int)Math.Ceiling(GrafikGovde.ActualWidth), h = (int)Math.Ceiling(GrafikGovde.ActualHeight);
        if (w <= 0 || h <= 0)
            return null;
        var gorsel = new DrawingVisual();
        using (DrawingContext dc = gorsel.RenderOpen())
        {
            dc.DrawRectangle(TryFindResource("PencereZeminFircasi") as Brush ?? Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(GrafikGovde), null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(gorsel);
        return rtb;
    }

    private void PngKopyala_Click(object sender, RoutedEventArgs e)
    {
        if (Grafik is not { } g || GoruntuAl() is not { } bitmap)
            return;
        // Pano paylaşımlı kaynak: pano geçmişi / başka uygulama anlık tutuyorsa OpenClipboard düşer
        // (CLIPBRD_E_CANT_OPEN) — birkaç kısa deneme, sonra dürüst mesaj.
        for (int deneme = 0; ; deneme++)
        {
            try
            {
                Clipboard.SetImage(bitmap);
                g.Durum = $"PNG panoya kopyalandı ({bitmap.PixelWidth}×{bitmap.PixelHeight})";
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (deneme < 5)
            {
                Thread.Sleep(80);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                g.Durum = "Pano meşgul — yeniden deneyin.";
                return;
            }
        }
    }

    private void PngKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (Grafik is not { } g || GoruntuAl() is not { } bitmap)
            return;
        var diyalog = new SaveFileDialog
        {
            Filter = "PNG görüntü (*.png)|*.png",
            FileName = $"sqlst-grafik-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if (diyalog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            var kodlayici = new PngBitmapEncoder();
            kodlayici.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream fs = File.Create(diyalog.FileName))
                kodlayici.Save(fs);
            g.Durum = $"PNG kaydedildi: {Path.GetFileName(diyalog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(Window.GetWindow(this), "SQLST", "Grafik kaydedilemedi", ex);
        }
    }
}
