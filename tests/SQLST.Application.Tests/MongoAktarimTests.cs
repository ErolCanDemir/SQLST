using MongoDB.Bson;
using MongoDB.Driver;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Mongo→Mongo Paket Aktarım motoru (v12-S5) — GERÇEK localhost:27017'ye karşı (bu makinede
/// MongoDB servisi kurulu; kullanıcı senaryosu: log koleksiyonunu arşiv DB'sine taşıma).
/// Doğrular: süzgeçli kopya · aynı sunucuda DB'ler arası taşıma · _id upsert'ün eklenen/
/// güncellenen ayrımı · YalnizEkle'de çakışan _id'nin politikaya düşmesi. Test DB'leri drop'lanır.
/// </summary>
public class MongoAktarimTests
{
    private const string KaynakDb = "SqlstAkTestKaynak";
    private const string HedefDb = "SqlstAkTestArsiv";

    private static ConnectionProfile Profil() => new()
    {
        Ad = "mongo-local", Sunucu = "localhost:27017",
        Motor = MotorTuru.Mongo, BaglantiTimeoutSn = 15,
    };

    [Fact]
    public async Task Suzgecli_kopya_upsert_ayrimi_ve_cakisan_id_politikasi()
    {
        var protector = new DpapiSecretProtector();
        var servis = new MongoAktarimServisi(protector);
        var istemci = new MongoClient("mongodb://localhost:27017/?appName=SQLST-test");
        await istemci.DropDatabaseAsync(KaynakDb);
        await istemci.DropDatabaseAsync(HedefDb);

        try
        {
            IMongoCollection<BsonDocument> log = istemci.GetDatabase(KaynakDb).GetCollection<BsonDocument>("Log");
            await log.InsertManyAsync(
            [
                new BsonDocument { { "_id", 1 }, { "Seviye", "Hata" }, { "Mesaj", "kırıldı" } },
                new BsonDocument { { "_id", 2 }, { "Seviye", "Bilgi" }, { "Mesaj", "açıldı" } },
                new BsonDocument { { "_id", 3 }, { "Seviye", "Hata" }, { "Mesaj", "Türkçe ığüşöç" } },
            ]);

            // 1) Süzgeçli kopya: yalnız Seviye=Hata belgeleri arşive gider (2 belge).
            var istek = new MongoAktarimIstegi(
                Profil(), KaynakDb, "Log", """{ "Seviye": "Hata" }""",
                Profil(), HedefDb, "LogArsiv");
            AktarimSonucu sonuc = await servis.AktarAsync(istek, null, CancellationToken.None);

            Assert.True(sonuc.Basarili, sonuc.Hata);
            Assert.Equal(2, sonuc.Okunan);
            Assert.Equal(2, sonuc.Yazilan);
            Assert.Equal(0, sonuc.Guncellenen);

            IMongoCollection<BsonDocument> arsiv =
                istemci.GetDatabase(HedefDb).GetCollection<BsonDocument>("LogArsiv");
            Assert.Equal(2, await arsiv.CountDocumentsAsync(new BsonDocument()));
            BsonDocument? turkce = await arsiv.Find(new BsonDocument("_id", 3)).FirstOrDefaultAsync();
            Assert.Equal("Türkçe ığüşöç", turkce!["Mesaj"].AsString);

            // 2) Ekle/Güncelle: kaynakta belge değişti + süzgeç kalktı → 2 güncellenir, 1 eklenir.
            await log.UpdateOneAsync(new BsonDocument("_id", 1),
                new BsonDocument("$set", new BsonDocument("Mesaj", "düzeltildi")));
            AktarimSonucu upsert = await servis.AktarAsync(istek with
            {
                SuzgecJson = null,
                YazmaKipi = AktarimYazmaKipi.EkleGuncelle,
            }, null, CancellationToken.None);

            Assert.True(upsert.Basarili, upsert.Hata);
            Assert.Equal(3, upsert.Okunan);
            Assert.Equal(1, upsert.Yazilan);      // _id 2 yeni eklendi
            Assert.Equal(2, upsert.Guncellenen);  // _id 1 ve 3 değiştirildi
            BsonDocument? duzeltilmis = await arsiv.Find(new BsonDocument("_id", 1)).FirstOrDefaultAsync();
            Assert.Equal("düzeltildi", duzeltilmis!["Mesaj"].AsString);

            // 3) YalnizEkle + çakışan _id'ler: AtlaVeRaporla → 3'ü de atlanır, örnekler dolar.
            AktarimSonucu atla = await servis.AktarAsync(istek with
            {
                SuzgecJson = null,
                HataPolitikasi = AktarimHataPolitikasi.AtlaVeRaporla,
            }, null, CancellationToken.None);

            Assert.True(atla.Basarili, atla.Hata);
            Assert.Equal(3, atla.Okunan);
            Assert.Equal(0, atla.Yazilan);
            Assert.Equal(3, atla.Atlanan);
            Assert.NotEmpty(atla.HataOrnekleri);

            // 4) IlkHatadaDur: ilk çakışan _id'de açık hatayla durur.
            AktarimSonucu dur = await servis.AktarAsync(istek with { SuzgecJson = null }, null, CancellationToken.None);
            Assert.False(dur.Basarili);
            Assert.Contains("Belge 1", dur.Hata!);

            // 5) Alan eşlemesi (v12-S6): yeniden adlandır + alan düşür; belgede olmayan alan atlanır.
            AktarimSonucu esli = await servis.AktarAsync(istek with
            {
                SuzgecJson = null,
                HedefKoleksiyon = "LogEsli",
                Eslesmeler = [new("Mesaj", "Aciklama"), new("YokBoyleAlan", "Hayalet")],
            }, null, CancellationToken.None);

            Assert.True(esli.Basarili, esli.Hata);
            Assert.Equal(3, esli.Yazilan);
            IMongoCollection<BsonDocument> esliKoleksiyon =
                istemci.GetDatabase(HedefDb).GetCollection<BsonDocument>("LogEsli");
            BsonDocument ilk = await esliKoleksiyon.Find(new BsonDocument("Aciklama", "düzeltildi")).FirstAsync();
            Assert.False(ilk.Contains("Seviye"));   // listede yok → düştü
            Assert.False(ilk.Contains("Hayalet"));  // kaynak belgede olmayan alan sessizce atlandı
        }
        finally
        {
            await istemci.DropDatabaseAsync(KaynakDb);
            await istemci.DropDatabaseAsync(HedefDb);
        }
    }
}
