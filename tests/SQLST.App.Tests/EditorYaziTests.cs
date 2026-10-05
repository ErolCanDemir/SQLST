using System.IO;
using System.Xml.Linq;

namespace SQLST.App.Tests;

/// <summary>
/// 🔤 Editör yazı görünümü kilidi (kullanıcı 2026-09-17: <i>"editör penceremizde yazılar çok
/// karışık, acaba font tipinden mi? SSMS'de böyle değil"</i>).
///
/// Tanı (FontTanisi ile ölçüldü, 17 Eyl 2026): font SUÇLU DEĞİLDİ — Cascadia Mono makinede
/// çözülüyor. SSMS'ten iki gerçek fark vardı:
///   (1) TextFormattingMode WPF varsayılanı IDEAL'di — harfler piksel-altı konumlara yerleşir,
///       13px kod yazısı yumuşak/bulanık görünür; SSMS/VS editörleri DISPLAY (piksel-kenetli).
///   (2) SQL editöründe WordWrap AÇIKTI — uzun satır ifadenin ortasından işaretsiz alta sarılıp
///       devam parçası kendi başına bir satır gibi görünüyordu ("karışık"ın asıl kaynağı).
///
/// Bu ikisi sessizce geri dönerse görsel gerileme olur ama derleme yeşil kalır — o yüzden XML
/// düzeyinde kilitlenir (<see cref="KaydiriciOlculeriTests"/> ile aynı desen, WPF yüklemeden).
/// </summary>
public class EditorYaziTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Avalon = "http://icsharpcode.net/sharpdevelop/avalonedit";

    private static string KokDizin()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SQLST.slnx")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static XDocument Yukle(params string[] parcalar)
        => XDocument.Load(Path.Combine([KokDizin(), .. parcalar]));

    [Fact]
    public void Kod_editorleri_display_moduyla_cizer()
    {
        // Örtük stil TÜM TextEditor'lara tek yerden uygular (SQL, LINQ, REST/SOAP, önizlemeler) —
        // editör başına öznitelik olsaydı yeni eklenen editör sessizce Ideal'e dönerdi.
        XElement? stil = Yukle("src", "SQLST.App", "Tema.xaml")
            .Descendants(Ns + "Style")
            .FirstOrDefault(s => ((string?)s.Attribute("TargetType"))?.Contains("TextEditor") == true);
        Assert.True(stil is not null,
            "Tema.xaml'da TextEditor örtük stili YOK — kod editörleri WPF varsayılanı IDEAL'e düşer "
            + "(bulanık yazı, kullanıcı bulgusu 2026-09-17).");

        XElement? ayar = stil!.Elements(Ns + "Setter")
            .FirstOrDefault(a => (string?)a.Attribute("Property") == "TextOptions.TextFormattingMode");
        Assert.Equal("Display", (string?)ayar?.Attribute("Value"));
    }

    [Fact]
    public void Sql_editorunde_satir_sarma_kapali()
    {
        List<XElement> editorler = [.. Yukle("src", "SQLST.App", "MainWindow.xaml")
            .Descendants(Avalon + "TextEditor")];
        Assert.NotEmpty(editorler);

        XElement sql = editorler.Single(
            e => (string?)e.Attribute("AutomationProperties.AutomationId") == "SqlEditor");
        Assert.True(!string.Equals((string?)sql.Attribute("WordWrap"), "True", StringComparison.OrdinalIgnoreCase),
            "SQL editöründe WordWrap açık — uzun satır işaretsiz sarılınca kod 'karışık' okunur; "
            + "SSMS gibi yatay kaydırma verilmeli (kullanıcı bulgusu 2026-09-17).");
    }
}
