using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Tablodan gruplanan tek log imzası: kaç kez + örnek mesaj + son görüldüğü an.</summary>
/// <param name="SonGorulme">Grubun EN YENİ kaydının zamanı (kullanıcı isteği 2026-07-29); zaman
/// kolonu yoksa ya da çözülemezse null (grid'de boş görünür).</param>
/// <param name="IlkGorulme">Grubun EN ESKİ kaydının zamanı (v20-S3 trend/"YENİ" rozeti); zaman yoksa null.</param>
/// <param name="Seviye">Gruptaki baskın log seviyesi (v20-S3); seviye kolonu yoksa null.</param>
/// <param name="Desen">Bu imzanın SQL LIKE kalıbı (v20-S3 gerçek-sayım/detay/trend anahtarı); <see cref="LogAnaliz.LikeDeseni"/>.</param>
/// <param name="GercekSayimMi">true ise <see cref="Sayi"/> örneklem değil TÜM tablodan gerçek sayımdır (v20-S3).</param>
/// <param name="EkDegerler">Kullanıcının seçtiği EK kolonların değerleri (v22-S12 "birden fazla kolonu
/// yan yana"): <see cref="OrnekMesaj"/> ile AYNI örnek satırdan, istenen kolon sırasıyla; ek kolon
/// seçilmediyse null. Gruplamaya girmez — yalnız görünüm/dışa aktarım içindir.</param>
public sealed record TabloLogGrubu(
    string Imza, int Sayi, string OrnekMesaj, DateTime? SonGorulme = null,
    DateTime? IlkGorulme = null, string? Seviye = null, string? Desen = null,
    bool GercekSayimMi = false, IReadOnlyList<string?>? EkDegerler = null);

/// <summary>
/// Veritabanındaki exception/log TABLOSU analizi (SAF — kullanıcı tasarımı 2026-07-23:
/// "tablo seçilecek, en çok tekrarlayanlar sırası ile gösterilecek"). Pencere tabloyu ve mesaj
/// kolonunu seçtirir, örneklem sorgusunu çalıştırır, satırları buraya verir; gruplama
/// <see cref="LogAnaliz.Imza"/> normalizasyonunu kullanır — "Tablo [A] yok" ile "Tablo [B] yok"
/// aynı gruba düşer (parametreli exception mesajları birleşir). UI/IO yok.
/// </summary>
public static class LogTabloAnaliz
{
    /// <summary>
    /// Motoruna göre "tek kolon, ilk N satır" örneklem sorgusu. Ad tırnaklanmaz — log tabloları
    /// tipik basit adlıdır; tırnaklamak PG/Oracle'da büyük-küçük harf tuzağı açar (bilinçli sınır).
    /// </summary>
    public static string OrneklemSorgusu(
        MotorTuru motor, string tablo, string kolon, int enFazla,
        string? zamanKolonu = null, string? seviyeKolonu = null,
        IReadOnlyList<string>? seviyeler = null, IReadOnlyList<string>? ekKolonlar = null)
    {
        // v20-S3: seviye süzgeci verilince WHERE'e katılır (yoksa "" → eski çıktı birebir korunur).
        string w = Nerede(SeviyeKosulu(motor, seviyeKolonu, seviyeler));
        return motor switch
        {
            MotorTuru.Mssql => $"SELECT TOP ({enFazla}) {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo}{w};",
            MotorTuru.Postgres or MotorTuru.MySql => $"SELECT {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo}{w} LIMIT {enFazla};",
            MotorTuru.Oracle => $"SELECT {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo}{w} FETCH FIRST {enFazla} ROWS ONLY",
            // Mongo: SQL değil — find JSON belgesi. Burada 'tablo' ÇIPLAK koleksiyon adıdır (TamAd değil).
            MotorTuru.Mongo => MongoOrneklem(tablo, kolon, enFazla, zamanKolonu, seviyeKolonu, seviyeler, ekKolonlar),
            _ => throw new NotSupportedException("Log analizi bu motorda desteklenmiyor."),
        };
    }

    /// <summary>SELECT kolon listesi: mesaj kolonu + (varsa) zaman + (v20-S3, varsa) seviye kolonu +
    /// (v22-S12, varsa) kullanıcının seçtiği EK kolonlar; mükerrer ad eklenmez (ek==zaman gibi durumlar).
    /// Ek kolonlar SQL ailesinde KIRPILMAZ — mesaj kolonu da bugüne dek kırpılmadan taşınıyor (bilinçli
    /// tutarlılık; yavaşlık sahadan gelirse önce ölçülür). Mongo'da ise projeksiyon sunucuda kırpar.</summary>
    private static string Secim(string kolon, string? zamanKolonu, string? seviyeKolonu = null,
        IReadOnlyList<string>? ekKolonlar = null)
    {
        var parcalar = new List<string> { kolon };
        if (!string.IsNullOrEmpty(zamanKolonu) && !parcalar.Contains(zamanKolonu))
            parcalar.Add(zamanKolonu);
        if (!string.IsNullOrEmpty(seviyeKolonu) && !parcalar.Contains(seviyeKolonu))
            parcalar.Add(seviyeKolonu);
        if (ekKolonlar is not null)
        {
            foreach (string ek in ekKolonlar)
            {
                if (!string.IsNullOrEmpty(ek) && !parcalar.Contains(ek))
                    parcalar.Add(ek);
            }
        }
        return string.Join(", ", parcalar);
    }

    /// <summary>
    /// SON 24 SAAT örneklem sorgusu (kullanıcı kararı 2026-07-25: "günlük log analizi — tüm tablo
    /// gezilmeyecek, son 24 saatin en çok tekrar edenleri"). Zaman kolonu SERVER saatine göre
    /// süzülür; her motorun kendi 24 saat ifadesi. <paramref name="zamanKolonu"/> boşsa çağrılmaz —
    /// çağıran, zaman kolonu bulamazsa <see cref="OrneklemSorgusu"/>'na düşer.
    /// </summary>
    public static string Orneklem24SaatSorgusu(
        MotorTuru motor, string tablo, string kolon, string zamanKolonu, int enFazla,
        string? seviyeKolonu = null, IReadOnlyList<string>? seviyeler = null,
        IReadOnlyList<string>? ekKolonlar = null)
    {
        string sa = Ve(SeviyeKosulu(motor, seviyeKolonu, seviyeler)); // v20-S3: seviye süzgeci (yoksa "")
        return motor switch
        {
            // ORDER BY … DESC (madde 2, 2026-07-30): son 24 saat KÜÇÜK bir küme → sıralama ucuz; zaman
            // kolonu indeksliyse seek + index sırası (en yeni örnekler). Aralık sorgusuna EKLENMEDİ (büyük
            // aralıkta indekssiz sort riski; orada TOP/LIMIT erken durur, timeout backstop).
            MotorTuru.Mssql =>
                $"SELECT TOP ({enFazla}) {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo} WHERE {zamanKolonu} >= DATEADD(HOUR, -24, SYSDATETIME()){sa} ORDER BY {zamanKolonu} DESC;",
            MotorTuru.Postgres =>
                $"SELECT {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo} WHERE {zamanKolonu} >= NOW() - INTERVAL '24 hours'{sa} ORDER BY {zamanKolonu} DESC LIMIT {enFazla};",
            MotorTuru.MySql =>
                $"SELECT {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo} WHERE {zamanKolonu} >= NOW() - INTERVAL 24 HOUR{sa} ORDER BY {zamanKolonu} DESC LIMIT {enFazla};",
            MotorTuru.Oracle =>
                $"SELECT {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo} WHERE {zamanKolonu} >= SYSTIMESTAMP - INTERVAL '24' HOUR{sa} ORDER BY {zamanKolonu} DESC FETCH FIRST {enFazla} ROWS ONLY",
            // Mongo: aggregate + $$NOW (SERVER saati) ile son 24 saat; zaman alanı BSON date olmalı.
            MotorTuru.Mongo => MongoOrneklem24Saat(tablo, kolon, zamanKolonu, enFazla, seviyeKolonu, seviyeler, ekKolonlar),
            _ => throw new NotSupportedException("Log analizi bu motorda desteklenmiyor."),
        };
    }

    /// <summary>
    /// 🐢→⚡ SON N KAYIT (v22-S1; saha turu-2 m.2 "Log analizi Mongo için çok geç cevap veriyor").
    /// ÖLÇÜM (yerel MongoDB 8.3, 600.000 belge, son 24 saatte ~2.000 kayıt — gerçek log deseni):
    /// zaman alanında index YOKKEN "son 24 saat" süzgeci <b>961 ms</b> (tüm koleksiyon taranır, çünkü
    /// <c>$limit 20.000</c> hiç dolmaz), index'liyken <b>11 ms</b>; buna karşılık zaman süzgeci
    /// OLMADAN son N kayıt (<c>sort: {$natural: -1}</c>) index'ten BAĞIMSIZ <b>86 ms</b>. Kullanıcının
    /// milyonlarca kayıtlık koleksiyonunda 961 ms onlarca saniyeye çıkıyordu — bu yüzden index yoksa
    /// çağıran bu sorguya düşer: zaman süzgeci yok, ekleme sırasının TERSİNDEN son N kayıt (log için
    /// en anlamlı dilim), sabit maliyet. Yalnız Mongo; SQL motorlarında zaman kolonu indeksliyse
    /// ORDER BY DESC yolu zaten kullanılır.
    /// </summary>
    public static string SonKayitlarSorgusu(
        MotorTuru motor, string tablo, string kolon, int enFazla,
        string? zamanKolonu = null, string? seviyeKolonu = null, IReadOnlyList<string>? seviyeler = null,
        IReadOnlyList<string>? ekKolonlar = null)
    {
        if (motor != MotorTuru.Mongo)
            throw new NotSupportedException("Son N kayıt sorgusu şimdilik yalnız MongoDB için üretilir.");

        // v22-S2: find + $natural DEĞİL, aggregate + $sort {_id:-1}. İki nedeni var:
        // (a) mesajı SUNUCUDA kırpmak için ifade projeksiyonu gerekir — $project her sürümde çalışır,
        //     find projeksiyonunda ifade yalnız MongoDB 4.4+'ta desteklenir (sunucu sürümüne bel bağlamayalım);
        // (b) _id index'i HER koleksiyonda vardır → "$sort {_id:-1} + $limit" index'ten ilk N'i alır,
        //     bloklayan sıralama yok (ölçüm: 200.000 belgede 222 ms, kırpmayla 191 ms / 7,3 MB).
        var pipeline = new List<object>();
        if (MongoEslesme(null, false, null, null, seviyeKolonu, seviyeler) is { } filtre) // yalnız seviye süzgeci
            pipeline.Add(new Dictionary<string, object> { ["$match"] = filtre });
        pipeline.Add(new Dictionary<string, object>
        {
            ["$sort"] = new Dictionary<string, object> { ["_id"] = -1 },
        });
        pipeline.Add(new Dictionary<string, object> { ["$limit"] = enFazla });
        pipeline.Add(new Dictionary<string, object>
        {
            ["$project"] = MongoProjection(kolon, zamanKolonu, seviyeKolonu, ekKolonlar),
        });
        return JsonSerializer.Serialize(
            new Dictionary<string, object> { ["aggregate"] = tablo, ["pipeline"] = pipeline });
    }

    /// <summary>
    /// TARİH ARALIĞI örneklem sorgusu (v11-öncesi #9 "Tüm Zamanlar Log Analizi", 2026-07-25):
    /// <paramref name="bas"/> dahil, <paramref name="bit"/> HARİÇ (yarı açık aralık — gün ekleme
    /// çağıranın işi; "25 Tem - 26 Tem" seçen kullanıcı için pencere bit'e +1 gün verir).
    /// SQL motorlarında ISO literal (sunucu-yerel saat varsayımı); Mongo'da <c>$date</c> UTC ISO —
    /// Mongo tarihleri UTC saklar, çağıran yerel→UTC çevirir. Zaman kolonu ZORUNLU: aralık,
    /// zaman kolonu olmayan tabloya uygulanamaz (çağıran bunu kullanıcıya söyler).
    /// </summary>
    public static string OrneklemAralikSorgusu(
        MotorTuru motor, string tablo, string kolon, string zamanKolonu,
        DateTime bas, DateTime bit, int enFazla, string? seviyeKolonu = null,
        IReadOnlyList<string>? seviyeler = null, IReadOnlyList<string>? ekKolonlar = null)
    {
        string b = bas.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        string s = bit.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        string sa = Ve(SeviyeKosulu(motor, seviyeKolonu, seviyeler)); // v20-S3: seviye süzgeci (yoksa "")
        return motor switch
        {
            MotorTuru.Mssql =>
                $"SELECT TOP ({enFazla}) {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo} WHERE {zamanKolonu} >= '{b}' AND {zamanKolonu} < '{s}'{sa};",
            MotorTuru.Postgres =>
                $"SELECT {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo} WHERE {zamanKolonu} >= '{b}'::timestamp AND {zamanKolonu} < '{s}'::timestamp{sa} LIMIT {enFazla};",
            MotorTuru.MySql =>
                $"SELECT {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo} WHERE {zamanKolonu} >= '{b.Replace("T", " ")}' AND {zamanKolonu} < '{s.Replace("T", " ")}'{sa} LIMIT {enFazla};",
            MotorTuru.Oracle =>
                $"SELECT {Secim(kolon, zamanKolonu, seviyeKolonu, ekKolonlar)} FROM {tablo} WHERE {zamanKolonu} >= TIMESTAMP '{b.Replace("T", " ")}' AND {zamanKolonu} < TIMESTAMP '{s.Replace("T", " ")}'{sa} FETCH FIRST {enFazla} ROWS ONLY",
            MotorTuru.Mongo => MongoOrneklemAralik(tablo, kolon, zamanKolonu, bas, bit, enFazla, seviyeKolonu, seviyeler, ekKolonlar),
            _ => throw new NotSupportedException("Log analizi bu motorda desteklenmiyor."),
        };
    }

    /// <summary>Mongo tarih-aralığı örneklemi: $match ile $gte(bas)/$lt(bit) — $date UTC ISO literal.</summary>
    private static string MongoOrneklemAralik(
        string koleksiyon, string alan, string zamanAlani, DateTime bas, DateTime bit, int enFazla,
        string? seviyeAlani = null, IReadOnlyList<string>? seviyeler = null,
        IReadOnlyList<string>? ekAlanlar = null)
    {
        var pipeline = new List<object>
        {
            new Dictionary<string, object> { ["$match"] = MongoEslesme(zamanAlani, false, bas, bit, seviyeAlani, seviyeler)! },
            new Dictionary<string, object> { ["$project"] = MongoProjection(alan, zamanAlani, seviyeAlani, ekAlanlar) },
            new Dictionary<string, object> { ["$limit"] = enFazla },
        };
        var doc = new Dictionary<string, object> { ["aggregate"] = koleksiyon, ["pipeline"] = pipeline };
        return JsonSerializer.Serialize(doc);
    }

    /// <summary>Yerel/UTC bir tarihi Mongo <c>$date</c> UTC ISO literal'ine çevirir (v20-S3'te de kullanılır).</summary>
    private static object MongoTarih(DateTime t) => new Dictionary<string, object>
    {
        ["$date"] = t.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Mesaj + (varsa) zaman + (v20-S3, varsa) seviye + (v22-S12, varsa) EK alanlar yansıtılır,
    /// _id elenir. Ek alanlar da mesaj gibi SUNUCUDA kırpılır (string ise ilk 300 karakter) — bunlar
    /// görünüm örnekleridir, tel üstünde KB'larca stack trace taşımanın anlamı yok (v22-S2 ölçümü).</summary>
    private static Dictionary<string, object> MongoProjection(string alan, string? zamanAlani,
        string? seviyeAlani = null, IReadOnlyList<string>? ekAlanlar = null)
    {
        // v22-S2 (saha turu-2 m.2, İKİNCİ tur): mesaj alanı SUNUCUDA kırpılır. Kullanıcı 0.23.0'te de
        // "5 dakika bekledim, cevap gelmedi" dedi; ölçüm darboğazın SUNUCU DEĞİL AĞ olduğunu gösterdi:
        // 20.000 belge × ~1 KB mesaj = 19,8 MB tel üstünde (kırpılmışta 7,3 MB; gerçek QueryLog
        // mesajları KB'larca olduğundan oran çok daha büyük). İmza gruplaması mesajın BAŞINI kullanır —
        // ilk 300 karakter fazlasıyla yeter. String olmayan alan olduğu gibi geçer ($cond koruması).
        var p = new Dictionary<string, object> { [alan] = MesajKirp(alan) };
        if (!string.IsNullOrEmpty(zamanAlani) && zamanAlani != alan)
            p[zamanAlani] = 1;
        if (!string.IsNullOrEmpty(seviyeAlani) && seviyeAlani != alan && seviyeAlani != zamanAlani)
            p[seviyeAlani] = 1;
        if (ekAlanlar is not null)
        {
            foreach (string ek in ekAlanlar)
            {
                if (!string.IsNullOrEmpty(ek) && !p.ContainsKey(ek))
                    p[ek] = MesajKirp(ek);
            }
        }
        p["_id"] = 0;
        return p;
    }

    /// <summary>Gruplama için taşınan azami mesaj uzunluğu (imza mesajın başından çıkar).</summary>
    public const int MongoMesajKirpma = 300;

    /// <summary>Mesaj alanını sunucuda kırpan projeksiyon ifadesi (string değilse dokunmaz).</summary>
    private static object MesajKirp(string alan) => new Dictionary<string, object>
    {
        ["$cond"] = new List<object>
        {
            new Dictionary<string, object>
            {
                ["$eq"] = new List<object>
                {
                    new Dictionary<string, object> { ["$type"] = "$" + alan },
                    "string",
                },
            },
            new Dictionary<string, object>
            {
                ["$substrCP"] = new List<object> { "$" + alan, 0, MongoMesajKirpma },
            },
            "$" + alan,
        },
    };

    /// <summary>Mongo örneklem (find): mesaj (+opsiyonel zaman/seviye/ek alanlar) yansıtılır, _id elenir. Koleksiyon adı ÇIPLAK olmalı.</summary>
    private static string MongoOrneklem(string koleksiyon, string alan, int enFazla,
        string? zamanAlani = null, string? seviyeAlani = null, IReadOnlyList<string>? seviyeler = null,
        IReadOnlyList<string>? ekAlanlar = null)
    {
        // v22-S2: find yerine aggregate — projeksiyon mesajı SUNUCUDA kırpıyor ve ifade projeksiyonu
        // find'da yalnız MongoDB 4.4+'ta destekli; $project her sürümde çalışır.
        var pipeline = new List<object>();
        if (MongoEslesme(null, false, null, null, seviyeAlani, seviyeler) is { } filtre) // v20-S3: yalnız seviye süzgeci
            pipeline.Add(new Dictionary<string, object> { ["$match"] = filtre });
        pipeline.Add(new Dictionary<string, object> { ["$limit"] = enFazla });
        pipeline.Add(new Dictionary<string, object>
        {
            ["$project"] = MongoProjection(alan, zamanAlani, seviyeAlani, ekAlanlar),
        });
        return JsonSerializer.Serialize(
            new Dictionary<string, object> { ["aggregate"] = koleksiyon, ["pipeline"] = pipeline });
    }

    /// <summary>
    /// Mongo son-24-saat örneklem: <c>$match</c> DÜZ tarih literaliyle süzer (v22-S4 m.4 — eski
    /// <c>$expr + $$NOW</c> biçimi her sunucu sürümünde index'e oturmuyordu; ExceptionLog'da 120 sn
    /// sunucu tavanını aşıyordu), tek alan yansıtılır, <c>_id</c> elenir, N ile sınırlanır.
    /// Zaman alanı BSON <c>date</c> değilse eşleşme olmaz (SQL'deki metin-tarih tuzağının Mongo karşılığı).
    /// </summary>
    private static string MongoOrneklem24Saat(string koleksiyon, string alan, string zamanAlani, int enFazla,
        string? seviyeAlani = null, IReadOnlyList<string>? seviyeler = null,
        IReadOnlyList<string>? ekAlanlar = null)
    {
        var pipeline = new List<object>
        {
            new Dictionary<string, object> { ["$match"] = MongoEslesme(zamanAlani, true, null, null, seviyeAlani, seviyeler)! },
            new Dictionary<string, object> { ["$project"] = MongoProjection(alan, zamanAlani, seviyeAlani, ekAlanlar) },
            new Dictionary<string, object> { ["$limit"] = enFazla },
        };
        var doc = new Dictionary<string, object> { ["aggregate"] = koleksiyon, ["pipeline"] = pipeline };
        return JsonSerializer.Serialize(doc);
    }

    /// <summary>Yalnız mesaj (zaman/seviye yoksa): imzaya göre gruplar; SonGorulme/IlkGorulme/Seviye null kalır.</summary>
    public static IReadOnlyList<TabloLogGrubu> Grupla(IEnumerable<object?> degerler, int enFazla = 50)
        => Grupla(degerler.Select(d => (d, (object?)null)), enFazla);

    /// <summary>(mesaj, zaman) çiftleri — seviye YOK aşırı yüklemesi (v20-S3 öncesi çağıranlar için).</summary>
    public static IReadOnlyList<TabloLogGrubu> Grupla(
        IEnumerable<(object? Mesaj, object? Zaman)> satirlar, int enFazla = 50)
        => Grupla(satirlar.Select(r => (r.Mesaj, r.Zaman, (object?)null)), enFazla);

    /// <summary>(mesaj, zaman, seviye) — EK DEĞER YOK aşırı yüklemesi (v22-S12 öncesi çağıranlar için).</summary>
    public static IReadOnlyList<TabloLogGrubu> Grupla(
        IEnumerable<(object? Mesaj, object? Zaman, object? Seviye)> satirlar, int enFazla = 50)
        => Grupla(satirlar.Select(r => (r.Mesaj, r.Zaman, r.Seviye, (object?[]?)null)), enFazla);

    /// <summary>
    /// (mesaj, zaman, seviye, ekler) satırlarını imzaya göre gruplar, en çok tekrarlayandan aza sıralar
    /// (boş/null mesaj atlanır). Her grup için: <see cref="TabloLogGrubu.SonGorulme"/> = imzanın EN YENİ
    /// zamanı, <see cref="TabloLogGrubu.IlkGorulme"/> = EN ESKİ zamanı (çözülemeyen/eksik zaman null
    /// sayılır), <see cref="TabloLogGrubu.Seviye"/> = gruptaki BASKIN (en sık) seviye,
    /// <see cref="TabloLogGrubu.Desen"/> = örnek mesajın SQL LIKE kalıbı (gerçek-sayım/detay/trend için),
    /// <see cref="TabloLogGrubu.EkDegerler"/> = örnek satırın (OrnekMesaj ile AYNI satır) ek kolon
    /// değerleri (v22-S12). Sayı burada ÖRNEKLEM sayısıdır (GercekSayimMi=false); gerçek sayımı
    /// <see cref="GercekSayimSorgusu"/> ile ayrı bir tarama verir.
    /// </summary>
    public static IReadOnlyList<TabloLogGrubu> Grupla(
        IEnumerable<(object? Mesaj, object? Zaman, object? Seviye, object?[]? Ekler)> satirlar, int enFazla = 50)
        => [.. satirlar
            .Select(r => (Mesaj: r.Mesaj?.ToString(), Zaman: ZamanCoz(r.Zaman), Seviye: r.Seviye?.ToString(), r.Ekler))
            .Where(r => !string.IsNullOrWhiteSpace(r.Mesaj))
            .GroupBy(r => LogAnaliz.Imza(r.Mesaj!))
            .Select(g => new TabloLogGrubu(
                g.Key, g.Count(), g.First().Mesaj!,
                SonGorulme: g.Max(x => x.Zaman),
                IlkGorulme: g.Min(x => x.Zaman),
                Seviye: BaskinSeviye(g.Select(x => x.Seviye)),
                Desen: LogAnaliz.LikeDeseni(g.First().Mesaj!),
                EkDegerler: EkDegerleriCoz(g.First().Ekler)))
            .OrderByDescending(g => g.Sayi)
            .ThenBy(g => g.Imza, StringComparer.Ordinal)
            .Take(enFazla)];

    /// <summary>Örnek satırın ek kolon hücrelerini metne çözer (null/DBNull → null); ek yoksa null.</summary>
    private static IReadOnlyList<string?>? EkDegerleriCoz(object?[]? ekler)
        => ekler is null ? null : [.. ekler.Select(e => e is null or DBNull ? null : e.ToString())];

    /// <summary>Gruptaki baskın (en sık) seviye; hepsi boşsa null. Harf-duyarsız gruplanır.</summary>
    private static string? BaskinSeviye(IEnumerable<string?> seviyeler)
        => seviyeler.Where(s => !string.IsNullOrWhiteSpace(s))
            .GroupBy(s => s!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    /// <summary>Sonuç hücresini (DateTime/DateTimeOffset/metin) DateTime'a çözer; çözülemezse null.</summary>
    private static DateTime? ZamanCoz(object? deger) => deger switch
    {
        null or DBNull => null,
        DateTime dt => dt,
        DateTimeOffset dto => dto.LocalDateTime,
        _ => DateTime.TryParse(deger.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime p) ? p : null,
    };

    /// <summary>
    /// Mesaj kolonu ön seçimi. Öncelik İPUCU sırasıyladır, kolon sırasıyla DEĞİL (inceleme
    /// bulgusu 2026-07-23: kolon-sıralı arama "LogId"yi "Message"ın önüne geçiriyordu) ve ipucu
    /// eşleşmesi yalnız METİN tipli kolonlarda geçerlidir (LogId int gibi kimlikler elenir).
    /// Hiçbiri tutmazsa ilk metin kolonu; o da yoksa null (kullanıcı elle seçer).
    /// </summary>
    public static string? MesajKolonuTahmini(IEnumerable<SemaKolonu> kolonlar)
    {
        // "log" en sonda — çok genel: LogDate/LogLevel gibi kolonları ancak son çare yakalasın.
        string[] ipuclari = ["mesaj", "message", "exception", "error", "hata", "aciklama", "log"];
        SemaKolonu[] metinler = [.. kolonlar.Where(MetinTipiMi)];

        foreach (string ipucu in ipuclari)
        {
            if (metinler.FirstOrDefault(k => k.Ad.Contains(ipucu, StringComparison.OrdinalIgnoreCase)) is { } k)
                return k.Ad;
        }
        return metinler.FirstOrDefault()?.Ad;
    }

    private static bool MetinTipiMi(SemaKolonu k)
        => k.Tip.Contains("char", StringComparison.OrdinalIgnoreCase)
        || k.Tip.Contains("text", StringComparison.OrdinalIgnoreCase)
        || k.Tip.Contains("clob", StringComparison.OrdinalIgnoreCase)
        || k.Tip.Contains("string", StringComparison.OrdinalIgnoreCase); // MongoDB alan tipi "string"

    /// <summary>
    /// Zaman kolonu ön seçimi (son 24 saat süzgeci için). Mesaj tahminiyle aynı desen: önce İPUCU
    /// sırasına göre, yalnız TARİH/ZAMAN tipli kolonlarda; hiçbiri tutmazsa ilk tarih/zaman kolonu;
    /// o da yoksa null (çağıran 24 saat süzgecini uygulayamaz, tüm tablodan örnekler).
    /// </summary>
    public static string? ZamanKolonuTahmini(IEnumerable<SemaKolonu> kolonlar)
    {
        string[] ipuclari =
            ["zaman", "tarih", "created", "olustur", "kayit", "insert", "date", "time", "log"];
        SemaKolonu[] zamanlar = [.. kolonlar.Where(ZamanTipiMi)];

        foreach (string ipucu in ipuclari)
        {
            if (zamanlar.FirstOrDefault(k => k.Ad.Contains(ipucu, StringComparison.OrdinalIgnoreCase)) is { } k)
                return k.Ad;
        }
        return zamanlar.FirstOrDefault()?.Ad;
    }

    // Tarih/zaman tipleri: datetime(2), smalldatetime, date, time, timestamp(tz), datetimeoffset.
    // NOT: MSSQL'de "timestamp" aslında rowversion'dır (ikili) — nadir ve tipik adı Version/RowVersion
    // olduğundan ipucu sırası gerçek zaman kolonunu öne alır; bu kabul edilen dar bir sınırdır.
    private static bool ZamanTipiMi(SemaKolonu k)
        => k.Tip.Contains("date", StringComparison.OrdinalIgnoreCase)
        || k.Tip.Contains("time", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // v20-S3 "Log Analizi 2.0": seviye tahmini/kovaları · GERÇEK sayım (tek tarama, N desen) · DETAY
    // (ham satırlar). Ortak anahtar LogAnaliz.LikeDeseni (imza→LIKE kalıbı). Trend (sparkline) ayrı
    // sorgu istemez: pencere DETAY satırlarının zaman damgalarından istemcide hesaplar.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Log Analizi'nde sunulan seviye kovaları (süzgeç çipi sırası).</summary>
    public static IReadOnlyList<string> SeviyeKovalari { get; } = ["Hata", "Uyarı", "Bilgi"];

    /// <summary>
    /// Seviye kolonu ön seçimi (v20-S3): ERROR/WARN/FATAL süzgeci ve rozet için. Mesaj/zaman tahminiyle
    /// aynı desen — önce İPUCU sırasına göre; seviye kolonu metin (Error/Warning) ya da sayı (Serilog
    /// 0..5) olabildiğinden tip SÜZÜLMEZ, yalnız ada bakılır. Hiçbiri tutmazsa null (süzgeç gizlenir).
    /// </summary>
    public static string? SeviyeKolonuTahmini(IEnumerable<SemaKolonu> kolonlar)
    {
        string[] ipuclari = ["level", "seviye", "severity", "önem", "onem", "loglevel", "priority"];
        SemaKolonu[] hepsi = [.. kolonlar];
        foreach (string ipucu in ipuclari)
            if (hepsi.FirstOrDefault(k => k.Ad.Contains(ipucu, StringComparison.OrdinalIgnoreCase)) is { } k)
                return k.Ad;
        return null;
    }

    /// <summary>
    /// Seviye kovası → o kovaya sayılan HAM seviye değerleri (v20-S3). Serilog'un hem tam adlarını
    /// (Verbose/Debug/Information/Warning/Error/Fatal) hem 3-harf kısaltmalarını (VRB/DBG/INF/WRN/ERR/FTL)
    /// kapsar. NOT: sayısal seviye kolonları (Serilog 0..5) BU eşleşmenin dışındadır — bilinen dar sınır.
    /// </summary>
    public static IReadOnlyList<string> SeviyeDegerleri(string kova) => kova switch
    {
        "Hata" => ["Error", "ERR", "Fatal", "FTL", "Err", "Critical", "CRT"],
        "Uyarı" => ["Warning", "WRN", "WARN", "Warn"],
        "Bilgi" => ["Information", "INF", "Info", "Debug", "DBG", "Verbose", "VRB", "Trace", "TRC"],
        _ => [],
    };

    /// <summary>Seçilen kova adlarını tek bir HAM seviye değer listesine açar (tekrarsız).</summary>
    public static IReadOnlyList<string> SeviyeDegerleriCoklu(IEnumerable<string> kovalar)
        => [.. kovalar.SelectMany(SeviyeDegerleri).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// GERÇEK sayım sorgusu (v20-S3, kullanıcı: "342× yanıltıcı"): örneklemde bulunan imzaların
    /// <paramref name="desenler"/> LIKE kalıplarını TEK taramada sayar —
    /// <c>SUM(CASE WHEN msg LIKE kalıp THEN 1 ELSE 0 END) AS c{i}</c>. Sonuç tek satır, c0..c{n-1}
    /// kolonları (çağıran indeksle okur). Kapsam: <paramref name="son24Saat"/> ya da
    /// (<paramref name="bas"/>,<paramref name="bit"/>) aralığı ya da hiçbiri (tüm tablo); seviye
    /// süzgeci de WHERE'e katılır. Mongo: aggregate <c>$group</c> + <c>$regexMatch</c>.
    /// </summary>
    public static string GercekSayimSorgusu(
        MotorTuru motor, string tablo, string mesajKolon, IReadOnlyList<string> desenler,
        string? zamanKolon = null, bool son24Saat = false, DateTime? bas = null, DateTime? bit = null,
        string? seviyeKolon = null, IReadOnlyList<string>? seviyeler = null)
    {
        if (desenler.Count == 0)
            throw new ArgumentException("En az bir desen gerekir.", nameof(desenler));
        if (motor == MotorTuru.Mongo)
            return MongoGercekSayim(tablo, mesajKolon, desenler, zamanKolon, son24Saat, bas, bit, seviyeKolon, seviyeler);

        string liste = string.Join(", ",
            desenler.Select((d, i) => $"SUM(CASE WHEN {LikeKosulu(motor, mesajKolon, d)} THEN 1 ELSE 0 END) AS c{i}"));
        string sql = $"SELECT {liste} FROM {tablo}"
            + Nerede(ZamanKosulu(motor, zamanKolon, son24Saat, bas, bit), SeviyeKosulu(motor, seviyeKolon, seviyeler));
        return Bitir(motor, sql);
    }

    /// <summary>
    /// DETAY sorgusu (v20-S3, kullanıcı: "detaya in — ham kayıtlar"): tek bir imzanın LIKE kalıbına uyan
    /// GERÇEK satırları (tüm kolonlar) en yeni önce döndürür (zaman kolonu varsa). Kapsam + seviye
    /// süzgeci gerçek-sayımla aynıdır (aynı küme). Pencere bu satırların zaman damgalarından sparkline'ı
    /// da hesaplar. Mongo: find + <c>$regex</c> + sort/limit.
    /// </summary>
    public static string DetaySorgusu(
        MotorTuru motor, string tablo, string mesajKolon, string desen, int enFazla,
        string? zamanKolon = null, bool son24Saat = false, DateTime? bas = null, DateTime? bit = null,
        string? seviyeKolon = null, IReadOnlyList<string>? seviyeler = null)
    {
        if (motor == MotorTuru.Mongo)
            return MongoDetay(tablo, mesajKolon, desen, enFazla, zamanKolon, son24Saat, bas, bit, seviyeKolon, seviyeler);

        string nere = Nerede(
            LikeKosulu(motor, mesajKolon, desen),
            ZamanKosulu(motor, zamanKolon, son24Saat, bas, bit),
            SeviyeKosulu(motor, seviyeKolon, seviyeler));
        string basSelect = motor == MotorTuru.Mssql ? $"SELECT TOP ({enFazla}) *" : "SELECT *";
        string sira = string.IsNullOrEmpty(zamanKolon) ? "" : $" ORDER BY {zamanKolon} DESC";
        string limit = motor switch
        {
            MotorTuru.Postgres or MotorTuru.MySql => $" LIMIT {enFazla}",
            MotorTuru.Oracle => $" FETCH FIRST {enFazla} ROWS ONLY",
            _ => "",
        };
        return Bitir(motor, $"{basSelect} FROM {tablo}{nere}{sira}{limit}");
    }

    // ── Ortak SQL parçaları ──────────────────────────────────────────────────────────────────

    /// <summary>String literal kaçışı: tek tırnak ikizlenir; MySQL string-literal ters bölüyü de
    /// işlediğinden (dosya yolları!) orada ters bölü de ikizlenir.</summary>
    private static string StrLit(MotorTuru motor, string s)
    {
        s = s.Replace("'", "''");
        return motor == MotorTuru.MySql ? s.Replace("\\", "\\\\") : s;
    }

    /// <summary><c>{kolon} LIKE '{desen}' ESCAPE '!'</c> — <see cref="LogAnaliz.LikeDeseni"/> kaçış karakteri '!'.</summary>
    private static string LikeKosulu(MotorTuru motor, string kolon, string desen)
        => $"{kolon} LIKE '{StrLit(motor, desen)}' ESCAPE '!'";

    /// <summary>Zaman süzgeci ifadesi: son 24 saat ya da [bas,bit) aralığı; zaman kolonu yoksa/verilmezse "".</summary>
    private static string ZamanKosulu(MotorTuru motor, string? zamanKolon, bool son24Saat, DateTime? bas, DateTime? bit)
    {
        if (string.IsNullOrEmpty(zamanKolon))
            return "";
        if (son24Saat)
            return motor switch
            {
                MotorTuru.Mssql => $"{zamanKolon} >= DATEADD(HOUR, -24, SYSDATETIME())",
                MotorTuru.Postgres => $"{zamanKolon} >= NOW() - INTERVAL '24 hours'",
                MotorTuru.MySql => $"{zamanKolon} >= NOW() - INTERVAL 24 HOUR",
                MotorTuru.Oracle => $"{zamanKolon} >= SYSTIMESTAMP - INTERVAL '24' HOUR",
                _ => "",
            };
        if (bas is { } b0 && bit is { } s0)
        {
            string b = b0.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            string s = s0.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            return motor switch
            {
                MotorTuru.Mssql => $"{zamanKolon} >= '{b}' AND {zamanKolon} < '{s}'",
                MotorTuru.Postgres => $"{zamanKolon} >= '{b}'::timestamp AND {zamanKolon} < '{s}'::timestamp",
                MotorTuru.MySql => $"{zamanKolon} >= '{b.Replace("T", " ")}' AND {zamanKolon} < '{s.Replace("T", " ")}'",
                MotorTuru.Oracle => $"{zamanKolon} >= TIMESTAMP '{b.Replace("T", " ")}' AND {zamanKolon} < TIMESTAMP '{s.Replace("T", " ")}'",
                _ => "",
            };
        }
        return "";
    }

    /// <summary>Seviye süzgeci: <c>{kolon} IN ('Error','Fatal',…)</c>; kolon ya da liste boşsa "".</summary>
    private static string SeviyeKosulu(MotorTuru motor, string? seviyeKolon, IReadOnlyList<string>? seviyeler)
    {
        if (string.IsNullOrEmpty(seviyeKolon) || seviyeler is not { Count: > 0 })
            return "";
        string liste = string.Join(", ", seviyeler.Select(d => $"'{StrLit(motor, d)}'"));
        return $"{seviyeKolon} IN ({liste})";
    }

    /// <summary>Boş olmayan koşulları <c> WHERE a AND b</c> olarak birleştirir; hepsi boşsa "".</summary>
    private static string Nerede(params string?[] kosullar)
    {
        string[] dolu = [.. kosullar.Where(k => !string.IsNullOrEmpty(k)).Select(k => k!)];
        return dolu.Length == 0 ? "" : " WHERE " + string.Join(" AND ", dolu);
    }

    /// <summary>Mevcut bir WHERE'e eklenecek koşul: <c> AND x</c> ya da "" (keşif sorgularına seviye eki).</summary>
    private static string Ve(string kosul) => kosul.Length == 0 ? "" : " AND " + kosul;

    /// <summary>Oracle noktalı virgül almaz; diğer motorlar ';' ile biter (mevcut sorgularla tutarlı).</summary>
    private static string Bitir(MotorTuru motor, string sql) => motor == MotorTuru.Oracle ? sql : sql + ";";

    // ── Mongo v20-S3 ─────────────────────────────────────────────────────────────────────────

    /// <summary>Mongo $match belgesi: zaman (son24/aralık) + seviye süzgeci; ikisi de yoksa null.</summary>
    private static Dictionary<string, object>? MongoEslesme(
        string? zamanAlan, bool son24Saat, DateTime? bas, DateTime? bit,
        string? seviyeAlan, IReadOnlyList<string>? seviyeler)
    {
        var m = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(zamanAlan))
        {
            // ⚡ v22-S4 turu-4 log m.4 (kullanıcı ekranı: ExceptionLog'da "Sorgu 120 sn zaman aşımını
            // aştı ve SUNUCU tarafından durduruldu" — yani sunucu bu şekilde gerçekten 120+ sn çalıştı):
            // son-24-saat eskiden `$expr + $$NOW + $subtract` idi. İki ölçülmüş sorunu var:
            //   (a) yerel MongoDB 8.3'te explain() iki biçimde de IXSCAN derken $expr 115 ms,
            //       düz literal 16 ms — aynı belge sayısında 7×;
            //   (b) $expr'in index kullanabilmesi sunucu SÜRÜMÜNE bağlı (eski sürümler hiç kullanamaz
            //       → milyonlarca dev belgede COLLSCAN). MERSIS prod'unun sürümü bilinmiyor; düz
            //       `{alan: {$gte: tarih}}` HER sürümde index'e oturur.
            // Bedeli: eşik artık SERVER saati ($$NOW) değil istemcinin UTC'si — NTP'li ortamda sapma
            // saniyelerdir, log örneklemi için önemsiz (bilinçli takas, buraya kayıtlı).
            if (son24Saat)
                m[zamanAlan] = new Dictionary<string, object>
                {
                    ["$gte"] = MongoTarih(DateTime.UtcNow.AddHours(-24)),
                };
            else if (bas is { } b && bit is { } s)
                m[zamanAlan] = new Dictionary<string, object> { ["$gte"] = MongoTarih(b), ["$lt"] = MongoTarih(s) };
        }
        if (!string.IsNullOrEmpty(seviyeAlan) && seviyeler is { Count: > 0 })
            m[seviyeAlan] = new Dictionary<string, object> { ["$in"] = seviyeler.Cast<object>().ToList() };
        return m.Count == 0 ? null : m;
    }

    private static string MongoGercekSayim(
        string koleksiyon, string alan, IReadOnlyList<string> desenler,
        string? zamanAlan, bool son24Saat, DateTime? bas, DateTime? bit,
        string? seviyeAlan, IReadOnlyList<string>? seviyeler)
    {
        var pipeline = new List<object>();
        if (MongoEslesme(zamanAlan, son24Saat, bas, bit, seviyeAlan, seviyeler) is { } eslesme)
            pipeline.Add(new Dictionary<string, object> { ["$match"] = eslesme });

        var grup = new Dictionary<string, object?> { ["_id"] = null };
        for (int i = 0; i < desenler.Count; i++)
            grup[$"c{i}"] = new Dictionary<string, object>
            {
                ["$sum"] = new Dictionary<string, object>
                {
                    ["$cond"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["$regexMatch"] = new Dictionary<string, object>
                            {
                                ["input"] = "$" + alan,
                                ["regex"] = desenler[i],
                                ["options"] = "s",
                            },
                        },
                        1, 0,
                    },
                },
            };
        pipeline.Add(new Dictionary<string, object> { ["$group"] = grup });
        var doc = new Dictionary<string, object> { ["aggregate"] = koleksiyon, ["pipeline"] = pipeline };
        return JsonSerializer.Serialize(doc);
    }

    private static string MongoDetay(
        string koleksiyon, string alan, string desen, int enFazla,
        string? zamanAlan, bool son24Saat, DateTime? bas, DateTime? bit,
        string? seviyeAlan, IReadOnlyList<string>? seviyeler)
    {
        Dictionary<string, object> filtre =
            MongoEslesme(zamanAlan, son24Saat, bas, bit, seviyeAlan, seviyeler) ?? [];
        filtre[alan] = new Dictionary<string, object> { ["$regex"] = desen, ["$options"] = "s" };

        var doc = new Dictionary<string, object>
        {
            ["find"] = koleksiyon,
            ["filter"] = filtre,
            ["limit"] = enFazla,
        };
        if (!string.IsNullOrEmpty(zamanAlan))
            doc["sort"] = new Dictionary<string, object> { [zamanAlan] = -1 };
        return JsonSerializer.Serialize(doc);
    }
}
