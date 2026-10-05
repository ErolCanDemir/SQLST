using System.Text.Json;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>Log tablo analizi (kullanıcı tasarımı 2026-07-23): sorgu lehçesi + imza gruplama + kolon tahmini.</summary>
public class LogTabloAnalizTests
{
    [Theory]
    [InlineData(MotorTuru.Mssql, "SELECT TOP (100) Mesaj FROM dbo.ExceptionLog;")]
    [InlineData(MotorTuru.Postgres, "SELECT Mesaj FROM dbo.ExceptionLog LIMIT 100;")]
    [InlineData(MotorTuru.MySql, "SELECT Mesaj FROM dbo.ExceptionLog LIMIT 100;")]
    [InlineData(MotorTuru.Oracle, "SELECT Mesaj FROM dbo.ExceptionLog FETCH FIRST 100 ROWS ONLY")]
    public void Orneklem_sorgusu_motora_gore_uretilir(MotorTuru motor, string beklenen)
        => Assert.Equal(beklenen, LogTabloAnaliz.OrneklemSorgusu(motor, "dbo.ExceptionLog", "Mesaj", 100));

    [Fact]
    public void Mongo_orneklem_find_belgesi_uretir()
    {
        // Kullanıcı 2026-07-25: log analizi Mongo'da da olmalı. Koleksiyon adı ÇIPLAK (TamAd değil).
        // v22-S2: find DEĞİL aggregate — mesaj SUNUCUDA kırpılıyor ve ifade projeksiyonu find'da
        // yalnız MongoDB 4.4+'ta destekli; $project her sürümde çalışır.
        string json = LogTabloAnaliz.OrneklemSorgusu(MotorTuru.Mongo, "logs", "Mesaj", 100);
        using JsonDocument belge = JsonDocument.Parse(json);
        JsonElement kok = belge.RootElement;
        Assert.Equal("logs", kok.GetProperty("aggregate").GetString());

        JsonElement boru = kok.GetProperty("pipeline");
        Assert.Equal(100, boru[0].GetProperty("$limit").GetInt32());
        JsonElement proj = boru[1].GetProperty("$project");
        Assert.Equal(0, proj.GetProperty("_id").GetInt32());
        Assert.Equal(LogTabloAnaliz.MongoMesajKirpma,
            proj.GetProperty("Mesaj").GetProperty("$cond")[1].GetProperty("$substrCP")[2].GetInt32());
    }

    [Fact]
    public void Mongo_son24saat_aggregate_belgesi_uretir()
    {
        // v22-S4 m.4: $match DÜZ tarih literaliyle — $expr+$$NOW her sunucu sürümünde index'e
        // oturmuyordu (ExceptionLog'da 120 sn sunucu tavanı aşıldı; yerel ölçüm 115 ms ↔ 16 ms).
        string json = LogTabloAnaliz.Orneklem24SaatSorgusu(MotorTuru.Mongo, "logs", "Mesaj", "Zaman", 100);
        using JsonDocument belge = JsonDocument.Parse(json);
        JsonElement kok = belge.RootElement;
        Assert.Equal("logs", kok.GetProperty("aggregate").GetString());

        JsonElement boru = kok.GetProperty("pipeline");
        Assert.Equal(3, boru.GetArrayLength());

        JsonElement eslesme = boru[0].GetProperty("$match");
        Assert.False(eslesme.TryGetProperty("$expr", out _)); // eski biçim GERİ GELMESİN
        string esik = eslesme.GetProperty("Zaman").GetProperty("$gte").GetProperty("$date").GetString()!;
        var esikZamani = DateTime.Parse(esik, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(DateTime.UtcNow - esikZamani, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));

        JsonElement proj = boru[1].GetProperty("$project");
        // v22-S2: mesaj SUNUCUDA kırpılır (ağ maliyeti belge boyutundan bağımsız kalsın); string
        // olmayan alanı bozmamak için $cond koruması var. Zaman/_id davranışı değişmedi.
        Assert.Equal(LogTabloAnaliz.MongoMesajKirpma,
            proj.GetProperty("Mesaj").GetProperty("$cond")[1].GetProperty("$substrCP")[2].GetInt32());
        Assert.Equal("$Mesaj", proj.GetProperty("Mesaj").GetProperty("$cond")[2].GetString()); // string değilse aynen
        Assert.Equal(1, proj.GetProperty("Zaman").GetInt32()); // "son görülme" için zaman da projelenir
        Assert.Equal(0, proj.GetProperty("_id").GetInt32());
        Assert.Equal(100, boru[2].GetProperty("$limit").GetInt32());
    }

    [Fact]
    public void Mongo_alan_tipleri_mesaj_ve_zaman_tahminine_uyar()
    {
        // MongoDB alan tipleri "string"/"date"/"objectId" (SQL char/datetime değil). Mesaj tahmini
        // "string"i METİN sayar; zaman tahmini "date"i tarih sayar.
        SemaKolonu[] mongoAlanlari =
        [
            new("_id", "objectId", false, true),
            new("Timestamp", "date", false, false),
            new("RenderedMessage", "string", true, false),
            new("Level", "string", true, false),
        ];
        Assert.Equal("RenderedMessage", LogTabloAnaliz.MesajKolonuTahmini(mongoAlanlari)); // "message" ipucu
        Assert.Equal("Timestamp", LogTabloAnaliz.ZamanKolonuTahmini(mongoAlanlari));       // "time" ipucu

        // İpucu tutmasa da ilk "string" alan mesaj sayılır (metin-dışı objectId/int elenir).
        SemaKolonu[] ipucusuz =
        [
            new("_id", "objectId", false, true),
            new("payload", "string", true, false),
            new("sayi", "int", true, false),
        ];
        Assert.Equal("payload", LogTabloAnaliz.MesajKolonuTahmini(ipucusuz));
    }

    [Theory]
    [InlineData(MotorTuru.Mssql,
        "SELECT TOP (100) Mesaj, Zaman FROM dbo.ExceptionLog WHERE Zaman >= DATEADD(HOUR, -24, SYSDATETIME()) ORDER BY Zaman DESC;")]
    [InlineData(MotorTuru.Postgres,
        "SELECT Mesaj, Zaman FROM dbo.ExceptionLog WHERE Zaman >= NOW() - INTERVAL '24 hours' ORDER BY Zaman DESC LIMIT 100;")]
    [InlineData(MotorTuru.MySql,
        "SELECT Mesaj, Zaman FROM dbo.ExceptionLog WHERE Zaman >= NOW() - INTERVAL 24 HOUR ORDER BY Zaman DESC LIMIT 100;")]
    [InlineData(MotorTuru.Oracle,
        "SELECT Mesaj, Zaman FROM dbo.ExceptionLog WHERE Zaman >= SYSTIMESTAMP - INTERVAL '24' HOUR ORDER BY Zaman DESC FETCH FIRST 100 ROWS ONLY")]
    public void Son24saat_sorgusu_motora_gore_uretilir(MotorTuru motor, string beklenen)
        => Assert.Equal(beklenen,
            LogTabloAnaliz.Orneklem24SaatSorgusu(motor, "dbo.ExceptionLog", "Mesaj", "Zaman", 100));

    [Theory]
    [InlineData(MotorTuru.Mssql,
        "SELECT TOP (100) Mesaj, Zaman FROM dbo.ExceptionLog WHERE Zaman >= '2026-07-01T00:00:00' AND Zaman < '2026-07-26T00:00:00';")]
    [InlineData(MotorTuru.Postgres,
        "SELECT Mesaj, Zaman FROM dbo.ExceptionLog WHERE Zaman >= '2026-07-01T00:00:00'::timestamp AND Zaman < '2026-07-26T00:00:00'::timestamp LIMIT 100;")]
    [InlineData(MotorTuru.MySql,
        "SELECT Mesaj, Zaman FROM dbo.ExceptionLog WHERE Zaman >= '2026-07-01 00:00:00' AND Zaman < '2026-07-26 00:00:00' LIMIT 100;")]
    [InlineData(MotorTuru.Oracle,
        "SELECT Mesaj, Zaman FROM dbo.ExceptionLog WHERE Zaman >= TIMESTAMP '2026-07-01 00:00:00' AND Zaman < TIMESTAMP '2026-07-26 00:00:00' FETCH FIRST 100 ROWS ONLY")]
    public void Aralik_sorgusu_motora_gore_uretilir(MotorTuru motor, string beklenen)
        => Assert.Equal(beklenen, LogTabloAnaliz.OrneklemAralikSorgusu(
            motor, "dbo.ExceptionLog", "Mesaj", "Zaman",
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 26), 100));

    [Fact]
    public void Mongo_aralik_aggregate_gte_lt_date_uretir()
    {
        // Tarihler UTC ISO $date literal'ine çevrilir (Mongo tarihleri UTC saklar).
        string json = LogTabloAnaliz.OrneklemAralikSorgusu(
            MotorTuru.Mongo, "logs", "Mesaj", "Zaman",
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc), 100);
        using JsonDocument belge = JsonDocument.Parse(json);
        JsonElement kok = belge.RootElement;
        Assert.Equal("logs", kok.GetProperty("aggregate").GetString());
        JsonElement zaman = kok.GetProperty("pipeline")[0].GetProperty("$match").GetProperty("Zaman");
        Assert.Equal("2026-07-01T00:00:00Z", zaman.GetProperty("$gte").GetProperty("$date").GetString());
        Assert.Equal("2026-07-26T00:00:00Z", zaman.GetProperty("$lt").GetProperty("$date").GetString());
    }

    [Fact]
    public void Zaman_kolonu_tahmini_once_ipucuna_sonra_tarih_tipine_bakar()
    {
        // İpucu önceliği: "kayit" tarihli kolon, ada göre öne geçer (kolon sırasından bağımsız).
        SemaKolonu[] tipikLogTablosu =
        [
            new("Id", "bigint", false, true),
            new("Message", "nvarchar(max)", true, false),
            new("KayitZamani", "datetime2", false, false),
        ];
        Assert.Equal("KayitZamani", LogTabloAnaliz.ZamanKolonuTahmini(tipikLogTablosu));

        // İpucu tutmazsa ilk TARİH/ZAMAN tipli kolon (metin/int elenir).
        SemaKolonu[] ipucusuz =
        [
            new("Id", "int", false, true),
            new("Damga", "datetimeoffset", false, false),
        ];
        Assert.Equal("Damga", LogTabloAnaliz.ZamanKolonuTahmini(ipucusuz));

        // Hiç tarih/zaman kolonu yoksa null (çağıran son-24-saat süzgecini uygulayamaz).
        SemaKolonu[] zamansiz =
        [
            new("Id", "int", false, true),
            new("Message", "nvarchar", true, false),
        ];
        Assert.Null(LogTabloAnaliz.ZamanKolonuTahmini(zamansiz));
    }

    [Fact]
    public void Grupla_ayni_imzayi_birlestirir_ve_cok_tekrarlayani_one_alir()
    {
        // 'X'/'Y' parametre farkı imzada elenir → aynı grup; null/boş atlanır
        object?[] degerler =
        [
            "Tablo 'A' bulunamadi", "Tablo 'B' bulunamadi", "Tablo 'C' bulunamadi",
            "Baglanti zaman asimi", null, "  ",
        ];

        IReadOnlyList<TabloLogGrubu> gruplar = LogTabloAnaliz.Grupla(degerler);

        Assert.Equal(2, gruplar.Count);
        Assert.Equal(3, gruplar[0].Sayi);                       // en çok tekrarlayan önde
        Assert.Equal("Tablo 'A' bulunamadi", gruplar[0].OrnekMesaj);
        Assert.Equal(1, gruplar[1].Sayi);
        Assert.All(gruplar, g => Assert.Null(g.SonGorulme));    // yalnız-mesaj aşırı yüklemesi → SonGorulme null
    }

    [Fact]
    public void Grupla_son_gorulme_grubun_en_yeni_zamanidir() // kullanıcı isteği 2026-07-29
    {
        var t = new DateTime(2026, 7, 29, 10, 0, 0);
        (object? Mesaj, object? Zaman)[] satirlar =
        [
            ("Tablo 'A' bulunamadi", t.AddHours(-3)),
            ("Tablo 'B' bulunamadi", t),                 // aynı imza — grubun EN YENİ'si bu
            ("Tablo 'C' bulunamadi", t.AddHours(-8)),
            ("Baglanti zaman asimi", t.AddDays(-2)),
        ];

        IReadOnlyList<TabloLogGrubu> gruplar = LogTabloAnaliz.Grupla(satirlar);

        Assert.Equal(3, gruplar[0].Sayi);
        Assert.Equal(t, gruplar[0].SonGorulme);              // imza bazında MAX(zaman)
        Assert.Equal(t.AddDays(-2), gruplar[1].SonGorulme);
    }

    [Fact]
    public void Grupla_zaman_cozulemezse_null_ayni_imzada_diger_zamana_bakar()
    {
        var t = new DateTime(2026, 7, 29, 9, 0, 0);
        (object? Mesaj, object? Zaman)[] satirlar =
        [
            ("Ayni hata", "tarih-degil"), // çözülemez → null sayılır
            ("Ayni hata", t),             // grubun son görülmesi bu olmalı
        ];

        IReadOnlyList<TabloLogGrubu> gruplar = LogTabloAnaliz.Grupla(satirlar);

        Assert.Single(gruplar);
        Assert.Equal(t, gruplar[0].SonGorulme);
    }

    [Fact]
    public void Orneklem_sorgusu_zaman_kolonu_verilince_onu_da_projeler() // "son görülme" için
    {
        Assert.Equal("SELECT TOP (100) Mesaj, Zaman FROM dbo.ExceptionLog;",
            LogTabloAnaliz.OrneklemSorgusu(MotorTuru.Mssql, "dbo.ExceptionLog", "Mesaj", 100, "Zaman"));

        // Mongo: zaman alanı verilince projeksiyona eklenir (_id yine elenir). v22-S2: $project.
        string json = LogTabloAnaliz.OrneklemSorgusu(MotorTuru.Mongo, "logs", "Mesaj", 100, "Zaman");
        using JsonDocument belge = JsonDocument.Parse(json);
        JsonElement proj = belge.RootElement.GetProperty("pipeline")[1].GetProperty("$project");
        Assert.True(proj.GetProperty("Mesaj").TryGetProperty("$cond", out _)); // mesaj kırpılır
        Assert.Equal(1, proj.GetProperty("Zaman").GetInt32());
        Assert.Equal(0, proj.GetProperty("_id").GetInt32());
    }

    [Fact]
    public void Mesaj_kolonu_tahmini_once_ada_sonra_tipe_bakar()
    {
        SemaKolonu[] adli =
        [
            new("Id", "int", false, true),
            new("ExceptionMessage", "nvarchar", true, false),
        ];
        Assert.Equal("ExceptionMessage", LogTabloAnaliz.MesajKolonuTahmini(adli));

        SemaKolonu[] tipli =
        [
            new("Id", "int", false, true),
            new("Icerik", "nvarchar(max)", true, false),
        ];
        Assert.Equal("Icerik", LogTabloAnaliz.MesajKolonuTahmini(tipli));

        SemaKolonu[] metinsiz = [new("Id", "int", false, true)];
        Assert.Null(LogTabloAnaliz.MesajKolonuTahmini(metinsiz));
    }

    [Fact]
    public void Mesaj_kolonu_tahmini_ipucu_onceligiyle_calisir_kolon_sirasiyla_degil()
    {
        // İnceleme bulgusu (2026-07-23): kolon-sıralı arama "LogId"yi (int!) "Message"ın önüne
        // geçiriyordu. Metin-dışı tipler elenir; ipucu önceliği kolon sırasını ezer.
        SemaKolonu[] tipikLogTablosu =
        [
            new("LogId", "bigint", false, true),      // "log" içerir ama int → elenir
            new("LogDate", "datetime2", false, false),
            new("Level", "nvarchar", true, false),
            new("Message", "nvarchar(max)", true, false),
        ];
        Assert.Equal("Message", LogTabloAnaliz.MesajKolonuTahmini(tipikLogTablosu));

        // Önce gelen genel eşleşme ("LogAciklama"), sonraki güçlü ipucuya ("message") yenilir.
        SemaKolonu[] oncelik =
        [
            new("LogAciklama", "nvarchar", true, false),
            new("ExceptionMessage", "nvarchar(max)", true, false),
        ];
        Assert.Equal("ExceptionMessage", LogTabloAnaliz.MesajKolonuTahmini(oncelik));
    }

    // ── v20-S3 "Log Analizi 2.0": seviye · gerçek-sayım · detay · zenginleşmiş gruplama ──────────

    [Fact]
    public void Orneklem_sorgusu_seviye_kolonu_verilince_onu_da_projeler()
    {
        Assert.Equal("SELECT TOP (100) Mesaj, Zaman, Seviye FROM dbo.ExceptionLog;",
            LogTabloAnaliz.OrneklemSorgusu(MotorTuru.Mssql, "dbo.ExceptionLog", "Mesaj", 100, "Zaman", "Seviye"));

        // Mongo projeksiyonu seviye alanını da ekler (_id yine elenir). v22-S2: $project.
        string json = LogTabloAnaliz.OrneklemSorgusu(MotorTuru.Mongo, "logs", "Mesaj", 100, "Zaman", "Level");
        JsonElement proj = JsonDocument.Parse(json).RootElement
            .GetProperty("pipeline")[1].GetProperty("$project");
        Assert.Equal(1, proj.GetProperty("Level").GetInt32());
    }

    [Fact]
    public void Seviye_kolonu_tahmini_ada_gore_bulur_yoksa_null()
    {
        SemaKolonu[] kolonlar =
        [
            new("Id", "bigint", false, true),
            new("Message", "nvarchar(max)", true, false),
            new("Level", "nvarchar(16)", true, false),
        ];
        Assert.Equal("Level", LogTabloAnaliz.SeviyeKolonuTahmini(kolonlar));

        SemaKolonu[] seviyesiz = [new("Id", "int", false, true), new("Message", "nvarchar", true, false)];
        Assert.Null(LogTabloAnaliz.SeviyeKolonuTahmini(seviyesiz));
    }

    [Fact]
    public void Seviye_degerleri_kova_ve_coklu_kovayi_acar()
    {
        Assert.Contains("Error", LogTabloAnaliz.SeviyeDegerleri("Hata"));
        Assert.Contains("Fatal", LogTabloAnaliz.SeviyeDegerleri("Hata"));
        Assert.Contains("Warning", LogTabloAnaliz.SeviyeDegerleri("Uyarı"));

        IReadOnlyList<string> coklu = LogTabloAnaliz.SeviyeDegerleriCoklu(["Hata", "Uyarı"]);
        Assert.Contains("Error", coklu);
        Assert.Contains("Warning", coklu);
        Assert.Equal(coklu.Count, coklu.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // tekrarsız
    }

    [Fact]
    public void Kesif_sorgulari_seviye_suzgecini_where_e_ekler()
    {
        // Son 24 saat + seviye: mevcut zaman WHERE'ine AND seviye IN(...) eklenir (yoksa çıktı birebir aynı).
        Assert.Equal(
            "SELECT TOP (100) Mesaj, Zaman, Lvl FROM L WHERE Zaman >= DATEADD(HOUR, -24, SYSDATETIME()) AND Lvl IN ('Error', 'Fatal') ORDER BY Zaman DESC;",
            LogTabloAnaliz.Orneklem24SaatSorgusu(MotorTuru.Mssql, "L", "Mesaj", "Zaman", 100, "Lvl", ["Error", "Fatal"]));

        // Süzgeçsiz temel örneklem + yalnız seviye → yeni WHERE eklenir.
        Assert.Equal(
            "SELECT TOP (100) Mesaj, Lvl FROM L WHERE Lvl IN ('Error');",
            LogTabloAnaliz.OrneklemSorgusu(MotorTuru.Mssql, "L", "Mesaj", 100, null, "Lvl", ["Error"]));
    }

    [Fact]
    public void Grupla_uclu_ilk_gorulme_baskin_seviye_ve_deseni_uretir()
    {
        var t = new DateTime(2026, 8, 5, 10, 0, 0);
        (object? Mesaj, object? Zaman, object? Seviye)[] satirlar =
        [
            ("Tablo 'A' yok", t.AddHours(-2), "Error"),
            ("Tablo 'B' yok", t, "Error"),
            ("Tablo 'C' yok", t.AddHours(-5), "Warning"),   // aynı imza; azınlık seviye
        ];

        IReadOnlyList<TabloLogGrubu> g = LogTabloAnaliz.Grupla(satirlar);

        Assert.Single(g);
        Assert.Equal(3, g[0].Sayi);
        Assert.Equal(t, g[0].SonGorulme);
        Assert.Equal(t.AddHours(-5), g[0].IlkGorulme);      // en eski
        Assert.Equal("Error", g[0].Seviye);                 // baskın (2×) seviye
        Assert.Equal("Tablo % yok", g[0].Desen);            // LIKE kalıbı
        Assert.False(g[0].GercekSayimMi);                   // gruplama örneklem sayar
    }

    [Theory]
    [InlineData(MotorTuru.Mssql,
        "SELECT SUM(CASE WHEN Mesaj LIKE 'Tablo % yok' ESCAPE '!' THEN 1 ELSE 0 END) AS c0, SUM(CASE WHEN Mesaj LIKE 'Zaman asimi' ESCAPE '!' THEN 1 ELSE 0 END) AS c1 FROM dbo.Log WHERE Zaman >= DATEADD(HOUR, -24, SYSDATETIME());")]
    public void Gercek_sayim_mssql_son24saat_tek_taramada_n_deseni_sayar(MotorTuru motor, string beklenen)
        => Assert.Equal(beklenen, LogTabloAnaliz.GercekSayimSorgusu(
            motor, "dbo.Log", "Mesaj", ["Tablo % yok", "Zaman asimi"], zamanKolon: "Zaman", son24Saat: true));

    [Fact]
    public void Gercek_sayim_seviye_suzgecini_where_e_katar()
        => Assert.Equal(
            "SELECT SUM(CASE WHEN M LIKE 'a' ESCAPE '!' THEN 1 ELSE 0 END) AS c0 FROM L WHERE Lvl IN ('Error', 'Fatal');",
            LogTabloAnaliz.GercekSayimSorgusu(MotorTuru.Mssql, "L", "M", ["a"],
                seviyeKolon: "Lvl", seviyeler: ["Error", "Fatal"]));

    [Fact]
    public void Gercek_sayim_postgres_aralik_ve_oracle_noktalivirgulsuz()
    {
        Assert.Equal(
            "SELECT SUM(CASE WHEN msg LIKE 'a%' ESCAPE '!' THEN 1 ELSE 0 END) AS c0 FROM log WHERE ts >= '2026-07-01T00:00:00'::timestamp AND ts < '2026-07-26T00:00:00'::timestamp;",
            LogTabloAnaliz.GercekSayimSorgusu(MotorTuru.Postgres, "log", "msg", ["a%"],
                zamanKolon: "ts", bas: new DateTime(2026, 7, 1), bit: new DateTime(2026, 7, 26)));

        Assert.Equal(
            "SELECT SUM(CASE WHEN m LIKE 'p' ESCAPE '!' THEN 1 ELSE 0 END) AS c0 FROM log",
            LogTabloAnaliz.GercekSayimSorgusu(MotorTuru.Oracle, "log", "m", ["p"]));
    }

    [Fact]
    public void Gercek_sayim_mysql_string_literalde_ters_boluyu_ikizler()
        => Assert.Contains(@"LIKE 'a\\b' ESCAPE '!'",   // C#: a + \\ + b  → SQL literal iki ters bölü
            LogTabloAnaliz.GercekSayimSorgusu(MotorTuru.MySql, "log", "m", [@"a\b"]),
            StringComparison.Ordinal);

    [Fact]
    public void Gercek_sayim_mongo_group_regexMatch_ve_match_uretir()
    {
        string json = LogTabloAnaliz.GercekSayimSorgusu(
            MotorTuru.Mongo, "logs", "msg", ["a.*b"],
            zamanKolon: "ts", son24Saat: true, seviyeKolon: "lvl", seviyeler: ["Error"]);
        JsonElement kok = JsonDocument.Parse(json).RootElement;
        Assert.Equal("logs", kok.GetProperty("aggregate").GetString());

        JsonElement boru = kok.GetProperty("pipeline");
        Assert.Equal(2, boru.GetArrayLength());
        Assert.Equal("Error",
            boru[0].GetProperty("$match").GetProperty("lvl").GetProperty("$in")[0].GetString());
        JsonElement regexMatch = boru[1].GetProperty("$group").GetProperty("c0")
            .GetProperty("$sum").GetProperty("$cond")[0].GetProperty("$regexMatch");
        Assert.Equal("a.*b", regexMatch.GetProperty("regex").GetString());
        Assert.Equal("$msg", regexMatch.GetProperty("input").GetString());
    }

    [Theory]
    [InlineData(MotorTuru.Mssql,
        "SELECT TOP (200) * FROM dbo.Log WHERE Msg LIKE 'Tablo % yok' ESCAPE '!' AND Zaman >= DATEADD(HOUR, -24, SYSDATETIME()) ORDER BY Zaman DESC;")]
    public void Detay_mssql_son24saat_ham_satirlari_en_yeni_once_getirir(MotorTuru motor, string beklenen)
        => Assert.Equal(beklenen, LogTabloAnaliz.DetaySorgusu(
            motor, "dbo.Log", "Msg", "Tablo % yok", 200, zamanKolon: "Zaman", son24Saat: true));

    [Fact]
    public void Detay_postgres_aralik_seviye_ve_oracle_zamansiz()
    {
        Assert.Equal(
            "SELECT * FROM log WHERE msg LIKE 'p%' ESCAPE '!' AND ts >= '2026-07-01T00:00:00'::timestamp AND ts < '2026-07-26T00:00:00'::timestamp AND lvl IN ('Error') ORDER BY ts DESC LIMIT 200;",
            LogTabloAnaliz.DetaySorgusu(MotorTuru.Postgres, "log", "msg", "p%", 200,
                zamanKolon: "ts", bas: new DateTime(2026, 7, 1), bit: new DateTime(2026, 7, 26),
                seviyeKolon: "lvl", seviyeler: ["Error"]));

        // Zaman kolonu yoksa ORDER BY yok; Oracle noktalı virgül almaz, FETCH FIRST ile sınırlar.
        Assert.Equal(
            "SELECT * FROM t WHERE m LIKE 'p' ESCAPE '!' FETCH FIRST 50 ROWS ONLY",
            LogTabloAnaliz.DetaySorgusu(MotorTuru.Oracle, "t", "m", "p", 50));
    }

    [Fact]
    public void Detay_mongo_find_regex_sort_limit_uretir()
    {
        string json = LogTabloAnaliz.DetaySorgusu(
            MotorTuru.Mongo, "logs", "msg", "a.*", 100,
            zamanKolon: "ts",
            bas: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            bit: new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc));
        JsonElement kok = JsonDocument.Parse(json).RootElement;
        Assert.Equal("logs", kok.GetProperty("find").GetString());
        Assert.Equal(100, kok.GetProperty("limit").GetInt32());
        Assert.Equal(-1, kok.GetProperty("sort").GetProperty("ts").GetInt32());

        JsonElement filtre = kok.GetProperty("filter");
        Assert.Equal("a.*", filtre.GetProperty("msg").GetProperty("$regex").GetString());
        Assert.Equal("2026-07-01T00:00:00Z", filtre.GetProperty("ts").GetProperty("$gte").GetProperty("$date").GetString());
    }

    /// <summary>
    /// v22-S1 (saha turu-2 m.2): SON N KAYIT sorgusu — zaman SÜZGECİ YOK, tarama yönü $natural:-1
    /// (en yeniden geriye), projeksiyonda mesaj + zaman + seviye. Zaman alanı index'siz olduğunda
    /// "son 24 saat" süzgeci tüm koleksiyonu tarıyordu (ölçüm: 600k belgede 1.718 ms ↔ 114 ms).
    /// </summary>
    /// <summary>
    /// v22-S2 (m.2 İKİNCİ tur — kullanıcı 0.23.0'te de "5 dakika bekledim"): son-N sorgusu artık
    /// find+$natural değil <b>aggregate + $sort {_id:-1}</b>. İki nedeni ölçüldü: (a) _id index'i her
    /// koleksiyonda vardır → ilk N index'ten gelir (200.000 belgede 222 ms); (b) mesajı SUNUCUDA
    /// kırpmak için ifade projeksiyonu gerekir ve bu find'da yalnız MongoDB 4.4+'ta destekli.
    /// Kırpma ağdaki veriyi düşürür (19,8 MB → 7,3 MB ölçüldü) — asıl darboğaz oydu.
    /// </summary>
    [Fact]
    public void Son_kayitlar_mongo_id_sirasiyla_ve_kirpilmis_mesajla_uretir()
    {
        string json = LogTabloAnaliz.SonKayitlarSorgusu(
            MotorTuru.Mongo, "ExceptionLog", "Mesaj", 5_000, "Zaman", "Seviye", ["Error"]);
        JsonElement kok = JsonDocument.Parse(json).RootElement;

        Assert.Equal("ExceptionLog", kok.GetProperty("aggregate").GetString());
        JsonElement boru = kok.GetProperty("pipeline");
        Assert.Equal(4, boru.GetArrayLength()); // $match(seviye) + $sort + $limit + $project

        // Seviye süzgeci korunur; ZAMAN süzgeci YOK (kısayolun tüm amacı bu).
        JsonElement filtre = boru[0].GetProperty("$match");
        Assert.Equal("Error", filtre.GetProperty("Seviye").GetProperty("$in")[0].GetString());
        Assert.False(filtre.TryGetProperty("Zaman", out _));
        Assert.False(filtre.TryGetProperty("$expr", out _));

        Assert.Equal(-1, boru[1].GetProperty("$sort").GetProperty("_id").GetInt32());
        Assert.Equal(5_000, boru[2].GetProperty("$limit").GetInt32());

        JsonElement p = boru[3].GetProperty("$project");
        Assert.Equal(LogTabloAnaliz.MongoMesajKirpma,
            p.GetProperty("Mesaj").GetProperty("$cond")[1].GetProperty("$substrCP")[2].GetInt32());
        Assert.Equal(1, p.GetProperty("Zaman").GetInt32());   // İlk/Son görülme için zaman da gelir
        Assert.Equal(1, p.GetProperty("Seviye").GetInt32());
        Assert.Equal(0, p.GetProperty("_id").GetInt32());
    }

    [Fact]
    public void Son_kayitlar_sql_motorlarinda_net_hata()
        => Assert.Throws<NotSupportedException>(() =>
            LogTabloAnaliz.SonKayitlarSorgusu(MotorTuru.Mssql, "dbo.Logs", "Mesaj", 100));

    // ── v22-S12: EK KOLONLAR ("birden fazla kolonu yan yana" — kullanıcı 2026-09-17) ─────────────

    [Fact]
    public void Orneklem_sorgusu_ek_kolonlari_projeler_ve_tekrari_ayiklar()
    {
        // Ek kolonlar SELECT'in sonuna girer; zaten listede olan ad (zaman kolonu) İKİNCİ KEZ eklenmez.
        Assert.Equal("SELECT TOP (100) Mesaj, Zaman, ExMsg, Kaynak FROM dbo.ExceptionLog;",
            LogTabloAnaliz.OrneklemSorgusu(MotorTuru.Mssql, "dbo.ExceptionLog", "Mesaj", 100, "Zaman",
                ekKolonlar: ["ExMsg", "Zaman", "Kaynak"]));

        // 24 saat + aralık sorguları da aynı seçimi kullanır (tek Secim kaynağı).
        Assert.StartsWith("SELECT Mesaj, Zaman, ExMsg FROM L WHERE",
            LogTabloAnaliz.Orneklem24SaatSorgusu(MotorTuru.Postgres, "L", "Mesaj", "Zaman", 100,
                ekKolonlar: ["ExMsg"]));
        Assert.StartsWith("SELECT Mesaj, Zaman, ExMsg FROM L WHERE",
            LogTabloAnaliz.OrneklemAralikSorgusu(MotorTuru.Postgres, "L", "Mesaj", "Zaman",
                new DateTime(2026, 9, 1), new DateTime(2026, 9, 2), 100, ekKolonlar: ["ExMsg"]));
    }

    [Fact]
    public void Mongo_ek_alanlar_projeksiyonda_mesaj_gibi_kirpilir()
    {
        // Ek alan görünüm örneğidir: string ise sunucuda MongoMesajKirpma'ya iner (v22-S2 ağ ölçümü),
        // string değilse $cond onu olduğu gibi geçirir. _id yine elenir.
        string json = LogTabloAnaliz.SonKayitlarSorgusu(
            MotorTuru.Mongo, "ExceptionLog", "Mesaj", 5_000, "Zaman", ekKolonlar: ["ExMsg"]);
        JsonElement p = JsonDocument.Parse(json).RootElement
            .GetProperty("pipeline")[2].GetProperty("$project"); // $match yok (seviye süzgeci verilmedi)
        Assert.Equal(LogTabloAnaliz.MongoMesajKirpma,
            p.GetProperty("ExMsg").GetProperty("$cond")[1].GetProperty("$substrCP")[2].GetInt32());
        Assert.Equal(1, p.GetProperty("Zaman").GetInt32());
        Assert.Equal(0, p.GetProperty("_id").GetInt32());
    }

    [Fact]
    public void Grupla_ek_degerleri_ornek_mesajla_ayni_satirdan_alir()
    {
        (object? Mesaj, object? Zaman, object? Seviye, object?[]? Ekler)[] satirlar =
        [
            ("Tablo 'A' yok", null, null, ["ilk açıklama", 7]),
            ("Tablo 'B' yok", null, null, ["ikinci açıklama", 9]),   // aynı imza — örnek İLK satırdır
            ("Zaman asimi", null, null, [null, DBNull.Value]),        // boşlar null'a çözülür
        ];

        IReadOnlyList<TabloLogGrubu> g = LogTabloAnaliz.Grupla(satirlar);

        Assert.Equal(2, g.Count);
        TabloLogGrubu tablo = g.Single(x => x.OrnekMesaj == "Tablo 'A' yok");
        Assert.Equal(new string?[] { "ilk açıklama", "7" }, tablo.EkDegerler); // örnek mesajla AYNI satır
        TabloLogGrubu zaman = g.Single(x => x.OrnekMesaj == "Zaman asimi");
        Assert.Equal(new string?[] { null, null }, zaman.EkDegerler);

        // Ek verilmeyen eski aşırı yükleme null bırakır (görünüm katmanı boş listeye çevirir).
        (object? Mesaj, object? Zaman, object? Seviye)[] eski = [("Mesaj", null, null)];
        Assert.Null(LogTabloAnaliz.Grupla(eski)[0].EkDegerler);
    }
}
