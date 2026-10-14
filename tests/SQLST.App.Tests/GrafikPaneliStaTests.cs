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
/// 📊 Grafik PENCERESİ (v23-S30 — grid yanındaki panelden ayrı pencereye taşındı; kullanıcı 14 Eki). GERÇEK MainWindow +
/// LocalDB: sorgu sonrası grid'de panel YOK; sonuç şeridindeki 📊 ayrı pencere açar — o sonucun verisi + SQL'i dolu,
/// otomatik tahminle grafik gelir; seri kolonu pivotlar; pasta çipi; PNG panoya; ipucu/vuruş → veri tablosunda satır seçimi;
/// pencerede ▶ Çalıştır kendi bağlantısıyla yeniden koşar, seçimler korunur; yazma sorgusu reddedilir (G2); sol rail 📊
/// boş pencere açar, sorgu yazılıp çalıştırılınca grafik gelir; HTML rapor grafiği gömer.
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

    /// <summary>Açık grafik pencereleri (Owner = ana pencere).</summary>
    internal static IReadOnlyList<GrafikPenceresi> Pencereler(MainWindow win) => [.. win.OwnedWindows.OfType<GrafikPenceresi>()];

    private static bool PanoyaResim(Button dugme, GrafikPaneliViewModel g)
    {
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.Clear(); } catch (System.Runtime.InteropServices.COMException) { }
            dugme.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            try { if (g.Durum.StartsWith("PNG panoya kopyalandı") && Clipboard.ContainsImage()) return true; }
            catch (System.Runtime.InteropServices.COMException) { }
            Thread.Sleep(200);
        }
        return false;
    }

    [Fact]
    public void Sonuctan_ayri_pencere_dolu_gelir_pivot_pasta_png_satir_secimi_yeniden_kosu_yazma_engeli()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "sqlst-grafik-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        (MainWindow win, MainViewModel vm, SorguSekmesiViewModel sekme) = Kur(d, tempData);
        try
        {
            GrafikPenceresi p = d.Invoke(() =>
            {
                // G1: ana penceredeki sonuçta grafik paneli YOK
                Assert.Empty(Bul<GrafikPaneli>(win));
                SonucSetiGorunumu set = sekme.SonucSetleri[0];
                Button dugme = Bul<Button>(win).First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "BtnGrafik"
                                                           && ReferenceEquals(b.DataContext, set));
                dugme.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                StaOrtak.Pump(TimeSpan.FromMilliseconds(600));
                GrafikPenceresi pencere = Assert.Single(Pencereler(win));
                pencere.Left = -32000; pencere.Top = -32000;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                Assert.Empty(Bul<GrafikPaneli>(win));                  // ana pencere yine panelsiz
                return pencere;
            });

            d.Invoke(() =>
            {
                var g = (GrafikPenceresiViewModel)p.DataContext;
                Assert.Equal(Sql, g.Sql);                              // SQL dolu
                Assert.Equal("master", g.SeciliDb);
                Assert.Contains(sekme.Baslik, g.PencereBasligi);       // kaynak sekme adı başlıkta
                Assert.Equal(5, g.Tablo!.Rows.Count);                  // veri dolu
                GrafikPaneliViewModel gr = g.Grafik!;
                Assert.Equal("Sehir", gr.XKolon);
                Assert.Equal(["İstanbul", "Ankara", "İzmir"], gr.Model!.Kategoriler);
                GrafikPaneli panel = Bul<GrafikPaneli>(p).Single();
                Assert.True(panel.Tuval.ActualWidth > 300, "tuval yerleşmedi");

                // seri kolonu + pasta
                gr.YSecimleri.First(y => y.Ad == "Yil").Secili = false;
                gr.SeriKolon = "Yil";
                Assert.Equal(["2026", "2025"], gr.Model!.Seriler.Select(s => s.Ad));
                // tık → veri tablosunda satır (Ankara 2025)
                gr.Tur = GrafikTuru.Cubuk;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                GrafikVurusu v = panel.Tuval.Vuruslar.Single(x => x.Kategori == 1 && x.Seri == 1);
                Assert.Equal("Ankara · 2025: 310", panel.Tuval.IpucuMetniUret(v));
                Assert.Equal(1, panel.KategoriSec(v.Kategori, v.Seri));
                DataGrid veri = Bul<DataGrid>(p).Single(x => System.Windows.Automation.AutomationProperties.GetAutomationId(x) == "GrafikVeriGrid");
                var satir = (System.Data.DataRowView)Assert.Single(veri.SelectedItems.Cast<object>());
                Assert.Equal("Ankara", satir["Sehir"]);
                // veri tablosu ham gösterim: ilk hücre metni
                Assert.True(veri.Columns.Count == 3);

                RadioButton pasta = Bul<RadioButton>(panel).First(r => System.Windows.Automation.AutomationProperties.GetAutomationId(r) == "CipPasta");
                pasta.IsChecked = true;
                Assert.Equal(GrafikTuru.Pasta, gr.Tur);
                // PNG panoya: ayrı testte (Pencerede_png_panoya) — pano ortam dalgalanması bu akışı kesmesin
            });

            // pencerede yeniden çalıştır: kendi bağlantısı, seçimler korunur, ana sekme değişmez
            SonucSetiGorunumu anaSet = d.Invoke(() => sekme.SonucSetleri[0]);
            d.Invoke(() =>
            {
                var g = (GrafikPenceresiViewModel)p.DataContext;
                g.Sql = Sql.Replace("(N'İzmir', 2025, 265)", "(N'İzmir', 2025, 265), (N'Bursa', 2026, 210)");
                _ = g.CalistirAsync();
                StaOrtak.PumpUntil(() => !g.Calisiyor && g.Tablo!.Rows.Count == 6, TimeSpan.FromSeconds(20));
                Assert.Equal(6, g.Tablo!.Rows.Count);
                Assert.Equal(GrafikTuru.Pasta, g.Grafik!.Tur);                  // ayar korundu
                Assert.Equal("Yil", g.Grafik.SeriKolon);
                Assert.Contains("6 satır", g.SonucOzeti);
                Assert.Same(anaSet, sekme.SonucSetleri[0]);                      // ana sekmeye dokunulmadı (G3)

                // G2: yazma reddedilir, veri değişmez
                g.Sql = "DELETE FROM dbo.Yok; SELECT 1 AS x;";
                _ = g.CalistirAsync();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
                Assert.StartsWith("⛔", g.Durum);
                Assert.Equal(6, g.Tablo!.Rows.Count);
                p.Close();
            });

            // sol rail → boş pencere → sorgu → grafik
            d.Invoke(() =>
            {
                Button rail = Bul<Button>(win).First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "BtnGrafikRail");
                Assert.Equal(Visibility.Visible, rail.Visibility);
                rail.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                StaOrtak.Pump(TimeSpan.FromMilliseconds(600));
                GrafikPenceresi bos = Assert.Single(Pencereler(win));
                bos.Left = -32000; bos.Top = -32000;
                var g = (GrafikPenceresiViewModel)bos.DataContext;
                Assert.True(g.BosDurum);
                Assert.Null(g.Grafik);
                Assert.Contains("yeni", g.PencereBasligi);
                g.Sql = "SELECT d.Ay, d.Tutar FROM (VALUES (N'Oca', 10), (N'Şub', 25), (N'Mar', 18)) d(Ay, Tutar);";
                _ = g.CalistirAsync();
                StaOrtak.PumpUntil(() => g.Grafik is not null, TimeSpan.FromSeconds(20));
                Assert.False(g.BosDurum);
                Assert.Equal(["Oca", "Şub", "Mar"], g.Grafik!.Model!.Kategoriler);
                Assert.Equal([10d, 25d, 18d], g.Grafik.Model.Seriler[0].Degerler);
                bos.Close();
            });
        }
        finally
        {
            d.Invoke(() => { foreach (Window w in win.OwnedWindows.Cast<Window>().ToList()) w.Close(); win.Close(); });
        }
    }

    /// <summary>Pencerede ⧉ PNG → pano görüntü içerir (pano paylaşımlı: CLIPBRD_E_CANT_OPEN ortam dalgalanmasında düşebilir —
    /// hafıza notu "SQLST pano testleri dalgalı").</summary>
    [Fact]
    public void Pencerede_png_panoya()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "sqlst-grafikpng-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        (MainWindow win, MainViewModel vm, SorguSekmesiViewModel sekme) = Kur(d, tempData);
        try
        {
            d.Invoke(() =>
            {
                SonucSetiGorunumu set = sekme.SonucSetleri[0];
                vm.GrafikPenceresiAc(sekme.Baslik, Sql, "master", set.Tablo, 0, "");
                StaOrtak.Pump(TimeSpan.FromMilliseconds(600));
                GrafikPenceresi p = Assert.Single(Pencereler(win));
                p.Left = -32000; p.Top = -32000;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                GrafikPaneli panel = Bul<GrafikPaneli>(p).Single();
                GrafikPaneliViewModel gr = ((GrafikPenceresiViewModel)p.DataContext).Grafik!;
                Button png = Bul<Button>(panel).First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "BtnGrafikPngKopyala");
                Assert.True(PanoyaResim(png, gr), $"pano görüntü içermiyor — {gr.Durum}");
                Assert.NotNull(panel.GoruntuAl());
            });
        }
        finally
        {
            d.Invoke(() => { foreach (Window w in win.OwnedWindows.Cast<Window>().ToList()) w.Close(); win.Close(); });
        }
    }

    /// <summary>Pencerenin SQL kutusu ana editörün sade alt kümesiyle kurulur (kullanıcı 14 Eki: "bizim editörle aynı yap"):
    /// T-SQL renklendirme, Ctrl+F arama paneli, 2 harften sonra IntelliSense listesi (SE → SELECT…).</summary>
    [Fact]
    public void Pencere_sql_kutusu_ana_editor_gibi_renk_arama_intellisense()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "sqlst-grafikedit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        (MainWindow win, MainViewModel vm, _) = Kur(d, tempData);
        try
        {
            d.Invoke(() =>
            {
                vm.GrafikPenceresiAc(null, "", "master", null, 0, "");
                StaOrtak.Pump(TimeSpan.FromMilliseconds(600));
                GrafikPenceresi p = Assert.Single(Pencereler(win));
                p.Left = -32000; p.Top = -32000;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                ICSharpCode.AvalonEdit.TextEditor ed = p.Editor;
                Assert.NotNull(ed.SyntaxHighlighting);
                Assert.Contains(ed.TextArea.DefaultInputHandler.NestedInputHandlers,
                    h => h is ICSharpCode.AvalonEdit.Search.SearchInputHandler);

                ed.Focus();
                ed.Text = "S";
                ed.CaretOffset = ed.Text.Length;
                ed.TextArea.PerformTextInput("E");
                StaOrtak.PumpUntil(() => System.Windows.Application.Current.Windows.OfType<ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow>().Any(),
                    TimeSpan.FromSeconds(15));
                var liste = Assert.Single(System.Windows.Application.Current.Windows.OfType<ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow>());
                Assert.Same(p, liste.Owner);
                Assert.Contains(liste.CompletionList.CompletionData, o => o.Text == "SELECT");
                Assert.Equal("SE", ((GrafikPenceresiViewModel)p.DataContext).Sql);
                liste.Close();
            });
        }
        finally
        {
            d.Invoke(() => { foreach (Window w in win.OwnedWindows.Cast<Window>().ToList()) w.Close(); win.Close(); });
        }
    }

    /// <summary>G2 kapısı SAF: SELECT geçer; yazma/DDL/EXEC ve boş reddedilir.</summary>
    [Theory]
    [InlineData("SELECT 1", false)]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT * FROM c", false)]
    [InlineData("UPDATE t SET a = 1", true)]
    [InlineData("DROP TABLE t", true)]
    [InlineData("EXEC dbo.usp_Rapor", true)]
    [InlineData("   ", true)]
    public void Yazma_engeli(string sql, bool engelli)
        => Assert.Equal(engelli, GrafikPenceresiViewModel.YazmaEngeli(sql, MotorTuru.Mssql) is not null);
}
