using System.Text.Json;
using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.26 fikir 9 — shell komutu yapıştır: Compass/mongosh metni SQLST'nin çalıştırdığı
/// biçime çevrilir. Kritik olan shell'in GEVŞEK JSON'u: tırnaksız anahtar, tek tırnak,
/// ObjectId/ISODate sarmaları, /regex/i. Üretilen her çıktı GEÇERLİ JSON olmalı (parse edilerek
/// doğrulanır) — bozuk sorgu kullanıcının eline geçmemeli.
/// </summary>
public class MongoShellCeviriciTests
{
    private static JsonElement Cevir(string shell)
    {
        (string? json, string? hata) = MongoShellCevirici.Cevir(shell);
        Assert.Null(hata);
        return JsonDocument.Parse(json!).RootElement;
    }

    [Fact]
    public void Find_sort_limit_zinciri_cevrilir()
    {
        JsonElement k = Cevir("db.oturumlar.find({ kullaniciId: 5 }).sort({ ts: -1 }).limit(10)");

        Assert.Equal("oturumlar", k.GetProperty("find").GetString());
        Assert.Equal(5, k.GetProperty("filter").GetProperty("kullaniciId").GetInt32());
        Assert.Equal(-1, k.GetProperty("sort").GetProperty("ts").GetInt32());
        Assert.Equal(10, k.GetProperty("limit").GetInt32());
    }

    /// <summary>
    /// v22-S3 saha turu-3 m.4: MongoDB Compass'ın "Copy" düğmesi sorguyu <c>db.getCollection('ad')</c>
    /// biçiminde verir — yani kullanıcının EN SIK yapıştıracağı metin. Eski ayrıştırıcı "db." ile
    /// "(" arasında nokta göremediği için bunu reddediyordu ("koleksiyon ya da metot adı okunamadı").
    /// </summary>
    [Theory]
    [InlineData("db.getCollection('oturumlar').find({ kullaniciId: 5 }).limit(10)")]
    [InlineData("db.getCollection(\"oturumlar\").find({ kullaniciId: 5 }).limit(10)")]
    [InlineData("db.getCollection( 'oturumlar' ).find({ kullaniciId: 5 }).limit(10)")]
    public void GetCollection_bicimi_de_cevrilir(string shell)
    {
        JsonElement k = Cevir(shell);

        Assert.Equal("oturumlar", k.GetProperty("find").GetString());
        Assert.Equal(5, k.GetProperty("filter").GetProperty("kullaniciId").GetInt32());
        Assert.Equal(10, k.GetProperty("limit").GetInt32());
    }

    /// <summary>Tırnak içindeki NOKTALI koleksiyon adı (log.2026) getCollection açılırken bozulmaz.</summary>
    [Fact]
    public void GetCollection_noktali_ad_bozulmaz()
        => Assert.Equal("log.2026", Cevir("db.getCollection('log.2026').find({})").GetProperty("find").GetString());

    [Fact]
    public void ObjectId_ve_isodate_extended_jsona_doner()
    {
        JsonElement f = Cevir("""db.log.find({ actorId: ObjectId("507f1f77bcf86cd799439011"), ts: ISODate("2026-08-14T10:00:00Z") })""")
            .GetProperty("filter");

        Assert.Equal("507f1f77bcf86cd799439011", f.GetProperty("actorId").GetProperty("$oid").GetString());
        Assert.Equal("2026-08-14T10:00:00Z", f.GetProperty("ts").GetProperty("$date").GetString());
    }

    [Fact]
    public void Tek_tirnak_ve_new_date_desteklenir()
    {
        JsonElement f = Cevir("db.log.find({ seviye: 'HATA', ts: new Date('2026-01-01') })").GetProperty("filter");

        Assert.Equal("HATA", f.GetProperty("seviye").GetString());
        Assert.Equal("2026-01-01", f.GetProperty("ts").GetProperty("$date").GetString());
    }

    [Fact]
    public void Regex_dolar_regexe_cevrilir()
    {
        JsonElement f = Cevir("db.log.find({ mesaj: /timeout/i })").GetProperty("filter");

        Assert.Equal("timeout", f.GetProperty("mesaj").GetProperty("$regex").GetString());
        Assert.Equal("i", f.GetProperty("mesaj").GetProperty("$options").GetString());
    }

    [Fact]
    public void Operatorler_ve_ic_ice_belgeler_korunur()
    {
        JsonElement f = Cevir("db.log.find({ $and: [ { millis: { $gt: 100 } }, { seviye: { $in: ['HATA','UYARI'] } } ] })")
            .GetProperty("filter");

        Assert.Equal(100, f.GetProperty("$and")[0].GetProperty("millis").GetProperty("$gt").GetInt32());
        Assert.Equal("UYARI", f.GetProperty("$and")[1].GetProperty("seviye").GetProperty("$in")[1].GetString());
    }

    [Fact]
    public void Findone_limit_1_ekler_ve_projeksiyon_cevrilir()
    {
        JsonElement k = Cevir("db.kullanicilar.findOne({ eposta: 'a@b.c' }, { ad: 1, _id: 0 })");

        Assert.Equal(1, k.GetProperty("limit").GetInt32());
        Assert.Equal(1, k.GetProperty("projection").GetProperty("ad").GetInt32());
        Assert.Equal(0, k.GetProperty("projection").GetProperty("_id").GetInt32());
    }

    [Fact]
    public void Aggregate_pipeline_aynen_gecer()
    {
        JsonElement k = Cevir("db.log.aggregate([ { $match: { seviye: 'HATA' } }, { $group: { _id: '$kaynak', n: { $sum: 1 } } } ])");

        Assert.Equal("log", k.GetProperty("aggregate").GetString());
        Assert.Equal("HATA", k.GetProperty("pipeline")[0].GetProperty("$match").GetProperty("seviye").GetString());
        Assert.Equal("$kaynak", k.GetProperty("pipeline")[1].GetProperty("$group").GetProperty("_id").GetString());
    }

    [Fact]
    public void Countdocuments_aggregate_sayimina_cevrilir()
    {
        JsonElement k = Cevir("db.log.countDocuments({ seviye: 'HATA' })");

        Assert.Equal("log", k.GetProperty("aggregate").GetString());
        Assert.Equal("adet", k.GetProperty("pipeline")[1].GetProperty("$count").GetString());
    }

    [Fact]
    public void Pretty_ve_toarray_sessizce_atlanir()
    {
        JsonElement k = Cevir("db.log.find({}).limit(5).pretty()");
        Assert.Equal(5, k.GetProperty("limit").GetInt32());
    }

    [Theory]
    [InlineData("", "çevrilecek bir shell komutu yok")]
    [InlineData("SELECT * FROM T", "db.' ile başlamıyor")]
    [InlineData("db.log.find({ a: 1 }", "parantezi kapanmıyor")]
    [InlineData("db.log.updateMany({}, {})", "çevrilmiyor")]
    [InlineData("db.log.find({}).map(x => x)", "çevrilmiyor")]
    public void Desteklenmeyen_girdide_net_hata(string shell, string mesajParcasi)
    {
        (string? json, string? hata) = MongoShellCevirici.Cevir(shell);
        Assert.Null(json);
        Assert.Contains(mesajParcasi, hata);
    }
}
