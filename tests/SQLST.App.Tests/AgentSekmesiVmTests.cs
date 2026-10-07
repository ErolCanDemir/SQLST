using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// ⏱ SQL Agent sekmesi VM'i (v23-S16 — kararlar K1–K6): durum bandı (Agent yok / durmuş / yetki
/// yok), süzgeç çipleri + arama + kategori, seçili job detayı, işlemlerin ONAY ve salt-okunur kapısı,
/// "Script olarak al" çalıştırmadan sekmeye. WPF gerekmez — sahte msdb köprüsüyle (FtsSekmesiVmTests
/// deseni). Gerçek msdb turu AgentLocalDbTests'te; çalıştırma/durdurma canlı turu kullanıcıda (K5).
/// </summary>
public class AgentSekmesiVmTests
{
    private static readonly Guid Hatali = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Calisan = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Kapali = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static QueryResult Rs(string[] kolonlar, params object?[][] satirlar) => new()
    {
        Basarili = true,
        ResultSetler = [new ResultSetData { Kolonlar = [.. kolonlar.Select(k => new KolonBilgisi(k, "x"))], Satirlar = [.. satirlar] }],
    };

    private static QueryResult Hata(int no) => new() { Hata = new SqlHata("hata", no, 0, 16) };

    private static QueryResult Durum(int motor = 3, string? servis = "Running", bool sysadmin = true, bool kullanici = false)
        => Rs(["motor_surumu", "surum", "sunucu", "ajan_durumu", "sysadmin", "okuyucu", "kullanici", "operator", "sunucu_saati"],
            [motor, "Developer Edition (64-bit)", "SQLPROD01", servis, sysadmin ? 1 : 0, 0, kullanici ? 1 : 0, 0,
             new DateTime(2026, 10, 6, 9, 51, 20)]);

    private static readonly string[] JobKolonlari =
    [
        "job_id", "name", "enabled", "description", "start_step_id", "category", "owner",
        "current_execution_status", "current_execution_step", "has_step", "has_schedule",
        "next_run_date", "next_run_time",
    ];

    private static QueryResult Joblar() => Rs(JobKolonlari,
        [Hatali, "Fatura aktarımı (ERP)", (byte)1, "ERP faturaları", 1, "[Uncategorized (Local)]", "LST\\etl", 4, "0 (unknown)", 3, 1, 20261006, 100000],
        [Calisan, "Stok senkron", (byte)1, "No description available.", 1, "Data Collector", "LST\\etl", 1, "1 (Al)", 3, 1, 0, 0],
        [Kapali, "Eski rapor", (byte)0, "", 1, "Report Server", "sa", 4, "0 (unknown)", 1, 0, 0, 0]);

    private static readonly string[] GecmisKolonlari =
        ["instance_id", "job_id", "job_name", "step_id", "step_name", "sql_message_id", "message", "run_status", "run_date", "run_time", "run_duration", "retries_attempted"];

    private static QueryResult Gecmis() => Rs(GecmisKolonlari,
        [3, Hatali, "Fatura", 0, "(Job outcome)", 0, "The job failed.", 0, 20261006, 94500, 41, 0],
        [2, Hatali, "Fatura", 2, "Birleştir", 2627, "Violation of PRIMARY KEY\nconstraint 'PK_Fatura'.", 0, 20261006, 94503, 38, 0],
        [1, Hatali, "Fatura", 1, "Staging'e al", 0, "(1284 rows affected)", 1, 20261006, 94500, 3, 0]);

    private static QueryResult Aktivite() => Rs(["job_id", "start_execution_date", "stop_execution_date"],
        [Calisan, new DateTime(2026, 10, 6, 9, 48, 8), null]);

    private static QueryResult Adimlar() => Rs(
        ["step_id", "step_name", "subsystem", "command", "database_name", "on_success_action", "on_success_step_id",
         "on_fail_action", "on_fail_step_id", "retry_attempts", "retry_interval", "proxy_id", "output_file_name", "flags"],
        [1, "Staging'e al", "TSQL", "EXEC dbo.Al;", "ERP_STG", 3, 0, 2, 0, 0, 0, null, null, 0],
        [2, "Birleştir", "TSQL", "EXEC dbo.FaturaBirlestir;", "MERSIS", 3, 0, 2, 0, 2, 5, null, null, 0],
        [3, "Bildirim", "PowerShell", "Write-Host x", null, 1, 0, 2, 0, 0, 0, null, null, 0]);

    private static QueryResult Zamanlamalar() => Rs(
        ["schedule_id", "schedule_name", "enabled", "freq_type", "freq_interval", "freq_subday_type", "freq_subday_interval",
         "freq_relative_interval", "freq_recurrence_factor", "active_start_date", "active_end_date", "active_start_time", "active_end_time"],
        [4, "Her 15 dk", 1, 8, 62, 4, 15, 0, 1, 20260101, 99991231, 80000, 180000]);

    private sealed class Kopru
    {
        public Func<QueryResult> DurumYaniti { get; set; } = () => Durum();
        public Func<string, QueryResult>? IslemYaniti { get; set; }
        public List<string> Gonderilen { get; } = [];
        public List<(string Baslik, string Sql, string? Db)> Acilan { get; } = [];
        public List<string> Sorulan { get; } = [];
        public bool OnayCevabi { get; set; } = true;

        public Task<QueryResult> Calistir(string sql, CancellationToken _)
        {
            Gonderilen.Add(sql);
            QueryResult r =
                sql.Contains("EngineEdition") ? DurumYaniti()
                : sql.Contains("sp_help_jobhistory") ? Gecmis()
                : sql.Contains("sp_help_jobactivity") ? Aktivite()
                : sql.Contains("sp_help_jobstep") ? Adimlar()
                : sql.Contains("sp_help_jobschedule") ? Zamanlamalar()
                : sql.Contains("sp_help_job") ? Joblar()
                : IslemYaniti?.Invoke(sql) ?? new QueryResult { Basarili = true };
            return Task.FromResult(r);
        }

        public AgentSekmesiViewModel Vm(bool saltOkunur = false) => new(
            Calistir, (b, s, db) => Acilan.Add((b, s, db)),
            m => { Sorulan.Add($"{m.Baslik} {m.Mesaj}"); return OnayCevabi; }, saltOkunur)
        { IslemSonrasiBeklemeMs = 0 };

        public bool IslemGitti => Gonderilen.Any(s => s.Contains("sp_start_job") || s.Contains("sp_stop_job") || s.Contains("sp_update_job"));
    }

    private static async Task<AgentSekmesiViewModel> SeciliAsync(Kopru k, Guid id, bool saltOkunur = false)
    {
        AgentSekmesiViewModel vm = k.Vm(saltOkunur);
        await vm.YenileAsync();
        vm.SeciliJob = vm.Joblar.Single(j => j.Job.JobId == id);
        await Task.Yield();
        return vm;
    }

    [Fact]
    public async Task Liste_rozetler_ve_sayaclar()
    {
        AgentSekmesiViewModel vm = new Kopru().Vm();
        await vm.YenileAsync();

        Assert.Equal("Agent çalışıyor", vm.DurumRozeti);
        Assert.Equal("ok", vm.DurumTuru);
        Assert.Contains("SQLPROD01 · Developer Edition (64-bit) · yetki: sysadmin", vm.SunucuBilgisi);
        Assert.False(vm.UyariVar);
        Assert.Equal((3, 1, 1, 1), (vm.TumSayisi, vm.HataliSayisi, vm.CalisanSayisi, vm.KapaliSayisi));

        AgentJobSatiri hatali = vm.Joblar.Single(j => j.Job.JobId == Hatali);
        Assert.Equal("Hata · adım 2", hatali.SonucMetni);
        Assert.Equal("hata", hatali.SonucTuru);
        Assert.Equal("2026-10-06 09:45:00", hatali.SonKosuMetni);   // ham biçim
        Assert.Equal("00:00:41", hatali.SureMetni);
        Assert.Equal("2026-10-06 10:00:00", hatali.SonrakiMetni);

        AgentJobSatiri calisan = vm.Joblar.Single(j => j.Job.JobId == Calisan);
        Assert.Equal("▶ Çalışıyor · adım 1/3", calisan.SonucMetni);
        Assert.Equal("calis", calisan.SonucTuru);
        Assert.Equal("00:03:12…", calisan.SureMetni);                 // sunucu saatine göre (09:51:20 − 09:48:08)

        AgentJobSatiri kapali = vm.Joblar.Single(j => j.Job.JobId == Kapali);
        Assert.Equal("— (kapalı)", kapali.SonrakiMetni);
        Assert.Equal("hiç çalışmadı", kapali.SonKosuMetni);
        Assert.Equal("—", kapali.AcikIsareti);
    }

    [Fact]
    public async Task Suzgec_arama_ve_kategori_birlikte_calisir()
    {
        AgentSekmesiViewModel vm = new Kopru().Vm();
        await vm.YenileAsync();

        vm.SuzgecHatali = true;
        Assert.Equal(["Fatura aktarımı (ERP)"], vm.Joblar.Select(j => j.Ad));
        Assert.False(vm.SuzgecTumu);

        vm.SuzgecCalisan = true;
        Assert.Equal(["Stok senkron"], vm.Joblar.Select(j => j.Ad));

        vm.SuzgecTumu = true;
        vm.AramaMetni = "RAPOR";                                      // büyük/küçük harf duyarsız
        Assert.Equal(["Eski rapor"], vm.Joblar.Select(j => j.Ad));

        vm.AramaMetni = "";
        Assert.Contains("Data Collector", vm.Kategoriler);
        vm.SeciliKategori = "Data Collector";
        Assert.Equal(["Stok senkron"], vm.Joblar.Select(j => j.Ad));
    }

    [Fact]
    public async Task Secili_job_detayi_ve_tam_mesaj_yenilemede_secim_korunur()
    {
        var k = new Kopru();
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);

        Assert.Equal(["(job)", "2 · Birleştir", "1 · Staging'e al"], vm.Gecmis.Select(g => g.AdimMetni));
        Assert.Equal(3, vm.Adimlar.Count);
        Assert.Equal("Hatayla çık · 2 deneme / 5 dk", vm.Adimlar[1].HatadaMetni);
        Assert.Equal("PowerShell", vm.Adimlar[2].Tur);
        AgentZamanlamaGorunum z = Assert.Single(vm.Zamanlamalar);
        Assert.Equal("Hafta içi her gün, 08:00–18:00 arası her 15 dakikada bir", z.Metin);
        Assert.Equal("· ERP faturaları", vm.SeciliJobAciklama);

        vm.SeciliGecmis = vm.Gecmis[1];
        Assert.Contains("\n", vm.TamMesaj);                            // tam mesaj çok satırlı
        Assert.DoesNotContain("\n", vm.Gecmis[1].TekSatirMesaj);       // grid tek satır

        await vm.YenileAsync();
        Assert.Equal(Hatali, vm.SeciliJob?.Job.JobId);
    }

    [Fact]
    public async Task Agent_yoksa_turkce_bant_ve_baslat_sunucuya_gitmez()
    {
        var k = new Kopru { DurumYaniti = () => Durum(motor: 4, servis: null) };
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);

        Assert.Equal("Agent yok", vm.DurumRozeti);
        Assert.Contains("Express ve LocalDB", vm.Uyari);
        Assert.Equal(3, vm.Joblar.Count);                              // tanımlar yine görünür
        Assert.False(vm.BaslatAcik);

        await vm.BaslatAsync();
        Assert.False(k.IslemGitti);
        Assert.Contains("Agent yok", vm.Bilgi);
    }

    [Fact]
    public async Task Agent_durmussa_bant_ve_baslat_kapali_ac_kapat_acik()
    {
        var k = new Kopru { DurumYaniti = () => Durum(servis: "Stopped") };
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);

        Assert.Equal("uyari", vm.DurumTuru);
        Assert.Contains("Agent servisi durmuş", vm.Uyari);
        Assert.False(vm.BaslatAcik);
        Assert.True(vm.AcKapatAcik);                                   // msdb tanımı — Agent'sız da olur
    }

    [Fact]
    public async Task Yetki_yoksa_bant_ve_job_sorgusu_gonderilmez()
    {
        var k = new Kopru { DurumYaniti = () => Durum(sysadmin: false) };
        AgentSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();

        Assert.Contains("görme yetkiniz yok", vm.Uyari);
        Assert.Empty(vm.Joblar);
        Assert.DoesNotContain(k.Gonderilen, s => s.Contains("sp_help_job"));
    }

    [Fact]
    public async Task UserRole_kendi_joblari_notu()
    {
        var k = new Kopru { DurumYaniti = () => Durum(sysadmin: false, kullanici: true) };
        AgentSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();
        Assert.Contains("yalnız sahibi olduğunuz", vm.Uyari);
        Assert.Equal(3, vm.Joblar.Count);
    }

    [Fact]
    public async Task Baslat_onaysiz_gitmez_onayla_baslar_ve_yeniler()
    {
        var k = new Kopru { OnayCevabi = false };
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);

        await vm.BaslatAsync();
        Assert.False(k.IslemGitti);
        Assert.Contains("baştan — 1. adım: Staging'e al", Assert.Single(k.Sorulan));

        k.OnayCevabi = true;
        int once = k.Gonderilen.Count(s => s.Contains("EngineEdition"));
        await vm.BaslatAsync();
        Assert.Contains(k.Gonderilen, s => s == AgentSorgulari.BaslatSql(Hatali));
        Assert.StartsWith("▶ \"Fatura aktarımı (ERP)\" başlatıldı", vm.Bilgi);
        Assert.True(k.Gonderilen.Count(s => s.Contains("EngineEdition")) > once); // işlem sonrası yenilendi
    }

    [Fact]
    public async Task Secili_adimdan_baslat_step_name_ile()
    {
        var k = new Kopru();
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);

        await vm.SeciliAdimdanBaslatAsync();
        Assert.False(k.IslemGitti);
        Assert.Contains("Adımlar sekmesinden", vm.Bilgi);

        vm.SeciliAdim = vm.Adimlar[1];
        Assert.Equal("Seçili adımdan başlat: 2 · Birleştir", vm.SeciliAdimdanMetni);
        await vm.SeciliAdimdanBaslatAsync();
        Assert.Contains(k.Gonderilen, s => s == AgentSorgulari.BaslatSql(Hatali, "Birleştir"));
    }

    [Fact]
    public async Task Agent_hatasi_turkce_bilgiye_doner()
    {
        var k = new Kopru { IslemYaniti = _ => Hata(22022) };
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);
        await vm.BaslatAsync();
        Assert.Contains("Agent servisi çalışmıyor", vm.Bilgi);
    }

    [Fact]
    public async Task Durdur_yalniz_calisan_jobda()
    {
        var k = new Kopru();
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);
        Assert.False(vm.DurdurAcik);
        await vm.DurdurAsync();
        Assert.False(k.IslemGitti);

        vm.SeciliJob = vm.Joblar.Single(j => j.Job.JobId == Calisan);
        Assert.True(vm.DurdurAcik);
        Assert.False(vm.BaslatAcik);
        await vm.DurdurAsync();
        Assert.Contains(k.Gonderilen, s => s == AgentSorgulari.DurdurSql(Calisan));
    }

    [Fact]
    public async Task Salt_okunur_baglantida_hicbir_islem_gitmez()
    {
        var k = new Kopru();
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali, saltOkunur: true);

        Assert.False(vm.BaslatAcik);
        Assert.False(vm.AcKapatAcik);
        await vm.BaslatAsync();
        await vm.AcKapatAsync();
        Assert.False(k.IslemGitti);
        Assert.Empty(k.Sorulan);
        Assert.Contains("Salt-okunur", vm.Bilgi);
    }

    [Fact]
    public async Task Ac_kapat_metni_ve_sql()
    {
        var k = new Kopru();
        AgentSekmesiViewModel vm = await SeciliAsync(k, Kapali);
        Assert.Equal("▶ Aç", vm.AcKapatMetni);
        await vm.AcKapatAsync();
        Assert.Contains(k.Gonderilen, s => s == AgentSorgulari.AcKapatSql(Kapali, true));

        vm.SeciliJob = vm.Joblar.Single(j => j.Job.JobId == Hatali);
        Assert.Equal("⏸ Kapat", vm.AcKapatMetni);
    }

    [Fact]
    public async Task Script_olarak_al_calistirmadan_msdb_sekmesinde()
    {
        var k = new Kopru();
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);

        await vm.ScriptOlarakAlAsync();

        (string baslik, string sql, string? db) = Assert.Single(k.Acilan);
        Assert.Equal("agent-Fatura aktarımı (ERP)", baslik);
        Assert.Equal("msdb", db);
        Assert.Contains("@job_name = N'Fatura aktarımı (ERP)'", sql);
        Assert.Contains("@step_name = N'Staging''e al'", sql);
        Assert.Contains("ÇALIŞTIRILMADI", sql);
        Assert.False(k.IslemGitti);
        Assert.DoesNotContain(k.Gonderilen, s => s.Contains("sp_add_job"));
    }

    [Fact]
    public async Task Adim_komutu_kendi_veritabaninda_acilir_tsql_disi_acilmaz()
    {
        var k = new Kopru();
        AgentSekmesiViewModel vm = await SeciliAsync(k, Hatali);

        vm.SeciliAdim = vm.Adimlar[1];
        vm.AdimiSekmedeAc();
        Assert.Equal(("agent-adim-Birleştir", "EXEC dbo.FaturaBirlestir;", "MERSIS"), Assert.Single(k.Acilan));

        vm.SeciliAdim = vm.Adimlar[2];
        vm.AdimiSekmedeAc();
        Assert.Single(k.Acilan);
        Assert.Contains("PowerShell", vm.Bilgi);
    }

    [Fact]
    public async Task Msdb_okunamazsa_bant_hata_mesajiyla()
    {
        var k = new Kopru { DurumYaniti = () => new QueryResult { Hata = new SqlHata("The server principal is not able to access the database \"msdb\"", 916, 0, 14) } };
        AgentSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();
        Assert.Contains("msdb okunamadı", vm.Uyari);
        Assert.Equal("Okunamadı", vm.DurumRozeti);
    }
}
