using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 saha m.17 — kolon listesinde FK göstergesi: önbellek kurulurken FK listesi kolonlara
/// işlenir (yalnız 🔗 ikonu — hedef yazılmaz, kullanıcı 2026-08-14: bağlar sağ tık → Kolon
/// Bağları'nda zaten var). Bu sınıf saf işaretleyicinin davranışını sabitler: doğru kolon
/// işaretlenir, PK+FK birlikte görünür, FK'sız nesneler AYNI referansla döner (kopya yok),
/// eşleşme büyük/küçük harf duyarsızdır, bileşik FK tüm kaynak kolonları işaretler.
/// </summary>
public class FkIsaretleyiciTests
{
    private static SemaNesnesi Tablo(string sema, string ad, params SemaKolonu[] kolonlar)
        => new("Db", sema, ad, SemaNesneTuru.Tablo, kolonlar, []);

    private static YabanciAnahtar Fk(
        string kaynakTablo, string kaynakKolon, string hedefTablo, string hedefKolon = "Id")
        => new("dbo", kaynakTablo, [kaynakKolon], "dbo", hedefTablo, [hedefKolon]);

    [Fact]
    public void Fk_kolonu_yalniz_ikonla_isaretlenir()
    {
        SemaNesnesi siparis = Tablo("dbo", "Siparis",
            new SemaKolonu("Id", "int", false, PkMi: true),
            new SemaKolonu("MusteriId", "int", false, PkMi: false));

        IReadOnlyList<SemaNesnesi> sonuc =
            FkIsaretleyici.Isaretle([siparis], [Fk("Siparis", "MusteriId", "Musteri")]);

        SemaKolonu k = sonuc[0].Kolonlar[1];
        Assert.True(k.FkMi);
        Assert.Equal("🔗 MusteriId  ·  int", k.Gosterim); // hedef YAZILMAZ — yalnız ikon
        Assert.False(sonuc[0].Kolonlar[0].FkMi); // PK kolonu dokunulmadı
        Assert.Equal("🔑 Id  ·  int", sonuc[0].Kolonlar[0].Gosterim);
    }

    [Fact]
    public void Pk_ve_fk_ayni_kolonda_iki_glif_birden()
    {
        // Bire-bir ilişki: PK aynı zamanda FK (ör. dikey bölünmüş tablo).
        SemaNesnesi detay = Tablo("dbo", "MusteriDetay",
            new SemaKolonu("MusteriId", "int", false, PkMi: true));

        IReadOnlyList<SemaNesnesi> sonuc =
            FkIsaretleyici.Isaretle([detay], [Fk("MusteriDetay", "MusteriId", "Musteri")]);

        Assert.Equal("🔑 🔗 MusteriId  ·  int", sonuc[0].Kolonlar[0].Gosterim);
    }

    [Fact]
    public void Fksiz_nesneler_ayni_referansla_doner()
    {
        SemaNesnesi musteri = Tablo("dbo", "Musteri", new SemaKolonu("Id", "int", false, true));
        SemaNesnesi siparis = Tablo("dbo", "Siparis", new SemaKolonu("MusteriId", "int", false, false));
        var view = new SemaNesnesi("Db", "dbo", "vSiparis", SemaNesneTuru.View,
            [new SemaKolonu("MusteriId", "int", false, false)], []);

        IReadOnlyList<SemaNesnesi> sonuc = FkIsaretleyici.Isaretle(
            [musteri, siparis, view], [Fk("Siparis", "MusteriId", "Musteri")]);

        Assert.Same(musteri, sonuc[0]); // FK'sı olmayan tablo kopyalanmadı
        Assert.NotSame(siparis, sonuc[1]);
        Assert.Same(view, sonuc[2]);    // view'da FK işaretlenmez (FK tablo kavramıdır)

        // FK listesi boşsa liste AYNEN döner (Mongo/FK'sız DB — sıfır maliyet).
        Assert.Same(sonuc, FkIsaretleyici.Isaretle(sonuc, []));
    }

    [Fact]
    public void Eslesme_buyuk_kucuk_harf_duyarsiz()
    {
        SemaNesnesi siparis = Tablo("dbo", "Siparis",
            new SemaKolonu("MusteriId", "int", false, false));
        var fk = new YabanciAnahtar("DBO", "SIPARIS", ["MUSTERIID"], "dbo", "Musteri", ["Id"]);

        IReadOnlyList<SemaNesnesi> sonuc = FkIsaretleyici.Isaretle([siparis], [fk]);

        Assert.True(sonuc[0].Kolonlar[0].FkMi);
    }

    [Fact]
    public void Bilesik_fk_tum_kaynak_kolonlari_isaretler()
    {
        SemaNesnesi kalem = Tablo("dbo", "SiparisKalem",
            new SemaKolonu("SiparisYil", "int", false, false),
            new SemaKolonu("SiparisNo", "int", false, false),
            new SemaKolonu("Adet", "int", false, false));
        var fk = new YabanciAnahtar("dbo", "SiparisKalem", ["SiparisYil", "SiparisNo"],
            "dbo", "Siparis", ["Yil", "No"]);

        IReadOnlyList<SemaNesnesi> sonuc = FkIsaretleyici.Isaretle([kalem], [fk]);

        Assert.True(sonuc[0].Kolonlar[0].FkMi);
        Assert.True(sonuc[0].Kolonlar[1].FkMi);
        Assert.False(sonuc[0].Kolonlar[2].FkMi);
    }

    [Fact]
    public void Ayni_kolonda_coklu_fk_tek_isaret()
    {
        SemaNesnesi t = Tablo("dbo", "Kayit", new SemaKolonu("RefId", "int", false, false));

        IReadOnlyList<SemaNesnesi> sonuc = FkIsaretleyici.Isaretle(
            [t], [Fk("Kayit", "RefId", "Musteri"), Fk("Kayit", "RefId", "Tedarikci")]);

        // İkinci FK patlatmaz/yinelenmez — tek 🔗; bağların dökümü Kolon Bağları penceresinde.
        Assert.Equal("🔗 RefId  ·  int", sonuc[0].Kolonlar[0].Gosterim);
    }
}
