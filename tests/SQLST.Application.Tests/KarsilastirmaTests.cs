using System.Data;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

public class SonucKarsilastiriciTests
{
    private static DataTable Tablo(params (int Id, string Ad, decimal Tutar)[] satirlar)
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Ad", typeof(string));
        t.Columns.Add("Tutar", typeof(decimal));
        foreach ((int id, string ad, decimal tutar) in satirlar)
            t.Rows.Add(id, ad, tutar);
        return t;
    }

    [Fact]
    public void Yalniz_a_yalniz_b_farkli_ve_ayni_siniflanir()
    {
        DataTable a = Tablo((1, "Ali", 10), (2, "Ayşe", 20), (3, "Can", 30));
        DataTable b = Tablo((2, "Ayşe", 20), (3, "Can", 99), (4, "Ece", 40));

        (SonucKarsilastirici.KarsilastirmaSonucu? s, string? hata) =
            SonucKarsilastirici.Karsilastir(a, b, ["Id"]);

        Assert.Null(hata);
        Assert.Equal(1, s!.YalnizAAdet);   // 1
        Assert.Equal(1, s.YalnizBAdet);    // 4
        Assert.Equal(1, s.FarkliAdet);     // 3 (Tutar 30→99)
        Assert.Equal(1, s.AyniAdet);       // 2
        SonucKarsilastirici.FarkSatiri farkli = s.Satirlar.Single(f => f.Tur == FarkTuru.Farkli);
        Assert.Equal(["Tutar"], farkli.FarkliKolonlar);
        Assert.Equal(3, s.Satirlar.Count); // aynılar varsayılan dahil değil
    }

    [Fact]
    public void Coklu_anahtar_ve_aynilari_dahil_etme()
    {
        DataTable a = Tablo((1, "Ali", 10), (1, "Veli", 20));
        DataTable b = Tablo((1, "Ali", 10), (1, "Veli", 25));

        (SonucKarsilastirici.KarsilastirmaSonucu? s, string? hata) =
            SonucKarsilastirici.Karsilastir(a, b, ["Id", "Ad"], aynilariDahilEt: true);

        Assert.Null(hata);
        Assert.Equal(2, s!.Satirlar.Count);
        Assert.Contains(s.Satirlar, f => f.Tur == FarkTuru.Ayni);
        Assert.Contains(s.Satirlar, f => f.Tur == FarkTuru.Farkli && f.Anahtar.Contains("Veli"));
    }

    [Fact]
    public void Tekil_olmayan_anahtar_durustce_reddedilir()
    {
        DataTable a = Tablo((1, "Ali", 10), (1, "Veli", 20)); // Id tek başına tekil değil

        (SonucKarsilastirici.KarsilastirmaSonucu? s, string? hata) =
            SonucKarsilastirici.Karsilastir(a, Tablo((1, "Ali", 10)), ["Id"]);

        Assert.Null(s);
        Assert.Contains("tekil değil", hata);
        Assert.Contains("A", hata);
    }

    [Fact]
    public void Anahtar_iki_tarafta_da_olmali()
    {
        DataTable a = Tablo((1, "Ali", 10));
        var b = new DataTable();
        b.Columns.Add("Baska", typeof(int));

        (_, string? hata) = SonucKarsilastirici.Karsilastir(a, b, ["Id"]);
        Assert.Contains("iki tarafta da", hata);
    }

    [Fact]
    public void Null_hucreler_esit_sayilir_ve_grid_a_ok_b_gosterimi()
    {
        DataTable a = Tablo((1, "Ali", 10));
        a.Rows[0]["Ad"] = DBNull.Value;
        DataTable b = Tablo((1, "Ali", 10));
        b.Rows[0]["Ad"] = DBNull.Value;
        b.Rows[0]["Tutar"] = 55m;

        (SonucKarsilastirici.KarsilastirmaSonucu? s, _) = SonucKarsilastirici.Karsilastir(a, b, ["Id"]);
        Assert.Equal(["Tutar"], s!.Satirlar.Single().FarkliKolonlar); // NULL=NULL aynı

        DataTable grid = SonucKarsilastirici.GridTablosuKur(s, ["Id"]);
        Assert.Equal("10  →  55", grid.Rows[0]["Tutar"]);
        Assert.Equal("≠ farklı", grid.Rows[0]["Durum"]);
    }
}

public class SemaFarkAliciTests
{
    private static SemaNesnesi Tablo(string ad, params (string Kolon, string Tip)[] kolonlar)
        => new("db", "dbo", ad, SemaNesneTuru.Tablo,
            [.. kolonlar.Select(k => new SemaKolonu(k.Kolon, k.Tip, false, false))], []);

    private static SemaSnapshotu Snapshot(params SemaNesnesi[] nesneler)
        => new("srv", "db", new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc), [.. nesneler]);

    [Fact]
    public void Eklenen_silinen_degisen_ayristirilir()
    {
        SemaSnapshotu eski = Snapshot(
            Tablo("Kalan", ("A", "int")),
            Tablo("Silinen", ("X", "int")),
            Tablo("Degisen", ("K", "int")));
        SemaSnapshotu yeni = Snapshot(
            Tablo("Kalan", ("A", "int")),
            Tablo("Eklenen", ("Y", "int")),
            Tablo("Degisen", ("K", "bigint")));

        IReadOnlyList<SemaFarkAlici.Fark> farklar = SemaFarkAlici.Karsilastir(eski, yeni);

        Assert.Equal(3, farklar.Count);
        Assert.Contains(farklar, f => f.Tur == SemaFarkAlici.DegisimTuru.Eklendi && f.TamAd == "dbo.Eklenen");
        Assert.Contains(farklar, f => f.Tur == SemaFarkAlici.DegisimTuru.Silindi && f.TamAd == "dbo.Silinen");
        SemaFarkAlici.Fark degisen = farklar.Single(f => f.Tur == SemaFarkAlici.DegisimTuru.Degisti);
        Assert.Contains("int", degisen.Detay);
        Assert.Contains("bigint", degisen.Detay);
    }

    [Fact]
    public void Ayni_semada_fark_yok()
    {
        SemaSnapshotu s1 = Snapshot(Tablo("T", ("A", "int")));
        SemaSnapshotu s2 = Snapshot(Tablo("T", ("A", "int")));
        Assert.Empty(SemaFarkAlici.Karsilastir(s1, s2));
    }

    [Fact]
    public void Json_gidis_donus_kayipsiz_ve_bozuk_dosya_null()
    {
        SemaSnapshotu snapshot = Snapshot(Tablo("Musteri", ("Ad", "nvarchar(50)")));

        string json = SemaSnapshotYazici.Yaz(snapshot);
        SemaSnapshotu? geri = SemaSnapshotYazici.Oku(json);

        Assert.NotNull(geri);
        Assert.Equal(snapshot.Veritabani, geri!.Veritabani);
        Assert.Equal("Musteri", geri.Nesneler[0].Ad);
        Assert.Equal("nvarchar(50)", geri.Nesneler[0].Kolonlar[0].Tip);
        Assert.Contains("Musteri", json); // Türkçe/ad kaçışsız okunur
        Assert.Null(SemaSnapshotYazici.Oku("{bozuk"));
    }
}

public class HtmlRaporYaziciTests
{
    [Fact]
    public void Rapor_meta_sorgu_ve_tabloyu_icerir_html_kacirir()
    {
        var t = new DataTable();
        t.Columns.Add("Ad<script>", typeof(object));
        t.Rows.Add("Gümüş & <b>oğlu</b>");
        t.Rows.Add(DBNull.Value);

        string html = HtmlRaporYazici.Yaz(
            @"(localdb)\X", "LstQmsDb", "SELECT * FROM T WHERE a < 5", "00:00:01.23",
            [("Sonuç 1", t)], new DateTime(2026, 7, 17, 14, 0, 0));

        Assert.Contains("LstQmsDb", html);
        Assert.Contains("17.07.2026 14:00", html);
        Assert.Contains("SELECT * FROM T WHERE a &lt; 5", html);   // sorgu kaçırıldı
        Assert.Contains("Ad&lt;script&gt;", html);                  // başlık kaçırıldı
        Assert.Contains("Gümüş &amp; &lt;b&gt;oğlu&lt;/b&gt;", html); // hücre kaçırıldı
        Assert.Contains("class=\"null\"", html);                    // NULL stili
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Satir_tavani_asiminda_durust_not()
    {
        var t = new DataTable();
        t.Columns.Add("X", typeof(object));
        for (int i = 0; i < HtmlRaporYazici.SatirTavani + 5; i++)
            t.Rows.Add(i);

        string html = HtmlRaporYazici.Yaz("s", null, "SELECT 1", "0", [("S", t)], DateTime.Now);

        Assert.Contains("İlk 1.000 satır gösteriliyor", html);
        Assert.Contains("1.005", html); // toplam
    }
}
