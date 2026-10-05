using System.Net.Sockets;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S1e: MongoDB <c>explain</c> çözümleme (birim). Fixture'lar CANLI mongod 8.0.12
/// çıktısından alındı — MySQL/Oracle'ın aksine burada test sunucusu var.
/// </summary>
public class MongoPlanOkuyucuTests
{
    /// <summary>Mongo sonucu: üst düzey anahtarlar KOLON olur (MongoSonucEsleyici deseni).</summary>
    private static QueryResult PlanSonucu(string? queryPlanner, string? executionStats)
    {
        var kolonlar = new List<KolonBilgisi> { new("explainVersion", "string", typeof(string)) };
        var deger = new List<object?> { "2" };

        if (queryPlanner is not null)
        {
            kolonlar.Add(new KolonBilgisi("queryPlanner", "object", typeof(string)));
            deger.Add(queryPlanner);
        }
        if (executionStats is not null)
        {
            kolonlar.Add(new KolonBilgisi("executionStats", "object", typeof(string)));
            deger.Add(executionStats);
        }
        kolonlar.Add(new KolonBilgisi("ok", "double", typeof(double)));
        deger.Add(1.0);

        return new QueryResult
        {
            Basarili = true,
            ResultSetler = [new ResultSetData { Kolonlar = kolonlar, Satirlar = [deger.ToArray()] }],
        };
    }

    /// <summary>Canlı çıktıdan: index'siz filtre → COLLSCAN.</summary>
    private const string CollscanPlanner = """
        {
          "namespace": "sqlst_demo.musteri",
          "parsedQuery": { "sehir": { "$eq": "Ankara" } },
          "winningPlan": { "stage": "COLLSCAN", "filter": { "sehir": { "$eq": "Ankara" } }, "direction": "forward" },
          "rejectedPlans": []
        }
        """;

    private const string CollscanStats = """
        {
          "executionSuccess": true,
          "nReturned": 0,
          "executionTimeMillis": 0,
          "totalKeysExamined": 0,
          "totalDocsExamined": 3,
          "executionStages": {
            "stage": "COLLSCAN",
            "filter": { "sehir": { "$eq": "Ankara" } },
            "nReturned": 0,
            "works": 4,
            "direction": "forward",
            "docsExamined": 3
          }
        }
        """;

    [Fact]
    public void Tahmini_planda_winningPlan_agaci_okunur()
    {
        SorguPlani plan = MongoPlanOkuyucu.Coz(PlanSonucu(CollscanPlanner, null), gercek: false);

        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        Assert.Equal("COLLSCAN", kok.Islem);
        Assert.Contains("sqlst_demo.musteri", plan.Ifadeler[0].IfadeMetni);
        Assert.All(plan.TumDugumler, d => Assert.Null(d.GercekSatir));
    }

    [Fact]
    public void SBE_motorunda_agac_bir_katman_DERINDEDIR()
    {
        // Mongo 8 SBE: winningPlan.queryPlan; klasik motorda winningPlan'ın kendisi.
        // Bu katman atlanmazsa ağaç HİÇ bulunamaz (canlı aggregate çıktısında görüldü).
        const string sbe = """
            {
              "namespace": "sqlst_demo.siparis",
              "winningPlan": {
                "queryPlan": {
                  "stage": "GROUP",
                  "inputStage": { "stage": "COLLSCAN", "direction": "forward" }
                },
                "slotBasedPlan": { "stages": "[3] group ..." }
              },
              "rejectedPlans": []
            }
            """;

        PlanDugumu kok = MongoPlanOkuyucu.Coz(PlanSonucu(sbe, null), gercek: false).Ifadeler[0].Kok!;

        Assert.Equal("GROUP", kok.Islem);
        Assert.Equal("COLLSCAN", Assert.Single(kok.Cocuklar).Islem);
    }

    [Fact]
    public void Gercek_planda_olculen_sayilar_executionStagestan_gelir()
    {
        SorguPlani plan = MongoPlanOkuyucu.Coz(PlanSonucu(CollscanPlanner, CollscanStats), gercek: true);

        Assert.True(plan.Gercek);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        Assert.Equal(0, kok.GercekSatir);
        Assert.Contains("incelenen belge: 3", kok.Ayrinti);
        Assert.Contains("0 belge", plan.Ifadeler[0].IfadeMetni);
    }

    [Fact]
    public void MALIYET_UYDURULMAZ_Mongoda_maliyet_modeli_yok()
    {
        // Diğer dört motor maliyet üretir; Mongo üretmez. Yüzde uydurmak yanlış sıralama
        // gösterirdi → 0 kalır ve plana AÇIK not düşülür (MariaDB'deki kararın aynısı).
        SorguPlani plan = MongoPlanOkuyucu.Coz(PlanSonucu(CollscanPlanner, CollscanStats), gercek: true);

        Assert.Equal(0, plan.Ifadeler[0].ToplamMaliyet);
        Assert.All(plan.TumDugumler, d => Assert.Equal(0, d.MaliyetYuzdesi));
        Assert.Contains(plan.Ifadeler[0].Kok!.Uyarilar, u => u.Contains("maliyet modeli sunmaz"));
    }

    [Fact]
    public void COLLSCAN_ve_bellekte_siralama_uyarilir()
    {
        SorguPlani plan = MongoPlanOkuyucu.Coz(PlanSonucu(CollscanPlanner, CollscanStats), gercek: true);
        Assert.Contains(plan.TumDugumler.SelectMany(d => d.Uyarilar),
            u => u.Contains("hiçbir index kullanmıyor"));

        const string sortPlanner = """
            { "namespace": "d.c", "winningPlan": { "stage": "SORT", "sortPattern": { "ad": 1 },
              "inputStage": { "stage": "COLLSCAN", "direction": "forward" } }, "rejectedPlans": [] }
            """;
        SorguPlani sortPlan = MongoPlanOkuyucu.Coz(PlanSonucu(sortPlanner, null), gercek: false);
        Assert.Contains(sortPlan.TumDugumler.SelectMany(d => d.Uyarilar),
            u => u.Contains("Bellekte sıralama"));
    }

    [Fact]
    public void Cok_belge_incelenip_az_donuyorsa_index_adayi_denir()
    {
        const string stats = """
            {
              "executionSuccess": true, "nReturned": 2, "totalKeysExamined": 0, "totalDocsExamined": 5000,
              "executionStages": { "stage": "COLLSCAN", "nReturned": 2, "docsExamined": 5000 }
            }
            """;

        SorguPlani plan = MongoPlanOkuyucu.Coz(PlanSonucu(CollscanPlanner, stats), gercek: true);
        IEnumerable<string> hepsi = plan.TumDugumler.SelectMany(d => d.Uyarilar);

        Assert.Contains(hepsi, u => u.Contains("index adayı"));
        Assert.Contains(hepsi, u => u.Contains("Hiç index anahtarı okunmadı"));
    }

    [Fact]
    public void Index_kullanan_planda_index_adi_gosterilir_ve_COLLSCAN_uyarisi_YOK()
    {
        const string ixPlanner = """
            {
              "namespace": "d.c",
              "winningPlan": {
                "stage": "FETCH",
                "inputStage": { "stage": "IXSCAN", "indexName": "ix_sehir",
                                "keyPattern": { "sehir": 1 }, "direction": "forward" }
              },
              "rejectedPlans": [ { "stage": "COLLSCAN" } ]
            }
            """;

        SorguPlani plan = MongoPlanOkuyucu.Coz(PlanSonucu(ixPlanner, null), gercek: false);
        PlanDugumu kok = plan.Ifadeler[0].Kok!;
        PlanDugumu ix = Assert.Single(kok.Cocuklar);

        Assert.Equal("IXSCAN", ix.Islem);
        Assert.Contains("ix_sehir", ix.Ayrinti);
        Assert.DoesNotContain(plan.TumDugumler.SelectMany(d => d.Uyarilar),
            u => u.Contains("hiçbir index kullanmıyor"));
        Assert.Contains(kok.Uyarilar, u => u.Contains("1 alternatif planı eledi"));
    }

    [Fact]
    public void Coklu_alt_asama_inputStages_ile_okunur()
    {
        const string orPlanner = """
            {
              "namespace": "d.c",
              "winningPlan": {
                "stage": "OR",
                "inputStages": [
                  { "stage": "IXSCAN", "indexName": "ix_a" },
                  { "stage": "IXSCAN", "indexName": "ix_b" }
                ]
              }
            }
            """;

        PlanDugumu kok = MongoPlanOkuyucu.Coz(PlanSonucu(orPlanner, null), false).Ifadeler[0].Kok!;
        Assert.Equal("OR", kok.Islem);
        Assert.Equal(2, kok.Cocuklar.Count);
    }

    [Fact]
    public void Sorgu_explain_ile_sarilir_ve_ayrinti_duzeyi_dogru_secilir()
    {
        const string sorgu = """{ "find": "musteri", "filter": { "sehir": "Ankara" } }""";

        Assert.Contains("\"verbosity\": \"queryPlanner\"", MongoPlanOkuyucu.SorguYaz(sorgu, gercek: false));
        Assert.Contains("\"verbosity\": \"executionStats\"", MongoPlanOkuyucu.SorguYaz(sorgu, gercek: true));
        Assert.Contains("\"explain\"", MongoPlanOkuyucu.SorguYaz(sorgu, false));
        Assert.Contains("\"find\": \"musteri\"", MongoPlanOkuyucu.SorguYaz(sorgu, false));
    }

    [Fact]
    public void JSON_olmayan_sorgu_acik_hata_verir()
        => Assert.Throws<InvalidOperationException>(() => MongoPlanOkuyucu.SorguYaz("SELECT 1", false));

    [Fact]
    public void Mongo_eksik_index_ONERMEZ()
        => Assert.Empty(MongoPlanOkuyucu.Coz(PlanSonucu(CollscanPlanner, null), false).Ifadeler[0].EksikIndexler);

    [Fact]
    public void Bos_ve_bozuk_cikti_sessizce_bos_donmez_acik_hata()
    {
        Assert.Throws<InvalidOperationException>(
            () => MongoPlanOkuyucu.Coz(new QueryResult { Basarili = true }, false));
        Assert.Throws<InvalidOperationException>(
            () => MongoPlanOkuyucu.Coz(PlanSonucu(null, null), false));
        // queryPlanner var ama içinde winningPlan yok
        Assert.Throws<InvalidOperationException>(
            () => MongoPlanOkuyucu.Coz(PlanSonucu("""{ "namespace": "d.c" }""", null), false));
    }
}

/// <summary>
/// V5-S1e CANLI KANITI: gerçek mongod'a karşı. En kritik davranış, <c>explain</c>'in bir
/// yazma işlemini <b>UYGULAMAMASI</b> — PostgreSQL'de tam tersi olduğu için bu fark
/// arayüzdeki uyarı kararını belirliyor.
///
/// Ortam-kapılı: 127.0.0.1:27027'de mongod yoksa ATLANIR.
/// </summary>
public class MongoPlanCanliTests : IAsyncLifetime
{
    private const string Host = "127.0.0.1";
    private const int Port = 27027;

    /// <summary>
    /// KENDİ koleksiyonu. Paylaşılan demo koleksiyonlarını kullanmak yanlış kırmızı verir:
    /// <c>MongoTests</c> paralel koşarken <c>musteri</c>'yi DROP edip yeniden dolduruyor
    /// (MongoTests.cs:164) ve testler tam o aralığa denk gelebiliyordu. Diğer canlı test
    /// sınıflarındaki (LocalDB, PostgreSQL) "kendi tablonu yarat" deseninin aynısı.
    /// </summary>
    private readonly string _kob = $"plan_e2e_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        if (!Erisilebilir()) return;
        await Executor().ExecuteAsync(Profil(), $$"""
            { "insert": "{{_kob}}", "documents": [
                { "ad": "Ali",  "sehir": "Ankara" },
                { "ad": "Ayşe", "sehir": "İzmir"  },
                { "ad": "Veli", "sehir": "Ankara" } ] }
            """, Demo, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (!Erisilebilir()) return;
        await Executor().ExecuteAsync(Profil(), $$"""{ "drop": "{{_kob}}" }""", Demo, CancellationToken.None);
    }

    private static ConnectionProfile Profil() => new()
    {
        Ad = "mongo-plan",
        Motor = MotorTuru.Mongo,
        Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 10,
    };

    private static readonly ExecuteOptions Demo = new() { VeritabaniOverride = "sqlst_demo" };

    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    private static MongoExecutor Executor() => new(new DpapiSecretProtector());

    private static async Task<SorguPlani> PlanAlAsync(string sorgu, bool gercek)
    {
        QueryResult sonuc = await Executor().ExecuteAsync(
            Profil(), MongoPlanOkuyucu.SorguYaz(sorgu, gercek), Demo, CancellationToken.None);
        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        return MongoPlanOkuyucu.Coz(sonuc, gercek);
    }

    private static async Task<int> SayAsync(string koleksiyon)
    {
        QueryResult r = await Executor().ExecuteAsync(
            Profil(), $$"""{ "count": "{{koleksiyon}}" }""", Demo, CancellationToken.None);
        return Convert.ToInt32(r.ResultSetler[0].Satirlar[0][0]);
    }

    [Fact]
    public async Task Tahmini_plan_gercek_sunucudan_okunur()
    {
        if (!Erisilebilir()) return;

        SorguPlani plan = await PlanAlAsync(
            $$"""{ "find": "{{_kob}}", "filter": { "sehir": "Ankara" } }""", false);

        Assert.False(plan.Gercek);
        Assert.NotNull(plan.Ifadeler[0].Kok);
        Assert.Contains(_kob, plan.Ifadeler[0].IfadeMetni);
    }

    [Fact]
    public async Task Gercek_planda_olculen_belge_sayilari_gelir()
    {
        if (!Erisilebilir()) return;

        SorguPlani plan = await PlanAlAsync($$"""{ "find": "{{_kob}}", "filter": {} }""", true);

        Assert.True(plan.Gercek);
        Assert.Contains(plan.TumDugumler, d => d.GercekSatir == 3);          // seed edilen belge sayısı
        Assert.Contains(plan.TumDugumler, d => d.Ayrinti?.Contains("incelenen belge: 3") == true);
    }

    [Fact]
    public async Task EXPLAIN_YAZMAYI_UYGULAMAZ_PostgreSQLin_tersine()
    {
        // KRİTİK: PG'de EXPLAIN ANALYZE DELETE satırları GERÇEKTEN siler; Mongo'da explain
        // yazmayı uygulamaz (kazanan plan BATCHED_DELETE görünür ama uygulanmaz). Arayüzde
        // yazma onayı sormama kararımızın dayanağı budur — olmayan bir riski uyarmak
        // gerçek uyarılara olan güveni aşındırırdı.
        //
        // KENDİ koleksiyonunu kullanır: paylaşılan demo koleksiyonunda sayım yapmak, paralel
        // koşan diğer Mongo testleri onu değiştirdiğinde YANLIŞ kırmızı verirdi (yaşandı).
        if (!Erisilebilir()) return;

        string kob = $"plan_yazma_{Guid.NewGuid():N}";
        try
        {
            await Executor().ExecuteAsync(Profil(),
                $$"""{ "insert": "{{kob}}", "documents": [ { "a": 1 }, { "a": 2 }, { "a": 3 } ] }""",
                Demo, CancellationToken.None);
            Assert.Equal(3, await SayAsync(kob));

            SorguPlani plan = await PlanAlAsync(
                $$"""{ "delete": "{{kob}}", "deletes": [ { "q": {}, "limit": 0 } ] }""", true);

            Assert.Equal(3, await SayAsync(kob));        // hiçbir belge silinmedi
            Assert.Contains(plan.TumDugumler, d => d.Islem.Contains("DELETE"));  // plan yine de okundu
        }
        finally
        {
            await Executor().ExecuteAsync(Profil(),
                $$"""{ "drop": "{{kob}}" }""", Demo, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Toplulastirma_plani_da_okunur()
    {
        if (!Erisilebilir()) return;

        SorguPlani plan = await PlanAlAsync(
            $$"""{ "aggregate": "{{_kob}}", "pipeline": [ { "$group": { "_id": "$sehir", "t": { "$sum": 1 } } } ], "cursor": {} }""",
            true);

        Assert.NotNull(plan.Ifadeler[0].Kok);
        Assert.NotEmpty(plan.TumDugumler);
    }

    [Fact]
    public async Task Plan_sonrasi_oturum_kirlenmez()
    {
        if (!Erisilebilir()) return;

        await PlanAlAsync($$"""{ "find": "{{_kob}}", "filter": {} }""", true);

        QueryResult sonra = await Executor().ExecuteAsync(
            Profil(), $$"""{ "find": "{{_kob}}", "limit": 2 }""", Demo, CancellationToken.None);

        Assert.True(sonra.Basarili, sonra.Hata?.Mesaj);
        Assert.DoesNotContain(sonra.ResultSetler[0].Kolonlar, k => k.Ad == "queryPlanner");
        Assert.Equal(2, sonra.ResultSetler[0].Satirlar.Count);   // plan değil, gerçek belgeler
    }
}
