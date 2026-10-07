using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// ⏱ SQL Agent saf çekirdeği (v23-S16 — docs/11-sql-agent-arastirma.md): Türkçe zamanlama
/// cümlesi, msdb tamsayı tarih/süre çevirisi, servis/yetki çıkarımı, liste birleştirme (son koşu ·
/// hatalı adım · çalışan) ve "Script olarak al" çıktısı. Sunucusuz; LocalDB turu AgentLocalDbTests'te.
/// </summary>
public class AgentSorgulariTests
{
    private static AgentZamanlama Z(int tur, int aralik = 1, int gunIciTur = 1, int gunIciAralik = 0,
        int goreli = 0, int tekrar = 1, int basSaat = 20000, int bitSaat = 235959, int bitTarih = 99991231)
        => new(1, "z", true, tur, aralik, gunIciTur, gunIciAralik, goreli, tekrar, 20261006, bitTarih, basSaat, bitSaat);

    [Theory]
    [InlineData(4, 1, 1, 0, 0, 1, 20000, 235959, "Her gün 02:00")]
    [InlineData(4, 3, 1, 0, 0, 1, 20000, 235959, "3 günde bir 02:00")]
    [InlineData(8, 62, 4, 15, 0, 1, 80000, 180000, "Hafta içi her gün, 08:00–18:00 arası her 15 dakikada bir")]
    [InlineData(8, 1, 1, 0, 0, 1, 233000, 235959, "Her pazar 23:30")]
    [InlineData(8, 42, 1, 0, 0, 1, 70000, 235959, "Her pazartesi, çarşamba ve cuma 07:00")]
    [InlineData(8, 65, 1, 0, 0, 2, 90000, 235959, "2 haftada bir: hafta sonu her gün 09:00")]
    [InlineData(16, 1, 1, 0, 0, 1, 60000, 235959, "Her ayın 1. günü 06:00")]
    [InlineData(16, 15, 1, 0, 0, 3, 60000, 235959, "3 ayda bir, ayın 15. günü 06:00")]
    [InlineData(32, 2, 1, 0, 1, 1, 70000, 235959, "Her ayın ilk pazartesisi 07:00")]
    [InlineData(32, 6, 1, 0, 16, 1, 180000, 235959, "Her ayın son cuması 18:00")]
    [InlineData(32, 9, 1, 0, 1, 1, 80000, 235959, "Her ayın ilk hafta içi günü 08:00")]
    [InlineData(4, 1, 8, 2, 0, 1, 0, 235959, "Her gün, gün boyu her 2 saatte bir")]
    [InlineData(4, 1, 2, 30, 0, 1, 0, 235959, "Her gün, gün boyu her 30 saniyede bir")]
    [InlineData(4, 1, 1, 0, 0, 1, 23015, 235959, "Her gün 02:30:15")]
    [InlineData(64, 0, 0, 0, 0, 0, 0, 235959, "SQL Server Agent başlarken")]
    [InlineData(128, 0, 0, 0, 0, 0, 0, 235959, "CPU boşta kaldığında")]
    public void Zamanlama_turkce_cumleye_cevrilir(int tur, int aralik, int gunIciTur, int gunIciAralik,
        int goreli, int tekrar, int bas, int bit, string beklenen)
        => Assert.Equal(beklenen, AgentSorgulari.ZamanlamaMetni(Z(tur, aralik, gunIciTur, gunIciAralik, goreli, tekrar, bas, bit)));

    [Fact]
    public void Bir_kez_zamanlamasi_tarihli_bitis_tarihi_eklenir()
    {
        Assert.Equal("Bir kez: 2026-10-06 02:00", AgentSorgulari.ZamanlamaMetni(Z(1)));
        Assert.Equal("Her gün 02:00 · 2026-12-31 tarihine kadar",
            AgentSorgulari.ZamanlamaMetni(Z(4, bitTarih: 20261231)));
    }

    [Fact]
    public void Msdb_tamsayi_tarih_ve_sure_ham_bicimde()
    {
        Assert.Equal(new DateTime(2026, 10, 6, 9, 45, 3), AgentSorgulari.AgentZamani(20261006, 94503));
        Assert.Null(AgentSorgulari.AgentZamani(0, 0));          // hiç çalışmadı
        Assert.Null(AgentSorgulari.AgentZamani(20261345, 0));   // bozuk satır — tarih uydurulmaz
        Assert.Equal("2026-10-06 09:45:03", AgentSorgulari.ZamanMetni(new DateTime(2026, 10, 6, 9, 45, 3)));
        Assert.Equal("00:14:32", AgentSorgulari.SureMetni(1432));
        Assert.Equal("01:02:10", AgentSorgulari.SureMetni(10210));
        Assert.Equal("125:00:07", AgentSorgulari.SureMetni(1250007)); // saat 99'u aşabilir
    }

    [Theory]
    [InlineData(4, "Running", AgentServisi.Yok)]        // Express/LocalDB: DMV ne derse desin yok
    [InlineData(5, null, AgentServisi.Yok)]             // Azure SQL Database
    [InlineData(3, "Running", AgentServisi.Calisiyor)]
    [InlineData(2, "Stopped", AgentServisi.Durmus)]
    [InlineData(3, null, AgentServisi.Bilinmiyor)]      // VIEW SERVER STATE yok
    [InlineData(8, null, AgentServisi.Calisiyor)]       // Managed Instance: DMV boş, Agent hep var
    public void Servis_durumu_surum_ve_dmvden_cikar(int motor, string? dmv, AgentServisi beklenen)
        => Assert.Equal(beklenen, new AgentSunucuDurumu(motor, "x", "s", dmv, true, false, false, false, DateTime.Now).Servis);

    [Fact]
    public void Yetki_rolleri_gorme_kapsamini_belirler()
    {
        AgentSunucuDurumu D(bool sa, bool ok, bool ku, bool op) => new(3, "x", "s", "Running", sa, ok, ku, op, DateTime.Now);
        Assert.False(D(false, false, false, false).GorebilirMi);
        Assert.True(D(false, false, true, false).GorebilirMi);
        Assert.False(D(false, false, true, false).HepsiniGorurMu); // UserRole: yalnız kendi job'ları
        Assert.True(D(false, true, false, false).HepsiniGorurMu);
        Assert.Contains("yalnız kendi", D(false, false, true, false).YetkiMetni);
    }

    // ── liste birleştirme ────────────────────────────────────────────────────

    private static readonly Guid J1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid J2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static QueryResult Rs(string[] kolonlar, params object?[][] satirlar) => new()
    {
        Basarili = true,
        ResultSetler = [new ResultSetData { Kolonlar = [.. kolonlar.Select(k => new KolonBilgisi(k, "x"))], Satirlar = [.. satirlar] }],
    };

    private static readonly string[] JobKolonlari =
    [
        "job_id", "name", "enabled", "description", "start_step_id", "category", "owner",
        "notify_level_eventlog", "notify_level_email", "notify_email_operator",
        "last_run_date", "last_run_time", "last_run_outcome", "next_run_date", "next_run_time",
        "current_execution_status", "current_execution_step", "has_step", "has_schedule",
    ];

    private static object?[] Job(Guid id, string ad, int durum = 4, string adim = "0 (unknown)",
        int sonTarih = 0, int sonSaat = 0, int sonSonuc = 5)
        => [id, ad, (byte)1, "açıklama", 1, "[Uncategorized (Local)]", "sa", 2, 0, "(unknown)",
            sonTarih, sonSaat, sonSonuc, 20261007, 20000, durum, adim, 3, 1];

    private static AgentGecmisSatiri G(int inst, Guid job, int adim, AgentSonuc sonuc, int saat, int sure = 10)
        => new(inst, job, "j", adim, adim == 0 ? "(Job outcome)" : $"adim{adim}", sonuc,
            AgentSorgulari.AgentZamani(20261006, saat), sure, $"m{inst}", 0, 0);

    [Fact]
    public void Son_kosu_gecmisten_hatali_adim_yalniz_son_kosudan()
    {
        // Eski koşu: adım 1 hata verdi (inst 1-2). Son koşu: adım 1 ok, adım 2 hata (inst 3-5).
        AgentGecmisSatiri[] gecmis =
        [
            G(1, J1, 1, AgentSonuc.Hata, 90000), G(2, J1, 0, AgentSonuc.Hata, 90000, 5),
            G(3, J1, 1, AgentSonuc.Basarili, 94500), G(4, J1, 2, AgentSonuc.Hata, 94503),
            G(5, J1, 0, AgentSonuc.Hata, 94500, 41),
        ];

        AgentJob j = Assert.Single(AgentSorgulari.JoblariKur(Rs(JobKolonlari, Job(J1, "Fatura")), gecmis));

        Assert.Equal(new DateTime(2026, 10, 6, 9, 45, 0), j.SonKosu);
        Assert.Equal(AgentSonuc.Hata, j.SonSonuc);
        Assert.Equal(41, j.SonSure);
        Assert.Equal(2, j.HataliAdim);
        Assert.Equal(new DateTime(2026, 10, 7, 2, 0, 0), j.SonrakiKosu);
        Assert.False(j.Calisiyor);
    }

    [Fact]
    public void Basarili_son_kosuda_hatali_adim_yok_eski_hata_tasinmaz()
    {
        AgentGecmisSatiri[] gecmis =
        [
            G(1, J1, 1, AgentSonuc.Hata, 90000), G(2, J1, 0, AgentSonuc.Hata, 90000),
            G(3, J1, 1, AgentSonuc.Basarili, 94500), G(4, J1, 0, AgentSonuc.Basarili, 94500),
        ];
        AgentJob j = Assert.Single(AgentSorgulari.JoblariKur(Rs(JobKolonlari, Job(J1, "x")), gecmis));
        Assert.Equal(AgentSonuc.Basarili, j.SonSonuc);
        Assert.Null(j.HataliAdim);
    }

    [Fact]
    public void Gecmis_yoksa_sp_help_job_son_kosu_alanlarina_duser()
    {
        AgentJob[] joblar =
        [
            .. AgentSorgulari.JoblariKur(Rs(JobKolonlari,
                Job(J1, "b-temizlenmis", sonTarih: 20261005, sonSaat: 60000, sonSonuc: 1),
                Job(J2, "a-hic")), []),
        ];
        Assert.Equal(["a-hic", "b-temizlenmis"], joblar.Select(j => j.Ad)); // ada göre sıralı
        Assert.Equal(AgentSonuc.Bilinmiyor, joblar[0].SonSonuc);
        Assert.Null(joblar[0].SonKosu);
        Assert.Equal(AgentSonuc.Basarili, joblar[1].SonSonuc);
        Assert.Null(joblar[1].SonSure); // süre yalnız geçmişte var — uydurulmaz
    }

    [Fact]
    public void Calisan_job_adimi_ve_baslangici_aktiviteden()
    {
        DateTime bas = new(2026, 10, 6, 9, 50, 0);
        QueryResult aktivite = Rs(["job_id", "start_execution_date", "stop_execution_date"],
            [J1, bas, null], [J2, bas.AddHours(-1), bas.AddMinutes(-50)]);

        AgentJob[] joblar =
        [
            .. AgentSorgulari.JoblariKur(Rs(JobKolonlari,
                Job(J1, "a", durum: 1, adim: "2 (Birleştir)"), Job(J2, "b")), [], aktivite),
        ];

        Assert.True(joblar[0].Calisiyor);
        Assert.Equal(2, joblar[0].CalisanAdim);
        Assert.Equal(bas, joblar[0].CalismaBaslangici);
        Assert.False(joblar[1].Calisiyor);
        Assert.Null(joblar[1].CalismaBaslangici); // bitmiş koşu çalışıyor sayılmaz
    }

    // ── işlemler + script ────────────────────────────────────────────────────

    [Fact]
    public void Islem_sqlleri_ve_ad_kacisi()
    {
        Assert.Equal($"EXEC msdb.dbo.sp_start_job @job_id = '{J1}';", AgentSorgulari.BaslatSql(J1));
        Assert.Equal($"EXEC msdb.dbo.sp_start_job @job_id = '{J1}', @step_name = N'O''Brien adımı';",
            AgentSorgulari.BaslatSql(J1, "O'Brien adımı"));
        Assert.Equal($"EXEC msdb.dbo.sp_stop_job @job_id = '{J1}';", AgentSorgulari.DurdurSql(J1));
        Assert.EndsWith("@enabled = 0;", AgentSorgulari.AcKapatSql(J1, false));
    }

    [Fact]
    public void Agent_calismiyor_hatasi_turkce_yonlendirir()
    {
        string m = AgentSorgulari.HataMetni(new SqlHata("SQLServerAgent is not currently running…", 22022, 0, 16));
        Assert.Contains("Agent servisi çalışmıyor", m);
        Assert.Contains("Configuration Manager", m);
    }

    [Fact]
    public void Olusturma_scripti_job_adimlar_zamanlamalar_tek_transactionda()
    {
        var job = new AgentJob(J1, "Fatura 'ERP'", true, "ETL", "LST\\etl", "açıklama", null, AgentSonuc.Bilinmiyor,
            null, null, null, false, null, null, 2, true, "DBA", 2, 2, 1);
        AgentAdim[] adimlar =
        [
            new(1, "Staging", "TSQL", "EXEC dbo.Al @k = N'ERP';", "ERP_STG", 3, 0, 2, 0, 0, 0, null, null),
            new(2, "Bildirim", "PowerShell", "Write-Host x", null, 1, 0, 2, 0, 2, 5, 7, @"C:\log\x.txt"),
        ];
        AgentZamanlama[] zaman = [Z(8, 62, 4, 15, 0, 1, 80000, 180000)];

        string s = AgentSorgulari.OlusturmaScripti(job, adimlar, zaman, "SQLPROD01", new DateTime(2026, 10, 6, 10, 0, 0));

        Assert.Contains("@job_name = N'Fatura ''ERP'''", s);                       // tırnak kaçışı
        Assert.Contains("sp_add_category @class = N'JOB', @type = N'LOCAL', @name = N'ETL'", s);
        Assert.Contains("@owner_login_name = N'LST\\etl'", s);
        Assert.Contains("@notify_email_operator_name = N'DBA'", s);
        Assert.Contains("@command = N'EXEC dbo.Al @k = N''ERP'';'", s);           // komut içi tırnak
        Assert.Contains("@database_name = N'ERP_STG'", s);
        Assert.Contains("proxy (id 7)", s);                                       // proxy adı uydurulmaz
        Assert.Contains(@"@output_file_name = N'C:\log\x.txt'", s);
        Assert.Contains("-- Hafta içi her gün, 08:00–18:00 arası her 15 dakikada bir", s);
        Assert.Contains("@freq_interval = 62", s);
        Assert.Contains("sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'", s);
        Assert.Contains("ROLLBACK TRANSACTION", s);
        Assert.True(s.IndexOf("BEGIN TRANSACTION", StringComparison.Ordinal) < s.IndexOf("COMMIT TRANSACTION", StringComparison.Ordinal));
    }

    // ── S3 (v23-S18): sihirbaz ───────────────────────────────────────────────

    private static AgentJob TaslakJob(string ad = "Fatura") => new(Guid.Empty, ad, true, "ETL", "sa", "açıklama", null,
        AgentSonuc.Bilinmiyor, null, null, null, false, null, null, 2, true);

    private static AgentAdim A(int id, string ad, int basari = 3, int basariAdim = 0, int hata = 2, int hataAdim = 0, string komut = "SELECT 1")
        => new(id, ad, "TSQL", komut, "master", basari, basariAdim, hata, hataAdim, 0, 0, null, null);

    [Fact]
    public void Guncelleme_scripti_jobu_silmeden_adim_ve_zamanlamalari_yeniden_yazar()
    {
        string s = AgentSorgulari.GuncellemeScripti(J1, TaslakJob("Yeni ad"), [A(1, "a"), A(2, "b", basari: 1)],
            [Z(4)], [7, 9], "srv", new DateTime(2026, 10, 6));

        Assert.Contains($"DECLARE @jobId uniqueidentifier = '{J1}';", s);
        Assert.DoesNotContain("sp_delete_job ", s);                                   // job silinmez
        Assert.DoesNotContain("sp_add_job\n", s);
        Assert.Contains("sp_delete_jobstep @job_id = @jobId, @step_id = 0;", s);
        Assert.Contains("sp_detach_schedule @job_id = @jobId, @schedule_id = 7, @delete_unused_schedule = 1;", s);
        Assert.Contains("@schedule_id = 9", s);
        Assert.Contains("@new_name = N'Yeni ad'", s);
        // sp_update_job (başlangıç adımı) adımlar eklendikten SONRA
        Assert.True(s.IndexOf("sp_add_jobstep", StringComparison.Ordinal) < s.IndexOf("sp_update_job", StringComparison.Ordinal));
        Assert.Contains("ROLLBACK TRANSACTION", s);
    }

    [Fact]
    public void Dogrulama_hata_ve_uyarilari_ayirir()
    {
        IReadOnlyList<(bool Hata, string Mesaj)> Dogrula(AgentJob j, AgentAdim[] a, AgentZamanlama[] z) => AgentSorgulari.Dogrula(j, a, z);

        Assert.Empty(Dogrula(TaslakJob(), [A(1, "a"), A(2, "b", basari: 1)], [Z(4)]));

        Assert.Contains(Dogrula(TaslakJob(" "), [A(1, "a", basari: 1)], []), x => x.Hata && x.Mesaj.Contains("Job adı"));
        Assert.Contains(Dogrula(TaslakJob(), [], []), x => x.Hata && x.Mesaj.Contains("En az bir adım"));
        Assert.Contains(Dogrula(TaslakJob(), [A(1, "a"), A(2, "A", basari: 1)], []), x => x.Hata && x.Mesaj.Contains("tekrar ediyor"));
        Assert.Contains(Dogrula(TaslakJob(), [A(1, "a", basari: 1, komut: " ")], []), x => x.Hata && x.Mesaj.Contains("komut boş"));
        Assert.Contains(Dogrula(TaslakJob(), [A(1, "a", basari: 4, basariAdim: 5)], []), x => x.Hata && x.Mesaj.Contains("gidilecek adım yok"));
        // kendine dönüş ve son adımda "sonraki" yalnız UYARI
        var uyar = Dogrula(TaslakJob(), [A(1, "a", basari: 1, hata: 4, hataAdim: 1)], []);
        Assert.Contains(uyar, x => !x.Hata && x.Mesaj.Contains("kendine gidiyor"));
        Assert.Contains(Dogrula(TaslakJob(), [A(1, "a")], []), x => !x.Hata && x.Mesaj.Contains("son adım"));

        Assert.Contains(Dogrula(TaslakJob(), [A(1, "a", basari: 1)], [Z(8, aralik: 0)]), x => x.Hata && x.Mesaj.Contains("en az bir gün"));
        Assert.Contains(Dogrula(TaslakJob(), [A(1, "a", basari: 1)], [Z(4, 1, 4, 15, 0, 1, 180000, 80000)]), x => x.Hata && x.Mesaj.Contains("bitiş saati"));
        Assert.Contains(Dogrula(TaslakJob(), [A(1, "a", basari: 1)], [Z(4, bitTarih: 20200101)]), x => x.Hata && x.Mesaj.Contains("bitiş tarihi"));
    }

    [Fact]
    public void Go_ile_bolme_ve_noexec_denetim_sqli()
    {
        Assert.Equal(["SELECT 1\n", "\nSELECT 2"],
            AgentSorgulari.GoIleBol("SELECT 1\nGO\nSELECT 2").Select(p => p.Replace("\r", "")));
        Assert.Single(AgentSorgulari.GoIleBol("SELECT 'GO' AS x -- GO burada ayırıcı değil"));
        Assert.Equal(2, AgentSorgulari.GoIleBol("a\n  go 5  \nb").Count);
        Assert.Equal("SET NOEXEC ON;\nDELETE x;\nSET NOEXEC OFF;", AgentSorgulari.DenetimSql("DELETE x;"));
    }

    [Fact]
    public void Yerlesik_kategori_icin_kategori_olusturulmaz()
    {
        var job = new AgentJob(J1, "x", true, "[Uncategorized (Local)]", "sa", "", null, AgentSonuc.Bilinmiyor,
            null, null, null, false, null, null, 0, false);
        string s = AgentSorgulari.OlusturmaScripti(job, [], [], "s", DateTime.Now);
        Assert.DoesNotContain("sp_add_category", s);
        Assert.DoesNotContain("@category_name", s);
        Assert.DoesNotContain("@notify_email_operator_name", s);
    }
}
