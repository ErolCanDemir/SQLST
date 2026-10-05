using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using SQLST.App.ViewModels;

namespace SQLST.App;

/// <summary>
/// Hatalı sorgu satırının altına KIRMIZI DALGA çizer (kullanıcı isteği 2026-07-23: "hatalı
/// sorguların altı yansın" — SSMS/IDE deseni). Kaynağı VM'deki <see cref="SorguSekmesiViewModel.HataSatiri"/>:
/// çalıştırma hatayla bitince dolar, metin değişince söner. Mongo'da VM hiç doldurmaz.
///
/// Canlı (yazarken) söz dizimi denetimi DEĞİLDİR — o, motor başına tam SQL ayrıştırıcısı ister;
/// buradaki çizgi sunucunun GERÇEK hatasını işaret eder (satır eşlemesi BatchYurutucu'dan).
/// </summary>
public sealed class HataAltCizgisi : IBackgroundRenderer
{
    private readonly TextEditor _editor;
    private readonly Pen _kalem;

    public HataAltCizgisi(TextEditor editor)
    {
        _editor = editor;
        _kalem = new Pen(Brushes.Red, 1.2);
        _kalem.Freeze();

        // VM değişimini izle: HataSatiri her değiştiğinde katmanı tazele. Editör sekme
        // şablonunda yaşar → DataContext sekme VM'idir; sekme kapanınca editörle birlikte ölür.
        editor.DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SorguSekmesiViewModel eski)
                eski.PropertyChanged -= VmDegisti;
            if (e.NewValue is SorguSekmesiViewModel yeni)
                yeni.PropertyChanged += VmDegisti;
        };
        if (editor.DataContext is SorguSekmesiViewModel vm)
            vm.PropertyChanged += VmDegisti;
    }

    private void VmDegisti(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SorguSekmesiViewModel.HataSatiri)
            or nameof(SorguSekmesiViewModel.HataSatirlari)) // çoklu hata (2026-07-30)
            _editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_editor.DataContext is not SorguSekmesiViewModel vm || textView.Document is not { } belge)
            return;

        // Tek hata (çalıştırma) + çoklu hata (canlı denetim, 2026-07-30) birlikte: tekilleştirilmiş küme.
        var satirlar = new HashSet<int>(vm.HataSatirlari);
        if (vm.HataSatiri is int tek)
            satirlar.Add(tek);

        foreach (int satir in satirlar)
        {
            if (satir < 1 || satir > belge.LineCount)
                continue;
            DocumentLine hatali = belge.GetLineByNumber(satir);
            var bolge = new TextSegment { StartOffset = hatali.Offset, EndOffset = hatali.EndOffset };
            foreach (Rect r in BackgroundGeometryBuilder.GetRectsForSegment(textView, bolge))
            {
                if (r.Width < 1)
                    continue;
                drawingContext.DrawGeometry(null, _kalem, Dalga(r));
            }
        }
    }

    /// <summary>Metnin hemen altında 3px adımlı zikzak (dalgalı alt çizgi) geometrisi.</summary>
    private static StreamGeometry Dalga(Rect r)
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            double y = r.Bottom - 1.5;
            ctx.BeginFigure(new Point(r.Left, y), isFilled: false, isClosed: false);
            bool ust = true;
            for (double x = r.Left + 3; x < r.Right; x += 3, ust = !ust)
                ctx.LineTo(new Point(x, ust ? y + 2 : y), isStroked: true, isSmoothJoin: false);
        }
        g.Freeze();
        return g;
    }
}
