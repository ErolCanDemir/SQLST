using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>v9-S2 — Harita sekmesi VM'i: model → kart/kenar görünümleri, hover odağı, bezier geometri
/// ve otomatik yerleştirme. UI/DB gerektirmez (SAF model + WPF geometri nesneleri).</summary>
public class HaritaSekmesiTests
{
    private static HaritaModeli Model()
    {
        var nesneler = new[]
        {
            new SemaNesnesi("db", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "int", false, true), new SemaKolonu("SehirId", "int", false, false)], []),
            new SemaNesnesi("db", "dbo", "Sehir", SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "int", false, true)], []),
            new SemaNesnesi("db", "dbo", "Urun", SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "int", false, true)], []),
        };
        var fkler = new[] { new YabanciAnahtar("dbo", "Musteri", ["SehirId"], "dbo", "Sehir", ["Id"]) };
        return HaritaKurucu.Kur(nesneler, fkler);
    }

    [Fact]
    public void Yukle_kart_ve_kenar_gorunumlerini_kurar()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(Model());

        Assert.Equal(3, vm.Dugumler.Count);
        HaritaKenarGorunumu kenar = Assert.Single(vm.Kenarlar);
        Assert.Equal("dbo.Musteri", kenar.Kaynak.TamAd);
        Assert.Equal("dbo.Sehir", kenar.Hedef.TamAd);
        Assert.NotNull(kenar.Cizim); // bezier + ok geometrisi kuruldu
        // Izgara konumları ayrık
        Assert.Equal(3, vm.Dugumler.Select(d => (d.X, d.Y)).Distinct().Count());
    }

    [Fact]
    public void Odakla_komsulari_vurgular_gerisini_soluklastirir()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(Model());
        HaritaDugumGorunumu musteri = vm.Dugumler.Single(d => d.Ad == "Musteri");
        HaritaDugumGorunumu sehir = vm.Dugumler.Single(d => d.Ad == "Sehir");
        HaritaDugumGorunumu urun = vm.Dugumler.Single(d => d.Ad == "Urun");

        vm.Odakla(musteri);

        Assert.True(musteri.Vurgulu);
        Assert.True(sehir.Vurgulu);       // komşu (FK)
        Assert.False(sehir.Soluk);
        Assert.True(urun.Soluk);          // ilişkisiz → soluk
        Assert.False(urun.Vurgulu);
        Assert.True(Assert.Single(vm.Kenarlar).Vurgulu);

        vm.OdakTemizle();
        Assert.All(vm.Dugumler, d => Assert.False(d.Vurgulu || d.Soluk));
        Assert.All(vm.Kenarlar, k => Assert.False(k.Vurgulu || k.Soluk));
    }

    [Fact]
    public void Kart_tasininca_kenar_geometrisi_yenilenir()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(Model());
        HaritaKenarGorunumu kenar = vm.Kenarlar[0];
        System.Windows.Media.Geometry? once = kenar.Cizim;

        vm.Dugumler.Single(d => d.Ad == "Musteri").X += 300; // sürükleme benzeri

        Assert.NotSame(once, kenar.Cizim); // X değişince Guncelle() yeni geometri üretti
    }

    [Fact]
    public void OtomatikYerlestir_konumlari_degistirir_ve_atmaz()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(Model());
        var oncekiler = vm.Dugumler.ToDictionary(d => d.TamAd, d => (d.X, d.Y));

        vm.OtomatikYerlestirCommand.Execute(null);

        // En az bir düğüm yer değiştirdi (kuvvet-yönlü ızgaradan farklı sonuç verir) ve konumlar geçerli
        Assert.Contains(vm.Dugumler, d => (d.X, d.Y) != oncekiler[d.TamAd]);
        Assert.All(vm.Dugumler, d => Assert.False(double.IsNaN(d.X) || double.IsNaN(d.Y)));
    }

    // ── S3: kalıcılık + arama + köprü ────────────────────────────────────────

    [Fact]
    public void Yukle_kayitli_konumlari_izgaraya_tercih_eder()
    {
        var vm = new HaritaSekmesiViewModel();
        var kayitli = new Dictionary<string, Nokta>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Musteri"] = new Nokta(777, 555),
        };

        vm.Yukle(Model(), kayitli, "demo");

        HaritaDugumGorunumu m = vm.Dugumler.Single(d => d.Ad == "Musteri");
        Assert.Equal(777, m.X);
        Assert.Equal(555, m.Y);
    }

    [Fact]
    public void Arama_esleseni_vurgular_temizlenince_sifirlanir()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(Model());

        vm.Arama = "sehir"; // büyük/küçük harf duyarsız
        Assert.True(vm.Dugumler.Single(d => d.Ad == "Sehir").Vurgulu);
        Assert.True(vm.Dugumler.Single(d => d.Ad == "Musteri").Soluk);

        vm.Arama = "";
        Assert.All(vm.Dugumler, d => Assert.False(d.Vurgulu || d.Soluk));
    }

    [Fact]
    public void Secim_ve_gorsele_gonder_callbacki_cagirip_secimi_temizler()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(Model());
        IReadOnlyList<string>? gonderilen = null;
        vm.GorselleGonder = liste => gonderilen = liste;

        vm.SecimiDegistir(vm.Dugumler.Single(d => d.Ad == "Musteri"));
        Assert.True(vm.SecililerVar);
        Assert.Equal(1, vm.SeciliSayisi);

        vm.GorseleGonderCommand.Execute(null);

        Assert.Equal(new[] { "dbo.Musteri" }, gonderilen);
        Assert.False(vm.SecililerVar); // gönderince seçim temizlenir
    }

    [Fact]
    public void Konum_kaydet_otoyerlestir_ve_kaydetistensinde_tetiklenir()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(Model());
        int cagri = 0;
        vm.KonumlariKaydetIstendi = () => cagri++;

        vm.OtomatikYerlestirCommand.Execute(null); // Konumla → kaydet ister
        vm.KaydetIstensin();                        // sürükleme sonu benzeri

        Assert.True(cagri >= 2);
    }

    [Fact]
    public void SvgUret_dolu_haritada_svg_metni_verir()
    {
        var vm = new HaritaSekmesiViewModel();
        vm.Yukle(Model());

        Assert.True(vm.DisaAktarilabilir);
        string svg = vm.SvgUret(koyu: true);
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Contains(">Musteri<", svg, StringComparison.Ordinal);
    }
}
