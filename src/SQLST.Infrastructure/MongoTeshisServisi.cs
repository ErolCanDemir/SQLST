using SQLST.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SQLST.Infrastructure;

/// <summary>
/// MongoDB Yönetim Paneli verileri (V3 — kullanıcı isteği 2026-07-18: "mongoda bağlandığım
/// dbnin yönetim panelini yapmam gerekiyor index vs.").
///
/// SQL Server paneli DMV'lere dayanır; Mongo'nun karşılıkları komutlardır:
///   dbStats      → veritabanı özeti (boyut, koleksiyon/index sayısı)
///   $collStats   → koleksiyon başına belge sayısı/boyut/index boyutu
///   $indexStats  → INDEX KULLANIMI (kaç kez kullanıldı, ne zamandan beri) — panelin özü
///   currentOp    → o an çalışan işlemler
/// Hiçbir şey otomatik çalıştırılmaz/değiştirilmez; panel salt okunurdur (R1.1 kural 3 ruhu).
/// </summary>
public sealed class MongoTeshisServisi
{
    private readonly ISecretProtector _protector;

    public MongoTeshisServisi(ISecretProtector protector) => _protector = protector;

    /// <summary>Sunucu sürümü + çalışma süresi (SQL Server uptime bandının Mongo karşılığı).</summary>
    public async Task<(string Surum, TimeSpan Uptime)> SunucuBilgisiAsync(
        ConnectionProfile profil, CancellationToken ct)
    {
        IMongoDatabase admin = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase("admin");
        BsonDocument bilgi = await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument("serverStatus", 1), cancellationToken: ct);
        string surum = bilgi.GetValue("version", "?").ToString() ?? "?";
        double saniye = bilgi.TryGetValue("uptime", out BsonValue? u) && u.IsNumeric ? u.ToDouble() : 0;
        return (surum, TimeSpan.FromSeconds(saniye));
    }

    /// <summary>Veritabanı özeti (dbStats) — tek satırlık sonuç kümesi.</summary>
    public async Task<ResultSetData> VeritabaniOzetiAsync(
        ConnectionProfile profil, string veritabani, CancellationToken ct)
    {
        IMongoDatabase db = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase(veritabani);
        BsonDocument s = await db.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["dbStats"] = 1, ["scale"] = 1 }, cancellationToken: ct);

        return Kume(
            ["Veritabanı", "Koleksiyon", "Belge", "Veri boyutu (MB)", "Depolama (MB)", "Index sayısı", "Index boyutu (MB)"],
            [[
                veritabani,
                Sayi(s, "collections"),
                Sayi(s, "objects"),
                Mb(s, "dataSize"),
                Mb(s, "storageSize"),
                Sayi(s, "indexes"),
                Mb(s, "indexSize"),
            ]]);
    }

    /// <summary>Koleksiyon başına boyut/belge/index özeti ($collStats), büyükten küçüğe.</summary>
    public async Task<ResultSetData> KoleksiyonlarAsync(
        ConnectionProfile profil, string veritabani, CancellationToken ct)
    {
        IMongoDatabase db = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase(veritabani);
        List<string> adlar = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);

        var satirlar = new List<object?[]>();
        foreach (string ad in adlar)
        {
            try
            {
                BsonDocument[] boru = [new BsonDocument("$collStats",
                    new BsonDocument { ["storageStats"] = new BsonDocument() })];
                List<BsonDocument> sonuc = await (await db.GetCollection<BsonDocument>(ad)
                    .AggregateAsync<BsonDocument>(boru, cancellationToken: ct)).ToListAsync(ct);
                BsonDocument st = sonuc.Count > 0 && sonuc[0].TryGetValue("storageStats", out BsonValue? v)
                    ? v.AsBsonDocument : [];

                satirlar.Add([
                    ad,
                    Sayi(st, "count"),
                    Mb(st, "size"),
                    Mb(st, "storageSize"),
                    Sayi(st, "nindexes"),
                    Mb(st, "totalIndexSize"),
                ]);
            }
            catch (MongoException ex)
            {
                satirlar.Add([ad, null, null, null, null, $"okunamadı: {ex.Message}"]);
            }
        }

        return Kume(
            ["Koleksiyon", "Belge", "Veri (MB)", "Depolama (MB)", "Index sayısı", "Index (MB)"],
            [.. satirlar.OrderByDescending(r => r[2] as double? ?? 0)]);
    }

    /// <summary>
    /// Koleksiyonun index'lerinde ÖNDE GELEN (prefix) alan adları (v22-S1, saha turu-2 m.2).
    /// Log analizi bunu strateji seçmek için sorar: zaman alanı bir index'in ilk alanı DEĞİLSE
    /// "son 24 saat" süzgeci sunucuda TÜM koleksiyonu tarar (ölçüm: 600k belgede 961 ms ↔ 11 ms;
    /// milyonlarda onlarca saniye) → çağıran zaman süzgeci olmayan "son N kayıt" yoluna düşer.
    /// Yalnız prefix alanlar döner: bileşik index'te ({a:1, b:1}) yalnız 'a' aralık taramasını
    /// tek başına destekler. Okunamazsa BOŞ döner (strateji güvenli tarafa, hızlı yola düşer).
    /// </summary>
    public async Task<IReadOnlyList<string>> IndexOnAlanlariAsync(
        ConnectionProfile profil, string veritabani, string koleksiyon, CancellationToken ct)
    {
        IMongoDatabase db = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase(veritabani);
        List<BsonDocument> indexler;
        try
        {
            indexler = await (await db.GetCollection<BsonDocument>(koleksiyon)
                .Indexes.ListAsync(ct)).ToListAsync(ct);
        }
        catch (MongoException)
        {
            return []; // yetki/ağ sorunu strateji seçimini bozmasın — çağıran HIZLI yola düşer
        }

        var alanlar = new List<string>();
        foreach (BsonDocument i in indexler)
        {
            if (i.TryGetValue("key", out BsonValue? k) && k.IsBsonDocument
                && k.AsBsonDocument.ElementCount > 0)
                alanlar.Add(k.AsBsonDocument.GetElement(0).Name);
        }
        return alanlar;
    }

    /// <summary>
    /// INDEX KULLANIMI ($indexStats): hangi index kaç kez kullanıldı, ne zamandan beri.
    /// SQL Server'daki "kullanılmayan index" panelinin Mongo karşılığı — ops = 0 olanlar adaydır.
    /// </summary>
    public async Task<ResultSetData> IndexKullanimiAsync(
        ConnectionProfile profil, string veritabani, CancellationToken ct)
    {
        IMongoDatabase db = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase(veritabani);
        List<string> adlar = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);

        var satirlar = new List<object?[]>();
        foreach (string ad in adlar)
        {
            try
            {
                BsonDocument[] boru = [new BsonDocument("$indexStats", new BsonDocument())];
                List<BsonDocument> istatistik = await (await db.GetCollection<BsonDocument>(ad)
                    .AggregateAsync<BsonDocument>(boru, cancellationToken: ct)).ToListAsync(ct);

                foreach (BsonDocument i in istatistik)
                {
                    long ops = i.TryGetValue("accesses", out BsonValue? a) && a.IsBsonDocument
                        && a.AsBsonDocument.TryGetValue("ops", out BsonValue? o) && o.IsNumeric ? o.ToInt64() : 0;
                    DateTime? since = i.TryGetValue("accesses", out BsonValue? a2) && a2.IsBsonDocument
                        && a2.AsBsonDocument.TryGetValue("since", out BsonValue? s) && s.IsValidDateTime
                        ? s.ToUniversalTime() : null;

                    satirlar.Add([
                        ad,
                        i.GetValue("name", "?").ToString(),
                        i.TryGetValue("key", out BsonValue? k) ? k.ToJson() : "",
                        ops,
                        ops == 0 ? "⚠ hiç kullanılmadı" : "",
                        since?.ToLocalTime(),
                    ]);
                }
            }
            catch (MongoException ex)
            {
                satirlar.Add([ad, "—", "", null, $"okunamadı: {ex.Message}", null]);
            }
        }

        return Kume(
            ["Koleksiyon", "Index", "Anahtar", "Kullanım (ops)", "Not", "Sayaç başlangıcı"],
            [.. satirlar.OrderBy(r => Convert.ToInt64(r[3] ?? 0L))]); // hiç kullanılmayanlar üstte
    }

    /// <summary>O an çalışan işlemler (currentOp) — SQL Server "bloklanan oturumlar" karşılığı.</summary>
    public async Task<ResultSetData> CalisanIslemlerAsync(ConnectionProfile profil, CancellationToken ct)
    {
        IMongoDatabase admin = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase("admin");
        BsonDocument sonuc = await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["currentOp"] = 1, ["active"] = true }, cancellationToken: ct);

        var satirlar = new List<object?[]>();
        if (sonuc.TryGetValue("inprog", out BsonValue? inprog) && inprog.IsBsonArray)
        {
            foreach (BsonValue islem in inprog.AsBsonArray)
            {
                BsonDocument i = islem.AsBsonDocument;
                satirlar.Add([
                    i.GetValue("opid", "").ToString(),
                    i.GetValue("op", "").ToString(),
                    i.GetValue("ns", "").ToString(),
                    i.TryGetValue("secs_running", out BsonValue? sn) && sn.IsNumeric ? sn.ToInt64() : 0L,
                    i.GetValue("desc", "").ToString(),
                    i.TryGetValue("waitingForLock", out BsonValue? w) && w.IsBoolean && w.AsBoolean ? "⚠ kilit bekliyor" : "",
                ]);
            }
        }

        return Kume(["Op ID", "İşlem", "Ad alanı", "Süre (sn)", "Açıklama", "Durum"], satirlar);
    }

    // ── v20-S21 m.26 fikir 5/10: system.profile — GEÇMİŞ yavaş sorgular ve index önerileri ──

    /// <summary>
    /// Profiler durumu: (açık mı, slowms eşiği). Kapalıysa panel bunu söyler ve açma komutunu
    /// gösterir — sessiz boş liste yerine nedeni yazan panel (kullanıcı neden veri yok anlasın).
    /// </summary>
    public async Task<(bool Acik, int SlowMs)> ProfilerDurumuAsync(
        ConnectionProfile profil, string veritabani, CancellationToken ct)
    {
        IMongoDatabase db = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase(veritabani);
        BsonDocument sonuc = await db.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["profile"] = -1 }, cancellationToken: ct);
        return (Sayi(sonuc, "was") > 0, (int)Sayi(sonuc, "slowms"));
    }

    /// <summary>
    /// system.profile'daki son okuma kayıtları (en yeniden eskiye). Koleksiyon yoksa/profiler hiç
    /// açılmamışsa boş liste — hata değildir.
    /// </summary>
    public async Task<IReadOnlyList<ProfilKaydi>> YavasSorgularAsync(
        ConnectionProfile profil, string veritabani, int limit, CancellationToken ct)
    {
        IMongoDatabase db = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase(veritabani);
        List<BsonDocument> kayitlar;
        try
        {
            kayitlar = await db.GetCollection<BsonDocument>("system.profile")
                .Find(new BsonDocument("op", new BsonDocument("$in", new BsonArray(["query", "getmore", "command"]))))
                .Sort(new BsonDocument("ts", -1))
                .Limit(limit)
                .ToListAsync(ct);
        }
        catch (MongoException)
        {
            return []; // koleksiyon yok (profiler hiç açılmamış) — panel "kapalı" mesajını gösterir
        }

        var sonuc = new List<ProfilKaydi>(kayitlar.Count);
        foreach (BsonDocument k in kayitlar)
        {
            string ns = k.GetValue("ns", "").ToString() ?? "";
            string koleksiyon = ns.Contains('.') ? ns[(ns.IndexOf('.') + 1)..] : ns;
            if (koleksiyon.StartsWith("system.", StringComparison.Ordinal))
                continue; // profiler'ın kendi okumaları öneri üretmemeli

            BsonDocument? komut = k.TryGetValue("command", out BsonValue? c) && c.IsBsonDocument
                ? c.AsBsonDocument : null;
            BsonDocument? filtre = Belge(komut, "filter") ?? Belge(k, "query");
            BsonDocument? sirala = Belge(komut, "sort");
            string plan = k.GetValue("planSummary", "").ToString() ?? "";

            sonuc.Add(new ProfilKaydi(
                koleksiyon,
                AlanAdlari(filtre),
                AlanAdlari(sirala),
                Sayi(k, "millis"),
                plan.Contains("COLLSCAN", StringComparison.OrdinalIgnoreCase)));
        }
        return sonuc;
    }

    /// <summary>Yavaş sorgu listesi (fikir 10) — panelde gösterilecek tablo.</summary>
    public async Task<ResultSetData> YavasSorguTablosuAsync(
        ConnectionProfile profil, string veritabani, int limit, CancellationToken ct)
    {
        IMongoDatabase db = MongoVeriKaynagi.IstemciAl(profil, _protector).GetDatabase(veritabani);
        List<BsonDocument> kayitlar;
        try
        {
            kayitlar = await db.GetCollection<BsonDocument>("system.profile")
                .Find(new BsonDocument())
                .Sort(new BsonDocument("millis", -1))
                .Limit(limit)
                .ToListAsync(ct);
        }
        catch (MongoException)
        {
            return Kume(["Koleksiyon", "İşlem", "Süre (ms)", "İncelenen", "Dönen", "Plan", "Zaman"], []);
        }

        var satirlar = new List<object?[]>();
        foreach (BsonDocument k in kayitlar)
        {
            string ns = k.GetValue("ns", "").ToString() ?? "";
            satirlar.Add([
                ns.Contains('.') ? ns[(ns.IndexOf('.') + 1)..] : ns,
                k.GetValue("op", "").ToString(),
                Sayi(k, "millis"),
                Sayi(k, "docsExamined"),
                Sayi(k, "nreturned"),
                k.GetValue("planSummary", "").ToString(),
                k.TryGetValue("ts", out BsonValue? ts) && ts.IsValidDateTime ? ts.ToUniversalTime().ToLocalTime() : null,
            ]);
        }
        return Kume(["Koleksiyon", "İşlem", "Süre (ms)", "İncelenen", "Dönen", "Plan", "Zaman"], satirlar);
    }

    private static BsonDocument? Belge(BsonDocument? kaynak, string alan)
        => kaynak is not null && kaynak.TryGetValue(alan, out BsonValue? v) && v.IsBsonDocument
            ? v.AsBsonDocument : null;

    /// <summary>Filtre/sort belgesindeki alan adları — $and/$or/$nor dizileri düzleştirilir,
    /// $-ile başlayan operatörler alan sayılmaz.</summary>
    private static IReadOnlyList<string> AlanAdlari(BsonDocument? belge)
    {
        if (belge is null)
            return [];
        var adlar = new List<string>();
        Topla(belge);
        return adlar;

        void Topla(BsonDocument d)
        {
            foreach (BsonElement e in d)
            {
                if (e.Name.StartsWith('$'))
                {
                    if (e.Value.IsBsonArray)
                    {
                        foreach (BsonValue alt in e.Value.AsBsonArray.Where(a => a.IsBsonDocument))
                            Topla(alt.AsBsonDocument);
                    }
                    continue;
                }
                if (!adlar.Contains(e.Name))
                    adlar.Add(e.Name);
            }
        }
    }

    private static ResultSetData Kume(string[] kolonlar, IReadOnlyList<object?[]> satirlar) => new()
    {
        Kolonlar = [.. kolonlar.Select(k => new KolonBilgisi(k, "", null))],
        Satirlar = satirlar,
    };

    private static long Sayi(BsonDocument d, string alan)
        => d.TryGetValue(alan, out BsonValue? v) && v.IsNumeric ? v.ToInt64() : 0;

    private static double Mb(BsonDocument d, string alan)
        => d.TryGetValue(alan, out BsonValue? v) && v.IsNumeric ? Math.Round(v.ToDouble() / 1024 / 1024, 2) : 0;
}
