using System.Collections.Concurrent;
using System.Diagnostics;
using SQLST.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace SQLST.Infrastructure;

/// <summary>
/// MongoDB ailesi (V3-S2/S3 — 08-v3r1 §5). SQL ailesinden AYRI iç uygulama: ne ADO.NET
/// ne ILehce; üst sözleşmeleri (ISqlExecutor/IDbOturum/ISchemaService) doğrudan uygular,
/// MotorYonlendirici* üzerinden seçilir. Sürücü: MongoDB.Driver (Apache-2.0).
///
/// Sorgu metni OPAK'tır (08-v3r1 §3) ve TEK JSON belgesidir:
///   { "find": "koleksiyon", "filter": {…}, "sort": {…}, "projection": {…}, "limit": n }
///   { "aggregate": "koleksiyon", "pipeline": [ {…}, … ] }
///   diğer her belge → veritabanı komutu olarak RunCommand (dbStats, count, insert…)
/// mongosh JS sözdizimi (db.x.find(...)) BİLİNÇLİ kapsam dışı (08-v3r1 §9 karar 3).
/// </summary>
public static class MongoVeriKaynagi
{
    /// <summary>
    /// Profil başına TEK MongoClient (08-v3r1 §5): havuz istemcinin içindedir, thread-safe'tir;
    /// sekme başına bağlantı nesnesi AÇILMAZ. Anahtar = bağlantı dizesi.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IMongoClient> Istemciler = new(StringComparer.Ordinal);

    internal static IMongoClient IstemciAl(ConnectionProfile profil, ISecretProtector protector)
    {
        // Bekleyen (inceleme 2026-07-30): sözlük anahtar başına sınırsız büyüyordu (her profil
        // düzenlemesi yeni dize → yeni kayıt). Tavan aşılınca temizlenir: sürücü 2.x'te ağır
        // kaynaklar (havuz/izleme) ClusterRegistry'de AYARLARA göre paylaşılır, MongoClient hafif
        // bir sarmalayıcıdır — yeniden kurmak aynı cluster'a bağlanır, bağlantı kaybettirmez.
        if (Istemciler.Count >= 16)
            Istemciler.Clear();
        return Istemciler.GetOrAdd(BaglantiDizesi(profil, protector), dize => new MongoClient(dize));
    }

    /// <summary>"host[:port]" → mongodb://host[:port]; kullanıcı tam mongodb:// URI de girebilir.</summary>
    internal static string BaglantiDizesi(ConnectionProfile profil, ISecretProtector protector)
    {
        string sunucu = profil.Sunucu.Trim();

        string kimlik = "";
        if (profil.Kimlik == KimlikTuru.Sql && !string.IsNullOrWhiteSpace(profil.KullaniciAdi))
        {
            string parola = profil.ParolaSifreli is null ? "" : protector.Coz(profil.ParolaSifreli);
            kimlik = $"{Uri.EscapeDataString(profil.KullaniciAdi)}:{Uri.EscapeDataString(parola)}@";
        }

        if (sunucu.StartsWith("mongodb", StringComparison.OrdinalIgnoreCase))
        {
            // SRV kimlik bekleyeni (inceleme 2026-07-30): tam URI (mongodb+srv:// dahil) girildiğinde
            // profildeki kullanıcı/parola SESSİZCE yok sayılıyordu — Atlas'ta "kimlik girdim ama
            // auth hatası" şaşırtıyordu. URI'de kimlik yoksa profildeki şemadan hemen sonra eklenir;
            // URI kendi kimliğini taşıyorsa URI kazanır.
            int semaSonu = sunucu.IndexOf("://", StringComparison.Ordinal);
            if (kimlik.Length > 0 && semaSonu > 0 && !sunucu[(semaSonu + 3)..].Contains('@'))
                return sunucu[..(semaSonu + 3)] + kimlik + sunucu[(semaSonu + 3)..];
            return sunucu;
        }

        // Çıplak IPv6 ("::1" ya da "[::1]:27017") URI içinde köşeli parantez ister (IPv6 bekleyeni).
        (string host, int? port) = HostPortAyristirici.Ayir(sunucu);
        if (host.Contains(':'))
            sunucu = $"[{host}]{(port is { } p ? $":{p}" : "")}";

        return $"mongodb://{kimlik}{sunucu}/?connectTimeoutMS={profil.BaglantiTimeoutSn * 1000}"
             + $"&serverSelectionTimeoutMS={profil.BaglantiTimeoutSn * 1000}&appName=SQLST";
    }
}

// Not: MongoDB ILehce uygulamaz (ayrı aile) — kendi yönetim paneli MongoTeshisServisi'ndedir.

/// <summary>Okuma sorgusunun hedef koleksiyonu seçili veritabanında yok (V3 kullanıcı bulgusu).</summary>
public sealed class KoleksiyonYokException(string mesaj) : Exception(mesaj);

/// <summary>Mongo yürütücüsü: JSON sorgu metnini çalıştırıp sonucu motor-nötr QueryResult'a çevirir.</summary>
public sealed class MongoExecutor : ISqlExecutor
{
    private readonly ISecretProtector _protector;

    public MongoExecutor(ISecretProtector protector) => _protector = protector;

    public async Task<QueryResult> ExecuteAsync(
        ConnectionProfile profil, string sql, ExecuteOptions opts, CancellationToken ct)
    {
        var sure = Stopwatch.StartNew();
        // Zaman aşımı SQL ailesiyle aynı sözleşme (inceleme 2026-07-30 bekleyeni): önce yüzey
        // override'ı, yoksa profil; 0 = sınırsız. Sunucu tarafında maxTime ile uygulanır —
        // ct yalnız istemciyi keser, koşan sorgu sunucuda sürerdi.
        int timeoutSn = opts.KomutTimeoutSnOverride ?? profil.KomutTimeoutSn;
        try
        {
            IMongoClient istemci = MongoVeriKaynagi.IstemciAl(profil, _protector);
            string dbAdi = string.IsNullOrWhiteSpace(opts.VeritabaniOverride) ? "admin" : opts.VeritabaniOverride!;
            IMongoDatabase db = istemci.GetDatabase(dbAdi);

            BsonDocument istek;
            try
            {
                istek = BsonDocument.Parse(sql);
            }
            catch (FormatException ex)
            {
                return new QueryResult
                {
                    Hata = new SqlHata($"Sorgu JSON olarak çözümlenemedi: {ex.Message} " +
                        "(Mongo sekmesinde tek bir JSON belgesi beklenir; örn. { \"find\": \"koleksiyon\", \"limit\": 10 })",
                        0, 1, 0),
                    Sure = sure.Elapsed,
                };
            }

            // 💾 Uyarlanır bütçe (v20-S16): SQL yolundaki (SonucOkuyucu) baskı kuralının aynısı.
            int satirSiniri = opts.SatirSiniri;
            long bellekSiniriBayt = opts.BellekSiniriBayt;
            bool baskida = BellekNobetcisi.BaskiAltindaMi()
                && (satirSiniri > 10_000 || bellekSiniriBayt > 24L * 1024 * 1024);
            if (baskida)
            {
                satirSiniri = Math.Min(satirSiniri, 10_000);
                bellekSiniriBayt = Math.Min(bellekSiniriBayt, 24L * 1024 * 1024);
            }

            (List<BsonDocument> belgeler, bool sinirAsildi, bool bellekAsildi, long toplamBayt,
                bool tavandanKesildi) =
                await CalistirAsync(db, istek, satirSiniri, bellekSiniriBayt, timeoutSn, ct,
                    null, 1_000); // v22-S3: asamali gosterim kaldirildi (kullanici istegi)

            int belgeSayisi = belgeler.Count;
            ResultSetData set = MongoSonucEsleyici.TabloyaCevir(belgeler);
            // 🧱 v22-S1: belge listesi ARTIK GEREKSİZ — satırlar kuruldu. BsonDocument grafiği ham
            // BSON'un 3-6 katı yer tutar; ikisini birlikte yaşatmak tepe belleği iki katlıyordu
            // (kullanıcının Mongo çökmesi: QueryLog find + sort). Hemen bırakılır.
            belgeler.Clear();
            return new QueryResult
            {
                Basarili = true,
                ResultSetler = [set],
                // v22-S4 m.4: kesme SEBEBİ artık ayırt edilir — "bütçe tahmini" ile "sürecin gerçek
                // yığın tavanı" aynı cümleyle anlatılırsa kullanıcı neyi daraltacağını bilemez.
                Mesajlar = Mesajlar(belgeSayisi, baskida, tavandanKesildi),
                Sure = sure.Elapsed,
                ToplamSatir = belgeSayisi,
                ToplamBayt = toplamBayt,
                // Sözleşme SQL yoluyla aynı (QueryResult.BellekSiniriAsildi belgesi): bellek
                // kesmesinde satır bayrağı DA true — UI tek "ilk N satır" bandını basar.
                SatirSiniriAsildi = sinirAsildi || bellekAsildi,
                BellekSiniriAsildi = bellekAsildi,
            };
        }
        catch (OperationCanceledException)
        {
            return new QueryResult
            {
                IptalEdildi = true,
                Mesajlar = ["Sorgu kullanıcı tarafından iptal edildi."],
                Sure = sure.Elapsed,
            };
        }
        catch (KoleksiyonYokException ex)
        {
            // Sessiz boş sonuç yerine AÇIK hata (kullanıcı bulgusu 2026-07-18)
            return new QueryResult { Hata = new SqlHata(ex.Message, 0, 1, 0), Sure = sure.Elapsed };
        }
        catch (MongoExecutionTimeoutException)
        {
            return new QueryResult
            {
                Hata = new SqlHata($"Sorgu {timeoutSn} sn zaman aşımını aştı ve sunucu tarafından " +
                    "durduruldu. Sorguyu daraltabilir (filtre/limit) ya da profil ayarından süreyi " +
                    "artırabilirsiniz (0 = sınırsız).", 0, 0, 0),
                Sure = sure.Elapsed,
            };
        }
        catch (MongoException ex)
        {
            return new QueryResult { Hata = new SqlHata(ex.Message, 0, 0, 0), Sure = sure.Elapsed };
        }
        catch (TimeoutException ex)
        {
            return new QueryResult { Hata = new SqlHata(ex.Message, 0, 0, 0), Sure = sure.Elapsed };
        }
    }

    /// <summary>
    /// Okuma sorgusunun hedefi var mı? Mongo, OLMAYAN koleksiyonda sessizce BOŞ sonuç döndürür;
    /// yanlış veritabanı seçiliyken bu "veri yok" gibi görünüyordu (kullanıcı bulgusu 2026-07-18).
    /// Bu yüzden find/aggregate öncesi koleksiyon varlığı doğrulanır ve yoksa AÇIK hata verilir.
    /// Yazma/DDL komutları (insert, create…) koleksiyonu oluşturabildiğinden denetlenmez.
    /// </summary>
    private static async Task<string?> KoleksiyonYokHatasiAsync(
        IMongoDatabase db, string koleksiyon, CancellationToken ct)
    {
        var süzgeç = new BsonDocument("name", koleksiyon);
        List<string> bulunan = await (await db.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = süzgeç }, ct)).ToListAsync(ct);
        if (bulunan.Count > 0)
            return null;

        List<string> mevcutlar = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        string oneri = mevcutlar.Count == 0
            ? "Bu veritabanında hiç koleksiyon yok."
            : $"Bu veritabanındaki koleksiyonlar: {string.Join(", ", mevcutlar.Take(15))}"
              + (mevcutlar.Count > 15 ? $" (+{mevcutlar.Count - 15})" : "");
        return $"'{koleksiyon}' koleksiyonu '{db.DatabaseNamespace.DatabaseName}' veritabanında yok. {oneri}";
    }

    /// <summary>Sonuç mesajları — kesme sebebi ayırt edilir (v22-S4 m.4).</summary>
    private static string[] Mesajlar(int belgeSayisi, bool baskida, bool tavandanKesildi)
    {
        List<string> mesajlar = [$"({belgeSayisi} belge)"];
        if (baskida)
        {
            mesajlar.Add("💾 Makine bellek baskısı altında — bu koşuda sınırlar 10.000 belge / 24 MB'a "
                + "düşürüldü (donma/çökme koruması).");
        }
        if (tavandanKesildi)
        {
            mesajlar.Add("🧱 Okuma UYGULAMA BELLEK TAVANINDA kesildi — bu bir tahmin değil, sürecin "
                + "ölçülen yığını. Eldeki satırlar geçerlidir; tamamı için sorguyu daraltın ya da "
                + "\"⬇ Tümünü dışa aktar\" kullanın (dışa aktarma sabit bellekle çalışır, sınırsızdır).");
        }
        return [.. mesajlar];
    }

    private static async Task<(List<BsonDocument> Belgeler, bool SinirAsildi, bool BellekAsildi,
        long ToplamBayt, bool TavandanKesildi)> CalistirAsync(
        IMongoDatabase db, BsonDocument istek, int satirSiniri, long bellekSiniriBayt, int timeoutSn,
        CancellationToken ct, Action<ResultSetData>? ilkPartiAlici = null, int ilkPartiSatir = 1_000)
    {
        TimeSpan? maxTime = timeoutSn > 0 ? TimeSpan.FromSeconds(timeoutSn) : null;

        // find / aggregate sürücü akışıyla (cursor sürücünün işi); diğer her belge RunCommand.
        if (istek.TryGetValue("find", out BsonValue? koleksiyonAdi) && koleksiyonAdi.IsString)
        {
            if (await KoleksiyonYokHatasiAsync(db, koleksiyonAdi.AsString, ct) is { } hata)
                throw new KoleksiyonYokException(hata);

            IMongoCollection<BsonDocument> koleksiyon = db.GetCollection<BsonDocument>(koleksiyonAdi.AsString);
            FilterDefinition<BsonDocument> filtre = istek.TryGetValue("filter", out BsonValue? f) && f.IsBsonDocument
                ? f.AsBsonDocument : FilterDefinition<BsonDocument>.Empty;

            IFindFluent<BsonDocument, BsonDocument> akis =
                koleksiyon.Find(filtre, maxTime is null ? null : new FindOptions { MaxTime = maxTime });
            if (istek.TryGetValue("sort", out BsonValue? s) && s.IsBsonDocument)
                akis = akis.Sort(s.AsBsonDocument);
            if (istek.TryGetValue("projection", out BsonValue? p) && p.IsBsonDocument)
                akis = akis.Project(p.AsBsonDocument);

            int? istenen = istek.TryGetValue("limit", out BsonValue? l) && l.IsNumeric ? l.ToInt32() : null;
            return await SinirlaOkuAsync(akis.Limit(EnKucuk(istenen, satirSiniri) + 1).ToCursorAsync(ct),
                istenen, satirSiniri, bellekSiniriBayt, ct, ilkPartiAlici, ilkPartiSatir);
        }

        if (istek.TryGetValue("aggregate", out BsonValue? aggKoleksiyon) && aggKoleksiyon.IsString
            && istek.TryGetValue("pipeline", out BsonValue? pipeline) && pipeline.IsBsonArray)
        {
            if (await KoleksiyonYokHatasiAsync(db, aggKoleksiyon.AsString, ct) is { } aggHata)
                throw new KoleksiyonYokException(aggHata);

            IMongoCollection<BsonDocument> koleksiyon = db.GetCollection<BsonDocument>(aggKoleksiyon.AsString);
            BsonDocument[] adimlar = [.. pipeline.AsBsonArray.Select(a => a.AsBsonDocument)];
            IAsyncCursor<BsonDocument> imlec = await koleksiyon.AggregateAsync<BsonDocument>(adimlar,
                maxTime is null ? null : new AggregateOptions { MaxTime = maxTime }, ct);
            return await SinirlaOkuAsync(Task.FromResult(imlec), istenen: null, satirSiniri, bellekSiniriBayt, ct,
                ilkPartiAlici, ilkPartiSatir);
        }

        // Genel komut yolu: tek belge sonucu tek satır olarak döner (dbStats, ping, insert…).
        // maxTimeMS sunucunun GENEL komut seçeneğidir (2.6+): kullanıcı kendisi yazmadıysa eklenir.
        if (maxTime is { } mt && !istek.Contains("maxTimeMS"))
        {
            istek = (BsonDocument)istek.DeepClone(); // çağıranın belgesi değişmesin
            istek.Add("maxTimeMS", (int)mt.TotalMilliseconds);
        }
        BsonDocument komutSonucu = await db.RunCommandAsync<BsonDocument>(istek, cancellationToken: ct);
        return ([komutSonucu], false, false, DegerBayt(komutSonucu), false);
    }

    private static int EnKucuk(int? istenen, int sinir)
        => istenen is { } i && i < sinir ? i : sinir;

    /// <summary>
    /// BsonDocument NESNE GRAFİĞİNİN ham BSON'a oranı (v22-S1 çökme düzeltmesi). Her alan bir
    /// BsonElement (nesne başlığı) + ad string'i + BsonValue nesnesi demektir; ölçümlerde ham
    /// veriye göre 3-4 kat yer tutar. Bayt bütçesi HAM veriyi saydığından bu kat olmadan bütçe
    /// gerçek belleği 3-4 kat AZ tahmin ediyordu (kullanıcı bulgusu: büyük koleksiyonda çökme).
    /// </summary>
    internal const int BsonNesneKati = 4;

    /// <summary>
    /// Kullanıcı sorguda AÇIK <c>limit</c> yazdığında geçerli bayt bütçesi (v22-S4, çökme denetimi
    /// düzeltmesi). Bütçeyi kapatmak yerine yükseltiriz: limit belge SAYISINI bağlar, BAYT'ı değil.
    /// </summary>
    internal const long LimitliOkumaBellekSiniriBayt = 256L * 1024 * 1024;

    /// <summary>
    /// Reader-side cap (motor-nötr desen — FOG-6): sınır+1 okunur, taşma bayrağa çevrilir.
    /// v19-S15 (canlı test 2026-08-04 "uygulama patladı" — KRİTİK): SQL yolundaki İKİ SINIR
    /// (satır + bellek bütçesi, SonucOkuyucu 2026-07-23 OOM kalkanı) artık burada da. Eskiden
    /// yalnız satır sayılıyordu: dev belgeli koleksiyonda (stack trace'li ExceptionLog gibi)
    /// 100.000 belge gigabaytlara ulaşıp süreci OOM ile öldürüyordu — log bile yazılamıyordu.
    /// ConfigureAwait(false) da SonucOkuyucu'daki UI-donma kuralının aynısıdır.
    /// </summary>
    /// <param name="bellekKesmeli">Bellek nöbetçisi — null ise gerçeği kullanır (makine kritik VEYA
    /// süreç tavanı). TESTLERDE dikiş: parti/bütçe davranışını ölçen testler <c>() =&gt; false</c>
    /// verip nöbetçinin araya girmesini engeller; yüklü bir makinede nöbetçi MEŞRU biçimde 256.
    /// belgede kesip testi kırılganlaştırıyordu (konusu nöbetçi olmayan testler yanlış kırmızı).</param>
    internal static async Task<(List<BsonDocument> Belgeler, bool SinirAsildi, bool BellekAsildi,
        long ToplamBayt, bool TavandanKesildi)> SinirlaOkuAsync(
        Task<IAsyncCursor<BsonDocument>> imlecGorevi, int? istenen, int satirSiniri, long bellekSiniriBayt,
        CancellationToken ct, Action<ResultSetData>? ilkPartiAlici = null, int ilkPartiSatir = 1_000,
        Func<bool>? bellekKesmeli = null)
    {
        bellekKesmeli ??= BellekNobetcisi.OkumaKesilmeli; // v22-S4: makine kritikligi tek basina kesmez
        int tavan = EnKucuk(istenen, satirSiniri);
        long toplamBayt = 0;
        bool bellekAsildi = false;
        bool tavandanKesildi = false;

        // 🧱 v22-S4 saha turu-4 m.4 (kullanıcı: "yine bellek hatası attı — SINIR VARDI ona rağmen";
        // ekranda: "İlk 7.148 satır gösteriliyor — bellek sınırına ulaşıldı"): sorguda AÇIK bir
        // limit varsa işin boyutunu kullanıcı ZATEN bağlamıştır; TAHMİNE dayalı bayt bütçesinin
        // onun kararını sessizce ezmesi doğru değil (7.148 ≈ 24 MB ham veri — bütçe 96 MB'lık
        // ama Mongo yolunda nesne katıyla çarpıldığı için pratikte dörtte biri kadar).
        // Limit verilmemişse bütçe AYNEN durur (sınırsız okumada tahmin makul bir varsayılandır).
        //
        // ⚠ DÜZELTME (çok ajanlı çökme denetimi, 20 Ağu): bütçeyi büsbütün KAPATMAK fazlaydı.
        // "limit" belge SAYISINI bağlar, BAYT'ı değil — `{"find":"X","limit":100000}` gibi bir
        // sorguda kullanıcı işi bağlamış sayılmaz. Kapatınca geriye tek koruma olarak 64 belgede
        // bir örneklenen süreç tavanı kalıyordu; dev belgelerde bu pencere fazla geniş.
        // Doğrusu: KAPATMA, YÜKSELT. Kullanıcı limit yazdıysa bütçe 96 MB yerine 256 MB olur —
        // 7.148 satırda kesilme şikâyeti çözülür ama kalkan yerinde durur.
        long etkinBellekSiniri = istenen is null
            ? bellekSiniriBayt
            : Math.Max(bellekSiniriBayt, LimitliOkumaBellekSiniriBayt);
        var belgeler = new List<BsonDocument>();
        Action<ResultSetData>? ilkParti = ilkPartiAlici;
        using IAsyncCursor<BsonDocument> imlec = await imlecGorevi.ConfigureAwait(false);
        while (!bellekAsildi && belgeler.Count <= tavan
            && await imlec.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (BsonDocument belge in imlec.Current)
            {
                belgeler.Add(belge);
                // 🧱 v22-S1: bütçe HAM BSON'u sayıyordu; bellekteki BsonDocument grafiği (her alan
                // bir BsonElement + ad string'i + BsonValue nesnesi) ham veriden ~3-4 kat büyüktür.
                // Kullanıcının Mongo çökmesinin (QueryLog find+sort) doğrudan nedeni buydu: 96 MB
                // "ham" bütçe gerçekte ~400 MB yığın + satır kopyası + DataTable demekti.
                toplamBayt += DegerBayt(belge);
                if (toplamBayt * BsonNesneKati >= etkinBellekSiniri)
                {
                    bellekAsildi = true; // okunan belge atılmaz — bütçe zaten "azami ham veri"dir
                    break;
                }
                // 💾 Fiziksel RAM nöbetçisi (v20-S15): bütçe tahmini yanılsa da makine belleği kritiğe
                // dayandıysa kes — SQL yolundaki (SonucOkuyucu) kalkanın aynısı.
                // 🧱 Süreç tavanı (v22-S1): makine rahat olsa bile SÜRECİN yığını tavandaysa kes.
                // v22-S4: sıklık 256 → 64. Limitli okumada tahmin devre dışı kaldığı için ağırlık
                // ARTIK bu ölçüme bindi; iki kontrol arasında büyüyebilecek pay dörtte bire iner.
                if ((belgeler.Count & 63) == 0 && bellekKesmeli())
                {
                    bellekAsildi = true;
                    tavandanKesildi = true;
                    break;
                }
                // ⏳ AŞAMALI GÖSTERİM (v22-S1): ilk N belge dolunca UI'ya parti verilir (SQL yolundaki
                // kalıbın aynısı) — kullanıcı beklemeden veriyi görür, imleç turu arka planda sürer.
                if (ilkParti is not null && belgeler.Count >= ilkPartiSatir)
                {
                    Action<ResultSetData> alici = ilkParti;
                    ilkParti = null;
                    try
                    {
                        alici(MongoSonucEsleyici.TabloyaCevir(belgeler));
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Mongo ilk parti alıcısı istisna fırlattı — okuma sürüyor");
                    }
                }
                if (belgeler.Count > tavan)
                    break;
            }
        }

        bool sinirAsildi = belgeler.Count > tavan && tavan == satirSiniri; // kullanıcı limiti değil, uygulama sınırı
        if (belgeler.Count > tavan)
            belgeler.RemoveAt(belgeler.Count - 1);
        return (belgeler, sinirAsildi, bellekAsildi, toplamBayt, tavandanKesildi);
    }

    /// <summary>
    /// BSON değerinin bellek maliyeti tahmini — SQL yolundaki SatirBayt'ın karşılığı (v19-S15).
    /// Metin/ikili baskındır; öteki skaler türler sabit maliyet sayılır (tahmin, muhasebe değil).
    /// </summary>
    internal static long DegerBayt(BsonValue deger) => deger.BsonType switch
    {
        BsonType.String => 8 + (2L * deger.AsString.Length),
        BsonType.Binary => 8 + deger.AsBsonBinaryData.Bytes.LongLength,
        BsonType.Document => 16 + deger.AsBsonDocument.Elements
            .Sum(e => (2L * e.Name.Length) + DegerBayt(e.Value)),
        BsonType.Array => 16 + deger.AsBsonArray.Sum(DegerBayt),
        BsonType.JavaScript or BsonType.JavaScriptWithScope or BsonType.Symbol
            => 8 + (2L * deger.ToString()!.Length),
        _ => 16,
    };

    public async Task<(bool Basarili, string? HataMesaji)> TestConnectionAsync(
        ConnectionProfile profil, CancellationToken ct)
    {
        try
        {
            IMongoClient istemci = MongoVeriKaynagi.IstemciAl(profil, _protector);
            await istemci.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1), cancellationToken: ct);
            return (true, null);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or ArgumentException or FormatException)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>
/// Mongo "oturumu": MongoClient zaten havuzlu/paylaşımlı olduğundan sekme başına yalnız
/// hafif bir sarmalayıcıdır (08-v3r1 §5) — kalıcı bağlantı/transaction durumu yoktur.
/// </summary>
public sealed class MongoOturumFabrikasi : IOturumFabrikasi
{
    private readonly MongoExecutor _executor;

    public MongoOturumFabrikasi(ISecretProtector protector) => _executor = new MongoExecutor(protector);

    public IDbOturum Olustur(ConnectionProfile profil) => new MongoOturum(profil, _executor);

    private sealed class MongoOturum(ConnectionProfile profil, MongoExecutor executor) : IDbOturum
    {
        public ConnectionProfile Profil => profil;

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
            => executor.ExecuteAsync(profil, sql, opts, ct);

        /// <summary>Toplu dışa aktarma MongoDB'de henüz yok (dinamik şema → CSV kolonları ayrı bir iş);
        /// UI'da düğme SQL ailesine gizli olduğundan bu çağrılmaz.</summary>
        public Task<long> AkisAsync(string sql, ExecuteOptions opts,
            Action<IReadOnlyList<string>> baslikYaz, Action<object?[]> satirYaz, CancellationToken ct)
            => throw new NotSupportedException("Toplu dışa aktarma MongoDB'de henüz desteklenmiyor.");

        /// <summary>Güvenli Yazma MSSQL'e özgü (UI kapılı); Mongo'da tek-belge yazımları zaten atomiktir.</summary>
        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => Task.FromResult(IslemDurumu.Yok);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // istemci paylaşımlı — kapatılmaz
    }
}

/// <summary>Mongo şema okuyucu: veritabanı → koleksiyon ağacı + $sample tabanlı alan envanteri.</summary>
public sealed class MongoSchemaService : ISchemaService
{
    /// <summary>Koleksiyon başına $sample belge sayısı. 100→50 (madde 1, 2026-07-30): tip çıkarımı için
    /// 50 belge yeterli, hem wire transferini hem $sample'ın yavaş yolunu azaltır (Compass 100 kullanır
    /// ama bu doğruluk gerekliliği değil, hız/doğruluk dengesi).</summary>
    private const int OrneklemBoyu = 50;

    private readonly ISecretProtector _protector;

    public MongoSchemaService(ISecretProtector protector) => _protector = protector;

    public async Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct)
    {
        IMongoClient istemci = MongoVeriKaynagi.IstemciAl(profil, _protector);
        List<string> adlar = await (await istemci.ListDatabaseNamesAsync(ct)).ToListAsync(ct);
        return [.. adlar
            .Select(a => new VeritabaniBilgisi(a, a is "admin" or "config" or "local"))
            .OrderBy(v => v.SistemMi).ThenBy(v => v.Ad, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Sürücü istisnalarını IOE'ye sarar (inceleme 2026-07-30): MongoException/Timeout,
    /// VM'nin dar catch'lerinden (IOE/OCE) kaçıp düğümü "yükleniyor…"da kilitliyor ve Durum'u sessiz
    /// bırakıyordu. SQL ailesinde SchemaService zaten IOE'ye sarıyor — Mongo da aynı sözleşmeye uyar.</summary>
    private static async Task<T> IoeSarAsync<T>(Func<Task<T>> is_)
    {
        try
        {
            return await is_();
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
        => IoeSarAsync(() => YukleCekirdekAsync(profil, veritabani, ct));

    private async Task<SemaOnbellegi> YukleCekirdekAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(veritabani))
            throw new InvalidOperationException("MongoDB'de şema okumak için veritabanı seçilmelidir.");

        IMongoClient istemci = MongoVeriKaynagi.IstemciAl(profil, _protector);
        IMongoDatabase db = istemci.GetDatabase(veritabani);
        List<string> koleksiyonlar = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);

        // Alan envanteri koleksiyon başına BİR $sample sorgusudur; SERİ yürütünce çok koleksiyonlu
        // veritabanında yavaştı (kullanıcı bulgusu 2026-07-29: "Log Analizi'nde koleksiyonlar çok geç
        // yükleniyor"). SINIRLI paralellikle (sürücü havuzunu dövmeden) toplam süre büyük ölçüde kısalır;
        // sonuç sırası KORUNUR (aşağıda koleksiyon adına göre sıralanır). AlanEnvanteriAsync saftır
        // (paylaşılan durum yok) → paralel güvenli.
        var envanter = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<SemaKolonu>>(
            StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            koleksiyonlar,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, // 8→16 (madde 1)
            async (ad, iptal) => envanter[ad] = await AlanEnvanteriAsync(db, ad, iptal));

        var nesneler = koleksiyonlar
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .Select(ad => new SemaNesnesi(
                Veritabani: veritabani!,
                Sema: veritabani!,
                Ad: ad,
                Tur: SemaNesneTuru.Koleksiyon,
                Kolonlar: envanter.TryGetValue(ad, out IReadOnlyList<SemaKolonu>? k) ? k : [],
                Parametreler: []))
            .ToList();

        return new SemaOnbellegi { Nesneler = nesneler, YuklenmeZamaniUtc = DateTime.UtcNow };
    }

    /// <summary>
    /// Madde 1 (2026-07-30): koleksiyon ADLARINI hızlıca (tek ListCollectionNames round-trip) döner —
    /// alan envanteri ($sample süpürmesi) YAPILMAZ, Kolonlar boş kalır. Ağaç, pahalı envanteri
    /// beklemeden koleksiyon adlarını anında gösterebilsin diye; envanter sonra <see cref="YukleAsync"/>
    /// ile arka planda doldurulur (çağıran önbelleği tam envanterle değiştirir).
    /// </summary>
    public Task<SemaOnbellegi> AdlariYukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
        => IoeSarAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(veritabani))
                throw new InvalidOperationException("MongoDB'de şema okumak için veritabanı seçilmelidir.");

            IMongoClient istemci = MongoVeriKaynagi.IstemciAl(profil, _protector);
            IMongoDatabase db = istemci.GetDatabase(veritabani);
            List<string> koleksiyonlar = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);

            var nesneler = koleksiyonlar
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .Select(ad => new SemaNesnesi(veritabani!, veritabani!, ad, SemaNesneTuru.Koleksiyon, [], []))
                .ToList();

            return new SemaOnbellegi { Nesneler = nesneler, YuklenmeZamaniUtc = DateTime.UtcNow };
        });

    /// <summary>
    /// $sample ile alan→tip envanteri (şemasızlıkta "şema"): alanın görüldüğü tipler ve
    /// bulunma oranı çıkarılır. NullOlabilir = alan bazı belgelerde YOK ya da null.
    /// </summary>
    private static async Task<IReadOnlyList<SemaKolonu>> AlanEnvanteriAsync(
        IMongoDatabase db, string koleksiyon, CancellationToken ct)
    {
        BsonDocument[] boru = [new BsonDocument("$sample", new BsonDocument("size", OrneklemBoyu))];
        List<BsonDocument> ornekler = await (await db.GetCollection<BsonDocument>(koleksiyon)
            .AggregateAsync<BsonDocument>(boru, cancellationToken: ct)).ToListAsync(ct);

        if (ornekler.Count == 0)
            return [];

        var alanlar = new Dictionary<string, (SortedSet<string> Tipler, int Sayi)>(StringComparer.Ordinal);
        var sira = new List<string>();
        foreach (BsonDocument belge in ornekler)
        {
            foreach (BsonElement e in belge)
            {
                if (!alanlar.TryGetValue(e.Name, out (SortedSet<string> Tipler, int Sayi) kayit))
                {
                    kayit = ([], 0);
                    sira.Add(e.Name);
                }
                if (!e.Value.IsBsonNull)
                    kayit.Tipler.Add(TipAdi(e.Value));
                alanlar[e.Name] = (kayit.Tipler, kayit.Sayi + (e.Value.IsBsonNull ? 0 : 1));
            }
        }

        return [.. sira.Select(ad =>
        {
            (SortedSet<string> tipler, int sayi) = alanlar[ad];
            string tip = tipler.Count switch { 0 => "null", 1 => tipler.First(), _ => string.Join("|", tipler) };
            return new SemaKolonu(
                Ad: ad,
                Tip: tip,
                NullOlabilir: sayi < ornekler.Count, // bazı belgelerde yok/null
                PkMi: ad == "_id");
        })];
    }

    private static string TipAdi(BsonValue v) => v.BsonType switch
    {
        BsonType.ObjectId => "objectId",
        BsonType.String => "string",
        BsonType.Int32 => "int",
        BsonType.Int64 => "long",
        BsonType.Double => "double",
        BsonType.Decimal128 => "decimal",
        BsonType.Boolean => "bool",
        BsonType.DateTime => "date",
        BsonType.Document => "document",
        BsonType.Array => "array",
        BsonType.Binary => "binary",
        _ => v.BsonType.ToString().ToLowerInvariant(),
    };

    /// <summary>Koleksiyonun SQL'deki gibi bir "tanımı" yoktur (şemasız) — tanım penceresi kapalı kalır.</summary>
    public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <summary>MongoDB'de yabancı anahtar yoktur (v6-S2) — boş liste. Görsel tasarımcı Mongo'da zaten kapalı.</summary>
    public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);

    public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(ConnectionProfile profil, SemaNesnesi tablo, CancellationToken ct)
        => throw new NotSupportedException("Edit modu (V2-S5) MongoDB'de desteklenmiyor — belge düzenleme ayrı bir V3 işidir.");
}

/// <summary>
/// BSON belge listesi → motor-nötr sonuç kümesi (08-v3r1 §6 MVP):
/// kolonlar üst-düzey anahtarların GÖRÜLME SIRALI birleşimi; skaler hücreler CLR değerine,
/// iç içe Document/Array hücreleri relaxed-JSON string'e çevrilir (grid'in mevcut uzun-hücre
/// görüntüleyicisi V2-S6 tam belgeyi gösterir). Eksik alan = DBNull.
/// </summary>
public static class MongoSonucEsleyici
{
    private static readonly JsonWriterSettings JsonAyar = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    public static ResultSetData TabloyaCevir(IReadOnlyList<BsonDocument> belgeler)
    {
        var kolonSira = new List<string>();
        var kolonTipleri = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (BsonDocument belge in belgeler)
        {
            foreach (BsonElement e in belge)
            {
                if (!kolonTipleri.ContainsKey(e.Name))
                {
                    kolonSira.Add(e.Name);
                    kolonTipleri[e.Name] = "";
                }
                if (kolonTipleri[e.Name].Length == 0 && !e.Value.IsBsonNull)
                    kolonTipleri[e.Name] = TipAdi(e.Value);
            }
        }

        var kolonlar = new KolonBilgisi[kolonSira.Count];
        for (int i = 0; i < kolonSira.Count; i++)
        {
            string ad = kolonSira[i];
            string tip = kolonTipleri[ad].Length == 0 ? "null" : kolonTipleri[ad];
            kolonlar[i] = new KolonBilgisi(ad, tip, ClrTip(tip));
        }

        var satirlar = new List<object?[]>(belgeler.Count);
        foreach (BsonDocument belge in belgeler)
        {
            var satir = new object[kolonSira.Count];
            for (int i = 0; i < kolonSira.Count; i++)
                satir[i] = belge.TryGetValue(kolonSira[i], out BsonValue? v) ? HucreDegeri(v) : DBNull.Value;
            satirlar.Add(satir);
        }

        return new ResultSetData { Kolonlar = kolonlar, Satirlar = satirlar };
    }

    /// <summary>Skalerler CLR'a; Document/Array relaxed-JSON string'e (iç içe yapı kaybolmaz).</summary>
    public static object HucreDegeri(BsonValue v) => v.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => DBNull.Value,
        BsonType.String => v.AsString,
        BsonType.Int32 => v.AsInt32,
        BsonType.Int64 => v.AsInt64,
        BsonType.Double => v.AsDouble,
        BsonType.Decimal128 => (object)Decimal128.ToDecimal(v.AsDecimal128),
        BsonType.Boolean => v.AsBoolean,
        BsonType.DateTime => v.ToUniversalTime(),
        BsonType.ObjectId => v.AsObjectId.ToString(),
        BsonType.Document or BsonType.Array => v.ToJson(JsonAyar),
        BsonType.Binary => $"binary({v.AsBsonBinaryData.Bytes.Length} bayt)",
        _ => v.ToString() ?? "",
    };

    private static string TipAdi(BsonValue v) => v.BsonType switch
    {
        BsonType.ObjectId => "objectId",
        BsonType.String => "string",
        BsonType.Int32 => "int",
        BsonType.Int64 => "long",
        BsonType.Double => "double",
        BsonType.Decimal128 => "decimal",
        BsonType.Boolean => "bool",
        BsonType.DateTime => "date",
        BsonType.Document => "document",
        BsonType.Array => "array",
        BsonType.Binary => "binary",
        _ => v.BsonType.ToString().ToLowerInvariant(),
    };

    private static Type? ClrTip(string tipAdi) => tipAdi switch
    {
        "string" or "objectId" => typeof(string),
        "int" => typeof(int),
        "long" => typeof(long),
        "double" => typeof(double),
        "decimal" => typeof(decimal),
        "bool" => typeof(bool),
        "date" => typeof(DateTime),
        _ => null, // document/array/karışık — Edit modu Mongo'da zaten kapalı
    };
}
