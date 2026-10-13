using System.Globalization;
using System.Text.RegularExpressions;
using SQLST.Contracts;

namespace SQLST.Application;

public enum QsGorunum { EnPahali, Gerileyen, Zorlanmis }
public enum QsMetrik { Sure, Cpu, Okuma, Yurutme }
public enum QsIstatistik { Toplam, Ortalama, EnYuksek }
public enum QsPencere { Saat1, Saat24, Gun7, Gun30, Tumu }

/// <summary>sys.database_query_store_options + izinler — bant/düğme görünürlüğü bundan türer (K2/K7).</summary>
public sealed record QsDurum(
    int SurumMajor, string Durum, int ReadonlyReason, string YakalamaModu, int MaxMb, int CurMb, int AralikDk,
    int SorguSayisi, int PlanSayisi, bool OkumaIzni, bool AlterIzni)
{
    public bool Destekli => SurumMajor >= 13;
    public bool Acik => Durum is "READ_WRITE";
    public bool SaltOkunur => Durum is "READ_ONLY";
    public bool Kapali => Durum is "OFF";

    /// <summary>readonly_reason bit maskesi (belgeler): 1 DB salt-okunur · 2 tek kullanıcı · 4 acil mod · 8 ikincil kopya ·
    /// 65536 depo dolu · 131072 bellek içi kuyruk dolu · 262144 iş yükü eşiği · 524288 disk alanı.</summary>
    public string ReadonlyNedeni => ReadonlyReason switch
    {
        0 => "",
        65536 => "depo dolu (MAX_STORAGE_SIZE_MB'ye ulaşıldı)",
        131072 => "bellek içi kuyruk dolu",
        262144 => "iş yükü eşiği aşıldı (otomatik koruma)",
        524288 => "disk alanı yetersiz",
        1 => "veritabanı salt-okunur",
        2 => "veritabanı tek kullanıcı modunda",
        4 => "veritabanı acil (emergency) modda",
        8 => "ikincil kopya (AG/okuma amaçlı)",
        _ => "neden kodu " + ReadonlyReason.ToString(CultureInfo.InvariantCulture),
    };
}

public sealed record QsSorgu(
    long QueryId, string Sql, string? Nesne, long Yurutme, double ToplamMs, double OrtMs, double EnYuksekMs,
    double CpuToplamMs, double OkumaToplam, int PlanSayisi, bool Zorlanmis, bool ZorlamaHatasi, DateTime? SonCalisma,
    double? OncekiOrtMs = null, bool PlanDegisti = false);

public sealed record QsPlan(long PlanId, long Yurutme, double OrtMs, double EnYuksekMs, DateTime? SonCalisma, bool Zorlanmis, string? ZorlamaHatasi);

/// <summary>
/// 📈 Query Store SAF çekirdeği (v23-S28 S1 — docs/14-query-store-arastirma.md). Sorgu metinleri (durum, en pahalı,
/// gerileyen, zorlanmış, planlar, plan XML'i, aç/zorla/kaldır/flush), ayrıştırma ve yardımcılar. Süreler Query Store'da
/// mikrosaniye — burada ms'ye çevrilir. WPF yalnız gösterir.
/// </summary>
public static class QueryStoreSorgulari
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string OnerilenAcmaSecenekleri = "OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = AUTO, MAX_STORAGE_SIZE_MB = 1000, INTERVAL_LENGTH_MINUTES = 60";

    // ---- durum ----

    /// <summary>Sürüm + izinler (her sürümde çalışır) — 2014 ve altında QS görünümleri yok, önce bu okunur.</summary>
    public static string SurumSql() => """
        SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS surum,
               ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DATABASE STATE'), 0) AS okuma_izni,
               ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER'), 0) AS alter_izni;
        """;

    /// <summary>Query Store seçenekleri + sayılar (DB bağlamında, 2016+).</summary>
    public static string DurumSql() => """
        SELECT o.actual_state_desc AS durum, o.readonly_reason, o.query_capture_mode_desc AS yakalama,
               o.max_storage_size_mb AS max_mb, o.current_storage_size_mb AS cur_mb, o.interval_length_minutes AS aralik_dk,
               (SELECT COUNT(*) FROM sys.query_store_query) AS sorgu_sayisi,
               (SELECT COUNT(*) FROM sys.query_store_plan) AS plan_sayisi
        FROM sys.database_query_store_options o;
        """;

    public static QsDurum? DurumOku(QueryResult surum, QueryResult? durum)
    {
        if (surum.ResultSetler.FirstOrDefault() is not { Satirlar.Count: > 0 } rs)
            return null;
        YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, rs.Satirlar[0]);
        int major = s.Int("surum");
        bool okuma = s.Bool("okuma_izni"), alter = s.Bool("alter_izni");
        if (durum?.ResultSetler.FirstOrDefault() is not { Satirlar.Count: > 0 } ds)
            return new QsDurum(major, major >= 13 ? "?" : "YOK", 0, "", 0, 0, 0, 0, 0, okuma, alter);
        YedekSorgulari.Okuyucu d = YedekSorgulari.Satir(ds, ds.Satirlar[0]);
        return new QsDurum(major, d.Metin("durum"), d.Int("readonly_reason"), d.Metin("yakalama"), d.Int("max_mb"), d.Int("cur_mb"),
            d.Int("aralik_dk"), d.Int("sorgu_sayisi"), d.Int("plan_sayisi"), okuma, alter);
    }

    // ---- pencere / sıralama ----

    public static int? PencereSaat(QsPencere p) => p switch
    {
        QsPencere.Saat1 => 1, QsPencere.Saat24 => 24, QsPencere.Gun7 => 24 * 7, QsPencere.Gun30 => 24 * 30, _ => null,
    };

    public static string PencereAdi(QsPencere p) => p switch
    {
        QsPencere.Saat1 => "son 1 saat", QsPencere.Saat24 => "son 24 saat", QsPencere.Gun7 => "son 7 gün", QsPencere.Gun30 => "son 30 gün", _ => "tüm zamanlar",
    };

    public static string MetrikAdi(QsMetrik m) => m switch { QsMetrik.Cpu => "CPU", QsMetrik.Okuma => "mantıksal okuma", QsMetrik.Yurutme => "yürütme sayısı", _ => "süre" };

    public static string IstatistikAdi(QsIstatistik i) => i switch { QsIstatistik.Ortalama => "ortalama", QsIstatistik.EnYuksek => "en yüksek", _ => "toplam" };

    /// <summary>ORDER BY kolonu: metrik × istatistik (yürütmede istatistik anlamsız).</summary>
    public static string SiralamaKolonu(QsMetrik m, QsIstatistik i) => (m, i) switch
    {
        (QsMetrik.Yurutme, _) => "yurutme",
        (QsMetrik.Sure, QsIstatistik.Ortalama) => "ort_ms",
        (QsMetrik.Sure, QsIstatistik.EnYuksek) => "enyuksek_ms",
        (QsMetrik.Sure, _) => "toplam_ms",
        (QsMetrik.Cpu, QsIstatistik.Ortalama) => "cpu_ort_ms",
        (QsMetrik.Cpu, QsIstatistik.EnYuksek) => "cpu_enyuksek_ms",
        (QsMetrik.Cpu, _) => "cpu_toplam_ms",
        (QsMetrik.Okuma, QsIstatistik.Ortalama) => "okuma_ort",
        (QsMetrik.Okuma, QsIstatistik.EnYuksek) => "okuma_enyuksek",
        (QsMetrik.Okuma, _) => "okuma_toplam",
    };

    private static string PencereKosulu(QsPencere p, string alias = "i")
        => PencereSaat(p) is { } saat ? $"WHERE {alias}.end_time >= DATEADD(hour, -{saat.ToString(Inv)}, SYSDATETIMEOFFSET())" : "";

    private const string SorguToplamaSecim = """
            q.query_id, t.query_sql_text AS sql_metni,
            CASE WHEN q.object_id > 0 THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) END AS nesne,
            SUM(s.count_executions) AS yurutme,
            SUM(s.count_executions * s.avg_duration) / 1000.0 AS toplam_ms,
            SUM(s.count_executions * s.avg_duration) / NULLIF(SUM(s.count_executions), 0) / 1000.0 AS ort_ms,
            MAX(s.max_duration) / 1000.0 AS enyuksek_ms,
            SUM(s.count_executions * s.avg_cpu_time) / 1000.0 AS cpu_toplam_ms,
            SUM(s.count_executions * s.avg_cpu_time) / NULLIF(SUM(s.count_executions), 0) / 1000.0 AS cpu_ort_ms,
            MAX(s.max_cpu_time) / 1000.0 AS cpu_enyuksek_ms,
            SUM(s.count_executions * s.avg_logical_io_reads) AS okuma_toplam,
            SUM(s.count_executions * s.avg_logical_io_reads) / NULLIF(SUM(s.count_executions), 0) AS okuma_ort,
            MAX(s.max_logical_io_reads) AS okuma_enyuksek,
            COUNT(DISTINCT s.plan_id) AS plan_sayisi,
            MAX(CASE WHEN p.is_forced_plan = 1 THEN 1 ELSE 0 END) AS zorlanmis,
            MAX(CASE WHEN p.is_forced_plan = 1 AND p.last_force_failure_reason <> 0 THEN 1 ELSE 0 END) AS zorlama_hatasi,
            MAX(s.last_execution_time) AS son_calisma
        """;

    /// <summary>🔥 En pahalı: penceredeki istatistikler sorgu bazında toplanır, seçilen metrik × istatistiğe göre sıralanır.</summary>
    public static string EnPahaliSql(QsMetrik m, QsIstatistik i, QsPencere p, int adet = 50) => $"""
        WITH s AS (
            SELECT rs.plan_id, rs.count_executions, rs.avg_duration, rs.max_duration, rs.avg_cpu_time, rs.max_cpu_time,
                   rs.avg_logical_io_reads, rs.max_logical_io_reads, rs.last_execution_time
            FROM sys.query_store_runtime_stats rs
            JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
            {PencereKosulu(p)}
        )
        SELECT TOP ({adet.ToString(Inv)})
        {SorguToplamaSecim}
        FROM s
        JOIN sys.query_store_plan p ON p.plan_id = s.plan_id
        JOIN sys.query_store_query q ON q.query_id = p.query_id
        JOIN sys.query_store_query_text t ON t.query_text_id = q.query_text_id
        GROUP BY q.query_id, t.query_sql_text, q.object_id
        ORDER BY {SiralamaKolonu(m, i)} DESC;
        """;

    /// <summary>📉 Gerileyen (K5): son pencere ortalama süresi, pencereden önceki 7 güne göre ≥ %50 ve ≥ 10 ms kötü; son dönemde
    /// daha yeni bir plan koştuysa (max plan_id büyüdü) "plan değişti".</summary>
    public static string GerilenSql(QsPencere p, int adet = 50)
    {
        int saat = PencereSaat(p) ?? 24;
        return $"""
        DECLARE @bas datetimeoffset = DATEADD(hour, -{saat.ToString(Inv)}, SYSDATETIMEOFFSET());
        DECLARE @once datetimeoffset = DATEADD(day, -7, @bas);
        WITH son AS (
            SELECT p.query_id, SUM(rs.count_executions) AS yurutme,
                   SUM(rs.count_executions * rs.avg_duration) / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS ort_ms,
                   SUM(rs.count_executions * rs.avg_duration) / 1000.0 AS toplam_ms, MAX(rs.max_duration) / 1000.0 AS enyuksek_ms,
                   SUM(rs.count_executions * rs.avg_cpu_time) / 1000.0 AS cpu_toplam_ms,
                   SUM(rs.count_executions * rs.avg_logical_io_reads) AS okuma_toplam,
                   COUNT(DISTINCT rs.plan_id) AS plan_sayisi, MAX(rs.plan_id) AS son_plan, MAX(rs.last_execution_time) AS son_calisma
            FROM sys.query_store_runtime_stats rs
            JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
            JOIN sys.query_store_plan p ON p.plan_id = rs.plan_id
            WHERE i.end_time >= @bas
            GROUP BY p.query_id
        ), once AS (
            SELECT p.query_id, SUM(rs.count_executions * rs.avg_duration) / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS ort_ms,
                   MAX(rs.plan_id) AS son_plan
            FROM sys.query_store_runtime_stats rs
            JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
            JOIN sys.query_store_plan p ON p.plan_id = rs.plan_id
            WHERE i.end_time < @bas AND i.end_time >= @once
            GROUP BY p.query_id
        )
        SELECT TOP ({adet.ToString(Inv)}) q.query_id, t.query_sql_text AS sql_metni,
               CASE WHEN q.object_id > 0 THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) END AS nesne,
               son.yurutme, son.toplam_ms, son.ort_ms, son.enyuksek_ms, son.cpu_toplam_ms, son.okuma_toplam, son.plan_sayisi,
               (SELECT MAX(CASE WHEN is_forced_plan = 1 THEN 1 ELSE 0 END) FROM sys.query_store_plan WHERE query_id = q.query_id) AS zorlanmis,
               (SELECT MAX(CASE WHEN is_forced_plan = 1 AND last_force_failure_reason <> 0 THEN 1 ELSE 0 END) FROM sys.query_store_plan WHERE query_id = q.query_id) AS zorlama_hatasi,
               son.son_calisma, once.ort_ms AS onceki_ort_ms,
               CASE WHEN son.son_plan > once.son_plan THEN 1 ELSE 0 END AS plan_degisti
        FROM son
        JOIN once ON once.query_id = son.query_id
        JOIN sys.query_store_query q ON q.query_id = son.query_id
        JOIN sys.query_store_query_text t ON t.query_text_id = q.query_text_id
        WHERE son.ort_ms >= once.ort_ms * 1.5 AND son.ort_ms - once.ort_ms >= 10
        ORDER BY son.ort_ms / NULLIF(once.ort_ms, 0) DESC;
        """;
    }

    /// <summary>📌 Zorlanmış planı olan sorgular (tüm zamanlar) — zorlama hatası varsa rozet kırmızı.</summary>
    public static string ZorlanmisSql(int adet = 200) => $"""
        WITH s AS (
            SELECT rs.plan_id, rs.count_executions, rs.avg_duration, rs.max_duration, rs.avg_cpu_time, rs.max_cpu_time,
                   rs.avg_logical_io_reads, rs.max_logical_io_reads, rs.last_execution_time
            FROM sys.query_store_runtime_stats rs
        )
        SELECT TOP ({adet.ToString(Inv)})
        {SorguToplamaSecim}
        FROM sys.query_store_plan p
        JOIN sys.query_store_query q ON q.query_id = p.query_id
        JOIN sys.query_store_query_text t ON t.query_text_id = q.query_text_id
        LEFT JOIN s ON s.plan_id = p.plan_id
        WHERE q.query_id IN (SELECT query_id FROM sys.query_store_plan WHERE is_forced_plan = 1)
        GROUP BY q.query_id, t.query_sql_text, q.object_id
        ORDER BY toplam_ms DESC;
        """;

    /// <summary>Seçili sorgunun planları (penceredeki istatistikle; istatistiği olmayan plan da listelenir).</summary>
    public static string PlanlarSql(long queryId, QsPencere p) => $"""
        SELECT p.plan_id, ISNULL(SUM(rs.count_executions), 0) AS yurutme,
               SUM(rs.count_executions * rs.avg_duration) / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS ort_ms,
               MAX(rs.max_duration) / 1000.0 AS enyuksek_ms, MAX(rs.last_execution_time) AS son_calisma,
               p.is_forced_plan AS zorlanmis,
               CASE WHEN p.is_forced_plan = 1 AND p.last_force_failure_reason <> 0 THEN p.last_force_failure_reason_desc END AS zorlama_hatasi
        FROM sys.query_store_plan p
        LEFT JOIN (
            SELECT rs.* FROM sys.query_store_runtime_stats rs
            JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
            {PencereKosulu(p)}
        ) rs ON rs.plan_id = p.plan_id
        WHERE p.query_id = {queryId.ToString(Inv)}
        GROUP BY p.plan_id, p.is_forced_plan, p.last_force_failure_reason, p.last_force_failure_reason_desc
        ORDER BY son_calisma DESC;
        """;

    public static string PlanXmlSql(long planId) => $"SELECT CAST(query_plan AS nvarchar(max)) AS plan_xml FROM sys.query_store_plan WHERE plan_id = {planId.ToString(Inv)};";

    // ---- işlemler ----

    public static string AcSql(string veritabani) => $"ALTER DATABASE {YedekSorgulari.Koseli(veritabani)} SET QUERY_STORE = ON ({OnerilenAcmaSecenekleri});";

    public static string FlushSql() => "EXEC sys.sp_query_store_flush_db;";

    public static string ZorlaSql(long queryId, long planId) => $"EXEC sys.sp_query_store_force_plan @query_id = {queryId.ToString(Inv)}, @plan_id = {planId.ToString(Inv)};";

    public static string ZorlamaKaldirSql(long queryId, long planId) => $"EXEC sys.sp_query_store_unforce_plan @query_id = {queryId.ToString(Inv)}, @plan_id = {planId.ToString(Inv)};";

    // ---- okuma ----

    public static IReadOnlyList<QsSorgu> SorgulariOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.LastOrDefault() is not { } rs)
            return [];
        List<QsSorgu> liste = [];
        foreach (object?[] satir in rs.Satirlar)
        {
            YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, satir);
            liste.Add(new QsSorgu(s.Long("query_id"), s.Metin("sql_metni"), s.MetinYaDaNull("nesne"), s.Long("yurutme"),
                s.Double("toplam_ms"), s.Double("ort_ms"), s.Double("enyuksek_ms"), s.Double("cpu_toplam_ms"), s.Double("okuma_toplam"),
                s.Int("plan_sayisi"), s.Bool("zorlanmis"), s.Bool("zorlama_hatasi"), s.Tarih("son_calisma"),
                s.MetinYaDaNull("onceki_ort_ms") is null ? null : s.Double("onceki_ort_ms"), s.Bool("plan_degisti")));
        }
        return liste;
    }

    public static IReadOnlyList<QsPlan> PlanlariOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        List<QsPlan> liste = [];
        foreach (object?[] satir in rs.Satirlar)
        {
            YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, satir);
            liste.Add(new QsPlan(s.Long("plan_id"), s.Long("yurutme"), s.Double("ort_ms"), s.Double("enyuksek_ms"), s.Tarih("son_calisma"),
                s.Bool("zorlanmis"), s.MetinYaDaNull("zorlama_hatasi")));
        }
        return liste;
    }

    public static string? PlanXmlOku(QueryResult sonuc)
        => sonuc.ResultSetler.FirstOrDefault() is { Satirlar.Count: > 0 } rs ? YedekSorgulari.Satir(rs, rs.Satirlar[0]).MetinYaDaNull("plan_xml") : null;

    // ---- metin ----

    /// <summary>Listede tek satır: boşluklar sıkıştırılır, uzunsa kırpılır.</summary>
    public static string KisaSql(string sql, int azami = 140)
    {
        string tek = Regex.Replace(sql.Trim(), @"\s+", " ");
        return tek.Length <= azami ? tek : tek[..(azami - 1)] + "…";
    }

    public static string MsMetni(double ms) => ms switch
    {
        >= 10_000 => (ms / 1000).ToString("#,0.0", CultureInfo.CurrentCulture) + " sn",
        >= 100 => ms.ToString("#,0", CultureInfo.CurrentCulture) + " ms",
        >= 1 => ms.ToString("0.0", CultureInfo.CurrentCulture) + " ms",
        _ => ms.ToString("0.00", CultureInfo.CurrentCulture) + " ms",
    };

    public static string SayiMetni(double n) => n.ToString("#,0", CultureInfo.CurrentCulture);

    /// <summary>Sık hataların Türkçesi.</summary>
    public static string HataMetni(SqlHata hata) => hata.Numara switch
    {
        297 or 300 => "VIEW DATABASE STATE izniniz yok — Query Store'u görmek için bu izin gerekir.",
        5058 => "Query Store ayarı değiştirilemedi: " + hata.Mesaj,
        12404 or 12405 => "Query Store bu veritabanında kapalı — önce açın.",
        12406 => "Plan zorlanamadı: bu plan bu sorguya ait değil ya da Query Store'da yok.",
        208 => "Query Store görünümleri yok — bu sürüm (2014 ve altı) Query Store desteklemiyor.",
        _ => hata.Mesaj,
    };
}
