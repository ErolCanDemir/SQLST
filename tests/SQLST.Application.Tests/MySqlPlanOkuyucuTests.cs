using System.Globalization;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S1c: MySQL/MariaDB <c>EXPLAIN FORMAT=JSON</c> çözümleme.
///
/// <b>DÜRÜST SINIR — fixture'lar ÖLÇÜM DEĞİL.</b> MSSQL ve PostgreSQL okuyucularında
/// fixture sayıları canlı sunucudan ölçülmüştü; burada kalıcı bir MySQL/MariaDB test
/// sunucusu olmadığından fixture'lar biçim bilgisine dayanıyor. Bu testler
/// "çözümleyici beklenen yapıyı doğru okuyor mu" sorusunu yanıtlar; "sunucu gerçekten
/// bu yapıyı üretiyor mu" sorusunu YANITLAMAZ — o, en sona bırakılan canlı doğrulama
/// borcunun parçasıdır (roadmap: v4 borcu, kapsamı v5 plan okuyucularını da içerir).
/// </summary>
public class MySqlPlanOkuyucuTests
{
    /// <summary>MySQL 8 şekli: sıralama → gruplama → nested_loop → iki tablo; maliyetler METİN.</summary>
    private const string MySqlJson = """
        {
          "query_block": {
            "select_id": 1,
            "cost_info": { "query_cost": "100.00" },
            "ordering_operation": {
              "using_filesort": true,
              "grouping_operation": {
                "using_temporary_table": true,
                "nested_loop": [
                  {
                    "table": {
                      "table_name": "musteri",
                      "access_type": "ALL",
                      "possible_keys": ["ix_grup"],
                      "rows_examined_per_scan": 5000,
                      "rows_produced_per_join": 5000,
                      "filtered": "100.00",
                      "cost_info": { "read_cost": "20.00", "eval_cost": "10.00", "prefix_cost": "30.00" }
                    }
                  },
                  {
                    "table": {
                      "table_name": "siparis",
                      "access_type": "ref",
                      "key": "ix_musteri_id",
                      "rows_examined_per_scan": 2,
                      "rows_produced_per_join": 10000,
                      "filtered": "100.00",
                      "cost_info": { "read_cost": "50.00", "eval_cost": "20.00", "prefix_cost": "100.00" }
                    }
                  }
                ]
              }
            }
          }
        }
        """;

    /// <summary>MariaDB şekli: cost_info YOK, satır sayısı "rows".</summary>
    private const string MariaDbJson = """
        {
          "query_block": {
            "select_id": 1,
            "table": {
              "table_name": "musteri",
              "access_type": "ALL",
              "rows": 5000,
              "filtered": 100,
              "attached_condition": "musteri.bakiye > 100"
            }
          }
        }
        """;

    [Fact]
    public void Ic_ice_ISIMLI_bolumlerden_agac_kurulur()
    {
        // MySQL ağacı "çocuklar dizisi" ile değil, iç içe İSİMLİ bölümlerle kurulur:
        // query_block → ordering_operation → grouping_operation → nested_loop[] → table
        SorguPlani plan = MySqlPlanOkuyucu.Coz(MySqlJson, gercek: false);

        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        Assert.Equal("Sıralama", kok.Islem);

        PlanDugumu gruplama = Assert.Single(kok.Cocuklar);
        Assert.StartsWith("Gruplama", gruplama.Islem);

        PlanDugumu dongu = Assert.Single(gruplama.Cocuklar);
        Assert.Equal("Nested loop", dongu.Islem);
        Assert.Equal(2, dongu.Cocuklar.Count);
    }

    [Fact]
    public void Maliyetler_METIN_olarak_gelir_ve_okunur()
    {
        // TUZAK: MySQL maliyetleri JSON'da string'dir ("read_cost": "20.00").
        // Yalnız Number bekleyen bir okuyucu TÜM maliyetleri sessizce 0 görürdü.
        SorguPlani plan = MySqlPlanOkuyucu.Coz(MySqlJson, gercek: false);

        Assert.Equal(100.0, plan.Ifadeler[0].ToplamMaliyet, 2);

        PlanDugumu dongu = plan.Ifadeler[0].Kok!.Cocuklar[0].Cocuklar[0];
        PlanDugumu musteri = dongu.Cocuklar[0];
        PlanDugumu siparis = dongu.Cocuklar[1];

        // Kendi maliyeti DOĞRUDAN gelir: read_cost + eval_cost (çıkarma/çarpma yok)
        Assert.Equal(30.0, musteri.MaliyetYuzdesi, 1);   // (20+10)/100
        Assert.Equal(70.0, siparis.MaliyetYuzdesi, 1);   // (50+20)/100
    }

    [Fact]
    public void Paylar_toplami_100u_asmaz_ve_hicbiri_araligin_disina_cikmaz()
    {
        SorguPlani plan = MySqlPlanOkuyucu.Coz(MySqlJson, gercek: false);

        Assert.All(plan.TumDugumler, d => Assert.InRange(d.MaliyetYuzdesi, 0, 100));
        Assert.True(plan.TumDugumler.Sum(d => d.MaliyetYuzdesi) <= 101);
    }

    [Fact]
    public void Erisim_turu_okunur_ada_cevrilir()
    {
        SorguPlani plan = MySqlPlanOkuyucu.Coz(MySqlJson, gercek: false);
        PlanDugumu dongu = plan.Ifadeler[0].Kok!.Cocuklar[0].Cocuklar[0];

        // Kullanıcı "ALL" görüp anlamasın diye Türkçe ad + ham tür birlikte
        Assert.Contains("Tam tablo taraması", dongu.Cocuklar[0].Islem);
        Assert.Contains("[ALL]", dongu.Cocuklar[0].Islem);
        Assert.Contains("Index araması", dongu.Cocuklar[1].Islem);
    }

    [Fact]
    public void Uyarilar_sinyallerden_turetilir()
    {
        // MySQL planında hazır "uyarı" alanı yok; türetmezsek en değerli çıktı kaybolur
        SorguPlani plan = MySqlPlanOkuyucu.Coz(MySqlJson, gercek: false);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        PlanDugumu gruplama = kok.Cocuklar[0];
        PlanDugumu musteri = gruplama.Cocuklar[0].Cocuklar[0];

        Assert.Contains(kok.Uyarilar, u => u.Contains("filesort"));
        Assert.Contains(gruplama.Uyarilar, u => u.Contains("Geçici tablo"));
        Assert.Contains(musteri.Uyarilar, u => u.Contains("Tam tablo taraması"));
        // En sık gerçek sorun: index adayı var ama seçilmemiş
        Assert.Contains(musteri.Uyarilar, u => u.Contains("seçilmemiş"));
    }

    [Fact]
    public void Dusuk_filtered_orani_index_adayi_olarak_isaretlenir()
    {
        const string json = """
            {
              "query_block": {
                "cost_info": { "query_cost": "500.00" },
                "table": {
                  "table_name": "buyuk",
                  "access_type": "ALL",
                  "rows_examined_per_scan": 100000,
                  "filtered": "2.00",
                  "cost_info": { "read_cost": "400.00", "eval_cost": "100.00" }
                }
              }
            }
            """;

        PlanDugumu kok = MySqlPlanOkuyucu.Coz(json, gercek: false).Ifadeler[0].Kok!;
        Assert.Contains(kok.Uyarilar, u => u.Contains("index adayı"));
    }

    [Fact]
    public void MariaDB_sekli_de_okunur_maliyet_yoksa_uydurulmaz()
    {
        // MariaDB'de cost_info yoktur ve satır "rows" alanındadır. Maliyet bilgisi olmadan
        // yüzde uydurmak yanlış sıralama gösterirdi → 0 kalır ve plana AÇIK not düşülür.
        SorguPlani plan = MySqlPlanOkuyucu.Coz(MariaDbJson, gercek: false);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;

        Assert.Equal(0, plan.Ifadeler[0].ToplamMaliyet);
        Assert.Equal(0, kok.MaliyetYuzdesi);
        Assert.Equal(5000, kok.TahminiSatir);                    // "rows" alanından
        Assert.Contains("musteri", kok.Ayrinti);
        Assert.Contains("bakiye > 100", kok.Ayrinti);
        Assert.Contains(kok.Uyarilar, u => u.Contains("maliyet bilgisi vermiyor"));
    }

    [Fact]
    public void Union_ve_alt_sorgu_bolumleri_taninir()
    {
        const string json = """
            {
              "query_block": {
                "union_result": {
                  "using_temporary_table": true,
                  "query_specifications": [
                    { "query_block": { "table": { "table_name": "a", "access_type": "ALL", "rows": 10 } } },
                    { "query_block": { "table": { "table_name": "b", "access_type": "ALL", "rows": 20 } } }
                  ]
                }
              }
            }
            """;

        PlanDugumu kok = MySqlPlanOkuyucu.Coz(json, gercek: false).Ifadeler[0].Kok!;

        Assert.Equal("Union", kok.Islem);
        Assert.Equal(2, kok.Cocuklar.Count);
        Assert.Contains("a", kok.Cocuklar[0].Ayrinti);
        Assert.Contains("b", kok.Cocuklar[1].Ayrinti);
    }

    [Fact]
    public void Gercek_plan_bu_motorda_ACIKCA_reddedilir()
    {
        // MySQL TREE metni, MariaDB JSON döndürür; canlı doğrulama da yok → yarım yol açılmadı
        var lehce = new MySqlLehcesi(new DpapiSecretProtector());

        Assert.True(lehce.PlanDestekler);
        Assert.False(lehce.PlanGercekDestekler);
        Assert.Throws<NotSupportedException>(() => lehce.PlanSorgusuYaz("SELECT 1", gercek: true));
    }

    [Fact]
    public void Tahmini_plan_sorgusu_EXPLAIN_FORMAT_JSON_ile_sarilir()
    {
        var lehce = new MySqlLehcesi(new DpapiSecretProtector());

        Assert.Equal("EXPLAIN FORMAT=JSON SELECT 1", lehce.PlanSorgusuYaz("SELECT 1;", gercek: false));
        Assert.True(lehce.PlanTekIfadeIster);
        Assert.Equal("", lehce.PlanAcSql(false));     // oturum ayarı gerekmez
        Assert.Equal("", lehce.PlanKapatSql(false));
    }

    [Fact]
    public void MySql_eksik_index_ONERMEZ()
        => Assert.Empty(MySqlPlanOkuyucu.Coz(MySqlJson, gercek: false).Ifadeler[0].EksikIndexler);

    [Fact]
    public void Gercek_satir_yoktur_tahmini_plandir()
    {
        SorguPlani plan = MySqlPlanOkuyucu.Coz(MySqlJson, gercek: false);
        Assert.False(plan.Gercek);
        Assert.All(plan.TumDugumler, d => Assert.Null(d.GercekSatir));
    }

    [Fact]
    public void Sayilar_daima_invariant_okunur()
    {
        // "100.00" tr-TR ile 10000 okunurdu — maliyet sıralaması tamamen bozulurdu
        CultureInfo eski = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            SorguPlani plan = MySqlPlanOkuyucu.Coz(MySqlJson, gercek: false);
            Assert.Equal(100.0, plan.Ifadeler[0].ToplamMaliyet, 2);
        }
        finally
        {
            CultureInfo.CurrentCulture = eski;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ bozuk json")]
    [InlineData("{ }")]                          // query_block yok
    [InlineData("{ \"query_block\": 5 }")]
    [InlineData("{ \"query_block\": { } }")]     // okunabilir düğüm yok
    public void Bozuk_plan_jsonu_sessizce_bos_donmez_acik_hata(string json)
        => Assert.Throws<InvalidOperationException>(() => MySqlPlanOkuyucu.Coz(json, false));
}
