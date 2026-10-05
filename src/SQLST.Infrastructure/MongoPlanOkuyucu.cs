using System.Globalization;
using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// MongoDB <c>explain</c> → motor-nötr <see cref="SorguPlani"/> (V5-S1e).
///
/// MongoDB ayrı ailedir (<see cref="ILehce"/> uygulamaz), bu yüzden plan yardımcıları da
/// burada durur: sorguyu sarma (<see cref="SorguYaz"/>) ve çözme (<see cref="Coz"/>).
///
/// <b>En büyük fark: MongoDB'nin MALİYET MODELİ YOKTUR.</b> Diğer dört motor bir maliyet
/// sayısı üretir; Mongo üretmez. Bu yüzden <see cref="PlanDugumu.MaliyetYuzdesi"/> daima 0
/// kalır ve plana açık bir not düşülür — <b>uydurma yüzde üretilmez</b>. (MariaDB'de
/// maliyet gelmediğinde uygulanan kararın aynısı.) Sıralama sinyali olarak <b>incelenen
/// belge/anahtar sayısı</b> kullanılır: asıl teşhis "kaç belge okundu, kaçı döndü"dür.
///
/// <b>Güvenlik farkı (canlı doğrulandı):</b> PostgreSQL'de <c>EXPLAIN ANALYZE DELETE</c>
/// satırları GERÇEKTEN siler; MongoDB'de <c>explain</c> yazma işlemini <b>UYGULAMAZ</b> —
/// alan adı bile <c>nWouldDelete</c>'tir ("silinecek olan"). Canlı ölçüm: explain sonrası
/// koleksiyon 3 belgeyle kaldı. Bu yüzden gerçek plan burada yazma riski taşımaz.
/// </summary>
public static class MongoPlanOkuyucu
{
    internal const string MaliyetYokNotu =
        "MongoDB maliyet modeli sunmaz — yüzde gösterilmez. Sıralama için 'incelenen belge' "
      + "sayısına ve aşama türüne bakın (COLLSCAN = index yok).";

    /// <summary>
    /// Kullanıcının JSON sorgusunu <c>explain</c> ile sarar.
    /// <paramref name="gercek"/> false → <c>queryPlanner</c> (yalnız plan yapısı),
    /// true → <c>executionStats</c> (sorgu çalışır, ölçülen sayılar gelir; yazma UYGULANMAZ).
    /// </summary>
    public static string SorguYaz(string sorgu, bool gercek)
    {
        string govde = sorgu.Trim();
        if (!govde.StartsWith('{'))
        {
            throw new InvalidOperationException(
                "MongoDB planı için sorgu tek bir JSON belgesi olmalıdır (ör. { \"find\": … }).");
        }

        string ayrinti = gercek ? "executionStats" : "queryPlanner";
        return $$"""{ "explain": {{govde}}, "verbosity": "{{ayrinti}}" }""";
    }

    public static SorguPlani Coz(QueryResult sonuc, bool gercek)
    {
        // Mongo sonucu tek satır, üst düzey anahtarlar KOLON olarak gelir (MongoSonucEsleyici):
        // explainVersion | queryPlanner | executionStats | command | serverInfo | ok
        if (sonuc.ResultSetler.Count == 0 || sonuc.ResultSetler[0].Satirlar.Count == 0)
            throw new InvalidOperationException("Sunucu plan döndürmedi.");

        ResultSetData set = sonuc.ResultSetler[0];
        object?[] satir = sonuc.ResultSetler[0].Satirlar[0];

        string? planlayici = Hucre(set, satir, "queryPlanner");
        string? istatistik = Hucre(set, satir, "executionStats");

        if (planlayici is null && istatistik is null)
            throw new InvalidOperationException("Plan çıktısında queryPlanner/executionStats bulunamadı.");

        var uyarilar = new List<string> { MaliyetYokNotu };
        PlanDugumu? kok = null;
        string baslik = "MongoDB planı";

        // GERÇEK planda ağaç executionStages'tedir (ölçülen sayılarla)
        if (gercek && istatistik is not null && Ayrıştır(istatistik) is { } ist)
        {
            using (ist)
            {
                JsonElement k = ist.RootElement;
                baslik = OzetBasligi(k);
                foreach (string u in ToplamUyarilari(k))
                    uyarilar.Add(u);

                if (k.TryGetProperty("executionStages", out JsonElement asamalar))
                    kok = DugumeCevir(asamalar, gercek: true);
            }
        }

        // Tahmini planda (ya da gerçek planda ağaç yoksa) queryPlanner.winningPlan
        if (kok is null && planlayici is not null && Ayrıştır(planlayici) is { } plan)
        {
            using (plan)
            {
                JsonElement k = plan.RootElement;
                if (k.TryGetProperty("namespace", out JsonElement ns) && ns.ValueKind == JsonValueKind.String)
                    baslik = $"MongoDB planı — {ns.GetString()}";

                if (RededilenSayisi(k) is > 0 and var red)
                    uyarilar.Add($"Optimizer {red} alternatif planı eledi.");

                if (KazananPlan(k) is { } kazanan)
                    kok = DugumeCevir(kazanan, gercek: false);
            }
        }

        if (kok is null)
            throw new InvalidOperationException("Plan çıktısında aşama ağacı bulunamadı.");

        kok = kok with { Uyarilar = [.. uyarilar, .. kok.Uyarilar] };

        return new SorguPlani(gercek, [new IfadePlani(
            IfadeMetni: baslik,
            ToplamMaliyet: 0,          // Mongo maliyet üretmez
            Kok: kok,
            EksikIndexler: [])]);      // Mongo eksik index önermez
    }

    /// <summary>
    /// Kazanan plan. Mongo 8'in SBE motorunda ağaç bir katman daha derindedir
    /// (<c>winningPlan.queryPlan</c>); klasik motorda <c>winningPlan</c>'ın kendisidir.
    /// </summary>
    private static JsonElement? KazananPlan(JsonElement planlayici)
    {
        if (!planlayici.TryGetProperty("winningPlan", out JsonElement kazanan)
            || kazanan.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return kazanan.TryGetProperty("queryPlan", out JsonElement sorguPlani)
               && sorguPlani.ValueKind == JsonValueKind.Object
            ? sorguPlani
            : kazanan;
    }

    private static PlanDugumu DugumeCevir(JsonElement asama, bool gercek)
    {
        var cocuklar = new List<PlanDugumu>();

        // Tek çocuk: inputStage · çok çocuk: inputStages (OR, SORT_MERGE, $lookup…)
        if (asama.TryGetProperty("inputStage", out JsonElement tek) && tek.ValueKind == JsonValueKind.Object)
            cocuklar.Add(DugumeCevir(tek, gercek));

        if (asama.TryGetProperty("inputStages", out JsonElement coklu) && coklu.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in coklu.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.Object)
                    cocuklar.Add(DugumeCevir(c, gercek));
            }
        }

        double? donen = gercek ? Sayi(asama, "nReturned") : null;

        return new PlanDugumu(
            Islem: (Metin(asama, "stage") ?? "(bilinmeyen)").ToUpperInvariant(),
            Ayrinti: Ayrinti(asama),
            MaliyetYuzdesi: 0,          // maliyet modeli yok — uydurulmaz
            // NULL = "tahmin diye bir şey yok". Önce 0 yazılıyordu ve bu, uydurma bir
            // sinyal üretiyordu: SapmaVar "tahmin 0 / gerçek 5000" görüp mükemmel index'li
            // sorgularda bile "istatistikler eskimiş olabilir" uyarısı yakıyordu. Mongo'nun
            // ne kardinalite tahmini ne de istatistiği vardır (A1/B1 bulgusu, 2026-07-19).
            TahminiSatir: null,
            GercekSatir: donen,
            Uyarilar: [.. AsamaUyarilari(asama, gercek)],
            Cocuklar: cocuklar);
    }

    private static string? Ayrinti(JsonElement asama)
    {
        var parcalar = new List<string>();

        if (Metin(asama, "indexName") is { } index)
            parcalar.Add($"index: {index}");
        if (Ham(asama, "keyPattern") is { } desen && parcalar.Count == 0)
            parcalar.Add($"anahtar: {desen}");
        if (Metin(asama, "direction") is { } yon && yon != "forward")
            parcalar.Add($"yön: {yon}");
        if (Ham(asama, "filter") is { } filtre && filtre is not "{}" and not "{ }")
            parcalar.Add($"filtre: {Kisalt(filtre)}");
        if (Ham(asama, "sortPattern") is { } sirala)
            parcalar.Add($"sıralama: {sirala}");

        // Ölçülen erişim: teşhisin asıl sinyali
        if (Sayi(asama, "docsExamined") is { } belge)
            parcalar.Add($"incelenen belge: {belge:N0}");
        if (Sayi(asama, "keysExamined") is { } anahtar)
            parcalar.Add($"incelenen anahtar: {anahtar:N0}");

        return parcalar.Count == 0 ? null : string.Join(" · ", parcalar);
    }

    private static IEnumerable<string> AsamaUyarilari(JsonElement asama, bool gercek)
    {
        string asamaAdi = (Metin(asama, "stage") ?? "").ToUpperInvariant();

        if (asamaAdi == "COLLSCAN")
            yield return "Koleksiyon taraması (COLLSCAN) — sorgu hiçbir index kullanmıyor.";

        if (asamaAdi == "SORT")
        {
            yield return "Bellekte sıralama — index sırası kullanılamıyor. "
                       + "Büyük sonuçta bellek sınırına takılıp sorgu hata verebilir.";
        }

        if (!gercek)
            yield break;

        // Okunan belge sayısı dönen belgenin çok üstündeyse index adayı
        double donen = Sayi(asama, "nReturned") ?? 0;
        if (Sayi(asama, "docsExamined") is { } incelenen && incelenen >= 100 && incelenen > donen * 10)
            yield return $"{incelenen:N0} belge incelendi, {donen:N0} belge döndü — index adayı.";
    }

    private static IEnumerable<string> ToplamUyarilari(JsonElement istatistik)
    {
        if (istatistik.TryGetProperty("executionSuccess", out JsonElement basari)
            && basari.ValueKind == JsonValueKind.False)
        {
            yield return "Sorgu ölçüm sırasında başarısız oldu — sayılar eksik olabilir.";
        }

        double donen = Sayi(istatistik, "nReturned") ?? 0;
        double belge = Sayi(istatistik, "totalDocsExamined") ?? 0;
        double anahtar = Sayi(istatistik, "totalKeysExamined") ?? 0;

        if (belge >= 100 && belge > donen * 10)
            yield return $"Toplam {belge:N0} belge incelendi, {donen:N0} belge döndü.";
        if (anahtar == 0 && belge > 0)
            yield return "Hiç index anahtarı okunmadı — sorgu tamamen koleksiyon taramasına dayanıyor.";
    }

    private static string OzetBasligi(JsonElement istatistik)
    {
        var parcalar = new List<string>();
        if (Sayi(istatistik, "nReturned") is { } donen)
            parcalar.Add($"{donen:N0} belge");
        if (Sayi(istatistik, "totalDocsExamined") is { } belge)
            parcalar.Add($"{belge:N0} incelendi");
        if (Sayi(istatistik, "executionTimeMillis") is { } sure)
            parcalar.Add($"{sure:N0} ms");

        return parcalar.Count == 0 ? "MongoDB planı" : $"MongoDB planı — {string.Join(" · ", parcalar)}";
    }

    private static int RededilenSayisi(JsonElement planlayici)
        => planlayici.TryGetProperty("rejectedPlans", out JsonElement red) && red.ValueKind == JsonValueKind.Array
            ? red.GetArrayLength()
            : 0;

    /// <summary>Hücreyi kolon ADINDAN bulur — kolon sırasına güvenmek kırılgan olurdu.</summary>
    private static string? Hucre(ResultSetData set, object?[] satir, string kolon)
    {
        for (int i = 0; i < set.Kolonlar.Count && i < satir.Length; i++)
        {
            if (string.Equals(set.Kolonlar[i].Ad, kolon, StringComparison.OrdinalIgnoreCase))
                return satir[i]?.ToString();
        }
        return null;
    }

    private static JsonDocument? Ayrıştır(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
        }
        catch (JsonException)
        {
            return null;    // çözümlenemeyen bölüm sessizce atlanır; çağıran kök yoksa hata verir
        }
    }

    private static string? Metin(JsonElement dugum, string ad)
        => dugum.TryGetProperty(ad, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static string? Ham(JsonElement dugum, string ad)
        => dugum.TryGetProperty(ad, out JsonElement e) && e.ValueKind == JsonValueKind.Object
            ? e.GetRawText()
            : null;

    private static double? Sayi(JsonElement dugum, string ad)
    {
        if (!dugum.TryGetProperty(ad, out JsonElement e))
            return null;

        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String when double.TryParse(
                e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
            _ => null,
        };
    }

    private static string Kisalt(string metin)
        => metin.Length <= 60 ? metin : metin[..60] + "…";
}
