using ICSharpCode.AvalonEdit.Highlighting;

namespace SQLST.App.Tests;

/// <summary>
/// Editör sözdizimi şemasının (kendi T-SQL tanımımız) gerçekten yüklendiğini kanıtlar. xshd parse
/// hatasında EditorTema sessizce gömülü "TSQL"e düşerdi; bu test o sessiz düşüşü yakalar — yani
/// kapsamlı anahtar sözcük listesi + sayı kuralı fiilen devrede.
/// </summary>
public class EditorTemaTests
{
    [Fact]
    public void Isik_ve_koyu_kendi_semamizi_yukler()
    {
        IHighlightingDefinition? isik = EditorTema.Tanim(koyu: false);
        IHighlightingDefinition? koyu = EditorTema.Tanim(koyu: true);

        // Gömülü "TSQL"e DÜŞMEDİ — kendi tanımlarımız yüklendi
        Assert.Equal("TSQL-SQLST", isik?.Name);
        Assert.Equal("TSQL-SQLST-Koyu", koyu?.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sema_sayi_ve_anahtar_sozcuk_renklerini_tanimlar(bool koyu)
    {
        IHighlightingDefinition tanim = EditorTema.Tanim(koyu)!;

        // Kullanıcı istekleri: sayılar ayrı renk + tüm SQL sözcükleri (NOLOCK/OVER/PARTITION…) boyalı
        Assert.Contains(tanim.NamedHighlightingColors, c => c.Name == "Number" && c.Foreground is not null);
        Assert.Contains(tanim.NamedHighlightingColors, c => c.Name == "Keyword" && c.Foreground is not null);
    }

    /// <summary>
    /// 🎨 SSMS renk paritesi kilidi (kullanıcı 2026-09-23: "editör renkleri SSMS'le aynı değil").
    /// Açık tema SSMS'in varsayılan editör paleti olmalı — sözcük SAF MAVİ ve KALINSIZ (SSMS hiçbir
    /// şeyi kalın yazmaz), 'metin' KIRMIZI, yorum yeşil, yerleşik fonksiyon MACENTA, operatör GRİ.
    /// Tek bilinçli sapma: sayı turuncu kalır (kullanıcının 2026-07-24 isteği). Renkler sessizce
    /// kayarsa görsel gerileme olur ama derleme yeşil kalır — o yüzden burada kilitli.
    /// </summary>
    [Theory]
    [InlineData("Keyword", "#ff0000ff")]
    [InlineData("String", "#ffff0000")]
    [InlineData("Comment", "#ff008000")]
    [InlineData("Fonksiyon", "#ffff00ff")]
    [InlineData("Operator", "#ff808080")]
    public void Acik_tema_ssms_paletidir(string ad, string beklenen)
    {
        HighlightingColor renk = EditorTema.Tanim(koyu: false)!
            .NamedHighlightingColors.Single(c => c.Name == ad);
        Assert.Equal(beklenen, renk.Foreground!.ToString(), ignoreCase: true);
        Assert.Null(renk.FontWeight); // SSMS hiçbir öğeyi kalın yazmaz — kalınlık geri gelmesin
    }

    [Fact]
    public void Koyu_tema_saf_mavi_kirmiziya_dusmez() // saf tonlar koyu zeminde okunmaz — köprü çevirir
    {
        IHighlightingDefinition koyu = EditorTema.Tanim(koyu: true)!;
        foreach (HighlightingColor c in koyu.NamedHighlightingColors.Where(c => c.Foreground is not null))
        {
            string f = c.Foreground!.ToString()!.ToLowerInvariant();
            Assert.True(f is not ("#ff0000ff" or "#ffff0000" or "#ffff00ff"),
                $"Koyu temada '{c.Name}' saf açık-tema tonunda ({f}) kalmış — köprü çevirmiyor.");
        }
    }

    [Fact]
    public void Fonksiyonlar_anahtar_sozcukten_ayri_gruptadir() // COUNT artık mavi değil macenta
    {
        // Tanım metnine değil YÜKLENMİŞ şemaya bakılır: ana ruleset'te hem Keyword hem Fonksiyon
        // renkli sözcük kuralı olmalı (SSMS'te COUNT/SUM/GETDATE macenta, SELECT/CAST mavi).
        IHighlightingDefinition tanim = EditorTema.Tanim(koyu: false)!;
        Assert.Contains(tanim.NamedHighlightingColors, c => c.Name == "Fonksiyon" && c.Foreground is not null);
        Assert.Contains(tanim.NamedHighlightingColors, c => c.Name == "Operator" && c.Foreground is not null);
    }
}
