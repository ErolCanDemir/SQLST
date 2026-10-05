using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Mongo→Mongo Paket Aktarım motoru (v12-S5; kullanıcı senaryosu 2026-07-26: "mongo loglarını
/// arşiv dbsine taşıma"). Kaynak koleksiyondan AKIŞLI cursor'la okur, hedefe belge belge yazar:
/// YalnizEkle → InsertOne (çakışan _id hata politikasına düşer) · EkleGuncelle → _id'ye göre
/// replace-upsert (MatchedCount>0 = güncellendi, değilse eklendi). SQL ailesindeki parti
/// TRANSACTION'ı burada yoktur (standalone Mongo desteklemez) — sayaçlar belge bazlı olduğu için
/// zaten dürüsttür; her <see cref="AktarimServisi.PartiBoyu"/> belgede ilerleme raporlanır.
/// "Önce temizle" deleteMany({})'dir ve UI açık onay ister. Sanal üyeler: UI testi sahteler.
/// </summary>
public class MongoAktarimServisi(ISecretProtector protector)
{
    /// <summary>v22-S16: 20 → 10.000 — ekran ilk 20'yi gösterir, tamamı hata raporu dosyasına
    /// + (ilk 200'ü) uygulama günlüğüne (AktarimServisi'ndeki gerekçenin aynısı).</summary>
    private const int EnCokHataOrnegi = 10_000;

    public virtual async Task<AktarimSonucu> AktarAsync(
        MongoAktarimIstegi istek, IProgress<AktarimIlerleme>? ilerleme, CancellationToken ct)
    {
        var sure = Stopwatch.StartNew();
        long okunan = 0, yazilan = 0, guncellenen = 0, atlanan = 0;
        var hataOrnekleri = new List<string>();

        try
        {
            IMongoCollection<BsonDocument> kaynak = MongoVeriKaynagi
                .IstemciAl(istek.Kaynak, protector)
                .GetDatabase(istek.KaynakVeritabani)
                .GetCollection<BsonDocument>(istek.KaynakKoleksiyon);
            IMongoCollection<BsonDocument> hedef = MongoVeriKaynagi
                .IstemciAl(istek.Hedef, protector)
                .GetDatabase(istek.HedefVeritabani)
                .GetCollection<BsonDocument>(istek.HedefKoleksiyon);

            BsonDocument suzgec = string.IsNullOrWhiteSpace(istek.SuzgecJson)
                ? []
                : BsonSerializer.Deserialize<BsonDocument>(istek.SuzgecJson);

            if (istek.OnceTemizle)
                await hedef.DeleteManyAsync(new BsonDocument(), ct);

            using IAsyncCursor<BsonDocument> imlec = await kaynak.FindAsync(
                suzgec, new FindOptions<BsonDocument> { BatchSize = AktarimServisi.PartiBoyu }, ct);
            while (await imlec.MoveNextAsync(ct))
            {
                foreach (BsonDocument belge in imlec.Current)
                {
                    ct.ThrowIfCancellationRequested();
                    okunan++;
                    BsonDocument yazilacak = istek.Eslesmeler is { Count: > 0 }
                        ? BelgeyiEsle(belge, istek.Eslesmeler)
                        : belge;
                    try
                    {
                        if (istek.YazmaKipi == AktarimYazmaKipi.EkleGuncelle)
                        {
                            // Filtre daima KAYNAK _id — eşlemede _id atlansa bile kimlik korunur
                            // (replacement'ta _id yoksa Mongo filtredekini/mevcudu kullanır).
                            ReplaceOneResult r = await hedef.ReplaceOneAsync(
                                new BsonDocument("_id", belge["_id"]), yazilacak,
                                new ReplaceOptions { IsUpsert = true }, ct);
                            if (r.MatchedCount > 0)
                                guncellenen++;
                            else
                                yazilan++;
                        }
                        else
                        {
                            await hedef.InsertOneAsync(yazilacak, cancellationToken: ct);
                            yazilan++;
                        }
                    }
                    catch (MongoException belgeHatasi)
                        when (istek.HataPolitikasi == AktarimHataPolitikasi.AtlaVeRaporla)
                    {
                        atlanan++;
                        if (hataOrnekleri.Count < EnCokHataOrnegi)
                            hataOrnekleri.Add($"Belge {okunan}: {belgeHatasi.Message}");
                    }
                    catch (MongoException belgeHatasi)
                    {
                        return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan,
                            $"Belge {okunan} yazılamadı: {belgeHatasi.Message}", sure.Elapsed, hataOrnekleri);
                    }

                    if (okunan % AktarimServisi.PartiBoyu == 0)
                        ilerleme?.Report(new AktarimIlerleme(okunan, yazilan, guncellenen, atlanan));
                }
            }

            ilerleme?.Report(new AktarimIlerleme(okunan, yazilan, guncellenen, atlanan));
            return new AktarimSonucu(true, okunan, yazilan, guncellenen, atlanan, null, sure.Elapsed, hataOrnekleri);
        }
        catch (OperationCanceledException)
        {
            // Transaction yok: o ana dek yazılan belgeler hedefte KALIR — sayaçlar zaten belge bazlı.
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan,
                "Aktarım kullanıcı tarafından iptal edildi.", sure.Elapsed, hataOrnekleri, IptalEdildi: true);
        }
        catch (FormatException ex)
        {
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan,
                $"Süzgeç JSON'u çözümlenemedi: {ex.Message}", sure.Elapsed, hataOrnekleri);
        }
        catch (MongoException ex)
        {
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan, ex.Message, sure.Elapsed, hataOrnekleri);
        }
        catch (TimeoutException ex)
        {
            // Sunucu seçimi zaman aşımı (erişilemeyen hedef) MongoException'dan türemez.
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan, ex.Message, sure.Elapsed, hataOrnekleri);
        }
    }

    /// <summary>
    /// Alan eşlemesi (v12-S6): belge yalnız listedeki ÜST DÜZEY alanlardan yeniden kurulur —
    /// yeniden adlandırma/atlama. Kaynak belgede olmayan alan o belgede atlanır (şemasızlık
    /// doğası: her belgede her alan olmayabilir).
    /// </summary>
    private static BsonDocument BelgeyiEsle(BsonDocument belge, IReadOnlyList<AktarimEslesmesi> eslesmeler)
    {
        var yeni = new BsonDocument();
        foreach (AktarimEslesmesi e in eslesmeler)
        {
            if (belge.TryGetValue(e.KaynakKolon, out BsonValue deger))
                yeni[e.HedefKolon] = deger;
        }

        return yeni;
    }
}
