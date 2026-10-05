using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S12 — REST "Tabloya kaydet" yolunun SAF çekirdeği: JSON yanıtından kolon+satır + tip çıkarımı.
/// Excel/TXT içe aktarımıyla PAYLAŞILAN <see cref="KolonTipiTahminci"/> tiplemesini kullanır.
/// </summary>
public class JsonTabloAyristiriciTests
{
    [Fact]
    public void Nesne_dizisi_kolon_ve_tipleri_cikarir()
    {
        const string json = """
            [
              { "id": 1, "ad": "Ahmet", "tutar": 12.5, "aktif": true },
              { "id": 2, "ad": "Ayşe",  "tutar": 8,    "aktif": false }
            ]
            """;

        DosyaOnizleme? o = JsonTabloAyristirici.Ayristir(json, out string? hata);

        Assert.Null(hata);
        Assert.NotNull(o);
        Assert.Equal(2, o!.Satirlar.Count);
        Assert.Equal(["id", "ad", "tutar", "aktif"], o.Kolonlar.Select(k => k.Ad));
        Assert.Equal(DosyaTipi.TamSayi, o.Kolonlar[0].Tip);
        Assert.Equal(DosyaTipi.Metin, o.Kolonlar[1].Tip);
        Assert.Equal(DosyaTipi.Ondalik, o.Kolonlar[2].Tip); // 12.5 + 8 karışımı → ondalık
        Assert.Equal(DosyaTipi.Bool, o.Kolonlar[3].Tip);
        Assert.Equal(1L, o.Satirlar[0][0]); // sayı .NET tipiyle taşınır (long)
    }

    [Fact]
    public void Tek_nesne_tek_satirlik_diziye_sarilir()
    {
        DosyaOnizleme? o = JsonTabloAyristirici.Ayristir("""{ "x": 1, "y": "a" }""", out string? hata);

        Assert.Null(hata);
        Assert.NotNull(o);
        Assert.Single(o!.Satirlar);
        Assert.Equal(["x", "y"], o.Kolonlar.Select(k => k.Ad));
    }

    [Fact]
    public void Ic_ice_nesne_ve_dizi_ham_json_metnine_serilestirilir()
    {
        const string json = """[ { "id": 1, "adres": { "il": "Ankara" }, "etiketler": [1,2] } ]""";

        DosyaOnizleme? o = JsonTabloAyristirici.Ayristir(json, out _);

        Assert.NotNull(o);
        // Karmaşık alanlar metin kolona düşer (ilk sürüm sınırı).
        Assert.Equal(DosyaTipi.Metin, o!.Kolonlar[1].Tip);
        Assert.Equal(DosyaTipi.Metin, o.Kolonlar[2].Tip);
        Assert.Contains("Ankara", (string)o.Satirlar[0][1]!);
    }

    [Fact]
    public void Farkli_alan_kumeleri_kolonlarin_birlesimini_verir()
    {
        const string json = """[ { "a": 1 }, { "b": 2 } ]""";

        DosyaOnizleme? o = JsonTabloAyristirici.Ayristir(json, out _);

        Assert.NotNull(o);
        Assert.Equal(["a", "b"], o!.Kolonlar.Select(k => k.Ad));
        Assert.Null(o.Satirlar[0][1]); // ilk satırda "b" yok → null
        Assert.Null(o.Satirlar[1][0]); // ikinci satırda "a" yok → null
    }

    [Theory]
    [InlineData("", "Yanıt boş")]
    [InlineData("   ", "Yanıt boş")]
    [InlineData("değil json", "Geçerli JSON değil")]
    [InlineData("[]", "dizisi boş")]
    [InlineData("42", "nesne ya da nesne dizisi olmalı")]
    public void Gecersiz_girdi_net_mesajla_reddedilir_cokmez(string json, string mesajParcasi)
    {
        DosyaOnizleme? o = JsonTabloAyristirici.Ayristir(json, out string? hata);

        Assert.Null(o);
        Assert.NotNull(hata);
        Assert.Contains(mesajParcasi, hata);
    }

    [Fact]
    public void Skaler_dizi_tek_deger_kolonuna_sarilir()
    {
        DosyaOnizleme? o = JsonTabloAyristirici.Ayristir("[1, 2, 3]", out string? hata);

        Assert.Null(hata);
        Assert.NotNull(o);
        Assert.Single(o!.Kolonlar);
        Assert.Equal("deger", o.Kolonlar[0].Ad);
        Assert.Equal(3, o.Satirlar.Count);
    }
}
