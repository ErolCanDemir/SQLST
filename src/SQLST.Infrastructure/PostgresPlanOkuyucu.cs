using System.Globalization;
using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// PostgreSQL <c>EXPLAIN (… FORMAT JSON)</c> → motor-nötr <see cref="SorguPlani"/> (V5-S1b).
///
/// PostgreSQL'in çıktısı SQL Server'ınkinden yalnız BİÇİMCE değil ANLAMCA da farklıdır;
/// aşağıdaki dört kural canlı PG 16.4 üzerinde ölçülerek doğrulandı:
///
/// <list type="number">
/// <item><b>Kök bir DİZİdir</b>: <c>[ { "Plan": { … } } ]</c>. Nesne bekleyip <c>["Plan"]</c>
/// aramak sessizce boş ağaç üretir.</item>
/// <item><b>Satır sayıları DÖNGÜ BAŞINAdır.</b> Gerçek toplam = <c>Actual Rows × Actual Loops</c>.
/// Nested Loop'un iç tarafı 5 kez dönüyorsa "6 satır" yazar, gerçek 30'dur. Çarpmayı atlamak
/// hata vermez — yalnızca YANLIŞ sayı gösterir.</item>
/// <item><b>Çocuğun maliyeti de İTERASYON BAŞINAdır.</b> Kendi maliyeti
/// <c>Total − Σ(çocuk.Total × çocuk.Loops)</c>'tur. Canlı ölçüm: Nested Loop 42.64,
/// çocuklar 1.05×1 + 8.31×5 = 42.60 → kendi ≈ 0.04.</item>
/// <item><b>ERKEN DURMA maliyet muhasebesini kırar.</b> <c>LIMIT</c>/Merge Join gibi düğümler
/// çocuğu sonuna kadar tüketmez; çocuğun <c>Total Cost</c>'u ise SONUNA KADAR okuma maliyetidir.
/// Canlı ölçüm: <c>Limit</c> 0.46 iken altındaki <c>Index Scan</c> 1693.29. Böyle bir düğümde
/// "kendi maliyeti" NEGATİF çıkar. Sessizce sıfıra kırpmak yanlış bir tabloyu doğru gibi
/// gösterirdi → sıfıra kırpılır AMA düğüme açık bir not düşülür ve plan "yaklaşık" işaretlenir.</item>
/// </list>
///
/// PostgreSQL eksik index ÖNERMEZ → <see cref="IfadePlani.EksikIndexler"/> daima boştur.
/// Uyarılar da hazır gelmez; <see cref="Uyarilar"/>'da sinyallerden TÜRETİLİR.
/// </summary>
public static class PostgresPlanOkuyucu
{
    /// <summary>Erken durma yüzünden maliyet payı hesaplanamayan düğüme düşülen not.</summary>
    internal const string ErkenDurmaNotu =
        "Bu düğüm alt planını sonuna kadar okumuyor (LIMIT/erken durma) — maliyet payı hesaplanamadı.";

    /// <summary>Erken duran bir düğümün ALTINDAKİ düğümlere düşülen not.</summary>
    internal const string UstErkenDurmaNotu =
        "Üstteki düğüm bu alt planı sonuna kadar okumuyor — buradaki maliyet payı hesaplanamadı.";

    public static SorguPlani Coz(string json, bool gercek)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Plan JSON'u boş — sunucu plan döndürmedi.");

        JsonDocument belge;
        try
        {
            // Varsayılan MaxDepth 64'tür; çok yollu join planları bunu aşabilir ve plan
            // HİÇ okunamaz olurdu (MSSQL tarafında böyle bir sınır yok — asimetri olmasın).
            belge = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Plan JSON'u çözümlenemedi: {ex.Message}", ex);
        }

        using (belge)
        {
            // Kök DİZİdir; her eleman bir ifadenin planını sarar
            JsonElement kok = belge.RootElement;
            if (kok.ValueKind != JsonValueKind.Array || kok.GetArrayLength() == 0)
                throw new InvalidOperationException("Plan JSON'u beklenen dizi biçiminde değil.");

            var ifadeler = new List<IfadePlani>();
            foreach (JsonElement sarmalayici in kok.EnumerateArray())
            {
                // Nesne olmayan eleman TryGetProperty'de ham .NET istisnası fırlatır —
                // kullanıcıya İngilizce bir sistem mesajı sızmasın diye önce tip denetlenir
                if (sarmalayici.ValueKind != JsonValueKind.Object
                    || !sarmalayici.TryGetProperty("Plan", out JsonElement kokDugum)
                    || kokDugum.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                double toplam = Sayi(kokDugum, "Total Cost") ?? 0;
                // Kök normalde 1 kez çalışır ama sayacı varsa ona güvenilir
                double kokDongu = gercek && Sayi(kokDugum, "Actual Loops") is 0 ? 0 : 1;
                PlanDugumu dugum = DugumeCevir(kokDugum, toplam, gercek, kokDongu, ustErkenDurma: false);

                ifadeler.Add(new IfadePlani(
                    IfadeMetni: OzetBasligi(sarmalayici, kokDugum),
                    ToplamMaliyet: toplam,
                    Kok: dugum,
                    EksikIndexler: []));   // PostgreSQL index önermez
            }

            if (ifadeler.Count == 0)
                throw new InvalidOperationException("Plan JSON'unda ifade bulunamadı.");

            return new SorguPlani(gercek, ifadeler);
        }
    }

    /// <param name="dongu">
    /// Bu düğümün maliyetinin plana kaç KEZ katıldığı — ebeveyn hesaplar (bkz. <see cref="Carpan"/>).
    /// <b>Actual Loops ile aynı şey DEĞİLDİR:</b> paralel planda Loops işçi sayısını gösterir
    /// ama maliyet zaten işçi başınadır; orada çarpan 1'dir.
    /// </param>
    /// <param name="ustErkenDurma">
    /// Üstte alt planını sonuna kadar okumayan bir düğüm var mı. Varsa bu düğümün maliyet
    /// payı ANLAMSIZDIR (kendi maliyeti "sonuna kadar okuma" maliyetidir, oysa okunmadı) →
    /// pay 0 verilir ve not düşülür. Yayılmazsa çocuk payı yüz binlerce yüzdeye çıkar.
    /// </param>
    private static PlanDugumu DugumeCevir(
        JsonElement dugum, double toplamMaliyet, bool gercek, double dongu, bool ustErkenDurma)
    {
        // Yaprakta "Plans" anahtarı HİÇ YOKTUR (boş dizi değil) → varlığı sorulmalı
        List<JsonElement> tumCocuklar = dugum.TryGetProperty("Plans", out JsonElement plans)
            && plans.ValueKind == JsonValueKind.Array
                ? [.. plans.EnumerateArray()]
                : [];

        // Önce KENDİ maliyetini bul: çocukların katkısı çarpanlarıyla düşülür.
        // InitPlan/SubPlan/CTE ana akışın maliyetine dahil DEĞİLDİR → muhasebeye girmez.
        double cocukToplami = 0;
        var cocukCarpanlari = new List<double>(tumCocuklar.Count);
        foreach (JsonElement c in tumCocuklar)
        {
            double carpan = Carpan(dugum, c, tumCocuklar, gercek);
            cocukCarpanlari.Add(carpan);
            if (AnaAkisCocugu(c))
                cocukToplami += (Sayi(c, "Total Cost") ?? 0) * carpan;
        }

        double kendiIterasyon = (Sayi(dugum, "Total Cost") ?? 0) - cocukToplami;

        // Negatif = ebeveyn alt planını sonuna kadar TÜKETMİYOR (LIMIT, Merge Join erken çıkışı).
        // Canlı ölçüm: Limit 0.59 iken altındaki Index Scan 19417.42.
        bool erkenDurma = kendiIterasyon < -0.01;   // yuvarlama gürültüsü değil, gerçek tutarsızlık
        if (kendiIterasyon < 0)
            kendiIterasyon = 0;

        // Hiç çalışmayan düğüm (Actual Loops = 0) plana maliyet KATMAZ
        double kendi = kendiIterasyon * Math.Max(dongu, 0);

        var uyarilar = new List<string>(Uyarilar(dugum));
        if (erkenDurma)
            uyarilar.Add(ErkenDurmaNotu);
        else if (ustErkenDurma)
            uyarilar.Add(UstErkenDurmaNotu);

        // Üstte erken durma varsa bu düğümün maliyeti "sonuna kadar okuma" maliyetidir ama
        // okunmadı → pay anlamsız. Ayrıca sözleşme gereği pay her hâlükârda 0-100 aralığında
        // kalmalı (PlanModelleri: "0-100"); tüketici ekranda ham basıyor.
        double yuzde = ustErkenDurma || toplamMaliyet <= 0
            ? 0
            : Math.Clamp(kendi / toplamMaliyet * 100, 0, 100);

        var cocuklar = new List<PlanDugumu>(tumCocuklar.Count);
        for (int i = 0; i < tumCocuklar.Count; i++)
        {
            cocuklar.Add(DugumeCevir(
                tumCocuklar[i], toplamMaliyet, gercek,
                dongu: dongu * cocukCarpanlari[i],
                ustErkenDurma: ustErkenDurma || erkenDurma));
        }

        // SATIR sayısı düğümün KENDİ Actual Loops'uyla çarpılır — maliyet çarpanıyla değil.
        // (Paralel planda çarpan 1'dir ama satırlar gerçekten işçi başına raporlanır,
        //  yani toplam satır için Loops ile çarpmak gerekir.)
        double satirDongusu = Sayi(dugum, "Actual Loops") ?? 1;

        double? gercekSatir = null;
        if (gercek && Sayi(dugum, "Actual Rows") is { } satir)
        {
            // Loops = 0 → düğüm HİÇ çalıştırılmadı; 0 satır demek yanıltıcı olur
            gercekSatir = satirDongusu > 0 ? satir * satirDongusu : null;
        }

        // Tahmin de gerçek satırla AYNI ölçekte olmalı (ikisi de döngü başına raporlanır);
        // yoksa sapma uyarısı düğüm başına asılsız yanar.
        double tahmini = Sayi(dugum, "Plan Rows") ?? 0;
        if (gercek && satirDongusu > 0)
            tahmini *= satirDongusu;

        return new PlanDugumu(
            Islem: IslemAdi(dugum),
            Ayrinti: Ayrinti(dugum),
            MaliyetYuzdesi: yuzde,
            TahminiSatir: tahmini,
            GercekSatir: gercekSatir,
            Uyarilar: uyarilar,
            Cocuklar: cocuklar);
    }

    /// <summary>
    /// Çocuğun maliyetinin ebeveyne kaç KEZ katıldığı.
    ///
    /// <b>Bu, <c>Actual Loops</c> DEĞİLDİR</b> — en pahalıya mal olan ayrım budur:
    /// <list type="bullet">
    /// <item><b>Yeniden çalıştırma (Nested Loop iç tarafı):</b> ebeveyn çocuğu dış satır
    /// sayısı kadar yeniden koşturur ve maliyetini o kadar kez öder. Canlı ölçüm:
    /// NL 42.64 = 1.05×1 + 8.31×<b>5</b>.</item>
    /// <item><b>Paralellik (Gather altı):</b> <c>Actual Loops</c> işçi+lider sayısını gösterir
    /// AMA maliyet zaten işçi başınadır; ebeveyn onu <b>bir kez</b> içerir. Canlı ölçüm:
    /// Gather 2832.06 = Partial Aggregate 2832.06 × <b>1</b> (loops 5 olmasına rağmen).
    /// Burada <c>Loops</c> ile çarpmak payları işçi sayısı kadar şişirir (ölçülen: Σ %485)
    /// ve ebeveyni negatife düşürüp asılsız "erken durma" notu doğurur.</item>
    /// </list>
    /// Bu yüzden çarpan, çocuğun döngüsünün EBEVEYNİNKİNE ORANIdır; Gather/Gather Merge
    /// altında ise daima 1'dir.
    /// </summary>
    private static double Carpan(JsonElement ebeveyn, JsonElement cocuk, List<JsonElement> kardesler, bool gercek)
    {
        // Paralel dağıtım: işçi sayısı maliyeti çoğaltmaz
        if (Metin(ebeveyn, "Node Type") is "Gather" or "Gather Merge")
            return 1;

        if (gercek)
        {
            double cocukDongu = Sayi(cocuk, "Actual Loops") ?? 1;
            double ebeveynDongu = Sayi(ebeveyn, "Actual Loops") ?? 1;
            if (ebeveynDongu <= 0)
                return 1;                       // ebeveyn hiç çalışmadı → oran anlamsız
            double oran = cocukDongu / ebeveynDongu;
            return oran > 1 ? oran : 1;         // 1'in altına inmez (paralel kalıntısı)
        }

        // TAHMİNİ planda sayaç yok: yalnız Nested Loop iç tarafı yeniden çalışır
        if (Metin(ebeveyn, "Node Type") == "Nested Loop"
            && Metin(cocuk, "Parent Relationship") == "Inner")
        {
            JsonElement dis = kardesler.FirstOrDefault(k => Metin(k, "Parent Relationship") == "Outer");
            if (dis.ValueKind == JsonValueKind.Object && Sayi(dis, "Plan Rows") is { } satir && satir > 0)
                return satir;
        }

        return 1;
    }

    /// <summary>
    /// Çocuk ana veri akışına mı ait? <c>Parent Relationship</c> Outer/Inner/Member ise evet;
    /// InitPlan/SubPlan (ve CTE kökü) ayrı hesaplanır ve ebeveynin maliyetine dahil DEĞİLDİR.
    /// Bunları normal çocuk saymak ebeveynin kendi maliyetini negatife düşürürdü.
    /// </summary>
    private static bool AnaAkisCocugu(JsonElement cocuk)
        => Metin(cocuk, "Parent Relationship") is not { } iliski
           || iliski is "Outer" or "Inner" or "Member";

    /// <summary>Düğüm adı + ayırt edici nitelikler: "Hash Join (Inner)", "Aggregate (Hashed)".</summary>
    private static string IslemAdi(JsonElement dugum)
    {
        string ad = Metin(dugum, "Node Type") ?? "(bilinmeyen)";

        var ekler = new List<string>();
        if (Metin(dugum, "Join Type") is { } join)
            ekler.Add(join);
        if (Metin(dugum, "Strategy") is { } strateji && strateji != "Plain")
            ekler.Add(strateji);
        if (Metin(dugum, "Partial Mode") is { } kismi && kismi != "Simple")
            ekler.Add(kismi);
        if (Metin(dugum, "Operation") is { } islem)
            ekler.Add(islem);
        if (Metin(dugum, "Scan Direction") is { } yon && yon == "Backward")
            ekler.Add("geriye");

        return ekler.Count == 0 ? ad : $"{ad} ({string.Join(", ", ekler)})";
    }

    /// <summary>Üstünde çalıştığı nesne + varsa koşul özeti.</summary>
    private static string? Ayrinti(JsonElement dugum)
    {
        var parcalar = new List<string>();

        // Şema yalnız VERBOSE ile gelir; yoksa yalnız tablo adı yazılır
        string? tablo = Metin(dugum, "Relation Name");
        if (tablo is not null)
        {
            string? sema = Metin(dugum, "Schema");
            parcalar.Add(sema is null ? tablo : $"{sema}.{tablo}");
        }
        if (Metin(dugum, "Index Name") is { } index)
            parcalar.Add(index);
        if (Metin(dugum, "CTE Name") is { } cte)
            parcalar.Add($"CTE {cte}");
        if (Metin(dugum, "Subplan Name") is { } alt)
            parcalar.Add(alt);
        if (Metin(dugum, "Index Cond") is { } indexKosul)
            parcalar.Add(indexKosul);
        else if (Metin(dugum, "Hash Cond") is { } hashKosul)
            parcalar.Add(hashKosul);
        else if (Metin(dugum, "Filter") is { } filtre)
            parcalar.Add(filtre);

        return parcalar.Count == 0 ? null : string.Join(" · ", parcalar);
    }

    /// <summary>
    /// PostgreSQL planında hazır "uyarı" alanı YOKTUR — sinyallerden türetilir.
    /// Bu liste boş kalırsa aracın en değerli çıktısı (diske taşma, kaçırılan paralellik,
    /// aşırı eleme) sessizce kaybolur.
    /// </summary>
    private static IEnumerable<string> Uyarilar(JsonElement dugum)
    {
        if (Metin(dugum, "Sort Space Type") == "Disk")
            yield return "Sıralama diske taştı (work_mem yetmedi).";
        if (Metin(dugum, "Sort Method") is { } yontem && yontem.Contains("external", StringComparison.OrdinalIgnoreCase))
            yield return $"Sıralama disk kullandı: {yontem}.";
        if (Sayi(dugum, "Hash Batches") is > 1)
            yield return "Hash için bellek yetmedi, diske bölündü.";
        if (Sayi(dugum, "Disk Usage") is > 0)
            yield return "Hash aggregate diske taştı.";
        if (Sayi(dugum, "Lossy Heap Blocks") is > 0)
            yield return "Bitmap tarama bellek sınırına takıldı (kayıplı bloklar).";
        if (Sayi(dugum, "Heap Fetches") is > 0)
            yield return "Index-only scan heap'e iniyor — VACUUM gerekebilir.";

        if (Sayi(dugum, "Workers Planned") is { } planlanan
            && Sayi(dugum, "Workers Launched") is { } baslatilan
            && baslatilan < planlanan)
        {
            yield return $"Planlanan paralellik sağlanamadı ({baslatilan}/{planlanan} işçi).";
        }

        // Aşırı eleme: filtre çok satır atıyorsa index adayıdır (elenen de döngü başınadır)
        double dongu = Sayi(dugum, "Actual Loops") ?? 1;
        if (Sayi(dugum, "Rows Removed by Filter") is { } elenen && elenen > 0 && dongu > 0)
        {
            double elenenToplam = elenen * dongu;
            double donen = (Sayi(dugum, "Actual Rows") ?? 0) * dongu;
            if (elenenToplam >= 1000 && elenenToplam > donen * 9)
                yield return $"Filtre {elenenToplam:N0} satır eledi, {donen:N0} satır döndü — index adayı.";
        }

        if (Sayi(dugum, "Actual Loops") is 0)
            yield return "Bu düğüm hiç çalıştırılmadı (çalışma anında elendi).";
    }

    /// <summary>Plan başlığı: PG ifade metnini vermez, düğüm ve süre bilgisinden özet kurulur.</summary>
    private static string OzetBasligi(JsonElement sarmalayici, JsonElement kokDugum)
    {
        string ad = IslemAdi(kokDugum);
        var ekler = new List<string>();
        if (Sayi(sarmalayici, "Planning Time") is { } planlama)
            ekler.Add($"planlama {planlama:N2} ms");
        if (Sayi(sarmalayici, "Execution Time") is { } yurutme)
            ekler.Add($"yürütme {yurutme:N2} ms");

        return ekler.Count == 0 ? ad : $"{ad} — {string.Join(" · ", ekler)}";
    }

    private static string? Metin(JsonElement dugum, string ad)
        => dugum.TryGetProperty(ad, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    /// <summary>
    /// Sayı okuma: JSON sayıları daima invariant'tır. <c>double</c> okunur — PG kesirli
    /// satır sayısı basabilir ve <c>int</c>'e cast etmek sessizce yuvarlardı.
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
}
