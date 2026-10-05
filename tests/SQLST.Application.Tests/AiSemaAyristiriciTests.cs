using SQLST.Application;
using Xunit;

namespace SQLST.Application.Tests;

public class AiSemaAyristiriciTests
{
    [Fact]
    public void Tablo_adi_ve_kolonlar_cozulur()
    {
        string cevap = """
            {"tabloAdi":"kisiler","kolonlar":[
              {"ad":"id","tip":"int","pk":true,"null":false},
              {"ad":"ad","tip":"nvarchar(100)","pk":false,"null":true}]}
            """;

        AiSema? sema = AiSemaAyristirici.Ayristir(cevap, out string? hata);

        Assert.Null(hata);
        Assert.Equal("kisiler", sema!.TabloAdi);
        Assert.Equal(2, sema.Kolonlar.Count);
        Assert.Equal("id", sema.Kolonlar[0].Ad);
        Assert.Equal("int", sema.Kolonlar[0].Tip);
        Assert.True(sema.Kolonlar[0].Pk);
        Assert.False(sema.Kolonlar[0].NullOlabilir);
        Assert.False(sema.Kolonlar[1].Pk);
        Assert.True(sema.Kolonlar[1].NullOlabilir);
    }

    [Fact]
    public void Onsoz_ve_cit_ciardan_ilk_blok_alinir()
    {
        string cevap = "Tabii:\n```json\n{\"kolonlar\":[{\"ad\":\"x\",\"tip\":\"int\"}]}\n```\n";

        AiSema? sema = AiSemaAyristirici.Ayristir(cevap, out string? hata);

        Assert.Null(hata);
        AiSemaKolonu k = Assert.Single(sema!.Kolonlar);
        Assert.Equal("x", k.Ad);
        Assert.False(k.Pk);          // yok → varsayılan false
        Assert.True(k.NullOlabilir); // yok → varsayılan true
    }

    [Fact]
    public void Pk_null_string_ya_da_sayi_ile_verilse_bagislanir()
    {
        string cevap = "{\"kolonlar\":[{\"ad\":\"id\",\"tip\":\"int\",\"pk\":\"true\",\"null\":0}]}";

        AiSema? sema = AiSemaAyristirici.Ayristir(cevap, out _);

        Assert.True(sema!.Kolonlar[0].Pk);          // "true" string
        Assert.False(sema.Kolonlar[0].NullOlabilir); // 0 → false
    }

    [Fact]
    public void Adi_ya_da_tipi_olmayan_kolon_atlanir()
    {
        string cevap = "{\"kolonlar\":[{\"ad\":\"\",\"tip\":\"int\"},{\"ad\":\"ok\",\"tip\":\"\"},{\"ad\":\"iyi\",\"tip\":\"int\"}]}";

        AiSema? sema = AiSemaAyristirici.Ayristir(cevap, out _);

        AiSemaKolonu k = Assert.Single(sema!.Kolonlar);
        Assert.Equal("iyi", k.Ad);
    }

    [Fact]
    public void Kolonlar_dizisi_yoksa_hata()
    {
        AiSema? sema = AiSemaAyristirici.Ayristir("{\"tabloAdi\":\"x\"}", out string? hata);

        Assert.Null(sema);
        Assert.NotNull(hata);
    }

    [Fact]
    public void Tum_kolonlar_gecersizse_hata()
    {
        AiSema? sema = AiSemaAyristirici.Ayristir("{\"kolonlar\":[{\"ad\":\"\",\"tip\":\"\"}]}", out string? hata);

        Assert.Null(sema);
        Assert.NotNull(hata);
    }

    [Fact]
    public void Json_yoksa_hata()
    {
        Assert.Null(AiSemaAyristirici.Ayristir("bir şema öneremedim", out string? hata));
        Assert.NotNull(hata);
    }
}
