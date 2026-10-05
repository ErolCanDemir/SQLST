using System.Data;
using System.Globalization;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S5 · Toplu grid işlemleri. Bu sınıf yalnız bekleyen DataTable'ı değiştirir;
/// DML üretimi ve tek işlemli uygulama mevcut raylardır ve değişmemiştir — yani
/// buradaki her değişiklik de PK'lı WHERE ile ve Show Script onayından sonra gider.
/// </summary>
public class GridTopluIslemTests
{
    private static DuzenlemeKolonu Kolon(
        string ad, string tip = "int", bool nullOlabilir = true, bool pk = false,
        bool identity = false, bool computed = false, bool rowversion = false)
        => new(ad, tip, tip, nullOlabilir, pk, identity, computed, rowversion, KiyasGuvenliMi: true);

    private static DuzenlemeMetasi Meta(params DuzenlemeKolonu[] kolonlar)
        => new("db", "dbo", "T", kolonlar);

    private static DataTable Tablo((string Ad, Type Tip)[] kolonlar, params object?[][] satirlar)
    {
        var t = new DataTable();
        foreach ((string ad, Type tip) in kolonlar)
            t.Columns.Add(ad, tip);
        foreach (object?[] s in satirlar)
            t.Rows.Add(s);
        t.AcceptChanges();
        return t;
    }

    // --- NULL atama ---

    [Fact]
    public void NullAta_secili_hucreleri_null_yapar()
    {
        DataTable t = Tablo([("Id", typeof(int)), ("Ad", typeof(string))], [1, "a"], [2, "b"]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false), Kolon("Ad", "nvarchar"));

        TopluSonuc sonuc = GridTopluIslem.NullAta(t, meta, [(0, "Ad"), (1, "Ad")]);

        Assert.Equal(2, sonuc.Uygulanan);
        Assert.Empty(sonuc.Atlananlar);
        Assert.Equal(DBNull.Value, t.Rows[0]["Ad"]);
        Assert.Equal(DBNull.Value, t.Rows[1]["Ad"]);
    }

    [Fact]
    public void NullAta_NOT_NULL_kolonu_ATLAR_ve_nedenini_soyler()
    {
        DataTable t = Tablo([("Id", typeof(int))], [1]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false));

        TopluSonuc sonuc = GridTopluIslem.NullAta(t, meta, [(0, "Id")]);

        Assert.Equal(0, sonuc.Uygulanan);
        Assert.Contains(sonuc.Atlananlar, a => a.Contains("NULL kabul etmiyor"));
        Assert.Equal(1, t.Rows[0]["Id"]);   // değer korunmalı
    }

    [Fact]
    public void NullAta_identity_ve_computed_kolonlari_ATLAR()
    {
        DataTable t = Tablo([("Id", typeof(int)), ("Hesap", typeof(int))], [1, 5]);
        DuzenlemeMetasi meta = Meta(
            Kolon("Id", pk: true, identity: true), Kolon("Hesap", computed: true));

        TopluSonuc sonuc = GridTopluIslem.NullAta(t, meta, [(0, "Id"), (0, "Hesap")]);

        Assert.Equal(0, sonuc.Uygulanan);
        Assert.All(sonuc.Atlananlar, a => Assert.Contains("sunucu tarafından üretilir", a));
    }

    // --- Yapıştırma ---

    [Fact]
    public void Yapistir_cok_satirli_bloku_yerlestirir()
    {
        DataTable t = Tablo([("Id", typeof(int)), ("Ad", typeof(string))], [1, "a"], [2, "b"]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false), Kolon("Ad", "nvarchar"));

        TopluSonuc sonuc = GridTopluIslem.Yapistir(
            t, meta, "10\tx\n20\ty", baslangicSatir: 0, baslangicKolon: 0, yeniSatirEklenebilir: false);

        Assert.Equal(4, sonuc.Uygulanan);
        Assert.Equal(10, t.Rows[0]["Id"]);
        Assert.Equal("y", t.Rows[1]["Ad"]);
    }

    [Fact]
    public void Yapistir_NULL_metnini_DBNull_yapar()
    {
        // Kopyalama tarafı NULL hücreleri "NULL" yazar → gidiş-dönüş korunmalı
        DataTable t = Tablo([("Ad", typeof(string))], ["a"]);
        DuzenlemeMetasi meta = Meta(Kolon("Ad", "nvarchar"));

        GridTopluIslem.Yapistir(t, meta, "NULL", 0, 0, false);

        Assert.Equal(DBNull.Value, t.Rows[0]["Ad"]);
    }

    [Fact]
    public void Yapistir_eksik_satirlari_EKLER_izin_varsa()
    {
        DataTable t = Tablo([("Id", typeof(int))], [1]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false));

        TopluSonuc sonuc = GridTopluIslem.Yapistir(t, meta, "1\n2\n3", 0, 0, yeniSatirEklenebilir: true);

        Assert.Equal(2, sonuc.EklenenSatir);
        Assert.Equal(3, t.Rows.Count);
        Assert.Equal(3, t.Rows[2]["Id"]);
    }

    [Fact]
    public void Yapistir_izin_yoksa_tasan_satirlari_ATLAR()
    {
        DataTable t = Tablo([("Id", typeof(int))], [1]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false));

        TopluSonuc sonuc = GridTopluIslem.Yapistir(t, meta, "1\n2\n3", 0, 0, yeniSatirEklenebilir: false);

        Assert.Equal(1, t.Rows.Count);
        Assert.Equal(0, sonuc.EklenenSatir);
        Assert.Contains(sonuc.Atlananlar, a => a.Contains("yeni satır eklenemiyor"));
    }

    [Fact]
    public void Yapistir_cevrilemeyen_degeri_ATLAR_digerlerini_uygular()
    {
        DataTable t = Tablo([("Id", typeof(int)), ("Ad", typeof(string))], [1, "a"]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false), Kolon("Ad", "nvarchar"));

        TopluSonuc sonuc = GridTopluIslem.Yapistir(t, meta, "abc\tyeni", 0, 0, false);

        Assert.Equal(1, sonuc.Uygulanan);              // yalnız Ad uygulandı
        Assert.Equal(1, t.Rows[0]["Id"]);              // Id bozulmadı
        Assert.Equal("yeni", t.Rows[0]["Ad"]);
        Assert.Contains(sonuc.Atlananlar, a => a.Contains("çevrilemedi"));
    }

    [Fact]
    public void Yapistir_sagdan_tasan_kolonlari_ATLAR()
    {
        DataTable t = Tablo([("Id", typeof(int))], [1]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false));

        TopluSonuc sonuc = GridTopluIslem.Yapistir(t, meta, "5\t9\t9", 0, 0, false);

        Assert.Equal(1, sonuc.Uygulanan);
        Assert.Contains(sonuc.Atlananlar, a => a.Contains("sağına taşan"));
    }

    [Fact]
    public void Yapistir_sondaki_bos_satir_YENI_SATIR_URETMEZ()
    {
        // Kopyalama AppendLine kullanır → metin daima "\n" ile biter. Bu yok sayılmazsa
        // her yapıştırmada boş bir satır eklenirdi.
        DataTable t = Tablo([("Id", typeof(int))], [1]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false));

        TopluSonuc sonuc = GridTopluIslem.Yapistir(t, meta, "7\r\n", 0, 0, yeniSatirEklenebilir: true);

        Assert.Equal(0, sonuc.EklenenSatir);
        Assert.Equal(1, t.Rows.Count);
        Assert.Equal(7, t.Rows[0]["Id"]);
    }

    [Fact]
    public void Yapistir_TR_kulturunde_ondalik_gidis_donusu_korur()
    {
        CultureInfo onceki = CultureInfo.CurrentCulture;
        try
        {
            // Kopyalama ToString() ile GEÇERLİ kültürde yazar: tr-TR'de 3,14
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            DataTable t = Tablo([("Tutar", typeof(decimal))], [1.0m]);
            DuzenlemeMetasi meta = Meta(Kolon("Tutar", "decimal"));

            GridTopluIslem.Yapistir(t, meta, "3,14", 0, 0, false);
            Assert.Equal(3.14m, t.Rows[0]["Tutar"]);

            // Kültür belirsizliği ÇÖZÜMÜ (inceleme 2026-07-30 bekleyeni): binlik ayraç parse'ta
            // KAPALI (GorselSorguUretici/VeriArayici ile aynı kural). Eskiden tr-TR'de "." binlik
            // sayıldığından İngilizce kaynaktan (Excel/CSV) gelen "2.5" SESSİZCE 25 olurdu; artık
            // tr'de çözülemez, invariant yedeği devreye girer ve 2.5 okunur. Bedeli bilinçli:
            // binlik ayraçlı dış kaynak ("2.500") desteklenmez — kopyalama tarafımız binlik
            // yazmadığından gidiş-dönüş etkilenmez.
            GridTopluIslem.Yapistir(t, meta, "2.5", 0, 0, false);
            Assert.Equal(2.5m, t.Rows[0]["Tutar"]);

            // Yerel biçim önceliği bozulmadı: virgül tr'de ondalıktır, "1,5" 1.5 okunur.
            GridTopluIslem.Yapistir(t, meta, "1,5", 0, 0, false);
            Assert.Equal(1.5m, t.Rows[0]["Tutar"]);

            // Invariant yedeği yalnız geçerli kültür GERÇEKTEN çözemeyince işe yarar.
            var tarihTablo = Tablo([("Zaman", typeof(DateTime))], [DateTime.Today]);
            DuzenlemeMetasi tarihMeta = Meta(Kolon("Zaman", "datetime"));
            GridTopluIslem.Yapistir(tarihTablo, tarihMeta, "2026-07-19", 0, 0, false);
            Assert.Equal(new DateTime(2026, 7, 19), tarihTablo.Rows[0]["Zaman"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = onceki;
        }
    }

    // --- Kolon bazlı toplu güncelleme ---

    [Fact]
    public void KolonaAta_tum_yuklu_satirlara_yazar()
    {
        DataTable t = Tablo([("Id", typeof(int)), ("Durum", typeof(string))],
            [1, "eski"], [2, "eski"], [3, "eski"]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, nullOlabilir: false), Kolon("Durum", "nvarchar"));

        TopluSonuc sonuc = GridTopluIslem.KolonaAta(t, meta, "Durum", "yeni");

        Assert.Equal(3, sonuc.Uygulanan);
        Assert.All(t.Rows.Cast<DataRow>(), r => Assert.Equal("yeni", r["Durum"]));
    }

    [Fact]
    public void KolonaAta_null_deger_NULL_atar()
    {
        DataTable t = Tablo([("Ad", typeof(string))], ["a"], ["b"]);
        DuzenlemeMetasi meta = Meta(Kolon("Ad", "nvarchar"));

        GridTopluIslem.KolonaAta(t, meta, "Ad", metinDeger: null);

        Assert.All(t.Rows.Cast<DataRow>(), r => Assert.Equal(DBNull.Value, r["Ad"]));
    }

    [Fact]
    public void KolonaAta_yalniz_verilen_satirlara_yazar()
    {
        DataTable t = Tablo([("Ad", typeof(string))], ["a"], ["b"], ["c"]);
        DuzenlemeMetasi meta = Meta(Kolon("Ad", "nvarchar"));

        GridTopluIslem.KolonaAta(t, meta, "Ad", "x", satirlar: [0, 2]);

        Assert.Equal("x", t.Rows[0]["Ad"]);
        Assert.Equal("b", t.Rows[1]["Ad"]);   // dokunulmadı
        Assert.Equal("x", t.Rows[2]["Ad"]);
    }

    [Fact]
    public void KolonaAta_yazilamaz_kolonu_reddeder()
    {
        DataTable t = Tablo([("Id", typeof(int))], [1]);
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true, identity: true));

        TopluSonuc sonuc = GridTopluIslem.KolonaAta(t, meta, "Id", "9");

        Assert.Equal(0, sonuc.Uygulanan);
        Assert.Equal(1, t.Rows[0]["Id"]);
    }

    [Fact]
    public void Toplu_islemler_satirlari_DEGISTI_olarak_isaretler()
    {
        // DmlUretici bekleyen kümeyi DataRow durumlarından okur; işaretlenmezse
        // toplu değişiklikler DML'e HİÇ girmezdi.
        DataTable t = Tablo([("Ad", typeof(string))], ["a"]);
        DuzenlemeMetasi meta = Meta(Kolon("Ad", "nvarchar"));

        GridTopluIslem.KolonaAta(t, meta, "Ad", "yeni");

        Assert.Equal(DataRowState.Modified, t.Rows[0].RowState);
        Assert.Equal("a", t.Rows[0]["Ad", DataRowVersion.Original]);
    }
}
