using SQLST.App.ViewModels;
using SQLST.Application;

namespace SQLST.App.Tests;

/// <summary>
/// Harita Odak+Bağlam VM davranışı (v11-öncesi #1, 2026-07-25): eşik altı küçük şema eski davranış,
/// eşik üstü MAHALLE açılışı (tablo kartı çizilmez), kümeye dalma, tabloya odaklanma (derinlik),
/// arama-Enter odağı ve kompakt kart kuralı.
/// </summary>
public class HaritaOdakBaglamTests
{
    private static HaritaModeli BuyukModel(int uyduSayisi = 40)
    {
        // Hub + N uydu — eşik (30) rahat aşılır; odak derinlik-1'de hepsi görünür (kompakt testi).
        var dugumler = new List<HaritaDugumu> { new("dbo", "Hub", []) };
        var kenarlar = new List<HaritaKenari>();
        for (int i = 1; i <= uyduSayisi; i++)
        {
            dugumler.Add(new HaritaDugumu("dbo", $"Uydu{i:00}", []));
            kenarlar.Add(new HaritaKenari($"dbo.Uydu{i:00}", "dbo.Hub", [new("X", "Id")]));
        }
        return new HaritaModeli(dugumler, kenarlar);
    }

    [Fact]
    public void Kucuk_sema_eski_davranis_tum_kartlar()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(new HaritaModeli(
            [new("dbo", "A", []), new("dbo", "B", [])],
            [new HaritaKenari("dbo.A", "dbo.B", [new("X", "Id")])]));

        Assert.False(vm.MahalleModu);
        Assert.False(vm.MahalleVar);
        Assert.Equal(2, vm.Dugumler.Count);
        Assert.Single(vm.Kenarlar);
    }

    [Fact]
    public void Buyuk_sema_mahalleyle_acilir_tablo_karti_cizilmez()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(BuyukModel());

        Assert.True(vm.MahalleModu);
        Assert.True(vm.MahalleVar);
        Assert.Empty(vm.Dugumler);                    // ≤30 kart kuralı: mahallede kart yok
        Assert.True(vm.KumeKartlari.Count >= 2);      // 41 tablo / hedef 12 → 4+ küme
        Assert.Contains(vm.KumeKartlari, k => k.Kume.Uyeler.Contains("dbo.Hub"));
    }

    [Fact]
    public void Kumeye_dalinca_yalniz_o_kumenin_tablolari()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(BuyukModel());
        HaritaKumeKartGorunumu kart = vm.KumeKartlari[0];

        vm.KumeAcCommand.Execute(kart);

        Assert.False(vm.MahalleModu);
        Assert.Equal(kart.UyeSayisi, vm.Dugumler.Count);
        Assert.True(vm.MahalleDonGorunur); // geri dönüş yolu görünür
    }

    [Fact]
    public void Tabloya_odaklaninca_komsuluk_cizilir_ve_derinlik_degisir()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(BuyukModel(uyduSayisi: 40));

        vm.TabloOdakla("dbo.Uydu01"); // derinlik 1: kendisi + Hub
        Assert.Equal(2, vm.Dugumler.Count);
        Assert.True(vm.OdakModunda);

        vm.DerinlikDegistirCommand.Execute(null); // derinlik 2: + Hub'ın TÜM uyduları
        Assert.Equal(2, vm.OdakDerinligi);
        Assert.Equal(41, vm.Dugumler.Count);
        Assert.True(vm.Dugumler.All(d => d.Kompakt), "41 kart eşik üstü — kompakt çizilmeli.");

        vm.DerinlikDegistirCommand.Execute(null); // geri 1
        Assert.Equal(2, vm.Dugumler.Count);
        Assert.False(vm.Dugumler.All(d => d.Kompakt) && vm.Dugumler.Count > 0
            ? vm.Dugumler[0].Kompakt : false); // az kart = tam kart
    }

    [Fact]
    public void Arama_enter_ilk_eslesene_odaklanir()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(BuyukModel());

        vm.Arama = "Uydu05";
        vm.AramaOdakla();

        Assert.False(vm.MahalleModu);
        Assert.True(vm.OdakModunda);
        Assert.Contains(vm.Dugumler, d => d.TamAd == "dbo.Uydu05");
        Assert.Contains(vm.Dugumler, d => d.TamAd == "dbo.Hub"); // komşusu da geldi
    }

    [Fact]
    public void Mahalleye_don_kume_kartlarini_geri_getirir()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(BuyukModel());
        vm.TabloOdakla("dbo.Hub");
        Assert.False(vm.MahalleModu);

        vm.MahalleyeDonCommand.Execute(null);

        Assert.True(vm.MahalleModu);
        Assert.Empty(vm.Dugumler);
        Assert.False(vm.OdakModunda);
    }
}
