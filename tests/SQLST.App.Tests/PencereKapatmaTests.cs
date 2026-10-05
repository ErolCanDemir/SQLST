using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SQLST.App;
using SQLST.App.Views;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S4 saha turu-4 m.1 — kullanıcı: "Sağ tıklayıp önizlemeyi açıyoruz. Oraya kapat butonu
/// koymuşuz, çalışmıyor."
///
/// KÖK NEDEN bir WPF tuzağı: <c>Button.IsCancel</c> yalnız <c>ShowDialog()</c> ile açılmış pencerede
/// iş görür (tıklanınca DialogResult=false yazılır ve pencere kapanır). <c>Show()</c> ile açılan
/// modelsiz pencerede DialogResult yoktur → WPF sessizce hiçbir şey yapmaz, düğme ÖLÜDÜR; Esc de
/// aynı yoldan geçtiği için o da ölüdür. Hata tek pencerede değil, <c>PencereGoster</c> ile açılan
/// HER pencerededir — bu yüzden test hem tuzağı hem merkezî düzeltmeyi sabitler.
/// </summary>
public class PencereKapatmaTests
{
    private static readonly bool Etkin = OperatingSystem.IsWindows();

    private static Window OnizlemePenceresi()
        => new TabloOnizlemePenceresi(
            new SQLST.Contracts.SemaNesnesi("db", "dbo", "T", SQLST.Contracts.SemaNesneTuru.Tablo, [], []),
            satirGetir: null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };

    /// <summary>
    /// GERÇEK tıklama yolu. <c>RaiseEvent(ClickEvent)</c> YETMEZ: yalnız yönlendirilmiş olayı yayar,
    /// <c>Button.OnClick()</c>'i çağırmaz — oysa <c>IsCancel</c> mantığı tam orada çalışır. Otomasyon
    /// eşi (<c>Invoke</c>) OnClick'i çağırır, yani kullanıcının farenin altında yaptığının aynısıdır.
    /// </summary>
    private static void Tikla(Button dugme)
        => ((System.Windows.Automation.Provider.IInvokeProvider)
                new System.Windows.Automation.Peers.ButtonAutomationPeer(dugme)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();

    private static Button KapatDugmesi(Window w)
    {
        List<Button> hepsi = [];
        Tara(w, hepsi);
        return hepsi.Single(b => b.IsCancel);

        static void Tara(DependencyObject d, List<Button> hedef)
        {
            if (d is Button { IsCancel: true } b)
                hedef.Add(b);
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                Tara(System.Windows.Media.VisualTreeHelper.GetChild(d, i), hedef);
        }
    }

    /// <summary>TUZAK: kurulum yapılmazsa IsCancel düğmesi modelsiz pencerede HİÇBİR ŞEY yapmaz.</summary>
    [Fact]
    public void IsCancel_tek_basina_modelsiz_pencereyi_KAPATMAZ()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            Window w = OnizlemePenceresi();
            w.Show();                                   // ← PencereGoster'in yaptığı: Show(), ShowDialog DEĞİL
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            Tikla(KapatDugmesi(w));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            Assert.True(w.IsVisible, "beklenen tuzak: IsCancel modelsiz pencerede iş görmez");
            w.Close();
        });
    }

    [Fact]
    public void Kurulumdan_sonra_kapat_dugmesi_pencereyi_kapatir()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            Window w = OnizlemePenceresi();
            PencereKapatma.Kur(w);
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            Tikla(KapatDugmesi(w));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            Assert.False(w.IsVisible);
        });
    }

    [Fact]
    public void Kurulumdan_sonra_esc_pencereyi_kapatir()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            Window w = OnizlemePenceresi();
            PencereKapatma.Kur(w);
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            var olay = new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            w.RaiseEvent(olay);
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            Assert.False(w.IsVisible);
        });
    }

    /// <summary>
    /// Merkezî düzeltme kilidi: modelsiz gösterim kapısı (<c>PencereGoster</c>) kurulumu ÇAĞIRMALI.
    /// Biri ileride satırı silerse beş pencerenin kapat düğmesi sessizce ölür — bu test yakalar.
    /// </summary>
    [Fact]
    public void PencereGoster_kapatma_kurulumunu_cagirir()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "SQLST.slnx")))
            dizin = dizin.Parent;
        Assert.NotNull(dizin);

        string kod = File.ReadAllText(
            Path.Combine(dizin!.FullName, "src", "SQLST.App", "MainWindow.xaml.cs"));
        int bas = kod.IndexOf("private void PencereGoster(Window pencere)", StringComparison.Ordinal);
        Assert.True(bas >= 0, "PencereGoster bulunamadı");
        string govde = kod[bas..(bas + 260)];

        Assert.Contains("PencereKapatma.Kur(pencere);", govde, StringComparison.Ordinal);
    }
}
