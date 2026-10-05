using System.Globalization;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S1: showplan XML çözümleme. Fixture'lar gerçek LocalDB çıktısının yapısına birebir
/// uyar (iç içe RelOp'lar operatöre özgü elemanın içinde, maliyet KÜMÜLATİF, gerçek satır
/// sayaçları thread başına).
/// </summary>
public class PlanOkuyucuTests
{
    private const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>Kök Sort (0.100) → Aggregate (0.060) → Index Scan (0.020). Kümülatif maliyetler.</summary>
    private static string UcDugumluPlan(bool gercekSayaclarla = false)
    {
        string sayac(string satir) => gercekSayaclarla
            ? $"""<RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="{satir}" ActualExecutions="1" /></RunTimeInformation>"""
            : "";

        return $"""
            <ShowPlanXML xmlns="{Ns}">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT Ad FROM dbo.T" StatementSubTreeCost="0.100">
                  <QueryPlan>
                    <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="Sort"
                           EstimateRows="10" EstimatedTotalSubtreeCost="0.100">
                      {sayac("12")}
                      <Sort>
                        <RelOp NodeId="1" PhysicalOp="Stream Aggregate" LogicalOp="Aggregate"
                               EstimateRows="10" EstimatedTotalSubtreeCost="0.060">
                          {sayac("12")}
                          <StreamAggregate>
                            <RelOp NodeId="2" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan"
                                   EstimateRows="500" EstimatedTotalSubtreeCost="0.020">
                              {sayac("500")}
                              <IndexScan>
                                <Object Database="[db]" Schema="[dbo]" Table="[T]" Index="[PK_T]" />
                              </IndexScan>
                            </RelOp>
                          </StreamAggregate>
                        </RelOp>
                      </Sort>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
    }

    [Fact]
    public void Operator_agaci_ic_ice_relop_yapisindan_kurulur()
    {
        // Çocuklar operatöre özgü elemanın (<Sort>, <StreamAggregate>, <IndexScan>) İÇİNDE
        // yuvalanır; doğrudan çocuk değildir. Naif Elements() ile aranırsa ağaç DÜZ çıkar.
        SorguPlani plan = MssqlPlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);

        IfadePlani ifade = Assert.Single(plan.Ifadeler);
        PlanDugumu kok = Assert.IsType<PlanDugumu>(ifade.Kok);

        Assert.Equal("Sort", kok.Islem);
        PlanDugumu orta = Assert.Single(kok.Cocuklar);
        Assert.Equal("Stream Aggregate", orta.Islem);
        PlanDugumu yaprak = Assert.Single(orta.Cocuklar);
        Assert.Equal("Clustered Index Scan", yaprak.Islem);
        Assert.Empty(yaprak.Cocuklar);
        Assert.Equal(3, kok.Hepsi().Count());
    }

    [Fact]
    public void Maliyet_yuzdesi_KENDI_payidir_kumulatif_degil()
    {
        // XML'deki EstimatedTotalSubtreeCost alt ağacın TOPLAMIDIR. Yüzde olarak onu
        // kullanmak kökü daima %100 gösterip planı işe yaramaz kılardı (SSMS de kendi payını gösterir).
        SorguPlani plan = MssqlPlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        PlanDugumu orta = kok.Cocuklar[0];
        PlanDugumu yaprak = orta.Cocuklar[0];

        Assert.Equal(40, kok.MaliyetYuzdesi, 1);      // (0.100 − 0.060) / 0.100
        Assert.Equal(40, orta.MaliyetYuzdesi, 1);     // (0.060 − 0.020) / 0.100
        Assert.Equal(20, yaprak.MaliyetYuzdesi, 1);   // 0.020 / 0.100 (yaprak = kendi maliyeti)

        // Toplam ~%100 olmalı — dağıtım tutarlıysa plan okunabilir demektir
        Assert.Equal(100, kok.Hepsi().Sum(d => d.MaliyetYuzdesi), 1);
    }

    [Fact]
    public void Tahmini_planda_gercek_satir_yoktur()
    {
        SorguPlani plan = MssqlPlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);

        Assert.False(plan.Gercek);
        Assert.All(plan.TumDugumler, d => Assert.Null(d.GercekSatir));
        Assert.All(plan.TumDugumler, d => Assert.False(d.SapmaVar));
    }

    [Fact]
    public void Gercek_planda_satir_sayaclari_okunur()
    {
        SorguPlani plan = MssqlPlanOkuyucu.Coz(UcDugumluPlan(gercekSayaclarla: true), gercek: true);

        Assert.True(plan.Gercek);
        PlanDugumu yaprak = plan.Ifadeler[0].Kok!.Cocuklar[0].Cocuklar[0];
        Assert.Equal(500, yaprak.GercekSatir);
    }

    [Fact]
    public void Paralel_planda_thread_sayaclari_TOPLANIR()
    {
        // Yalnız ilk thread'i almak paralel planları olduğundan küçük gösterirdi
        string xml = $"""
            <ShowPlanXML xmlns="{Ns}">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT 1" StatementSubTreeCost="1">
                  <QueryPlan>
                    <RelOp NodeId="0" PhysicalOp="Table Scan" EstimateRows="900" EstimatedTotalSubtreeCost="1">
                      <RunTimeInformation>
                        <RunTimeCountersPerThread Thread="1" ActualRows="300" />
                        <RunTimeCountersPerThread Thread="2" ActualRows="400" />
                        <RunTimeCountersPerThread Thread="3" ActualRows="200" />
                      </RunTimeInformation>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        SorguPlani plan = MssqlPlanOkuyucu.Coz(xml, gercek: true);
        Assert.Equal(900, plan.Ifadeler[0].Kok!.GercekSatir);
    }

    [Fact]
    public void Tahmin_gercek_sapmasi_isaretlenir_ama_kucuk_sayilarda_susar()
    {
        // Planın en işe yarar sinyali: optimizer 10 bekledi, 5000 geldi → istatistik eski
        string Xml(string tahmin, string gercek) => $"""
            <ShowPlanXML xmlns="{Ns}">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT 1" StatementSubTreeCost="1">
                  <QueryPlan>
                    <RelOp NodeId="0" PhysicalOp="Table Scan" EstimateRows="{tahmin}" EstimatedTotalSubtreeCost="1">
                      <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="{gercek}" /></RunTimeInformation>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        Assert.True(MssqlPlanOkuyucu.Coz(Xml("10", "5000"), true).Ifadeler[0].Kok!.SapmaVar);
        Assert.True(MssqlPlanOkuyucu.Coz(Xml("5000", "10"), true).Ifadeler[0].Kok!.SapmaVar);

        // 1 → 20 satır "20 kat"tır ama önemsiz; küçük sayılarda oran yanıltıcıdır
        Assert.False(MssqlPlanOkuyucu.Coz(Xml("1", "20"), true).Ifadeler[0].Kok!.SapmaVar);
        // Yakın tahmin uyarı üretmez
        Assert.False(MssqlPlanOkuyucu.Coz(Xml("1000", "1200"), true).Ifadeler[0].Kok!.SapmaVar);
    }

    [Fact]
    public void Nesne_adi_ve_index_ayrinti_olarak_okunur()
    {
        SorguPlani plan = MssqlPlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);
        PlanDugumu yaprak = plan.Ifadeler[0].Kok!.Cocuklar[0].Cocuklar[0];

        Assert.Equal("[dbo].[T] · [PK_T]", yaprak.Ayrinti);
        // Üstteki operatörler bir nesnenin üstünde çalışmıyor → ayrıntı yok
        Assert.Null(plan.Ifadeler[0].Kok!.Ayrinti);
    }

    [Fact]
    public void Uyarilar_okunur()
    {
        string xml = $"""
            <ShowPlanXML xmlns="{Ns}">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT 1" StatementSubTreeCost="1">
                  <QueryPlan>
                    <RelOp NodeId="0" PhysicalOp="Hash Match" EstimateRows="1" EstimatedTotalSubtreeCost="1">
                      <Warnings NoJoinPredicate="1">
                        <SpillToTempDb SpillLevel="1" />
                        <PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT(int,[x])" />
                      </Warnings>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        PlanDugumu kok = MssqlPlanOkuyucu.Coz(xml, gercek: true).Ifadeler[0].Kok!;

        Assert.Equal(3, kok.Uyarilar.Count);
        Assert.Contains(kok.Uyarilar, u => u.Contains("Join koşulu yok"));
        Assert.Contains(kok.Uyarilar, u => u.Contains("tempdb"));
        Assert.Contains(kok.Uyarilar, u => u.Contains("CONVERT(int,[x])"));
    }

    [Fact]
    public void Eksik_index_onerisi_ve_scripti_okunur()
    {
        string xml = $"""
            <ShowPlanXML xmlns="{Ns}">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT 1" StatementSubTreeCost="1">
                  <QueryPlan>
                    <MissingIndexes>
                      <MissingIndexGroup Impact="92.5">
                        <MissingIndex Database="[db]" Schema="[dbo]" Table="[Siparis]">
                          <ColumnGroup Usage="EQUALITY"><Column Name="[MusteriId]" ColumnId="2" /></ColumnGroup>
                          <ColumnGroup Usage="INEQUALITY"><Column Name="[Tarih]" ColumnId="3" /></ColumnGroup>
                          <ColumnGroup Usage="INCLUDE"><Column Name="[Tutar]" ColumnId="4" /></ColumnGroup>
                        </MissingIndex>
                      </MissingIndexGroup>
                    </MissingIndexes>
                    <RelOp NodeId="0" PhysicalOp="Table Scan" EstimateRows="1" EstimatedTotalSubtreeCost="1" />
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        PlanEksikIndexi oneri = Assert.Single(MssqlPlanOkuyucu.Coz(xml, false).Ifadeler[0].EksikIndexler);

        Assert.Equal(92.5, oneri.Etki, 1);
        Assert.Equal("[dbo].[Siparis]", oneri.Tablo);
        Assert.Equal(["[MusteriId]"], oneri.EsitlikKolonlari);
        Assert.Equal(["[Tarih]"], oneri.AralikKolonlari);
        Assert.Equal(["[Tutar]"], oneri.DahilKolonlar);

        string script = oneri.Script();
        Assert.Contains("CREATE NONCLUSTERED INDEX", script);
        Assert.Contains("([MusteriId], [Tarih])", script);
        Assert.Contains("INCLUDE ([Tutar])", script);
        Assert.Contains("ölçmeden uygulamayın", script);   // öneri vaat değildir
    }

    [Fact]
    public void Cok_ifadeli_batch_her_ifade_icin_ayri_plan_verir()
    {
        string xml = $"""
            <ShowPlanXML xmlns="{Ns}">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT 1" StatementSubTreeCost="1">
                  <QueryPlan><RelOp NodeId="0" PhysicalOp="Constant Scan" EstimateRows="1" EstimatedTotalSubtreeCost="1" /></QueryPlan>
                </StmtSimple>
                <StmtSimple StatementText="SELECT 2" StatementSubTreeCost="2">
                  <QueryPlan><RelOp NodeId="0" PhysicalOp="Table Scan" EstimateRows="9" EstimatedTotalSubtreeCost="2" /></QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        SorguPlani plan = MssqlPlanOkuyucu.Coz(xml, gercek: false);

        Assert.Equal(2, plan.Ifadeler.Count);
        Assert.Equal("SELECT 1", plan.Ifadeler[0].IfadeMetni);
        Assert.Equal("Constant Scan", plan.Ifadeler[0].Kok!.Islem);
        Assert.Equal("Table Scan", plan.Ifadeler[1].Kok!.Islem);
    }

    [Fact]
    public void Sayilar_daima_invariant_okunur()
    {
        // 07-r2 §4 ile aynı tuzak: tr-TR'de "0.100" virgüllü sanılırsa maliyetler bozulur
        CultureInfo eski = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            PlanDugumu kok = MssqlPlanOkuyucu.Coz(UcDugumluPlan(), false).Ifadeler[0].Kok!;
            Assert.Equal(40, kok.MaliyetYuzdesi, 1);
        }
        finally
        {
            CultureInfo.CurrentCulture = eski;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<bu geçerli xml değil")]
    public void Bozuk_plan_xmli_sessizce_bos_donmez_acik_hata(string xml)
        => Assert.Throws<InvalidOperationException>(() => MssqlPlanOkuyucu.Coz(xml, false));

    [Fact]
    public void Ifadesiz_xml_acik_hata_verir()
        => Assert.Throws<InvalidOperationException>(
            () => MssqlPlanOkuyucu.Coz($"""<ShowPlanXML xmlns="{Ns}"><BatchSequence /></ShowPlanXML>""", false));
}
