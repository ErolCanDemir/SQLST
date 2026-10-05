using System.IO;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S1 (saha turu-2 m.5; kullanıcı: "Mongo'ya özel 'bu id nerede geçiyor' MSSQL'de de görünüyor").
/// KÖK NEDEN bir WPF tuzağıydı: ContextMenu kendi POPUP ağacında yaşar; içindeki
/// <c>RelativeSource AncestorType=Window</c> bağlaması Window'u BULAMAZ → binding sessizce düşer ve
/// <c>Visibility</c> varsayılanında (Visible) kalır. Yani motora özgü öğeler HER motorda görünüyordu
/// (Mongo öğesi MSSQL'de, MSSQL öğeleri Mongo'da).
///
/// Kural testi bu hatayı YAKALAMAZDI (kural doğruydu, bağlama çalışmıyordu) — bu yüzden denetim
/// XAML'in kendisine bakıyor: ContextMenu içinde Window'a ancestor bağlaması OLMAYACAK; motora
/// özgü görünürlük Tag sözleşmesiyle (M:Mongo · M:Mssql · M:Script) kodda kurulacak.
/// </summary>
public class MenuMotorSuzgeciTests
{
    private static string XamlOku(string ad)
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "SQLST.slnx")))
            dizin = dizin.Parent;
        Assert.NotNull(dizin); // depo kökü bulunamadı
        return File.ReadAllText(Path.Combine(dizin!.FullName, "src", "SQLST.App", ad));
    }

    /// <summary>ContextMenu bloklarının içinde kalan satırlar (iç içe blok sayacıyla).</summary>
    private static List<string> MenuIciSatirlar(string xaml)
    {
        var icerde = new List<string>();
        int derinlik = 0;
        foreach (string satir in xaml.Split('\n'))
        {
            if (satir.Contains("<ContextMenu") && !satir.Contains("/>"))
                derinlik++;
            if (satir.Contains("</ContextMenu>"))
                derinlik--;
            if (derinlik > 0)
                icerde.Add(satir);
        }
        return icerde;
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    public void ContextMenu_icinde_window_ancestor_baglamasi_olmaz(string dosya)
    {
        List<string> suphe = [.. MenuIciSatirlar(XamlOku(dosya))
            .Where(s => s.Contains("AncestorType=Window") && s.Contains("{Binding"))];

        Assert.True(suphe.Count == 0,
            "ContextMenu içinde Window'a ancestor bağlaması ÇALIŞMAZ (popup ayrı ağaç) — görünürlüğü "
            + $"Tag (M:Mongo/M:Mssql/M:Script) + kod ile kurun:\n{string.Join("\n", suphe)}");
    }

    [Fact]
    public void Motora_ozgu_menu_ogeleri_tag_tasiyor()
    {
        string xaml = XamlOku("MainWindow.xaml");

        // Kullanıcının bulduğu öğe ve kardeşleri: hepsi motor etiketiyle işaretli olmalı.
        Assert.Contains("Click=\"MongoIdIzi_Click\" Tag=\"M:Mongo\"", xaml);
        Assert.Contains("Click=\"InsertKopyala_Click\" Tag=\"M:Mssql\"", xaml);
        Assert.Contains("Click=\"InListeKopyala_Click\" Tag=\"M:Mssql\"", xaml);
        Assert.Contains("Click=\"KayitHaritasi_Click\" Tag=\"M:Script\"", xaml);
        Assert.Contains("Click=\"KayitHaritasiDelete_Click\" Tag=\"M:Script\"", xaml);
        Assert.Contains("Tag=\"M:Mssql\"", xaml); // ağaçtaki 📤 Şemayı kopyala (aynı tuzak)

        // Ve her M: etiketli öğe bir açılış işleyicisi olan menüde olmalı: grid menüsü
        // SonucMenu_Aciliyor, ağaç DB menüsü MotorMenusu_Aciliyor ile kurulur.
        Assert.Contains("ContextMenuOpening=\"SonucMenu_Aciliyor\"", xaml);
        Assert.Contains("ContextMenuOpening=\"MotorMenusu_Aciliyor\"", xaml);
    }

    /// <summary>
    /// v22-S4 Edit m.2 kilidi: Edit sekmesinin sorgu kutusu GERÇEK editördür (avalon:TextEditor) —
    /// düz TextBox'a geri dönülürse (öneriler sessizce ölür) bu test kırmızı yanar.
    /// </summary>
    /// <summary>
    /// v22-S5 (kullanıcı: "balonlar ekranın ortasında gibi, sola sağa yaslayalım"): sohbet
    /// kapsayıcısı TAM GENIŞLIKTE olmalı. Eskiden MaxWidth=900 + Center idi; balonlar zaten
    /// sola/sağa hizalıydı ama o dar sütunun içinde hizalandığı için geniş ekranda ortada
    /// toplanıyordu. Kapsayıcıya MaxWidth/Center geri gelirse bu test kırmızı yanar.
    /// </summary>
    [Fact]
    public void Asistan_sohbet_kapsayicisi_tam_genislikte()
    {
        string xaml = XamlOku("MainWindow.xaml");
        int i = xaml.IndexOf("AsistanMesajlar", StringComparison.Ordinal);
        Assert.True(i >= 0, "Asistan mesaj listesi bulunamadı");

        string blok = xaml[Math.Max(0, i - 400)..i];
        Assert.Contains("HorizontalAlignment=\"Stretch\"", blok);
        Assert.DoesNotContain("HorizontalAlignment=\"Center\"", blok);
        Assert.DoesNotContain("MaxWidth=\"900\"", blok);
    }

    [Fact]
    public void Edit_sorgu_kutusu_gercek_editordur()
    {
        string xaml = XamlOku("MainWindow.xaml");
        int i = xaml.IndexOf("TxtEditFiltreSql", StringComparison.Ordinal);
        Assert.True(i >= 0, "Edit sorgu kutusu bulunamadı");

        string blok = xaml[Math.Max(0, i - 900)..i];
        Assert.Contains("avalon:TextEditor", blok);
        Assert.Contains("DuzenlemeEditor_Loaded", blok);   // tamamlama kurulumunun kapısı
        Assert.Contains("FiltreBelgesi", blok);            // belge bağlaması (m.2 köprüsü)
        Assert.DoesNotContain("<TextBox", blok);
    }
}
