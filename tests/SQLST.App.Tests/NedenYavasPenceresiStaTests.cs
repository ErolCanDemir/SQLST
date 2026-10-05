using System.Windows.Controls;
using SQLST.App.Views;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// BF-7 (2026-07-27): "Neden yavaş?" rapor penceresi smoke testi — bulgular bağlanır, özet doğru,
/// önem DataTrigger'ları (enum x:Static) + NullGizle converter'ı hata vermeden yüklenir.
/// </summary>
public class NedenYavasPenceresiStaTests
{
    [Fact]
    public void Bulgular_baglanir_ve_ozet_darbogazi_bildirir()
    {
        (int adet, string ozet) = StaOrtak.Sta().Invoke(() => Kosu());

        Assert.Equal(2, adet);                      // eksik index + en pahalı adım
        Assert.Contains("Olası darboğazlar", ozet); // DarbogazVar → bu başlık
    }

    private static (int, string) Kosu()
    {
        StaOrtak.Birlestir("PaletAcik.xaml");
        StaOrtak.Birlestir("Tema.xaml");

        var eksik = new PlanEksikIndexi(80, "[dbo].[Siparis]", ["MusteriId"], [], []);
        var kok = new PlanDugumu("Index Seek", "[dbo].[Siparis]", 100, null, null, [], []);
        var plan = new SQLST.Contracts.SorguPlani(true, [new SQLST.Contracts.IfadePlani("SELECT", 1.2, kok, [eksik])]);
        YavaslikRaporu rapor = YavaslikCozumleyici.Coz(plan);

        string? yakalanan = null;
        var w = new NedenYavasPenceresi(rapor, "Sekme 1", s => yakalanan = s)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w.Show();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(150));

        var liste = (ItemsControl)w.FindName("BulgularListe");
        int adet = liste.Items.Count;
        string ozet = ((TextBlock)w.FindName("Ozet")).Text;
        w.Close();
        _ = yakalanan; // script callback imzası derlenir (tıklama görsel ağaç gerektirir; analizör testte kanıtlı)
        return (adet, ozet);
    }
}
