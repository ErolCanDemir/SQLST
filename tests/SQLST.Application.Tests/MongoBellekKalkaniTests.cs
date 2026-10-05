using MongoDB.Bson;
using MongoDB.Driver;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v19-S15 (canlı test 2026-08-04 — KRİTİK "uygulama patladı"): Mongo okuma yolunda bellek
/// bütçesi. Dev belgeli koleksiyonda (stack trace'li ExceptionLog) yalnız SATIR sınırı vardı;
/// 100.000 dev belge gigabaytlara ulaşıp süreci OOM ile öldürüyordu — log bile yazılamıyordu.
/// Bu sınıf iki sınırın (satır + bellek) ve bayt tahmincisinin davranışını sabitler.
/// </summary>
public class MongoBellekKalkaniTests
{
    /// <summary>Sonsuz/uzun belge akışını partiler hâlinde veren sahte imleç (ağ yok).</summary>
    private sealed class SahteImlec(IEnumerable<BsonDocument[]> partiler) : IAsyncCursor<BsonDocument>
    {
        private readonly IEnumerator<BsonDocument[]> _parti = partiler.GetEnumerator();

        public IEnumerable<BsonDocument> Current { get; private set; } = [];

        public bool MoveNext(CancellationToken cancellationToken = default)
        {
            if (!_parti.MoveNext())
                return false;
            Current = _parti.Current;
            return true;
        }

        public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(MoveNext(cancellationToken));

        public void Dispose() { }
    }

    private static BsonDocument DevBelge(int metinBoyu)
        => new() { ["_id"] = ObjectId.GenerateNewId(), ["Mesaj"] = new string('x', metinBoyu) };

    private static IEnumerable<BsonDocument[]> SonsuzPartiler(int metinBoyu)
    {
        while (true)
            yield return [DevBelge(metinBoyu), DevBelge(metinBoyu)];
    }

    [Fact]
    public async Task Dev_belgelerde_bellek_butcesi_okumayi_keser_satir_sinirini_beklemez()
    {
        // Belge başına ~200KB; bütçe 1MB → satır sınırı (100.000) dolmadan ~5-6 belgede kesilmeli.
        (List<BsonDocument> belgeler, bool sinirAsildi, bool bellekAsildi, long toplamBayt, _) =
            await MongoExecutor.SinirlaOkuAsync(
                Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec(SonsuzPartiler(100_000))),
                istenen: null, satirSiniri: 100_000, bellekSiniriBayt: 1024 * 1024, CancellationToken.None);

        Assert.True(bellekAsildi);
        Assert.InRange(belgeler.Count, 1, 20);          // OOM'a koşmak yerine erken kesildi
        // v22-S1: bütçe artık NESNE GRAFİĞİNE göre dolar — ham bayt × BsonNesneKati bütçeyi aşınca
        // kesilir (BsonDocument grafiği ham BSON'un ~4 katı; eski kural gerçek belleği 4x az sayıyordu).
        Assert.True(toplamBayt * MongoExecutor.BsonNesneKati >= 1024 * 1024);
        Assert.False(sinirAsildi);                       // satır sınırı bayrağı ayrı (UI'da OR'lanır)
    }

    [Fact]
    public async Task Ilk_parti_esikte_bir_kez_verilir_okuma_surer()
    {
        // ⏳ v22-S1 (saha turu-2 m.9): 1.000 belgede UI'ya parti düşer, imleç turu devam eder.
        var partiler = new List<int>();
        BsonDocument[] parti = [.. Enumerable.Range(0, 1_200).Select(_ => DevBelge(10))];
        (List<BsonDocument> belgeler, _, _, _, _) = await MongoExecutor.SinirlaOkuAsync(
            Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec([parti])),
            istenen: null, satirSiniri: 100_000, bellekSiniriBayt: long.MaxValue, CancellationToken.None,
            ilkPartiAlici: set => partiler.Add(set.Satirlar.Count), ilkPartiSatir: 1_000,
            // Bellek nöbetçisi devre dışı: yüklü makinede nöbetçi (meşru biçimde) 256. belgede kesip
            // partiyi hiç ateşlemiyordu → test kırılgandı. Bu testin konusu parti, nöbetçi değil.
            bellekKesmeli: () => false);

        Assert.Single(partiler);             // TEK kez
        Assert.Equal(1_000, partiler[0]);    // eşikteki satır sayısıyla
        Assert.Equal(1_200, belgeler.Count); // parti verildikten SONRA okuma sürdü
    }

    [Fact]
    public async Task Ilk_parti_alicisi_firlatirsa_okuma_olmez()
    {
        BsonDocument[] parti = [.. Enumerable.Range(0, 30).Select(_ => DevBelge(10))];
        (List<BsonDocument> belgeler, _, _, _, _) = await MongoExecutor.SinirlaOkuAsync(
            Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec([parti])),
            istenen: null, satirSiniri: 100_000, bellekSiniriBayt: long.MaxValue, CancellationToken.None,
            ilkPartiAlici: _ => throw new InvalidOperationException("UI patladı"), ilkPartiSatir: 10,
            bellekKesmeli: () => false);

        Assert.Equal(30, belgeler.Count); // sorgu, UI geri çağrısının istisnasından etkilenmez
    }

    [Fact]
    public async Task Kucuk_belgelerde_satir_siniri_davranisi_degismedi()
    {
        BsonDocument[] parti = [.. Enumerable.Range(0, 10).Select(_ => DevBelge(10))];
        (List<BsonDocument> belgeler, bool sinirAsildi, bool bellekAsildi, _, _) =
            await MongoExecutor.SinirlaOkuAsync(
                Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec([parti])),
                istenen: null, satirSiniri: 5, bellekSiniriBayt: long.MaxValue, CancellationToken.None);

        Assert.Equal(5, belgeler.Count);
        Assert.True(sinirAsildi);
        Assert.False(bellekAsildi);
    }

    [Fact]
    public async Task Kullanici_limiti_sinir_bayragi_uretmez()
    {
        BsonDocument[] parti = [.. Enumerable.Range(0, 10).Select(_ => DevBelge(10))];
        (List<BsonDocument> belgeler, bool sinirAsildi, _, _, _) =
            await MongoExecutor.SinirlaOkuAsync(
                Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec([parti])),
                istenen: 3, satirSiniri: 100_000, bellekSiniriBayt: long.MaxValue, CancellationToken.None);

        Assert.Equal(3, belgeler.Count);
        Assert.False(sinirAsildi); // kullanıcının kendi limiti — "ilk N" bandı basılmaz
    }

    // ---- v22-S4 saha turu-4 m.4 ----
    // Kullanıcı: "yine selectte bellek hatası attı… SINIR VARDI ona rağmen bunu attı."
    // Ekran: "İlk 7.148 satır gösteriliyor — bellek sınırına ulaşıldı (geniş satırlar)."
    // 7.148 belge ≈ 24 MB ham veri: bütçe 96 MB olmasına rağmen Mongo yolunda ham bayt
    // BsonNesneKati (4) ile çarpıldığı için pratikte dörtte birinde kesiyordu. Sorguda AÇIK bir
    // limit varken bu TAHMİN, kullanıcının kendi kararını sessizce eziyor.

    [Fact]
    public async Task Acik_limit_varken_bayt_TAHMINI_kesmez()
    {
        // Belge başına ~200KB, bütçe 1MB → tahmin olsaydı ~5 belgede keserdi. Kullanıcı 40 istedi.
        (List<BsonDocument> belgeler, bool sinirAsildi, bool bellekAsildi, _, bool tavandan) =
            await MongoExecutor.SinirlaOkuAsync(
                Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec(SonsuzPartiler(100_000))),
                istenen: 40, satirSiniri: 100_000, bellekSiniriBayt: 1024 * 1024, CancellationToken.None,
                bellekKesmeli: () => false); // gerçek tavan bu testte devrede değil

        Assert.Equal(40, belgeler.Count); // ESKİDEN: ~5 belgede kesilirdi
        Assert.False(bellekAsildi);
        Assert.False(sinirAsildi);
        Assert.False(tavandan);
    }

    [Fact]
    public async Task Limit_YOKKEN_bayt_butcesi_AYNEN_korunur()
    {
        // Sınırsız okumada tahmin makul bir varsayılandır — koruma gevşetilmedi.
        (List<BsonDocument> belgeler, _, bool bellekAsildi, long toplamBayt, bool tavandan) =
            await MongoExecutor.SinirlaOkuAsync(
                Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec(SonsuzPartiler(100_000))),
                istenen: null, satirSiniri: 100_000, bellekSiniriBayt: 1024 * 1024, CancellationToken.None,
                bellekKesmeli: () => false);

        Assert.True(bellekAsildi);
        Assert.InRange(belgeler.Count, 1, 20);
        Assert.True(toplamBayt * MongoExecutor.BsonNesneKati >= 1024 * 1024);
        Assert.False(tavandan); // kesen bütçeydi, süreç tavanı değil
    }

    /// <summary>
    /// Açık limitte GERÇEK koruma sürecin ölçülen yığın tavanıdır — tahmin devre dışı kalınca
    /// ortada koruma kalmadığı sanılmasın. Kesme sebebi de ayırt edilir (mesaj farklı yazılır).
    /// </summary>
    [Fact]
    public async Task Acik_limitte_SUREC_TAVANI_yine_keser_ve_sebep_ayirt_edilir()
    {
        (List<BsonDocument> belgeler, _, bool bellekAsildi, _, bool tavandan) =
            await MongoExecutor.SinirlaOkuAsync(
                Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec(SonsuzPartiler(1_000))),
                istenen: 100_000, satirSiniri: 100_000, bellekSiniriBayt: long.MaxValue,
                CancellationToken.None,
                bellekKesmeli: () => true); // yığın tavanda

        Assert.True(bellekAsildi);
        Assert.True(tavandan);                          // sebep: ölçüm, tahmin değil
        Assert.InRange(belgeler.Count, 1, 200);         // 64'lük denetim aralığında kesildi
    }

    /// <summary>
    /// Tavan denetimi 256 → 64 belgede bir yapılır: tahmin devre dışıyken iki denetim arasında
    /// büyüyebilecek pay dörtte birine iner. Eski aralıkta bu test 256 belgeye kadar okurdu.
    /// </summary>
    [Fact]
    public async Task Tavan_denetimi_64_belgede_bir_yapilir()
    {
        (List<BsonDocument> belgeler, _, _, _, bool tavandan) = await MongoExecutor.SinirlaOkuAsync(
            Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec(SonsuzPartiler(10))),
            istenen: 100_000, satirSiniri: 100_000, bellekSiniriBayt: long.MaxValue,
            CancellationToken.None, bellekKesmeli: () => true);

        Assert.True(tavandan);
        Assert.InRange(belgeler.Count, 64, 65); // ilk 64'lük denetimde kesildi (eskiden 256)
    }

    /// <summary>
    /// v22-S4 turu-4 Find m.3 — SINIR SÖZLEŞMESİ: <c>satirSiniri = 0</c> "sınırsız" DEĞİL,
    /// "hiç satır" demektir. Find yardımcısının alan keşfi bu tuzağa düştü: 0 geçilince tavan 0
    /// oluyor, ilk belge eklenir eklenmez kırpılıyor ve sorgu hep 0 satırla dönüyordu — ekranda
    /// "⚠ alan okunamadı". Gerçek Mongo'da ölçüldü (0 → 0 satır; 50 → 4 alan). Biri bu davranışı
    /// "0'ı sınırsız yapayım" diye değiştirmek isterse bu test bilinçli karar istesin.
    /// </summary>
    [Fact]
    public async Task Satir_siniri_sifir_HIC_satir_demektir()
    {
        BsonDocument[] parti = [.. Enumerable.Range(0, 10).Select(_ => DevBelge(10))];
        (List<BsonDocument> belgeler, bool sinirAsildi, _, _, _) =
            await MongoExecutor.SinirlaOkuAsync(
                Task.FromResult<IAsyncCursor<BsonDocument>>(new SahteImlec([parti])),
                istenen: null, satirSiniri: 0, bellekSiniriBayt: long.MaxValue, CancellationToken.None,
                bellekKesmeli: () => false);

        Assert.Empty(belgeler);
        Assert.True(sinirAsildi);
    }

    [Fact]
    public void DegerBayt_metin_agirligini_gercekci_tahmin_eder()
    {
        var belge = new BsonDocument { ["Mesaj"] = new string('x', 1000) };
        Assert.InRange(MongoExecutor.DegerBayt(belge), 2000, 3000); // ~2 bayt/karakter + pay

        var icice = new BsonDocument
        {
            ["dis"] = new BsonDocument { ["ic"] = new string('y', 500) },
            ["dizi"] = new BsonArray { new string('z', 500), 42 },
        };
        Assert.True(MongoExecutor.DegerBayt(icice) >= 2000); // iç içe metinler de sayılır

        var ikili = new BsonDocument { ["veri"] = new BsonBinaryData(new byte[4096]) };
        Assert.True(MongoExecutor.DegerBayt(ikili) >= 4096);
    }
}
