using SQLST.Application;
using Xunit;

namespace SQLST.Application.Tests;

public class AiIstekAyristiriciTests
{
    [Fact]
    public void Duz_json_metod_url_baslik_govde_cozer()
    {
        string cevap = """
            {"metod":"POST","url":"https://api/kisi","basliklar":{"Authorization":"Bearer x"},"govde":"{\"ad\":\"Ahmet\"}"}
            """;

        AiUretilenIstek? istek = AiIstekAyristirici.Ayristir(cevap, out string? hata);

        Assert.Null(hata);
        Assert.NotNull(istek);
        Assert.Equal("POST", istek!.Metod);
        Assert.Equal("https://api/kisi", istek.Url);
        Assert.Equal("{\"ad\":\"Ahmet\"}", istek.Govde);
        AiBaslik b = Assert.Single(istek.Basliklar);
        Assert.Equal("Authorization", b.Ad);
        Assert.Equal("Bearer x", b.Deger);
    }

    [Fact]
    public void Onsoz_ve_kod_biti_ciardan_ilk_blok_alinir()
    {
        // Yerel model sık sık açıklama + ```json çiti ekler — ilk dengeli {…} bloğu alınmalı
        string cevap = "İşte istek:\n```json\n{\"metod\":\"GET\",\"url\":\"https://api/x\"}\n```\nUmarım yardımcı olur.";

        AiUretilenIstek? istek = AiIstekAyristirici.Ayristir(cevap, out string? hata);

        Assert.Null(hata);
        Assert.Equal("GET", istek!.Metod);
        Assert.Equal("https://api/x", istek.Url);
        Assert.Null(istek.Govde);
        Assert.Empty(istek.Basliklar);
    }

    [Fact]
    public void Gecersiz_metod_get_e_duser()
    {
        AiUretilenIstek? istek = AiIstekAyristirici.Ayristir(
            "{\"metod\":\"FETCH\",\"url\":\"https://api/x\"}", out _);

        Assert.Equal("GET", istek!.Metod);
    }

    [Fact]
    public void Metod_buyuk_harfe_normallenir()
    {
        AiUretilenIstek? istek = AiIstekAyristirici.Ayristir(
            "{\"metod\":\"post\",\"url\":\"https://api/x\"}", out _);

        Assert.Equal("POST", istek!.Metod);
    }

    [Fact]
    public void Ic_ice_govde_suslu_parantezi_blok_sonunu_bozmaz()
    {
        // govde içindeki } string içindedir; dengeli tarama string'i atlamalı
        string cevap = "{\"metod\":\"POST\",\"url\":\"https://api/x\",\"govde\":\"{ bu bir } metin }\"}";

        AiUretilenIstek? istek = AiIstekAyristirici.Ayristir(cevap, out string? hata);

        Assert.Null(hata);
        Assert.Equal("{ bu bir } metin }", istek!.Govde);
    }

    [Fact]
    public void Url_yoksa_hata()
    {
        AiUretilenIstek? istek = AiIstekAyristirici.Ayristir("{\"metod\":\"GET\"}", out string? hata);

        Assert.Null(istek);
        Assert.NotNull(hata);
    }

    [Fact]
    public void Json_yoksa_hata()
    {
        AiUretilenIstek? istek = AiIstekAyristirici.Ayristir("Üzgünüm, bir istek üretemedim.", out string? hata);

        Assert.Null(istek);
        Assert.NotNull(hata);
    }

    [Fact]
    public void Bos_cevap_hata()
    {
        Assert.Null(AiIstekAyristirici.Ayristir("   ", out string? hata));
        Assert.NotNull(hata);
    }
}
