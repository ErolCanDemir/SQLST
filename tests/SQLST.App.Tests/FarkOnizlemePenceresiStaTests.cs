using System.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SQLST.App.Views;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// BF-3 (2026-07-27): tam eşitleme fark önizleme penceresi UI testi. Doğrular: sekme başlıkları kova
/// sayılarını gösterir · gridler dolu · "sil" KAPALIYKEN üretilen script'te DELETE YOK (yalnız
/// UPDATE+INSERT) · "sil" AÇIKKEN DELETE var · "Script Üret" callback'i Güvenli Yazma metnini verir.
/// </summary>
public class FarkOnizlemePenceresiStaTests
{
    private static Dictionary<string, object?> S(params (string, object?)[] a)
        => a.ToDictionary(x => x.Item1, x => x.Item2);

    private static TabloFarki Fark() => new(
        Yeniler: [new FarkSatiri(S(("Id", 3), ("Ad", "Yeni")))],
        Degisenler: [new DegisenSatir(
            S(("Id", 2)),
            new Dictionary<string, (object?, object?)> { ["Ad"] = ("Ayse", "Ayse2") },
            S(("Id", 2), ("Ad", "Ayse2")))],
        Silinenler: [new FarkSatiri(S(("Id", 9), ("Ad", "Silinecek")))]);

    [Fact]
    public void Silme_kapaliyken_DELETE_uretilmez_acikken_uretilir()
    {
        (string yeniBaslik, string degisenBaslik, string silinenBaslik,
            int yeniSatir, int degisenSatir, int silinenSatir,
            string silmeKapali, string silmeAcik) = StaOrtak.Sta().Invoke(() => Kosu());

        Assert.Equal("➕ Yeni (1)", yeniBaslik);
        Assert.Equal("✏ Değişen (1)", degisenBaslik);
        Assert.Equal("🗑 Silinecek (1)", silinenBaslik);
        Assert.Equal(1, yeniSatir);
        Assert.Equal(1, degisenSatir);  // hücre başına satır: Id=2, Ad Ayse→Ayse2
        Assert.Equal(1, silinenSatir);

        // Sil KAPALI: UPDATE + INSERT var, DELETE YOK.
        Assert.Contains("UPDATE [dbo].[Musteri] SET [Ad] = N'Ayse2' WHERE [Id] = 2;", silmeKapali);
        Assert.Contains("INSERT INTO [dbo].[Musteri] ([Id], [Ad]) VALUES (3, N'Yeni');", silmeKapali);
        Assert.DoesNotContain("DELETE", silmeKapali);

        // Sil AÇIK: DELETE de var ve ilk sırada (DELETE→UPDATE→INSERT).
        Assert.Contains("DELETE FROM [dbo].[Musteri] WHERE [Id] = 9;", silmeAcik);
        Assert.StartsWith("DELETE", silmeAcik);
    }

    private static (string, string, string, int, int, int, string, string) Kosu()
    {
        StaOrtak.Birlestir("PaletAcik.xaml");
        StaOrtak.Birlestir("Tema.xaml");
        var lehce = new MssqlLehcesi(new DpapiSecretProtector());

        string? kapali = null;
        var w1 = new FarkOnizlemePenceresi(Fark(), lehce, "dbo", "Musteri", ["Id"], sql => kapali = sql)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w1.Show();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(150));

        string yeniBaslik = ((TabItem)w1.FindName("YeniSekme")).Header.ToString()!;
        string degisenBaslik = ((TabItem)w1.FindName("DegisenSekme")).Header.ToString()!;
        string silinenBaslik = ((TabItem)w1.FindName("SilinenSekme")).Header.ToString()!;
        int yeniSatir = ((DataView)((DataGrid)w1.FindName("YeniGrid")).ItemsSource!).Count;
        int degisenSatir = ((DataView)((DataGrid)w1.FindName("DegisenGrid")).ItemsSource!).Count;
        int silinenSatir = ((DataView)((DataGrid)w1.FindName("SilinenGrid")).ItemsSource!).Count;

        // Varsayılan AÇIK (tam eşitleme) → testte kapalı durumu için açıkça kapat, sonra Script Üret.
        ((CheckBox)w1.FindName("SilmeyiUygula")).IsChecked = false;
        ((Button)w1.FindName("BtnFarkScript")).RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
        StaOrtak.Pump(TimeSpan.FromMilliseconds(100));

        // Yeni pencere, sil kutusu AÇIK.
        string? acik = null;
        var w2 = new FarkOnizlemePenceresi(Fark(), lehce, "dbo", "Musteri", ["Id"], sql => acik = sql)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w2.Show();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(150));
        ((CheckBox)w2.FindName("SilmeyiUygula")).IsChecked = true;
        ((Button)w2.FindName("BtnFarkScript")).RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
        StaOrtak.Pump(TimeSpan.FromMilliseconds(100));

        return (yeniBaslik, degisenBaslik, silinenBaslik, yeniSatir, degisenSatir, silinenSatir,
            kapali ?? "", acik ?? "");
    }
}
