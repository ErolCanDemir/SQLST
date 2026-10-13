using System.Data;
using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// 📊 Sonuçtan grafik saf çekirdeği (v23-S24 S1 — docs/12-grafik-arastirma.md K4/K5): otomatik tahmin,
/// toplama (GROUP BY yazmadan), seri kolonu (uzun → geniş pivot), NULL kuralı, 200 kategori ve 50.000
/// nokta tavanları, pasta "Diğer" dilimi. WPF yok; çizim App tarafında piksel testli.
/// </summary>
public class GrafikHazirlayiciTests
{
    private static DataTable Tablo(params (string Ad, Type Tip)[] kolonlar)
    {
        var t = new DataTable();
        foreach ((string ad, Type tip) in kolonlar)
            t.Columns.Add(ad, tip);
        return t;
    }

    private static DataTable SehirYil()
    {
        DataTable t = Tablo(("Sehir", typeof(string)), ("Yil", typeof(int)), ("Tutar", typeof(decimal)), ("Adet", typeof(int)));
        t.Rows.Add("İstanbul", 2026, 485m, 1912);
        t.Rows.Add("İstanbul", 2025, 420m, 1704);
        t.Rows.Add("Ankara", 2026, 335m, 1288);
        t.Rows.Add("Ankara", 2025, 310m, 1201);
        t.Rows.Add("İzmir", 2025, 265m, 1030);
        return t;
    }

    [Fact]
    public void Tahmin_ilk_metin_kolonu_X_sayisallar_Y_tekrar_varsa_Toplam()
    {
        GrafikAyari a = GrafikHazirlayici.Tahmin(SehirYil());

        Assert.Equal("Sehir", a.XKolon);
        Assert.Equal(["Yil", "Tutar", "Adet"], a.YKolonlar);
        Assert.Equal(GrafikToplama.Toplam, a.Toplama);
        Assert.Equal(GrafikTuru.Cubuk, a.Tur);
        Assert.Null(a.SeriKolon);
    }

    [Fact]
    public void Tahmin_tarih_X_ise_cizgi_tekrar_yoksa_Yok()
    {
        DataTable t = Tablo(("Gun", typeof(DateTime)), ("Cpu", typeof(double)));
        t.Rows.Add(new DateTime(2026, 10, 1), 10.5);
        t.Rows.Add(new DateTime(2026, 10, 2), 12.0);

        GrafikAyari a = GrafikHazirlayici.Tahmin(t);

        Assert.Equal(GrafikTuru.Cizgi, a.Tur);
        Assert.Equal("Gun", a.XKolon);
        Assert.Equal(GrafikToplama.Yok, a.Toplama);
    }

    [Fact]
    public void Tahmin_sayisal_kolon_yoksa_kategori_sayimi()
    {
        DataTable t = Tablo(("Durum", typeof(string)), ("Not", typeof(string)));
        t.Rows.Add("Onaylandı", "a");
        t.Rows.Add("Onaylandı", "b");
        t.Rows.Add("İptal", "c");

        GrafikAyari a = GrafikHazirlayici.Tahmin(t);
        GrafikModeli m = GrafikHazirlayici.Kur(t, a);

        Assert.Equal(GrafikToplama.Adet, a.Toplama);
        Assert.Empty(a.YKolonlar);
        Assert.Equal(["Onaylandı", "İptal"], m.Kategoriler);
        Assert.Equal("Adet", m.Seriler[0].Ad);
        Assert.Equal([2d, 1d], m.Seriler[0].Degerler);
    }

    [Fact]
    public void Toplama_Toplam_ayni_kategoriyi_birlestirir_ilk_gorulme_sirasi_korunur()
    {
        GrafikModeli m = GrafikHazirlayici.Kur(SehirYil(),
            new GrafikAyari(GrafikTuru.Cubuk, "Sehir", ["Tutar"], null, GrafikToplama.Toplam));

        Assert.Equal(["İstanbul", "Ankara", "İzmir"], m.Kategoriler);
        Assert.Single(m.Seriler);
        Assert.Equal([905d, 645d, 265d], m.Seriler[0].Degerler);
        Assert.Null(m.Uyari);
    }

    [Theory]
    [InlineData(GrafikToplama.Ortalama, 452.5)]
    [InlineData(GrafikToplama.EnKucuk, 420)]
    [InlineData(GrafikToplama.EnBuyuk, 485)]
    [InlineData(GrafikToplama.Adet, 2)]
    public void Toplama_turleri(GrafikToplama toplama, double beklenenIstanbul)
    {
        GrafikModeli m = GrafikHazirlayici.Kur(SehirYil(),
            new GrafikAyari(GrafikTuru.Cubuk, "Sehir", ["Tutar"], null, toplama));

        Assert.Equal(beklenenIstanbul, m.Seriler[0].Degerler[0]);
    }

    [Fact]
    public void Seri_kolonu_uzun_sonucu_pivotlar_eksik_hucre_NULL()
    {
        GrafikModeli m = GrafikHazirlayici.Kur(SehirYil(),
            new GrafikAyari(GrafikTuru.Cubuk, "Sehir", ["Tutar", "Adet"], "Yil", GrafikToplama.Toplam));

        Assert.Equal(["İstanbul", "Ankara", "İzmir"], m.Kategoriler);
        Assert.Equal(["2026", "2025"], m.Seriler.Select(s => s.Ad)); // ilk görülme sırası
        Assert.Equal([485d, 335d, null], m.Seriler[0].Degerler);      // İzmir 2026 yok → NULL
        Assert.Equal([420d, 310d, 265d], m.Seriler[1].Degerler);
    }

    [Fact]
    public void Toplama_Yok_her_satir_ayri_nokta_tekrar_eden_etiketle()
    {
        GrafikModeli m = GrafikHazirlayici.Kur(SehirYil(),
            new GrafikAyari(GrafikTuru.Cubuk, "Sehir", ["Tutar"], null, GrafikToplama.Yok));

        Assert.Equal(5, m.Kategoriler.Count);
        Assert.Equal(["İstanbul", "İstanbul", "Ankara", "Ankara", "İzmir"], m.Kategoriler);
        Assert.Equal([485d, 420d, 335d, 310d, 265d], m.Seriler[0].Degerler);
    }

    [Fact]
    public void NULL_X_bos_kategori_NULL_Y_nokta_yok_tarih_ISO()
    {
        DataTable t = Tablo(("Gun", typeof(DateTime)), ("Deger", typeof(int)));
        t.Rows.Add(new DateTime(2026, 10, 1), 5);
        t.Rows.Add(DBNull.Value, 7);
        t.Rows.Add(new DateTime(2026, 10, 3, 14, 30, 0), DBNull.Value);

        GrafikModeli m = GrafikHazirlayici.Kur(t,
            new GrafikAyari(GrafikTuru.Cizgi, "Gun", ["Deger"], null, GrafikToplama.Yok));

        Assert.Equal(["2026-10-01", "(boş)", "2026-10-03 14:30:00"], m.Kategoriler);
        Assert.Equal([5d, 7d, null], m.Seriler[0].Degerler);
        Assert.Equal(2, m.NoktaSayisi);
    }

    [Fact]
    public void Eksik_secimde_bos_model_ve_aciklayici_uyari()
    {
        DataTable t = SehirYil();

        GrafikModeli xYok = GrafikHazirlayici.Kur(t, new GrafikAyari(GrafikTuru.Cubuk, null, ["Tutar"], null, GrafikToplama.Yok));
        GrafikModeli yYok = GrafikHazirlayici.Kur(t, new GrafikAyari(GrafikTuru.Cubuk, "Sehir", [], null, GrafikToplama.Yok));
        GrafikModeli kolonKayip = GrafikHazirlayici.Kur(t, new GrafikAyari(GrafikTuru.Cubuk, "Sehir", ["Silinmis"], null, GrafikToplama.Yok));

        Assert.True(xYok.BosMu); Assert.Contains("X", xYok.Uyari);
        Assert.True(yYok.BosMu); Assert.Contains("Y", yYok.Uyari);
        Assert.True(kolonKayip.BosMu); Assert.Contains("Y", kolonKayip.Uyari);
    }

    [Fact]
    public void Cubukta_200_kategori_tavani_ilk_200_ve_durust_uyari()
    {
        DataTable t = Tablo(("K", typeof(string)), ("V", typeof(int)));
        for (int i = 0; i < 1264; i++)
            t.Rows.Add("k" + i, i);

        GrafikModeli m = GrafikHazirlayici.Kur(t, new GrafikAyari(GrafikTuru.Cubuk, "K", ["V"], null, GrafikToplama.Yok));

        Assert.Equal(200, m.Kategoriler.Count);
        Assert.Equal(1264, m.ToplamKategori);
        Assert.Equal("k199", m.Kategoriler[^1]);
        Assert.Contains("+1.064", m.Uyari);
        Assert.Contains("TOP/WHERE", m.Uyari);
    }

    [Fact]
    public void Pastada_fazla_kategoriler_Diger_diliminde_toplanir_ve_tek_seri()
    {
        DataTable t = Tablo(("K", typeof(string)), ("V", typeof(int)), ("W", typeof(int)));
        for (int i = 0; i < 250; i++)
            t.Rows.Add("k" + i, 1, 9);

        GrafikModeli m = GrafikHazirlayici.Kur(t, new GrafikAyari(GrafikTuru.Pasta, "K", ["V", "W"], null, GrafikToplama.Yok));

        Assert.Single(m.Seriler);
        Assert.Equal(201, m.Kategoriler.Count);
        Assert.StartsWith("Diğer", m.Kategoriler[^1]);
        Assert.Equal(50d, m.Seriler[0].Degerler[^1]);
        Assert.Contains("Diğer", m.Uyari);
    }

    [Fact]
    public void Cizgide_50bin_ustu_nokta_kovalanir_tepe_ve_cukur_kaybolmaz()
    {
        DataTable t = Tablo(("I", typeof(int)), ("V", typeof(double)));
        for (int i = 0; i < 120_000; i++)
            t.Rows.Add(i, i == 77_777 ? 9_999d : i == 33_333 ? -5_000d : Math.Sin(i / 500.0) * 100);

        GrafikModeli m = GrafikHazirlayici.Kur(t, new GrafikAyari(GrafikTuru.Cizgi, "I", ["V"], null, GrafikToplama.Yok), 1200);

        Assert.True(m.Kategoriler.Count < 5_000, $"indirgenmedi: {m.Kategoriler.Count}");
        Assert.Contains(9_999d, m.Seriler[0].Degerler);
        Assert.Contains(-5_000d, m.Seriler[0].Degerler);
        Assert.Contains("120.000 nokta", m.Uyari);
        Assert.Equal(120_000, m.ToplamKategori);
    }

    [Fact]
    public void Deger_araligi_sifiri_her_zaman_icerir_negatif_destekli()
    {
        DataTable t = Tablo(("K", typeof(string)), ("V", typeof(int)));
        t.Rows.Add("a", 40); t.Rows.Add("b", -15); t.Rows.Add("c", 90);

        GrafikModeli m = GrafikHazirlayici.Kur(t, new GrafikAyari(GrafikTuru.Cubuk, "K", ["V"], null, GrafikToplama.Yok));

        Assert.Equal((-15d, 90d), m.DegerAraligi());
        DataTable p = Tablo(("K", typeof(string)), ("V", typeof(int)));
        p.Rows.Add("a", 40); p.Rows.Add("b", 90);
        Assert.Equal((0d, 90d), GrafikHazirlayici.Kur(p, new GrafikAyari(GrafikTuru.Cubuk, "K", ["V"], null, GrafikToplama.Yok)).DegerAraligi());
    }

    [Fact]
    public void Yigilmis_eksen_kategori_toplamini_gorur_seri_rengi_kalici()
    {
        DataTable t = SehirYil();
        var ayar = new GrafikAyari(GrafikTuru.Cubuk, "Sehir", ["Tutar"], "Yil", GrafikToplama.Toplam, Yigilmis: true);

        GrafikModeli m = GrafikHazirlayici.Kur(t, ayar);

        Assert.True(m.Yigilmis);
        Assert.Equal((0d, 905d), m.DegerAraligi());            // İstanbul 485 + 420 üst üste
        Assert.Equal([0, 1], m.Seriler.Select(s => s.Renk));   // gizlenince renk kaymasın diye kalıcı
        GrafikModeli tek = GrafikHazirlayici.Kur(t, ayar with { SeriKolon = null });
        Assert.False(tek.Yigilmis);                             // tek seride yığın anlamsız
        Assert.False(GrafikHazirlayici.Kur(t, ayar with { Tur = GrafikTuru.Cizgi }).Yigilmis);
    }

    [Fact]
    public void Sayisal_ve_kategori_kolon_siniflamasi()
    {
        DataTable t = Tablo(("Ad", typeof(string)), ("N", typeof(long)), ("D", typeof(decimal)),
            ("B", typeof(byte[])), ("G", typeof(Guid)), ("T", typeof(DateTime)), ("F", typeof(float)));

        Assert.Equal(["N", "D", "F"], GrafikHazirlayici.SayisalKolonlar(t));
        Assert.Equal(["Ad", "N", "D", "G", "T", "F"], GrafikHazirlayici.KategoriKolonlari(t)); // varbinary X olamaz
    }
}
