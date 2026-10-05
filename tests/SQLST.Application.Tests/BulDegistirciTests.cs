using System.Text.RegularExpressions;
using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>Editör "Bul ve Değiştir" SAF çekirdeği (kullanıcı isteği 2026-07-23): desen kurma + tümünü değiştir.</summary>
public class BulDegistirciTests
{
    [Fact]
    public void Duz_metin_buyuk_kucuk_duyarsiz_varsayilan()
    {
        Regex d = BulDegistirci.Desen("abc", buyukKucuk: false, tamSozcuk: false, regex: false, out string? hata)!;
        Assert.Null(hata);
        Assert.Matches(d, "xABCy"); // duyarsız
        Assert.Equal(2, d.Matches("abc ABC").Count);
    }

    [Fact]
    public void Buyuk_kucuk_duyarli()
    {
        Regex d = BulDegistirci.Desen("abc", buyukKucuk: true, tamSozcuk: false, regex: false, out _)!;
        Assert.DoesNotMatch(d, "ABC");
        Assert.Matches(d, "abc");
    }

    [Fact]
    public void Tam_sozcuk_parca_eslesmez()
    {
        Regex d = BulDegistirci.Desen("cat", buyukKucuk: false, tamSozcuk: true, regex: false, out _)!;
        Assert.DoesNotMatch(d, "category");
        Assert.Matches(d, "a cat b");
    }

    [Fact]
    public void Duz_modda_ozel_karakter_literal()
    {
        // "a.b" düz modda sadece "a.b" eşleşir, "axb" değil (nokta kaçırılır)
        Regex d = BulDegistirci.Desen("a.b", buyukKucuk: false, tamSozcuk: false, regex: false, out _)!;
        Assert.Matches(d, "a.b");
        Assert.DoesNotMatch(d, "axb");
    }

    [Fact]
    public void Gecersiz_regex_hata_doner()
    {
        Regex? d = BulDegistirci.Desen("(", buyukKucuk: false, tamSozcuk: false, regex: true, out string? hata);
        Assert.Null(d);
        Assert.Contains("regex", hata!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bos_metin_hata()
    {
        Assert.Null(BulDegistirci.Desen("", false, false, false, out string? hata));
        Assert.Contains("boş", hata!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TumunuDegistir_sayar_ve_degistirir()
    {
        Regex d = BulDegistirci.Desen("x", false, false, false, out _)!;
        (string metin, int sayi) = BulDegistirci.TumunuDegistir("xax bx", d, "Y");
        Assert.Equal(3, sayi);
        Assert.Equal("YaY bY", metin);
    }

    [Fact]
    public void TumunuDegistir_eslesme_yoksa_kaynak_ayni()
    {
        Regex d = BulDegistirci.Desen("z", false, false, false, out _)!;
        (string metin, int sayi) = BulDegistirci.TumunuDegistir("abc", d, "Y");
        Assert.Equal(0, sayi);
        Assert.Equal("abc", metin);
    }

    [Fact]
    public void Yerlestirme_duz_modda_dolar_literal()
    {
        Regex d = BulDegistirci.Desen("x", false, false, regex: false, out _)!;
        (string metin, _) = BulDegistirci.TumunuDegistir("x", d, BulDegistirci.Yerlestirme("$1", regex: false));
        Assert.Equal("$1", metin); // $1 grup değil, literal
    }
}
