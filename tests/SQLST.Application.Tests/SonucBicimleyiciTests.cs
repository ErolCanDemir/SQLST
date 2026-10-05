using System.Data;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

public class SonucBicimleyiciTests
{
    [Fact]
    public void TabloyaCevir_bos_ve_cakisan_kolon_adlarini_tekillestirir()
    {
        var set = new ResultSetData
        {
            Kolonlar = [new("Id", "int"), new("Id", "int"), new("", "int"), new("Id", "int")],
            Satirlar = [[1, 2, 3, 4]],
        };

        SonucSeti sonuc = SonucBicimleyici.TabloyaCevir(set);

        Assert.Equal(["Id", "Id_2", "adsız", "Id_3"], // m.9: yer tutucu parantezle BAŞLAYAMAZ
            sonuc.Tablo.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal(1, sonuc.SatirSayisi);
        Assert.Equal("int", sonuc.KolonTipleri["Id_3"]); // tip haritası tekilleştirilmiş adla eşleşir (FG-4.10)
    }

    [Fact]
    public void TabloyaCevir_dbnull_degerleri_tasir()
    {
        var set = new ResultSetData
        {
            Kolonlar = [new("Ad", "nvarchar")],
            Satirlar = [[DBNull.Value], ["dolu"]],
        };

        DataTable tablo = SonucBicimleyici.TabloyaCevir(set).Tablo;

        Assert.Equal(DBNull.Value, tablo.Rows[0][0]);
        Assert.Equal("dolu", tablo.Rows[1][0]);
    }

    [Fact]
    public void PanoMetni_basliklarla_ve_bassiz_tab_ayirici_uretir()
    {
        DataTable tablo = SonucBicimleyici.TabloyaCevir(new ResultSetData
        {
            Kolonlar = [new("Ad", "nvarchar"), new("Bakiye", "decimal")],
            Satirlar = [["Ali", 10.5m], [DBNull.Value, DBNull.Value]],
        }).Tablo;

        string basli = SonucBicimleyici.PanoMetni(tablo, satirlar: null, basliklarla: true);
        Assert.StartsWith("Ad\tBakiye", basli);
        Assert.Contains("NULL\tNULL", basli); // NULL hücreler görünür yazılır

        string bassiz = SonucBicimleyici.PanoMetni(tablo, satirlar: null, basliklarla: false);
        Assert.DoesNotContain("Ad\tBakiye", bassiz);
        Assert.StartsWith("Ali\t10", bassiz);

        // seçili satır alt kümesi
        string secili = SonucBicimleyici.PanoMetni(tablo, [tablo.Rows[0]], basliklarla: false);
        Assert.Equal("Ali\t10,5", secili.TrimEnd().Replace("10.5", "10,5")); // kültürden bağımsız karşılaştırma
    }

    [Fact]
    public void Mesajlar_hata_msg_bicimiyle_basta_yer_alir()
    {
        var sonuc = new QueryResult
        {
            Hata = new SqlHata("Invalid object name 'X'.", 208, 3, 16),
            Mesajlar = ["önce gelen print"],
        };

        string metin = SonucBicimleyici.MesajlariBirlestir(sonuc);

        Assert.StartsWith("Msg 208, Satır 3: Invalid object name 'X'.", metin);
        Assert.Contains("önce gelen print", metin);
    }

    [Fact]
    public void Basarili_ve_mesajsiz_sonuc_icin_standart_metin()
    {
        Assert.Equal("Komut başarıyla tamamlandı.",
            SonucBicimleyici.MesajlariBirlestir(new QueryResult { Basarili = true }));
    }

    [Fact]
    public void Satir_siniri_bilgi_bandi_eklenir()
    {
        var sonuc = new QueryResult { Basarili = true, ToplamSatir = 10, SatirSiniriAsildi = true };
        Assert.Contains("İlk 10 satır", SonucBicimleyici.MesajlariBirlestir(sonuc));
    }

    [Theory]
    [InlineData(0, 0, 1, 240, "00:00:01.24")]
    [InlineData(1, 2, 3, 0, "01:02:03.00")]
    public void Sure_ssms_bicimiyle_yazilir(int saat, int dk, int sn, int ms, string beklenen)
    {
        Assert.Equal(beklenen, SonucBicimleyici.SureFormatla(new TimeSpan(0, saat, dk, sn, ms)));
    }
    // ── v22-S3 (saha turu-3): "gereksiz yüklerden arındırma" ──────────────────────────────────
    // Kullanıcı üçüncü kez çökme bildirdi. ÖLÇÜM (200.000 satır × 10 kolon; 4 sayı + 1 tarih +
    // 5 metin): ara liste + object kolonlarla satır başına 708 bayt · ara liste olmadan 595 ·
    // GERÇEK TİPLİ kolonlarla 457 (1 GB tepede 1,5M → 2,3M satır). Kolon tipi artık okuyucudan gelir.

    [Fact]
    public void Kolonlar_okuyucunun_gercek_tipiyle_kurulur_kutulama_kalkar()
    {
        var set = new ResultSetData
        {
            Kolonlar =
            [
                new("Id", "int", typeof(int)),
                new("Ad", "nvarchar", typeof(string)),
                new("Tarih", "datetime", typeof(DateTime)),
            ],
            Satirlar = [[1, "Ali", new DateTime(2026, 8, 18)], [2, "Veli", DBNull.Value]],
        };

        SonucSeti sonuc = SonucBicimleyici.TabloyaCevir(set);

        Assert.Equal(typeof(int), sonuc.Tablo.Columns["Id"]!.DataType);
        Assert.Equal(typeof(string), sonuc.Tablo.Columns["Ad"]!.DataType);
        Assert.Equal(typeof(DateTime), sonuc.Tablo.Columns["Tarih"]!.DataType);
        Assert.Equal(2, sonuc.SatirSayisi);
        Assert.Equal(DBNull.Value, sonuc.Tablo.Rows[1]["Tarih"]); // null'lar korunur
        Assert.False(sonuc.Kesildi);
    }

    [Fact]
    public void Tip_bilinmiyorsa_object_kolona_dusulur() // eski üreticiler ClrTip vermez
    {
        var set = new ResultSetData
        {
            Kolonlar = [new("Karisik", "any")],
            Satirlar = [[1], ["metin"]],
        };

        SonucSeti sonuc = SonucBicimleyici.TabloyaCevir(set);

        Assert.Equal(typeof(object), sonuc.Tablo.Columns["Karisik"]!.DataType);
        Assert.Equal(2, sonuc.SatirSayisi);
    }

    [Fact]
    public void Semasiz_kaynakta_tip_tutmazsa_object_kolonlarla_yeniden_kurulur()
    {
        // MongoDB: kolon tipi İLK belgeden çıkarılır; sonraki belgede alan bambaşka tipte olabilir.
        // Tipli kolon bunu reddeder → gösterim ÇÖKMEZ, object kolonlara düşülür.
        var set = new ResultSetData
        {
            Kolonlar = [new("Alan", "int", typeof(int))],
            Satirlar = [[1], [new Dictionary<string, string> { ["ic"] = "belge" }]],
        };

        SonucSeti sonuc = SonucBicimleyici.TabloyaCevir(set);

        Assert.Equal(typeof(object), sonuc.Tablo.Columns["Alan"]!.DataType);
        Assert.Equal(2, sonuc.SatirSayisi); // iki satır da duruyor
    }

    [Fact]
    public void Bellek_tavani_tablo_kurulumunu_da_keser_ve_bildirir()
    {
        // Koruma eskiden yalnız OKUMA fazındaydı; DataTable okunan satırların ÜSTÜNE kurulduğu için
        // tepe bellek okuma bittikten SONRA ikiye katlanıyordu — çökme oradaydı.
        var set = new ResultSetData
        {
            Kolonlar = [new("Id", "int", typeof(int))],
            Satirlar = [.. Enumerable.Range(0, 20_000).Select(i => new object?[] { i })],
        };

        SonucSeti sonuc = SonucBicimleyici.TabloyaCevir(set, durmaliMi: () => true);

        Assert.True(sonuc.Kesildi);
        Assert.InRange(sonuc.SatirSayisi, 1, 8_192); // ilk kontrol noktasında kesildi
    }

    [Fact]
    public void Tavan_asilmazsa_tum_satirlar_yuklenir()
    {
        var set = new ResultSetData
        {
            Kolonlar = [new("Id", "int", typeof(int))],
            Satirlar = [.. Enumerable.Range(0, 10_000).Select(i => new object?[] { i })],
        };

        SonucSeti sonuc = SonucBicimleyici.TabloyaCevir(set, durmaliMi: () => false);

        Assert.False(sonuc.Kesildi);
        Assert.Equal(10_000, sonuc.SatirSayisi);
    }
}
