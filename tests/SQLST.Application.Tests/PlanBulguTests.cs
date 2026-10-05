using System.Text.Json;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// B1/A1 (2026-07-19) — V5-S1b adversaryel incelemesinden doğrulanmadan kalan bulguların
/// yeniden doğrulanmasında ÇIKAN GERÇEK KUSURLARIN regresyon testleri.
///
/// Her test bir bulguya karşılık gelir ve <b>düzeltmeden önce düşerdi</b>.
/// </summary>
public class PlanBulguTests
{
    private static PlanDugumu Dugum(double? tahmin, double? gercek, params PlanDugumu[] cocuklar)
        => new("Op", null, 0, tahmin, gercek, [], cocuklar);

    // ── Bulgu 1: MongoDB'de uydurma tahmin → her sağlıklı sorguda sapma uyarısı ──

    [Fact]
    public void Tahmin_YOKSA_sapma_uyarisi_CIKMAZ()
    {
        // Mongo: tahmin üretilmez, gerçek 5000 belge döner. Eskiden tahmin 0 yazılıyordu
        // ve SapmaVar "0 → 5000" görüp mükemmel index'li sorguda bile uyarı yakıyordu.
        PlanDugumu mongo = Dugum(tahmin: null, gercek: 5000);

        Assert.False(mongo.SapmaVar);
        Assert.False(mongo.TahminVar);
    }

    [Fact]
    public void Tahmin_VARSA_gercek_sapma_hala_yakalanir()
    {
        // Düzeltme sapma tespitini köreltmemeli: 10 satır beklenip 5000 gelmesi hâlâ uyarı.
        Assert.True(Dugum(tahmin: 10, gercek: 5000).SapmaVar);
        Assert.True(Dugum(tahmin: 5000, gercek: 10).SapmaVar);

        // Küçük sayılarda oran yanıltıcıdır — eşik korunuyor
        Assert.False(Dugum(tahmin: 1, gercek: 20).SapmaVar);
    }

    [Fact]
    public void Mongo_okuyucusu_tahmin_YAZMAZ()
    {
        const string planner = """
            { "winningPlan": { "stage": "COLLSCAN" } }
            """;
        const string istatistik = """
            {
              "nReturned": 5000, "totalDocsExamined": 5000,
              "executionStages": { "stage": "COLLSCAN", "nReturned": 5000 }
            }
            """;

        var sonuc = new QueryResult
        {
            ResultSetler =
            [
                new ResultSetData
                {
                    Kolonlar =
                    [
                        new KolonBilgisi("queryPlanner", "object", typeof(string)),
                        new KolonBilgisi("executionStats", "object", typeof(string)),
                    ],
                    Satirlar = [[planner, istatistik]],
                },
            ],
        };

        SorguPlani plan = MongoPlanOkuyucu.Coz(sonuc, gercek: true);

        // Tahmin ÜRETİLMEZ → sapma uyarısı da çıkmaz (5000 belge dönmesine rağmen)
        Assert.All(plan.TumDugumler, d => Assert.Null(d.TahminiSatir));
        Assert.DoesNotContain(plan.TumDugumler, d => d.SapmaVar);
    }

    // ── Bulgu 2: MSSQL çok ifadeli batch'te sessiz plan kaybı ──

    [Fact]
    public void MSSQL_her_showplan_KUMESI_okunur()
    {
        // STATISTICS XML ifade BAŞINA ayrı sonuç kümesi döndürür. Eskiden ilk kümede
        // return ediliyordu → 2..N. ifadelerin planı sessizce atılıyordu.
        var lehce = new MssqlLehcesi(new DpapiSecretProtector());
        QueryResult sonuc = IkiPlanKumesi();

        SorguPlani plan = lehce.PlanCoz(sonuc, gercek: true);

        Assert.Equal(2, plan.Ifadeler.Count);
        Assert.Contains(plan.Ifadeler, i => i.IfadeMetni.Contains("Musteri", StringComparison.Ordinal));
        Assert.Contains(plan.Ifadeler, i => i.IfadeMetni.Contains("Siparis", StringComparison.Ordinal));
    }

    private static QueryResult IkiPlanKumesi()
    {
        static ResultSetData Kume(string tablo) => new()
        {
            Kolonlar = [new KolonBilgisi("Microsoft SQL Server 2005 XML Showplan", "xml")],
            Satirlar = [[PlanXml(tablo)]],
        };

        return new QueryResult
        {
            ResultSetler = [Kume("Musteri"), Kume("Siparis")],
        };
    }

    private static string PlanXml(string tablo) => $"""
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT * FROM dbo.{tablo}" StatementSubTreeCost="1.0">
              <QueryPlan>
                <RelOp PhysicalOp="Table Scan" EstimatedTotalSubtreeCost="1.0" EstimateRows="10" />
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    // ── Bulgu 3: MSSQL kapsayıcı ifade (IF/WHILE) hayalet plan üretiyordu ──

    [Fact]
    public void MSSQL_kapsayici_ifade_HAYALET_plan_uretmez()
    {
        // StmtCond'un kendi <QueryPlan>'ı yoktur; planı içteki StmtSimple'dadır. Eskiden
        // StmtCond da ifade listesine giriyordu: boş metinli, maliyeti 0, ağacı BOŞ bir
        // satır — üstelik listede ÖNCE geldiği için sekme boş ağaçla açılıyordu.
        const string xml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements>
                <StmtCond StatementText="IF EXISTS (SELECT 1 FROM dbo.Siparis)">
                  <Condition><QueryPlan>
                    <RelOp PhysicalOp="Index Seek" EstimatedTotalSubtreeCost="0.5" EstimateRows="1" />
                  </QueryPlan></Condition>
                  <Then><Statements>
                    <StmtSimple StatementText="SELECT 1" StatementSubTreeCost="0.1">
                      <QueryPlan>
                        <RelOp PhysicalOp="Constant Scan" EstimatedTotalSubtreeCost="0.1" EstimateRows="1" />
                      </QueryPlan>
                    </StmtSimple>
                  </Statements></Then>
                </StmtCond>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        SorguPlani plan = MssqlPlanOkuyucu.Coz(xml, gercek: false);

        Assert.DoesNotContain(plan.Ifadeler, i => i.Kok is null);
        Assert.All(plan.Ifadeler, i => Assert.NotEqual("", i.IfadeMetni));
    }

    // ── Bulgu 4: MySQL iç query_block'un maliyeti dış bloğun toplamına bölünüyordu ──

    [Fact]
    public void MySQL_ic_blok_KENDI_toplamiyla_olceklenir()
    {
        // Dış blok küçücük (geçici tabloyu tarama), iç blok asıl iş. Eskiden iç düğümün
        // payı dış toplama bölünüyor, sonuç %100'ü katbekat aşıyor ve Math.Clamp bunu
        // makul görünen bir sayıya çevirip GİZLİYORDU.
        const string json = """
            {
              "query_block": {
                "select_id": 1,
                "cost_info": { "query_cost": "2.50" },
                "table": {
                  "table_name": "d",
                  "cost_info": { "read_cost": "1.00", "eval_cost": "0.50" },
                  "rows_examined_per_scan": 10,
                  "materialized_from_subquery": {
                    "query_block": {
                      "select_id": 2,
                      "cost_info": { "query_cost": "11000.00" },
                      "table": {
                        "table_name": "siparis",
                        "cost_info": { "read_cost": "10000.00", "eval_cost": "1000.00" },
                        "rows_examined_per_scan": 1000000
                      }
                    }
                  }
                }
              }
            }
            """;

        SorguPlani plan = MySqlPlanOkuyucu.Coz(json, gercek: false);

        // Sözleşme: her düğümün payı 0-100. Clamp'e DAYANMADAN sağlanmalı — iç blok kendi
        // toplamıyla ölçeklendiği için siparis taraması bloğunun neredeyse tamamıdır.
        Assert.All(plan.TumDugumler, d =>
            Assert.InRange(d.MaliyetYuzdesi, 0, 100));

        PlanDugumu siparis = plan.TumDugumler.Single(d =>
            d.Ayrinti?.Contains("siparis", StringComparison.Ordinal) == true);
        Assert.True(siparis.MaliyetYuzdesi > 90,
            $"iç bloğun asıl işi kendi bloğunun ~%100'ü olmalı, gelen: {siparis.MaliyetYuzdesi:F1}");
    }

    [Fact]
    public void MySQL_maliyet_varsa_MariaDB_notu_DUSULMEZ()
    {
        // Dış blokta cost_info olmayan (UNION) ama iç bloklarda olan MySQL planında
        // kullanıcıya "(MariaDB) maliyet vermiyor" notu gösteriliyordu.
        const string json = """
            {
              "query_block": {
                "union_result": {
                  "using_temporary_table": true,
                  "query_specifications": [
                    { "query_block": {
                        "select_id": 1,
                        "cost_info": { "query_cost": "5.00" },
                        "table": { "table_name": "a", "rows_examined_per_scan": 5 } } }
                  ]
                }
              }
            }
            """;

        SorguPlani plan = MySqlPlanOkuyucu.Coz(json, gercek: false);

        Assert.DoesNotContain(plan.TumDugumler,
            d => d.Uyarilar.Any(u => u.Contains("MariaDB", StringComparison.Ordinal)));
    }

    // ── Bulgu 5: Oracle PLAN_TABLE'da döngü → StackOverflow (süreç ölümü) ──

    [Fact]
    public void Oracle_dongulu_PLAN_TABLE_cokmez()
    {
        // PLAN_TABLE sıradan, kullanıcının yazabildiği bir tablodur; parent_id'si kendini
        // gösteren bozuk bir satır sonsuz özyineleme yapardı. StackOverflowException .NET'te
        // YAKALANAMAZ — süreç anında ölür ve kaydedilmemiş editör içeriği kaybolur.
        var sonuc = new QueryResult
        {
            ResultSetler =
            [
                new ResultSetData
                {
                    Kolonlar =
                    [
                        new KolonBilgisi("id", "int"), new KolonBilgisi("parent_id", "int"),
                        new KolonBilgisi("operation", "varchar2"), new KolonBilgisi("options", "varchar2"),
                        new KolonBilgisi("object_owner", "varchar2"), new KolonBilgisi("object_name", "varchar2"),
                        new KolonBilgisi("cardinality", "number"), new KolonBilgisi("cost", "number"),
                        new KolonBilgisi("access_predicates", "varchar2"), new KolonBilgisi("filter_predicates", "varchar2"),
                    ],
                    Satirlar =
                    [
                        [0, null, "SELECT STATEMENT", null, null, null, 100, 10, null, null],
                        [1, 0, "NESTED LOOPS", null, null, null, 100, 10, null, null],
                        // 2 → 1 → 2 döngüsü
                        [2, 1, "TABLE ACCESS", "FULL", "HR", "EMP", 100, 5, null, null],
                        [3, 2, "TABLE ACCESS", "FULL", "HR", "DEPT", 10, 2, null, null],
                    ],
                },
            ],
        };

        // Döngü olmayan hâlde de çalışmalı; asıl sınav aşağıdaki döngülü satır
        SorguPlani plan = OraclePlanOkuyucu.Coz(sonuc, gercek: false);
        Assert.NotNull(plan.Ifadeler[0].Kok);

        // Kendini gösteren satır: sonsuz özyineleme YAPMAMALI
        sonuc.ResultSetler[0].Satirlar[3][1] = 3;   // parent_id = kendi id'si
        SorguPlani dongulu = OraclePlanOkuyucu.Coz(sonuc, gercek: false);
        Assert.NotNull(dongulu.Ifadeler[0].Kok);
    }

    [Fact]
    public void Oracle_beklenmedik_kolon_tipi_COKERTMEZ()
    {
        // Convert.ToInt32/ToDouble beklenmedik tipte atar; plan yolu yalnız
        // InvalidOperationException yakaladığından bunlar uygulama çökmesi olurdu.
        var sonuc = new QueryResult
        {
            ResultSetler =
            [
                new ResultSetData
                {
                    Kolonlar =
                    [
                        new KolonBilgisi("id", "int"), new KolonBilgisi("parent_id", "int"),
                        new KolonBilgisi("operation", "varchar2"), new KolonBilgisi("options", "varchar2"),
                        new KolonBilgisi("object_owner", "varchar2"), new KolonBilgisi("object_name", "varchar2"),
                        new KolonBilgisi("cardinality", "number"), new KolonBilgisi("cost", "number"),
                        new KolonBilgisi("access_predicates", "varchar2"), new KolonBilgisi("filter_predicates", "varchar2"),
                    ],
                    Satirlar =
                    [
                        // cardinality ve cost sayı DEĞİL — sürücü/tablo beklenmedik tip verirse
                        [0, null, "SELECT STATEMENT", null, null, null, "çok", "pahalı", null, null],
                    ],
                },
            ],
        };

        SorguPlani plan = OraclePlanOkuyucu.Coz(sonuc, gercek: false);

        Assert.NotNull(plan.Ifadeler[0].Kok);
        Assert.Null(plan.Ifadeler[0].Kok!.TahminiSatir);
    }
}
