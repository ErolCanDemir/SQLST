using System.Text;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>v20-S14 Dosya Aç: kodlama çözümü — BOM öncelikli, sıkı UTF-8, 1254'e uyarılı düşüş.
/// Türkçe karakter hiçbir yolda sessizce bozulmaz.</summary>
public class SqlDosyaKodlamaTests
{
    private const string Turkce = "SELECT 'ışğüçöİĞÜ' AS Tanımı";

    [Fact]
    public void Utf8_bom_cozulur_bom_metne_sizmaz()
    {
        byte[] bayt = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Turkce)];

        KodlanmisMetin sonuc = SqlDosyaKodlama.Coz(bayt);

        Assert.Equal(Turkce, sonuc.Metin);
        Assert.Equal("UTF-8 (BOM)", sonuc.KodlamaAdi);
        Assert.Null(sonuc.Uyari);
    }

    [Fact]
    public void Bomsuz_utf8_cozulur()
    {
        KodlanmisMetin sonuc = SqlDosyaKodlama.Coz(Encoding.UTF8.GetBytes(Turkce));

        Assert.Equal(Turkce, sonuc.Metin);
        Assert.Equal("UTF-8", sonuc.KodlamaAdi);
    }

    [Fact]
    public void Utf16_le_bom_cozulur()
    {
        byte[] bayt = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Turkce)];

        KodlanmisMetin sonuc = SqlDosyaKodlama.Coz(bayt);

        Assert.Equal(Turkce, sonuc.Metin);
        Assert.Equal("UTF-16 LE", sonuc.KodlamaAdi);
    }

    [Fact]
    public void Turkce_ansi_1254_uyariyla_cozulur()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bayt = Encoding.GetEncoding(1254).GetBytes(Turkce); // ı/ş/ğ 1254'te tek bayt, UTF-8'de geçersiz dizi

        KodlanmisMetin sonuc = SqlDosyaKodlama.Coz(bayt);

        Assert.Equal(Turkce, sonuc.Metin); // mojibake YOK — "tanÄ±mÄ±" asla
        Assert.Equal("Windows-1254", sonuc.KodlamaAdi);
        Assert.NotNull(sonuc.Uyari);
    }

    [Fact]
    public void Bos_dosya_bos_metin()
    {
        KodlanmisMetin sonuc = SqlDosyaKodlama.Coz([]);

        Assert.Equal("", sonuc.Metin);
        Assert.Equal("UTF-8", sonuc.KodlamaAdi);
    }
}
