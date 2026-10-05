using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SQLST.App;

/// <summary>
/// MODELSİZ (<see cref="Window.Show"/> ile açılan) pencerelerde "Kapat / İptal" kapısı.
///
/// KÖK NEDEN (v22-S4 saha turu-4 m.1 — kullanıcı: "önizlemeye kapat butonu koymuşuz, çalışmıyor"):
/// WPF'te <see cref="Button.IsCancel"/> yalnız <see cref="Window.ShowDialog"/> ile açılmış pencerede
/// iş görür — tıklanınca <c>DialogResult=false</c> yazılır ve pencere kapanır. <c>Show()</c> ile
/// açılan pencerede DialogResult diye bir şey yoktur; WPF sessizce hiçbir şey yapmaz. Yani düğme
/// ölüdür ve <b>Esc de ölüdür</b> (o da aynı yoldan geçer). Hata bir tek pencerede değil, modelsiz
/// açılan HER pencerede vardır — bu yüzden düzeltme tek tek XAML'lere değil, gösterim kapısına
/// (<c>MainWindow.PencereGoster</c>) konur.
/// </summary>
internal static class PencereKapatma
{
    /// <summary>
    /// Modelsiz gösterilecek pencerede <c>IsCancel</c> taşıyan düğmeleri ve Esc'i
    /// <see cref="Window.Close"/>'a bağlar. Gösterimden ÖNCE çağrılır; düğmeler görsel ağaç
    /// kurulunca (Loaded) aranır.
    /// </summary>
    internal static void Kur(Window pencere)
    {
        pencere.PreviewKeyDown += (gonderen, e) =>
        {
            if (e.Key is Key.Escape && gonderen is Window w)
            {
                e.Handled = true;
                w.Close();
            }
        };

        if (pencere.IsLoaded)
            DugmeleriBagla(pencere);
        else
            pencere.Loaded += (gonderen, _) => DugmeleriBagla((Window)gonderen);
    }

    private static void DugmeleriBagla(Window pencere)
    {
        foreach (Button dugme in IptalDugmeleri(pencere))
            dugme.Click += (gonderen, _) => Window.GetWindow((DependencyObject)gonderen)?.Close();
    }

    /// <summary>Görsel ağaçtaki <c>IsCancel</c> düğmeleri (pencere başına bir kez taranır).</summary>
    private static List<Button> IptalDugmeleri(DependencyObject kok)
    {
        var bulunan = new List<Button>();
        Tara(kok, bulunan);
        return bulunan;

        static void Tara(DependencyObject dugum, List<Button> hedef)
        {
            if (dugum is Button { IsCancel: true } dugme)
                hedef.Add(dugme);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(dugum); i++)
                Tara(VisualTreeHelper.GetChild(dugum, i), hedef);
        }
    }
}
