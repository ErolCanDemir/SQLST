using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLST.App.ViewModels;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v20-S21 saha m.30 (2026-08-14, kullanıcı: "gridi kaydırdım, başka sekmeye geçip döndüm — grid
/// en üste dönmüş"): TabControl'ün tek ContentPresenter'ı her geçişte görseli söküp yeniden
/// kuruyordu; kaydırma/seçim/imleç sıfırlanıyordu. Artık her sekmenin görseli BİR KEZ kurulup
/// canlı tutulur. Bu sınıf çekirdek garantiyi sabitler: geçiş sonrası dönünce AYNI görsel örnek
/// (Assert.Same) — aynı örnek = kaydırma konumu dahil tüm UI durumu yerinde; kapanan sekmenin
/// sunucusu ise ATILIR (VM/grid belleği kaçmaz).
/// </summary>
public class SekmeIcerikCanliTests
{
    private static ConnectionProfile LocalDbProfil() => new()
    {
        Ad = "Test", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
    };

    /// <summary>İçerik yuvası grid'i (şablondaki SekmeIcerikYuvasi) — sekme BAŞLIKLARININ
    /// sunucularıyla karışmasın diye arama yalnız bu yuvanın çocuklarında yapılır.</summary>
    private static Grid? YuvaBul(DependencyObject kok)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(kok); i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(kok, i);
            if (c is Grid { Name: "SekmeIcerikYuvasi" } g)
                return g;
            if (YuvaBul(c) is { } alt)
                return alt;
        }
        return null;
    }

    private static ContentPresenter? SunucuBul(DependencyObject kok, object icerik)
        => YuvaBul(kok)?.Children.OfType<ContentPresenter>()
            .FirstOrDefault(cp => ReferenceEquals(cp.Content, icerik));

    [Fact]
    public void Sekme_gecisi_gorseli_sokmez_kapanis_sunucuyu_atar()
    {
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-m30-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        try
        {
            (MainWindow win, MainViewModel vm) = d.Invoke(() =>
            {
                StaOrtak.Birlestir("PaletKoyu.xaml");
                StaOrtak.Birlestir("Tema.xaml");
                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1100; w.Height = 700;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                return (w, (MainViewModel)w.DataContext);
            });

            d.Invoke(() => win.ProfilUygula(LocalDbProfil()));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1200)));

            d.Invoke(() =>
            {
                SorguSekmesiViewModel a = vm.SekmeAc("a.sql", "SELECT 1", null);
                SorguSekmesiViewModel b = vm.SekmeAc("b.sql", "SELECT 2", null);
                vm.SeciliSekme = a;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

                ContentPresenter? ilkA = SunucuBul(win, a);
                Assert.NotNull(ilkA);
                Assert.True(ilkA!.IsVisible);

                vm.SeciliSekme = b; // başka sekmeye geç…
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                Assert.False(ilkA.IsVisible);          // gizlendi ama SÖKÜLMEDİ

                vm.SeciliSekme = a; // …ve geri dön
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                ContentPresenter? tekrarA = SunucuBul(win, a);
                Assert.Same(ilkA, tekrarA);            // AYNI görsel örnek → kaydırma/seçim yerinde
                Assert.True(ilkA.IsVisible);

                vm.SekmeKapat(b); // kapanan sekmenin sunucusu atılmalı (bellek kaçmasın)
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                Assert.Null(SunucuBul(win, b));
            });
            d.Invoke(win.Close);
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { }
        }
    }
}
