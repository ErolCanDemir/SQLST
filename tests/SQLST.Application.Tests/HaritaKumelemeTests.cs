using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// Harita Odak+Bağlam SAF mantığı (v11-öncesi #1, 2026-07-25): deterministik kümeleme (çekirdek =
/// en bağlantılı), küme boyu sınırı, bağımsızlar kümesi, kümeler arası FK sayacı, odak BFS komşuluğu
/// ve radyal yerleşim.
/// </summary>
public class HaritaKumelemeTests
{
    private static HaritaDugumu D(string ad, string sema = "dbo") => new(sema, ad, []);

    private static HaritaKenari K(string kaynak, string hedef)
        => new($"dbo.{kaynak}", $"dbo.{hedef}", [new("X", "Id")]);

    [Fact]
    public void Yildiz_semada_cekirdek_hub_olur_ve_kume_boyu_asilmaz()
    {
        // Hub + 15 uydu (hedefBoy 12) → ilk küme 12 üyeli (hub çekirdek), kalan 4 ikinci kümede.
        var dugumler = new List<HaritaDugumu> { D("Hub") };
        var kenarlar = new List<HaritaKenari>();
        for (int i = 1; i <= 15; i++)
        {
            dugumler.Add(D($"Uydu{i:00}"));
            kenarlar.Add(K($"Uydu{i:00}", "Hub"));
        }

        HaritaKumeHaritasi km = HaritaKumeleme.Kur(new HaritaModeli(dugumler, kenarlar), hedefBoy: 12);

        Assert.Equal(2, km.Kumeler.Count);
        Assert.Equal(12, km.Kumeler[0].Uyeler.Count);
        Assert.Contains("dbo.Hub", km.Kumeler[0].Uyeler);          // çekirdek ilk kümede
        Assert.Equal("dbo.Hub", km.Kumeler[0].OneCikanlar[0]);     // en bağlantılı öne çıkar
        Assert.Equal(4, km.Kumeler[1].Uyeler.Count);
        // İki küme arasında FK bağları sayılır (kalan 4 uydu hub'a bağlı)
        Assert.Equal(4, Assert.Single(km.Kenarlar).FkSayisi);
    }

    [Fact]
    public void Bagimsiz_tablolar_tek_kumede_toplanir()
    {
        var model = new HaritaModeli(
            [D("A"), D("B"), D("Tek1"), D("Tek2")],
            [K("A", "B")]);

        HaritaKumeHaritasi km = HaritaKumeleme.Kur(model);

        Assert.Equal(2, km.Kumeler.Count);
        Assert.Equal("Bağımsız tablolar", km.Kumeler[1].Ad);
        Assert.Equal(["dbo.Tek1", "dbo.Tek2"], km.Kumeler[1].Uyeler);
        Assert.Empty(km.Kenarlar); // bağımsızlara FK yok
    }

    [Fact]
    public void Kumeleme_deterministiktir()
    {
        var model = new HaritaModeli(
            [D("A"), D("B"), D("C"), D("E"), D("F")],
            [K("A", "B"), K("B", "C"), K("E", "F")]);

        HaritaKumeHaritasi bir = HaritaKumeleme.Kur(model);
        HaritaKumeHaritasi iki = HaritaKumeleme.Kur(model);

        Assert.Equal(
            bir.Kumeler.Select(k => string.Join("|", k.Uyeler)),
            iki.Kumeler.Select(k => string.Join("|", k.Uyeler)));
    }

    [Fact]
    public void Komsular_bfs_derinlige_gore()
    {
        // Zincir: A - B - C - D
        var model = new HaritaModeli(
            [D("A"), D("B"), D("C"), D("D")],
            [K("A", "B"), K("B", "C"), K("C", "D")]);

        Assert.Equal(["dbo.B", "dbo.A", "dbo.C"],
            HaritaKumeleme.Komsular(model, "dbo.B", 1));
        Assert.Equal(["dbo.B", "dbo.A", "dbo.C", "dbo.D"],
            HaritaKumeleme.Komsular(model, "dbo.B", 2));
    }

    [Fact]
    public void Radyal_tum_gorunurlere_konum_verir_ve_merkez_ortadadir()
    {
        var model = new HaritaModeli(
            [D("M"), D("K1"), D("K2"), D("K3")],
            [K("K1", "M"), K("K2", "M"), K("K3", "M")]);
        IReadOnlyList<string> gorunur = HaritaKumeleme.Komsular(model, "dbo.M", 1);

        IReadOnlyDictionary<string, Nokta> yer = HaritaKumeleme.Radyal(model, "dbo.M", gorunur);

        Assert.Equal(4, yer.Count);
        // Uydular merkezden en az halka yarıçapı kadar uzakta (üst üste binmez)
        Nokta m = yer["dbo.M"];
        foreach (string uydu in new[] { "dbo.K1", "dbo.K2", "dbo.K3" })
        {
            Nokta u = yer[uydu];
            double uzaklik = Math.Sqrt(Math.Pow(u.X - m.X, 2) + Math.Pow(u.Y - m.Y, 2));
            Assert.True(uzaklik > 200, $"{uydu} merkeze çok yakın: {uzaklik:F0}");
        }
    }

    [Fact]
    public void Kume_yerlesimi_kart_sayisi_kadar_ve_cakismasiz()
    {
        IReadOnlyList<Nokta> yerler = HaritaKumeleme.KumeYerlesimi(7);
        Assert.Equal(7, yerler.Count);
        Assert.Equal(yerler.Count, yerler.Select(n => (n.X, n.Y)).Distinct().Count());
    }

    [Fact]
    public void Tek_semali_kume_ad_semayi_tasir()
    {
        var model = new HaritaModeli(
            [D("Musteri", "satis"), D("Siparis", "satis")],
            [new HaritaKenari("satis.Siparis", "satis.Musteri", [new("MusteriId", "Id")])]);

        HaritaKumeHaritasi km = HaritaKumeleme.Kur(model);
        Assert.StartsWith("satis · ", Assert.Single(km.Kumeler).Ad);
    }
}
