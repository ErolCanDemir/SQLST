using System.Data;
using SQLST.App;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 🧱 v22-S3 (saha turu-3; kullanıcı ÜÇÜNCÜ kez çökme bildirdi: "mantığımızı MSSQL'deki gibi yapalım,
/// gereksiz yüklerden arındıralım"). Okunan satırlar artık ara liste olmadan DOĞRUDAN grid tablosuna
/// yazılır ve kolonlar GERÇEK tipiyle kurulur.
///
/// ÖLÇÜM (200.000 satır × 10 kolon; 4 sayı + 1 tarih + 5 metin): ara liste + object kolonlar
/// <b>708 bayt/satır</b> (1 GB'da ~1,5M satır) ↔ tek kopya + tipli kolonlar <b>457 bayt/satır</b>
/// (~2,3M satır). Ayrıca tavan artık YÜKLEME SIRASINDA sorulur: eskiden koruma yalnız okuma
/// fazındaydı, oysa tepe bellek tablo kurulurken oluşuyordu — çökme oradaydı.
/// </summary>
public class GridTabloAlicisiTests
{
    private static KolonBilgisi[] Kolonlar() =>
    [
        new("Id", "int", typeof(int)),
        new("Ad", "nvarchar", typeof(string)),
        new("Tarih", "datetime", typeof(DateTime)),
    ];

    [Fact]
    public void Satirlar_dogrudan_tipli_tabloya_yazilir()
    {
        var alici = new GridTabloAlicisi(durmaliMi: () => false);

        alici.KumeBasladi(0, Kolonlar());
        Assert.True(alici.Satir([1, "Ali", new DateTime(2026, 8, 18)]));
        Assert.True(alici.Satir([2, "Veli", DBNull.Value]));
        alici.KumeBitti(0);

        SonucSeti set = Assert.Single(alici.Setler);
        Assert.Equal(typeof(int), set.Tablo.Columns["Id"]!.DataType);       // kutulama yok
        Assert.Equal(typeof(DateTime), set.Tablo.Columns["Tarih"]!.DataType);
        Assert.Equal(2, set.SatirSayisi);
        Assert.Equal(DBNull.Value, set.Tablo.Rows[1]["Tarih"]);
        Assert.False(alici.Kesildi);
    }

    [Fact]
    public void Cakisan_ve_bos_kolon_adlari_tekillestirilir()
    {
        var alici = new GridTabloAlicisi(durmaliMi: () => false);

        alici.KumeBasladi(0, [new("Id", "int", typeof(int)), new("Id", "int", typeof(int)), new("", "int", typeof(int))]);
        alici.Satir([1, 2, 3]);
        alici.KumeBitti(0);

        Assert.Equal(["Id", "Id_2", "adsız"], // m.9: yer tutucu parantezle BAŞLAYAMAZ (WPF bağlaması düşer)
            Assert.Single(alici.Setler).Tablo.Columns.Cast<DataColumn>().Select(k => k.ColumnName).ToArray());
    }

    [Fact]
    public void Semasiz_kaynakta_tip_tutmazsa_object_kolonlara_dusulur_satir_kaybolmaz()
    {
        // MongoDB: kolon tipi İLK belgeden çıkar; sonraki belgede alan bambaşka tipte olabilir.
        var alici = new GridTabloAlicisi(durmaliMi: () => false);

        alici.KumeBasladi(0, [new("Alan", "int", typeof(int))]);
        alici.Satir([1]);
        alici.Satir([new Dictionary<string, string> { ["ic"] = "belge" }]); // tipli kolon reddeder
        alici.KumeBitti(0);

        SonucSeti set = Assert.Single(alici.Setler);
        Assert.Equal(typeof(object), set.Tablo.Columns["Alan"]!.DataType);
        Assert.Equal(2, set.SatirSayisi); // iki satır da duruyor — gösterim ÇÖKMEZ
    }

    [Fact]
    public void Tavan_dolunca_okuyucuya_DUR_denir_ve_kesildi_bildirilir()
    {
        var alici = new GridTabloAlicisi(durmaliMi: () => true);
        alici.KumeBasladi(0, Kolonlar());

        bool devam = true;
        int yazilan = 0;
        for (int i = 0; i < 20_000 && devam; i++)
        {
            devam = alici.Satir([i, "x", DBNull.Value]);
            if (devam)
                yazilan++;
        }
        alici.KumeBitti(0);

        Assert.False(devam);                       // okuyucuya DUR dendi
        Assert.True(alici.Kesildi);
        Assert.InRange(yazilan, 1, 4_095);         // ilk kontrol noktasında kesildi
        Assert.Equal(yazilan, Assert.Single(alici.Setler).SatirSayisi);
    }

    [Fact]
    public void Coklu_kume_sirasiyla_biriktirilir()
    {
        var alici = new GridTabloAlicisi(durmaliMi: () => false);

        alici.KumeBasladi(0, [new("A", "int", typeof(int))]);
        alici.Satir([1]);
        alici.KumeBitti(0);
        alici.KumeBasladi(1, [new("B", "nvarchar", typeof(string))]);
        alici.Satir(["x"]);
        alici.Satir(["y"]);
        alici.KumeBitti(1);

        Assert.Equal(2, alici.Setler.Count);
        Assert.Equal("A", alici.Setler[0].Tablo.Columns[0].ColumnName);
        Assert.Equal(2, alici.Setler[1].SatirSayisi);
    }
}
