using System.Windows;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// BF-7 "Neden yavaş?" raporu penceresi: <see cref="YavaslikCozumleyici"/>'nin bulgu listesini
/// önceliklendirilmiş kartlar halinde gösterir (Yüksek kırmızı, Orta amber, Bilgi gri). Düzeltme
/// script'i olan bulgular "Script'i aç" ile YENİ SORGU SEKMESİNDE açılır — asla kendiliğinden
/// çalıştırılmaz (plan sekmesindeki "incele-kopyala" kuralı). Pencere veritabanına DOKUNMAZ; rapor
/// zaten hesaplanmış gelir.
/// </summary>
public partial class NedenYavasPenceresi : Window
{
    private readonly YavaslikRaporu _rapor;
    private readonly Action<string> _scriptiAc;

    public NedenYavasPenceresi(YavaslikRaporu rapor, string sekmeAdi, Action<string> scriptiAc)
    {
        _rapor = rapor;
        _scriptiAc = scriptiAc;

        InitializeComponent();

        Ozet.Text = (rapor.DarbogazVar
                ? "Olası darboğazlar aşağıda (önem sırasına göre) — "
                : "Belirgin bir darboğaz görünmüyor — ")
            + $"{(rapor.GercekPlan ? "ölçülü (gerçek)" : "tahmini")} plan · {sekmeAdi} · "
            + $"toplam maliyet {rapor.ToplamMaliyet:N4}. Düzeltme script'leri yeni sekmede açılır, "
            + "asla kendiliğinden çalışmaz.";

        BulgularListe.ItemsSource = rapor.Bulgular;
    }

    private void ScriptAc_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is YavaslikBulgusu { DuzeltmeScript: { Length: > 0 } script })
        {
            _scriptiAc(script);
            Close();
        }
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(YavaslikCozumleyici.RaporMetni(_rapor));
        Ozet.Text = "✔ Rapor panoya kopyalandı. " + Ozet.Text;
    }

    private void Kapat_Click(object sender, RoutedEventArgs e) => Close();
}
