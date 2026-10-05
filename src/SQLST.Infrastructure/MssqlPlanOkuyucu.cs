using System.Globalization;
using System.Xml.Linq;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// SQL Server showplan XML → motor-nötr <see cref="SorguPlani"/> (V5-S1).
///
/// Aynı çözümleyici hem TAHMİNİ (<c>SET SHOWPLAN_XML ON</c> — sorgu çalışmaz) hem GERÇEK
/// (<c>SET STATISTICS XML ON</c> — sorgu çalışır, gerçek satır sayıları gelir) planı okur;
/// ikisi de aynı şemadır, gerçek planda ek olarak <c>RunTimeInformation</c> bulunur.
///
/// İki incelik:
/// <list type="bullet">
/// <item><b>Maliyet kümülatiftir.</b> XML'deki <c>EstimatedTotalSubtreeCost</c> alt ağacın
/// TOPLAMIDIR. SSMS'in gösterdiği yüzde ise düğümün KENDİ payıdır:
/// <c>kendi = altAğaç − Σ(çocukların altAğaçları)</c>. Kümülatifi yüzde sanmak, kökü
/// daima %100 gösterip planı işe yaramaz kılar.</item>
/// <item><b>Çocuklar operatöre özgü elemanın içinde yuvalanır</b> (<c>&lt;Hash&gt;</c>,
/// <c>&lt;NestedLoops&gt;</c>, <c>&lt;IndexScan&gt;</c>…), doğrudan çocuk değildir. Bu yüzden
/// "en yakın RelOp atası ben olan RelOp'lar" diye aranır.</item>
/// </list>
/// </summary>
public static class MssqlPlanOkuyucu
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>Showplan XML'i çözümler. Geçersiz/boş XML'de açık hata — sessizce boş plan dönmez.</summary>
    public static SorguPlani Coz(string xml, bool gercek)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException("Plan XML'i boş — sunucu plan döndürmedi.");

        XDocument belge;
        try
        {
            belge = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException($"Plan XML'i çözümlenemedi: {ex.Message}", ex);
        }

        var ifadeler = new List<IfadePlani>();
        foreach (XElement stmt in belge.Descendants().Where(e => e.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal)))
        {
            XElement? planKoku = stmt.Element(Ns + "QueryPlan")?.Elements(Ns + "RelOp").FirstOrDefault();

            // KAPSAYICI ifadeler atlanır (StmtCond = IF/WHILE, StmtCursor). Onların planı
            // doğrudan çocuklarında değil <Condition>/<CursorPlan> altındaki iç StmtSimple'da
            // durur; buraya alınırlarsa boş metinli, maliyeti 0, ağacı boş bir HAYALET ifade
            // üretiyorlardı — üstelik Descendants ile okunan eksik index önerileri hem
            // hayalette hem gerçek ifadede İKİ KEZ listeleniyordu. Ağaçta önce kapsayıcı
            // geldiği için sekme de boş ağaçla açılıyordu (A1/B1 bulgusu, 2026-07-19).
            if (planKoku is null && stmt.Descendants(Ns + "QueryPlan").Any())
                continue;

            double toplam = Sayi(stmt.Attribute("StatementSubTreeCost"))
                            ?? (planKoku is null ? 0 : AltAgacMaliyeti(planKoku));

            PlanDugumu? kok = planKoku is null ? null : DugumeCevir(planKoku, toplam);

            ifadeler.Add(new IfadePlani(
                IfadeMetni: (stmt.Attribute("StatementText")?.Value ?? "").Trim(),
                ToplamMaliyet: toplam,
                Kok: kok,
                EksikIndexler: EksikIndexleriOku(stmt)));
        }

        if (ifadeler.Count == 0)
            throw new InvalidOperationException("Plan XML'inde ifade bulunamadı.");

        return new SorguPlani(gercek, ifadeler);
    }

    private static PlanDugumu DugumeCevir(XElement relOp, double toplamMaliyet)
    {
        List<XElement> cocukElemanlar = [.. DogrudanCocukRelOplar(relOp)];
        List<PlanDugumu> cocuklar = [.. cocukElemanlar.Select(c => DugumeCevir(c, toplamMaliyet))];

        // Kendi maliyeti = alt ağaç − çocukların alt ağaçları (SSMS'in yüzdesi bu)
        double kendi = AltAgacMaliyeti(relOp) - cocukElemanlar.Sum(AltAgacMaliyeti);
        if (kendi < 0)
            kendi = 0;                                   // yuvarlama gürültüsü negatife düşürebilir

        // Sözleşme (PlanModelleri: "0-100") burada da kilitlenir. StatementSubTreeCost ile
        // kök RelOp'un EstimatedTotalSubtreeCost'u sunucu tarafından AYRI üretilir ve her
        // zaman birbirini tutmaz; tutmadığında yüzde 100'ü aşıp ekrana ham basılıyordu.
        // Diğer üç okuyucu zaten clamp'liydi, yalnız bu değildi (A1/B1, 2026-07-19).
        double yuzde = toplamMaliyet > 0 ? Math.Clamp(kendi / toplamMaliyet * 100, 0, 100) : 0;

        return new PlanDugumu(
            Islem: relOp.Attribute("PhysicalOp")?.Value ?? "(bilinmeyen)",
            Ayrinti: Ayrinti(relOp),
            MaliyetYuzdesi: yuzde,
            TahminiSatir: Sayi(relOp.Attribute("EstimateRows")) ?? 0,
            GercekSatir: GercekSatir(relOp),
            Uyarilar: UyarilariOku(relOp),
            Cocuklar: cocuklar);
    }

    /// <summary>Çocuklar operatöre özgü elemanın içinde yuvalanır → "en yakın RelOp atası bu olanlar".</summary>
    private static IEnumerable<XElement> DogrudanCocukRelOplar(XElement relOp)
        => relOp.Descendants(Ns + "RelOp")
                .Where(d => d.Ancestors(Ns + "RelOp").FirstOrDefault() == relOp);

    private static double AltAgacMaliyeti(XElement relOp)
        => Sayi(relOp.Attribute("EstimatedTotalSubtreeCost")) ?? 0;

    /// <summary>Operatörün üstünde çalıştığı nesne: [db].[şema].[tablo].[index] → okunur kısa ad.</summary>
    private static string? Ayrinti(XElement relOp)
    {
        XElement? nesne = relOp.Descendants(Ns + "Object")
            .FirstOrDefault(o => o.Ancestors(Ns + "RelOp").FirstOrDefault() == relOp);
        if (nesne is null)
            return null;

        string? tablo = nesne.Attribute("Table")?.Value;
        string? index = nesne.Attribute("Index")?.Value;
        string? sema = nesne.Attribute("Schema")?.Value;

        if (tablo is null)
            return index;

        string ad = sema is null ? tablo : $"{sema}.{tablo}";
        return index is null ? ad : $"{ad} · {index}";
    }

    /// <summary>
    /// Gerçek satır sayısı: iş parçacığı başına sayaçların TOPLAMI (paralel planda birden çok
    /// satır olur; yalnız ilkini almak paralel planları olduğundan küçük gösterirdi).
    /// </summary>
    private static double? GercekSatir(XElement relOp)
    {
        List<XElement> sayaclar =
        [
            .. relOp.Descendants(Ns + "RunTimeCountersPerThread")
                    .Where(c => c.Ancestors(Ns + "RelOp").FirstOrDefault() == relOp)
        ];
        if (sayaclar.Count == 0)
            return null;                                 // tahmini plan — gerçek sayaç yok

        return sayaclar.Sum(c => Sayi(c.Attribute("ActualRows")) ?? 0);
    }

    private static IReadOnlyList<string> UyarilariOku(XElement relOp)
    {
        XElement? uyari = relOp.Elements(Ns + "Warnings").FirstOrDefault();
        if (uyari is null)
            return [];

        var liste = new List<string>();

        if (uyari.Attribute("NoJoinPredicate")?.Value is "1" or "true")
            liste.Add("Join koşulu yok — kartezyen çarpım riski.");
        if (uyari.Attribute("SpillToTempDb") is not null || uyari.Elements(Ns + "SpillToTempDb").Any())
            liste.Add("tempdb'ye taşma — bellek yetmedi, disk kullanıldı.");
        if (uyari.Elements(Ns + "ColumnsWithNoStatistics").Any())
            liste.Add("İstatistiği olmayan kolon — tahminler güvenilmez.");

        foreach (XElement d in uyari.Elements(Ns + "PlanAffectingConvert"))
        {
            string ifade = d.Attribute("Expression")?.Value ?? "";
            liste.Add($"Tip dönüşümü planı etkiliyor (index kullanımını engelleyebilir): {ifade}");
        }
        foreach (XElement d in uyari.Elements(Ns + "UnmatchedIndexes"))
            liste.Add("Filtrelenmiş index eşleşmedi.");

        return liste;
    }

    private static IReadOnlyList<PlanEksikIndexi> EksikIndexleriOku(XElement stmt)
    {
        var oneriler = new List<PlanEksikIndexi>();

        foreach (XElement grup in stmt.Descendants(Ns + "MissingIndexGroup"))
        {
            double etki = Sayi(grup.Attribute("Impact")) ?? 0;
            foreach (XElement mi in grup.Elements(Ns + "MissingIndex"))
            {
                string tablo = string.Join(".", new[]
                {
                    mi.Attribute("Schema")?.Value,
                    mi.Attribute("Table")?.Value,
                }.Where(s => !string.IsNullOrEmpty(s)));

                oneriler.Add(new PlanEksikIndexi(
                    Etki: etki,
                    Tablo: tablo,
                    EsitlikKolonlari: Kolonlar(mi, "EQUALITY"),
                    AralikKolonlari: Kolonlar(mi, "INEQUALITY"),
                    DahilKolonlar: Kolonlar(mi, "INCLUDE")));
            }
        }

        return oneriler;
    }

    private static IReadOnlyList<string> Kolonlar(XElement missingIndex, string kullanim)
        => [.. missingIndex.Elements(Ns + "ColumnGroup")
                .Where(g => string.Equals(g.Attribute("Usage")?.Value, kullanim, StringComparison.OrdinalIgnoreCase))
                .SelectMany(g => g.Elements(Ns + "Column"))
                .Select(c => c.Attribute("Name")?.Value ?? "")
                .Where(s => s.Length > 0)];

    /// <summary>Showplan sayıları DAİMA invariant'tır (nokta ondalık) — tr-TR'de Parse yanlış okur.</summary>
    private static double? Sayi(XAttribute? oznitelik)
        => oznitelik is not null
           && double.TryParse(oznitelik.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d : null;
}
