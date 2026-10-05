using System.IO;
using System.Xml.Linq;

namespace SQLST.App.Tests;

/// <summary>
/// v19-S9 — palet ailesi uyum bekçisi: 14 palet dosyası (7 açık + 7 koyu; v22-S15 VS ailesi
/// dahil) BİREBİR AYNI anahtar
/// kümesini tanımlamalı. Kural eskiden yorumdaydı ("yeni renk eklerken iki palete birden ekle");
/// varyantlarla dosya sayısı arttı — elle takip imkânsız, test korur. XML düzeyinde kıyas
/// (WPF yüklemeden): x:Key öznitelikleri + SystemColors statik anahtarları.
/// </summary>
public class PaletUyumTestleri
{
    private static readonly string[] Dosyalar =
    [
        "PaletAcik.xaml", "PaletAcikGrafit.xaml", "PaletAcikSlate.xaml",
        "PaletAcikPetrol.xaml", "PaletAcikAmber.xaml", "PaletAcikKor.xaml", "PaletAcikVS.xaml",
        "PaletKoyu.xaml", "PaletKoyuGrafit.xaml", "PaletKoyuSlate.xaml",
        "PaletKoyuPetrol.xaml", "PaletKoyuAmber.xaml", "PaletKoyuKor.xaml", "PaletKoyuVS.xaml",
        "PaletAcikAntrasit.xaml", "PaletKoyuAntrasit.xaml", // v23-S14
    ];

    private static string AppKlasoru()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SQLST.slnx")))
            d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "SQLST.App");
    }

    private static SortedSet<string> Anahtarlar(string yol)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument belge = XDocument.Load(yol);
        return [.. belge.Root!.Elements().Select(e => e.Attribute(x + "Key")?.Value ?? "?")];
    }

    [Fact]
    public void Tum_paletler_ayni_anahtar_kumesini_tanimlar()
    {
        string klasor = AppKlasoru();
        SortedSet<string> referans = Anahtarlar(Path.Combine(klasor, Dosyalar[0]));
        Assert.True(referans.Count > 40, "Referans palet beklenenden küçük — dosya mı değişti?");

        foreach (string dosya in Dosyalar.Skip(1))
        {
            SortedSet<string> anahtarlar = Anahtarlar(Path.Combine(klasor, dosya));
            IEnumerable<string> eksik = referans.Except(anahtarlar);
            IEnumerable<string> fazla = anahtarlar.Except(referans);
            Assert.True(anahtarlar.SetEquals(referans),
                $"{dosya} anahtar kümesi PaletAcik.xaml'dan sapıyor — eksik: [{string.Join(", ", eksik)}] · fazla: [{string.Join(", ", fazla)}]");
        }
    }

    [Fact]
    public void Palet_dosyasi_secimi_dogru_dosyaya_gider() // App.PaletDosyasi eşlemesi
    {
        Assert.Equal("PaletKoyu.xaml", App.PaletDosyasi(koyu: true, "indigo"));
        Assert.Equal("PaletKoyu.xaml", App.PaletDosyasi(koyu: true, "bilinmeyen")); // eski ayar → varsayılan
        Assert.Equal("PaletKoyuPetrol.xaml", App.PaletDosyasi(koyu: true, "petrol"));
        Assert.Equal("PaletAcik.xaml", App.PaletDosyasi(koyu: false, null));
        Assert.Equal("PaletAcikAmber.xaml", App.PaletDosyasi(koyu: false, "amber"));

        Assert.Equal("PaletKoyuVS.xaml", App.PaletDosyasi(koyu: true, "vs"));   // v22-S15
        Assert.Equal("PaletAcikVS.xaml", App.PaletDosyasi(koyu: false, "vs"));
        Assert.Equal("PaletKoyuAntrasit.xaml", App.PaletDosyasi(koyu: true, "antrasit"));   // v23-S14
        Assert.Equal("PaletAcikAntrasit.xaml", App.PaletDosyasi(koyu: false, "antrasit"));

        // Tüm varyant adları iki kipte de gerçek bir dosyaya çözülmeli ve dosya diskte olmalı
        string klasor = AppKlasoru();
        foreach (string ad in new[] { "indigo", "grafit", "slate", "petrol", "amber", "kor", "vs", "antrasit" })
        {
            Assert.True(File.Exists(Path.Combine(klasor, App.PaletDosyasi(true, ad))));
            Assert.True(File.Exists(Path.Combine(klasor, App.PaletDosyasi(false, ad))));
        }
    }

    // ── v22-S1 (saha turu-2 m.8): OKUNURLUK BEKÇİSİ ────────────────────────────────────────
    // Kullanıcı bulgusu: Kor AÇIK temada ray başlığındaki yazı beyaz kalmış, okunmuyordu (sabit
    // "White" XAML'e yazılmıştı; m.7'de ray zemini açık küle dönünce görünmez oldu). Kök neden
    // sabit renkti ama ders şu: metin/zemin ÇİFTLERİ ölçülmeli. Bu test 12 paletin gerçekten
    // eşleştiği çiftleri WCAG kontrast oranıyla sınar — palet/ton değişimi okunurluğu sessizce
    // bozamaz. Eşikler: normal metin 4.5:1; ikincil/küçük etiket ve chip 3:1.

    private static Dictionary<string, string> Renkler(string yol)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument belge = XDocument.Load(yol);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement e in belge.Root!.Elements())
        {
            if (e.Attribute(x + "Key")?.Value is not { } anahtar)
                continue;
            // SolidColorBrush Color="#.." · <Color x:Key="..">#..</Color>
            string renk = e.Attribute("Color")?.Value ?? e.Value.Trim();
            if (renk.StartsWith('#'))
                d[anahtar] = renk;
        }
        return d;
    }

    /// <summary>WCAG bağıl luminans (#RRGGBB ya da #AARRGGBB).</summary>
    private static double Luminans(string hex)
    {
        string h = hex.TrimStart('#');
        if (h.Length == 8)
            h = h[2..]; // alfa atılır — kontrast tam opak varsayımıyla ölçülür
        static double Kanal(int b)
        {
            double c = b / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        int r = Convert.ToInt32(h[..2], 16), g = Convert.ToInt32(h[2..4], 16), b2 = Convert.ToInt32(h[4..6], 16);
        return (0.2126 * Kanal(r)) + (0.7152 * Kanal(g)) + (0.0722 * Kanal(b2));
    }

    private static double Kontrast(string a, string b)
    {
        double la = Luminans(a), lb = Luminans(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData("MetinFircasi", "PencereZeminFircasi", 4.5)]   // ana gövde metni
    [InlineData("MetinFircasi", "ZeminAcikFircasi", 4.5)]
    [InlineData("MetinFircasi", "PanelZeminFircasi", 4.5)]
    [InlineData("IkincilFircasi", "PanelZeminFircasi", 3.0)]   // grid başlığı / ikincil etiket
    [InlineData("RayMetinFircasi", "RayGradyanUst", 4.5)]      // m.8'in tam çifti (ray başlığı)
    [InlineData("RayMetinFircasi", "RayGradyanAlt", 4.5)]
    [InlineData("EditorMetinFircasi", "EditorZeminFircasi", 4.5)]
    [InlineData("BilgiMetinFircasi", "BilgiZeminFircasi", 3.0)]
    [InlineData("UyariMetinFircasi", "UyariZeminFircasi", 3.0)]
    [InlineData("VurguZeminMetinFircasi", "VurguZeminFircasi", 3.0)] // m.18 chip yazısı (m.8: kendi anahtarı)
    // v22-S15 sistem tasarımı: vurgu renkli durum çubuğu + monokrom ray ikonları TÜM ailelerde —
    // tonlar bu çiftler ölçülerek seçildi; sessizce kayarlarsa okunmaz bant/ikon geri gelir.
    [InlineData("DurumCubuguMetinFircasi", "DurumCubuguZeminFircasi", 3.0)]
    [InlineData("RayIkonFircasi", "RayCipOrtusuFircasi", 3.0)]
    // v23-S14: grid metni, aile başına alternatif satır zemininde de okunmalı.
    [InlineData("MetinFircasi", "GridAlternatifSatirFircasi", 4.5)]
    public void Metin_zemin_ciftleri_okunur_kontrast_tasir(string metin, string zemin, double esik)
    {
        string klasor = AppKlasoru();
        var kirmizi = new List<string>();
        foreach (string dosya in Dosyalar)
        {
            Dictionary<string, string> renk = Renkler(Path.Combine(klasor, dosya));
            Assert.True(renk.ContainsKey(metin) && renk.ContainsKey(zemin),
                $"{dosya}: {metin}/{zemin} anahtarının rengi okunamadı");
            double oran = Kontrast(renk[metin], renk[zemin]);
            if (oran < esik)
                kirmizi.Add($"{dosya}: {metin}({renk[metin]}) / {zemin}({renk[zemin]}) = {oran:F2}:1");
        }
        Assert.True(kirmizi.Count == 0,
            $"{metin} · {zemin} çifti {esik}:1 eşiğini tutmuyor:\n{string.Join("\n", kirmizi)}");
    }
}
