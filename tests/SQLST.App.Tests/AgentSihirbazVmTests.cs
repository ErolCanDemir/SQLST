using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// ⏱ SQL Agent S3 sihirbazı (v23-S18 — mockup onaylı; kararlar K3 script→sekme, K4 yalnız T-SQL):
/// yeni job varsayılanları, anlık denetim, adım sırası değişince "N. adıma git" hedeflerinin doğru
/// numarayı alması, zamanlama formunun msdb alanlarına çevrimi (ve geri), düzenleme/kopya ön-dolumu
/// (T-SQL dışı adım korunur), düzenlemede GÜNCELLEME script'i, söz dizimi denetiminin NOEXEC ile
/// adımın kendi veritabanında koşması ve Agent sekmesinin sihirbaz isteği. Sahte köprüler.
/// </summary>
public class AgentSihirbazVmTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class Ortam
    {
        public List<(string Sql, string? Db)> Calisan { get; } = [];
        public List<(string Baslik, string Script)> Acilan { get; } = [];
        public Func<string, QueryResult> Yanit { get; set; } = _ => new QueryResult { Basarili = true };

        public AgentSihirbazViewModel Vm(AgentSihirbazIstegi istek) => new(
            istek, ["MERSIS", "ERP_STG"],
            (sql, db, _) => { Calisan.Add((sql, db)); return Task.FromResult(Yanit(sql)); },
            () => [("SQLST1", "SELECT * FROM dbo.Fatura;"), ("Rapor", "EXEC dbo.Rapor;")],
            (b, s) => Acilan.Add((b, s)));
    }

    private static AgentSihirbazIstegi Yeni() => new(AgentSihirbazKipi.Yeni, null, [], [], ["ETL", "[Uncategorized (Local)]"], @"LST\etl", "SQLPROD01");

    private static AgentJob MevcutJob() => new(Id, "Fatura aktarımı", true, "ETL", "sa", "ERP faturaları", null,
        AgentSonuc.Basarili, null, null, null, false, null, null, 3, true, "DBA", 2, 2, 1);

    private static AgentSihirbazIstegi Duzenle(AgentSihirbazKipi kip = AgentSihirbazKipi.Duzenle) => new(kip, MevcutJob(),
        [
            new(1, "Staging", "TSQL", "EXEC dbo.Al;", "ERP_STG", 3, 0, 4, 3, 0, 0, null, null),
            new(2, "Birleştir", "TSQL", "EXEC dbo.Birlestir;", "MERSIS", 3, 0, 2, 0, 2, 5, null, null),
            new(3, "Bildirim", "PowerShell", "Write-Host ok", null, 1, 0, 2, 0, 0, 0, 7, @"C:\log.txt"),
        ],
        [new(42, "Her 15 dk", true, 8, 62, 4, 15, 0, 1, 20260101, 99991231, 80000, 180000)],
        ["ETL"], "sa", "SQLPROD01");

    [Fact]
    public void Yeni_job_varsayilanlari_ve_ad_bosken_script_kapali()
    {
        var o = new Ortam();
        AgentSihirbazViewModel vm = o.Vm(Yeni());

        Assert.Equal("⏱ Yeni job — SQLPROD01", vm.PencereBasligi);
        Assert.Equal(@"LST\etl", vm.Sahip);                         // bağlı kullanıcı
        Assert.Equal("[Uncategorized (Local)]", vm.Kategori);
        AgentAdimTaslagi a = Assert.Single(vm.Adimlar);
        Assert.Equal("MERSIS", a.Veritabani);
        Assert.True(vm.HataVar);                                     // ad boş + komut boş
        Assert.Contains(vm.Hatalar, h => h.Contains("Job adı"));
        Assert.False(vm.ScriptUretCommand.CanExecute(null));

        vm.Ad = "Gece temizliği";
        a.Komut = "DELETE FROM dbo.Log WHERE Tarih < DATEADD(DAY,-30,GETDATE());";
        a.BasaridaIndex = 1;                                         // başarıyla çık
        Assert.False(vm.HataVar);
        Assert.True(vm.ScriptUretCommand.CanExecute(null));

        vm.ScriptUretCommand.Execute(null);
        (string baslik, string script) = Assert.Single(o.Acilan);
        Assert.Equal("agent-yeni-Gece temizliği", baslik);
        Assert.Contains("sp_add_job", script);
        Assert.Contains(@"@owner_login_name = N'LST\etl'", script);
        Assert.Contains("ÇALIŞTIRILMADI", script);
        Assert.True(vm.Tamamlandi);                                  // pencere kapanır
        Assert.Empty(o.Calisan);                                     // hiçbir şey çalıştırılmadı (K3)
    }

    [Fact]
    public void Adim_sirasi_degisince_hedef_numarasi_izler_silinen_hedef_hata_olur()
    {
        AgentSihirbazViewModel vm = new Ortam().Vm(Yeni());
        vm.Ad = "x";
        AgentAdimTaslagi a1 = vm.Adimlar[0];
        vm.AdimEkleCommand.Execute(null);
        AgentAdimTaslagi a2 = vm.Adimlar[1];
        vm.AdimEkleCommand.Execute(null);
        AgentAdimTaslagi a3 = vm.Adimlar[2];
        foreach (AgentAdimTaslagi a in vm.Adimlar)
            a.Komut = "SELECT 1";
        a1.HatadaIndex = 3;                                          // belirli adıma git
        a1.HatadaHedef = a3;
        Assert.Equal("3. adıma git", a1.HatadaMetni);

        vm.SeciliAdim = a3;
        vm.AdimYukariCommand.Execute(null);                          // a3 artık 2. sırada
        Assert.Equal([a1, a3, a2], vm.Adimlar);
        Assert.Equal("2. adıma git", a1.HatadaMetni);
        Assert.Equal(2, a1.Model().HatadaAdim);

        vm.SeciliAdim = a3;
        vm.AdimSilCommand.Execute(null);
        Assert.Null(a1.HatadaHedef);
        Assert.Contains(vm.Hatalar, h => h.Contains("gidilecek adım yok"));
    }

    [Fact]
    public void Zamanlama_formu_msdb_alanlarina_ve_turkce_cumleye_cevrilir()
    {
        var z = new AgentZamanlamaTaslagi
        {
            Ad = "Mesai", TurIndex = 2, Her = 1, Pzt = true, Sal = true, Car = true, Per = true, Cum = true,
            GunIciTekrar = true, TekrarAraligi = 15, BirimIndex = 0, AraBaslangic = "08:00", AraBitis = "18:00",
            BaslangicTarihi = "2026-10-06",
        };
        AgentZamanlama m = z.Model();
        Assert.Equal((8, 62, 4, 15, 1, 20261006, 99991231, 80000, 180000),
            (m.FreqType, m.FreqInterval, m.SubdayType, m.SubdayInterval, m.RecurrenceFactor, m.BaslangicTarihi, m.BitisTarihi, m.BaslangicSaati, m.BitisSaati));
        Assert.Equal("→ Hafta içi her gün, 08:00–18:00 arası her 15 dakikada bir", z.Metin);

        z.TurIndex = 3;                                              // aylık → göreli
        z.AylikGoreli = true;
        z.SiraIndex = 0;
        z.GoreliGunIndex = 0;                                        // pazartesi
        z.GunIciTekrar = false;
        z.Saat = "07:00";
        Assert.Equal("→ Her ayın ilk pazartesisi 07:00", z.Metin);
        Assert.Equal((32, 2, 1), (z.Model().FreqType, z.Model().FreqInterval, z.Model().RelativeInterval));

        z.Saat = "25:00";
        Assert.NotNull(z.SaatHatasi);
        Assert.Equal("→ (saat geçersiz)", z.Metin);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0, 0, 20261010, 99991231, 200000)]       // bir kez
    [InlineData(4, 2, 8, 3, 0, 0, 20261006, 20261231, 0)]            // 2 günde bir, her 3 saatte
    [InlineData(8, 65, 1, 0, 0, 2, 20261006, 99991231, 90000)]       // 2 haftada bir hafta sonu
    [InlineData(16, 15, 1, 0, 0, 3, 20261006, 99991231, 60000)]      // 3 ayda bir 15'i
    [InlineData(32, 6, 1, 0, 16, 1, 20261006, 99991231, 180000)]     // ayın son cuması
    public void Zamanlama_msdbden_forma_ve_geri_birebir(int tur, int aralik, int gunIci, int gunIciAralik, int goreli,
        int tekrar, int bas, int bit, int saat)
    {
        int bitisSaat = gunIci is 2 or 4 or 8 ? 220000 : 235959;
        var kaynak = new AgentZamanlama(5, "z", true, tur, aralik, gunIci, gunIciAralik, goreli, tekrar, bas, bit, saat, bitisSaat);
        Assert.Equal(kaynak with { Id = 0 }, AgentZamanlamaTaslagi.Kur(kaynak).Model());
    }

    [Fact]
    public void Duzenleme_on_dolum_tsql_disi_adim_korunur_ve_guncelleme_scripti_uretilir()
    {
        var o = new Ortam();
        AgentSihirbazViewModel vm = o.Vm(Duzenle());

        Assert.Equal("✎ Job düzenle — Fatura aktarımı · SQLPROD01", vm.PencereBasligi);
        Assert.Equal(3, vm.Adimlar.Count);
        Assert.Same(vm.Adimlar[2], vm.Adimlar[0].HatadaHedef);       // 3'e git → nesne hedef
        AgentAdimTaslagi ps = vm.Adimlar[2];
        Assert.True(ps.SaltGoruntu);
        Assert.Equal("PowerShell", ps.Tur);
        AgentZamanlamaTaslagi z = Assert.Single(vm.Zamanlamalar);
        Assert.True(z.HaftalikMi && z.Pzt && !z.Cmt && z.GunIciTekrar);
        Assert.False(vm.HataVar);

        vm.Ad = "Fatura aktarımı v2";
        vm.ScriptUretCommand.Execute(null);
        (string baslik, string script) = Assert.Single(o.Acilan);
        Assert.Equal("agent-duzenle-Fatura aktarımı v2", baslik);
        Assert.Contains($"DECLARE @jobId uniqueidentifier = '{Id}';", script);
        Assert.Contains("@new_name = N'Fatura aktarımı v2'", script);
        Assert.Contains("@schedule_id = 42, @delete_unused_schedule = 1", script);   // eski zamanlama ayrılır
        Assert.Contains("@subsystem = N'PowerShell'", script);                       // T-SQL dışı adım korundu
        Assert.Contains(@"@output_file_name = N'C:\log.txt'", script);
        Assert.Contains("proxy (id 7)", script);
        Assert.DoesNotContain("sp_add_job\n", script);
    }

    [Fact]
    public void Kopya_yeni_ad_ile_olusturma_scripti_uretir()
    {
        var o = new Ortam();
        AgentSihirbazViewModel vm = o.Vm(Duzenle(AgentSihirbazKipi.Kopya));
        Assert.Equal("Fatura aktarımı (kopya)", vm.Ad);
        vm.ScriptUretCommand.Execute(null);
        string script = Assert.Single(o.Acilan).Script;
        Assert.Contains("EXEC msdb.dbo.sp_add_job", script);
        Assert.DoesNotContain("sp_detach_schedule", script);
    }

    [Fact]
    public async Task Soz_dizimi_denetimi_noexec_ile_adimin_veritabaninda_parca_parca()
    {
        var o = new Ortam();
        AgentSihirbazViewModel vm = o.Vm(Duzenle());
        vm.SeciliAdim = vm.Adimlar[1];
        vm.SeciliAdim.Komut = "SELECT 1\nGO\nSELEC 2";
        o.Yanit = sql => sql.Contains("SELEC 2")
            ? new QueryResult { Hata = new SqlHata("Incorrect syntax near 'SELEC'.", 102, 2, 15) }
            : new QueryResult { Basarili = true };

        await vm.DenetleAsync();

        Assert.Equal(2, o.Calisan.Count);
        Assert.All(o.Calisan, c => Assert.StartsWith("SET NOEXEC ON;", c.Sql));
        Assert.All(o.Calisan, c => Assert.Equal("MERSIS", c.Db));
        Assert.Equal("⚠ 2. parça, satır 1: Incorrect syntax near 'SELEC'.", vm.SeciliAdim.DenetimSonucu);
        Assert.Equal("hata", vm.SeciliAdim.DenetimTuru);

        vm.SeciliAdim.Komut = "SELECT 1";
        Assert.Equal("", vm.SeciliAdim.DenetimSonucu);               // komut değişince sonuç sıfırlanır
        await vm.DenetleAsync();
        Assert.Equal("✓ Söz dizimi geçerli", vm.SeciliAdim.DenetimSonucu);

        vm.SeciliAdim = vm.Adimlar[2];                               // PowerShell — denetlenmez
        int once = o.Calisan.Count;
        await vm.DenetleAsync();
        Assert.Equal(once, o.Calisan.Count);
    }

    [Fact]
    public void Acik_sorgudan_al_ve_sayfa_gecisi_ozette_script_onizler()
    {
        AgentSihirbazViewModel vm = new Ortam().Vm(Yeni());
        vm.Ad = "j";
        vm.SorgudanAl(vm.AcikSorgular()[1].Metin);
        Assert.Equal("EXEC dbo.Rapor;", vm.Adimlar[0].Komut);
        vm.Adimlar[0].BasaridaIndex = 1;

        Assert.False(vm.GeriAcik);
        vm.IleriCommand.Execute(null);
        vm.IleriCommand.Execute(null);
        vm.IleriCommand.Execute(null);
        Assert.Equal(3, vm.Sayfa);
        Assert.False(vm.IleriAcik);
        Assert.Contains("EXEC dbo.Rapor;", vm.ScriptOnizleme);

        vm.Ad = "";                                                  // özetteyken hata → önizleme uyarıya döner
        Assert.Contains("hataları düzeltin", vm.ScriptOnizleme);
    }

    [Fact]
    public async Task Agent_sekmesi_sihirbaz_istegini_detayla_gonderir()
    {
        AgentSihirbazIstegi? istek = null;
        QueryResult Rs(string[] k, params object?[][] s) => new()
        {
            Basarili = true,
            ResultSetler = [new ResultSetData { Kolonlar = [.. k.Select(x => new KolonBilgisi(x, "x"))], Satirlar = [.. s] }],
        };
        var vm = new AgentSekmesiViewModel(
            (sql, _) => Task.FromResult(
                sql.Contains("EngineEdition") ? Rs(["motor_surumu", "sunucu", "ajan_durumu", "sysadmin", "sunucu_saati", "giris"], [3, "SQLPROD01", "Running", 1, DateTime.Now, @"LST\erolcan"])
                : sql.Contains("sp_help_jobstep") ? Rs(["step_id", "step_name", "subsystem", "command"], [1, "a", "TSQL", "SELECT 1"])
                : sql.Contains("sp_help_jobschedule") ? Rs(["schedule_id", "schedule_name"], [9, "z"])
                : sql.Contains("sp_help_jobhistory") || sql.Contains("sp_help_jobactivity") ? new QueryResult { Basarili = true }
                : sql.Contains("sp_help_job") ? Rs(["job_id", "name", "enabled", "category", "has_step"], [Id, "J", (byte)1, "ETL", 1])
                : new QueryResult { Basarili = true }),
            (_, _, _) => { }, _ => true, saltOkunur: false)
        { SihirbazIste = i => istek = i };
        await vm.YenileAsync();

        vm.YeniJob();
        Assert.Equal((AgentSihirbazKipi.Yeni, @"LST\erolcan", "SQLPROD01"), (istek!.Kip, istek.VarsayilanSahip, istek.Sunucu));
        Assert.Contains("ETL", istek.Kategoriler);

        vm.SeciliJob = vm.Joblar[0];
        await vm.JobDuzenleAsync();
        Assert.Equal(AgentSihirbazKipi.Duzenle, istek!.Kip);
        Assert.Equal(Id, istek.Job?.JobId);
        Assert.Equal("a", Assert.Single(istek.Adimlar).Ad);
        Assert.Equal(9, Assert.Single(istek.Zamanlamalar).Id);
    }
}
