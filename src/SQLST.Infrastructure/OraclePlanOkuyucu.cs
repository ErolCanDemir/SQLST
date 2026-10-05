using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Oracle <c>PLAN_TABLE</c> satırları → motor-nötr <see cref="SorguPlani"/> (V5-S1d).
///
/// <b>Neden metin ayrıştırmıyoruz:</b> Oracle planı klasik olarak
/// <c>DBMS_XPLAN.DISPLAY</c> ile HİZALANMIŞ METİN olarak gösterilir ve ağaç yapısı yalnız
/// girintiden anlaşılır. Ama <c>EXPLAIN PLAN FOR</c> planı aslında <c>PLAN_TABLE</c>'a YAZAR;
/// orada <c>ID</c>/<c>PARENT_ID</c> sütunlarıyla ağaç <b>zaten yapısaldır</b>. Girinti saymak
/// yerine bu sütunlar okunur — kırılgan metin ayrıştırma hiç yapılmaz.
///
/// <b>Maliyet DİKEY olarak kümülatiftir:</b> <c>COST</c> düğümün kendi alt ağacını kapsar,
/// bu yüzden kendi payı <c>COST − Σ(çocuk COST)</c> ile hesaplanır.
///
/// <b>⚠ AÇIK KUSUR — NESTED LOOPS'ta pay yanlış operatöre yazılıyor (A1/B1, 2026-07-19).</b>
/// Bu başlıkta önce şöyle yazıyordu: *"PostgreSQL'deki 'iterasyonla çarp' tuzağı burada
/// YOKTUR"*. Bu iddia büyük olasılıkla YANLIŞ. Oracle'ın maliyet modeli
/// <c>cost(NL) = cost(dış) + kardinalite(dış) × cost(iç)</c>'tir; yani iç tarafın
/// <c>COST</c>'u <b>tek geçişin</b> maliyetidir, tekrar sayısıyla çarpılmış hâli değil.
/// Kod ise çocuk maliyetini bir kez çıkardığı için tekrarlanan iç işin tamamı
/// <c>NESTED LOOPS</c> düğümüne yazılıyor ve kullanıcı asıl darboğaz olan iç aramayı
/// "bedava" görüyor. Toplam yine ~%100 çıktığı için sayı gözle yanlış görünmüyor.
///
/// <b>Neden düzeltilmedi:</b> doğru çarpanı körlemesine yazmak daha kötüsünü yapabilir —
/// 11g+ "nested loops batching" iki yığılı NL satırı üretir (hangi çocuğun dış taraf olduğu
/// değişir) ve Oracle <c>COST</c>'u tam sayıya yuvarladığından yeniden kurulan toplam
/// tutmayıp negatif-pay koruması devreye girebilir. Canlı Oracle sunucusu yok; düzeltme
/// **canlı doğrulama borcuna** bağlandı. Bu not bilerek burada duruyor: bir sonraki okuyan
/// yanlış bir güvenceyle karşılaşmasın.
///
/// <b>GERÇEK plan bu motorda kapalıdır</b> (<see cref="OracleLehcesi.PlanGercekDestekler"/>):
/// gerçek satır sayıları için sorgunun <c>GATHER_PLAN_STATISTICS</c> ipucuyla çalıştırılıp
/// <c>V$SQL_PLAN_STATISTICS_ALL</c>'ün okunması gerekir — bu görünüm ek yetki ister ve
/// canlı Oracle sunucusu olmadığından doğrulanamaz. Yarım yol açılmadı.
/// </summary>
public static class OraclePlanOkuyucu
{
    /// <summary>
    /// <see cref="OracleLehcesi.PlanOkumaSql"/> ile birebir aynı kolon SIRASI beklenir.
    /// Sıra değişirse burası da değişmeli — bu yüzden ikisi yan yana tutulur.
    /// </summary>
    private const int SutunId = 0, SutunParent = 1, SutunIslem = 2, SutunSecenek = 3,
                      SutunNesneSahibi = 4, SutunNesne = 5, SutunKardinalite = 6,
                      SutunMaliyet = 7, SutunErisim = 8, SutunFiltre = 9;

    public static SorguPlani Coz(QueryResult sonuc, bool gercek)
    {
        if (sonuc.ResultSetler.Count == 0 || sonuc.ResultSetler[0].Satirlar.Count == 0)
        {
            throw new InvalidOperationException(
                "PLAN_TABLE boş — sunucu plan üretmedi (sorgu plan üretmeyen bir ifade olabilir).");
        }

        IReadOnlyList<object?[]> satirlar = sonuc.ResultSetler[0].Satirlar;

        // ID → ham satır; PARENT_ID → çocuk listesi
        var ham = new Dictionary<int, object?[]>();
        var cocukIdleri = new Dictionary<int, List<int>>();
        int? kokId = null;

        foreach (object?[] s in satirlar)
        {
            if (Tam(s, SutunId) is not { } id)
                continue;

            ham[id] = s;

            if (Tam(s, SutunParent) is { } ebeveyn)
            {
                if (!cocukIdleri.TryGetValue(ebeveyn, out List<int>? liste))
                    cocukIdleri[ebeveyn] = liste = [];
                liste.Add(id);
            }
            else
            {
                kokId ??= id;      // PARENT_ID boş olan tek satır köktür (genelde ID = 0)
            }
        }

        if (kokId is null || ham.Count == 0)
            throw new InvalidOperationException("PLAN_TABLE satırlarında kök düğüm bulunamadı.");

        double toplam = Ondalik(ham[kokId.Value], SutunMaliyet) ?? 0;
        PlanDugumu kok = DugumeCevir(kokId.Value, ham, cocukIdleri, toplam, []);

        return new SorguPlani(gercek, [new IfadePlani(
            IfadeMetni: toplam > 0 ? $"Oracle planı — maliyet {toplam:N0}" : "Oracle planı",
            ToplamMaliyet: toplam,
            Kok: kok,
            EksikIndexler: [])]);   // Oracle EXPLAIN PLAN eksik index önermez
    }

    /// <param name="gorulen">
    /// Bu daldaki ID'ler — DÖNGÜ KORUMASI. PLAN_TABLE sıradan, kullanıcının yazabildiği bir
    /// tablodur; <c>parent_id</c> kendini ya da bir atayı gösteren bozuk/elle doldurulmuş bir
    /// satır sonsuz özyineleme yapar. <c>StackOverflowException</c> .NET'te YAKALANAMAZ:
    /// süreç anında ölür ve kullanıcı kaydetmediği editör içeriğini kaybeder (A1/B1, 2026-07-19).
    /// </param>
    private static PlanDugumu DugumeCevir(
        int id, Dictionary<int, object?[]> ham, Dictionary<int, List<int>> cocukIdleri,
        double toplam, HashSet<int> gorulen)
    {
        object?[] satir = ham[id];

        var cocuklar = new List<PlanDugumu>();
        double cocukMaliyeti = 0;
        if (cocukIdleri.TryGetValue(id, out List<int>? liste))
        {
            foreach (int c in liste)
            {
                // Zaten bu dalda olan bir ID'ye geri dönülüyorsa dal burada biter.
                if (!ham.ContainsKey(c) || gorulen.Contains(c) || c == id)
                    continue;

                cocuklar.Add(DugumeCevir(c, ham, cocukIdleri, toplam, [.. gorulen, id]));
                cocukMaliyeti += Ondalik(ham[c], SutunMaliyet) ?? 0;
            }
        }

        // Maliyet KÜMÜLATİF (MSSQL deseni): kendi payı = alt ağaç − çocukların alt ağaçları
        double kendi = (Ondalik(satir, SutunMaliyet) ?? 0) - cocukMaliyeti;
        if (kendi < 0)
            kendi = 0;              // yuvarlama gürültüsü

        return new PlanDugumu(
            Islem: IslemAdi(satir),
            Ayrinti: Ayrinti(satir),
            MaliyetYuzdesi: toplam > 0 ? Math.Clamp(kendi / toplam * 100, 0, 100) : 0,
            // CARDINALITY okunamadıysa NULL geçilir, 0 değil: "sıfır satır bekleniyor" ile
            // "tahmin okunamadı" farklı şeylerdir ve sıfır yazmak uydurma sayı olurdu.
            TahminiSatir: Ondalik(satir, SutunKardinalite),
            GercekSatir: null,      // gerçek plan bu motorda kapalı
            Uyarilar: [.. Uyarilar(satir)],
            Cocuklar: cocuklar);
    }

    /// <summary>OPERATION + OPTIONS: "TABLE ACCESS" + "FULL" → "TABLE ACCESS (FULL)".</summary>
    private static string IslemAdi(object?[] satir)
    {
        string islem = Metin(satir, SutunIslem) ?? "(bilinmeyen)";
        return Metin(satir, SutunSecenek) is { Length: > 0 } secenek
            ? $"{islem} ({secenek})"
            : islem;
    }

    private static string? Ayrinti(object?[] satir)
    {
        var parcalar = new List<string>();

        if (Metin(satir, SutunNesne) is { Length: > 0 } nesne)
        {
            string? sahip = Metin(satir, SutunNesneSahibi);
            parcalar.Add(string.IsNullOrEmpty(sahip) ? nesne : $"{sahip}.{nesne}");
        }
        if (Metin(satir, SutunErisim) is { Length: > 0 } erisim)
            parcalar.Add($"erişim: {erisim}");
        else if (Metin(satir, SutunFiltre) is { Length: > 0 } filtre)
            parcalar.Add($"filtre: {filtre}");

        return parcalar.Count == 0 ? null : string.Join(" · ", parcalar);
    }

    /// <summary>
    /// Oracle da hazır uyarı vermez — klasik tuzaklar işlem adından türetilir.
    /// </summary>
    private static IEnumerable<string> Uyarilar(object?[] satir)
    {
        string islem = (Metin(satir, SutunIslem) ?? "").ToUpperInvariant();
        string secenek = (Metin(satir, SutunSecenek) ?? "").ToUpperInvariant();

        if (islem == "TABLE ACCESS" && secenek == "FULL")
            yield return "Tam tablo taraması — uygun index yoksa büyük tabloda pahalıdır.";
        if (islem == "INDEX" && secenek.Contains("FULL SCAN"))
            yield return "Tam index taraması — aralık daraltılamamış.";
        if (islem.Contains("MERGE JOIN") && secenek == "CARTESIAN")
            yield return "Kartezyen birleştirme — join koşulu eksik olabilir.";
        if (islem == "SORT" && secenek.Contains("ORDER BY"))
            yield return "Sıralama adımı — index sırası kullanılamıyor olabilir.";

        // Filtre satır çok eliyorsa index adayıdır (yalnız filtre koşulu varsa anlamlı)
        if (Metin(satir, SutunFiltre) is { Length: > 0 }
            && Ondalik(satir, SutunKardinalite) is { } kart && kart <= 1)
        {
            yield return "Filtre neredeyse tüm satırları eliyor — index adayı olabilir.";
        }
    }

    private static string? Metin(object?[] satir, int sutun)
        => sutun < satir.Length ? satir[sutun]?.ToString() : null;

    /// <summary>
    /// Oracle sayıları sürücüden <c>decimal</c> gelir (NUMBER); <c>int</c>'e cast etmek
    /// yerine güvenli dönüşüm yapılır. Boş (NULL) değer normaldir — ör. kök PARENT_ID.
    /// </summary>
    // Convert.To* beklenmedik bir kolon tipinde FormatException/InvalidCastException/
    // OverflowException atar. Plan yolu yalnız InvalidOperationException yakaladığından
    // bunlar "Plan alınamadı: …" yerine UYGULAMA ÇÖKMESİ olurdu. Sürücü/PLAN_TABLE tipi
    // beklenenden farklıysa o alanı yok saymak doğru davranış (A1/B1, 2026-07-19).

    private static int? Tam(object?[] satir, int sutun)
    {
        if (sutun >= satir.Length || satir[sutun] is not { } d || d is DBNull)
            return null;
        try { return Convert.ToInt32(d, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static double? Ondalik(object?[] satir, int sutun)
    {
        if (sutun >= satir.Length || satir[sutun] is not { } d || d is DBNull)
            return null;
        try { return Convert.ToDouble(d, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
