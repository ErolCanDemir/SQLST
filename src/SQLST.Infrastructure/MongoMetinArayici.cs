using SQLST.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SQLST.Infrastructure;

/// <summary>
/// MongoDB'de metin arama (B5/A6, 2026-07-19).
///
/// <b>KAPSAM KARARI — neyin "tanım" sayıldığı.</b> SQL ailesinde arama <i>nesne
/// tanımlarında</i> yapılır (view/SP/fonksiyon gövdeleri). MongoDB'de bunun gerçek
/// karşılığı ikidir ve ikisi de saklanan BELGE'dir:
/// <list type="bullet">
///   <item><b>View'lar</b> — <c>viewOn</c> + <c>pipeline</c>: bir view'ın tanımı budur.</item>
///   <item><b>Index'ler</b> — anahtar belgesi + seçenekleri: "bu alana hangi index'ler
///         dokunuyor?" sorusunun cevabı.</item>
/// </list>
///
/// <b>Koleksiyon ADLARI bilerek DIŞARIDA.</b> Ad araması tanım araması değildir ve
/// özelliğin anlamını motora göre kaydırırdı; üstelik nesne gezgininin tepesinde zaten
/// ad süzgeci var. Aynı gerekçeyle SQL tarafında da tablo adları aranmıyor.
///
/// <b>SÜZME İSTEMCİDE.</b> SQL motorlarında süzgeç sunucuya gider (<c>LIKE</c>); burada
/// üst veri (koleksiyon listesi + index listesi) çekilip istemcide süzülür. Kabul edilebilir,
/// çünkü bu üst veri küçüktür — belge VERİSİ değil, şema tanımıdır. Yine de tavan uygulanır.
/// </summary>
public static class MongoMetinArayici
{
    /// <summary>Bulunan bir tanım: kanonik dört alan (SQL tarafıyla aynı biçim).</summary>
    public sealed record Bulgu(string Sema, string Ad, SemaNesneTuru Tur, string Tanim);

    /// <summary>
    /// Seçili veritabanındaki view ve index tanımlarında arar.
    /// En çok <see cref="ILehce.AramaTavani"/> sonuç döner (B4/A5 ile aynı tavan).
    /// </summary>
    public static async Task<IReadOnlyList<Bulgu>> AraAsync(
        ConnectionProfile profil, ISecretProtector protector, string veritabani,
        string aranan, bool buyukKucukDuyarli, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(veritabani))
            throw new InvalidOperationException("MongoDB'de arama için veritabanı seçilmelidir.");

        StringComparison kiyas = buyukKucukDuyarli
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        IMongoClient istemci = MongoVeriKaynagi.IstemciAl(profil, protector);
        IMongoDatabase db = istemci.GetDatabase(veritabani);

        var bulgular = new List<Bulgu>();

        // ListCollections (ListCollectionNames DEĞİL): yalnız adı değil type/options da gelir,
        // view'ların viewOn+pipeline'ı orada durur.
        List<BsonDocument> koleksiyonlar =
            await (await db.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);

        foreach (BsonDocument koleksiyon in koleksiyonlar.OrderBy(k => Ad(k), StringComparer.OrdinalIgnoreCase))
        {
            if (bulgular.Count >= ILehce.AramaTavani)
                break;

            string ad = Ad(koleksiyon);

            if (ViewTanimi(koleksiyon) is { } viewTanimi
                && viewTanimi.Contains(aranan, kiyas))
            {
                bulgular.Add(new Bulgu(veritabani, ad, SemaNesneTuru.View, viewTanimi));
            }

            // View'ların kendi index'i yoktur; yalnız gerçek koleksiyonlarda index sorulur.
            if (TurAdi(koleksiyon) == "view")
                continue;

            foreach (Bulgu index in await IndexBulgulariAsync(db, veritabani, ad, aranan, kiyas, ct))
            {
                bulgular.Add(index);
                if (bulgular.Count >= ILehce.AramaTavani)
                    break;
            }
        }

        return bulgular;
    }

    private static async Task<IReadOnlyList<Bulgu>> IndexBulgulariAsync(
        IMongoDatabase db, string veritabani, string koleksiyon,
        string aranan, StringComparison kiyas, CancellationToken ct)
    {
        List<BsonDocument> indexler;
        try
        {
            indexler = await (await db.GetCollection<BsonDocument>(koleksiyon)
                .Indexes.ListAsync(ct)).ToListAsync(ct);
        }
        catch (MongoCommandException)
        {
            // Koleksiyon arada silinmiş ya da yetki yoksa: TÜM arama düşmesin, o koleksiyon
            // atlansın. (PG'de tek bir aggregate fonksiyonun tüm aramayı düşürmesi dersi.)
            return [];
        }

        var bulgular = new List<Bulgu>();
        foreach (BsonDocument index in indexler)
        {
            string tanim = index.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true });
            if (!tanim.Contains(aranan, kiyas))
                continue;

            string indexAdi = index.TryGetValue("name", out BsonValue ad) ? ad.AsString : "(adsız)";
            bulgular.Add(new Bulgu(veritabani, $"{koleksiyon}.{indexAdi}", SemaNesneTuru.Index, tanim));
        }
        return bulgular;
    }

    /// <summary>View tanımı: viewOn + pipeline, okunur JSON olarak. View değilse null.</summary>
    private static string? ViewTanimi(BsonDocument koleksiyon)
    {
        if (TurAdi(koleksiyon) != "view"
            || !koleksiyon.TryGetValue("options", out BsonValue secenekler)
            || secenekler is not BsonDocument secenekBelgesi)
        {
            return null;
        }

        return secenekBelgesi.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true });
    }

    private static string Ad(BsonDocument koleksiyon)
        => koleksiyon.TryGetValue("name", out BsonValue ad) ? ad.AsString : "";

    private static string TurAdi(BsonDocument koleksiyon)
        => koleksiyon.TryGetValue("type", out BsonValue tur) ? tur.AsString : "collection";
}
