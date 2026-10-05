using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 🔍 Profiler sekmesi VM'i (v23-S1): oturum yaşam döngüsü (kur→başlat→durdur→SİL — sunucuda
/// oturum bırakmama sözü), yetki hatasında Türkçe yönlendirme, akış okuma ve dışa aktarma.
/// WPF gerekmez — VM saf delegeyle test edilir (LogAnalizSekmesiVmTests deseni).
/// </summary>
public class ProfilerSekmesiVmTests
{
    private static QueryResult Bos() => new() { Basarili = true };

    private static QueryResult Hatali(string mesaj) => new() { Hata = new SqlHata(mesaj, 0, 0, 0) };

    private static QueryResult Xml(string icerik) => new()
    {
        Basarili = true,
        ToplamSatir = 1,
        ResultSetler =
        [
            new ResultSetData
            {
                Kolonlar = [new KolonBilgisi("veri", "nvarchar", typeof(string))],
                Satirlar = [new object?[] { icerik }],
            },
        ],
    };

    [Fact]
    public async Task Baslat_kur_basla_sirasiyla_gider_durdur_oturumu_sunucudan_siler()
    {
        var sorgular = new List<string>();
        var vm = new ProfilerSekmesiViewModel(["KdsDemo"], (sql, _) =>
        {
            sorgular.Add(sql);
            return Task.FromResult(sql.Contains("dm_xe_session_targets") ? Xml("") : Bos());
        });

        Task baslat = vm.BaslatCommand.ExecuteAsync(null);

        Assert.True(vm.Calisiyor);
        Assert.False(vm.BaslatCommand.CanExecute(null)); // çalışırken ikinci başlatma yok
        // Sıra sözleşmesi: önce eski oturum silinir (çökme kalıntısı), sonra kur + başlat.
        Assert.Contains("DROP EVENT SESSION", sorgular[0]);
        Assert.Contains("CREATE EVENT SESSION", sorgular[1]);
        Assert.Contains("STATE = START", sorgular[2]);

        await vm.DurdurCommand.ExecuteAsync(null);
        await baslat; // döngü iptalle biter — asılı kalmaz

        Assert.False(vm.Calisiyor);
        Assert.Contains(sorgular, s => s.Contains("STATE = STOP"));
        // Söz: arkamızda oturum bırakmayız — durdurunca DROP da gider.
        Assert.True(sorgular.Count(s => s.Contains("DROP EVENT SESSION")) >= 2);
        Assert.Contains("Durduruldu", vm.Ozet);
    }

    [Fact]
    public async Task Yetki_hatasi_turkce_yonlendirmeyle_gosterilir()
    {
        var vm = new ProfilerSekmesiViewModel([], (sql, _) => Task.FromResult(
            sql.Contains("CREATE EVENT SESSION")
                ? Hatali("VIEW SERVER STATE permission was denied")
                : Bos()));

        await vm.BaslatCommand.ExecuteAsync(null);

        Assert.False(vm.Calisiyor);
        Assert.Contains("ALTER ANY EVENT SESSION", vm.Ozet); // kullanıcı ne isteyeceğini bilsin
        Assert.Contains("permission was denied", vm.Ozet);   // ham neden de kaybolmasın
    }

    [Fact]
    public async Task Akis_ring_bufferdan_okunur_ve_grid_e_gelir() // tek tik ~2 sn bekler (bilinçli)
    {
        const string ornekXml = """
            <RingBufferTarget><event name="sql_batch_completed" timestamp="2026-09-24T09:00:01.000Z">
              <data name="duration"><value>5000</value></data>
              <data name="batch_text"><value>SELECT 1</value></data>
              <action name="client_app_name"><value>MERSIS</value></action>
              <action name="session_id"><value>51</value></action>
            </event></RingBufferTarget>
            """;
        var vm = new ProfilerSekmesiViewModel([], (sql, _) => Task.FromResult(
            sql.Contains("dm_xe_session_targets") ? Xml(ornekXml) : Bos()));

        Task baslat = vm.BaslatCommand.ExecuteAsync(null);
        await Task.Delay(TimeSpan.FromSeconds(3.5)); // ilk okuma tiki (2 sn aralık) gelsin

        ProfilerOlayi olay = Assert.Single(vm.Olaylar);
        Assert.Equal("Batch", olay.Olay);
        Assert.Equal("SELECT 1", olay.Metin);
        Assert.Equal("MERSIS", olay.Uygulama);
        Assert.Contains("1 olay", vm.Ozet);

        await vm.DurdurCommand.ExecuteAsync(null);
        await baslat;
        // Aynı XML her tikte tekrar gelir — artımlı eşik sayesinde İKİNCİ kez eklenmedi.
        Assert.Single(vm.Olaylar);
    }

    // ── v23-S2: 🧠 imza gruplama ──────────────────────────────────────────────

    [Fact]
    public async Task Ayni_imzali_olaylar_tek_grupta_toplanir_ve_en_cok_ustte() // LogAnaliz.Imza motoru
    {
        const string ornekXml = """
            <RingBufferTarget>
              <event name="sql_batch_completed" timestamp="2026-09-24T09:00:01.000Z">
                <data name="duration"><value>10000</value></data>
                <data name="batch_text"><value>SELECT * FROM T WHERE Id = 5</value></data>
              </event>
              <event name="sql_batch_completed" timestamp="2026-09-24T09:00:02.000Z">
                <data name="duration"><value>30000</value></data>
                <data name="batch_text"><value>SELECT * FROM T WHERE Id = 9412</value></data>
              </event>
              <event name="sql_batch_completed" timestamp="2026-09-24T09:00:03.000Z">
                <data name="duration"><value>5000</value></data>
                <data name="batch_text"><value>DELETE FROM K WHERE Ad = N'x'</value></data>
              </event>
            </RingBufferTarget>
            """;
        var vm = new ProfilerSekmesiViewModel([], (sql, _) => Task.FromResult(
            sql.Contains("dm_xe_session_targets") ? Xml(ornekXml) : Bos()));
        vm.GruplaAcik = true;

        Task baslat = vm.BaslatCommand.ExecuteAsync(null);
        await Task.Delay(TimeSpan.FromSeconds(3.5)); // ilk okuma tiki

        Assert.Equal(3, vm.Olaylar.Count);   // ham akış arkada durur
        Assert.Equal(2, vm.Gruplar.Count);   // Id=5 ve Id=9412 AYNI imza (parametre farkı elenir)

        ProfilerGrubu grup = vm.Gruplar[0];  // en çok tekrarlayan ÜSTTE
        Assert.Equal(2, grup.Kez);
        Assert.Equal(20, grup.OrtMs);        // (10 + 30) / 2
        Assert.Equal(30, grup.MaksMs);
        Assert.Contains("Id = 5", grup.OrnekMetin); // örnek = İLK olayın metni
        Assert.Equal(1, vm.Gruplar[1].Kez);

        // Tam metin paneli grup seçimini izler (olay ↔ grup köprüsü).
        vm.SeciliGrup = grup;
        Assert.Equal(grup.OrnekMetin, vm.TamMetin);

        await vm.DurdurCommand.ExecuteAsync(null);
        await baslat;
    }

    [Fact]
    public void Grup_modunda_satir_metni_grup_basliklariyla_uretilir()
    {
        var vm = new ProfilerSekmesiViewModel([], (_, _) => Task.FromResult(Bos()));
        var olay = new ProfilerOlayi(new DateTime(2026, 9, 24, 9, 0, 0), "Batch",
            120, 40, 500, 0, 10, "SELECT 1", "KdsDemo", "MERSIS", "ali", "PC1", 51);
        vm.Gruplar.Add(new ProfilerGrubu("imza", olay));
        vm.GruplaAcik = true;

        string metin = vm.SatirMetni('\t');

        Assert.Contains("Kez\tOrt ms\tMaks ms", metin);
        Assert.Contains("SELECT 1", metin);
        Assert.DoesNotContain("SPID", metin); // olay başlıkları DEĞİL — aktif görünüm dışa gider
        Assert.True(vm.SonucVar);
    }

    // ── v23-S3: 🕸 deadlock köprüsü ───────────────────────────────────────────

    [Fact]
    public void Deadlock_secilince_sema_dugmesi_iki_gorunumde_de_acilir()
    {
        var vm = new ProfilerSekmesiViewModel([], (_, _) => Task.FromResult(Bos()));
        var dl = new ProfilerOlayi(new DateTime(2026, 9, 24, 11, 0, 0), "Deadlock",
            0, 0, 0, 0, 0, "<deadlock />", null, null, null, null, 0);
        var normal = new ProfilerOlayi(DateTime.Now, "Batch", 1, 0, 0, 0, 0, "SELECT 1",
            null, null, null, null, 0);

        vm.SeciliOlay = dl;
        Assert.True(vm.DeadlockSecili);
        Assert.Equal(dl.Zaman, vm.SeciliZaman);

        vm.SeciliOlay = normal;
        Assert.False(vm.DeadlockSecili); // düğme yalnız kilitlenme satırında

        // Grup modunda karar GRUBUN türünden verilir (örnek metin = deadlock XML'i).
        vm.Gruplar.Add(new ProfilerGrubu("g", dl));
        vm.GruplaAcik = true;
        vm.SeciliGrup = vm.Gruplar[0];
        Assert.True(vm.DeadlockSecili);
        Assert.Equal("<deadlock />", vm.TamMetin); // şema penceresi bu XML'i alır
    }

    // ── v23-S4: 🤖 AI yorumu ──────────────────────────────────────────────────

    private static ProfilerOlayi Ornek(string olay, string metin, long sure = 12) => new(
        new DateTime(2026, 9, 24, 12, 0, 0), olay, sure, 4, 100, 0, 5, metin,
        "KdsDemo", "MERSIS", "ali", "PC1", 51);

    [Fact]
    public async Task Ai_yorumu_secili_olayin_metrikleriyle_istem_kurar()
    {
        string? istem = null;
        var vm = new ProfilerSekmesiViewModel([], (_, _) => Task.FromResult(Bos()),
            s => { istem = s; return Task.FromResult(AsistanCevabi.Basari("Index önerisi: …")); });

        Assert.True(vm.AiGorunur);
        // Seçim yokken model ÇAĞRILMAZ — kullanıcıya yol gösterilir.
        await vm.AiYorumlaCommand.ExecuteAsync(null);
        Assert.Null(istem);
        Assert.Contains("satır seçin", vm.Ozet);

        vm.SeciliOlay = Ornek("Batch", "SELECT * FROM T");
        await vm.AiYorumlaCommand.ExecuteAsync(null);

        Assert.NotNull(istem);
        Assert.Contains("Profiler", istem);            // yönerge sarmalayıcı (AsistanIstemleri)
        Assert.Contains("süre 12 ms", istem);          // metrikler modele gider
        Assert.Contains("SELECT * FROM T", istem);
        Assert.Equal("Index önerisi: …", vm.AiYorum);  // yanıt panele yazıldı
        Assert.True(vm.AiYorumAcik);
    }

    [Fact]
    public async Task Ai_yorumu_grup_modunda_grup_metrikleri_deadlockta_COZULMUS_ozet_gonderir()
    {
        string? istem = null;
        var vm = new ProfilerSekmesiViewModel([], (_, _) => Task.FromResult(Bos()),
            s => { istem = s; return Task.FromResult(AsistanCevabi.Basari("ok")); });

        // Grup modu: kez/ort/maks istemde.
        var g = new ProfilerGrubu("g", Ornek("Batch", "SELECT 1", sure: 10));
        g.Isle(Ornek("Batch", "SELECT 2", sure: 30));
        vm.Gruplar.Add(g);
        vm.GruplaAcik = true;
        vm.SeciliGrup = g;
        await vm.AiYorumlaCommand.ExecuteAsync(null);
        Assert.Contains("2 varyant", istem);
        Assert.Contains("ortalama 20 ms", istem);
        Assert.Contains("en yüksek 30 ms", istem);

        // Deadlock: modele HAM XML değil çözülmüş özet gider (KURBAN + kaynak satırları).
        const string dlXml = """
            <deadlock><victim-list><victimProcess id="p1" /></victim-list>
            <process-list>
              <process id="p1" spid="73" clientapp="MERSIS"><inputbuf>UPDATE A SET X=1</inputbuf></process>
              <process id="p2" spid="88" clientapp="ERP"><inputbuf>UPDATE B SET Y=1</inputbuf></process>
            </process-list>
            <resource-list><keylock objectname="dbo.A">
              <owner-list><owner id="p1" mode="X" /></owner-list>
              <waiter-list><waiter id="p2" mode="U" /></waiter-list>
            </keylock></resource-list></deadlock>
            """;
        vm.GruplaAcik = false;
        vm.SeciliOlay = Ornek("Deadlock", dlXml, sure: 0);
        await vm.AiYorumlaCommand.ExecuteAsync(null);
        Assert.Contains("KİLİTLENME", istem);
        Assert.Contains("SPID 73", istem);
        Assert.Contains("[KURBAN]", istem);
        Assert.Contains("dbo.A", istem);
        Assert.DoesNotContain("<process-list>", istem); // ham XML gitmez — özet gider
    }

    // ── v23-S5: ⧉ sekmede aç köprüsü ─────────────────────────────────────────

    [Fact]
    public void Sekmede_ac_yalniz_sorgu_satirlarinda_gorunur()
    {
        var vm = new ProfilerSekmesiViewModel([], (_, _) => Task.FromResult(Bos()));

        vm.SeciliOlay = Ornek("Batch", "SELECT 1");
        Assert.True(vm.SekmedeAcilabilir);
        Assert.Equal("KdsDemo", vm.SekmeVeritabani); // olayın DB'si önseçilir

        vm.SeciliOlay = Ornek("Hata", "Msg 208 …");
        Assert.False(vm.SekmedeAcilabilir);          // hata mesajı editörde anlamsız

        vm.SeciliOlay = Ornek("Deadlock", "<deadlock />");
        Assert.False(vm.SekmedeAcilabilir);          // deadlock'un yolu 🕸 şema

        // Grup modunda örnek sorgu da açılabilir (RPC grubu).
        vm.Gruplar.Add(new ProfilerGrubu("g", Ornek("RPC", "exec dbo.Sp @x=1")));
        vm.GruplaAcik = true;
        vm.SeciliGrup = vm.Gruplar[0];
        Assert.True(vm.SekmedeAcilabilir);
        Assert.Null(vm.SekmeVeritabani);             // grupta DB taşınmaz → profil varsayılanı
    }

    [Fact]
    public void Kopru_yoksa_ai_dugmesi_hic_gorunmez()
    {
        var vm = new ProfilerSekmesiViewModel([], (_, _) => Task.FromResult(Bos()));
        Assert.False(vm.AiGorunur);
    }

    // ── v23-S6: canlı tanı düzeltmeleri (28 Eyl 2026 — "çalışmıyor / donuyor") ─

    [Fact]
    public async Task Kendi_polling_gurultumuz_akisa_yazilmaz() // canlı tanı: dış trafiksiz 10 sn'de 8 sahte olay
    {
        const string ornekXml = """
            <RingBufferTarget>
              <event name="sql_batch_completed" timestamp="2026-09-28T09:00:01.000Z">
                <data name="batch_text"><value>SELECT CAST(t.target_data AS nvarchar(max)) AS veri FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t ON t.event_session_address = s.address WHERE s.name = N'SQLST_Profiler'</value></data>
                <action name="client_app_name"><value>SQLST</value></action>
              </event>
              <event name="rpc_completed" timestamp="2026-09-28T09:00:02.000Z">
                <data name="statement"><value>exec sp_reset_connection</value></data>
                <action name="client_app_name"><value>SQLST</value></action>
              </event>
              <event name="rpc_completed" timestamp="2026-09-28T09:00:03.000Z">
                <data name="statement"><value>exec sp_reset_connection</value></data>
                <action name="client_app_name"><value>ERP</value></action>
              </event>
              <event name="sql_batch_completed" timestamp="2026-09-28T09:00:04.000Z">
                <data name="batch_text"><value>SELECT * FROM Musteri</value></data>
                <action name="client_app_name"><value>SQLST</value></action>
              </event>
            </RingBufferTarget>
            """;
        var vm = new ProfilerSekmesiViewModel([], (sql, _) => Task.FromResult(
            sql.Contains("dm_xe_session_targets") ? Xml(ornekXml) : Bos()));

        Task baslat = vm.BaslatCommand.ExecuteAsync(null);
        await Task.Delay(TimeSpan.FromSeconds(3.5)); // ilk okuma tiki

        // Polling SELECT'i (oturum adını taşır) + SQLST havuz sıfırlaması ELENİR;
        // BAŞKA uygulamanın sp_reset_connection'ı ve kullanıcının kendi SQLST sorgusu KALIR.
        Assert.Equal(2, vm.Olaylar.Count);
        Assert.DoesNotContain(vm.Olaylar, o => o.Metin?.Contains("SQLST_Profiler") == true);
        Assert.Contains(vm.Olaylar, o => o.Metin == "SELECT * FROM Musteri");
        Assert.Contains(vm.Olaylar, o => o.Uygulama == "ERP");

        await vm.DurdurCommand.ExecuteAsync(null);
        await baslat;
        Assert.Equal(2, vm.Olaylar.Count); // elenen olaylar eşiği de ilerletti — tekrar gelmediler
    }

    [Fact]
    public async Task Canli_tikte_secim_ve_liste_sifirlanmaz_yalniz_yeni_eklenir() // donma + seçim kaybı düzeltmesi
    {
        const string xml1 = """
            <RingBufferTarget><event name="sql_batch_completed" timestamp="2026-09-28T10:00:01.000Z">
              <data name="batch_text"><value>SELECT 1</value></data>
            </event></RingBufferTarget>
            """;
        const string xml2 = """
            <RingBufferTarget>
              <event name="sql_batch_completed" timestamp="2026-09-28T10:00:01.000Z">
                <data name="batch_text"><value>SELECT 1</value></data>
              </event>
              <event name="sql_batch_completed" timestamp="2026-09-28T10:00:05.000Z">
                <data name="batch_text"><value>SELECT 2</value></data>
              </event>
            </RingBufferTarget>
            """;
        int okuma = 0;
        var vm = new ProfilerSekmesiViewModel([], (sql, _) => Task.FromResult(
            sql.Contains("dm_xe_session_targets") ? Xml(++okuma == 1 ? xml1 : xml2) : Bos()));

        Task baslat = vm.BaslatCommand.ExecuteAsync(null);
        await Task.Delay(TimeSpan.FromSeconds(3.5)); // 1. tik → SELECT 1

        ProfilerOlayi ilk = Assert.Single(vm.Olaylar);
        vm.SeciliOlay = ilk;

        await Task.Delay(TimeSpan.FromSeconds(2.5)); // 2. tik → SELECT 2 SONA eklenir

        Assert.Equal(2, vm.Olaylar.Count);
        Assert.Same(ilk, vm.Olaylar[0]);      // liste Clear edilmedi — eski satır yerinde
        Assert.Same(ilk, vm.SeciliOlay);      // seçim (ve tam-metin paneli) 2 sn'de bir düşmüyor
        Assert.Equal("SELECT 2", vm.Olaylar[1].Metin);

        await vm.DurdurCommand.ExecuteAsync(null);
        await baslat;
    }

    [Fact]
    public async Task Kapat_olu_sunucuda_asili_kalmaz_5sn_tavaniyla_doner() // bağlantı değişimi donması
    {
        var asili = new TaskCompletionSource<QueryResult>();
        var vm = new ProfilerSekmesiViewModel([], (sql, _) =>
            sql.Contains("STATE = STOP") ? asili.Task : Task.FromResult(
                sql.Contains("dm_xe_session_targets") ? Xml("") : Bos()));

        Task baslat = vm.BaslatCommand.ExecuteAsync(null); // oturum kuruldu → _oturumSunucuda
        for (int i = 0; i < 50 && !vm.Calisiyor; i++)
            await Task.Delay(50);

        var kron = System.Diagnostics.Stopwatch.StartNew();
        await vm.KapatAsync(); // STOP sonsuza dek asılı — MainViewModel bağlantı değişiminde bunu bekliyor
        kron.Stop();

        Assert.InRange(kron.Elapsed.TotalSeconds, 0, 8); // 5 sn tavan + pay; eskiden süresiz beklerdi
        Assert.False(vm.Calisiyor);
        asili.SetResult(Bos()); // arkada süren temizliği serbest bırak (test artığı kalmasın)
        await baslat;
    }

    [Fact]
    public async Task Durdurulmus_sekme_kapanirken_sunucuya_gitmez() // oturum çoktan silindi — boş tur atma
    {
        var sorgular = new List<string>();
        var vm = new ProfilerSekmesiViewModel([], (sql, _) =>
        {
            sorgular.Add(sql);
            return Task.FromResult(sql.Contains("dm_xe_session_targets") ? Xml("") : Bos());
        });

        Task baslat = vm.BaslatCommand.ExecuteAsync(null);
        await vm.DurdurCommand.ExecuteAsync(null); // STOP + DROP burada gitti
        await baslat;

        int oncesi = sorgular.Count;
        await vm.KapatAsync();
        Assert.Equal(oncesi, sorgular.Count); // kapanış sunucuya İKİNCİ kez gitmedi
    }

    [Fact]
    public void Satir_metni_basliklar_ve_csv_kacisiyla_uretilir()
    {
        var vm = new ProfilerSekmesiViewModel([], (_, _) => Task.FromResult(Bos()));
        vm.Olaylar.Add(new ProfilerOlayi(
            new DateTime(2026, 9, 24, 9, 0, 1, 500), "Batch", 12, 3, 100, 0, 5,
            "SELECT \"x\",\ny FROM t", "KdsDemo", "MERSIS", "ali", "PC1", 51));

        string csv = vm.SatirMetni(',', csv: true);

        Assert.Contains("Zaman,Olay,Sure ms", csv);
        Assert.Contains("\"SELECT \"\"x\"\", ⏎ y FROM t\"", csv); // çok satır tek satıra, tırnak ikizlenir
        Assert.Contains("MERSIS", csv);
    }
}
