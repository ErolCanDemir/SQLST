using SQLST.Application;
using SQLST.Contracts;
using Xunit;

namespace SQLST.Application.Tests;

public class LookupCozumleyiciTests
{
    private static SemaKolonu K(string ad, string tip = "int", bool pk = false) => new(ad, tip, false, pk);

    private static SemaNesnesi T(string sema, string ad, params SemaKolonu[] kolonlar) =>
        new("db", sema, ad, SemaNesneTuru.Tablo, kolonlar, []);

    private static YabanciAnahtar FK(string kt, string kk, string ht, string hk, string sema = "dbo") =>
        new(sema, kt, [kk], sema, ht, [hk]);

    [Fact]
    public void Fk_kolon_lookup_tabloya_cozulur()
    {
        SemaNesnesi siparis = T("dbo", "Siparis", K("Id", "int", true), K("DurumId"), K("Tutar", "decimal"));
        SemaNesnesi durum = T("dbo", "Durum", K("Id", "int", true), K("Ad", "nvarchar(50)"));
        SemaNesnesi fatura = T("dbo", "Fatura", K("Id", "int", true), K("DurumId"));
        YabanciAnahtar[] fkler = [FK("Siparis", "DurumId", "Durum", "Id"), FK("Fatura", "DurumId", "Durum", "Id")];

        LookupCozumu c = LookupCozumleyici.Coz(siparis, "DurumId", fkler, [siparis, durum, fatura]);

        Assert.True(c.FkVar);
        Assert.Equal("Durum", c.HedefTablo);
        Assert.Equal("Id", c.AnahtarKolon);
        Assert.Equal("Ad", c.AciklamaKolon);
        Assert.True(c.TanimTablosu); // 2 gelen FK + açıklama kolonu
    }

    [Fact]
    public void Fk_olmayan_kolon_reddedilir()
    {
        SemaNesnesi siparis = T("dbo", "Siparis", K("Id", "int", true), K("DurumId"), K("Tutar", "decimal"));
        LookupCozumu c = LookupCozumleyici.Coz(siparis, "Tutar",
            [FK("Siparis", "DurumId", "Durum", "Id")], [siparis]);

        Assert.False(c.FkVar);
        Assert.Null(c.AciklamaKolon);
    }

    [Fact]
    public void Aciklama_kolonu_yoksa_tanim_degil()
    {
        SemaNesnesi x = T("dbo", "X", K("KodId"));
        SemaNesnesi kodlar = T("dbo", "Kodlar", K("Id", "int", true), K("Sira", "int")); // metin kolon yok
        LookupCozumu c = LookupCozumleyici.Coz(x, "KodId", [FK("X", "KodId", "Kodlar", "Id")], [x, kodlar]);

        Assert.True(c.FkVar);
        Assert.Null(c.AciklamaKolon);
        Assert.False(c.TanimTablosu);
    }

    [Fact]
    public void Turkce_diakritik_aciklama_kolonu_eslesir()
    {
        SemaNesnesi sip = T("dbo", "Sip", K("DurumId"));
        SemaNesnesi durum = T("dbo", "Durum", K("Id", "int", true), K("Açıklama", "nvarchar(100)"));
        LookupCozumu c = LookupCozumleyici.Coz(sip, "DurumId", [FK("Sip", "DurumId", "Durum", "Id")], [sip, durum]);

        Assert.Equal("Açıklama", c.AciklamaKolon); // "aciklama" adayına diakritik-duyarsız eşleşti
    }

    [Fact]
    public void Ad_kolonu_koddan_oncelikli()
    {
        SemaNesnesi sip = T("dbo", "Sip", K("TipId"));
        SemaNesnesi tip = T("dbo", "Tip", K("Id", "int", true), K("Kod", "varchar(10)"), K("Ad", "nvarchar(50)"));
        LookupCozumu c = LookupCozumleyici.Coz(sip, "TipId", [FK("Sip", "TipId", "Tip", "Id")], [sip, tip]);

        Assert.Equal("Ad", c.AciklamaKolon);
    }

    [Fact]
    public void Tek_gelen_fk_ama_az_kolon_tanim_sayilir()
    {
        SemaNesnesi sip = T("dbo", "Sip", K("TipId"));
        SemaNesnesi tip = T("dbo", "Tip", K("Id", "int", true), K("Ad", "nvarchar(50)")); // 2 kolon ≤5
        LookupCozumu c = LookupCozumleyici.Coz(sip, "TipId", [FK("Sip", "TipId", "Tip", "Id")], [sip, tip]);

        Assert.True(c.TanimTablosu); // gelen FK=1 ama ≤5 kolon
    }

    [Fact]
    public void Hedef_tablo_semada_yoksa_referans_bildirilir()
    {
        SemaNesnesi siparis = T("dbo", "Siparis", K("DurumId"));
        LookupCozumu c = LookupCozumleyici.Coz(siparis, "DurumId",
            [FK("Siparis", "DurumId", "Durum", "Id")], [siparis]); // Durum listede yok

        Assert.True(c.FkVar);
        Assert.Equal("Durum", c.HedefTablo);
        Assert.Null(c.AciklamaKolon);
        Assert.False(c.TanimTablosu);
    }

    // v22-S1 (saha turu-2 m.4 "hücre üzerine gelince lookup ipucu çalışmıyor"): sorgudan çıkarılan
    // tablo adı ile şema önbelleğindeki nesneyi eşleme kuralı — ipucunun İLK adımı buydu.

    [Fact]
    public void Kaynak_tablo_sema_ve_adla_bulunur()
    {
        SemaNesnesi mersis = T("Mersis", "Talep", K("Id", "int", true));
        SemaNesnesi dbo = T("dbo", "Talep", K("Id", "int", true));

        Assert.Same(mersis, LookupCozumleyici.KaynakTabloBul([mersis, dbo], "Mersis.Talep"));
        Assert.Same(dbo, LookupCozumleyici.KaynakTabloBul([mersis, dbo], "dbo.Talep"));
    }

    [Fact]
    public void Sema_eslesmezse_tek_ad_eslesmesi_kabul_edilir()
    {
        // Sorgu 'Mersis.Talep' yazmış ama önbellekte tablo dbo şemasında (ad = veritabanı olabilir):
        // ADLA TEK eşleşme varsa kabul edilir — özellik eskiden burada sessizce vazgeçiyordu.
        SemaNesnesi dbo = T("dbo", "Talep", K("Id", "int", true));

        Assert.Same(dbo, LookupCozumleyici.KaynakTabloBul([dbo], "Mersis.Talep"));
        Assert.Same(dbo, LookupCozumleyici.KaynakTabloBul([dbo], "Talep"));
    }

    [Fact]
    public void Ayni_ad_birden_cok_semada_ise_belirsiz_sayilir()
    {
        SemaNesnesi a = T("Satis", "Talep", K("Id", "int", true));
        SemaNesnesi b = T("Uretim", "Talep", K("Id", "int", true));

        // Yanlış tanım tablosu göstermek yerine null (belirsizlik sessiz yanlıştan iyidir).
        Assert.Null(LookupCozumleyici.KaynakTabloBul([a, b], "Mersis.Talep"));
        Assert.Null(LookupCozumleyici.KaynakTabloBul([a, b], "Yok.Tablo"));
    }
}
