using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLST.App.ViewModels;
using SQLST.App.Views;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// Sunum dokümanı için GERÇEK WPF pencerelerini (aynı XAML + tema) sentetik veriyle PNG'ye render eder.
/// Normal test koşusunda ATLANIR (dosya üretir, doğrulama değil) — elle çağrılır:
///   dotnet test --filter "FullyQualifiedName~EkranGoruntuUretici" -e SQLST_SS=1
/// Çıktı: docs/sunum/gorseller/*.png. Kanıt amaçlı iki pencereyle başlar; genişletilir.
/// </summary>
public class EkranGoruntuUretici
{
    private static bool Etkin => Environment.GetEnvironmentVariable("SQLST_SS") == "1";

    private static string GorselKlasoru()
    {
        // tests/SQLST.App.Tests/bin/.../ → repo köküne çık
        string dizin = AppContext.BaseDirectory;
        DirectoryInfo? d = new(dizin);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SQLST.slnx")))
            d = d.Parent;
        string hedef = Path.Combine(d?.FullName ?? dizin, "docs", "sunum", "gorseller");
        Directory.CreateDirectory(hedef);
        return hedef;
    }

    private static void Kaydet(Window w, string ad)
    {
        w.UpdateLayout();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
        int gen = (int)Math.Ceiling(w.ActualWidth), yuk = (int)Math.Ceiling(w.ActualHeight);
        if (gen <= 0 || yuk <= 0)
            return;
        var rtb = new RenderTargetBitmap(gen, yuk, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(Path.Combine(GorselKlasoru(), ad + ".png"));
        enc.Save(fs);
    }

    private static Dictionary<string, object?> S(params (string, object?)[] a)
        => a.ToDictionary(x => x.Item1, x => x.Item2);

    /// <summary>Görsel ağaçta ilk SONUÇ grid'ini (ItemsSource'u DataView olan DataGrid) bulur.</summary>
    private static System.Windows.Controls.DataGrid? SonucGridiBul(DependencyObject kok)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(kok); i++)
        {
            DependencyObject cocuk = VisualTreeHelper.GetChild(kok, i);
            if (cocuk is System.Windows.Controls.DataGrid dg && dg.ItemsSource is System.Data.DataView)
                return dg;
            if (SonucGridiBul(cocuk) is { } bulunan)
                return bulunan;
        }

        return null;
    }

    /// <summary>
    /// v20-S3 "Log Analizi 2.0" DOLU görsel doğrulaması (izole; LocalDB gerektirmez): sahte log verisi
    /// + gerçek-sayım geçişi ile pencereyi doldurur, "_log-analizi-dolu.png" üretir. Seviye rozetleri,
    /// İlk/Son görülme, "YENİ", seviye çipleri, süz kutusu ve GERÇEK toplam sayıları görünür.
    /// </summary>
    [Fact]
    public void Log_analizi2_gorsel()
    {
        if (!Etkin)
            return;

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var kolonlar = new[]
            {
                new SemaKolonu("Id", "bigint", false, true),
                new SemaKolonu("TimeStamp", "datetime2", false, false),
                new SemaKolonu("Level", "nvarchar(16)", true, false),
                new SemaKolonu("Message", "nvarchar(max)", true, false),
            };
            var logTablo = new SemaNesnesi("db", "dbo", "Logs", SemaNesneTuru.Tablo, kolonlar, []);

            DateTime now = DateTime.Now;
            var kesifSatir = new List<object?[]>();
            void Grup(string sablon, string seviye, int adet, double enEskiSaat)
            {
                for (int i = 0; i < adet; i++)
                {
                    double saatOnce = enEskiSaat * (1.0 - (double)i / Math.Max(1, adet - 1));
                    kesifSatir.Add([$"Tablo [{(char)('A' + i)}] {sablon}, id {1000 + i}", now.AddHours(-saatOnce), seviye]);
                }
            }
            Grup("bulunamadı", "Error", 12, 2.0);                                // en yeni → YENİ
            Grup("Object reference not set on OrderService", "Fatal", 8, 30.0);  // eski → YENİ değil
            Grup("Yavaş sorgu tespit edildi", "Warning", 6, 26.0);
            Grup("Bağlantı zaman aşımı", "Error", 5, 3.0);                       // YENİ
            Grup("Kullanıcı giriş yaptı", "Information", 10, 20.0);              // YENİ

            Task<QueryResult> SahteCalistir(string db, string sql, System.Threading.CancellationToken ct)
            {
                if (sql.Contains("SUM(CASE WHEN", StringComparison.Ordinal))     // gerçek-sayım geçişi
                {
                    int n = System.Text.RegularExpressions.Regex.Matches(sql, @"AS c\d+").Count;
                    var kol = Enumerable.Range(0, n).Select(i => new KolonBilgisi($"c{i}", "int", typeof(int))).ToList();
                    object?[] satir = [.. Enumerable.Range(0, n).Select(i => (object?)Math.Max(23, 1840 - i * 430))];
                    return Task.FromResult(new QueryResult
                    {
                        Basarili = true, ToplamSatir = 1,
                        ResultSetler = [new ResultSetData { Kolonlar = kol, Satirlar = [satir] }],
                    });
                }
                var kesifKol = new List<KolonBilgisi>
                {
                    new("Message", "nvarchar", typeof(string)),
                    new("TimeStamp", "datetime2", typeof(DateTime)),
                    new("Level", "nvarchar", typeof(string)),
                };
                return Task.FromResult(new QueryResult
                {
                    Basarili = true, ToplamSatir = kesifSatir.Count,
                    ResultSetler = [new ResultSetData { Kolonlar = kesifKol, Satirlar = kesifSatir }],
                });
            }

            var w = new LogAnalizPenceresi(
                MotorTuru.Mssql, ["QmsDb"], "QmsDb",
                _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>([logTablo]),
                SahteCalistir)
            {
                WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 1200, Height = 780,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(500));   // Loaded → tablo/kolon/seviye otomatik seçimi
            ((System.Windows.Controls.Button)w.FindName("AnalizDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(900));   // keşif + gerçek-sayım geçişi tamamlansın
            Kaydet(w, "log-analiz");                          // sunum ekran görüntüsü (dolu, yeni)
            w.Close();
        });

        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "log-analiz.png")));
    }

    /// <summary>v20-S8 REST İstemcisi DOLU görsel doğrulaması (izole; ağ yok): sahte 200 JSON yanıtıyla
    /// pencereyi doldurup "rest-istemci.png" üretir — URL {{baseUrl}}, Bearer token, renkli JSON yanıt,
    /// durum bandı, yanıt eylemleri (JSON Olarak Aç · URL'i Kopyala · Kopyalanmış URL'i Aç · AI Yorumla).
    /// SQLST_SS=1 ister.</summary>
    [Fact]
    public void Rest_istemci_gorsel()
    {
        if (!Etkin)
            return;

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new RestIstemciPenceresi(
                new SahteRest(),
                depo: null,
                aiYorumla: (_, _, _) => Task.FromResult<AsistanCevabi>(null!)) // düğme görünsün diye non-null
            {
                WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 1200, Height = 780,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((System.Windows.Controls.TextBox)w.FindName("UrlKutusu")).Text = "{{baseUrl}}/api/firmalar?il=Ankara&sayfa=1";
            ((System.Windows.Controls.ComboBox)w.FindName("KimlikTuru")).SelectedItem = "Bearer";
            ((System.Windows.Controls.TextBox)w.FindName("ParolaKutusu")).Text = "{{token}}";
            StaOrtak.Pump(TimeSpan.FromMilliseconds(150));
            ((System.Windows.Controls.Button)w.FindName("GonderDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(500));
            Kaydet(w, "rest-istemci");
            w.Close();
        });

        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "rest-istemci.png")));
    }

    /// <summary>v20-S15 KOR teması (kırmızı-sarı-siyah) görsel doğrulaması — koyu + açık varyantı gerçek
    /// REST penceresinde render eder. Sunum klasörüne DEĞİL, %TEMP%'e yazar (elle inceleme). SQLST_SS=1 ister.</summary>
    [Fact]
    public void Kor_tema_gorsel()
    {
        if (!Etkin)
            return;

        string koyuYol = Path.Combine(Path.GetTempPath(), "kor-koyu.png");
        string acikYol = Path.Combine(Path.GetTempPath(), "kor-acik.png");

        RestIstemciPenceresi Pencere() => new(
            new SahteRest(), depo: null, aiYorumla: (_, _, _) => Task.FromResult<AsistanCevabi>(null!))
        {
            WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, Width = 1180, Height = 760,
        };
        void Doldur(Window w)
        {
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(350));
            ((System.Windows.Controls.TextBox)w.FindName("UrlKutusu")).Text = "https://api/firmalar?il=Ankara";
            ((System.Windows.Controls.Button)w.FindName("GonderDugmesi")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(450));
        }

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("Tema.xaml");
            StaOrtak.Birlestir("PaletKoyuKor.xaml"); // KOYU aktif
            var wk = Pencere();
            Doldur(wk);
            KaydetYol(wk, koyuYol);
            wk.Close();

            StaOrtak.Birlestir("PaletAcikKor.xaml"); // sonradan eklenir → anahtarlar açığı ezer
            var wa = Pencere();
            Doldur(wa);
            KaydetYol(wa, acikYol);
            wa.Close();
        });

        Assert.True(File.Exists(koyuYol));
        Assert.True(File.Exists(acikYol));
    }

    /// <summary>v20-S21 saha m.4 görsel doğrulaması: KOYU temada Log Analizi sekme etiketleri
    /// ("Veritabanı:/Koleksiyon:/Mesaj alanı:/Ek alanlar:") artık okunmalı (Foreground fırçası eklendi).
    /// %TEMP%'e yazar (elle inceleme); SQLST_SS=1 ister. Bağlantı gerekmez — sekme sahte VM ile enjekte edilir.</summary>
    [Fact]
    public void M4_loganaliz_koyu_etiketler_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m4-loganaliz-koyu.png");
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-m4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        try
        {
            StaOrtak.Sta().Invoke(() =>
            {
                StaOrtak.Birlestir("PaletKoyu.xaml");
                StaOrtak.Birlestir("Tema.xaml");

                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1280; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

                var vm = (MainViewModel)w.DataContext;
                var sekme = new LogAnalizSekmesiViewModel(
                    MotorTuru.Mongo, ["MersisServices"], "MersisServices",
                    _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>([]),
                    (_, _, _) => Task.FromResult(new QueryResult { Basarili = true }));
                sekme.Tablolar.Add("MersisServices.ExceptionLog");
                sekme.SecilenTablo = "MersisServices.ExceptionLog"; // _tablolar boş → kademe erken döner
                sekme.Kolonlar.Add("StackTrace");
                sekme.SecilenKolon = "StackTrace";
                // v22-S12: seviye kaldırıldı; ek kolon adayı ekle ki "Ek alanlar" düğmesi dolu görünsün.
                sekme.EkKolonOgeleri.Add(new LogEkKolonOgesi("ExceptionMessage", () => { }));
                vm.Sekmeler.Add(sekme);
                vm.SeciliSekme = sekme;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(500));
                KaydetYol(w, yol);
                w.Close();
            });
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { /* geçici */ }
        }

        Assert.True(File.Exists(yol));
    }

    /// <summary>v22-S12 görsel doğrulaması (kullanıcı 2026-09-17): Log Analizi'nde (a) Seviye
    /// combo/çip/grid kolonu YOK, (b) "Ek kolonlar" seçicisiyle koşulan analizde seçilen kolon
    /// (ExceptionMessage) grid'de örnek mesajın yanında AYRI KOLON olarak değerleriyle görünür.
    /// Piksel kuralı: iddia RenderTargetBitmap çıktısından okunur, ActualHeight'a bakılmaz.
    /// %TEMP%\v22s7-log-ek-kolon.png; SQLST_SS=1 ister.</summary>
    [Fact]
    public void V22S12_log_ek_kolonlar_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "v22s7-log-ek-kolon.png");
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-v22s7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        try
        {
            StaOrtak.Sta().Invoke(() =>
            {
                StaOrtak.Birlestir("PaletAcik.xaml");
                StaOrtak.Birlestir("Tema.xaml");

                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1280; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

                var sonuc = new QueryResult
                {
                    Basarili = true,
                    ToplamSatir = 3,
                    ResultSetler =
                    [
                        new ResultSetData
                        {
                            Kolonlar =
                            [
                                new KolonBilgisi("StackTrace", "nvarchar", typeof(string)),
                                new KolonBilgisi("ExceptionMessage", "nvarchar", typeof(string)),
                            ],
                            Satirlar =
                            [
                                new object?[] { "at OrderService.Submit() line 42", "Sunucu yanıt vermedi (timeout)" },
                                new object?[] { "at OrderService.Submit() line 42", "Sunucu yanıt vermedi (timeout)" },
                                new object?[] { "at Billing.Charge() line 7", "Kart reddedildi" },
                            ],
                        },
                    ],
                };

                var vm = (MainViewModel)w.DataContext;
                var sekme = new LogAnalizSekmesiViewModel(
                    MotorTuru.Mssql, ["MersisServices"], "MersisServices",
                    _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>([]),
                    (_, _, _) => Task.FromResult(sonuc));
                sekme.Tablolar.Add("dbo.ExceptionLog");
                sekme.SecilenTablo = "dbo.ExceptionLog"; // _tablolar boş → kademe erken döner
                sekme.Kolonlar.Add("StackTrace");
                sekme.SecilenKolon = "StackTrace";
                sekme.EkKolonOgeleri.Add(new LogEkKolonOgesi("ExceptionMessage", () => { }) { Secili = true });
                vm.Sekmeler.Add(sekme);
                vm.SeciliSekme = sekme;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(400)); // grid Loaded → köprü abone

                sekme.AnalizCommand.Execute(null); // sahte sonuç senkron döner
                StaOrtak.Pump(TimeSpan.FromMilliseconds(500));
                KaydetYol(w, yol);
                w.Close();
            });
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { /* geçici */ }
        }

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 saha m.5 görsel doğrulaması: "Tüm Zamanlar" tarih kartı (temalı DatePicker +
    /// hızlı aralık düğmeleri) koyu temada. %TEMP%'e yazar; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M5_tarih_alani_koyu_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m5-tarih-koyu.png");
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-m5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        try
        {
            StaOrtak.Sta().Invoke(() =>
            {
                StaOrtak.Birlestir("PaletKoyu.xaml");
                StaOrtak.Birlestir("Tema.xaml");

                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1280; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

                var vm = (MainViewModel)w.DataContext;
                var sekme = new LogAnalizSekmesiViewModel(
                    MotorTuru.Mssql, ["MersisLog"], "MersisLog",
                    _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>([]),
                    (_, _, _) => Task.FromResult(new QueryResult { Basarili = true }));
                sekme.Tablolar.Add("dbo.ExceptionLog");
                sekme.SecilenTablo = "dbo.ExceptionLog";
                sekme.Kolonlar.Add("Message");
                sekme.SecilenKolon = "Message";
                sekme.HizliAralikCommand.Execute("7"); // dolu tarihlerle görünsün
                vm.Sekmeler.Add(sekme);
                vm.SeciliSekme = sekme;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

                // İç TabControl'ü bulup "Tüm Zamanlar" sekmesine geç
                System.Windows.Controls.TabControl? tablar = Bul<System.Windows.Controls.TabControl>(w,
                    tc => tc.Items.Count == 2 && tc.Items[1] is System.Windows.Controls.TabItem
                    { Header: "Tüm Zamanlar Log Analizi" });
                Assert.NotNull(tablar);
                tablar!.SelectedIndex = 1;
                StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

                KaydetYol(w, yol);
                w.Close();
            });
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { /* geçici */ }
        }

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 saha m.6 görsel doğrulaması: AI giriş kutusu ÇOK SATIRLA büyürken yanındaki
    /// "AI Sor"/iptal butonları ve onay kutusu BÜYÜMEMELİ (ortada sabit). %TEMP%'e yazar; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M6_ai_giris_buyume_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m6-ai-giris-koyu.png");
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-m6-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        try
        {
            StaOrtak.Sta().Invoke(() =>
            {
                StaOrtak.Birlestir("PaletKoyu.xaml");
                StaOrtak.Birlestir("Tema.xaml");

                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1280; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

                var vm = (MainViewModel)w.DataContext;
                vm.AsistanAc().GetAwaiter().GetResult(); // profilsiz de açılır
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                if (vm.Sekmeler.OfType<AsistanSekmesiViewModel>().FirstOrDefault() is { } ai)
                    ai.Soru = "SELECT k.Ad, s.Tutar\nFROM Kisi k\nJOIN Siparis s ON s.KisiId = k.Id\n"
                        + "WHERE s.Tutar > 1000\nORDER BY s.Tutar DESC\n-- bu sorgu neden yavaş?";
                StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

                KaydetYol(w, yol);
                w.Close();
            });
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { /* geçici */ }
        }

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 saha m.8 görsel doğrulaması: LINQ⇄SQL penceresinde çevir düğmeleri artık KENDİ
    /// panellerinin üstünde (karışıklık bitti) + kullanıcının terkin sorgusu SQL→LINQ çevrilmiş halde.
    /// %TEMP%'e yazar; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M8_linqsql_yerlesim_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m8-linqsql-koyu.png");
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new LinqSqlPenceresi(
                () => Task.FromResult<SQLST.App.ViewModels.LinqBaglam?>(null),
                (_, _) => Task.CompletedTask)
            {
                WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 1400, Height = 720,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((ICSharpCode.AvalonEdit.TextEditor)w.FindName("SqlEditor")).Text = """
                select t.Id as TalepId, u.TamUnvan, '' as KararListesi
                from Mersis.Talep t with(nolock)
                inner join Mersis.Unvan u with(nolock) on t.FirmaId = u.FirmaId and u.TescilDurumuId in (2,3,5) and u.BitisTalepId is null
                where t.TalepTuruId IN (1056, 47, 6) and t.TalepNo = @TalepNo
                """;
            ((System.Windows.Controls.Button)w.FindName("TersCevirDugmesi")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 saha m.11/12/13 görsel doğrulaması: gerçek ana pencerede sekme şeridi —
    /// ✕ her sekmenin SAĞ KENARINDA (m.11), sabit sekmede ✕ yerine 📌 (m.13), uzun başlık kırpılır.
    /// %TEMP%'e yazar; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M11_13_sekme_seridi_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m11-13-sekmeler-koyu.png");
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-ss1113-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        try
        {
            (MainWindow win, MainViewModel vm) = d.Invoke(() =>
            {
                // Kullanıcının GÜNLÜK teması Kor koyu (2026-08-15) — ana pencere görselleri onda
                // doğrulanır; editör/panel zemin kontrastı da burada göze çarpar.
                StaOrtak.Birlestir("PaletKoyuKor.xaml");
                StaOrtak.Birlestir("Tema.xaml");
                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1200; w.Height = 700;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                return (w, (MainViewModel)w.DataContext);
            });

            d.Invoke(() => win.ProfilUygula(LocalDbProfil()));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1200)));
            d.Invoke(() =>
            {
                SQLST.App.ViewModels.SorguSekmesiViewModel sabit =
                    vm.SekmeAc("rapor.sql", "SELECT 1", "tempdb");
                sabit.Sabit = true; // 📌 sabit — ✕ gizlenmeli
                vm.SekmeAc("cok-uzun-dosya-adi-deneme-kirpilma.sql", "SELECT 2", "tempdb");
                vm.SekmeAc("kisa.sql", "SELECT 3", "tempdb");
                StaOrtak.Pump(TimeSpan.FromMilliseconds(600));
            });
            d.Invoke(() => KaydetYol(win, yol));
            d.Invoke(win.Close);
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { }
        }

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 saha m.22 devamı görsel doğrulaması: LINQ ⇄ SQL artık PENCERE değil SEKME —
    /// pencere içeriği AracSekmesiViewModel ile sekmede barınır; ikinci açış kopya sekme AÇMAZ.
    /// %TEMP%'e yazar; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M22_arac_sekmesi_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m22-linqsql-sekme.png");
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-ss22-{Guid.NewGuid():N}");
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
                w.Width = 1280; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                return (w, (MainViewModel)w.DataContext);
            });

            d.Invoke(() => win.ProfilUygula(LocalDbProfil()));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1200)));
            d.Invoke(() =>
            {
                vm.AracSekmesiAcVeyaSec("🔁 LINQ ⇄ SQL", () => new LinqSqlPenceresi(
                    () => vm.LinqBaglamiAsync(), (sql, db) => vm.LinqSqlSekmedeAcAsync(sql, db)));
                int sayi = vm.Sekmeler.Count;
                // İkinci açış: fabrika ÇAĞRILMAMALI, mevcut sekme seçilmeli.
                vm.AracSekmesiAcVeyaSec("🔁 LINQ ⇄ SQL",
                    () => throw new InvalidOperationException("kopya araç sekmesi açılmamalı"));
                Assert.Equal(sayi, vm.Sekmeler.Count);
                StaOrtak.Pump(TimeSpan.FromMilliseconds(800)); // içerik Loaded + bağlam yüklemesi
            });
            d.Invoke(() => KaydetYol(win, yol));
            d.Invoke(win.Close);
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { }
        }

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 saha m.7 «Kül Beyazı» görsel doğrulaması: AÇIK KOR'da iskelet (şerit+ray)
    /// artık açık kül — siyah blok kopukluğu bitti; kırmızı/sarı kimlik yerinde. %TEMP%; SQLST_SS=1.</summary>
    [Fact]
    public void M7_kor_acik_ana_pencere_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m7-kor-acik-ana.png");
        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-ss7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        try
        {
            (MainWindow win, MainViewModel vm) = d.Invoke(() =>
            {
                StaOrtak.Birlestir("PaletAcikKor.xaml");
                StaOrtak.Birlestir("Tema.xaml");
                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1280; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                return (w, (MainViewModel)w.DataContext);
            });

            d.Invoke(() => win.ProfilUygula(LocalDbProfil()));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1200)));
            // v22-S1 m.8: RAY AÇIK olmalı — "SQLST" başlığı ve ☰ ikonu yalnız ray genişken görünür;
            // hata (beyaz yazı açık kül zeminde) tam orada yaşıyordu, kapalı rayda görünmüyordu.
            d.Invoke(() => vm.RayAcKapaCommand.Execute(null));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(300)));
            d.Invoke(() => vm.SekmeAcVeCalistir("KorAcik",
                "SELECT 1 AS Id, N'Ali Veli' AS Ad, N'İzmir' AS Sehir", "tempdb"));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1500)));
            d.Invoke(() => KaydetYol(win, yol));
            d.Invoke(win.Close);
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { }
        }

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 m.10 fikir 5 görsel doğrulaması: kolon istatistiği penceresi (Kor koyu) —
    /// etiket/değer hizası, NULL satırı ve TOP notu. %TEMP%; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M10_kolon_istatistigi_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m10-kolon-istatistik.png");
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyuKor.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new KolonIstatistikPenceresi("Tutar", () => Task.FromResult<(IReadOnlyList<(string, string)>?, string?, string?)>(
                ([("satır", "128"), ("farklı", "97"), ("en küçük", "99,00"), ("en büyük", "8.400,50"),
                  ("ortalama", "2.956,05"), ("toplam", "378.374,40"), ("NULL", "6 / 128")],
                 "Sorguda TOP var — istatistik TOP'suz, eşleşen TÜM satırlar üzerinden hesaplandı.", null)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(600)); // Loaded → veri dolsun
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>v22-S3 saha turu-3 m.1 görsel doğrulaması: 👁 tablo önizleme PENCERESİ (sağ tık yolu) —
    /// kolon özeti + ilk 5 satır mini tablosu. %TEMP%; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M1_tablo_onizleme_penceresi_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m1-tablo-onizleme.png");
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyuKor.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            SemaKolonu K(string ad, string tip, bool pk = false, bool fk = false)
                => new(ad, tip, true, pk, fk);
            var nesne = new SemaNesnesi("Mersis", "dbo", "Talep", SemaNesneTuru.Tablo,
                [K("Id", "int", pk: true), K("FirmaId", "int", fk: true), K("TsmId", "int", fk: true),
                 K("TalepTuruId", "int", fk: true), K("BasvuruTarihi", "datetime"), K("Aciklama", "nvarchar(400)")],
                []);

            var veri = new ResultSetData
            {
                Kolonlar =
                [
                    new KolonBilgisi("Id", "int", typeof(int)),
                    new KolonBilgisi("FirmaId", "int", typeof(int)),
                    new KolonBilgisi("TsmId", "int", typeof(int)),
                    new KolonBilgisi("BasvuruTarihi", "datetime", typeof(DateTime)),
                    new KolonBilgisi("Aciklama", "nvarchar", typeof(string)),
                ],
                Satirlar =
                [
                    [248788, 2234711, 443, new DateTime(2002, 10, 24), "Birleşme başvurusu"],
                    [248789, 2234711, 443, new DateTime(2001, 7, 13), "Devir onayı bekliyor"],
                    [248790, 2234711, 443, new DateTime(2001, 7, 13), "Tescil tamamlandı"],
                    [248791, 254958, 303, new DateTime(1998, 9, 21), null],
                    [248792, 254958, 303, new DateTime(2008, 6, 18), "Unvan değişikliği"],
                ],
            };

            var w = new TabloOnizlemePenceresi(nesne, _ => Task.FromResult<(ResultSetData?, string?)>((veri, null)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(600)); // Loaded → satırlar dolsun
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 m.26 fikir 1 görsel doğrulaması: ObjectId izi penceresi (Kor koyu) — sahte
    /// sayımlarla tarama akışı, eşleşen satırlar ve özet. %TEMP%; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M26_objectid_izi_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m26-objectid-izi.png");
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyuKor.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            IReadOnlyList<IzAdayi> adaylar =
            [
                new("kullanicilar", "_id"), new("oturumlar", "kullaniciId"),
                new("islemLog", "actorId"), new("bildirimler", "hedefKullanici"),
                new("raporlar", "olusturanId"),
            ];
            var sayilar = new Dictionary<string, long> // sahte sayımlar (sorgu metnine göre)
            {
                ["kullanicilar"] = 1, ["oturumlar"] = 184, ["islemLog"] = 2417, ["bildirimler"] = 39,
            };

            var w = new MongoIdIziPenceresi("507f1f77bcf86cd799439011",
                _ => Task.FromResult<IReadOnlyList<IzAdayi>?>(adaylar),
                (sorgu, _) => Task.FromResult<long?>(
                    sayilar.FirstOrDefault(p => sorgu.Contains($"\"{p.Key}\"")).Value is var n && n > 0 ? n : null),
                (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(900)); // tarama akışı bitsin
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>v20-S21 m.26 fikir 7 görsel doğrulaması: $lookup sihirbazı (Kor koyu) — seçimler
    /// solda, pipeline sağda CANLI. %TEMP%; SQLST_SS=1 ister.</summary>
    [Fact]
    public void M26_lookup_sihirbazi_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m26-lookup-sihirbazi.png");
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyuKor.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            SemaNesnesi Kol(string ad, params string[] alanlar) => new("Db", "Db", ad, SemaNesneTuru.Koleksiyon,
                [.. alanlar.Select(a => new SemaKolonu(a, "objectId", false, a == "_id"))], []);

            var w = new MongoLookupPenceresi(
                [Kol("oturumlar", "_id", "kullaniciId", "giris"), Kol("kullanicilar", "_id", "ad", "eposta")],
                (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(500));
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>Görsel ağaçta koşulu sağlayan ilk T ögesini bulur (m.5 sekme geçişi).</summary>
    private static T? Bul<T>(DependencyObject kok, Func<T, bool> sart) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(kok); i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(kok, i);
            if (c is T t && sart(t))
                return t;
            if (Bul(c, sart) is { } alt)
                return alt;
        }
        return null;
    }

    /// <summary>RenderTargetBitmap'i verilen TAM yola PNG olarak kaydeder (sunum klasörü dışı; Kor doğrulaması).</summary>
    private static void KaydetYol(Window w, string tamYol)
    {
        w.UpdateLayout();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
        int gen = (int)Math.Ceiling(w.ActualWidth), yuk = (int)Math.Ceiling(w.ActualHeight);
        if (gen <= 0 || yuk <= 0)
            return;
        var rtb = new RenderTargetBitmap(gen, yuk, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(tamYol);
        enc.Save(fs);
    }

    /// <summary>v20-S10 Kolon bağları görsel doğrulaması (izole; ağ yok): sahte FK grafıyla hem ▲ giden
    /// hem ▼ gelen bağları olan bir kolon penceresi — "kolon-baglari.png". SQLST_SS=1 ister.</summary>
    [Fact]
    public void Kolon_baglari_gorsel()
    {
        if (!Etkin)
            return;

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            IReadOnlyList<KolonBaglari.Bag> baglar = KolonBaglari.Bul("dbo", "Siparis", "Id",
            [
                new YabanciAnahtar("dbo", "Siparis", ["Id"], "dbo", "SiparisArsiv", ["SiparisId"], "FK_Siparis_Arsiv"),
                new YabanciAnahtar("dbo", "SiparisKalem", ["SiparisId"], "dbo", "Siparis", ["Id"], "FK_Kalem_Siparis"),
                new YabanciAnahtar("dbo", "Fatura", ["SiparisId"], "dbo", "Siparis", ["Id"], "FK_Fatura_Siparis"),
                new YabanciAnahtar("sevk", "Irsaliye", ["SiparisId"], "dbo", "Siparis", ["Id"], null),
            ]);
            var w = new KolonBaglariPenceresi("dbo.Siparis.Id", baglar)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
            Kaydet(w, "kolon-baglari");
            w.Close();
        });

        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "kolon-baglari.png")));
    }

    /// <summary>v20-S17 Ctrl+F eşleşme vurgusu (kullanıcı bulgusu: koyu temada okunmuyordu):
    /// koyu temada amber vurgunun metni okunur bıraktığı kanıtlanır — "_arama-vurgu.png".</summary>
    [Fact]
    public void Arama_vurgusu_koyu_temada_okunur()
    {
        if (!Etkin)
            return;

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var editor = new ICSharpCode.AvalonEdit.TextEditor
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 13,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1a)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xD6)),
                Text = "SELECT k.Ad + N' ' + k.SoyAd AS KisiAdSoyad,\n       mk.Ad AS Marka,\n       md.Ad AS Model\nFROM kisi.Kisiler k\nJOIN ortak.Markalar mk ON mk.Ad = N'Audi'",
            };
            editor.SyntaxHighlighting = SQLST.App.EditorTema.Tanim(koyu: true);
            var panel = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(editor);
            panel.SetResourceReference(
                ICSharpCode.AvalonEdit.Search.SearchPanel.MarkerBrushProperty, "AramaVurguFircasi");

            var w = new Window
            {
                Content = editor, Width = 720, Height = 260,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1a, 0x2e)),
            };
            w.Show();
            panel.SearchPattern = "Ad";
            panel.Open();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            Kaydet(w, "_arama-vurgu");
            w.Close();
        });

        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "_arama-vurgu.png")));
    }

    /// <summary>v20-S11 LINQ→SQL görsel doğrulaması (izole; ağ yok): sahte şema+FK bağlamıyla örnek
    /// LINQ çevrilir — "linq-sql.png". SQLST_SS=1 ister.</summary>
    [Fact]
    public void Linq_sql_gorsel()
    {
        if (!Etkin)
            return;

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var baglam = new SQLST.App.ViewModels.LinqBaglam(
                [new SemaNesnesi("Dukkan", "dbo", "Musteri", SemaNesneTuru.Tablo,
                    [new SemaKolonu("Id", "int", false, true), new SemaKolonu("Ad", "nvarchar(80)", true, false),
                     new SemaKolonu("Sehir", "nvarchar(40)", true, false)], []),
                 new SemaNesnesi("Dukkan", "dbo", "Siparis", SemaNesneTuru.Tablo,
                    [new SemaKolonu("Id", "int", false, true), new SemaKolonu("MusteriId", "int", false, false),
                     new SemaKolonu("Tutar", "decimal(18,2)", true, false)], [])],
                [new YabanciAnahtar("dbo", "Siparis", ["MusteriId"], "dbo", "Musteri", ["Id"], "FK_Siparis_Musteri")],
                new MssqlLehcesi(new DpapiSecretProtector()), "Dukkan", MotorTuru.Mssql);

            var w = new LinqSqlPenceresi(
                () => Task.FromResult<SQLST.App.ViewModels.LinqBaglam?>(baglam),
                (_, _) => Task.CompletedTask)
            {
                WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 1160, Height = 700,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((System.Windows.Controls.Button)w.FindName("CevirDugmesi")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            Kaydet(w, "linq-sql");
            w.Close();
        });

        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "linq-sql.png")));
    }

    /// <summary>Görsel test için sahte REST istemcisi: gerçek 200 JSON yanıtı taklit eder (ağ yok).</summary>
    private sealed class SahteRest : Infrastructure.RestIstemcisi
    {
        public override Task<RestCevap> GonderAsync(RestIstek istek, System.Threading.CancellationToken ct)
            => Task.FromResult(new RestCevap(200, "OK",
                [new RestSatir("Content-Type", "application/json; charset=utf-8"),
                 new RestSatir("Server", "Kestrel"), new RestSatir("X-Sure", "142ms")],
                """{"toplam":128,"sayfa":1,"firmalar":[{"id":42,"unvan":"LST Yazılım A.Ş.","il":"Ankara","aktif":true},{"id":57,"unvan":"Anadolu Tic. Ltd.","il":"Ankara","aktif":true},{"id":61,"unvan":"Başkent İnşaat","il":"Ankara","aktif":false}]}""",
                TimeSpan.FromMilliseconds(142), 3100, "application/json; charset=utf-8", null));
    }

    [Fact]
    public void Uret_kanit()
    {
        if (!Etkin)
            return; // yalnız SQLST_SS=1 ile üret

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            // 1) BF-3 Fark önizleme
            var fark = new TabloFarki(
                Yeniler: [new FarkSatiri(S(("Id", 104), ("Ad", "Veli Kaya"), ("Sehir", "Ankara")))],
                Degisenler: [new DegisenSatir(S(("Id", 102)),
                    new Dictionary<string, (object?, object?)> { ["Sehir"] = ("İstanbul", "Bursa") },
                    S(("Id", 102), ("Ad", "Ayşe Yılmaz"), ("Sehir", "Bursa")))],
                Silinenler: [new FarkSatiri(S(("Id", 103), ("Ad", "Can Demir"), ("Sehir", "Adana")))]);
            var lehce = new MssqlLehcesi(new DpapiSecretProtector());
            var w1 = new FarkOnizlemePenceresi(fark, lehce, "dbo", "Musteri", ["Id"], _ => { })
            { Width = 820, Height = 560, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            w1.Show(); StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
            Kaydet(w1, "bf3-fark-onizleme"); w1.Close();

            // 2) BF-7 Neden yavaş?
            var eksik = new PlanEksikIndexi(78, "[dbo].[Siparis]", ["MusteriId"], ["Tarih"], ["Tutar"]);
            var kok = new PlanDugumu("Clustered Index Scan", "[dbo].[Siparis]", 62, 1200, 48000,
                ["Type conversion in expression (CONVERT_IMPLICIT) may affect cardinality"], []);
            var plan = new SorguPlani(true, [new IfadePlani("SELECT ...", 3.42, kok, [eksik])]);
            YavaslikRaporu rapor = YavaslikCozumleyici.Coz(plan);
            var w2 = new NedenYavasPenceresi(rapor, "Sekme 1 · Siparisler", _ => { })
            { Width = 780, Height = 560, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            w2.Show(); StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
            Kaydet(w2, "bf7-neden-yavas"); w2.Close();

            // 3) Kısayol kılavuzu (F1)
            var w3 = new KisayolPenceresi
            { Width = 640, Height = 620, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            w3.Show(); StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
            Kaydet(w3, "kisayollar"); w3.Close();

            // 4) Yeni tablo (CREATE) onayı — İçe aktarımda DDL önizleme
            var w4 = new DdlOnayPenceresi(
                "CREATE TABLE [dbo].[YeniSatislar] (\n"
                + "    [Id] int NULL,\n    [Ad] nvarchar(50) NULL,\n    [Tutar] decimal(18,4) NULL,\n"
                + "    [Tarih] datetime2 NULL\n);")
            { Width = 640, Height = 460, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            w4.Show(); StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
            Kaydet(w4, "yeni-tablo-onay"); w4.Close();

            // 5) Hücre görüntüleyici (JSON/uzun metin) — çift tık
            var w5 = new HucrePenceresi(
                "{\n  \"musteriId\": 2954858,\n  \"ad\": \"Ayşe Yılmaz\",\n  \"siparisler\": [\n"
                + "    { \"no\": 1001, \"tutar\": 1250.75 },\n    { \"no\": 1002, \"tutar\": 980.00 }\n  ]\n}")
            { Width = 620, Height = 460, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            w5.Show(); StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
            Kaydet(w5, "hucre-goruntuleyici"); w5.Close();

            // 6) Tablo oluşturma sihirbazı
            var w6 = new TabloOlusturPenceresi(lehce, "dbo", (_, _) => { })
            { Width = 720, Height = 560, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            w6.Show(); StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
            Kaydet(w6, "tablo-olusturucu"); w6.Close();
        });

        foreach (string ad in new[] { "bf3-fark-onizleme", "bf7-neden-yavas", "kisayollar",
            "yeni-tablo-onay", "hucre-goruntuleyici", "tablo-olusturucu" })
            Assert.True(File.Exists(Path.Combine(GorselKlasoru(), ad + ".png")), ad);
    }

    /// <summary>0 satırlık SELECT'in görsel doğrulaması (kullanıcı bulgusu 2026-07-30 "0 satırda
    /// grid görünmüyor"): gerçek ana pencerede boş sonucun grid başlıkları + gridin üstündeki
    /// "N satır · süre" etiketiyle göründüğünü kanıtlar. Normal koşuda atlanır (SQLST_SS=1 ister).</summary>
    [Fact]
    public void Uret_sifir_satir()
    {
        if (!Etkin)
            return;

        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-ss0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        try
        {
            (MainWindow win, MainViewModel vm) = d.Invoke(() =>
            {
                StaOrtak.Birlestir("PaletAcik.xaml");
                StaOrtak.Birlestir("Tema.xaml");
                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1100; w.Height = 760;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                return (w, (MainViewModel)w.DataContext);
            });

            d.Invoke(() => win.ProfilUygula(LocalDbProfil()));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1200)));
            d.Invoke(() => vm.SekmeAcVeCalistir("SifirSatir",
                "SELECT 1 AS Id, N'x' AS Ad, GETDATE() AS Tarih WHERE 1 = 0", "tempdb"));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1500)));
            d.Invoke(() => Kaydet(win, "_dogrulama-sifir-satir"));
            d.Invoke(win.Close);
        }
        finally
        {
            try { Directory.Delete(tempData, true); } catch { }
        }

        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "_dogrulama-sifir-satir.png")));
    }

    /// <summary>Ctrl+F arama kutusu hiza bekçisi (kullanıcı bulgusu 2026-07-30, 3 kez bildirildi):
    /// paneli koyu temada birebir kurup yazılan metnin kutuda TAM göründüğünü (kırpılmadığını)
    /// hem ölçümle (viewport ≥ extent) hem render'la doğrular. SQLST_SS=1 ister.</summary>
    [Fact]
    public void Uret_arama_kutusu()
    {
        if (!Etkin)
            return;

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml"); // kullanıcının teması: KOYU
            StaOrtak.Birlestir("Tema.xaml");

            var editor = new ICSharpCode.AvalonEdit.TextEditor
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 13,
                Text = "SELECT 1;",
            };
            var w = new Window
            {
                Content = editor, Width = 900, Height = 260,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
                Background = Brushes.Black,
            };
            w.Show();
            var panel = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(editor.TextArea);
            panel.Open();
            panel.SearchPattern = "asdasd";
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            // Metin kutuda TAM sığmalı (kırpılma regresyonu 2026-07-30: Padding'in çifte uygulanması
            // 17.3px'lik satırı 12px'e sıkıştırıyordu — GirisKutusuSablonu düzeltmesinin bekçisi).
            System.Windows.Controls.TextBox? tb = null;
            void Gez(DependencyObject k)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(k); i++)
                {
                    DependencyObject c = VisualTreeHelper.GetChild(k, i);
                    if (c is System.Windows.Controls.TextBox t && t.Name == "PART_searchTextBox") { tb = t; return; }
                    Gez(c);
                    if (tb is not null) return;
                }
            }
            Gez(w);
            var sv = tb?.Template.FindName("PART_ContentHost", tb) as System.Windows.Controls.ScrollViewer;
            Assert.NotNull(sv);
            Assert.True(sv!.ViewportHeight >= sv.ExtentHeight - 0.01,
                $"Arama kutusunda metin kırpılıyor: viewport={sv.ViewportHeight} < extent={sv.ExtentHeight}");

            Kaydet(w, "_dogrulama-arama-kutusu");
            w.Close();
        });

        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "_dogrulama-arama-kutusu.png")));
    }

    private static ConnectionProfile LocalDbProfil() => new()
    {
        Ad = "Örnek Bağlantı", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
    };

    /// <summary>
    /// GERÇEK ana pencereyi (DI'dan kurulu, tam XAML + tema) LocalDB tempdb'ye bağlayıp örnek sorguyla
    /// doldurur ve PNG'ye render eder: ana pencere + tüm özellikler. SQLite deposu GEÇİCİ klasöre yönlendirilir
    /// → kullanıcının gerçek verisine (sqlst.db) DOKUNMAZ. Async, dispatcher'da deadlock olmadan adım adım sürülür.
    /// </summary>
    [Fact]
    public void Uret_ana_pencere()
    {
        if (!Etkin)
            return;

        const string tablo = "SunumMusteri";
        var executor = new SqlExecutor(new LehceSaglayici(new DpapiSecretProtector()));
        var tempdb = new ExecuteOptions { VeritabaniOverride = "tempdb" };
        executor.ExecuteAsync(LocalDbProfil(), $"""
            IF OBJECT_ID('tempdb.dbo.{tablo}') IS NOT NULL DROP TABLE tempdb.dbo.{tablo};
            CREATE TABLE tempdb.dbo.{tablo} (
                Id INT PRIMARY KEY, AdSoyad NVARCHAR(50), Telefon VARCHAR(15),
                Email NVARCHAR(60), TCKimlikNo CHAR(11), Sehir NVARCHAR(30), Bakiye DECIMAL(18,2));
            INSERT INTO tempdb.dbo.{tablo} VALUES
                (1, N'Ahmet Yılmaz',  '05321234590', N'ahmet.yilmaz@site.com',  '12345678901', N'İstanbul', 1250.75),
                (2, N'Ayşe Demir',    '05339876543', N'ayse.demir@site.com',    '23456789012', N'Ankara',    980.00),
                (3, N'Mehmet Kaya',   '05445551122', N'mehmet.kaya@site.com',   '34567890123', N'İzmir',    3410.20),
                (4, N'Fatma Çelik',   '05061234567', N'fatma.celik@site.com',   '45678901234', N'Bursa',     540.50),
                (5, N'Can Öztürk',    '05559998877', N'can.ozturk@site.com',    '56789012345', N'Antalya',  7620.00),
                (6, N'Zeynep Arslan', '05323334455', N'zeynep.arslan@site.com', '67890123456', N'Adana',    1180.90);
            """, tempdb, CancellationToken.None).GetAwaiter().GetResult();

        string tempData = Path.Combine(Path.GetTempPath(), $"sqlst-ss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempData);
        Dispatcher d = StaOrtak.Sta();
        // Best-effort: bazı pencereler odak kaybında kendini kapatır (PaletPenceresi.Deactivated) ve
        // benim Close'umla çakışıp dispatcher'da YAKALANMAYAN hata fırlatır — tüm run'ı düşürmesin.
        DispatcherUnhandledExceptionEventHandler yutucu = (_, e) => e.Handled = true;
        d.Invoke(() => Dispatcher.CurrentDispatcher.UnhandledException += yutucu);

        try
        {
            (MainWindow win, MainViewModel vm, ServiceProvider sp) = d.Invoke(() =>
            {
                StaOrtak.Birlestir("PaletAcik.xaml");
                StaOrtak.Birlestir("Tema.xaml");

                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(tempData, "sqlst.db"))); // son kayıt kazanır → geçici
                ServiceProvider prov = services.BuildServiceProvider();

                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.Width = 1320; w.Height = 820;
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                return (w, (MainViewModel)w.DataContext, prov);
            });

            // Bağlan — gerçek akış: ProfilUygula bağlam çipini doldurur + şemayı yükler (fire-and-forget).
            d.Invoke(() => win.ProfilUygula(LocalDbProfil()));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1800)));
            d.Invoke(() => vm.SekmeAcVeCalistir("Müşteriler", $"SELECT * FROM tempdb.dbo.{tablo}", "tempdb"));
            d.Invoke(() => StaOrtak.Pump(TimeSpan.FromMilliseconds(1500)));

            d.Invoke(() => Kaydet(win, "ana-pencere"));

            // Seçim istatistiği (SUM/AVG/MIN/MAX) — hücreleri seç, durum çubuğu dolsun.
            // (Maske kaldırma sonrası bu yolun bozulmadığını görsel olarak doğrular.)
            d.Invoke(() =>
            {
                if (SonucGridiBul(win) is { } sg)
                {
                    sg.SelectAllCells();
                    StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
                    Kaydet(win, "secim-istatistik");
                    sg.UnselectAllCells();
                }
            });

            // Kolon araçları (kullanıcı isteği): geniş + çoğu NULL satırda "boş kolonları gizle" →
            // yalnız dolu kolonlar (Id/TcKimlikNo/Adi/Soyadi) kalmalı.
            d.Invoke(() =>
            {
                vm.SekmeAcVeCalistir("KolonTest",
                    "SELECT * FROM (VALUES (1, NULL, NULL, NULL, N'12345678901', NULL, NULL, N'EROLCAN', N'DEMİR', NULL, NULL, NULL))"
                    + " AS t(Id, EskiId, Aktif, UyrukId, TcKimlikNo, PasaportNo, VergiNo, Adi, Soyadi, CinsiyetId, KisiNufusId, KaydedenId)",
                    "tempdb");
                StaOrtak.Pump(TimeSpan.FromMilliseconds(900));
                Kaydet(win, "_dogrulama-tum-kolon"); // önce: tüm kolonlar (boşlar dahil) görünür

                // "Boş kolonları gizle" düğmesinin yaptığını uygula: tümü boş kolonları gizle.
                if (SonucGridiBul(win) is { } grid && grid.DataContext is SonucSetiGorunumu set)
                {
                    var bos = new HashSet<string>(GridKolonAraclari.BosKolonlar(set.Tablo));
                    foreach (System.Windows.Controls.DataGridColumn k in grid.Columns)
                        if (k.Header is string ad && bos.Contains(ad))
                            k.Visibility = Visibility.Collapsed;
                    StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                    Kaydet(win, "_kolon-arac-sonuc"); // sonra: yalnız dolu kolonlar
                }
            });

            // ---- Yardımcılar: sekme (async/sync) ve ayrı pencere render — her biri best-effort (try/catch) ----
            void AsyncSekme(string ad, Func<Task> ac, int beklemeMs = 900)
            {
                try
                {
                    d.InvokeAsync(async () => await ac()).Task.Unwrap().GetAwaiter().GetResult();
                    d.Invoke(() => { StaOrtak.Pump(TimeSpan.FromMilliseconds(beklemeMs)); Kaydet(win, ad); });
                }
                catch (Exception ex) { File.AppendAllText(Path.Combine(GorselKlasoru(), "_hatalar.txt"), $"{ad}: {ex.GetType().Name}: {ex.Message}" + System.Environment.NewLine); }
            }
            void SyncSekme(string ad, Action ac, int beklemeMs = 800)
            {
                try { d.Invoke(() => { ac(); StaOrtak.Pump(TimeSpan.FromMilliseconds(beklemeMs)); Kaydet(win, ad); }); }
                catch (Exception ex) { File.AppendAllText(Path.Combine(GorselKlasoru(), "_hatalar.txt"), $"{ad}: {ex.GetType().Name}: {ex.Message}" + System.Environment.NewLine); }
            }
            void Pencere(string ad, int gen, int yuk, Func<Window> kur, int beklemeMs = 600)
            {
                try
                {
                    d.Invoke(() =>
                    {
                        Window w = kur();
                        w.Width = gen; w.Height = yuk; w.Owner = null;
                        w.WindowState = WindowState.Normal; // v19-S21: XAML Maximized'ı sunum render'ı için geç
                        w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                        w.Show(); StaOrtak.Pump(TimeSpan.FromMilliseconds(beklemeMs)); Kaydet(w, ad); w.Close();
                    });
                }
                catch (Exception ex) { File.AppendAllText(Path.Combine(GorselKlasoru(), "_hatalar.txt"), $"{ad}: {ex.GetType().Name}: {ex.Message}" + System.Environment.NewLine); }
            }

            // ---- Sekme tabanlı özellikler (bağlı ana pencere) ----
            AsyncSekme("plan", () => vm.PlanAcAsync());                          // execution plan
            AsyncSekme("yonetim-paneli", () => vm.TeshisAcAsync());              // teşhis merkezi
            AsyncSekme("ai-asistan", () => vm.AsistanAc());                      // 🤖 AI asistan sekmesi
            AsyncSekme("_ai-dusunuyor", async () =>                              // "düşünüyor" balonu (kullanıcı isteği 2026-07-28)
            {
                await vm.AsistanAc();
                if (vm.Sekmeler.OfType<AsistanSekmesiViewModel>().FirstOrDefault() is { } ai)
                {
                    ai.Mesajlar.Add(new AsistanSekmesiViewModel.AsistanMesaji(
                        KullaniciMi: true, "Union ve Union all arasındaki fark nedir"));
                    ai.Mesajlar.Add(new AsistanSekmesiViewModel.AsistanMesaji(
                        KullaniciMi: false, "", Dusunuyor: true));
                }
            });
            AsyncSekme("edit-modu", () => vm.DuzenlemeAcAsync(new SemaNesnesi(
                "tempdb", "dbo", tablo, SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "int", false, true), new SemaKolonu("AdSoyad", "nvarchar(50)", true, false),
                 new SemaKolonu("Telefon", "varchar(15)", true, false), new SemaKolonu("Sehir", "nvarchar(30)", true, false),
                 new SemaKolonu("Bakiye", "decimal(18,2)", true, false)], [])));
            SyncSekme("gorsel-sorgu", () => vm.GorselSorguAc());                 // 🎨 görsel sorgu tasarımcısı
            SyncSekme("harita", () => vm.HaritaAcCommand.Execute(null), 1200);   // 🗺 veritabanı haritası
            SyncSekme("gecmis-panel", () => vm.GecmisGorunur = true);            // 🕘 sorgu geçmişi paneli

            // ---- Ayrı pencere özellikleri (DI'dan kurulu) ----
            var profil = LocalDbProfil();
            Pencere("soap", 900, 680, () => new SoapIstemciPenceresi(
                sp.GetRequiredService<SoapIstemcisi>(), sp.GetRequiredService<ISoapDeposu>(),
                (i, y, f) => vm.AsistanSoapYorumlaAsync(i, y, f), sp.GetRequiredService<ISecretProtector>()));
            Pencere("_soap-xml", 1000, 700, () =>                         // XML vurgusu görsel doğrulaması (madde 3)
            {
                var w = new SoapIstemciPenceresi(
                    sp.GetRequiredService<SoapIstemcisi>(), sp.GetRequiredService<ISoapDeposu>(),
                    (i, y, f) => vm.AsistanSoapYorumlaAsync(i, y, f), sp.GetRequiredService<ISecretProtector>());
                const string ornek =
                    "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">\n"
                  + "  <soap:Body>\n    <GetFirma xmlns=\"http://tempuri.org/\">\n"
                  + "      <sicilNo>140068-0</sicilNo>\n      <!-- örnek yorum -->\n"
                  + "    </GetFirma>\n  </soap:Body>\n</soap:Envelope>";
                void Ata(string ad) => ((ICSharpCode.AvalonEdit.TextEditor)typeof(SoapIstemciPenceresi)
                    .GetField(ad, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(w)!).Text = ornek;
                Ata("Zarf"); Ata("Yanit");
                return w;
            });
            Pencere("paket-aktarim", 900, 640, () => new AktarimPenceresi(
                profil, vm.LogAnalizVeritabanlari, vm.LogAnalizAktifVeritabani, vm.LogAnalizTablolariAsync,
                sp.GetRequiredService<IProfileStore>(), sp.GetRequiredService<ISchemaService>(),
                sp.GetRequiredService<ILehceSaglayici>(), sp.GetRequiredService<AktarimServisi>()));
            Pencere("ice-aktar", 900, 660, () => new DosyaImportPenceresi(
                profil, vm.LogAnalizVeritabanlari, vm.LogAnalizAktifVeritabani, vm.LogAnalizTablolariAsync,
                sp.GetRequiredService<ILehceSaglayici>(), sp.GetRequiredService<DosyaAktarimServisi>(),
                new FarkOkumaServisi(sp.GetRequiredService<ILehceSaglayici>()), (_, _, _) => { }));
            // NOT: "log-analiz.png" artık DOLU (gerçek-sayım geçişli) haliyle ayrı izole testte üretilir:
            // Log_analizi2_gorsel (LocalDB gerektirmez). Buradaki boş render kaldırıldı ki bayat boş görüntü
            // dolu olanı ezmesin.
            Pencere("karsilastirma", 940, 660, () => new KarsilastirmaPenceresi(vm, profil));
            Pencere("palet", 620, 440, () => new PaletPenceresi(vm.PaletOgeleriKur()));
            Pencere("geri-al", 820, 560, () => new GeriAlPenceresi(sp.GetRequiredService<IGeriAlDeposu>(), (_, _, _) => { }));
            Pencere("baglanti-ekrani", 900, 640, () => new BaglantiPenceresi(
                sp.GetRequiredService<BaglantiEkraniViewModel>(), () => win));
            Pencere("nesne-tarihcesi", 820, 600, () => new TarihcePenceresi(
                "dbo.SP_SiparisHesapla",
                [new TarihceKaydi { Id = 3, Sunucu = "(localdb)", Veritabani = "tempdb", Sema = "dbo",
                    Ad = "SP_SiparisHesapla", IcerikHash = "a1", Kaynak = "alter-öncesi",
                    Tanim = "ALTER PROCEDURE dbo.SP_SiparisHesapla AS\nBEGIN\n  SELECT SUM(Tutar) FROM Siparis;\nEND" },
                 new TarihceKaydi { Id = 2, Sunucu = "(localdb)", Veritabani = "tempdb", Sema = "dbo",
                    Ad = "SP_SiparisHesapla", IcerikHash = "b2", Kaynak = "okuma",
                    Tanim = "ALTER PROCEDURE dbo.SP_SiparisHesapla AS\nBEGIN\n  SELECT COUNT(*) FROM Siparis;\nEND" }],
                (_, _) => { }));
            Pencere("mongo-bul", 720, 560, () => new MongoBulPenceresi((_, _) => { }));

            // 🪄 SP Sihirbazı (v19 bayrak özelliği) — adım 2 (Eşle): ad → tablo.kolon + JOIN yolu.
            try
            {
                d.Invoke(() =>
                {
                    static SemaNesnesi Tbl(string ad, params SemaKolonu[] k) => new("db", "dbo", ad, SemaNesneTuru.Tablo, k, []);
                    var sema = new SemaOnbellegi
                    {
                        YuklenmeZamaniUtc = DateTime.UtcNow, YabanciAnahtarlar = [],
                        Nesneler =
                        [
                            Tbl("Kisi", new("Id","int",false,true), new("TcKimlikNo","decimal(18,0)",true,false), new("Ad","nvarchar(70)",true,false)),
                            Tbl("Firma", new("Id","int",false,true), new("Unvan","nvarchar(200)",true,false)),
                            Tbl("FirmaKisi", new("Id","int",false,true), new("KisiId","int",true,false), new("FirmaId","int",true,false)),
                        ],
                    };
                    var w = new SpSihirbaziPenceresi(["tempdb"], "tempdb",
                        _ => Task.FromResult<SemaOnbellegi?>(sema), (_, _, _) => { })
                    {
                        WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000, Top = -32000, Width = 1180, Height = 720,
                    };
                    w.Show();
                    StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
                    ((System.Windows.Controls.TextBox)w.FindName("Girisler")).Text = "TcKimlikNo";
                    ((System.Windows.Controls.TextBox)w.FindName("Cikislar")).Text = "Ad, Unvan";
                    ((System.Windows.Controls.Button)w.FindName("IleriDugmesi")).RaiseEvent(
                        new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    StaOrtak.Pump(TimeSpan.FromMilliseconds(600));
                    Kaydet(w, "sp-sihirbazi");
                    w.Close();
                });
            }
            catch (Exception ex) { File.AppendAllText(Path.Combine(GorselKlasoru(), "_hatalar.txt"), $"sp-sihirbazi: {ex.GetType().Name}: {ex.Message}" + System.Environment.NewLine); }

            d.Invoke(win.Close);
        }
        finally
        {
            try { d.Invoke(() => Dispatcher.CurrentDispatcher.UnhandledException -= yutucu); } catch { }
            executor.ExecuteAsync(LocalDbProfil(),
                $"IF OBJECT_ID('tempdb.dbo.{tablo}') IS NOT NULL DROP TABLE tempdb.dbo.{tablo};",
                tempdb, CancellationToken.None).GetAwaiter().GetResult();
            try { Directory.Delete(tempData, true); } catch { /* geçici */ }
        }

        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "ana-pencere.png")));
        Assert.True(File.Exists(Path.Combine(GorselKlasoru(), "secim-istatistik.png")));
    }

    /// <summary>
    /// v22-S3 saha turu-3 m.2 (kullanıcı: "denetim izi gridini sağa doğru kaydırabileyim"):
    /// Şablon TEST İÇİNDE YENİDEN YAZILMAZ — gerçek <c>MainWindow.xaml</c>'den okunup XamlReader
    /// ile ayrıştırılır (yalnız olay bağlamaları sökülür; düzeni etkilemez). Böylece kolon
    /// genişlikleri gerçek dosyanın genişlikleridir: biri yıldıza (<c>*</c>) dönerse grid taşmaz,
    /// yatay çubuk çıkmaz ve test kırmızı yanar. %TEMP%; SQLST_SS=1 ister.
    /// </summary>
    [Fact]
    public void M2_denetim_izi_yatay_kaydirma_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m2-denetim-izi.png");
        bool yatayCubuk = false;

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var vm = new DenetimIziViewModel(new SahteDenetimGecmisi());
            vm.YukleAsync(new ConnectionProfile { Ad = "t", Sunucu = ".", Motor = MotorTuru.Mssql })
              .GetAwaiter().GetResult();

            var w = new Window
            {
                Width = 1000, Height = 300,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
                Background = System.Windows.Media.Brushes.White,
                Content = new System.Windows.Controls.ContentControl
                {
                    Content = vm,
                    ContentTemplate = DenetimSablonu(),
                },
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(600));
            yatayCubuk = YatayCubukGorunurMu(w);
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
        Assert.True(yatayCubuk,
            "Denetim gridinde yatay kaydırma çubuğu YOK — SQL kolonu yıldız (*) genişlikte olmalı değil, "
            + "yıldız kolon artan yeri yuttuğu için grid asla taşmaz.");
    }

    /// <summary>
    /// m.2'nin ikinci yarısı: denetim izinde çift tıkla açılan pencere — sonuç gridiyle AYNI
    /// <see cref="HucrePenceresi"/>, tam SQL kopyalanabilir, başlıkta hangi kayıt olduğu yazar.
    /// </summary>
    [Fact]
    public void M2_denetim_izi_ciftik_penceresi_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m2-denetim-ciftik.png");
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new HucrePenceresi(
                "SELECT k.Id, k.Ad, k.Kod, t.Tanim\n"
                + "  FROM Aktarim.KonfigurasyonMenuListeParametre k\n"
                + "  JOIN Ortak.KararTuru t ON t.Id = k.KararTuruId\n"
                + " WHERE k.Aktif = 1\n"
                + " ORDER BY k.Ad;")
            {
                Title = "SQLST — 12.08.2026 10:52:33 · sa · Okuma",
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>
    /// v22-S3 saha turu-3 m.3: ObjectId izi penceresinin İLK ANI — envanter daha okunurken ekran
    /// ZATEN açık ve ne beklediğini yazıyor (eskiden bu saniyelerde hiçbir şey görünmüyordu).
    /// %TEMP%; SQLST_SS=1 ister.
    /// </summary>
    [Fact]
    public void M3_objectid_izi_ilk_an_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m3-objectid-izi-ilk-an.png");
        var envanter = new TaskCompletionSource<IReadOnlyList<IzAdayi>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyuKor.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoIdIziPenceresi("507f1f77bcf86cd799439011", _ => envanter.Task,
                (_, _) => Task.FromResult<long?>(0), (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400)); // envanter HÂLÂ uçuşta
            KaydetYol(w, yol);
            envanter.SetResult([]);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>
    /// v22-S3 saha turu-3 m.4: 📋 Shell yapıştır EKRANI (eskiden ekran yoktu). Pano ön-dolu,
    /// metin düzenlenebilir, çeviri anında görünür. %TEMP%; SQLST_SS=1 ister.
    /// </summary>
    [Fact]
    public void M4_shell_yapistir_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m4-shell-yapistir.png");
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyuKor.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            // Compass'ın "Copy" çıktısı biçimi — m.4'te desteklenir hâle geldi
            var w = new MongoShellPenceresi(
                "db.getCollection('oturumlar').find({ kullaniciId: 5, ts: { $gt: ISODate(\"2026-08-01T00:00:00Z\") } })"
                + ".sort({ ts: -1 }).limit(50)",
                _ => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>
    /// v22-S3 saha turu-3 m.5: find yardımcısında koleksiyon seçilince alan envanterinin geldiği
    /// görünür ("3 alan"). Öneri popup'ı ayrı HWND olduğu için ekran görüntüsüne düşmez — öneri
    /// İÇERİĞİ MongoBulAlanOnerisiTests'te doğrulanır. %TEMP%; SQLST_SS=1 ister.
    /// </summary>
    [Fact]
    public void M5_find_yardimcisi_alan_notu_gorsel()
    {
        if (!Etkin)
            return;

        string yol = Path.Combine(Path.GetTempPath(), "m5-find-alan-notu.png");
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyuKor.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoBulPenceresi((_, _) => { }, null,
                () => Task.FromResult<IReadOnlyList<SemaNesnesi>>(
                    [new SemaNesnesi("log", "", "oturumlar", SemaNesneTuru.Koleksiyon, [], [])]),
                _ => Task.FromResult<IReadOnlyList<string>>(["_id", "kullaniciId", "ts"]))
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
            w.KoleksiyonKutusu.Text = "oturumlar";
            w.SortKutusu.Text = "{ \"ts\": -1 }";
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            KaydetYol(w, yol);
            w.Close();
        });

        Assert.True(File.Exists(yol));
    }

    /// <summary>Gerçek MainWindow.xaml'deki denetim izi DataTemplate'ini ayrıştırır (olay bağlamaları sökülü).</summary>
    private static DataTemplate DenetimSablonu()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SQLST.slnx")))
            d = d.Parent;
        string xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "SQLST.App", "MainWindow.xaml"));

        const string bas = "<DataTemplate DataType=\"{x:Type vm:DenetimIziViewModel}\">";
        int i = xaml.IndexOf(bas, StringComparison.Ordinal);
        Assert.True(i >= 0, "MainWindow.xaml'de denetim izi DataTemplate'i bulunamadı");

        // İç içe DataTemplate'i sayarak kapanışı bul
        int derinlik = 0, j = i;
        while (j < xaml.Length)
        {
            int ac = xaml.IndexOf("<DataTemplate", j, StringComparison.Ordinal);
            int kapa = xaml.IndexOf("</DataTemplate>", j, StringComparison.Ordinal);
            Assert.True(kapa >= 0, "DataTemplate kapanışı bulunamadı");
            if (ac >= 0 && ac < kapa) { derinlik++; j = ac + 13; continue; }
            derinlik--;
            j = kapa + 15;
            if (derinlik == 0)
                break;
        }

        string blok = xaml[i..j];
        // XamlReader olay bağlamalarını çözemez (code-behind yok) — düzeni etkilemedikleri için sökülür
        blok = System.Text.RegularExpressions.Regex.Replace(
            blok, @"\s(?:Click|MouseDoubleClick|SelectionChanged|Loaded)=""[^""]*""", "");
        blok = blok.Replace("<DataTemplate ",
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:vm=\"clr-namespace:SQLST.App.ViewModels;assembly=SQLST.App\" ",
            StringComparison.Ordinal);

        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(blok);
    }

    /// <summary>Görsel ağaçta GÖRÜNÜR bir yatay kaydırma çubuğu var mı.</summary>
    private static bool YatayCubukGorunurMu(DependencyObject kok)
    {
        if (kok is System.Windows.Controls.Primitives.ScrollBar
            { Orientation: System.Windows.Controls.Orientation.Horizontal, Visibility: Visibility.Visible } cubuk
            && cubuk.ActualWidth > 0)
            return true;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(kok); i++)
            if (YatayCubukGorunurMu(VisualTreeHelper.GetChild(kok, i)))
                return true;
        return false;
    }

    /// <summary>m.2 görselleri için sabit denetim kayıtları (kullanıcının ekran görüntüsündeki desen).</summary>
    private sealed class SahteDenetimGecmisi : ISorguGecmisiDeposu
    {
        public Task EkleAsync(GecmisKaydi kayit, int enCok = 1000, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task TemizleAsync(Guid? profilId = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<GecmisKaydi>> AraAsync(
            string? metin, int limit = 200, Guid? profilId = null, CancellationToken ct = default)
        {
            GecmisKaydi K(string sql, int sure, int satir, int gunOnce) => new()
            {
                Sunucu = ".", Veritabani = "MersisV3", Kullanici = "sa", Sql = sql,
                BaslangicUtc = new DateTime(2026, 8, 14, 11, 56, 14, DateTimeKind.Utc).AddDays(-gunOnce),
                SureMs = sure, SatirSayisi = satir, Durum = GecmisDurumu.Basarili,
            };

            IReadOnlyList<GecmisKaydi> liste =
            [
                K("-- ===================================================\n-- Author: <Author,,Name>\n"
                  + "-- Create date: <Create Date,,>\nCREATE PROCEDURE dbo.KararGetir AS BEGIN SET NOCOUNT ON; END", 84, 0, 0),
                K("SELECT * FROM Ortak.KararTuru", 46, 38, 0),
                K("SELECT * FROM Mersis.KonfigurasyonParametre", 210, 28345, 2),
                K("SELECT * FROM Yonetim.Menu WHERE Controller = 'SicilYonetim'", 12, 2, 2),
                K("SELECT * FROM Aktarim.KonfigurasyonMenuListeParametre", 16, 238, 2),
                K("SELECT TOP (100) N'Yonetim.Menu' AS [Tablo], * FROM [Yonetim].[Menu] "
                  + "WHERE [Text] = N'TCKimlikNoIleDogumTarihiKontrol'", 46, 1, 9),
            ];
            return Task.FromResult(liste);
        }
    }
}
