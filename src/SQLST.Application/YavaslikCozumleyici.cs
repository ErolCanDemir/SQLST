using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Bir "neden yavaş?" bulgusunun önemi — rapor bu sırada gösterilir (Yüksek → Orta → Bilgi).</summary>
public enum YavaslikOnem { Yuksek, Orta, Bilgi }

/// <summary>
/// Tek bir "neden yavaş?" bulgusu (BF-7): simge + başlık + Türkçe açıklama + (varsa) İNCELENECEK
/// düzeltme script'i. Script asla kendiliğinden çalıştırılmaz — plan sekmesindeki "incele-kopyala"
/// kuralının aynısı; kullanıcı yeni sekmede görür ve karar verir.
/// </summary>
public sealed record YavaslikBulgusu(
    YavaslikOnem Onem, string Simge, string Baslik, string Aciklama, string? DuzeltmeScript = null);

/// <summary>
/// "Neden yavaş?" raporu (BF-7): bir <see cref="SorguPlani"/>'nin bulgularını tek listede toplar.
/// </summary>
public sealed record YavaslikRaporu(
    bool GercekPlan, double ToplamMaliyet, IReadOnlyList<YavaslikBulgusu> Bulgular)
{
    /// <summary>Bilgi dışında (Yüksek/Orta) en az bir bulgu var mı — "darboğaz bulundu" başlığı için.</summary>
    public bool DarbogazVar => Bulgular.Any(b => b.Onem != YavaslikOnem.Bilgi);
}

/// <summary>
/// BF-7 "Neden yavaş?" tek tuşu: execution plan'ı (tercihen GERÇEK — ölçülü satırlarla) tek bir
/// Türkçe rapora orkestre eder. Planın parçaları zaten vardı (V5 plan sekmesi); bu çözümleyici
/// onları darboğaz sinyallerine çevirir ve önceliklendirir:
///   1) Eksik index (sunucunun önerisi — en somut kazanç, CREATE script'iyle)
///   2) Tam tarama (Scan operatörleri — index'siz okuma)
///   3) İstatistik tazeliği (tahmin/gerçek kardinalite sapması ≥10× — kötü plan seçiminin sık nedeni)
///   4) Sunucu uyarıları (tempdb'ye taşma, örtük tip dönüşümü — index'i devre dışı bırakır)
///   5) En pahalı adım (bilgi çıpası — kullanıcı nereye bakacağını bilsin)
/// SAF: veritabanına dokunmaz, planı verilmiş kabul eder. Motor-nötr (plan zaten ortak ağaca çevrilmiş).
/// </summary>
public static class YavaslikCozumleyici
{
    public static YavaslikRaporu Coz(SorguPlani plan)
    {
        var bulgular = new List<YavaslikBulgusu>();
        double toplamMaliyet = plan.Ifadeler.Sum(i => i.ToplamMaliyet);
        List<PlanDugumu> dugumler = [.. plan.TumDugumler];

        // 1) Eksik index — planın en somut önerisi (etkiye göre azalan).
        foreach (IfadePlani ifade in plan.Ifadeler)
            foreach (PlanEksikIndexi ix in ifade.EksikIndexler.OrderByDescending(x => x.Etki))
            {
                string anahtar = string.Join(", ", ix.EsitlikKolonlari.Concat(ix.AralikKolonlari));
                bulgular.Add(new(YavaslikOnem.Yuksek, "🔑", "Eksik index",
                    $"{ix.Tablo} — sunucu tahmini %{ix.Etki:F0} iyileşme. Anahtar kolonlar: {anahtar}. "
                    + "CREATE INDEX script'i hazır (inceleyip uygulayın; her index yazma maliyeti ekler).",
                    ix.Script()));
            }

        // 2) Tam tarama — maliyeti anlamlı Scan operatörleri (index'siz okuma sinyali).
        foreach (PlanDugumu d in dugumler
            .Where(d => TaramaMi(d.Islem) && d.MaliyetYuzdesi >= 5)
            .OrderByDescending(d => d.MaliyetYuzdesi)
            .Take(3))
        {
            bulgular.Add(new(
                d.MaliyetYuzdesi >= 30 ? YavaslikOnem.Yuksek : YavaslikOnem.Orta, "🔍", "Tam tarama",
                $"{d.Islem}{Ek(d.Ayrinti)} — planın %{d.MaliyetYuzdesi:F0}'i. Uygun bir index yoksa "
                + "satırların tümü okunuyor; WHERE/JOIN kolonlarına index düşünün."));
        }

        // 3) İstatistik tazeliği — tahmin/gerçek sapması (yalnız GERÇEK planda dolu olur).
        foreach (PlanDugumu d in dugumler.Where(d => d.SapmaVar).Take(3))
        {
            bulgular.Add(new(YavaslikOnem.Orta, "📊", "İstatistik tazeliği",
                $"{d.Islem}{Ek(d.Ayrinti)}: optimizer {d.TahminiSatir:N0} satır bekledi, gerçek {d.GercekSatir:N0} "
                + "(≥10× sapma). İstatistikler eskimiş olabilir — bu, yanlış plan seçiminin en sık nedenidir.",
                IstatistikScript(d.Ayrinti)));
        }

        // 4) Sunucu uyarıları — tempdb'ye taşma, örtük tip dönüşümü vb. (Türkçe ipucuyla).
        foreach ((string islem, string uyari) in dugumler
            .SelectMany(d => d.Uyarilar.Select(u => (d.Islem, u)))
            .Distinct())
        {
            bulgular.Add(new(YavaslikOnem.Orta, "⚠", "Sunucu uyarısı",
                $"{islem}: {uyari}{UyariIpucu(uyari)}"));
        }

        // 5) En pahalı adım — bilgi çıpası (nereye bakılacağı).
        PlanDugumu? pahali = dugumler
            .Where(d => d.MaliyetYuzdesi > 0)
            .OrderByDescending(d => d.MaliyetYuzdesi)
            .FirstOrDefault();
        if (pahali is not null)
            bulgular.Add(new(YavaslikOnem.Bilgi, "🐢", "En pahalı adım",
                $"{pahali.Islem}{Ek(pahali.Ayrinti)} — planın %{pahali.MaliyetYuzdesi:F0}'i."));

        // 6) Tahmini plan uyarısı — istatistik sinyali ancak çalıştırınca ölçülür.
        if (!plan.Gercek)
            bulgular.Add(new(YavaslikOnem.Bilgi, "ℹ", "Tahmini plan",
                "Bu TAHMİNİ plandır; istatistik tazeliği (tahmin/gerçek sapması) ancak sorgu "
                + "ÇALIŞTIRILARAK ölçülür. Ölçülü sinyal için gerçek planı alın."));

        // Yüksek → Orta → Bilgi sırası (enum sırası bu şekilde tanımlı).
        return new YavaslikRaporu(plan.Gercek, toplamMaliyet, [.. bulgular.OrderBy(b => (int)b.Onem)]);
    }

    /// <summary>Rapor metni ("kopyala" için): başlık + her bulgu satır satır.</summary>
    public static string RaporMetni(YavaslikRaporu rapor)
    {
        IEnumerable<string> satirlar = rapor.Bulgular.Select(b =>
            $"{b.Simge} [{OnemAdi(b.Onem)}] {b.Baslik}: {b.Aciklama}");
        string bas = rapor.DarbogazVar
            ? "Neden yavaş? — olası darboğazlar:"
            : "Neden yavaş? — belirgin bir darboğaz görünmüyor.";
        return bas + Environment.NewLine + string.Join(Environment.NewLine, satirlar);
    }

    /// <summary>
    /// Gerçek TAM okuma (index'siz/sıralı) mı — motor-nötr (kod inceleme 2026-07-27). Operatör adları
    /// motora göre değişir, "Scan" içeren her şey tam tarama DEĞİL:
    ///   • İNDEKS TABANLI erişimler HARİÇ (yanlış "index ekle" uyarısı olmasın): seek/lookup ·
    ///     PostgreSQL "Index Only Scan"/"Bitmap …" · Oracle "INDEX (UNIQUE/RANGE SCAN)" ·
    ///     MySQL "… araması [ref/eq_ref]" · "Constant Scan" (veri okumaz).
    ///   • TAM okuma sinyalleri: MSSQL "Table/Clustered Index/Index Scan" · PostgreSQL "Seq Scan" ·
    ///     Oracle "TABLE ACCESS (FULL)" · MySQL "Tam tablo taraması [ALL]".
    /// </summary>
    private static bool TaramaMi(string islem)
    {
        string u = islem.ToUpperInvariant();
        if (u.Contains("SEEK") || u.Contains("ONLY") || u.Contains("BITMAP")
            || u.Contains("INDEX (") || u.Contains("ARAMASI") || u.Contains("CONSTANT"))
            return false;
        return u.Contains("SCAN") || u.Contains("FULL") || u.Contains("TAM TABLO");
    }

    private static string Ek(string? ayrinti) => string.IsNullOrWhiteSpace(ayrinti) ? "" : $" ({ayrinti})";

    private static string OnemAdi(YavaslikOnem o) => o switch
    {
        YavaslikOnem.Yuksek => "Yüksek",
        YavaslikOnem.Orta => "Orta",
        _ => "Bilgi",
    };

    private static string UyariIpucu(string uyari)
    {
        string u = uyari.ToLowerInvariant();
        if (u.Contains("conver") || u.Contains("dönüş") || u.Contains("implicit") || u.Contains("cast") || u.Contains("tip"))
            return "  → Örtük tip dönüşümü index'i devre dışı bırakabilir; kolonu doğru tiple karşılaştırın.";
        if (u.Contains("tempdb") || u.Contains("spill") || u.Contains("taşma"))
            return "  → tempdb'ye taşma: sıralama/hash için bellek yetmedi; index ya da daha seçici filtre yardımcı olur.";
        return "";
    }

    /// <summary>İstatistik tazeleme şablonu — nesne adı çözülebiliyorsa gömer, çözülemezse elle doldurulur.</summary>
    private static string IstatistikScript(string? ayrinti)
    {
        string not = string.IsNullOrWhiteSpace(ayrinti) ? "" : $"-- İlgili nesne: {ayrinti}{Environment.NewLine}";
        return not
            + "-- İstatistikleri tazele (kötü kardinalite tahmininin en sık çözümü). Tablo adını yazın:"
            + Environment.NewLine
            + "UPDATE STATISTICS <sema>.<tablo> WITH FULLSCAN;";
    }
}
