using System.Globalization;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S1b: PostgreSQL EXPLAIN JSON çözümleme. Fixture'lardaki sayılar CANLI PG 16.4'ten
/// ölçüldü (uydurulmadı) — özellikle Nested Loop maliyetleri (42.64 = 1.05×1 + 8.31×5)
/// ve erken durma örneği (Limit 0.46 &lt; Index Scan 1693.29).
/// </summary>
public class PostgresPlanOkuyucuTests
{
    /// <summary>Canlıdan ölçülen Nested Loop: dış Seq Scan 1 kez, iç Index Scan 5 kez döner.</summary>
    private const string NestedLoopJson = """
        [
          {
            "Plan": {
              "Node Type": "Nested Loop",
              "Join Type": "Inner",
              "Startup Cost": 0.29, "Total Cost": 42.64,
              "Plan Rows": 5, "Actual Rows": 5, "Actual Loops": 1,
              "Plans": [
                {
                  "Node Type": "Seq Scan", "Parent Relationship": "Outer",
                  "Relation Name": "gecici_nl_dis", "Schema": "public",
                  "Startup Cost": 0.00, "Total Cost": 1.05,
                  "Plan Rows": 5, "Actual Rows": 5, "Actual Loops": 1
                },
                {
                  "Node Type": "Index Scan", "Parent Relationship": "Inner",
                  "Relation Name": "gecici_nl_ic", "Index Name": "gecici_nl_ic_id_idx",
                  "Index Cond": "(id = d.id)", "Scan Direction": "Forward",
                  "Startup Cost": 0.29, "Total Cost": 8.31,
                  "Plan Rows": 1, "Actual Rows": 1, "Actual Loops": 5
                }
              ]
            },
            "Planning Time": 0.21,
            "Execution Time": 0.09
          }
        ]
        """;

    [Fact]
    public void Kok_DIZIdir_ve_Plan_katmani_atlanamaz()
    {
        // Nesne bekleyip ["Plan"] aramak sessizce boş ağaç üretirdi
        SorguPlani plan = PostgresPlanOkuyucu.Coz(NestedLoopJson, gercek: true);

        IfadePlani ifade = Assert.Single(plan.Ifadeler);
        Assert.NotNull(ifade.Kok);
        Assert.StartsWith("Nested Loop", ifade.Kok!.Islem);
        Assert.Equal(2, ifade.Kok.Cocuklar.Count);
    }

    [Fact]
    public void GERCEK_SATIR_dongu_ile_carpilir()
    {
        // PG'de Actual Rows DÖNGÜ BAŞINAdır: iç taraf "1 satır" der ama 5 kez döner → 5.
        // Çarpmayı atlamak hata vermez, yalnızca YANLIŞ sayı gösterir.
        SorguPlani plan = PostgresPlanOkuyucu.Coz(NestedLoopJson, gercek: true);
        PlanDugumu ic = plan.Ifadeler[0].Kok!.Cocuklar[1];

        Assert.Equal("Index Scan", ic.Islem);
        Assert.Equal(5, ic.GercekSatir);        // 1 × 5, ham "1" değil
        Assert.Equal(5, ic.TahminiSatir);       // tahmin de aynı ölçeğe getirilir
    }

    [Fact]
    public void KENDI_MALIYETI_cocugun_dongusuyle_carpilarak_bulunur()
    {
        // Canlı ölçüm: 42.64 − (1.05×1 + 8.31×5) = 0.04
        // MSSQL'deki "Total − Σçocuk" formülü kopyalansaydı 42.64 − 9.36 = 33.28 çıkardı
        // ve Nested Loop'u darboğaz gibi gösterirdi.
        SorguPlani plan = PostgresPlanOkuyucu.Coz(NestedLoopJson, gercek: true);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        PlanDugumu ic = kok.Cocuklar[1];

        Assert.Equal(0.04 / 42.64 * 100, kok.MaliyetYuzdesi, 1);   // ≈ %0,1 — kendi işi yok denecek kadar az
        Assert.Equal(8.31 * 5 / 42.64 * 100, ic.MaliyetYuzdesi, 1); // ≈ %97 — gerçek darboğaz
        Assert.True(ic.MaliyetYuzdesi > 90, "Asıl maliyet iç Index Scan'de olmalı");
    }

    [Fact]
    public void ERKEN_DURMADA_sessizce_sifirlanmaz_acik_not_dusulur()
    {
        // Canlı ölçüm: Limit 0.46 iken altındaki Index Scan 1693.29 — ebeveyn çocuğu
        // sonuna kadar tüketmiyor. Sessizce 0'a kırpmak yanlış tabloyu doğru gibi gösterirdi.
        const string json = """
            [
              {
                "Plan": {
                  "Node Type": "Limit",
                  "Startup Cost": 0.29, "Total Cost": 0.46,
                  "Plan Rows": 5, "Actual Rows": 5, "Actual Loops": 1,
                  "Plans": [
                    {
                      "Node Type": "Index Scan", "Parent Relationship": "Outer",
                      "Relation Name": "gecici_nl_ic", "Index Name": "gecici_nl_ic_id_idx",
                      "Startup Cost": 0.29, "Total Cost": 1693.29,
                      "Plan Rows": 50000, "Actual Rows": 5, "Actual Loops": 1
                    }
                  ]
                }
              }
            ]
            """;

        SorguPlani plan = PostgresPlanOkuyucu.Coz(json, gercek: true);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;

        Assert.Equal(0, kok.MaliyetYuzdesi);                       // negatif değil, sıfır
        Assert.Contains(kok.Uyarilar, u => u.Contains("erken durma", StringComparison.OrdinalIgnoreCase));

        // REGRESYON (adüversaryel inceleme, canlı ölçüm): önce YALNIZ ebeveyn kırpılıyordu;
        // çocuğun payı kök maliyetine (0.46) bölünüp %368.106 çıkıyordu. Canlı ölçümde
        // 600k satırlık tabloda %3.291.088 görüldü ve ekrana ham basılıyordu.
        PlanDugumu cocuk = Assert.Single(kok.Cocuklar);
        Assert.InRange(cocuk.MaliyetYuzdesi, 0, 100);
        Assert.Contains(cocuk.Uyarilar, u => u.Contains("Üstteki düğüm"));

        // Sözleşme (PlanModelleri: "0-100") plandaki HER düğümde geçerli olmalı
        Assert.All(plan.TumDugumler, d => Assert.InRange(d.MaliyetYuzdesi, 0, 100));
    }

    [Fact]
    public void PARALEL_planda_maliyet_isci_sayisiyla_SISMEZ()
    {
        // REGRESYON (canlı ölçümle bulundu): Gather'ın Total Cost'u çocuğunu ZATEN BİR KEZ
        // içerir; Actual Loops (işçi+lider) ile çarpmak payları işçi sayısı kadar şişiriyor
        // ve ebeveyni negatife düşürüp asılsız "erken durma" notu doğuruyordu (ölçülen Σ %485).
        // Canlı sayılar: Gather 2832.06 = Partial Aggregate 2832.06 × 1 (loops 5 olmasına rağmen).
        const string json = """
            [
              {
                "Plan": {
                  "Node Type": "Aggregate", "Partial Mode": "Finalize",
                  "Total Cost": 2832.08, "Plan Rows": 1, "Actual Rows": 1, "Actual Loops": 1,
                  "Plans": [
                    {
                      "Node Type": "Gather", "Parent Relationship": "Outer",
                      "Total Cost": 2832.06, "Plan Rows": 4, "Actual Rows": 5, "Actual Loops": 1,
                      "Workers Planned": 4, "Workers Launched": 4,
                      "Plans": [
                        {
                          "Node Type": "Aggregate", "Partial Mode": "Partial", "Parent Relationship": "Outer",
                          "Total Cost": 2832.06, "Plan Rows": 1, "Actual Rows": 1, "Actual Loops": 5,
                          "Plans": [
                            {
                              "Node Type": "Seq Scan", "Parent Relationship": "Outer",
                              "Relation Name": "buyuk", "Parallel Aware": true,
                              "Total Cost": 2752.50, "Plan Rows": 31812, "Actual Rows": 31812, "Actual Loops": 5
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              }
            ]
            """;

        SorguPlani plan = PostgresPlanOkuyucu.Coz(json, gercek: true);

        double toplam = plan.TumDugumler.Sum(d => d.MaliyetYuzdesi);
        Assert.InRange(toplam, 95, 101);                 // önce ~%485 çıkıyordu

        Assert.All(plan.TumDugumler, d => Assert.InRange(d.MaliyetYuzdesi, 0, 100));
        // Planda LIMIT yok → hiçbir düğüme "erken durma" notu DÜŞMEMELİ
        Assert.All(plan.TumDugumler,
            d => Assert.DoesNotContain(d.Uyarilar, u => u.Contains("erken durma", StringComparison.OrdinalIgnoreCase)));

        // Asıl iş taramada: payın büyük kısmı orada olmalı
        PlanDugumu tarama = plan.TumDugumler.Single(d => d.Islem.StartsWith("Seq Scan"));
        Assert.True(tarama.MaliyetYuzdesi > 90, $"tarama payı %{tarama.MaliyetYuzdesi:F1} — asıl maliyet orada olmalı");

        // Satırlar ise gerçekten işçi başınadır → Loops ile ÇARPILIR (maliyetten farklı kural)
        Assert.Equal(31812d * 5, tarama.GercekSatir);
    }

    [Fact]
    public void InitPlan_ve_SubPlan_maliyet_muhasebesine_GIRMEZ()
    {
        // Ana akış olmayan çocuklar ebeveynin maliyetine dahil değildir; normal çocuk
        // sayılsaydı ebeveynin kendi maliyeti negatife düşer, yüzdeler 100'ü tutmazdı.
        const string json = """
            [
              {
                "Plan": {
                  "Node Type": "Seq Scan", "Relation Name": "musteri",
                  "Startup Cost": 0.10, "Total Cost": 20.00,
                  "Plan Rows": 100, "Actual Rows": 100, "Actual Loops": 1,
                  "Plans": [
                    {
                      "Node Type": "Aggregate", "Parent Relationship": "InitPlan",
                      "Subplan Name": "InitPlan 1 (returns $0)",
                      "Startup Cost": 0.00, "Total Cost": 500.00,
                      "Plan Rows": 1, "Actual Rows": 1, "Actual Loops": 1
                    }
                  ]
                }
              }
            ]
            """;

        PlanDugumu kok = PostgresPlanOkuyucu.Coz(json, gercek: true).Ifadeler[0].Kok!;

        Assert.Equal(100, kok.MaliyetYuzdesi, 1);                  // 20 / 20 — InitPlan düşülmedi
        Assert.DoesNotContain(kok.Uyarilar, u => u.Contains("erken durma"));
        PlanDugumu init = Assert.Single(kok.Cocuklar);             // yine de AĞAÇTA görünür
        Assert.Contains("InitPlan 1", init.Ayrinti);
    }

    [Fact]
    public void Yaprak_dugumde_Plans_anahtari_HIC_YOKTUR()
    {
        // Doğrudan dugum["Plans"] erişimi istisna atardı (boş dizi değil, anahtar yok)
        const string json = """
            [{ "Plan": { "Node Type": "Seq Scan", "Total Cost": 5.0, "Plan Rows": 3, "Actual Rows": 3, "Actual Loops": 1 } }]
            """;

        PlanDugumu kok = PostgresPlanOkuyucu.Coz(json, gercek: true).Ifadeler[0].Kok!;
        Assert.Empty(kok.Cocuklar);
        Assert.Equal(100, kok.MaliyetYuzdesi, 1);
    }

    [Fact]
    public void Hic_calismayan_dugum_sifir_satir_DEMEK_DEGILDIR()
    {
        // Actual Loops = 0 → düğüm hiç yürütülmedi. "0 satır" demek "join yanlış" teşhisi
        // koydururdu; ayrıca tahmin/gerçek sapması yanlış yere yanardı.
        const string json = """
            [
              {
                "Plan": {
                  "Node Type": "Index Scan", "Relation Name": "t",
                  "Total Cost": 10.0, "Plan Rows": 50000,
                  "Actual Rows": 0, "Actual Loops": 0
                }
              }
            ]
            """;

        PlanDugumu kok = PostgresPlanOkuyucu.Coz(json, gercek: true).Ifadeler[0].Kok!;

        Assert.Null(kok.GercekSatir);                              // 0 değil, "bilinmiyor"
        Assert.False(kok.SapmaVar);                                // sapma hesabı atlanır
        Assert.Contains(kok.Uyarilar, u => u.Contains("hiç çalıştırılmadı"));
    }

    [Fact]
    public void Tahmini_planda_gercek_satir_yoktur()
    {
        SorguPlani plan = PostgresPlanOkuyucu.Coz(NestedLoopJson, gercek: false);

        Assert.False(plan.Gercek);
        Assert.All(plan.TumDugumler, d => Assert.Null(d.GercekSatir));
        // Tahmini planda satırlar döngüyle ÇARPILMAZ (ham per-loop kalır)
        Assert.Equal(1, plan.Ifadeler[0].Kok!.Cocuklar[1].TahminiSatir);
    }

    [Fact]
    public void Uyarilar_sinyallerden_TURETILIR()
    {
        // PG planında hazır "Warnings" alanı yoktur; türetmezsek aracın en değerli çıktısı ölür
        const string json = """
            [
              {
                "Plan": {
                  "Node Type": "Sort", "Total Cost": 100.0, "Plan Rows": 10,
                  "Actual Rows": 10, "Actual Loops": 1,
                  "Sort Method": "external merge", "Sort Space Type": "Disk", "Sort Space Used": 8192,
                  "Workers Planned": 4, "Workers Launched": 1,
                  "Plans": [
                    {
                      "Node Type": "Seq Scan", "Parent Relationship": "Outer", "Relation Name": "buyuk",
                      "Total Cost": 90.0, "Plan Rows": 10, "Actual Rows": 10, "Actual Loops": 1,
                      "Filter": "(x > 5)", "Rows Removed by Filter": 500000
                    }
                  ]
                }
              }
            ]
            """;

        SorguPlani plan = PostgresPlanOkuyucu.Coz(json, gercek: true);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        PlanDugumu tarama = kok.Cocuklar[0];

        Assert.Contains(kok.Uyarilar, u => u.Contains("diske taştı"));
        Assert.Contains(kok.Uyarilar, u => u.Contains("disk kullandı"));
        Assert.Contains(kok.Uyarilar, u => u.Contains("Planlanan paralellik sağlanamadı"));
        Assert.Contains(tarama.Uyarilar, u => u.Contains("index adayı"));
    }

    [Fact]
    public void Elenen_satir_de_dongu_ile_carpilir()
    {
        // Rows Removed by Filter da döngü başınadır — çarpılmazsa uyarıdaki sayı yanlış olur
        const string json = """
            [
              {
                "Plan": {
                  "Node Type": "Seq Scan", "Relation Name": "t",
                  "Total Cost": 100.0, "Plan Rows": 1,
                  "Actual Rows": 1, "Actual Loops": 3,
                  "Filter": "(x > 5)", "Rows Removed by Filter": 156299
                }
              }
            ]
            """;

        PlanDugumu kok = PostgresPlanOkuyucu.Coz(json, gercek: true).Ifadeler[0].Kok!;
        Assert.Contains(kok.Uyarilar, u => u.Contains("468,897") || u.Contains("468.897"));
    }

    [Fact]
    public void Postgres_eksik_index_ONERMEZ()
    {
        SorguPlani plan = PostgresPlanOkuyucu.Coz(NestedLoopJson, gercek: true);
        Assert.Empty(plan.Ifadeler[0].EksikIndexler);
    }

    [Fact]
    public void Ayrinti_tablo_index_ve_kosulu_tasir()
    {
        SorguPlani plan = PostgresPlanOkuyucu.Coz(NestedLoopJson, gercek: true);
        PlanDugumu ic = plan.Ifadeler[0].Kok!.Cocuklar[1];

        Assert.Contains("gecici_nl_ic", ic.Ayrinti);
        Assert.Contains("gecici_nl_ic_id_idx", ic.Ayrinti);
        Assert.Contains("(id = d.id)", ic.Ayrinti);
    }

    [Fact]
    public void Sayilar_daima_invariant_okunur()
    {
        // "1250.5" tr-TR ile 12505 okunurdu — 10 kat sapma, hiçbir istisna yok
        CultureInfo eski = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            SorguPlani plan = PostgresPlanOkuyucu.Coz(NestedLoopJson, gercek: true);
            Assert.Equal(42.64, plan.Ifadeler[0].ToplamMaliyet, 2);
        }
        finally
        {
            CultureInfo.CurrentCulture = eski;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ bu gecerli json degil")]
    [InlineData("{ \"Plan\": {} }")]   // kök dizi değil
    [InlineData("[]")]
    [InlineData("[ 42 ]")]             // dizi elemanı nesne değil → ham .NET istisnası sızmamalı
    [InlineData("[ { \"Plan\": 5 } ]")]
    public void Bozuk_plan_jsonu_sessizce_bos_donmez_acik_hata(string json)
        => Assert.Throws<InvalidOperationException>(() => PostgresPlanOkuyucu.Coz(json, true));

    [Fact]
    public void Cok_derin_plan_okunabilir()
    {
        // REGRESYON: JsonDocument varsayılan MaxDepth 64'tür. Çok yollu join planları bunu
        // aşar ve plan HİÇ okunamazdı — MSSQL tarafında böyle bir sınır yok, asimetri olurdu.
        var sb = new System.Text.StringBuilder("[{\"Plan\":");
        const int derinlik = 120;
        for (int i = 0; i < derinlik; i++)
        {
            sb.Append($"{{\"Node Type\":\"Nested Loop\",\"Total Cost\":{derinlik - i}," +
                      "\"Plan Rows\":1,\"Actual Rows\":1,\"Actual Loops\":1,\"Plans\":[");
        }
        sb.Append("{\"Node Type\":\"Seq Scan\",\"Total Cost\":1,\"Plan Rows\":1,\"Actual Rows\":1,\"Actual Loops\":1}");
        for (int i = 0; i < derinlik; i++)
            sb.Append("]}");
        sb.Append("}]");

        SorguPlani plan = PostgresPlanOkuyucu.Coz(sb.ToString(), gercek: true);

        Assert.Equal(derinlik + 1, plan.TumDugumler.Count());
        Assert.All(plan.TumDugumler, d => Assert.InRange(d.MaliyetYuzdesi, 0, 100));
    }
}
