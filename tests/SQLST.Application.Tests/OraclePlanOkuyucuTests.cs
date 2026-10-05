using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S1d: Oracle <c>PLAN_TABLE</c> satırlarından plan ağacı.
///
/// <b>DÜRÜST SINIR — fixture'lar ÖLÇÜM DEĞİL.</b> MSSQL ve PostgreSQL'de fixture sayıları
/// canlı sunucudan ölçülmüştü; Oracle'a hiç bağlanılamadığından buradaki satırlar
/// <c>PLAN_TABLE</c> şemasına dayanan kurgulardır. Bu testler "çözümleyici ID/PARENT_ID
/// ağacını ve kümülatif maliyeti doğru işliyor mu" der; "sunucu gerçekten bu satırları
/// üretiyor mu" DEMEZ — o, en sona bırakılan canlı doğrulama borcunun parçasıdır.
/// </summary>
public class OraclePlanOkuyucuTests
{
    /// <summary>OracleLehcesi.PlanOkumaSql ile AYNI kolon sırası.</summary>
    private static QueryResult Plan(params object?[][] satirlar)
    {
        var set = new ResultSetData
        {
            Kolonlar =
            [
                new KolonBilgisi("ID", "NUMBER", typeof(decimal)),
                new KolonBilgisi("PARENT_ID", "NUMBER", typeof(decimal)),
                new KolonBilgisi("OPERATION", "VARCHAR2", typeof(string)),
                new KolonBilgisi("OPTIONS", "VARCHAR2", typeof(string)),
                new KolonBilgisi("OBJECT_OWNER", "VARCHAR2", typeof(string)),
                new KolonBilgisi("OBJECT_NAME", "VARCHAR2", typeof(string)),
                new KolonBilgisi("CARDINALITY", "NUMBER", typeof(decimal)),
                new KolonBilgisi("COST", "NUMBER", typeof(decimal)),
                new KolonBilgisi("ACCESS_PREDICATES", "VARCHAR2", typeof(string)),
                new KolonBilgisi("FILTER_PREDICATES", "VARCHAR2", typeof(string)),
            ],
            Satirlar = [.. satirlar],
        };
        return new QueryResult { Basarili = true, ResultSetler = [set] };
    }

    /// <summary>
    /// Klasik üç satırlı plan: SELECT STATEMENT (kök) → HASH JOIN → iki tam tarama.
    /// COST kümülatiftir: kök 100, join 100, taramalar 30 ve 50.
    /// </summary>
    private static QueryResult UcDugumluPlan() => Plan(
        [0m, null, "SELECT STATEMENT", null, null, null, 500m, 100m, null, null],
        [1m, 0m, "HASH JOIN", null, null, null, 500m, 100m, null, "M.ID=S.MUSTERI_ID"],
        [2m, 1m, "TABLE ACCESS", "FULL", "SATIS", "MUSTERI", 100m, 30m, null, null],
        [3m, 1m, "TABLE ACCESS", "FULL", "SATIS", "SIPARIS", 500m, 50m, null, null]);

    [Fact]
    public void Agac_ID_ve_PARENT_ID_sutunlarindan_kurulur()
    {
        // Girinti/metin ayrıştırma YOK — ağaç yapısal sütunlardan gelir
        SorguPlani plan = OraclePlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);

        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        Assert.Equal("SELECT STATEMENT", kok.Islem);

        PlanDugumu join = Assert.Single(kok.Cocuklar);
        Assert.Equal("HASH JOIN", join.Islem);
        Assert.Equal(2, join.Cocuklar.Count);
        Assert.Equal(4, kok.Hepsi().Count());
    }

    [Fact]
    public void Maliyet_KUMULATIFtir_kendi_payi_cikarilir()
    {
        // COST alt ağacın toplamıdır (MSSQL deseni). Kümülatifi yüzde sanmak kökü daima
        // %100 gösterip planı işe yaramaz kılardı.
        SorguPlani plan = OraclePlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        PlanDugumu join = kok.Cocuklar[0];

        Assert.Equal(0, kok.MaliyetYuzdesi, 1);        // 100 − 100
        Assert.Equal(20, join.MaliyetYuzdesi, 1);      // (100 − 80) / 100
        Assert.Equal(30, join.Cocuklar[0].MaliyetYuzdesi, 1);
        Assert.Equal(50, join.Cocuklar[1].MaliyetYuzdesi, 1);

        Assert.Equal(100, kok.Hepsi().Sum(d => d.MaliyetYuzdesi), 1);
        Assert.All(plan.TumDugumler, d => Assert.InRange(d.MaliyetYuzdesi, 0, 100));
    }

    [Fact]
    public void Islem_ve_secenek_birlestirilir_nesne_adi_sahibiyle_yazilir()
    {
        SorguPlani plan = OraclePlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);
        PlanDugumu tarama = plan.Ifadeler[0].Kok!.Cocuklar[0].Cocuklar[0];

        Assert.Equal("TABLE ACCESS (FULL)", tarama.Islem);
        Assert.Contains("SATIS.MUSTERI", tarama.Ayrinti);
    }

    [Fact]
    public void Kardinalite_tahmini_satir_olarak_okunur()
    {
        SorguPlani plan = OraclePlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);
        Assert.Equal(500, plan.Ifadeler[0].Kok!.TahminiSatir);
        Assert.Equal(100, plan.Ifadeler[0].Kok!.Cocuklar[0].Cocuklar[0].TahminiSatir);
    }

    [Fact]
    public void Tam_tablo_taramasi_ve_kartezyen_uyarilari_turetilir()
    {
        SorguPlani plan = OraclePlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);
        PlanDugumu tarama = plan.Ifadeler[0].Kok!.Cocuklar[0].Cocuklar[0];
        Assert.Contains(tarama.Uyarilar, u => u.Contains("Tam tablo taraması"));

        QueryResult kartezyen = Plan(
            [0m, null, "SELECT STATEMENT", null, null, null, 1m, 10m, null, null],
            [1m, 0m, "MERGE JOIN", "CARTESIAN", null, null, 1m, 10m, null, null]);
        PlanDugumu join = OraclePlanOkuyucu.Coz(kartezyen, false).Ifadeler[0].Kok!.Cocuklar[0];
        Assert.Contains(join.Uyarilar, u => u.Contains("Kartezyen"));
    }

    [Fact]
    public void Gercek_satir_yoktur_gercek_plan_bu_motorda_kapali()
    {
        SorguPlani plan = OraclePlanOkuyucu.Coz(UcDugumluPlan(), gercek: false);
        Assert.False(plan.Gercek);
        Assert.All(plan.TumDugumler, d => Assert.Null(d.GercekSatir));

        var lehce = new OracleLehcesi(new DpapiSecretProtector());
        Assert.True(lehce.PlanDestekler);
        Assert.False(lehce.PlanGercekDestekler);
        Assert.Throws<NotSupportedException>(() => lehce.PlanSorgusuYaz("SELECT 1 FROM dual", gercek: true));
    }

    [Fact]
    public void Plan_IKI_ADIMLIDIR_ve_ifadelerde_noktali_virgul_yoktur()
    {
        var lehce = new OracleLehcesi(new DpapiSecretProtector());

        // 1. adım: PLAN_TABLE'a yazar, satır döndürmez. STATEMENT_ID ile İŞARETLENİR
        // (A1/B1): paylaşımlı bir PLAN_TABLE'da kendi satırlarımızı ayırt edebilmek için.
        string yaz = lehce.PlanSorgusuYaz("SELECT * FROM dual;", gercek: false);
        Assert.Equal("EXPLAIN PLAN SET STATEMENT_ID = 'SQLST' FOR SELECT * FROM dual", yaz);
        Assert.DoesNotContain(";", yaz);                 // ORA-00933

        // 2. adım: plan buradan OKUNUR — yalnız BİZİM en son planımız. Yalnız MAX(plan_id)
        // bakılıyordu ve araya giren başka bir oturumun planı bu sekmenin planı diye
        // gösterilebiliyordu (A1/B1).
        string oku = lehce.PlanOkumaSql(false);
        Assert.Contains("plan_table", oku);
        Assert.Contains("parent_id", oku);
        Assert.Contains("statement_id = 'SQLST'", oku);
        Assert.DoesNotContain(";", oku);

        // Temizlik de KENDİ satırlarımızla sınırlı: filtresiz DELETE, elle oluşturulmuş
        // (paylaşımlı) bir PLAN_TABLE'da başka kullanıcıların planlarını siliyordu (A1/B1).
        string kapat = lehce.PlanKapatSql(false);
        Assert.Contains("DELETE FROM plan_table", kapat);
        Assert.Contains("'SQLST'", kapat);
        // Ad değişiminde (2026-07-20) temizlik ESKİ etiketi de kapsamalı: yoksa 0.5.0'ın
        // yazdığı satırlar paylaşımlı bir PLAN_TABLE'da sonsuza dek kalırdı.
        Assert.Contains("'MINISSMS'", kapat);
        Assert.True(lehce.PlanTekIfadeIster);
    }

    [Fact]
    public void Diger_motorlar_tek_adimlidir()
    {
        var koruyucu = new DpapiSecretProtector();
        Assert.Equal("", new MssqlLehcesi(koruyucu).PlanOkumaSql(false));
        Assert.Equal("", new PostgresLehcesi(koruyucu).PlanOkumaSql(false));
        Assert.Equal("", new MySqlLehcesi(koruyucu).PlanOkumaSql(false));
    }

    [Fact]
    public void Oracle_eksik_index_ONERMEZ()
        => Assert.Empty(OraclePlanOkuyucu.Coz(UcDugumluPlan(), false).Ifadeler[0].EksikIndexler);

    [Fact]
    public void Bos_PLAN_TABLE_sessizce_bos_donmez_acik_hata()
    {
        Assert.Throws<InvalidOperationException>(
            () => OraclePlanOkuyucu.Coz(new QueryResult { Basarili = true }, false));
        Assert.Throws<InvalidOperationException>(
            () => OraclePlanOkuyucu.Coz(Plan(), false));
    }

    [Fact]
    public void Koksuz_satir_kumesi_acik_hata_verir()
    {
        // Her satırın PARENT_ID'si dolu → kök yok; sessizce boş ağaç dönmemeli
        QueryResult koksuz = Plan(
            [1m, 0m, "HASH JOIN", null, null, null, 1m, 10m, null, null],
            [2m, 1m, "TABLE ACCESS", "FULL", null, "T", 1m, 5m, null, null]);

        Assert.Throws<InvalidOperationException>(() => OraclePlanOkuyucu.Coz(koksuz, false));
    }

    [Fact]
    public void Eksik_kolon_ve_NULL_degerler_cokmeye_yol_acmaz()
    {
        // CARDINALITY/COST NULL olabilir (ör. bazı DDL planlarında) — okuyucu dayanmalı
        QueryResult eksik = Plan(
            [0m, null, "SELECT STATEMENT", null, null, null, null, null, null, null],
            [1m, 0m, "TABLE ACCESS", "FULL", null, "T", null, null, null, null]);

        SorguPlani plan = OraclePlanOkuyucu.Coz(eksik, false);
        Assert.Equal(0, plan.Ifadeler[0].ToplamMaliyet);
        Assert.All(plan.TumDugumler, d => Assert.Equal(0, d.MaliyetYuzdesi));
        Assert.Equal(2, plan.TumDugumler.Count());
    }
}
