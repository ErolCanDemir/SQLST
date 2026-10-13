using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLST.App.ViewModels;
using SQLST.App.Views;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 📊 Sonuçtan grafik paneli (v23-S24 S1, K1–K8): GERÇEK MainWindow + LocalDB sonucu. Panel kendiliğinden AÇILMAZ (K8);
/// 📊 düğmesi açar ve otomatik tahminle DOLU gelir; seri kolonu pivotlar; tür çipi pastaya çevirir; PNG panoya gider;
/// yeniden çalıştırmada panel AÇIK kalır ve seçimleri korur; ✕ kapatınca yeni sonuçta da kapalı kalır.
/// </summary>
public class GrafikPaneliStaTests
{
    private const string Sql =
        "SELECT v.Sehir, v.Yil, v.Tutar FROM (VALUES (N'İstanbul', 2026, 485), (N'İstanbul', 2025, 420), "
        + "(N'Ankara', 2026, 335), (N'Ankara', 2025, 310), (N'İzmir', 2025, 265)) v(Sehir, Yil, Tutar);";

    internal static IEnumerable<T> Bul<T>(DependencyObject kok) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(kok); i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(kok, i);
            if (c is T t)
                yield return t;
            foreach (T alt in Bul<T>(c))
                yield return alt;
        }
    }

    internal static (MainWindow Pencere, MainViewModel Vm, SorguSekmesiViewModel Sekme) Kur(Dispatcher d, string tempData)
    {
        (MainWindow win, MainViewModel vm) = d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcikVS.xaml");
            StaOrtak.Birlestir("Tema.xaml");
            var services = new ServiceCollection();
            SQLST.App.App.HizmetleriKur(services);
            services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
            MainWindow w = services.BuildServiceProvider().GetRequiredService<MainWindow>();
            w.Width = 1300; w.Height = 820;
            w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            return (w, (MainViewModel)w.DataContext);
        });
        d.Invoke(() => win.ProfilUygula(new ConnectionProfile
        {
            Ad = "Local", Sunucu = @"(localdb)\MSSQLLocalDB", Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
        }));
        d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1500)));
        SorguSekmesiViewModel sekme = d.Invoke(() => vm.SekmeAcVeCalistir("grafik", Sql, "master"));
        d.Invoke(() => StaOrtak.PumpUntil(() => sekme.SonucSetleri.Count == 1, TimeSpan.FromSeconds(20)));
        d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(400)));
        return (win, vm, sekme);
    }

    [Fact]
    public void Panel_yalniz_dugmeyle_acilir_dolu_gelir_pivotlar_pasta_png_yeniden_kosuda_kalir()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "sqlst-grafik-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        (MainWindow win, MainViewModel vm, SorguSekmesiViewModel sekme) = Kur(d, tempData);
        try
        {
            d.Invoke(() =>
            {
                SonucSetiGorunumu set = sekme.SonucSetleri[0];
                GrafikPaneli panel = Bul<GrafikPaneli>(win).First(p => ReferenceEquals(p.DataContext, set));

                // K8 — sorgu koştu, panel KAPALI
                Assert.False(set.GrafikAcik);
                Assert.Null(set.Grafik);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);

                // 📊 düğmesi → açık + otomatik tahminle DOLU (X=Sehir, Y=Yil+Tutar, Toplam, çubuk)
                Button dugme = Bul<Button>(win).First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "BtnGrafik"
                                                           && ReferenceEquals(b.DataContext, set));
                dugme.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                Assert.True(set.GrafikAcik);
                Assert.Equal(Visibility.Visible, panel.Visibility);
                GrafikPaneliViewModel g = set.Grafik!;
                Assert.Equal("Sehir", g.XKolon);
                Assert.Equal(GrafikTuru.Cubuk, g.Tur);
                Assert.Equal(GrafikToplama.Toplam, g.Toplama.Deger);
                Assert.Equal(["Yil", "Tutar"], g.YSecimleri.Where(y => y.Secili).Select(y => y.Ad));
                Assert.Equal(["İstanbul", "Ankara", "İzmir"], g.Model!.Kategoriler);
                Assert.True(panel.Tuval.ActualWidth > 100, "tuval yerleşmedi");

                // Seri kolonu = Yil → 2026/2025 serileri, Yil çipi kapalı
                g.YSecimleri.First(y => y.Ad == "Yil").Secili = false;
                g.SeriKolon = "Yil";
                Assert.Equal(["2026", "2025"], g.Model!.Seriler.Select(s => s.Ad));
                Assert.Equal([485d, 335d, null], g.Model.Seriler[0].Degerler);
                Assert.Equal(["2026", "2025"], g.Aciklama.Select(a => a.Ad));

                // Tür çipi → pasta (tek seri uyarısı + açıklama kategorilere döner)
                RadioButton pasta = Bul<RadioButton>(panel).First(r => System.Windows.Automation.AutomationProperties.GetAutomationId(r) == "CipPasta");
                pasta.IsChecked = true;
                Assert.Equal(GrafikTuru.Pasta, g.Tur);
                Assert.Contains("tek seri", g.Uyari);
                Assert.Equal(["İstanbul", "Ankara", "İzmir"], g.Aciklama.Select(a => a.Ad));

                // PNG → pano. Pano paylaşımlı kaynak: başka süreç tutuyorsa CLIPBRD_E_CANT_OPEN (ortam) —
                // ürün "Pano meşgul" der; test birkaç kez dener (önceki süitlerde de aynı dalgalanma görüldü).
                Button pngKopyala = Bul<Button>(panel).First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "BtnGrafikPngKopyala");
                bool resim = false;
                for (int i = 0; i < 10 && !resim; i++)
                {
                    try { Clipboard.Clear(); } catch (System.Runtime.InteropServices.COMException) { }
                    pngKopyala.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    try { resim = g.Durum.StartsWith("PNG panoya kopyalandı") && Clipboard.ContainsImage(); }
                    catch (System.Runtime.InteropServices.COMException) { }
                    if (!resim) Thread.Sleep(200);
                }
                Assert.True(resim, $"pano görüntü içermiyor — durum: '{g.Durum}'");
            });

            // Yeniden çalıştır → yeni set, panel AÇIK ve seçimler yerinde (pasta, seri=Yil)
            SonucSetiGorunumu eskiSet = sekme.SonucSetleri[0];
            d.Invoke(() => _ = sekme.CalistirAsync());
            d.Invoke(() => StaOrtak.PumpUntil(() => sekme.SonucSetleri.Count == 1 && !ReferenceEquals(sekme.SonucSetleri[0], eskiSet), TimeSpan.FromSeconds(20)));
            d.Invoke(() =>
            {
                SonucSetiGorunumu yeni = sekme.SonucSetleri[0];
                Assert.True(yeni.GrafikAcik);
                Assert.Equal(GrafikTuru.Pasta, yeni.Grafik!.Tur);
                Assert.Equal("Yil", yeni.Grafik.SeriKolon);
                Assert.Equal(["Tutar"], yeni.Grafik.YSecimleri.Where(y => y.Secili).Select(y => y.Ad));

                // ✕ kapat → yeni koşuda da kapalı (K8)
                yeni.GrafikKapat();
                Assert.False(yeni.GrafikAcik);
            });
            SonucSetiGorunumu kapaliSet = sekme.SonucSetleri[0];
            d.Invoke(() => _ = sekme.CalistirAsync());
            d.Invoke(() => StaOrtak.PumpUntil(() => sekme.SonucSetleri.Count == 1 && !ReferenceEquals(sekme.SonucSetleri[0], kapaliSet), TimeSpan.FromSeconds(20)));
            d.Invoke(() => Assert.False(sekme.SonucSetleri[0].GrafikAcik));
        }
        finally
        {
            d.Invoke(() => win.Close());
        }
    }

    /// <summary>S2: vuruş → ipucu metni; tık → grid'de satırlar; açıklama → seri gizli (renk kalıcı); yığılmış; rapora gömme.</summary>
    [Fact]
    public void S2_ipucu_tik_grid_secimi_seri_gizle_yigilmis_rapora_gomme()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "sqlst-grafik2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        (MainWindow win, MainViewModel vm, SorguSekmesiViewModel sekme) = Kur(d, tempData);
        try
        {
            d.Invoke(() =>
            {
                vm.SonucYuksekligi = new GridLength(600); // tuval gerçek boyut alsın
                SonucSetiGorunumu set = sekme.SonucSetleri[0];
                set.GrafikAc();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
                GrafikPaneliViewModel g = set.Grafik!;
                g.YSecimleri.First(y => y.Ad == "Yil").Secili = false;
                g.SeriKolon = "Yil"; // X=Sehir, seri=Yil, Y=Tutar, Toplam
                GrafikPaneli panel = MainWindow.GrafikPaneliBul(win, set)!;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                GrafikTuvali tuval = panel.Tuval;
                Assert.True(tuval.ActualHeight > 100, "tuval yerleşmedi");

                // vuruşlar: 3 kategori × 2 seri − İzmir 2026 NULL = 5 çubuk
                Assert.Equal(5, tuval.Vuruslar.Count);
                GrafikVurusu ankara2025 = tuval.Vuruslar.Single(v => v.Kategori == 1 && v.Seri == 1);
                Assert.Equal(310d, ankara2025.Deger);
                var orta = new Point(ankara2025.Alan.X + ankara2025.Alan.Width / 2, ankara2025.Alan.Y + ankara2025.Alan.Height / 2);
                Assert.Same(ankara2025, tuval.Vur(orta));
                Assert.Equal("Ankara · 2025: 310", tuval.IpucuMetniUret(ankara2025));
                Assert.Null(tuval.Vur(new Point(1, 1)));

                // tık → grid'de o satır seçili (Ankara, 2025)
                int secilen = panel.KategoriSec(ankara2025.Kategori, ankara2025.Seri);
                DataGrid grid = Bul<DataGrid>(win).First(x => System.Windows.Automation.AutomationProperties.GetAutomationId(x) == "GridSonuc");
                Assert.Equal(1, secilen);
                var satir = (System.Data.DataRowView)Assert.Single(grid.SelectedItems.Cast<object>());
                Assert.Equal("Ankara", satir["Sehir"]);
                Assert.Equal(2025, satir["Yil"]);
                Assert.Contains("1 satır grid'de seçildi", g.Durum);

                // açıklama: 2026'yı gizle → tek seri kalır, 2025'in rengi (1) KAYMAZ, açıklama 2026'yı soluk listeler
                g.AciklamaDegistir("2026");
                Assert.Single(g.Model!.Seriler);
                Assert.Equal("2025", g.Model.Seriler[0].Ad);
                Assert.Equal(1, g.Model.Seriler[0].Renk);
                Assert.True(g.Aciklama.Single(a => a.Ad == "2026").Gizli);
                Assert.Contains("1 gizli", g.Ozet);
                g.AciklamaDegistir("2026");
                Assert.Equal(2, g.Model!.Seriler.Count);

                // yığılmış: çip görünür, eksen üst sınırı 905 (İstanbul 485+420), ayar taşınır
                Assert.True(g.YigilmisSecilebilir);
                g.Yigilmis = true;
                Assert.True(g.Model!.Yigilmis);
                Assert.Equal(905d, g.Model.DegerAraligi().EnBuyuk);
                Assert.True(g.Ayar.Yigilmis);
                StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
                Assert.Equal(5, tuval.Vuruslar.Count);
                g.Tur = GrafikTuru.Cizgi;
                Assert.False(g.YigilmisSecilebilir);
                g.Tur = GrafikTuru.Cubuk;

                // rapora gömme: panel görüntüsü base64 → HTML'de img
                var bitmap = panel.GoruntuAl()!;
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var ms = new MemoryStream();
                enc.Save(ms);
                string html = HtmlRaporYazici.Yaz("srv", "master", Sql, "0 sn", [(set.Baslik, set.Tablo)], DateTime.Now,
                    new Dictionary<int, string> { [0] = Convert.ToBase64String(ms.ToArray()) });
                Assert.Contains("<img class=\"grafik\"", html);
                Assert.Contains("data:image/png;base64,iVBOR", html); // PNG imzası
            });
        }
        finally
        {
            d.Invoke(() => win.Close());
        }
    }
}
