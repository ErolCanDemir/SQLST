using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>BF-3 (2026-07-27): Excel/tablo fark motoru — YENI/DEGISEN/SILINMIS kovalari + tip normalize.</summary>
public class ExcelFarkTests
{
    private static Dictionary<string, object?> S(params (string, object?)[] alanlar)
        => alanlar.ToDictionary(a => a.Item1, a => a.Item2);

    [Fact]
    public void Uc_kova_dogru_ayrilir()
    {
        var kaynak = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Id", 1), ("Ad", "Ahmet"), ("Sehir", "Izmir")),   // degismedi
            S(("Id", 2), ("Ad", "Ayse"), ("Sehir", "Bursa")),    // Sehir degisti
            S(("Id", 4), ("Ad", "Veli"), ("Sehir", "Ankara")),   // yeni
        };
        var hedef = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Id", 1), ("Ad", "Ahmet"), ("Sehir", "Izmir")),
            S(("Id", 2), ("Ad", "Ayse"), ("Sehir", "Istanbul")), // eski Sehir
            S(("Id", 3), ("Ad", "Can"), ("Sehir", "Adana")),     // Excel'de yok -> silinecek
        };

        TabloFarki fark = ExcelFarkKarsilastirici.Karsilastir(
            kaynak, hedef, ["Id"], ["Ad", "Sehir"]);

        Assert.Single(fark.Yeniler);
        Assert.Equal(4, fark.Yeniler[0].Degerler["Id"]);

        DegisenSatir degisen = Assert.Single(fark.Degisenler);
        Assert.Equal(2, degisen.Anahtar["Id"]);
        Assert.True(degisen.Degisenler.ContainsKey("Sehir"));
        Assert.False(degisen.Degisenler.ContainsKey("Ad"));      // Ad ayni -> degisiklikte YOK
        Assert.Equal("Istanbul", degisen.Degisenler["Sehir"].Eski);
        Assert.Equal("Bursa", degisen.Degisenler["Sehir"].Yeni);

        FarkSatiri silinen = Assert.Single(fark.Silinenler);
        Assert.Equal(3, silinen.Degerler["Id"]);
    }

    [Fact]
    public void Sayisal_olcek_farki_yanlis_degisti_uretmez()
    {
        // Excel double 1250 vs DB decimal 1250.00 -> AYNI sayilmali (olcek/gosterim farki).
        var kaynak = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Tutar", 1250d)) };
        var hedef = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Tutar", 1250.00m)) };

        TabloFarki fark = ExcelFarkKarsilastirici.Karsilastir(kaynak, hedef, ["Id"], ["Tutar"]);

        Assert.Empty(fark.Degisenler); // 1250 == 1250.00
        Assert.Empty(fark.Yeniler);
        Assert.Empty(fark.Silinenler);
    }

    [Fact]
    public void Null_ile_bos_metin_farkli_sayilir()
    {
        var kaynak = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Not", "")) };
        var hedef = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Not", null)) };

        TabloFarki fark = ExcelFarkKarsilastirici.Karsilastir(kaynak, hedef, ["Id"], ["Not"]);

        Assert.Single(fark.Degisenler); // NULL != "" -> gercek degisiklik
        Assert.Equal("", fark.Degisenler[0].Degisenler["Not"].Yeni);
        Assert.Null(fark.Degisenler[0].Degisenler["Not"].Eski);
    }

    [Fact]
    public void Bilesik_anahtar_desteklenir()
    {
        var kaynak = new IReadOnlyDictionary<string, object?>[] { S(("Yil", 2026), ("No", 5), ("Tutar", 10)) };
        var hedef = new IReadOnlyDictionary<string, object?>[] { S(("Yil", 2026), ("No", 5), ("Tutar", 20)) };

        TabloFarki fark = ExcelFarkKarsilastirici.Karsilastir(kaynak, hedef, ["Yil", "No"], ["Tutar"]);

        Assert.Single(fark.Degisenler); // ayni bilesik anahtar, Tutar farkli
        Assert.Empty(fark.Yeniler);
        Assert.Empty(fark.Silinenler);
    }

    [Fact]
    public void Anahtar_kolon_yoksa_firlatir()
        => Assert.Throws<ArgumentException>(() =>
            ExcelFarkKarsilastirici.Karsilastir([], [], [], ["Ad"]));

    [Fact]
    public void Tarih_saat_bileseni_farki_ayni_gunde_degisti_saymaz_saat_gercekse_sayar()
    {
        // Ayni an -> ayni; farkli saat -> degisti (tam gun+saat karsilastirmasi).
        var t1 = new DateTime(2026, 7, 27, 9, 0, 0);
        var kaynakAyni = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Zaman", t1)) };
        var hedefAyni = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Zaman", t1)) };
        Assert.Empty(ExcelFarkKarsilastirici.Karsilastir(kaynakAyni, hedefAyni, ["Id"], ["Zaman"]).Degisenler);

        var hedefFarkli = new IReadOnlyDictionary<string, object?>[]
            { S(("Id", 1), ("Zaman", new DateTime(2026, 7, 27, 10, 0, 0))) };
        Assert.Single(ExcelFarkKarsilastirici.Karsilastir(kaynakAyni, hedefFarkli, ["Id"], ["Zaman"]).Degisenler);
    }
}
