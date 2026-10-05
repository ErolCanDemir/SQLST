using SQLST.Application;
using Xunit;

namespace SQLST.Application.Tests;

public class JsonYolAyiklayiciTests
{
    [Fact]
    public void Duz_alan_skaler_deger()
    {
        Assert.Equal("abc", JsonYolAyiklayici.Ayikla("{\"token\":\"abc\"}", "token", out string? hata));
        Assert.Null(hata);
    }

    [Fact]
    public void Ic_ice_alan_nokta_ile()
    {
        Assert.Equal("42", JsonYolAyiklayici.Ayikla("{\"data\":{\"id\":42}}", "data.id", out _));
    }

    [Fact]
    public void Dizi_indeksi_ve_alan()
    {
        string json = "{\"items\":[{\"ad\":\"x\"},{\"ad\":\"y\"}]}";
        Assert.Equal("y", JsonYolAyiklayici.Ayikla(json, "items[1].ad", out _));
    }

    [Fact]
    public void Kok_dizi_indeksi()
    {
        Assert.Equal("1", JsonYolAyiklayici.Ayikla("[1,2,3]", "[0]", out _));
    }

    [Fact]
    public void Nesne_ya_da_dizi_ham_json_doner()
    {
        string json = "{\"data\":{\"id\":1,\"ad\":\"x\"}}";
        string? sonuc = JsonYolAyiklayici.Ayikla(json, "data", out _);
        Assert.Contains("\"id\"", sonuc);
        Assert.Contains("\"ad\"", sonuc);
    }

    [Fact]
    public void Bool_deger_metin()
    {
        Assert.Equal("true", JsonYolAyiklayici.Ayikla("{\"aktif\":true}", "aktif", out _));
    }

    [Fact]
    public void Olmayan_alan_null_ve_hata()
    {
        Assert.Null(JsonYolAyiklayici.Ayikla("{\"a\":1}", "b", out string? hata));
        Assert.NotNull(hata);
    }

    [Fact]
    public void Gecersiz_indeks_null_ve_hata()
    {
        Assert.Null(JsonYolAyiklayici.Ayikla("{\"a\":[1]}", "a[5]", out string? hata));
        Assert.NotNull(hata);
    }

    [Fact]
    public void Gecersiz_json_null_ve_hata()
    {
        Assert.Null(JsonYolAyiklayici.Ayikla("{bozuk", "a", out string? hata));
        Assert.NotNull(hata);
    }

    [Fact]
    public void Bos_yol_hata()
    {
        Assert.Null(JsonYolAyiklayici.Ayikla("{\"a\":1}", "", out string? hata));
        Assert.NotNull(hata);
    }
}
