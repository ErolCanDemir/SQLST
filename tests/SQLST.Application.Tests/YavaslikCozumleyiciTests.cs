using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>BF-7 (2026-07-27): "Neden yavaş?" — plandan darboğaz raporu (eksik index/tarama/istatistik/uyarı, önceliklendirme).</summary>
public class YavaslikCozumleyiciTests
{
    private static PlanDugumu Dugum(string islem, string? ayrinti, double maliyet,
        double? tahmin = null, double? gercek = null, IReadOnlyList<string>? uyarilar = null,
        IReadOnlyList<PlanDugumu>? cocuklar = null)
        => new(islem, ayrinti, maliyet, tahmin, gercek, uyarilar ?? [], cocuklar ?? []);

    private static SorguPlani Plan(PlanDugumu kok, bool gercek = true,
        IReadOnlyList<PlanEksikIndexi>? eksik = null)
        => new(gercek, [new IfadePlani("SELECT ...", 1.5, kok, eksik ?? [])]);

    [Fact]
    public void Eksik_index_yuksek_oncelikli_ve_script_uretir()
    {
        var eksik = new PlanEksikIndexi(85, "[dbo].[Siparis]", ["MusteriId"], [], ["Tutar"]);
        YavaslikRaporu r = YavaslikCozumleyici.Coz(Plan(Dugum("Index Seek", "[dbo].[Siparis]", 100), eksik: [eksik]));

        YavaslikBulgusu b = r.Bulgular[0]; // en yüksek öncelik başta
        Assert.Equal(YavaslikOnem.Yuksek, b.Onem);
        Assert.Equal("Eksik index", b.Baslik);
        Assert.Contains("CREATE NONCLUSTERED INDEX", b.DuzeltmeScript);
        Assert.True(r.DarbogazVar);
    }

    [Fact]
    public void Tam_tarama_yuksek_maliyette_yuksek_onem()
    {
        YavaslikRaporu r = YavaslikCozumleyici.Coz(Plan(Dugum("Clustered Index Scan", "[dbo].[Musteri]", 40)));

        YavaslikBulgusu tarama = r.Bulgular.Single(b => b.Baslik == "Tam tarama");
        Assert.Equal(YavaslikOnem.Yuksek, tarama.Onem);
        Assert.Contains("%40", tarama.Aciklama);
    }

    [Fact]
    public void Kardinalite_sapmasi_istatistik_tazeligi_bulgusu_uretir()
    {
        // Gerçek plan: tahmin 10, gerçek 5000 → ≥10× sapma → istatistik tazeliği.
        YavaslikRaporu r = YavaslikCozumleyici.Coz(Plan(
            Dugum("Hash Match", "birleştirme", 20, tahmin: 10, gercek: 5000)));

        YavaslikBulgusu ist = r.Bulgular.Single(b => b.Baslik == "İstatistik tazeliği");
        Assert.Equal(YavaslikOnem.Orta, ist.Onem);
        Assert.Contains("UPDATE STATISTICS", ist.DuzeltmeScript);
    }

    [Fact]
    public void Sunucu_uyarisi_ipucuyla_yuzeye_cikar()
    {
        YavaslikRaporu r = YavaslikCozumleyici.Coz(Plan(
            Dugum("Nested Loops", null, 15, uyarilar: ["Type conversion in expression may affect cardinality"])));

        YavaslikBulgusu uyari = r.Bulgular.Single(b => b.Baslik == "Sunucu uyarısı");
        Assert.Contains("Örtük tip dönüşümü", uyari.Aciklama); // Türkçe ipucu eklendi
    }

    [Fact]
    public void Oncelik_sirasi_yuksek_orta_bilgi()
    {
        var eksik = new PlanEksikIndexi(50, "[dbo].[T]", ["A"], [], []);
        YavaslikRaporu r = YavaslikCozumleyici.Coz(Plan(
            Dugum("Table Scan", "[dbo].[T]", 20, tahmin: 5, gercek: 9000), eksik: [eksik]));

        // İlk bulgu Yüksek, son bulgu Bilgi olmalı (sıralama korunur).
        Assert.Equal(YavaslikOnem.Yuksek, r.Bulgular.First().Onem);
        Assert.Equal(YavaslikOnem.Bilgi, r.Bulgular.Last().Onem);
    }

    [Fact]
    public void Tahmini_planda_istatistik_olcumu_yok_ama_not_var()
    {
        YavaslikRaporu r = YavaslikCozumleyici.Coz(Plan(
            Dugum("Index Seek", "[dbo].[T]", 100), gercek: false));

        Assert.False(r.GercekPlan);
        Assert.DoesNotContain(r.Bulgular, b => b.Baslik == "İstatistik tazeliği");
        Assert.Contains(r.Bulgular, b => b.Baslik == "Tahmini plan");
    }

    [Fact]
    public void Temiz_planda_darbogaz_yok()
    {
        YavaslikRaporu r = YavaslikCozumleyici.Coz(Plan(Dugum("Index Seek", "[dbo].[T]", 100)));

        Assert.False(r.DarbogazVar); // yalnız "En pahalı adım" (Bilgi)
        Assert.StartsWith("Neden yavaş? — belirgin bir darboğaz", YavaslikCozumleyici.RaporMetni(r));
    }
}
