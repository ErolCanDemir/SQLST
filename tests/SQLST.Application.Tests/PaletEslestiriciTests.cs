using SQLST.Application;

namespace SQLST.Application.Tests;

public class PaletEslestiriciTests
{
    [Fact]
    public void Onek_icerir_ve_altdizi_oncelik_sirasi()
    {
        string[] adaylar = ["MusteriKaydet", "spMusteri", "KayitliMusteriler", "Siparis"];

        IReadOnlyList<string> sonuc = PaletEslestirici.Sirala("mus", adaylar, a => a);

        Assert.Equal("MusteriKaydet", sonuc[0]);       // önek
        Assert.Contains("spMusteri", sonuc);           // içerir
        Assert.Contains("KayitliMusteriler", sonuc);
        Assert.DoesNotContain("Siparis", sonuc);       // eşleşmez
    }

    [Fact]
    public void Buyuk_kucuk_harf_ve_altdizi_fuzzy()
    {
        Assert.NotNull(PaletEslestirici.Puan("spmk", "spMusteriKaydet")); // s-p-M-K alt-dizi
        Assert.NotNull(PaletEslestirici.Puan("VWMO", "vwMusteriOzet"));
        Assert.Null(PaletEslestirici.Puan("xyz", "spMusteriKaydet"));
    }

    [Fact]
    public void Bos_sorgu_hepsini_dogal_sirada_birakir()
    {
        string[] adaylar = ["b", "a", "c"];
        Assert.Equal(adaylar, PaletEslestirici.Sirala("", adaylar, a => a));
    }

    [Fact]
    public void Kisa_ad_ayni_turde_uzun_ada_gore_ustte()
    {
        IReadOnlyList<string> sonuc = PaletEslestirici.Sirala("sp",
            ["spCokUzunBirProcedureAdi", "spKisa"], a => a);
        Assert.Equal("spKisa", sonuc[0]);
    }

    [Fact]
    public void En_cok_siniri_uygulanir()
    {
        List<string> adaylar = [.. Enumerable.Range(0, 50).Select(i => $"sekme{i}")];
        Assert.Equal(12, PaletEslestirici.Sirala("sekme", adaylar, a => a).Count);
    }
}
