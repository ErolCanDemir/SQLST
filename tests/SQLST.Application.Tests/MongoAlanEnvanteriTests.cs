using System.Text.Json;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v22-S2 (saha turu-2 m.2, İKİNCİ tur): Log Analizi'nde koleksiyon seçilince alan listesi için
/// <c>{"find": X, "limit": 50}</c> koşuyordu — 50 TAM BELGE ağdan geçiyordu. Kullanıcının QueryLog'u
/// gibi mesaj alanları KB'larca olan koleksiyonlarda bu tek başına megabaytlar (VPN arkasında
/// dakikalar). ÖLÇÜM (yerel, ~1 KB belgeler): eski yol 846 ms / 101 KB ↔ yeni yol <b>50 ms / 13 KB</b>
/// — gerçek büyük belgelerde fark çok daha yüksek, çünkü artık DEĞERLER hiç taşınmıyor.
/// </summary>
public class MongoAlanEnvanteriTests
{
    [Fact]
    public void Sorgu_degerleri_tasimaz_yalnizca_ad_ve_tip_ister()
    {
        JsonElement kok = JsonDocument.Parse(MongoAlanEnvanteri.Sorgu("QueryLog")).RootElement;

        Assert.Equal("QueryLog", kok.GetProperty("aggregate").GetString());
        JsonElement boru = kok.GetProperty("pipeline");
        Assert.Equal(MongoAlanEnvanteri.OrneklemBelge, boru[0].GetProperty("$limit").GetInt32());

        JsonElement map = boru[1].GetProperty("$project").GetProperty("alanlar").GetProperty("$map");
        Assert.Equal("$$ROOT", map.GetProperty("input").GetProperty("$objectToArray").GetString());
        JsonElement ic = map.GetProperty("in");
        Assert.Equal("$$a.k", ic.GetProperty("k").GetString());
        Assert.Equal("$$a.v", ic.GetProperty("t").GetProperty("$type").GetString());
        // Belge GÖVDESİ istenmiyor: projeksiyonda alan adı/tip dışında bir şey yok.
        Assert.Equal(0, boru[1].GetProperty("$project").GetProperty("_id").GetInt32());
    }

    private static string Satir(params (string Ad, string Tip)[] alanlar)
        => JsonSerializer.Serialize(alanlar.Select(a => new { k = a.Ad, t = a.Tip }));

    [Fact]
    public void Alanlar_ilk_gorulme_sirasinda_ve_tipleriyle_cozulur()
    {
        IReadOnlyList<SemaKolonu> envanter = MongoAlanEnvanteri.Coz(
        [
            Satir(("_id", "objectId"), ("Zaman", "date"), ("Mesaj", "string")),
            Satir(("_id", "objectId"), ("Zaman", "date"), ("Mesaj", "string"), ("Seviye", "string")),
        ]);

        Assert.Equal(["_id", "Zaman", "Mesaj", "Seviye"], envanter.Select(k => k.Ad).ToArray());
        Assert.Equal("date", envanter[1].Tip);       // zaman alanı tahmini buna bakar
        Assert.Equal("string", envanter[3].Tip);
    }

    [Fact]
    public void Sayisal_tipler_uygulamanin_sozlugune_eslenir()
    {
        // $type "int"/"long" der; MongoIdIzi ve tip tahminleri "int32"/"int64" bekler — eşlenmezse
        // ObjectId izi adayları ve alan tahminleri SESSİZCE bozulurdu.
        IReadOnlyList<SemaKolonu> envanter = MongoAlanEnvanteri.Coz(
            [Satir(("Sayi", "int"), ("Buyuk", "long"), ("Tutar", "decimal"), ("Aktif", "bool"))]);

        Assert.Equal("int32", envanter[0].Tip);
        Assert.Equal("int64", envanter[1].Tip);
        Assert.Equal("decimal128", envanter[2].Tip);
        Assert.Equal("boolean", envanter[3].Tip);
    }

    [Fact]
    public void Ilk_belgede_null_olan_alan_sonraki_belgeden_gercek_tipini_alir()
    {
        IReadOnlyList<SemaKolonu> envanter = MongoAlanEnvanteri.Coz(
            [Satir(("Zaman", "null")), Satir(("Zaman", "date"))]);

        Assert.Equal("date", Assert.Single(envanter).Tip);
    }

    [Fact]
    public void Bozuk_veya_bos_hucreler_atlanir_envanter_yine_kurulur()
    {
        IReadOnlyList<SemaKolonu> envanter = MongoAlanEnvanteri.Coz(
            [null, "", "{bozuk json", "\"dizi degil\"", Satir(("Mesaj", "string"))]);

        Assert.Equal("Mesaj", Assert.Single(envanter).Ad);
    }
}
