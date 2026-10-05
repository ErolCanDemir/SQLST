using SQLST.Application;
using Xunit;

namespace SQLST.Application.Tests;

public class YanitDbKarsilastiriciTests
{
    private static object?[] R(params object?[] hucreler) => hucreler;

    [Fact]
    public void Yalniz_api_yalniz_db_ve_esit_siniflanir()
    {
        string[] kolonlar = ["id", "ad"];
        // API: 1,2  ·  DB: 1,3  → id1 eşit, id2 yalnız-API, id3 yalnız-DB
        var api = new object?[][] { R(1L, "Ahmet"), R(2L, "Ayşe") };
        var db = new object?[][] { R(1, "Ahmet"), R(3, "Mehmet") };

        KarsilastirmaSonucu s = YanitDbKarsilastirici.Karsilastir(kolonlar, api, kolonlar, db, "id");

        Assert.Null(s.Hata);
        Assert.Equal(1, s.Esit);
        Assert.Equal(1, s.YalnizApi);
        Assert.Equal(1, s.YalnizDb);
        Assert.Equal(0, s.Farkli);
        Assert.Equal(KarsilastirmaDurumu.Esit, s.Satirlar.Single(x => x.Anahtar == "1").Durum);
        Assert.Equal(KarsilastirmaDurumu.YalnizApi, s.Satirlar.Single(x => x.Anahtar == "2").Durum);
        Assert.Equal(KarsilastirmaDurumu.YalnizDb, s.Satirlar.Single(x => x.Anahtar == "3").Durum);
    }

    [Fact]
    public void Farkli_deger_farkli_kolonu_isaretler()
    {
        string[] kolonlar = ["id", "ad", "tutar"];
        var api = new object?[][] { R(1L, "Ahmet", "100") };
        var db = new object?[][] { R(1, "Mehmet", "100") }; // ad farklı, tutar aynı

        KarsilastirmaSonucu s = YanitDbKarsilastirici.Karsilastir(kolonlar, api, kolonlar, db, "id");

        Assert.Equal(1, s.Farkli);
        KarsilastirmaSatiri satir = s.Satirlar.Single();
        Assert.Equal(KarsilastirmaDurumu.Farkli, satir.Durum);
        Assert.Equal(["ad"], satir.FarkliKolonlar);
    }

    [Fact]
    public void Tam_sayi_json_long_ile_db_int_esit_sayilir()
    {
        // Madde 1 düzeltmesi: JSON tam sayısı long kalır; DB int → ikisi de "1" → eşit
        string[] kolonlar = ["id", "sayi"];
        var api = new object?[][] { R(1L, 42L) };
        var db = new object?[][] { R(1, 42) };

        KarsilastirmaSonucu s = YanitDbKarsilastirici.Karsilastir(kolonlar, api, kolonlar, db, "id");

        Assert.Equal(1, s.Esit);
        Assert.Equal(0, s.Farkli);
    }

    [Fact]
    public void Ortak_olmayan_kolonlar_karsilastirmaya_girmez()
    {
        // API'de "extra" var, DB'de yok → ortak kolon yalnız "ad"; "extra" farkı tetiklemez
        var api = YanitDbKarsilastirici.Karsilastir(
            ["id", "ad", "extra"], new object?[][] { R(1L, "X", "sadece-api") },
            ["id", "ad"], new object?[][] { R(1, "X") },
            "id");

        Assert.Equal(["ad"], api.OrtakKolonlar);
        Assert.Equal(1, api.Esit);
    }

    [Fact]
    public void Anahtar_adi_buyuk_kucuk_harf_duyarsiz()
    {
        var s = YanitDbKarsilastirici.Karsilastir(
            ["ID"], new object?[][] { R(1L) },
            ["id"], new object?[][] { R(1) },
            "Id"); // farklı casing

        Assert.Null(s.Hata);
        Assert.Equal(1, s.Esit);
    }

    [Fact]
    public void Anahtar_kolon_yoksa_hata()
    {
        var s = YanitDbKarsilastirici.Karsilastir(
            ["ad"], new object?[][] { R("X") },
            ["ad"], new object?[][] { R("X") },
            "id");

        Assert.NotNull(s.Hata);
        Assert.Empty(s.Satirlar);
    }

    [Fact]
    public void Null_ve_dbnull_bos_metin_olarak_esit()
    {
        var s = YanitDbKarsilastirici.Karsilastir(
            ["id", "not"], new object?[][] { R(1L, null) },
            ["id", "not"], new object?[][] { R(1, DBNull.Value) },
            "id");

        Assert.Equal(1, s.Esit); // null ↔ DBNull → ikisi de "" → eşit
    }
}
