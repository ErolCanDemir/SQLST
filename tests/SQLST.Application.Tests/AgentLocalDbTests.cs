using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// ⏱ SQL Agent — gerçek LocalDB msdb'sinde (v23-S16, karar K5). LocalDB'de Agent SERVİSİ yoktur
/// ama msdb prosedürleri vardır: job tanımlanır, listelenir, adım/zamanlama/geçmiş okunur — ekranın
/// kullandığı TÜM okuma sorguları burada gerçek sunucuya karşı koşar. Başlatma 22022 ile reddedilir
/// (Türkçe yönlendirme doğrulanır); gerçek çalıştırma/durdurma turu kullanıcının Agent'lı sunucusunda.
/// Her test kendi benzersiz job'unu kurar ve finally'de siler (msdb sunucu geneli).
/// </summary>
public class AgentLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());

    private static Task<QueryResult> Msdb(string sql)
        => Executor.ExecuteAsync(Profil(), sql, new ExecuteOptions { VeritabaniOverride = "msdb" }, CancellationToken.None);

    private static async Task<Guid> JobKurAsync(string ad)
    {
        QueryResult r = await Msdb($"""
            DECLARE @id uniqueidentifier;
            EXEC msdb.dbo.sp_add_job @job_name = N'{ad}', @description = N'Fatura aktarımı — test', @job_id = @id OUTPUT;
            EXEC msdb.dbo.sp_add_jobstep @job_id = @id, @step_name = N'Staging''e al', @subsystem = N'TSQL',
                 @command = N'SELECT N''ERP'';', @database_name = N'master', @on_success_action = 3;
            EXEC msdb.dbo.sp_add_jobstep @job_id = @id, @step_name = N'Birleştir', @subsystem = N'TSQL',
                 @command = N'SELECT 2;', @database_name = N'tempdb', @retry_attempts = 2, @retry_interval = 5;
            EXEC msdb.dbo.sp_add_jobschedule @job_id = @id, @name = N'Her 15 dk', @freq_type = 8, @freq_interval = 62,
                 @freq_recurrence_factor = 1, @freq_subday_type = 4, @freq_subday_interval = 15,
                 @active_start_time = 80000, @active_end_time = 180000;
            EXEC msdb.dbo.sp_add_jobserver @job_id = @id, @server_name = N'(local)';
            INSERT msdb.dbo.sysjobhistory (job_id, step_id, step_name, sql_message_id, sql_severity, message, run_status,
                   run_date, run_time, run_duration, operator_id_emailed, operator_id_netsent, operator_id_paged, retries_attempted, server)
            VALUES (@id, 1, N'Staging''e al', 0, 0, N'(1284 rows affected)', 1, 20261006, 94500, 3, 0, 0, 0, 0, N'x'),
                   (@id, 2, N'Birleştir', 2627, 14, N'Violation of PRIMARY KEY constraint ''PK_Fatura''.', 0, 20261006, 94503, 38, 0, 0, 0, 0, N'x'),
                   (@id, 0, N'(Job outcome)', 0, 0, N'The job failed.', 0, 20261006, 94500, 41, 0, 0, 0, 0, N'x');
            SELECT @id;
            """);
        Assert.Null(r.Hata);
        return (Guid)r.ResultSetler[^1].Satirlar[0][0]!;
    }

    private static Task<QueryResult> SilAsync(string ad) => Msdb($"""
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'{ad}')
            EXEC msdb.dbo.sp_delete_job @job_name = N'{ad}';
        """);

    [Fact]
    public async Task Durum_localdbde_agent_yok_ve_sysadmin()
    {
        AgentSunucuDurumu? d = AgentSorgulari.DurumOku(await Msdb(AgentSorgulari.DurumSorgusu()));

        Assert.NotNull(d);
        Assert.Equal(4, d.MotorSurumu);           // Express (LocalDB)
        Assert.Equal(AgentServisi.Yok, d.Servis);
        Assert.True(d.Sysadmin);
        Assert.True(d.GorebilirMi);
        Assert.True(Math.Abs((d.SunucuSaati - DateTime.Now).TotalMinutes) < 5);
    }

    [Fact]
    public async Task Liste_gecmis_adim_ve_zamanlama_gercek_msdbden_okunur()
    {
        string ad = $"sqlst_test_{Guid.NewGuid():N}";
        try
        {
            Guid id = await JobKurAsync(ad);

            IReadOnlyList<AgentGecmisSatiri> gecmis = AgentSorgulari.GecmisOku(await Msdb(AgentSorgulari.GecmisSorgusu()));
            IReadOnlyList<AgentJob> joblar = AgentSorgulari.JoblariKur(
                await Msdb(AgentSorgulari.JobListesiSorgusu()), gecmis, await Msdb(AgentSorgulari.AktiviteSorgusu()));

            AgentJob j = Assert.Single(joblar, x => x.JobId == id);
            Assert.Equal(ad, j.Ad);
            Assert.True(j.Acik);
            Assert.Equal("Fatura aktarımı — test", j.Aciklama);   // Türkçe karakterler bozulmadan
            Assert.Equal(new DateTime(2026, 10, 6, 9, 45, 0), j.SonKosu);
            Assert.Equal(AgentSonuc.Hata, j.SonSonuc);
            Assert.Equal(41, j.SonSure);
            Assert.Equal(2, j.HataliAdim);
            Assert.Equal(2, j.AdimSayisi);
            Assert.True(j.ZamanlamaVar);
            Assert.False(j.Calisiyor);

            AgentGecmisSatiri[] jobGecmisi = [.. gecmis.Where(g => g.JobId == id)];
            Assert.Equal([0, 2, 1], jobGecmisi.Select(g => g.AdimId)); // en yeni üstte
            Assert.Equal(2627, jobGecmisi[1].MesajNo);

            AgentAdim[] adimlar = [.. AgentSorgulari.AdimlariOku(await Msdb(AgentSorgulari.AdimSorgusu(id)))];
            Assert.Equal(["Staging'e al", "Birleştir"], adimlar.Select(a => a.Ad));
            Assert.Equal("SELECT N'ERP';", adimlar[0].Komut);
            Assert.Equal(3, adimlar[0].BasaridaEylem);
            Assert.Equal("tempdb", adimlar[1].Veritabani);
            Assert.Equal(2, adimlar[1].YenidenDeneme);

            AgentZamanlama z = Assert.Single(AgentSorgulari.ZamanlamalariOku(await Msdb(AgentSorgulari.ZamanlamaSorgusu(id))));
            Assert.Equal("Hafta içi her gün, 08:00–18:00 arası her 15 dakikada bir", AgentSorgulari.ZamanlamaMetni(z));
        }
        finally
        {
            await SilAsync(ad);
        }
    }

    [Fact]
    public async Task Baslatma_agent_yokken_22022_ve_turkce_yonlendirme_acKapat_calisir()
    {
        string ad = $"sqlst_test_{Guid.NewGuid():N}";
        try
        {
            Guid id = await JobKurAsync(ad);

            QueryResult basla = await Msdb(AgentSorgulari.BaslatSql(id));
            Assert.NotNull(basla.Hata);
            Assert.Equal(22022, basla.Hata.Numara);
            Assert.Contains("Agent servisi çalışmıyor", AgentSorgulari.HataMetni(basla.Hata));

            // Aç/kapat yalnız msdb tanımını değiştirir — Agent'sız da çalışır.
            Assert.Null((await Msdb(AgentSorgulari.AcKapatSql(id, false))).Hata);
            AgentJob j = Assert.Single(AgentSorgulari.JoblariKur(await Msdb(AgentSorgulari.JobListesiSorgusu()), []),
                x => x.JobId == id);
            Assert.False(j.Acik);
        }
        finally
        {
            await SilAsync(ad);
        }
    }

    // ── S3 (v23-S18): sihirbaz script'leri gerçek msdb'de ────────────────────

    [Fact]
    public async Task Duzenleme_scripti_job_kimligini_ve_gecmisi_korur_adim_zamanlama_yenilenir()
    {
        string ad = $"sqlst_test_{Guid.NewGuid():N}";
        string yeniAd = ad + "_yeni";
        try
        {
            Guid id = await JobKurAsync(ad);
            int eskiZaman = Assert.Single(AgentSorgulari.ZamanlamalariOku(await Msdb(AgentSorgulari.ZamanlamaSorgusu(id)))).Id;
            AgentJob job = Assert.Single(AgentSorgulari.JoblariKur(await Msdb(AgentSorgulari.JobListesiSorgusu()), []), x => x.JobId == id);

            AgentAdim[] yeniAdimlar =
            [
                new(1, "Tek adım", "TSQL", "SELECT N'güncel';", "tempdb", 1, 0, 2, 0, 1, 3, null, null),
            ];
            AgentZamanlama[] yeniZaman = [new(0, "Aylık", true, 32, 2, 1, 0, 1, 1, 20261006, 99991231, 70000, 235959)];
            string script = AgentSorgulari.GuncellemeScripti(id, job with { Ad = yeniAd, Aciklama = "düzenlendi" },
                yeniAdimlar, yeniZaman, [eskiZaman], "localdb", DateTime.Now);

            QueryResult r = await Msdb(script);
            Assert.True(r.Hata is null, r.Hata?.Mesaj);

            AgentJob sonra = Assert.Single(AgentSorgulari.JoblariKur(await Msdb(AgentSorgulari.JobListesiSorgusu()), []), x => x.JobId == id);
            Assert.Equal(yeniAd, sonra.Ad);                                   // aynı kimlik, yeni ad
            Assert.Equal("düzenlendi", sonra.Aciklama);
            Assert.Equal(3, AgentSorgulari.GecmisOku(await Msdb(AgentSorgulari.GecmisSorgusu(id))).Count); // geçmiş korundu
            AgentAdim adim = Assert.Single(AgentSorgulari.AdimlariOku(await Msdb(AgentSorgulari.AdimSorgusu(id))));
            Assert.Equal(yeniAdimlar[0], adim);
            AgentZamanlama z = Assert.Single(AgentSorgulari.ZamanlamalariOku(await Msdb(AgentSorgulari.ZamanlamaSorgusu(id))));
            Assert.Equal("Her ayın ilk pazartesisi 07:00", AgentSorgulari.ZamanlamaMetni(z));
            QueryResult eski = await Msdb($"SELECT COUNT(*) FROM msdb.dbo.sysschedules WHERE schedule_id = {eskiZaman};");
            Assert.Equal(0, Convert.ToInt32(eski.ResultSetler[0].Satirlar[0][0]));  // paylaşılmayan eski zamanlama silindi
        }
        finally
        {
            await SilAsync(ad);
            await SilAsync(yeniAd);
        }
    }

    public static TheoryData<AgentZamanlama> SihirbazZamanlamalari() =>
    [
        new(0, "bir kez", true, 1, 0, 0, 0, 0, 0, 20261010, 99991231, 200000, 235959), // sunucu bir kezde gün içi türünü 0 saklar
        new(0, "günlük tekrar", true, 4, 2, 4, 30, 0, 0, 20261006, 20261231, 80000, 180000),
        new(0, "haftalık", false, 8, 42, 1, 0, 0, 2, 20261006, 99991231, 70000, 235959),
        new(0, "aylık gün", true, 16, 15, 8, 2, 0, 3, 20261006, 99991231, 0, 235959),
        new(0, "aylık göreli", true, 32, 10, 1, 0, 16, 1, 20261006, 99991231, 180000, 235959),
        new(0, "agent", true, 64, 0, 0, 0, 0, 0, 20261006, 99991231, 0, 235959),
        new(0, "cpu", true, 128, 0, 0, 0, 0, 0, 20261006, 99991231, 0, 235959),
    ];

    [Theory]
    [MemberData(nameof(SihirbazZamanlamalari))]
    public async Task Sihirbaz_zamanlama_turleri_sunucuca_kabul_edilir_ve_birebir_okunur(AgentZamanlama z)
    {
        string ad = $"sqlst_test_{Guid.NewGuid():N}";
        try
        {
            var job = new AgentJob(Guid.Empty, ad, true, "[Uncategorized (Local)]", "", "", null, AgentSonuc.Bilinmiyor,
                null, null, null, false, null, null, 1, true);
            string script = AgentSorgulari.OlusturmaScripti(job,
                [new(1, "a", "TSQL", "SELECT 1", "master", 1, 0, 2, 0, 0, 0, null, null)], [z], "localdb", DateTime.Now);
            QueryResult r = await Msdb(script);
            Assert.True(r.Hata is null, r.Hata?.Mesaj);

            Guid id = Assert.Single(AgentSorgulari.JoblariKur(await Msdb(AgentSorgulari.JobListesiSorgusu()), []), x => x.Ad == ad).JobId;
            AgentZamanlama okunan = Assert.Single(AgentSorgulari.ZamanlamalariOku(await Msdb(AgentSorgulari.ZamanlamaSorgusu(id))));
            // 64/128'de sunucu gün içi alanlarını kendi değerine çekebilir — anlamlı alanlar karşılaştırılır
            Assert.Equal((z.Ad, z.Acik, z.FreqType, z.FreqInterval, z.BaslangicTarihi, z.BitisTarihi),
                (okunan.Ad, okunan.Acik, okunan.FreqType, okunan.FreqInterval, okunan.BaslangicTarihi, okunan.BitisTarihi));
            if (z.FreqType is not (64 or 128))
                Assert.Equal(z with { Id = okunan.Id }, okunan);
        }
        finally
        {
            await SilAsync(ad);
        }
    }

    [Fact]
    public async Task Soz_dizimi_denetimi_calistirmaz_hatayi_yakalar()
    {
        string tablo = $"##sqlst_noexec_{Guid.NewGuid():N}";
        QueryResult gecerli = await Msdb(AgentSorgulari.DenetimSql($"CREATE TABLE {tablo} (x int); INSERT INTO {tablo} VALUES (1);"));
        Assert.Null(gecerli.Hata);
        QueryResult var = await Msdb($"SELECT OBJECT_ID('tempdb..{tablo}');");
        Assert.True(var.ResultSetler[0].Satirlar[0][0] is null or DBNull);   // tablo OLUŞMADI

        QueryResult hatali = await Msdb(AgentSorgulari.DenetimSql("SELEC 1 FRM x;"));
        Assert.NotNull(hatali.Hata);
        Assert.Contains("SELEC", hatali.Hata.Mesaj);
    }

    [Fact]
    public async Task Script_olarak_al_job_silinip_scriptle_birebir_geri_kurulur()
    {
        string ad = $"sqlst_test_{Guid.NewGuid():N}";
        try
        {
            Guid id = await JobKurAsync(ad);
            AgentJob job = Assert.Single(AgentSorgulari.JoblariKur(await Msdb(AgentSorgulari.JobListesiSorgusu()), []),
                x => x.JobId == id);
            AgentAdim[] adimlar = [.. AgentSorgulari.AdimlariOku(await Msdb(AgentSorgulari.AdimSorgusu(id)))];
            AgentZamanlama[] zaman = [.. AgentSorgulari.ZamanlamalariOku(await Msdb(AgentSorgulari.ZamanlamaSorgusu(id)))];
            string script = AgentSorgulari.OlusturmaScripti(job, adimlar, zaman, "localdb", DateTime.Now);

            await SilAsync(ad);
            QueryResult kur = await Msdb(script);
            Assert.True(kur.Hata is null, kur.Hata?.Mesaj);

            AgentJob yeni = Assert.Single(AgentSorgulari.JoblariKur(await Msdb(AgentSorgulari.JobListesiSorgusu()), []),
                x => x.Ad == ad);
            Assert.NotEqual(id, yeni.JobId);
            Assert.Equal(job.Aciklama, yeni.Aciklama);
            Assert.Equal(job.Sahip, yeni.Sahip);
            AgentAdim[] yeniAdimlar = [.. AgentSorgulari.AdimlariOku(await Msdb(AgentSorgulari.AdimSorgusu(yeni.JobId)))];
            Assert.Equal(adimlar, yeniAdimlar);   // kayıt eşitliği: ad · komut · db · eylemler · deneme
            AgentZamanlama yeniZaman = Assert.Single(AgentSorgulari.ZamanlamalariOku(await Msdb(AgentSorgulari.ZamanlamaSorgusu(yeni.JobId))));
            Assert.Equal(zaman[0] with { Id = yeniZaman.Id }, yeniZaman);

            // İkinci koşu: aynı adlı job varken script hata verir ve YARIM job bırakmaz.
            QueryResult ikinci = await Msdb(script);
            Assert.NotNull(ikinci.Hata);
            Assert.Single(AgentSorgulari.JoblariKur(await Msdb(AgentSorgulari.JobListesiSorgusu()), []), x => x.Ad == ad);
        }
        finally
        {
            await SilAsync(ad);
        }
    }
}
