using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.10 fikir 10 — WHERE'de tanım değeri önerisi: bağlam tespiti (imleç karşılaştırmanın
/// sağında mı) ve kolonun FK hedefini bulma. Yanlış bağlamda öneri AÇILMAMALI (yazarken gürültü),
/// belirsiz hedefte de öneri yok (yanlış tablonun kodlarını önermektense hiç önermemek).
/// </summary>
public class TanimDegeriBaglamiTests
{
    private static string? Kolon(string metin) => TanimDegeriBaglami.KolonCikar(metin, metin.Length);

    [Theory]
    [InlineData("SELECT * FROM T WHERE DurumId = ")]              // klasik
    [InlineData("SELECT * FROM T WHERE t.DurumId = ")]            // alias'lı → son parça
    [InlineData("SELECT * FROM T WHERE [t].[DurumId]=")]          // köşeli + boşluksuz
    [InlineData("SELECT * FROM T WHERE DurumId <> ")]             // eşit değil
    [InlineData("SELECT * FROM T WHERE DurumId >= ")]             // kıyas
    [InlineData("SELECT * FROM T WHERE DurumId = 3")]             // kısmen yazılmış değer
    [InlineData("SELECT * FROM T WHERE DurumId IN (")]            // IN listesi başı
    [InlineData("SELECT * FROM T WHERE DurumId IN (1, 2, ")]      // IN listesi devamı
    public void Karsilastirma_baglaminda_kolon_cikar(string metin)
        => Assert.Equal("DurumId", Kolon(metin));

    [Theory]
    [InlineData("SELECT * FROM T WHERE ")]                        // henüz kolon yok
    [InlineData("SELECT * FROM T")]                               // karşılaştırma yok
    [InlineData("SELECT DurumId, ")]                              // SELECT listesi virgülü ≠ IN
    [InlineData("SELECT * FROM T WHERE ISNULL(")]                 // fonksiyon parantezi
    [InlineData("")]
    public void Yanlis_baglamda_oneri_acilmaz(string metin)
        => Assert.Null(Kolon(metin));

    private static YabanciAnahtar Fk(string kaynakTablo, string kaynakKolon, string hedefTablo, string hedefKolon = "Id")
        => new("dbo", kaynakTablo, [kaynakKolon], "dbo", hedefTablo, [hedefKolon]);

    [Fact]
    public void Hedef_fk_grafindan_bulunur()
    {
        var hedef = TanimDegeriBaglami.HedefBul([Fk("Talep", "DurumId", "Durum")], "DurumId");

        Assert.NotNull(hedef);
        Assert.Equal(("dbo", "Durum", "Id"), (hedef!.Value.Sema, hedef.Value.Tablo, hedef.Value.AnahtarKolon));
    }

    [Fact]
    public void Ayni_kolon_adi_farkli_hedeflere_gidiyorsa_oneri_yok()
    {
        List<YabanciAnahtar> fkler = [Fk("Talep", "DurumId", "Durum"), Fk("Siparis", "DurumId", "SiparisDurumu")];

        Assert.Null(TanimDegeriBaglami.HedefBul(fkler, "DurumId"));   // belirsiz → sus

        // Kaynak tablo biliniyorsa (tek tablolu sorgu) belirsizlik kalkar
        Assert.Equal("Durum", TanimDegeriBaglami.HedefBul(fkler, "DurumId", "Talep")!.Value.Tablo);
        Assert.Equal("SiparisDurumu", TanimDegeriBaglami.HedefBul(fkler, "DurumId", "Siparis")!.Value.Tablo);
    }

    [Fact]
    public void Ayni_hedefe_giden_coklu_fk_belirsizlik_sayilmaz()
    {
        List<YabanciAnahtar> fkler = [Fk("Talep", "DurumId", "Durum"), Fk("Iade", "DurumId", "Durum")];
        Assert.Equal("Durum", TanimDegeriBaglami.HedefBul(fkler, "DurumId")!.Value.Tablo);
    }

    [Fact]
    public void Fk_olmayan_kolonda_oneri_yok()
        => Assert.Null(TanimDegeriBaglami.HedefBul([Fk("Talep", "DurumId", "Durum")], "Tutar"));
}
