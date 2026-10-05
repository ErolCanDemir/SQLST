using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.26 fikir 5 — index önerisi: MongoDB'de "missing index" DMV'si YOK; öneriler
/// system.profile'daki GERÇEK yavaş sorgulardan çıkar. Bu sınıf kuralları sabitler: yalnız
/// COLLSCAN, filtre+sıralama sırası (ESR), aynı desenin gruplanması, en çok zaman yiyenin önde,
/// filtresiz taramanın önerilmemesi.
/// </summary>
public class MongoIndexOnerisiTests
{
    private static ProfilKaydi Kayit(string koleksiyon, string[] filtre, long ms,
        bool collscan = true, string[]? sirala = null)
        => new(koleksiyon, filtre, sirala ?? [], ms, collscan);

    [Fact]
    public void Collscan_yapan_sorgudan_oneri_uretilir()
    {
        IReadOnlyList<IndexOnerisi> oneriler = MongoIndexOnerisi.Uret([Kayit("oturumlar", ["kullaniciId"], 900)]);

        IndexOnerisi o = Assert.Single(oneriler);
        Assert.Equal("oturumlar", o.Koleksiyon);
        Assert.Equal(["kullaniciId"], o.Alanlar);
        Assert.Equal(1, o.KacKez);
        Assert.Equal(900, o.ToplamMs);
        Assert.Equal("db.oturumlar.createIndex({ \"kullaniciId\": 1 })", o.Komut);
    }

    [Fact]
    public void Index_kullanan_sorgu_oneri_uretmez() // IXSCAN'li yavaşlık başka bir dert
        => Assert.Empty(MongoIndexOnerisi.Uret([Kayit("log", ["seviye"], 5000, collscan: false)]));

    [Fact]
    public void Filtresiz_tarama_onerilmez() // tüm koleksiyonu okuyan sorguyu index kurtarmaz
        => Assert.Empty(MongoIndexOnerisi.Uret([Kayit("log", [], 3000)]));

    [Fact]
    public void Ayni_desen_gruplanir_sayilar_toplanir()
    {
        IReadOnlyList<IndexOnerisi> oneriler = MongoIndexOnerisi.Uret(
        [
            Kayit("log", ["seviye"], 100), Kayit("log", ["seviye"], 250), Kayit("log", ["seviye"], 50),
        ]);

        IndexOnerisi o = Assert.Single(oneriler);
        Assert.Equal(3, o.KacKez);
        Assert.Equal(400, o.ToplamMs);
    }

    [Fact]
    public void En_cok_zaman_yiyen_desen_once_gelir()
    {
        IReadOnlyList<IndexOnerisi> oneriler = MongoIndexOnerisi.Uret(
        [
            Kayit("a", ["x"], 100),
            Kayit("b", ["y"], 400), Kayit("b", ["y"], 500),
        ]);

        Assert.Equal("b", oneriler[0].Koleksiyon);   // 900 ms
        Assert.Equal("a", oneriler[1].Koleksiyon);   // 100 ms
    }

    [Fact]
    public void Siralama_alanlari_filtreden_SONRA_gelir() // ESR: Equality, Sort, Range
    {
        IndexOnerisi o = Assert.Single(MongoIndexOnerisi.Uret(
            [Kayit("log", ["seviye", "kaynak"], 300, sirala: ["zaman"])]));

        Assert.Equal(["seviye", "kaynak", "zaman"], o.Alanlar);
        Assert.Equal("db.log.createIndex({ \"seviye\": 1, \"kaynak\": 1, \"zaman\": 1 })", o.Komut);
    }

    [Fact]
    public void Alan_tavani_asilmaz_ve_yinelenen_alan_tekillesir()
    {
        IndexOnerisi o = Assert.Single(MongoIndexOnerisi.Uret(
            [Kayit("log", ["a", "b", "c", "d", "e", "f"], 100, sirala: ["a"])]));

        Assert.Equal(MongoIndexOnerisi.AzamiAlan, o.Alanlar.Count);
        Assert.Equal(["a", "b", "c", "d"], o.Alanlar);   // "a" sıralamada tekrar etti, bir kez yazıldı
    }

    [Fact]
    public void Oneri_sayisi_sinirli()
    {
        IReadOnlyList<IndexOnerisi> oneriler = MongoIndexOnerisi.Uret(
            [.. Enumerable.Range(1, 30).Select(i => Kayit($"k{i}", [$"alan{i}"], i * 10))], azamiOneri: 5);

        Assert.Equal(5, oneriler.Count);
        Assert.Equal("k30", oneriler[0].Koleksiyon);   // en pahalı desen başta
    }
}
