using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SQLST.Application;

namespace SQLST.App;

/// <summary>
/// TextBox'a hafif otomatik doldurma takar (v11-öncesi #3 — Find yardımcısı, 2026-07-25).
/// AvalonEdit CompletionWindow kullanılmadı: o TextEditor'a bağlıdır, buradaki kutular düz
/// TextBox. Popup + ListBox yeterli: yazarken öneri düşer, ↑/↓ gezer, Enter/Tab uygular,
/// Esc kapatır, Ctrl+Space zorla açar. Öneri üretimi SAF <see cref="MongoBulTamamlama"/>'da.
/// </summary>
public sealed class TamamlamaAdaptoru
{
    private readonly TextBox _kutu;
    private readonly Func<string, int, MongoTamamlamaSonucu?> _oner;
    private readonly Popup _popup;
    private readonly ListBox _liste;
    private MongoTamamlamaSonucu? _sonuc;
    private bool _uygulaniyor; // Uygula sırasındaki TextChanged döngüsünü keser

    public TamamlamaAdaptoru(TextBox kutu, Func<string, int, MongoTamamlamaSonucu?> oner)
    {
        _kutu = kutu;
        _oner = oner;

        _liste = new ListBox
        {
            MaxHeight = 180,
            MinWidth = 190,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
        _liste.SetResourceReference(Control.ForegroundProperty, "MetinFircasi");
        _liste.ItemTemplate = OgeSablonu();
        _liste.PreviewMouseLeftButtonUp += (_, _) => Uygula();

        var cerceve = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            Child = _liste,
        };
        cerceve.SetResourceReference(Border.BackgroundProperty, "PencereZeminFircasi");
        cerceve.SetResourceReference(Border.BorderBrushProperty, "KenarFircasi");

        _popup = new Popup
        {
            PlacementTarget = _kutu,
            Placement = PlacementMode.Relative,
            StaysOpen = true, // odak TextBox'ta kalır; kapanışı biz yönetiriz
            AllowsTransparency = true,
            Child = cerceve,
        };

        _kutu.TextChanged += (_, _) => { if (!_uygulaniyor) Tazele(); };
        _kutu.PreviewKeyDown += TusYakala;
        _kutu.LostKeyboardFocus += (_, _) => Kapat();
    }

    private static DataTemplate OgeSablonu()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var ad = new FrameworkElementFactory(typeof(TextBlock));
        ad.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MongoOneri.Goster)));
        ad.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
        panel.AppendChild(ad);

        var aciklama = new FrameworkElementFactory(typeof(TextBlock));
        aciklama.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MongoOneri.Aciklama)));
        aciklama.SetValue(TextBlock.MarginProperty, new Thickness(10, 0, 0, 0));
        aciklama.SetValue(TextBlock.OpacityProperty, 0.55);
        aciklama.SetValue(TextBlock.FontSizeProperty, 11.0);
        panel.AppendChild(aciklama);

        return new DataTemplate { VisualTree = panel };
    }

    /// <summary>Ctrl+Space için dışarıdan da çağrılabilir: imleç konumuna göre önerileri açar/kapatır.</summary>
    public void Tazele()
    {
        _sonuc = _oner(_kutu.Text, _kutu.CaretIndex);
        if (_sonuc is null || _sonuc.Oneriler.Count == 0)
        {
            Kapat();
            return;
        }

        _liste.ItemsSource = _sonuc.Oneriler;
        _liste.SelectedIndex = 0;

        // Popup imlecin altına: satır sonu/kaydırmada da doğru yerde dursun
        Rect r = _kutu.GetRectFromCharacterIndex(Math.Min(_kutu.CaretIndex, _kutu.Text.Length));
        _popup.HorizontalOffset = r.Left;
        _popup.VerticalOffset = r.Bottom + 2;
        _popup.IsOpen = true;
    }

    private void TusYakala(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Tazele();
            e.Handled = true;
            return;
        }
        if (!_popup.IsOpen)
            return;

        switch (e.Key)
        {
            case Key.Down:
                _liste.SelectedIndex = Math.Min(_liste.SelectedIndex + 1, _liste.Items.Count - 1);
                _liste.ScrollIntoView(_liste.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _liste.SelectedIndex = Math.Max(_liste.SelectedIndex - 1, 0);
                _liste.ScrollIntoView(_liste.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Uygula();
                e.Handled = true;
                break;
            // v22-S4 saha turu-4 m.5 (kullanıcı: "değeri girdiğimde Tab ile geçiyorum, sorun
            // oluyor"): Tab ARTIK öneri uygulamaz. Değer konumunda liste çoğu zaman açık kalıyor
            // (true/false/null, 1/-1) ve Tab'ı kaçırdığı için kullanıcı bir sonraki kutuya
            // geçemiyor, istemediği öneri metne giriyordu. Tab = odak geçişi (WPF'e bırakılır),
            // öneriyi uygulamak için Enter var. Popup yalnız kapatılır.
            case Key.Tab:
                Kapat();
                break;
            case Key.Escape:
                Kapat();
                e.Handled = true;
                break;
        }
    }

    private void Uygula()
    {
        if (_sonuc is null || _liste.SelectedItem is not MongoOneri oneri)
            return;

        _uygulaniyor = true;
        (string metin, int caret) = MongoBulTamamlama.Uygula(
            _kutu.Text, _sonuc.ParcaBas, _kutu.CaretIndex, oneri.Ekle, oneri.ImlecGeri);
        _kutu.Text = metin;
        _kutu.CaretIndex = caret;
        _uygulaniyor = false;

        // Zincir akışı (Compass): alan uygulanınca değer konumuna düşülür → 1/-1 önerisi hemen açılır
        Tazele();
    }

    private void Kapat()
    {
        _popup.IsOpen = false;
        _sonuc = null;
    }
}
