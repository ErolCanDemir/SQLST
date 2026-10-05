using SQLST.App.ViewModels;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// B2/A3 — plan sekmesi görünüm mantığı. B1'de düzeltilen "uydurma sinyal" hatasının
/// ViewModel tarafındaki regresyonu: tahmin üretmeyen motorda açıklama metni ve sapma
/// bandı yanlış şey söylüyordu.
/// </summary>
public class PlanSekmesiTests
{
    private static PlanDugumu Dugum(double? tahmin, double? gercek, params string[] uyarilar)
        => new("Seq Scan", "musteri", 42, tahmin, gercek, uyarilar, []);

    private static SorguPlani Plan(bool gercek, params PlanDugumu[] dugumler)
        => new(gercek, [.. dugumler.Select(d => new IfadePlani("SELECT 1", 1, d, []))]);

    [Fact]
    public void Tahmin_URETMEYEN_motorda_aciklama_karsilastirma_VAAT_ETMEZ()
    {
        // Mongo: ölçüm var, tahmin yok. Eski metin "optimizer'ın tahminiyle
        // karşılaştırılabilir" diyordu — karşılaştıracak tahmin yokken.
        var sekme = new PlanSekmesiViewModel(Plan(gercek: true, Dugum(null, 5000)), "test.sql");

        Assert.DoesNotContain("tahminiyle karşılaştırılabilir", sekme.Aciklama, StringComparison.Ordinal);
        Assert.Contains("incelenen belge", sekme.Aciklama, StringComparison.Ordinal);
        Assert.False(sekme.SapmaVar);
        Assert.Empty(sekme.Sapmalar);
    }

    [Fact]
    public void Tahmin_URETEN_motorda_karsilastirma_anlatilir()
    {
        var sekme = new PlanSekmesiViewModel(Plan(gercek: true, Dugum(10, 20)), "test.sql");

        Assert.Contains("tahminiyle karşılaştırılabilir", sekme.Aciklama, StringComparison.Ordinal);
    }

    [Fact]
    public void Tahmini_planda_sorgunun_CALISTIRILMADIGI_soylenir()
    {
        var sekme = new PlanSekmesiViewModel(Plan(gercek: false, Dugum(10, null)), "test.sql");

        Assert.Contains("ÇALIŞTIRILMADI", sekme.Aciklama, StringComparison.Ordinal);
    }

    [Fact]
    public void Gercek_sapma_bandi_HALA_calisir()
    {
        // Düzeltme sinyali köreltmemeli: 10 beklenip 5000 gelmesi hâlâ bildirilmeli.
        var sekme = new PlanSekmesiViewModel(Plan(gercek: true, Dugum(10, 5000)), "test.sql");

        Assert.True(sekme.SapmaVar);
        Assert.Single(sekme.Sapmalar);
    }

    [Fact]
    public void Uyarilar_TEKILLESTIRILIR()
    {
        var sekme = new PlanSekmesiViewModel(
            Plan(gercek: true, Dugum(1, 1, "diske taşma"), Dugum(1, 1, "diske taşma")), "test.sql");

        Assert.True(sekme.UyariVar);
        Assert.Single(sekme.Uyarilar);
    }

    [Fact]
    public void Baslik_planin_turunu_soyler()
    {
        Assert.StartsWith("📊", new PlanSekmesiViewModel(Plan(true, Dugum(1, 1)), "a.sql").Baslik,
            StringComparison.Ordinal);
        Assert.StartsWith("📈", new PlanSekmesiViewModel(Plan(false, Dugum(1, null)), "a.sql").Baslik,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Ilk_ifade_secili_gelir()
    {
        var sekme = new PlanSekmesiViewModel(Plan(true, Dugum(1, 1), Dugum(2, 2)), "test.sql");

        Assert.Equal(2, sekme.Ifadeler.Count);
        Assert.NotNull(sekme.SeciliIfade);
    }
}
