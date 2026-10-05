using System.Text.RegularExpressions;
using System.Windows;
using ICSharpCode.AvalonEdit;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// Editör "Bul ve Değiştir" penceresi (kullanıcı isteği 2026-07-23 — AvalonEdit'te hazır replace paneli
/// yok). Non-modal, üstte kalır; aktif editör belgesinde çalışır. Eşleştirme/değiştirme mantığı SAF
/// <see cref="BulDegistirci"/>'de; bu pencere yalnız seçim/kaydırma ve UI yapar.
/// </summary>
public partial class BulDegistirPenceresi : Window
{
    private readonly TextEditor _editor;

    public BulDegistirPenceresi(TextEditor editor)
    {
        _editor = editor;
        InitializeComponent();
        Loaded += (_, _) => { BulKutusu.Text = editor.SelectedText.Contains('\n') ? "" : editor.SelectedText; BulKutusu.Focus(); BulKutusu.SelectAll(); };
    }

    private Regex? Desen()
    {
        Regex? d = BulDegistirci.Desen(BulKutusu.Text, BuyukKucuk.IsChecked == true,
            TamSozcuk.IsChecked == true, RegexKutusu.IsChecked == true, out string? hata);
        if (d is null)
            Durum.Text = $"⚠ {hata}";
        return d;
    }

    /// <summary>İmleçten ileri (yoksa baştan) sonraki eşleşmeyi seçer. Bulunursa true.</summary>
    private bool SonrakiniBul()
    {
        if (Desen() is not { } desen)
            return false;

        string metin = _editor.Text;
        int baslangic = Math.Min(_editor.SelectionStart + _editor.SelectionLength, metin.Length);
        Match m = desen.Match(metin, baslangic);
        if (!m.Success && baslangic > 0)
            m = desen.Match(metin, 0); // başa sar

        if (!m.Success)
        {
            Durum.Text = "Eşleşme bulunamadı.";
            return false;
        }
        _editor.Select(m.Index, m.Length);
        _editor.ScrollToLine(_editor.Document.GetLineByOffset(m.Index).LineNumber);
        Durum.Text = "";
        return true;
    }

    private void SonrakiniBul_Click(object sender, RoutedEventArgs e) => SonrakiniBul();

    /// <summary>Seçili metin geçerli eşleşmeyse değiştirir, sonra sonrakini bulur; değilse yalnız bulur.</summary>
    private void Degistir_Click(object sender, RoutedEventArgs e)
    {
        if (Desen() is not { } desen)
            return;

        // Mevcut seçim tam bir eşleşme mi? (öyleyse onu değiştir)
        if (_editor.SelectionLength > 0)
        {
            Match m = desen.Match(_editor.SelectedText);
            if (m.Success && m.Index == 0 && m.Length == _editor.SelectionLength)
            {
                string yeni = RegexKutusu.IsChecked == true
                    ? m.Result(DegistirKutusu.Text)
                    : DegistirKutusu.Text;
                _editor.Document.Replace(_editor.SelectionStart, _editor.SelectionLength, yeni);
                _editor.CaretOffset = _editor.SelectionStart; // değiştirilen sonrasından devam
            }
        }
        SonrakiniBul();
    }

    private void TumunuDegistir_Click(object sender, RoutedEventArgs e)
    {
        if (Desen() is not { } desen)
            return;

        (string metin, int sayi) = BulDegistirci.TumunuDegistir(
            _editor.Text, desen, BulDegistirci.Yerlestirme(DegistirKutusu.Text, RegexKutusu.IsChecked == true));

        if (sayi == 0)
        {
            Durum.Text = "Eşleşme bulunamadı.";
            return;
        }
        _editor.Document.Replace(0, _editor.Document.TextLength, metin); // tek geri-al adımı
        Durum.Text = $"{sayi} eşleşme değiştirildi.";
    }

    private void Kapat_Click(object sender, RoutedEventArgs e) => Close();
}
