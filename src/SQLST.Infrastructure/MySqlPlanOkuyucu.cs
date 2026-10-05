using System.Globalization;
using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// MySQL / MariaDB <c>EXPLAIN FORMAT=JSON</c> → motor-nötr <see cref="SorguPlani"/> (V5-S1c).
///
/// Yapı, SQL Server ve PostgreSQL'den TAMAMEN farklıdır: ağaç, "çocuklar" dizisiyle değil
/// <b>iç içe geçmiş İSİMLİ bölümlerle</b> kurulur (<c>query_block</c> → <c>ordering_operation</c>
/// → <c>grouping_operation</c> → <c>nested_loop[]</c> → <c>table</c>). Bu yüzden okuyucu
/// "çocuk dizisini gez" değil, <b>bilinen bölüm adlarını tanı</b> mantığıyla çalışır.
///
/// <b>Bir kolaylık:</b> maliyet çıkarma işlemi GEREKMEZ. MySQL her tablo için
/// <c>cost_info.read_cost</c> + <c>eval_cost</c> ile düğümün KENDİ maliyetini doğrudan verir;
/// toplam da <c>query_block.cost_info.query_cost</c>'tur. PostgreSQL'deki "kümülatiften kendi
/// payını çıkar" ve "iterasyonla çarp" tuzaklarının hiçbiri burada yok.
///
/// <b>İki sunucu, iki şekil (dürüst sınır):</b> MariaDB aynı komutu kabul eder ama JSON'u
/// farklıdır — satır sayısı <c>rows</c> (MySQL'de <c>rows_examined_per_scan</c>) ve
/// <c>cost_info</c> çoğu sürümde YOKTUR. Okuyucu ikisini de tanır; maliyet bilgisi yoksa
/// yüzdeler 0 kalır ve plana açık bir not düşülür (uydurma yüzde üretilmez).
///
/// <b>GERÇEK plan bu motorda kapalıdır</b> (<see cref="MySqlLehcesi.PlanGercekDestekler"/>):
/// MariaDB <c>ANALYZE FORMAT=JSON</c> ile aynı JSON'u verir ama MySQL 8 <c>EXPLAIN ANALYZE</c>
/// ile JSON değil TREE metni döndürür. Canlı doğrulama yapılamadığından yarım bir yol açılmadı.
/// </summary>
public static class MySqlPlanOkuyucu
{
    /// <summary>Maliyet bilgisi hiç gelmediğinde (tipik MariaDB) plana düşülen not.</summary>
    internal const string MaliyetYokNotu =
        "Bu sunucu planda maliyet bilgisi vermiyor (MariaDB) — maliyet payları hesaplanamadı; "
      + "sıralama için satır sayılarına ve erişim türüne bakın.";

    public static SorguPlani Coz(string json, bool gercek)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Plan JSON'u boş — sunucu plan döndürmedi.");

        JsonDocument belge;
        try
        {
            belge = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Plan JSON'u çözümlenemedi: {ex.Message}", ex);
        }

        using (belge)
        {
            JsonElement kok = belge.RootElement;
            if (kok.ValueKind != JsonValueKind.Object
                || !kok.TryGetProperty("query_block", out JsonElement blok)
                || blok.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Plan JSON'unda query_block bulunamadı.");
            }

            double toplam = Sayi(blok, "cost_info", "query_cost") ?? 0;
            PlanDugumu? dugum = BolumdenDugum(blok, toplam);

            if (dugum is null)
                throw new InvalidOperationException("Plan JSON'unda okunabilir düğüm bulunamadı.");

            // Maliyet hiç yoksa (MariaDB) uydurma yüzde üretme — durumu açıkça söyle.
            // Ölçüt DIŞ bloğun maliyeti değil, planda HİÇ cost_info bulunmaması: MySQL'de
            // UNION'ın dış bloğunda cost_info yoktur ama iç bloklarda vardır; eski koşul
            // orada MySQL kullanıcısına "(MariaDB)" notu gösteriyordu (A1/B1, 2026-07-19).
            if (!MaliyetBilgisiVar(kok))
                dugum = dugum with { Uyarilar = [.. dugum.Uyarilar, MaliyetYokNotu] };

            return new SorguPlani(gercek, [new IfadePlani(
                IfadeMetni: Basligi(blok),
                ToplamMaliyet: toplam,
                Kok: dugum,
                EksikIndexler: [])]);   // MySQL/MariaDB eksik index önermez
        }
    }

    /// <summary>Planın herhangi bir yerinde maliyet bilgisi var mı (iç bloklar dahil).</summary>
    private static bool MaliyetBilgisiVar(JsonElement dugum)
    {
        switch (dugum.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty ozellik in dugum.EnumerateObject())
                {
                    if (ozellik.NameEquals("cost_info") || MaliyetBilgisiVar(ozellik.Value))
                        return true;
                }
                return false;

            case JsonValueKind.Array:
                foreach (JsonElement oge in dugum.EnumerateArray())
                {
                    if (MaliyetBilgisiVar(oge))
                        return true;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Bilinen bölüm adlarını tanıyarak düğüm kurar. Sıra ÖNEMLİdir: dış sarmalayıcılar
    /// (sıralama/gruplama) önce gelir ki ağaç yürütme sırasını yansıtsın.
    /// </summary>
    private static PlanDugumu? BolumdenDugum(JsonElement bolum, double toplam)
    {
        if (bolum.ValueKind != JsonValueKind.Object)
            return null;

        // İç içe alt sorgu bloğu. ÖLÇEK BURADA DEĞİŞİR: MySQL'de her query_block'un KENDİ
        // query_cost'u vardır ve iç bloğun maliyeti dış bloğunkiyle aynı ölçekte DEĞİLDİR.
        // Önce dış bloğun toplamı aşağı taşınıyordu; materyalize edilmiş bir alt sorguda dış
        // maliyet küçücük (geçici tabloyu tarama), iç maliyet ise asıl iş olduğundan yüzdeler
        // %100'ü katbekat aşıyor, Math.Clamp de bunu makul görünen bir sayıya çevirip
        // GİZLİYORDU. Artık her blok kendi toplamıyla ölçekleniyor: yüzde "bu düğümün, içinde
        // bulunduğu bloğa oranı" demektir (A1/B1 bulgusu, 2026-07-19).
        if (bolum.TryGetProperty("query_block", out JsonElement icBlok))
        {
            double icToplam = Sayi(icBlok, "cost_info", "query_cost") ?? 0;
            return BolumdenDugum(icBlok, icToplam > 0 ? icToplam : toplam);
        }

        foreach ((string ad, string etiket) in Sarmalayicilar)
        {
            if (!bolum.TryGetProperty(ad, out JsonElement ic))
                continue;

            PlanDugumu? cocuk = BolumdenDugum(ic, toplam);
            return new PlanDugumu(
                Islem: etiket,
                Ayrinti: SarmalayiciAyrintisi(ic, ad),
                MaliyetYuzdesi: 0,               // MySQL bu adımlara ayrı maliyet vermez
                TahminiSatir: Sayi(ic, "rows_produced_per_join") ?? Sayi(ic, "rows") ?? 0,
                GercekSatir: null,
                Uyarilar: [.. SarmalayiciUyarilari(ic)],
                Cocuklar: cocuk is null ? [] : [cocuk]);
        }

        // Birden çok tablonun birleştirilmesi
        if (bolum.TryGetProperty("nested_loop", out JsonElement dongu) && dongu.ValueKind == JsonValueKind.Array)
        {
            var cocuklar = new List<PlanDugumu>();
            foreach (JsonElement e in dongu.EnumerateArray())
            {
                if (BolumdenDugum(e, toplam) is { } c)
                    cocuklar.Add(c);
            }
            return new PlanDugumu(
                Islem: "Nested loop",
                Ayrinti: $"{cocuklar.Count} tablo",
                MaliyetYuzdesi: 0,
                TahminiSatir: cocuklar.Count > 0 ? cocuklar[^1].TahminiSatir : 0,
                GercekSatir: null,
                Uyarilar: [],
                Cocuklar: cocuklar);
        }

        // UNION
        if (bolum.TryGetProperty("union_result", out JsonElement birlesim))
        {
            var cocuklar = new List<PlanDugumu>();
            if (birlesim.TryGetProperty("query_specifications", out JsonElement ozellikler)
                && ozellikler.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in ozellikler.EnumerateArray())
                {
                    if (BolumdenDugum(e, toplam) is { } c)
                        cocuklar.Add(c);
                }
            }
            return new PlanDugumu("Union", null, 0, 0, null, [], cocuklar);
        }

        if (bolum.TryGetProperty("table", out JsonElement tablo))
            return TablodanDugum(tablo, toplam);

        return null;
    }

    /// <summary>Dış sarmalayıcı bölümler — dıştan içe doğru sıralı.</summary>
    private static readonly (string Ad, string Etiket)[] Sarmalayicilar =
    [
        // union_result BURADA DEĞİL: çocukları query_specifications dizisindedir,
        // tek çocuklu sarmalayıcı mantığıyla okunamaz (kendi dalı var).
        ("ordering_operation", "Sıralama"),
        ("duplicates_removal", "Yinelenen ayıklama (DISTINCT)"),
        ("grouping_operation", "Gruplama (GROUP BY)"),
        ("windowing", "Pencere fonksiyonu"),
        ("buffer_result", "Sonuç arabelleği"),
        ("materialized_from_subquery", "Alt sorgu somutlaştırma"),
    ];

    private static PlanDugumu TablodanDugum(JsonElement tablo, double toplam)
    {
        // KENDİ maliyeti doğrudan gelir — çıkarma/çarpma gerekmez (PG'nin aksine)
        double kendi = (Sayi(tablo, "cost_info", "read_cost") ?? 0)
                     + (Sayi(tablo, "cost_info", "eval_cost") ?? 0);

        // MariaDB'de rows, MySQL'de rows_produced_per_join / rows_examined_per_scan
        double satir = Sayi(tablo, "rows_produced_per_join")
                    ?? Sayi(tablo, "rows_examined_per_scan")
                    ?? Sayi(tablo, "rows")
                    ?? 0;

        var cocuklar = new List<PlanDugumu>();
        if (tablo.TryGetProperty("materialized_from_subquery", out JsonElement alt)
            && BolumdenDugum(alt, toplam) is { } altDugum)
        {
            cocuklar.Add(altDugum);
        }
        if (tablo.TryGetProperty("attached_subqueries", out JsonElement altlar)
            && altlar.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in altlar.EnumerateArray())
            {
                if (BolumdenDugum(e, toplam) is { } c)
                    cocuklar.Add(c);
            }
        }

        return new PlanDugumu(
            Islem: ErisimAdi(tablo),
            Ayrinti: TabloAyrintisi(tablo),
            MaliyetYuzdesi: toplam > 0 ? Math.Clamp(kendi / toplam * 100, 0, 100) : 0,
            TahminiSatir: satir,
            GercekSatir: null,               // gerçek plan bu motorda kapalı
            Uyarilar: [.. TabloUyarilari(tablo)],
            Cocuklar: cocuklar);
    }

    /// <summary>Erişim türünü okunur ada çevirir — kullanıcı "ALL" görüp anlamasın diye.</summary>
    private static string ErisimAdi(JsonElement tablo)
    {
        string tur = Metin(tablo, "access_type") ?? "?";
        string ad = tur switch
        {
            "ALL" => "Tam tablo taraması",
            "index" => "Tam index taraması",
            "range" => "Aralık taraması",
            "ref" or "eq_ref" => "Index araması",
            "const" or "system" => "Sabit satır",
            "fulltext" => "Tam metin araması",
            "unique_subquery" or "index_subquery" => "Alt sorgu index araması",
            _ => $"Erişim ({tur})",
        };
        return $"{ad} [{tur}]";
    }

    private static string? TabloAyrintisi(JsonElement tablo)
    {
        var parcalar = new List<string>();
        if (Metin(tablo, "table_name") is { } ad)
            parcalar.Add(ad);
        if (Metin(tablo, "key") is { } anahtar)
            parcalar.Add($"index: {anahtar}");
        if (Metin(tablo, "attached_condition") is { } kosul)
            parcalar.Add(kosul);
        return parcalar.Count == 0 ? null : string.Join(" · ", parcalar);
    }

    /// <summary>
    /// MySQL planında hazır "uyarı" alanı yoktur — PostgreSQL'de olduğu gibi sinyallerden
    /// TÜRETİLİR. Bunlar bu motorun klasik performans tuzaklarıdır.
    /// </summary>
    private static IEnumerable<string> TabloUyarilari(JsonElement tablo)
    {
        string? erisim = Metin(tablo, "access_type");

        if (erisim == "ALL")
            yield return "Tam tablo taraması — uygun index yoksa büyük tabloda pahalıdır.";
        if (erisim == "index")
            yield return "Tam index taraması — tüm index okunuyor, aralık daraltılamamış.";

        // Index adayı var ama kullanılmıyor: en sık rastlanan gerçek sorun
        bool adayVar = tablo.TryGetProperty("possible_keys", out JsonElement adaylar)
                       && adaylar.ValueKind == JsonValueKind.Array && adaylar.GetArrayLength() > 0;
        if (adayVar && Metin(tablo, "key") is null)
            yield return "Kullanılabilir index var ama seçilmemiş (possible_keys dolu, key boş).";

        if (Bayrak(tablo, "using_filesort"))
            yield return "Sıralama için filesort — index sırası kullanılamıyor.";
        if (Bayrak(tablo, "using_temporary_table"))
            yield return "Geçici tablo kullanılıyor — GROUP BY/DISTINCT index'ten karşılanamadı.";

        // Okunan satırın çok azı geçiyorsa filtre index'e taşınabilir
        if (Sayi(tablo, "filtered") is { } yuzde && yuzde is > 0 and < 10
            && (Sayi(tablo, "rows_examined_per_scan") ?? Sayi(tablo, "rows")) is > 1000)
        {
            yield return $"Okunan satırların yalnız %{yuzde:F1}'i koşulu geçiyor — index adayı.";
        }
    }

    private static IEnumerable<string> SarmalayiciUyarilari(JsonElement bolum)
    {
        if (Bayrak(bolum, "using_filesort"))
            yield return "Sıralama için filesort — index sırası kullanılamıyor.";
        if (Bayrak(bolum, "using_temporary_table"))
            yield return "Geçici tablo kullanılıyor.";
    }

    private static string? SarmalayiciAyrintisi(JsonElement bolum, string ad)
        => Metin(bolum, "table_name") is { } t ? t : null;

    private static string Basligi(JsonElement blok)
    {
        double? maliyet = Sayi(blok, "cost_info", "query_cost");
        string secim = Sayi(blok, "select_id") is { } id ? $"select #{id:F0}" : "sorgu";
        return maliyet is { } m ? $"{secim} — maliyet {m:N2}" : secim;
    }

    private static bool Bayrak(JsonElement dugum, string ad)
        => dugum.TryGetProperty(ad, out JsonElement e)
           && (e.ValueKind == JsonValueKind.True
               || (e.ValueKind == JsonValueKind.String && e.GetString() is "true" or "1"));

    private static string? Metin(JsonElement dugum, string ad)
        => dugum.TryGetProperty(ad, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    /// <summary>
    /// Sayı okuma. <b>MySQL maliyetleri JSON'da METİN olarak gelir</b> ("read_cost": "0.25") —
    /// yalnız Number bekleyen bir okuyucu tüm maliyetleri sessizce 0 görürdü.
    /// Ayrıştırma daima invariant'tır (tr-TR'de "0.25" → 25 olurdu).
    /// </summary>
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

    private static double? Sayi(JsonElement dugum, string bolum, string ad)
        => dugum.TryGetProperty(bolum, out JsonElement ic) && ic.ValueKind == JsonValueKind.Object
            ? Sayi(ic, ad)
            : null;
}
