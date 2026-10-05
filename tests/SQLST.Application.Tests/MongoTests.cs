using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;
using MongoDB.Bson;

namespace SQLST.Application.Tests;

/// <summary>Mongo ailesi birim testleri (V3-S2/S3): BSON→grid eşleme + sorgu metni üretimi — sunucusuz.</summary>
public class MongoBirimTests
{
    [Fact]
    public void Kolonlar_gorulme_sirali_birlesim_eksik_alan_dbnull()
    {
        // Şemasızlık: belgeler farklı alan kümeleri taşır (08-v3r1 §6 kolon birleşimi)
        List<BsonDocument> belgeler =
        [
            BsonDocument.Parse("""{ "_id": 1, "ad": "Ahmet" }"""),
            BsonDocument.Parse("""{ "_id": 2, "bakiye": 42.5 }"""),
        ];

        ResultSetData set = MongoSonucEsleyici.TabloyaCevir(belgeler);

        Assert.Equal(["_id", "ad", "bakiye"], set.Kolonlar.Select(k => k.Ad));
        Assert.Equal("Ahmet", set.Satirlar[0][1]);
        Assert.Equal(DBNull.Value, set.Satirlar[0][2]); // 1. belgede bakiye yok
        Assert.Equal(DBNull.Value, set.Satirlar[1][1]); // 2. belgede ad yok
        Assert.Equal(42.5, set.Satirlar[1][2]);
    }

    [Fact]
    public void Ic_ice_belge_ve_dizi_json_hucre_olarak_gelir()
    {
        List<BsonDocument> belgeler =
        [
            BsonDocument.Parse("""{ "_id": 1, "adres": { "sehir": "Ankara", "posta": 6420 }, "etiketler": ["a","b"] }"""),
        ];

        ResultSetData set = MongoSonucEsleyici.TabloyaCevir(belgeler);

        Assert.Equal("document", set.Kolonlar[1].TipAdi);
        string adres = Assert.IsType<string>(set.Satirlar[0][1]); // iç içe → relaxed JSON string
        Assert.Contains("Ankara", adres);
        string etiketler = Assert.IsType<string>(set.Satirlar[0][2]);
        Assert.Contains("\"a\"", etiketler);
    }

    [Fact]
    public void Skaler_tipler_clr_karsiliklarina_iner()
    {
        var belge = new BsonDocument
        {
            ["_id"] = ObjectId.Parse("507f1f77bcf86cd799439011"),
            ["i"] = 42,
            ["l"] = 42L,
            ["b"] = true,
            ["t"] = new BsonDateTime(new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc)),
            ["n"] = BsonNull.Value,
        };

        ResultSetData set = MongoSonucEsleyici.TabloyaCevir([belge]);
        object?[] satir = set.Satirlar[0];

        Assert.Equal("507f1f77bcf86cd799439011", satir[0]); // ObjectId → string
        Assert.Equal(42, satir[1]);
        Assert.Equal(42L, satir[2]);
        Assert.Equal(true, satir[3]);
        Assert.IsType<DateTime>(satir[4]);
        Assert.Equal(DBNull.Value, satir[5]);
    }

    [Fact]
    public void Koleksiyonda_ilk_n_mongo_find_jsonu_uretir()
    {
        var koleksiyon = new SemaNesnesi("demo", "demo", "musteri", SemaNesneTuru.Koleksiyon, [], []);
        string metin = NesneScriptleyici.SelectScripti(koleksiyon, 200);

        Assert.Equal("""{ "find": "musteri", "limit": 200 }""", metin);
        BsonDocument.Parse(metin); // geçerli JSON — motor yorumlayabilir

        // Tablo yolu değişmedi (regresyon)
        var tablo = new SemaNesnesi("db", "dbo", "T", SemaNesneTuru.Tablo, [], []);
        Assert.Equal("SELECT TOP 5 * FROM [dbo].[T];", NesneScriptleyici.SelectScripti(tablo, 5));
    }

    [Fact]
    public void Koleksiyon_adindaki_tirnak_json_icinde_kacirilir()
    {
        var garip = new SemaNesnesi("demo", "demo", "ko\"leksiyon", SemaNesneTuru.Koleksiyon, [], []);
        string metin = NesneScriptleyici.SelectScripti(garip, 10);
        Assert.Equal("ko\"leksiyon", BsonDocument.Parse(metin)["find"].AsString); // kaçış doğru çözülür
    }

    // Mongo sekmesinde "Biçimlendir" T-SQL çözümleyicisine gidip hata veriyordu
    // (kullanıcı bulgusu 2026-07-18) — artık JSON biçimlendirici kullanılır.
    [Fact]
    public void Mongo_sorgusu_json_olarak_bicimlendirilir()
    {
        (string? sonuc, string? hata) = JsonBicimleyici.Bicimlendir(
            """{ "find": "musteri", "filter": { "aktif": true }, "limit": 200 }""");

        Assert.Null(hata);
        Assert.Contains("\n", sonuc);                 // girintilendi
        Assert.Contains("\"find\": \"musteri\"", sonuc);
        BsonDocument.Parse(sonuc!);                   // hâlâ geçerli / çalıştırılabilir
    }

    [Fact]
    public void Bicimlendirici_turkce_karakterleri_ve_operatorleri_kacirmaz()
    {
        (string? sonuc, _) = JsonBicimleyici.Bicimlendir(
            """{"filter":{"ad":"Gülşah Ünal","bakiye":{"$gt":100}}}""");

        Assert.Contains("Gülşah Ünal", sonuc);  // \u kaçışına dönüşmemeli
        Assert.Contains("$gt", sonuc);
    }

    [Fact]
    public void Bozuk_jsonda_metne_dokunulmaz_ve_satir_bildirilir()
    {
        (string? sonuc, string? hata) = JsonBicimleyici.Bicimlendir("{ bozuk ");

        Assert.Null(sonuc);
        Assert.Contains("Geçerli JSON değil", hata);
    }
}

/// <summary>
/// V3-S2/S3 CANLI KANITI: Mongo ailesi (executor + şema + oturum) gerçek mongod'a karşı.
/// Ortam-kapılı: 127.0.0.1:27027'de mongod yoksa atlanır. Veri, MongoExecutor'ın KENDİSİYLE
/// eklenir (insert de RunCommand yolundan geçer — çift doğrulama).
/// </summary>
public class MongoCanliTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 27027;
    private const string Db = "sqlst_demo";

    private static ConnectionProfile Profil() => new()
    {
        Ad = "mongo-demo",
        Motor = MotorTuru.Mongo,
        Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Windows, // auth'suz yerel sunucu — kimlik gönderilmez
        BaglantiTimeoutSn = 5,
    };

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

    private static ExecuteOptions Secenek() => new() { VeritabaniOverride = Db };

    private static async Task VeriKurAsync(MongoExecutor executor)
    {
        await executor.ExecuteAsync(Profil(), """{ "drop": "musteri" }""", Secenek(), CancellationToken.None);
        QueryResult ekle = await executor.ExecuteAsync(Profil(), """
            { "insert": "musteri", "documents": [
                { "_id": 1, "ad": "Ahmet Celik", "bakiye": 1250.75, "aktif": true,
                  "adres": { "sehir": "Ankara" }, "etiketler": ["vip","yeni"] },
                { "_id": 2, "ad": "Sukru Ozturk", "aktif": false },
                { "_id": 3, "ad": "Gulsah Unal", "bakiye": 42.0, "aktif": true }
            ] }
            """, Secenek(), CancellationToken.None);
        Assert.True(ekle.Basarili, ekle.Hata?.Mesaj);
    }

    [Fact]
    public async Task Find_filtre_sirala_limit_calisir_ve_gride_iner()
    {
        if (!Erisilebilir()) return; // ortam-kapılı: mongod yoksa atla
        MongoExecutor executor = Executor();
        await VeriKurAsync(executor);

        QueryResult sonuc = await executor.ExecuteAsync(Profil(), """
            { "find": "musteri", "filter": { "aktif": true }, "sort": { "_id": -1 }, "limit": 10 }
            """, Secenek(), CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        ResultSetData set = Assert.Single(sonuc.ResultSetler);
        Assert.Equal(2, set.Satirlar.Count);                       // yalnız aktifler
        Assert.Equal(3, Convert.ToInt32(set.Satirlar[0][0]));      // sort: _id desc
        int adresKolonu = set.Kolonlar.ToList().FindIndex(k => k.Ad == "adres");
        Assert.Contains("Ankara", (string)set.Satirlar[1][adresKolonu]!); // iç içe → JSON hücre
    }

    [Fact]
    public async Task Aggregate_pipeline_calisir()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        MongoExecutor executor = Executor();
        await VeriKurAsync(executor);

        QueryResult sonuc = await executor.ExecuteAsync(Profil(), """
            { "aggregate": "musteri", "pipeline": [
                { "$match": { "aktif": true } },
                { "$group": { "_id": null, "toplam": { "$sum": "$bakiye" } } }
            ] }
            """, Secenek(), CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        object? toplam = sonuc.ResultSetler[0].Satirlar[0][1];
        Assert.Equal(1292.75, Convert.ToDouble(toplam), precision: 2);
    }

    [Fact]
    public async Task Satir_siniri_mongo_akisinda_da_uygulanir()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        MongoExecutor executor = Executor();
        await VeriKurAsync(executor);

        QueryResult sonuc = await executor.ExecuteAsync(Profil(),
            """{ "find": "musteri" }""",
            new ExecuteOptions { SatirSiniri = 2, VeritabaniOverride = Db }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.Equal(2, sonuc.ResultSetler[0].Satirlar.Count);
        Assert.True(sonuc.SatirSiniriAsildi);
    }

    [Fact]
    public async Task Sema_agaci_koleksiyonlari_ve_alan_envanterini_cikari()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        MongoExecutor executor = Executor();
        await VeriKurAsync(executor);
        var schema = new MongoSchemaService(new DpapiSecretProtector());

        IReadOnlyList<VeritabaniBilgisi> dbler = await schema.VeritabanlariAsync(Profil(), CancellationToken.None);
        Assert.Contains(dbler, d => d.Ad == Db && !d.SistemMi);
        Assert.Contains(dbler, d => d.Ad == "admin" && d.SistemMi);

        SemaOnbellegi sema = await schema.YukleAsync(Profil(), Db, CancellationToken.None);
        SemaNesnesi musteri = Assert.Single(sema.Nesneler, n => n.Ad == "musteri");
        Assert.Equal(SemaNesneTuru.Koleksiyon, musteri.Tur);
        Assert.True(musteri.Kolonlar.Single(k => k.Ad == "_id").PkMi);
        Assert.Equal("string", musteri.Kolonlar.Single(k => k.Ad == "ad").Tip);
        Assert.True(musteri.Kolonlar.Single(k => k.Ad == "bakiye").NullOlabilir); // 2. belgede yok
        Assert.Equal("document", musteri.Kolonlar.Single(k => k.Ad == "adres").Tip);
    }

    // Mongo, olmayan koleksiyonda sessizce BOŞ döndürüyordu; yanlış DB seçiliyken bu
    // "veri yok" gibi görünüyordu (kullanıcı bulgusu 2026-07-18) → artık açık hata.
    [Fact]
    public async Task Olmayan_koleksiyonda_acik_hata_verilir_ve_mevcutlar_listelenir()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        MongoExecutor executor = Executor();
        await VeriKurAsync(executor);

        QueryResult sonuc = await executor.ExecuteAsync(Profil(),
            """{ "find": "boyle_bir_koleksiyon_yok" }""", Secenek(), CancellationToken.None);

        Assert.False(sonuc.Basarili);
        Assert.NotNull(sonuc.Hata);
        Assert.Contains("boyle_bir_koleksiyon_yok", sonuc.Hata!.Mesaj);
        Assert.Contains(Db, sonuc.Hata.Mesaj);          // hangi veritabanında aradığı
        Assert.Contains("musteri", sonuc.Hata.Mesaj);   // o DB'de NE olduğu
    }

    [Fact]
    public async Task Yanlis_veritabaninda_var_olan_koleksiyon_adi_da_hata_verir()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        MongoExecutor executor = Executor();
        await VeriKurAsync(executor);

        // 'musteri' sqlst_demo'da var ama 'config'te yok — kullanıcının senaryosu
        QueryResult sonuc = await executor.ExecuteAsync(Profil(),
            """{ "find": "musteri", "limit": 200 }""",
            new ExecuteOptions { VeritabaniOverride = "config" }, CancellationToken.None);

        Assert.False(sonuc.Basarili);
        Assert.Contains("config", sonuc.Hata!.Mesaj);
    }

    [Fact]
    public async Task Yazma_komutlari_koleksiyon_denetimine_takilmaz()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        MongoExecutor executor = Executor();

        // insert olmayan koleksiyonu OLUŞTURUR — denetim yazma yolunu engellememeli
        string yeni = $"gecici_{Guid.NewGuid():N}";
        QueryResult ekle = await executor.ExecuteAsync(Profil(),
            $$"""{ "insert": "{{yeni}}", "documents": [ { "x": 1 } ] }""", Secenek(), CancellationToken.None);
        Assert.True(ekle.Basarili, ekle.Hata?.Mesaj);

        QueryResult oku = await executor.ExecuteAsync(Profil(),
            $$"""{ "find": "{{yeni}}" }""", Secenek(), CancellationToken.None);
        Assert.True(oku.Basarili, oku.Hata?.Mesaj);

        await executor.ExecuteAsync(Profil(), $$"""{ "drop": "{{yeni}}" }""", Secenek(), CancellationToken.None);
    }

    [Fact]
    public async Task Yonlendirici_mongo_profilini_mongo_ailesine_gonderir()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        var protector = new DpapiSecretProtector();
        var yonlendirici = new MotorYonlendiriciExecutor(
            new SqlExecutor(protector), new MongoExecutor(protector));
        await VeriKurAsync(Executor());

        // Mongo JSON'u SQL ailesine gitseydi SqlClient bağlantı hatası alırdık
        QueryResult sonuc = await yonlendirici.ExecuteAsync(
            Profil(), """{ "find": "musteri", "limit": 1 }""", Secenek(), CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.Single(sonuc.ResultSetler[0].Satirlar);
    }
}
