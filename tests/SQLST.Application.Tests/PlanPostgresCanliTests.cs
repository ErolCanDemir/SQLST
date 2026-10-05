using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S1b CANLI KANITI: PostgreSQL plan yolunun tamamı gerçek sunucuya karşı çalışır.
/// Birim testleri yalnız çözümlemeyi kanıtlar; sunucunun gerçekten beklediğimiz biçimde
/// cevap verdiğini, <c>ANALYZE</c>'ın DML'i GERÇEKTEN çalıştırdığını ve düz <c>EXPLAIN</c>'in
/// çalıştırmadığını yalnız bunlar gösterir.
///
/// Ortam-kapılı: 127.0.0.1:5433'te PostgreSQL yoksa ATLANIR.
/// </summary>
public class PlanPostgresCanliTests : IAsyncLifetime
{
    private const string Host = "127.0.0.1";
    private const int Port = 5433;
    private const string Db = "sqlst_demo";

    private static ConnectionProfile Profil(bool saltOkunur = false) => new()
    {
        Ad = "pg-plan",
        Motor = MotorTuru.Postgres,
        Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql,
        KullaniciAdi = "postgres",
        BaglantiTimeoutSn = 10,
        SaltOkunur = saltOkunur,
    };

    private static readonly ExecuteOptions Demo = new() { VeritabaniOverride = Db };

    private readonly string _tablo = $"plan_e2e_{Guid.NewGuid():N}";
    private readonly PostgresLehcesi _lehce = new(new DpapiSecretProtector());
    private readonly SqlExecutor _executor;

    public PlanPostgresCanliTests() => _executor = new SqlExecutor(_lehce);

    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    public async Task InitializeAsync()
    {
        if (!Erisilebilir()) return;
        QueryResult r = await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE public.{_tablo} (id INT PRIMARY KEY, grup INT, ad TEXT);
            INSERT INTO public.{_tablo}
            SELECT g, g % 50, 'ad' || g FROM generate_series(1, 20000) g;
            ANALYZE public.{_tablo};
            """, Demo, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);
    }

    public async Task DisposeAsync()
    {
        if (!Erisilebilir()) return;
        await _executor.ExecuteAsync(Profil(),
            $"DROP TABLE IF EXISTS public.{_tablo};", Demo, CancellationToken.None);
    }

    private async Task<SorguPlani> PlanAlAsync(IDbOturum oturum, string sql, bool gercek)
    {
        QueryResult sonuc = await oturum.CalistirAsync(
            _lehce.PlanSorgusuYaz(sql, gercek), Demo, CancellationToken.None);
        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        return _lehce.PlanCoz(sonuc, gercek);
    }

    private async Task<int> SayAsync(IDbOturum oturum)
    {
        QueryResult r = await oturum.CalistirAsync(
            $"SELECT count(*) FROM public.{_tablo};", Demo, CancellationToken.None);
        return Convert.ToInt32(r.ResultSetler[0].Satirlar[0][0]);
    }

    [Fact]
    public async Task Tahmini_plan_alinir_ve_sorgu_CALISTIRILMAZ()
    {
        if (!Erisilebilir()) return;
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        SorguPlani plan = await PlanAlAsync(oturum, $"DELETE FROM public.{_tablo} WHERE id <= 100", gercek: false);

        Assert.False(plan.Gercek);
        Assert.NotNull(plan.Ifadeler[0].Kok);
        Assert.All(plan.TumDugumler, d => Assert.Null(d.GercekSatir));
        Assert.Equal(20000, await SayAsync(oturum));      // düz EXPLAIN çalıştırmaz
    }

    [Fact]
    public async Task ANALYZE_DML_i_GERCEKTEN_calistirir()
    {
        // Bu davranış aracın güvenlik tasarımının dayanağıdır: "gerçek plan" düğmesi
        // bir yazma ifadesinde veriyi DEĞİŞTİRİR. Kanıtlanmazsa uyarılarımız temelsiz olur.
        if (!Erisilebilir()) return;
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        int once = await SayAsync(oturum);
        await PlanAlAsync(oturum, $"DELETE FROM public.{_tablo} WHERE id <= 100", gercek: true);
        int sonra = await SayAsync(oturum);

        Assert.Equal(once - 100, sonra);                  // satırlar GERÇEKTEN silindi
    }

    [Fact]
    public async Task Salt_okunur_profilde_EXPLAIN_ANALYZE_DML_ENGELLENIR()
    {
        // REGRESYON — en kritik güvenlik davranışı: sarılmış metnin ilk sözcüğü EXPLAIN
        // olduğundan QueryService'in salt-okunur kapısı onu yazma SAYMAZ ve geçirir.
        // Bu yüzden denetim HAM sql üzerinde, sarmadan önce yapılır (SorguSekmesiViewModel).
        // Burada kapının gerçekten gerekli olduğunu kanıtlıyoruz.
        if (!Erisilebilir()) return;
        var servis = new QueryService();
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil(saltOkunur: true));

        string ham = $"DELETE FROM public.{_tablo} WHERE id <= 50";
        string sarili = _lehce.PlanSorgusuYaz(ham, gercek: true);

        // 1) HAM metin yazma sayılır → kapı çalışır
        Assert.True(QueryService.YazmaSorgusuMu(ham, MotorTuru.Postgres));
        // 2) SARILMIŞ metin yazma SAYILMAZ → kapı açık kalır (tehlike buradadır)
        Assert.False(QueryService.YazmaSorgusuMu(sarili, MotorTuru.Postgres));

        // 3) Bu yüzden salt-okunur profilde sarılmış metin sunucuya GİTMEMELİ.
        //    (Uygulamada bu, PlanAlAsync'teki ham-metin denetimiyle sağlanır.)
        QueryResult hamSonuc = await servis.RunAsync(oturum, ham, Demo, CancellationToken.None);
        Assert.NotNull(hamSonuc.Hata);
        Assert.Contains("salt-okunur", hamSonuc.Hata!.Mesaj);
    }

    [Fact]
    public async Task Gercek_planda_satirlar_dongu_ile_carpilir()
    {
        if (!Erisilebilir()) return;
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        SorguPlani plan = await PlanAlAsync(oturum,
            $"SELECT grup, count(*) FROM public.{_tablo} GROUP BY grup ORDER BY 2 DESC", gercek: true);

        Assert.True(plan.Gercek);
        Assert.Contains(plan.TumDugumler, d => d.GercekSatir is not null);

        // Taramanın gerçek satırı tablo boyutuna yakın olmalı (döngüyle çarpım doğruysa)
        PlanDugumu? tarama = plan.TumDugumler.FirstOrDefault(d => d.Islem.Contains("Scan"));
        Assert.NotNull(tarama);
        Assert.True(tarama!.GercekSatir >= 19000,
            $"tarama {tarama.GercekSatir} satır bildirdi, ~20000 bekleniyordu");
    }

    [Fact]
    public async Task Maliyet_paylari_toplami_makul_kalir()
    {
        if (!Erisilebilir()) return;
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        SorguPlani plan = await PlanAlAsync(oturum,
            $"SELECT grup, count(*) FROM public.{_tablo} GROUP BY grup", gercek: true);

        double toplam = plan.Ifadeler[0].Kok!.Hepsi().Sum(d => d.MaliyetYuzdesi);
        // Erken durma yoksa ~%100 olmalı; kırpma olduysa altında kalır ama asla aşmamalı
        Assert.InRange(toplam, 80.0, 101.0);
    }

    [Fact]
    public async Task Erken_durmali_planda_pay_PATLAMAZ_ve_not_dusulur()
    {
        // REGRESYON: PK üzerinden LIMIT → Index Scan'li erken durma. Önce çocuğun payı
        // kök maliyetine bölünüp yüz binlerce yüzdeye çıkıyordu (canlı ölçüm: %3.291.088).
        // "ORDER BY ad" Sort'a düştüğü için oranı patlatmıyordu — bu yüzden PK kullanılıyor.
        if (!Erisilebilir()) return;
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        SorguPlani plan = await PlanAlAsync(oturum,
            $"SELECT * FROM public.{_tablo} ORDER BY id LIMIT 5", gercek: true);

        Assert.All(plan.TumDugumler, d => Assert.InRange(d.MaliyetYuzdesi, 0, 100));
        Assert.True(plan.TumDugumler.Sum(d => d.MaliyetYuzdesi) <= 101,
            "payların toplamı %100'ü aşamaz");
    }

    [Fact]
    public async Task Paralel_planda_paylar_isci_sayisiyla_SISMEZ()
    {
        // REGRESYON: Gather altındaki maliyetler Actual Loops ile çarpılınca toplam %485'e
        // çıkıyordu. Paralel plan için tabloyu büyütüp paralelliği zorluyoruz.
        if (!Erisilebilir()) return;
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        await oturum.CalistirAsync($"""
            SET max_parallel_workers_per_gather = 4;
            SET parallel_setup_cost = 0;
            SET parallel_tuple_cost = 0;
            SET min_parallel_table_scan_size = 0;
            """, Demo, CancellationToken.None);

        SorguPlani plan = await PlanAlAsync(oturum,
            $"SELECT count(*) FROM public.{_tablo} WHERE grup > 0", gercek: true);

        Assert.All(plan.TumDugumler, d => Assert.InRange(d.MaliyetYuzdesi, 0, 100));
        Assert.True(plan.TumDugumler.Sum(d => d.MaliyetYuzdesi) <= 101,
            $"toplam pay %{plan.TumDugumler.Sum(d => d.MaliyetYuzdesi):F0} — işçi sayısı kadar şişmiş olabilir");
    }

    [Fact]
    public void CTE_ile_yazma_plan_kapisinda_YAZMA_sayilir()
    {
        // REGRESYON — GÜVENLİK: ilk sözcük WITH olduğu için ilk-kelime mantığı bunu OKUMA
        // sayıyordu; EXPLAIN ANALYZE ile sarılınca tüm tablo silinirdi.
        string cte = $"WITH silinen AS (DELETE FROM public.{_tablo} RETURNING *) SELECT count(*) FROM silinen";

        Assert.False(QueryService.YazmaSorgusuMu(cte, MotorTuru.Postgres));       // eski mantık kaçırıyor
        Assert.True(QueryService.PlanIcinYazmaSayilir(cte, MotorTuru.Postgres));  // plan kapısı yakalıyor
    }

    [Theory]
    [InlineData("SELECT 'DELETE FROM t' AS metin")]          // metin sabiti kod değildir
    [InlineData("-- DELETE FROM t\nSELECT 1")]               // yorum kod değildir
    [InlineData("/* UPDATE t SET x=1 */ SELECT 1")]
    public void Temkinli_denetim_metin_ve_yorumda_yanlis_alarm_vermez(string sql)
        => Assert.False(QueryService.PlanIcinYazmaSayilir(sql, MotorTuru.Postgres));

    [Fact]
    public async Task Postgres_eksik_index_ONERMEZ()
    {
        if (!Erisilebilir()) return;
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        SorguPlani plan = await PlanAlAsync(oturum,
            $"SELECT * FROM public.{_tablo} WHERE ad = 'ad42'", gercek: false);

        Assert.All(plan.Ifadeler, i => Assert.Empty(i.EksikIndexler));
    }

    [Fact]
    public async Task Plan_yolu_oturumu_KIRLETMEZ_sonraki_sorgu_normal_calisir()
    {
        // PG'de oturum ayarı kullanılmaz (PlanAcSql boştur) — yine de sonraki sorgunun
        // etkilenmediği kanıtlanmalı (MSSQL'deki SET açık kalma regresyonunun karşılığı).
        if (!Erisilebilir()) return;
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        await PlanAlAsync(oturum, $"SELECT * FROM public.{_tablo} LIMIT 3", gercek: true);

        QueryResult sonra = await oturum.CalistirAsync(
            $"SELECT id FROM public.{_tablo} ORDER BY id LIMIT 3;", Demo, CancellationToken.None);

        Assert.True(sonra.Basarili, sonra.Hata?.Mesaj);
        ResultSetData set = Assert.Single(sonra.ResultSetler);
        Assert.Equal("id", set.Kolonlar[0].Ad);          // plan değil, gerçek sonuç
        Assert.Equal(3, set.Satirlar.Count);
    }
}
