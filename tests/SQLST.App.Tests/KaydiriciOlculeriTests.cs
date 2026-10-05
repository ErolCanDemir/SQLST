using System.IO;
using System.Xml.Linq;

namespace SQLST.App.Tests;

/// <summary>
/// 🖱 Kaydırıcı ölçüleri kilidi (kullanıcı bulgusu 25 Ağu 2026: <i>"hâlâ çok verili tablolarda grid
/// scroll küçük kalıyor"</i>).
///
/// NEDEN TEST GEREKİYOR: tutamağın boyu içerik/görünüm ORANIYLA belirlenir. 34.000 satırlık bir
/// sonuçta doğal boy birkaç piksele iner ve tamamen <c>MinHeight</c> TABANINA dayanır — yani
/// "çok verili tabloda" kullanıcının gördüğü şey doğrudan bu sabittir. Sayı sessizce küçültülürse
/// kusur aynen geri gelir ve kimse fark etmez (görsel bir gerileme, derleme yeşil kalır).
///
/// XML düzeyinde denetlenir (WPF yüklemeden) — <see cref="PaletUyumTestleri"/> ile aynı desen.
/// </summary>
public class KaydiriciOlculeriTests
{

    /// <summary>Kaydırıcı bandının asgari kalınlığı (px) — m.7'de 14'e çıkarılmıştı.</summary>
    private const int AsgariBantKalinligi = 14;

    private static XDocument Tema()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SQLST.slnx")))
            d = d.Parent;
        Assert.NotNull(d);
        return XDocument.Load(Path.Combine(d!.FullName, "src", "SQLST.App", "Tema.xaml"));
    }

    /// <summary>
    /// Asgari mekanizması <c>Track.ViewportSize</c> ŞİŞİRMESİ olmalı; tutamakta Min* OLMAMALI.
    ///
    /// ⚠ Bu test 27 Ağu 2026'da kökten değişti. Tutamağa MinHeight vermek (sabit YA DA bağlı)
    /// HİÇ çalışmadı: Track tutamağı kendi hesapladığı doğal orana yerleştirir ve MinHeight'lı
    /// elemanı o dikdörtgene KIRPAR — eleman 59 px ÖLÇÜLÜR, ekrana ~4 px ÇİZİLİR (piksel kanıtı
    /// Tema.xaml'daki şablon notunda). Yani Thumb üzerindeki her Min* denemesi sessizce işlevsizdir
    /// ve artık KUSUR sayılır. Doğru yol: Track'in girdisini (ViewportSize) şişirmek — o zaman
    /// Track'in KENDİ hesabı hedef boyu verir ve kırpma olmaz.
    /// </summary>
    [Fact]
    public void Asgari_viewport_sismesiyle_dayatilir_thumbda_min_yok()
    {
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sablonlar = Tema().Descendants(ns + "ControlTemplate")
            .Where(ct => (string?)ct.Attribute(x + "Key") is "DikeyKaydirici" or "YatayKaydirici")
            .ToList();
        Assert.Equal(2, sablonlar.Count);

        foreach (XElement sablon in sablonlar)
        {
            string ad = (string)sablon.Attribute(x + "Key")!;

            // 1) Thumb'da Min* YOK — Track kırptığı için her Min* denemesi sessizce işlevsiz.
            foreach (XElement t in sablon.Descendants(ns + "Thumb"))
            {
                Assert.True(t.Attribute("MinHeight") is null && t.Attribute("MinWidth") is null,
                    $"{ad}: Thumb'da Min* var — Track bunu KIRPAR, ekranda görünmez; "
                    + "asgari ViewportSize şişirmesiyle dayatılmalı (27 Ağu 2026 piksel kanıtı).");
            }

            // 2) Track.ViewportSize şişirme bağlaması VAR ve doğru dönüştürücüyle.
            XElement? viewport = sablon.Descendants(ns + "Track.ViewportSize").FirstOrDefault();
            Assert.True(viewport is not null,
                $"{ad}: Track.ViewportSize bağlaması YOK — tutamaç asgarisi dayatılamaz (nokta geri gelir).");
            XElement? mb = viewport!.Element(ns + "MultiBinding");
            Assert.True(mb is not null && (mb.Attribute("Converter")?.Value.Contains("TutamacViewport") ?? false),
                $"{ad}: Track.ViewportSize, TutamacViewport dönüştürücüsüne bağlı olmalı.");
        }
    }

    /// <summary>
    /// Viewport şişirme matematiği: şişirilmiş V' ile Track'in kendi hesabı (L×V'/(V'+R)) TAM hedef
    /// boyu vermeli. Bu, "ekranda gerçekten o boyda çizilir" iddiasının cebirsel karşılığıdır.
    /// </summary>
    [Theory]
    [InlineData(98.4, 3, 9997, 59.0)]    // kullanıcının gerçek senaryosu: 10.000 satır, kısa grid
    [InlineData(658, 20, 33980, 160)]    // tam ekran + 34.000 satır → tavan
    [InlineData(358, 8, 192, 160)]       // 200 satır → tavan (ekran görüntüsündeki durum)
    [InlineData(133, 40, 300, 79.8)]     // sonuç alanı dış kaydırıcısı (ölçülen)
    public void Sisirilmis_viewport_track_hesabinda_hedef_boyu_verir(
        double ray, double viewport, double aralik, double beklenenBoy)
    {
        var d = new SQLST.App.Converters.TutamacViewportConverter();
        object sonuc = d.Convert([viewport, aralik, 0d, ray], typeof(double), null, null!);
        double v2 = Assert.IsType<double>(sonuc);
        double trackHesabi = ray * v2 / (v2 + aralik);
        Assert.Equal(beklenenBoy, trackHesabi, 0);
    }

    /// <summary>Doğal boy zaten hedefin üstündeyse viewport'a DOKUNULMAZ (davranış değişmez).</summary>
    [Fact]
    public void Dogal_boy_yeterliyse_viewport_degismez()
    {
        var d = new SQLST.App.Converters.TutamacViewportConverter();
        // yatay gerçek senaryo: ray 1088, viewport 1106, aralık 383 → doğal 808 px > 160
        object sonuc = d.Convert([1106d, 383d, 0d, 1087.7], typeof(double), null, null!);
        Assert.Equal(1106d, Assert.IsType<double>(sonuc));
    }

    /// <summary>Kaydırılacak şey yokken / yerleşim öncesi bozulma yok.</summary>
    [Theory]
    [InlineData(0, 0, 0, 100)]     // aralık 0 — kaydırma yok
    [InlineData(10, 0, 0, 100)]    // Max=Min
    [InlineData(10, 50, 0, 0)]     // ray henüz ölçülmedi
    public void Kenar_durumlarinda_viewport_aynen_doner(
        double viewport, double max, double min, double ray)
    {
        var d = new SQLST.App.Converters.TutamacViewportConverter();
        object sonuc = d.Convert([viewport, max, min, ray], typeof(double), null, null!);
        Assert.Equal(viewport, Assert.IsType<double>(sonuc));
    }

    [Fact]
    public void Kaydirici_bandi_yeterince_kalin()
    {
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var scrollBarStili = Tema().Descendants(ns + "Style")
            .FirstOrDefault(s => s.Attribute("TargetType")?.Value == "ScrollBar");
        Assert.NotNull(scrollBarStili);

        string? genislik = scrollBarStili!.Elements(ns + "Setter")
            .FirstOrDefault(s => s.Attribute("Property")?.Value == "MinWidth")
            ?.Attribute("Value")?.Value;

        Assert.True(int.TryParse(genislik, out int px), $"ScrollBar MinWidth okunamadı: '{genislik}'");
        Assert.True(px >= AsgariBantKalinligi,
            $"kaydırıcı bandı {px}px — en az {AsgariBantKalinligi}px olmalı (m.7 kararı).");
    }

    /// <summary>
    /// Asgari boy hesabı (saf). İki uç AYNI ANDA tutulmalı: uzun rayda kullanıcının istediği
    /// tutulabilir boy (72), kısa rayda RAYA SIĞAN boy. Sabit 72 tam olarak ikincisinde kırılmıştı.
    /// </summary>
    [Theory]
    [InlineData(700, 160)]  // tam ekran rayı → tavan (~%24)
    [InlineData(358, 160)]  // grid 400px'in rayı → tavan
    [InlineData(200, 120)]  // 200×0,6=120 → oran devrede
    [InlineData(120, 72)]   // 120×0,6=72 → oran devrede
    [InlineData(100, 60)]   // kısalıyor → rayla birlikte küçülür
    [InlineData(58, 34.8)]  // grid 100px (ÖLÇÜLEN ray) — eskiden 72 idi, taşıyordu
    [InlineData(38, 22.8)]  // grid 80px
    [InlineData(18, 10.8)]  // grid 60px
    [InlineData(10, 8)]     // taban
    [InlineData(0, 8)]      // henüz ölçülmedi
    public void Asgari_boy_raya_gore_hesaplanir(double ray, double beklenen)
        => Assert.Equal(beklenen, SQLST.App.Converters.TutamacAsgarisiConverter.Hesapla(ray), 1);

    /// <summary>Hesaplanan asgari HER ZAMAN raya sığmalı — kusurun tanımı tam olarak buydu.</summary>
    [Theory]
    [InlineData(700)]
    [InlineData(200)]
    [InlineData(120)]
    [InlineData(58)]
    [InlineData(38)]
    [InlineData(18)]
    [InlineData(12)]
    public void Asgari_boy_asla_rayi_asmaz(double ray)
    {
        double asgari = SQLST.App.Converters.TutamacAsgarisiConverter.Hesapla(ray);
        Assert.True(asgari <= ray, $"ray {ray}px, asgari {asgari}px — TAŞIYOR (kaydırıcı kaybolur).");
    }
}
