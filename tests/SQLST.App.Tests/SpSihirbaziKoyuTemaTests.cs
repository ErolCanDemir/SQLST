using System.IO;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 🪄 SP Sihirbazı · KOYU TEMA okunabilirliği (kullanıcı bulgusu 25 Ağu 2026:
/// <i>"SP sihirbazında koyu modda madde başlıkları siyah olduğu için görünmüyor"</i>).
///
/// Bu testin işi RENGİ ÖLÇMEK: pencere koyu palette kurulur ve başlık TextBlock'larının ETKİN
/// <c>Foreground</c>'u okunur. "Foreground vermemişsek pencereden devralır" varsayımı yeterli
/// değildi — kullanıcı sahada siyah gördü; ölçüm varsayımı yener.
/// </summary>
public class SpSihirbaziKoyuTemaTests
{
    private static bool Etkin => Environment.GetEnvironmentVariable("SQLST_SS") == "1"
                                 || Environment.GetEnvironmentVariable("SQLST_STA") == "1";

    /// <summary>
    /// ⚠ MANTIKSAL ağaç, görsel ağaç DEĞİL. Sihirbazın 2. ve 3. adım panelleri açılışta
    /// <c>Visibility="Collapsed"</c>; görsel ağaçta HİÇ bulunmazlar, dolayısıyla görsel taramayla
    /// yapılan ilk denemem "kusur yok" dedi — oysa kullanıcının şikâyet ettiği başlıklar
    /// ("🔗 JOIN yolu", "Ek WHERE koşulları", "Sıralama") tam olarak o adımlarda. Mantıksal ağaç
    /// gizli dalları da içerdiği için hepsini görür.
    /// </summary>
    private static IEnumerable<TextBlock> Metinler(DependencyObject kok)
    {
        foreach (object? c in LogicalTreeHelper.GetChildren(kok))
        {
            if (c is not DependencyObject d)
                continue;
            if (d is TextBlock t)
                yield return t;
            foreach (TextBlock alt in Metinler(d))
                yield return alt;
        }
    }

    /// <summary>
    /// ⚠ YALNIZ koyu palet etkin olsun — sonra eski hâli geri ver.
    ///
    /// <c>StaOrtak.Birlestir</c> EKLEMELİ ve STA dispatcher'ı testler arasında PAYLAŞILIYOR: tam
    /// süitte başka testlerin yüklediği AÇIK paletler de <c>Application.Resources</c>'ta duruyor ve
    /// <c>MetinFircasi</c> yanlış palete çözülebiliyor. İlk hâlde bu testler tek başına YEŞİL, süitte
    /// KIRMIZI veriyordu — yani ürünü değil test kirlenmesini ölçüyorlardı. Ölçtüğünü söylediği şeyi
    /// ölçmeyen test, olmayandan kötüdür.
    /// </summary>
    private static IDisposable YalnizKoyuPalet(string paletDosyasi)
    {
        System.Windows.Application app = System.Windows.Application.Current!;
        var oncekiler = app.Resources.MergedDictionaries.ToList();
        var kaldirilan = oncekiler
            .Where(d => d.Source?.OriginalString.Contains("Palet", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        foreach (ResourceDictionary d in kaldirilan)
            app.Resources.MergedDictionaries.Remove(d);

        StaOrtak.Birlestir(paletDosyasi);
        StaOrtak.Birlestir("Tema.xaml");

        // Doğrulama: ölçüme başlamadan ÖNCE ortamın gerçekten koyu olduğunu kanıtla.
        var metin = (SolidColorBrush)app.FindResource("MetinFircasi");
        double p = (0.299 * metin.Color.R + 0.587 * metin.Color.G + 0.114 * metin.Color.B) / 255.0;
        Assert.True(p > 0.5, $"ortam koyu palete geçmedi — MetinFircasi {metin.Color} (parlaklık {p:F2})");

        return new Geriye(() =>
        {
            foreach (ResourceDictionary d in kaldirilan)
                if (!app.Resources.MergedDictionaries.Contains(d))
                    app.Resources.MergedDictionaries.Add(d);
        });
    }

    private sealed class Geriye(Action is_) : IDisposable
    {
        public void Dispose() => is_();
    }

    /// <summary>
    /// Koyu temada HİÇBİR başlık siyaha yakın olamaz. Eşik: parlaklık &lt; 0.35 → koyu zeminde
    /// (#161a2e) okunmaz. Yalnız "siyah mı" diye bakmıyoruz; #111 gibi bir değer de kusurdur.
    /// </summary>
    [Fact]
    public void Koyu_temada_hicbir_baslik_koyu_renk_degil()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            using IDisposable _palet = YalnizKoyuPalet("PaletKoyu.xaml");

            var w = new SQLST.App.Views.SpSihirbaziPenceresi(
                ["db1"], "db1",
                _ => Task.FromResult<SemaOnbellegi?>(new SemaOnbellegi
                {
                    Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow,
                }),
                (_, _, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.PumpUntil(() => Metinler(w).Count() > 10);

            // Taramanın GERÇEKTEN çalıştığının kanıtı: pencere kurulmasa/ağaç boş kalsa da "kusur
            // bulunamadı" diye YEŞİL verirdi — o yüzden önce anlamlı sayıda metin bulunduğu doğrulanır.
            int metinSayisi = Metinler(w).Count();
            Assert.True(metinSayisi > 20, $"tarama çalışmadı — yalnız {metinSayisi} TextBlock bulundu");

            var kusurlu = new List<string>();
            foreach (TextBlock t in Metinler(w))
            {
                if (string.IsNullOrWhiteSpace(t.Text) || t.Foreground is not SolidColorBrush f)
                    continue;
                Color c = f.Color;
                if (c.A == 0)
                    continue; // görünmez ama bilerek (yer tutucu)
                double parlaklik = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                if (parlaklik < 0.35)
                    kusurlu.Add($"'{t.Text[..Math.Min(40, t.Text.Length)]}' → {c} (parlaklık {parlaklik:F2})");
            }

            w.Close();
            Assert.True(kusurlu.Count == 0,
                "Koyu temada okunmayan metin(ler):\n  " + string.Join("\n  ", kusurlu));
        });
    }

    /// <summary>
    /// 🎯 ASIL KUSUR (kullanıcı ekran görüntüsü 25 Ağu 2026): sihirbaz ayrı pencere DEĞİL, araç
    /// SEKMESİ olarak açılıyor — <c>AracSekmesiViewModel</c> pencerenin içeriğini koparıp sekmeye
    /// taşıyor (<c>pencere.Content = null</c>).
    ///
    /// Sihirbazın <c>Foreground="{DynamicResource MetinFircasi}"</c>'ı PENCERENİN üzerindedir;
    /// içerik koparılınca o kalıtım zinciri kopar ve <c>Foreground</c> verilmemiş metinler WPF
    /// varsayılanına — SİYAHA — düşer. Açık rengi elle yazılmış alt satırlar sağ kalır; bu yüzden
    /// kullanıcı "başlıklar görünmüyor ama altları görünüyor" dedi.
    ///
    /// İlk testim ayrı PENCEREYİ ölçtüğü için YEŞİL vermişti — kusuru göremiyordu. Bu test gerçek
    /// yolu izler: pencereyi kur → sekmeye taşı → rengi ölç.
    /// </summary>
    [Fact]
    public void Sekmeye_tasininca_da_basliklar_okunur_kalir()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            using IDisposable _palet = YalnizKoyuPalet("PaletKoyu.xaml");

            var pencere = new SQLST.App.Views.SpSihirbaziPenceresi(
                ["db1"], "db1",
                _ => Task.FromResult<SemaOnbellegi?>(new SemaOnbellegi
                {
                    Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow,
                }),
                (_, _, _) => { });

            // Ürünün gerçek yolu: içerik pencereden koparılıp SEKME barındırıcısına konur.
            // ⚠ TabControl ŞART: ilk denememde içeriği düz bir ContentControl'e koydum ve test YEŞİL
            // verdi — kusuru göremedi. Gerçekte içerik MainWindow'un TabControl'ünün şablonundaki
            // yuvada barınır ve WPF'in VARSAYILAN TabControl stili Foreground'u SystemColors
            // .ControlTextBrush'a (SİYAH) ayarlar; Tema.xaml yalnız Template'i geçersiz kılmış,
            // Foreground'u değil. O siyah, pencereden gelen açık rengi EZİP içeriğe iner.
            var sekme = new SQLST.App.ViewModels.AracSekmesiViewModel("🪄 SP Sihirbazı", pencere);
            var tab = new TabControl();
            tab.Items.Add(new TabItem { Header = "🪄", Content = sekme.Icerik });
            var host = new Window
            {
                Width = 1080, Height = 720, Content = tab,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            host.Show();
            StaOrtak.PumpUntil(() => Metinler(host).Count() > 10);

            var kusurlu = new List<string>();
            foreach (TextBlock t in Metinler(host))
            {
                if (string.IsNullOrWhiteSpace(t.Text) || t.Foreground is not SolidColorBrush f || f.Color.A == 0)
                    continue;
                Color c = f.Color;
                double parlaklik = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                if (parlaklik < 0.35)
                    kusurlu.Add($"'{t.Text[..Math.Min(40, t.Text.Length)]}' → {c} (parlaklık {parlaklik:F2})");
            }

            host.Close();
            Assert.True(kusurlu.Count == 0,
                "SEKMEYE TAŞININCA okunmayan metin(ler):\n  " + string.Join("\n  ", kusurlu));
        });
    }

    /// <summary>
    /// 🎯 GERÇEK SENARYO — TEMA DEĞİŞTİRME. Kusuru ancak burada üretebildim.
    ///
    /// Sihirbaz sekmesi AÇIK temada kurulup sonra KOYU temaya geçilince başlıklar koyu kalıyor.
    /// Mekanizma: sihirbazın metin rengi PENCERENİN üzerinde <c>DynamicResource</c>'tur; içerik
    /// sekmeye taşınınca pencere görsel ağaçtan çıkar ve <b>o DynamicResource bir daha ÇÖZÜLMEZ</b> —
    /// içerik eski temanın rengini taşımaya devam eder. Rengi elle yazılmış metinler canlı ağaçta
    /// olduğu için güncellenir; kullanıcının "başlıklar görünmüyor ama altları görünüyor" demesinin
    /// sebebi tam olarak bu ayrım.
    ///
    /// Düzeltme (<c>AracSekmesiViewModel</c>): içerik köküne <c>SetResourceReference</c> ile
    /// MetinFircasi bağlanır — kök CANLI ağaçta olduğu için tema değişiminde yeniden çözülür.
    /// Düzeltme geri alınırsa bu test KIRMIZI verir; koruduğu şey budur.
    /// </summary>
    [Fact]
    public void Tema_acikten_koyuya_donunce_basliklar_koyu_kalmaz()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            System.Windows.Application app = System.Windows.Application.Current!;
            var paletler = app.Resources.MergedDictionaries
                .Where(x => x.Source?.OriginalString.Contains("Palet", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            foreach (ResourceDictionary x in paletler)
                app.Resources.MergedDictionaries.Remove(x);
            try
            {
                // 1) AÇIK temada kur
                StaOrtak.Birlestir("PaletAcik.xaml");
                StaOrtak.Birlestir("Tema.xaml");

                var pencere = new SQLST.App.Views.SpSihirbaziPenceresi(
                    ["db1"], "db1",
                    _ => Task.FromResult<SemaOnbellegi?>(new SemaOnbellegi
                    {
                        Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow,
                    }),
                    (_, _, _) => { });
                var sekme = new SQLST.App.ViewModels.AracSekmesiViewModel("🪄 SP Sihirbazı", pencere);
                var tab = new TabControl();
                tab.Items.Add(new TabItem { Header = "🪄", Content = sekme.Icerik });
                var host = new Window
                {
                    Width = 1080, Height = 720, Content = tab,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000, Top = -32000, ShowInTaskbar = false,
                };
                host.Show();
                StaOrtak.PumpUntil(() => Metinler(host).Count() > 10);

                // 2) KOYU temaya geç — ürünün tema değiştirmesinin karşılığı
                ResourceDictionary acik = app.Resources.MergedDictionaries
                    .First(x => x.Source!.OriginalString.Contains("PaletAcik", StringComparison.Ordinal));
                app.Resources.MergedDictionaries.Remove(acik);
                StaOrtak.Birlestir("PaletKoyu.xaml");
                StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

                var kusurlu = new List<string>();
                foreach (TextBlock t in Metinler(host))
                {
                    if (string.IsNullOrWhiteSpace(t.Text) || t.Foreground is not SolidColorBrush f || f.Color.A == 0)
                        continue;
                    Color c = f.Color;
                    double p = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                    if (p < 0.35)
                        kusurlu.Add($"'{t.Text[..Math.Min(40, t.Text.Length)]}' → {c} (parlaklık {p:F2})");
                }

                host.Close();
                Assert.True(kusurlu.Count == 0,
                    "TEMA DEĞİŞİMİNDEN sonra koyu kalan metin(ler):\n  " + string.Join("\n  ", kusurlu));
            }
            finally
            {
                foreach (ResourceDictionary x in app.Resources.MergedDictionaries
                             .Where(x => x.Source?.OriginalString.Contains("Palet", StringComparison.OrdinalIgnoreCase) == true)
                             .ToList())
                    app.Resources.MergedDictionaries.Remove(x);
                foreach (ResourceDictionary x in paletler)
                    app.Resources.MergedDictionaries.Add(x);
            }
        });
    }

    /// <summary>
    /// 📸 TANI: sihirbazın DÖRT ADIMINI da koyu temada PNG'ye basar (%TEMP%\sqlst-sp-koyu-N.png).
    /// Test değil, gözle bakma aracı — renk ölçümü "kusur yok" derken kullanıcı sahada siyah başlık
    /// gördüğü için, ölçümün göremediği bir şey var mı diye insan gözüyle bakılıyor.
    /// </summary>
    [Fact]
    public void Koyu_tema_ekran_goruntusu_uret()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            using IDisposable _palet = YalnizKoyuPalet("PaletKoyu.xaml");

            var w = new SQLST.App.Views.SpSihirbaziPenceresi(
                ["db1"], "db1",
                _ => Task.FromResult<SemaOnbellegi?>(new SemaOnbellegi
                {
                    Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow,
                }),
                (_, _, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.PumpUntil(() => w.ActualWidth > 0);

            for (int adim = 1; adim <= 4; adim++)
            {
                // Adım panellerini doğrudan görünür kıl (İleri düğmesi doğrulama isteyebilir).
                foreach (string ad in new[] { "Icerik1", "Icerik2", "Icerik3", "Icerik4" })
                    if (w.FindName(ad) is UIElement e)
                        e.Visibility = ad == $"Icerik{adim}" ? Visibility.Visible : Visibility.Collapsed;

                w.UpdateLayout();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
                int gen = (int)Math.Ceiling(w.ActualWidth), yuk = (int)Math.Ceiling(w.ActualHeight);
                var rtb = new RenderTargetBitmap(gen, yuk, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(w);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using FileStream fs = File.Create(
                    Path.Combine(Path.GetTempPath(), $"sqlst-sp-koyu-{adim}.png"));
                enc.Save(fs);
            }

            w.Close();
        });
    }
}
