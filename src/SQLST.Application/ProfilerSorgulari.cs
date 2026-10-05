using System.Globalization;
using System.Xml.Linq;

namespace SQLST.Application;

/// <summary>Profiler şablonları (v23-S1) — SSMS Profiler'ın hazır şablonlarının Türkçe karşılığı.</summary>
public enum ProfilerSablonu
{
    /// <summary>Batch + RPC tamamlanmaları (Profiler "Standard" karşılığı).</summary>
    Standart,

    /// <summary>Yalnız süresi eşiği aşan Batch/RPC'ler (Profiler "TSQL_Duration" ruhu).</summary>
    YavasSorgular,

    /// <summary>error_reported (önem ≥ 11) — kullanıcıya dönen gerçek hatalar.</summary>
    Hatalar,

    /// <summary>xml_deadlock_report (v23-S3) — kilitlenmeler; olayın metni deadlock XML'idir,
    /// görsel şema o XML'den çizilir (SSMS'in .xdl deneyiminin gömülüsü).</summary>
    Deadlock,
}

/// <summary>Akıştaki tek olay: ring_buffer XML'inden çözülmüş satır (v23-S1).</summary>
/// <param name="Zaman">Olayın YEREL zamanı (XML UTC verir, çözücü çevirir).</param>
/// <param name="SureMs">duration alanı µs gelir — çözücü ms'e indirir (error_reported'da 0).</param>
public sealed record ProfilerOlayi(
    DateTime Zaman, string Olay, long SureMs, long CpuMs, long Reads, long Writes,
    long SatirSayisi, string? Metin, string? Veritabani, string? Uygulama,
    string? Kullanici, string? Makine, int Spid);

/// <summary>
/// 🔍 Profiler'ın SAF çekirdeği (v23-S1 — araştırma: docs/09-profiler-arastirma.md; kullanıcı
/// kararları K1-K4): SSMS Profiler DEPRECATED olduğundan izleme <b>Extended Events</b> üstünde —
/// bu sınıf oturum DDL'lerini üretir ve ring_buffer hedefinin XML'ini satırlara çözer. UI/IO yok;
/// canlılık, VM'in bu sorguları aralıklı koşturmasıyla sağlanır (K2: ring_buffer sorgulama —
/// sürücü bağımlılığı yok, her sürümde çalışır).
///
/// YETKİ: oturum DDL'leri sunucuda <c>ALTER ANY EVENT SESSION</c> ister — yoksa çağıran, gelen
/// SQL hatasını kullanıcıya Türkçe yönlendirmeyle gösterir (VM yapar).
/// </summary>
public static class ProfilerSorgulari
{
    /// <summary>Sunucudaki oturum adı — SABİT: aynı makineden ikinci SQLST açılırsa da tek oturum
    /// (Baslat önce siler); elle temizlik gerekirse ad bilinir olsun.</summary>
    public const string OturumAdi = "SQLST_Profiler";

    /// <summary>Olaylara eklenen ortak ACTION listesi (kim/nereden/hangi DB bağlamı).</summary>
    private const string Eylemler =
        "ACTION(sqlserver.client_app_name, sqlserver.client_hostname, "
        + "sqlserver.database_name, sqlserver.username, sqlserver.session_id)";

    /// <summary>
    /// Oturumu şablona göre kurar. <paramref name="esikMs"/> yalnız <see cref="ProfilerSablonu.YavasSorgular"/>'da
    /// kullanılır (duration XEvents'te µs — çevirme burada). <paramref name="veritabani"/> verilirse
    /// sunucu tarafında süzülür (boş/null = tüm veritabanları). Ad tek tırnak kaçışıyla gömülür.
    /// </summary>
    public static string OturumOlustur(ProfilerSablonu sablon, int esikMs = 1000, string? veritabani = null)
    {
        string dbKosul = string.IsNullOrWhiteSpace(veritabani)
            ? ""
            : $"[sqlserver].[database_name] = N'{veritabani.Replace("'", "''")}'";

        static string Nerede(params string[] kosullar)
        {
            string[] dolu = [.. kosullar.Where(k => k.Length > 0)];
            return dolu.Length == 0 ? "" : $" WHERE ({string.Join(" AND ", dolu)})";
        }

        string olaylar = sablon switch
        {
            // Deadlock raporu sunucu geneli bir olaydır — veritabanı/eşik süzgeci uygulanmaz
            // (raporun içinde her iki sürecin bağlamı zaten vardır).
            ProfilerSablonu.Deadlock => "ADD EVENT sqlserver.xml_deadlock_report()",
            ProfilerSablonu.Hatalar =>
                // Önem >= 11: kullanıcıya dönen gerçek hatalar (bilgi mesajları elenir).
                $"ADD EVENT sqlserver.error_reported({Eylemler}{Nerede("severity >= 11", dbKosul)})",
            ProfilerSablonu.YavasSorgular =>
                $"ADD EVENT sqlserver.sql_batch_completed({Eylemler}{Nerede($"duration >= {(long)esikMs * 1000}", dbKosul)}),\n"
                + $"ADD EVENT sqlserver.rpc_completed({Eylemler}{Nerede($"duration >= {(long)esikMs * 1000}", dbKosul)})",
            _ =>
                $"ADD EVENT sqlserver.sql_batch_completed({Eylemler}{Nerede(dbKosul)}),\n"
                + $"ADD EVENT sqlserver.rpc_completed({Eylemler}{Nerede(dbKosul)})",
        };

        // ring_buffer 4 MB: ~binlerce olay tutar; ALLOW_SINGLE_EVENT_LOSS + 3 sn dispatch =
        // izleme yükü sunucuyu ezmesin (Profiler'ın canlı akışının aksine — araştırma A4).
        return $"""
            CREATE EVENT SESSION [{OturumAdi}] ON SERVER
            {olaylar}
            ADD TARGET package0.ring_buffer(SET max_memory = 4096)
            WITH (MAX_MEMORY = 8192 KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
                  MAX_DISPATCH_LATENCY = 3 SECONDS, STARTUP_STATE = OFF);
            """;
    }

    /// <summary>Varsa siler (yeniden kurulum + kapanış temizliği — sunucuda oturum bırakmayız).</summary>
    public static string OturumSil() => $"""
        IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'{OturumAdi}')
            DROP EVENT SESSION [{OturumAdi}] ON SERVER;
        """;

    public static string OturumBaslat() => $"ALTER EVENT SESSION [{OturumAdi}] ON SERVER STATE = START;";

    public static string OturumDurdur() => $"ALTER EVENT SESSION [{OturumAdi}] ON SERVER STATE = STOP;";

    /// <summary>ring_buffer içeriğini tek XML kolonu olarak okur (oturum çalışmıyorsa 0 satır).</summary>
    public static string RingBufferOku() => $"""
        SELECT CAST(t.target_data AS nvarchar(max)) AS veri
        FROM sys.dm_xe_sessions s
        JOIN sys.dm_xe_session_targets t ON t.event_session_address = s.address
        WHERE s.name = N'{OturumAdi}' AND t.target_name = N'ring_buffer';
        """;

    /// <summary>
    /// ring_buffer XML'ini olay satırlarına çözer. Ring buffer HER okuyuşta baştan beri tuttuğu
    /// olayların TÜMÜNÜ verir — artımlı akış için <paramref name="sonZamandanSonra"/> (UTC değil,
    /// çözülmüş YEREL zaman) verilir ve yalnız ondan yenileri döner. Aynı mikrosaniyeye düşen iki
    /// olayın sınır durumunda tek kayıp olabilir (bilinçli dar sınır — µs çakışması pratikte yok).
    /// Bozuk/eksik düğüm satırı atlanır: izleme akışı tek kötü olayla ölmez.
    /// </summary>
    public static IReadOnlyList<ProfilerOlayi> RingBufferCoz(string? xml, DateTime? sonZamandanSonra = null)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return [];
        XDocument belge;
        try { belge = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return []; }

        var sonuc = new List<ProfilerOlayi>();
        foreach (XElement e in belge.Descendants("event"))
        {
            ProfilerOlayi? olay = OlayCoz(e);
            if (olay is null)
                continue;
            if (sonZamandanSonra is { } esik && olay.Zaman <= esik)
                continue;
            sonuc.Add(olay);
        }
        return sonuc;
    }

    private static ProfilerOlayi? OlayCoz(XElement e)
    {
        string? ad = e.Attribute("name")?.Value;
        string? zamanMetni = e.Attribute("timestamp")?.Value;
        if (ad is null || zamanMetni is null)
            return null;
        if (!DateTime.TryParse(zamanMetni, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out DateTime utc))
            return null;

        // <data name="x"><value>..</value></data> ve <action name="y"><value>..</value></action>
        static string? Deger(XElement kok, string dugum, string alan)
            => kok.Elements(dugum).FirstOrDefault(d => d.Attribute("name")?.Value == alan)
                ?.Element("value")?.Value;
        static long Sayi(string? s) => long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out long n) ? n : 0;

        // xml_deadlock_report'un xml_report alanı DÜZ METİN değil GÖMÜLÜ XML'dir: <value> altında
        // <deadlock> ağacı gelir — .Value birleşik metni verirdi (etiketler kaybolurdu), ağacın
        // KENDİSİ string'e yazılır (görsel şema + .xdl kaydı bu XML'den beslenir).
        static string? XmlDeger(XElement kok, string alan)
        {
            XElement? deger = kok.Elements("data")
                .FirstOrDefault(d => d.Attribute("name")?.Value == alan)?.Element("value");
            return deger?.Elements().FirstOrDefault()?.ToString() ?? deger?.Value;
        }

        string? metin = ad switch
        {
            "sql_batch_completed" => Deger(e, "data", "batch_text"),
            "rpc_completed" => Deger(e, "data", "statement"),
            "error_reported" =>
                $"Msg {Sayi(Deger(e, "data", "error_number"))}, Önem {Sayi(Deger(e, "data", "severity"))}: "
                + Deger(e, "data", "message"),
            "xml_deadlock_report" => XmlDeger(e, "xml_report"),
            _ => Deger(e, "data", "statement") ?? Deger(e, "data", "batch_text"),
        };

        return new ProfilerOlayi(
            Zaman: DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime(),
            Olay: ad switch
            {
                "sql_batch_completed" => "Batch",
                "rpc_completed" => "RPC",
                "error_reported" => "Hata",
                "xml_deadlock_report" => "Deadlock",
                _ => ad,
            },
            SureMs: Sayi(Deger(e, "data", "duration")) / 1000, // µs → ms
            CpuMs: Sayi(Deger(e, "data", "cpu_time")) / 1000,
            Reads: Sayi(Deger(e, "data", "logical_reads")),
            Writes: Sayi(Deger(e, "data", "writes")),
            SatirSayisi: Sayi(Deger(e, "data", "row_count")),
            Metin: metin,
            Veritabani: Deger(e, "action", "database_name"),
            Uygulama: Deger(e, "action", "client_app_name"),
            Kullanici: Deger(e, "action", "username"),
            Makine: Deger(e, "action", "client_hostname"),
            Spid: (int)Sayi(Deger(e, "action", "session_id")));
    }
}
