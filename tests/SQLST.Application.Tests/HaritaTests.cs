using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>v9-S1 — Veritabanı haritasının SAF modeli (<see cref="HaritaKurucu"/>) ve deterministik
/// yerleşimi (<see cref="HaritaYerlesim"/>). Canlı sunucu/UI gerektirmez.</summary>
public class HaritaTests
{
    private static SemaNesnesi Tablo(string sema, string ad, params SemaKolonu[] kolonlar)
        => new("db", sema, ad, SemaNesneTuru.Tablo, kolonlar, []);

    private static SemaKolonu Kol(string ad, string tip = "int", bool pk = false)
        => new(ad, tip, NullOlabilir: false, PkMi: pk);

    private static YabanciAnahtar Fk(string ks, string kt, string kk, string hs, string ht, string hk)
        => new(ks, kt, [kk], hs, ht, [hk]);

    // ── Model kurma ───────────────────────────────────────────────────────────

    [Fact]
    public void Kur_dugum_kenar_ve_PK_FK_bayraklari()
    {
        var nesneler = new[]
        {
            Tablo("dbo", "Musteri", Kol("Id", pk: true), Kol("Ad", "nvarchar(80)"), Kol("SehirId")),
            Tablo("dbo", "Sehir", Kol("Id", pk: true), Kol("Ad", "nvarchar(60)")),
        };
        var fkler = new[] { Fk("dbo", "Musteri", "SehirId", "dbo", "Sehir", "Id") };

        HaritaModeli m = HaritaKurucu.Kur(nesneler, fkler);

        Assert.Equal(2, m.Dugumler.Count);
        HaritaKenari kenar = Assert.Single(m.Kenarlar);
        Assert.Equal("dbo.Musteri", kenar.KaynakTamAd);
        Assert.Equal("dbo.Sehir", kenar.HedefTamAd);
        HaritaKenarKolonu cift = Assert.Single(kenar.Kolonlar);
        Assert.Equal("SehirId", cift.KaynakKolon);
        Assert.Equal("Id", cift.HedefKolon);

        HaritaDugumu musteri = m.Dugumler.Single(d => d.Ad == "Musteri");
        Assert.True(musteri.Kolonlar.Single(c => c.Ad == "Id").PkMi);
        Assert.True(musteri.Kolonlar.Single(c => c.Ad == "SehirId").FkMi);
        Assert.False(musteri.Kolonlar.Single(c => c.Ad == "Ad").FkMi);
    }

    [Fact]
    public void Bilesik_FK_tek_kenar_iki_kolon_cifti()
    {
        var nesneler = new[]
        {
            Tablo("dbo", "Kalem", Kol("SiparisId", pk: true), Kol("SatirNo", pk: true), Kol("UrunId")),
            Tablo("dbo", "SiparisSatir", Kol("SiparisId", pk: true), Kol("SatirNo", pk: true)),
        };
        var fkler = new[]
        {
            new YabanciAnahtar("dbo", "Kalem", ["SiparisId", "SatirNo"],
                               "dbo", "SiparisSatir", ["SiparisId", "SatirNo"]),
        };

        HaritaModeli m = HaritaKurucu.Kur(nesneler, fkler);

        HaritaKenari kenar = Assert.Single(m.Kenarlar);
        Assert.Equal(2, kenar.Kolonlar.Count);
        Assert.Equal(("SiparisId", "SiparisId"), (kenar.Kolonlar[0].KaynakKolon, kenar.Kolonlar[0].HedefKolon));
        Assert.Equal(("SatirNo", "SatirNo"), (kenar.Kolonlar[1].KaynakKolon, kenar.Kolonlar[1].HedefKolon));
    }

    [Fact]
    public void Haritada_olmayan_tabloya_FK_kenar_uretmez()
    {
        var nesneler = new[] { Tablo("dbo", "Musteri", Kol("Id", pk: true), Kol("SehirId")) };
        // Sehir şemada YOK → kenar atlanır (kaynak FkMi yine işaretlenir)
        var fkler = new[] { Fk("dbo", "Musteri", "SehirId", "dbo", "Sehir", "Id") };

        HaritaModeli m = HaritaKurucu.Kur(nesneler, fkler);

        Assert.Single(m.Dugumler);
        Assert.Empty(m.Kenarlar);
        Assert.True(m.Dugumler[0].Kolonlar.Single(c => c.Ad == "SehirId").FkMi);
    }

    [Fact]
    public void Yalniz_tablo_ve_view_dugum_olur_sp_haric()
    {
        var nesneler = new[]
        {
            Tablo("dbo", "Musteri", Kol("Id", pk: true)),
            new SemaNesnesi("db", "dbo", "vwOzet", SemaNesneTuru.View, [new SemaKolonu("Toplam", "int", true, false)], []),
            new SemaNesnesi("db", "dbo", "spSil", SemaNesneTuru.StoredProcedure, [], []),
            new SemaNesnesi("db", "dbo", "fnHesap", SemaNesneTuru.Fonksiyon, [], []),
        };

        HaritaModeli m = HaritaKurucu.Kur(nesneler, []);

        Assert.Equal(2, m.Dugumler.Count); // tablo + view
        Assert.Contains(m.Dugumler, d => d.Ad == "vwOzet");
        Assert.DoesNotContain(m.Dugumler, d => d.Ad is "spSil" or "fnHesap");
    }

    [Fact]
    public void MongoDB_koleksiyonlari_bagsiz_dugum_olur() // v9-S5
    {
        var nesneler = new[]
        {
            new SemaNesnesi("shop", "shop", "musteriler", SemaNesneTuru.Koleksiyon,
                [new SemaKolonu("_id", "objectId", false, false), new SemaKolonu("ad", "string", true, false)], []),
            new SemaNesnesi("shop", "shop", "siparisler", SemaNesneTuru.Koleksiyon, [], []),
        };

        HaritaModeli m = HaritaKurucu.Kur(nesneler, []); // MongoDB'de FK yok

        Assert.Equal(2, m.Dugumler.Count);   // koleksiyonlar kart olur
        Assert.Empty(m.Kenarlar);            // ilişki yok
        Assert.Contains(m.Dugumler, d => d.Ad == "musteriler" && d.Kolonlar.Count == 2);
    }

    // ── Yerleşim ──────────────────────────────────────────────────────────────

    private static HaritaModeli OrnekModel()
    {
        var nesneler = new[]
        {
            Tablo("dbo", "Musteri", Kol("Id", pk: true), Kol("SehirId")),
            Tablo("dbo", "Sehir", Kol("Id", pk: true)),
            Tablo("dbo", "Siparis", Kol("Id", pk: true), Kol("MusteriId")),
            Tablo("dbo", "Urun", Kol("Id", pk: true)),
            Tablo("dbo", "Kalem", Kol("Id", pk: true), Kol("SiparisId"), Kol("UrunId")),
        };
        var fkler = new[]
        {
            Fk("dbo", "Musteri", "SehirId", "dbo", "Sehir", "Id"),
            Fk("dbo", "Siparis", "MusteriId", "dbo", "Musteri", "Id"),
            Fk("dbo", "Kalem", "SiparisId", "dbo", "Siparis", "Id"),
            Fk("dbo", "Kalem", "UrunId", "dbo", "Urun", "Id"),
        };
        return HaritaKurucu.Kur(nesneler, fkler);
    }

    [Fact]
    public void Izgara_her_dugume_ayri_konum_verir()
    {
        HaritaModeli m = OrnekModel();
        IReadOnlyDictionary<string, Nokta> yer = HaritaYerlesim.Izgara(m);

        Assert.Equal(m.Dugumler.Count, yer.Count);
        Assert.All(m.Dugumler, d => Assert.True(yer.ContainsKey(d.TamAd)));
        // hiçbir iki düğüm aynı noktada değil
        Assert.Equal(yer.Count, yer.Values.Select(p => (p.X, p.Y)).Distinct().Count());
    }

    [Fact]
    public void Izgara_bos_modelde_bos_doner()
        => Assert.Empty(HaritaYerlesim.Izgara(new HaritaModeli([], [])));

    [Fact]
    public void KuvvetYonlu_deterministik_ve_gecerli_konumlar()
    {
        HaritaModeli m = OrnekModel();

        IReadOnlyDictionary<string, Nokta> a = HaritaYerlesim.KuvvetYonlu(m);
        IReadOnlyDictionary<string, Nokta> b = HaritaYerlesim.KuvvetYonlu(m);

        // Aynı girdi → birebir aynı çıktı (rastgelelik yok)
        Assert.Equal(m.Dugumler.Count, a.Count);
        foreach (HaritaDugumu d in m.Dugumler)
        {
            Assert.Equal(a[d.TamAd].X, b[d.TamAd].X, 9);
            Assert.Equal(a[d.TamAd].Y, b[d.TamAd].Y, 9);
            Assert.False(double.IsNaN(a[d.TamAd].X) || double.IsNaN(a[d.TamAd].Y));
            Assert.True(a[d.TamAd].X >= -0.001 && a[d.TamAd].Y >= -0.001); // sol-üst (0,0)'a çekildi
        }
    }

    [Fact]
    public void KuvvetYonlu_bagli_ciftler_bagsizlardan_daha_yakin()
    {
        // Sehir↔Musteri BAĞLI; Sehir↔Urun BAĞSIZ. FK çekimi bağlı çifti daha yakın tutmalı.
        HaritaModeli m = OrnekModel();
        IReadOnlyDictionary<string, Nokta> y = HaritaYerlesim.KuvvetYonlu(m);

        double Uzaklik(string a, string b)
        {
            double dx = y[a].X - y[b].X, dy = y[a].Y - y[b].Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        Assert.True(Uzaklik("dbo.Musteri", "dbo.Sehir") < Uzaklik("dbo.Sehir", "dbo.Urun"));
    }

    // ── Konum kalıcılığı serileştirmesi (v9-S3) ───────────────────────────────

    [Fact]
    public void Konum_serile_coz_gider_gelir()
    {
        var konum = new Dictionary<string, Nokta>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Musteri"] = new Nokta(120, 40),
            ["dbo.Sehir"] = new Nokta(400.5, 260.25),
        };

        IReadOnlyDictionary<string, Nokta> geri = HaritaKonumSerisi.Coz(HaritaKonumSerisi.Serile(konum));

        Assert.Equal(2, geri.Count);
        Assert.Equal(120, geri["dbo.Musteri"].X);
        Assert.Equal(260.25, geri["dbo.Sehir"].Y);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{bozuk json")]
    public void Konum_coz_bozuk_veya_bos_bos_doner(string? json)
        => Assert.Empty(HaritaKonumSerisi.Coz(json));

    // ── SVG dışa aktarma (v9-S4) ──────────────────────────────────────────────

    [Fact]
    public void Svg_dugumleri_ve_kenarlari_icerir()
    {
        HaritaModeli m = OrnekModel(); // 5 tablo, 4 FK
        string svg = HaritaSvg.Uret(m, HaritaYerlesim.Izgara(m));

        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Contains(">Musteri<", svg, StringComparison.Ordinal);
        Assert.Contains(">Sehir<", svg, StringComparison.Ordinal);
        // Geçerli XML mi (bozuk SVG yalnız açınca belli olurdu) — parse etmeli, atmamalı
        System.Xml.Linq.XDocument.Parse(svg);
        // Her kenar = 2 path (eğri + ok) → en az kenar×2 kadar <path
        int pathSayisi = System.Text.RegularExpressions.Regex.Matches(svg, "<path").Count;
        Assert.True(pathSayisi >= m.Kenarlar.Count * 2);
    }

    [Fact]
    public void Svg_ozel_karakterleri_kacirir()
    {
        var nesneler = new[]
        {
            new SemaNesnesi("db", "dbo", "A<B>", SemaNesneTuru.Tablo,
                [new SemaKolonu("x&y", "int", false, false)], []),
        };
        HaritaModeli m = HaritaKurucu.Kur(nesneler, []);

        string svg = HaritaSvg.Uret(m, HaritaYerlesim.Izgara(m));

        Assert.Contains("A&lt;B&gt;", svg, StringComparison.Ordinal);
        Assert.Contains("x&amp;y", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("A<B>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Svg_bos_modelde_gecerli_bos_tuval()
    {
        string svg = HaritaSvg.Uret(new HaritaModeli([], []), new Dictionary<string, Nokta>());
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("</svg>", svg, StringComparison.Ordinal);
    }
}
