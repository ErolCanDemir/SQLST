using System.Globalization;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Agent servisinin bu sunucudaki hâli (rozet + yönlendirme bandı buna göre).</summary>
public enum AgentServisi
{
    /// <summary>Servis durumu okunamadı (VIEW SERVER STATE yok) — job'lar yine listelenir.</summary>
    Bilinmiyor,

    /// <summary>Bu sürümde Agent YOK: Express/LocalDB, Azure SQL Database, Edge…</summary>
    Yok,

    Calisiyor,
    Durmus,
}

/// <summary>Bir koşunun (ya da adımının) sonucu — msdb <c>run_status</c> kodlarıyla birebir.</summary>
public enum AgentSonuc
{
    Hata = 0,
    Basarili = 1,
    YenidenDeneme = 2,
    Iptal = 3,
    Suruyor = 4,
    Bilinmiyor = 5,
}

/// <summary>Durum sorgusunun tek satırı: sürüm + servis + kullanıcının Agent yetkileri.</summary>
public sealed record AgentSunucuDurumu(
    int MotorSurumu, string Surum, string Sunucu, string? ServisDurumu,
    bool Sysadmin, bool Okuyucu, bool Kullanici, bool Operator, DateTime SunucuSaati, string Giris = "")
{
    /// <summary>
    /// <c>SERVERPROPERTY('EngineEdition')</c>: 4 Express (LocalDB dahil) · 5 Azure SQL Database ·
    /// 6/11 Synapse · 9 Azure SQL Edge · 12 Fabric — bunlarda Agent servisi bulunmaz. 8 (Managed
    /// Instance) Agent'lıdır ve servis DMV'si boş döner → çalışıyor sayılır.
    /// </summary>
    public AgentServisi Servis => MotorSurumu is 4 or 5 or 6 or 9 or 11 or 12
        ? AgentServisi.Yok
        : ServisDurumu switch
        {
            "Running" => AgentServisi.Calisiyor,
            null when MotorSurumu == 8 => AgentServisi.Calisiyor,
            null or "" => AgentServisi.Bilinmiyor,
            _ => AgentServisi.Durmus, // Stopped / Paused / Start pending…
        };

    /// <summary>msdb'de job görebilir mi (sysadmin ya da SQLAgent* rollerinden biri).</summary>
    public bool GorebilirMi => Sysadmin || Okuyucu || Kullanici || Operator;

    /// <summary>Tüm job'ları mı görür, yoksa yalnız KENDİ job'larını mı (SQLAgentUserRole)?</summary>
    public bool HepsiniGorurMu => Sysadmin || Okuyucu || Operator;

    public string YetkiMetni => Sysadmin ? "sysadmin"
        : Operator ? "SQLAgentOperatorRole"
        : Okuyucu ? "SQLAgentReaderRole"
        : Kullanici ? "SQLAgentUserRole (yalnız kendi job'larınız)"
        : "Agent yetkisi yok";
}

/// <summary>Job listesinin bir satırı (sp_help_job + geçmiş + aktivite birleşimi).</summary>
public sealed record AgentJob(
    Guid JobId, string Ad, bool Acik, string Kategori, string Sahip, string Aciklama,
    DateTime? SonKosu, AgentSonuc SonSonuc, int? SonSure, int? HataliAdim,
    DateTime? SonrakiKosu, bool Calisiyor, int? CalisanAdim, DateTime? CalismaBaslangici,
    int AdimSayisi, bool ZamanlamaVar,
    string BildirimOperatoru = "", int BildirimSeviyesiEposta = 0, int BildirimSeviyesiOlay = 2,
    int BaslangicAdimi = 1);

/// <summary>Job geçmişinin bir satırı — adım 0 job'un genel sonucudur ("(Job outcome)").</summary>
public sealed record AgentGecmisSatiri(
    int InstanceId, Guid JobId, string JobAdi, int AdimId, string AdimAdi,
    AgentSonuc Sonuc, DateTime? Zaman, int Sure, string Mesaj, int MesajNo, int YenidenDenemeler);

/// <summary>Job adımı (sp_help_jobstep).</summary>
public sealed record AgentAdim(
    int Id, string Ad, string AltSistem, string Komut, string? Veritabani,
    int BasaridaEylem, int BasaridaAdim, int HatadaEylem, int HatadaAdim,
    int YenidenDeneme, int DenemeAraligi, int? ProxyId, string? CiktiDosyasi, int Bayraklar = 0);

/// <summary>Job zamanlaması (sp_help_jobschedule) — msdb <c>freq_*</c> alanlarıyla birebir.</summary>
public sealed record AgentZamanlama(
    int Id, string Ad, bool Acik, int FreqType, int FreqInterval, int SubdayType, int SubdayInterval,
    int RelativeInterval, int RecurrenceFactor, int BaslangicTarihi, int BitisTarihi,
    int BaslangicSaati, int BitisSaati);

/// <summary>
/// ⏱ SQL Server Agent'ın SAF çekirdeği (v23-S16 — araştırma: docs/11-sql-agent-arastirma.md;
/// kararlar K1–K6: yalnız MSSQL · görüntüleme+işlemler · script sekmeye · canlı doğrulama
/// kullanıcıda). UI/IO yok.
///
/// Okuma msdb TABLOLARINDAN değil BELGELİ PROSEDÜRLERDEN yapılır (<c>sp_help_job</c>,
/// <c>sp_help_jobhistory</c>…): SQLAgent* rollerine yalnız bu prosedürlerin EXECUTE izni verilir —
/// tablo okuyan bir ekran sysadmin olmayan her kullanıcıya boş görünürdü (SSMS de böyle okur).
/// Hepsi msdb'de koşar (köprü veritabanını msdb'ye sabitler).
/// </summary>
public static class AgentSorgulari
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── sorgular ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Tek satır: sürüm · sunucu · Agent servis durumu · kullanıcının Agent rolleri · sunucu saati
    /// (çalışan job'un süresi SUNUCU saatine göre hesaplanır — istemci saati kayık olabilir).
    /// Servis DMV'si VIEW SERVER STATE ister; yetki yoksa TRY/CATCH ile NULL → "bilinmiyor"
    /// (Agent oturumu görünüyorsa çalışıyor sayılır). msdb'de koşmalı (IS_ROLEMEMBER).
    /// </summary>
    public static string DurumSorgusu() => """
        SET NOCOUNT ON;
        DECLARE @ajan nvarchar(60);
        BEGIN TRY
            SELECT TOP (1) @ajan = status_desc FROM sys.dm_server_services
            WHERE servicename LIKE N'SQL Server Agent%';
        END TRY
        BEGIN CATCH
            SET @ajan = NULL;
        END CATCH;
        IF @ajan IS NULL AND EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE program_name LIKE N'SQLAgent - %')
            SET @ajan = N'Running';
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS int) AS motor_surumu,
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS surum,
               CAST(SERVERPROPERTY('ServerName') AS nvarchar(128)) AS sunucu,
               @ajan AS ajan_durumu,
               ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS sysadmin,
               ISNULL(IS_ROLEMEMBER('SQLAgentReaderRole'), 0) AS okuyucu,
               ISNULL(IS_ROLEMEMBER('SQLAgentUserRole'), 0) AS kullanici,
               ISNULL(IS_ROLEMEMBER('SQLAgentOperatorRole'), 0) AS operator,
               GETDATE() AS sunucu_saati,
               SUSER_SNAME() AS giris;
        """;

    /// <summary>Job listesi (+ şu an çalışan durumu: <c>current_execution_status</c> 1 = çalışıyor).</summary>
    public static string JobListesiSorgusu() => "EXEC msdb.dbo.sp_help_job;";

    /// <summary>
    /// Geçmiş — job verilmezse TÜM görünür job'lar (liste özeti: son koşunun süresi ve hatalı adımı
    /// buradan çıkar). msdb varsayılan saklama job başına 100 / toplam 1.000 satırdır — tek okuma.
    /// </summary>
    public static string GecmisSorgusu(Guid? jobId = null) => jobId is { } id
        ? $"EXEC msdb.dbo.sp_help_jobhistory @job_id = '{id}', @mode = N'FULL';"
        : "EXEC msdb.dbo.sp_help_jobhistory @mode = N'FULL';";

    /// <summary>Şu anki Agent oturumundaki aktivite — çalışan job'un başlangıç zamanı.</summary>
    public static string AktiviteSorgusu() => "EXEC msdb.dbo.sp_help_jobactivity;";

    public static string AdimSorgusu(Guid jobId) => $"EXEC msdb.dbo.sp_help_jobstep @job_id = '{jobId}';";

    public static string ZamanlamaSorgusu(Guid jobId) => $"EXEC msdb.dbo.sp_help_jobschedule @job_id = '{jobId}';";

    // ── işlemler (S2) ─────────────────────────────────────────────────────────

    /// <summary>Job'u başlatır; <paramref name="adimAdi"/> verilirse o adımdan.</summary>
    public static string BaslatSql(Guid jobId, string? adimAdi = null)
        => $"EXEC msdb.dbo.sp_start_job @job_id = '{jobId}'"
           + (adimAdi is { Length: > 0 } a ? $", @step_name = {N(a)}" : "") + ";";

    public static string DurdurSql(Guid jobId) => $"EXEC msdb.dbo.sp_stop_job @job_id = '{jobId}';";

    public static string AcKapatSql(Guid jobId, bool acik)
        => $"EXEC msdb.dbo.sp_update_job @job_id = '{jobId}', @enabled = {(acik ? 1 : 0)};";

    /// <summary>
    /// İşlem hatasını Türkçeleştirir. 22022 = Agent çalışmıyor (Express/LocalDB'de de bu gelir);
    /// 22268 = job zaten çalışıyor; 22269 = job zaten çalışmıyor (durdurulamaz); 229/15247 = yetki.
    /// </summary>
    public static string HataMetni(SqlHata hata) => hata.Numara switch
    {
        22022 => "SQL Server Agent servisi çalışmıyor — job başlatılamaz/durdurulamaz. "
                 + "Servisi SQL Server Configuration Manager'dan başlatın (Express/LocalDB'de Agent yoktur).",
        22268 => "Job zaten çalışıyor.",
        22269 => "Job şu an çalışmıyor — durdurulacak bir koşu yok.",
        229 or 15247 or 14262 or 14525 =>
            $"Bu işlem için yetkiniz yok (SQLAgentOperatorRole ya da job sahipliği gerekir). [{hata.Numara}] {hata.Mesaj}",
        _ => $"[{hata.Numara}] {hata.Mesaj}",
    };

    // ── ayrıştırma ───────────────────────────────────────────────────────────

    public static AgentSunucuDurumu? DurumOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.LastOrDefault() is not { Satirlar.Count: > 0 } rs)
            return null;
        var s = new SatirOkuyucu(rs, rs.Satirlar[0]);
        return new AgentSunucuDurumu(
            s.Int("motor_surumu"), s.Metin("surum"), s.Metin("sunucu"), s.MetinYaDaNull("ajan_durumu"),
            s.Bool("sysadmin"), s.Bool("okuyucu"), s.Bool("kullanici"), s.Bool("operator"),
            s.Tarih("sunucu_saati") ?? DateTime.Now, s.Metin("giris"));
    }

    public static IReadOnlyList<AgentGecmisSatiri> GecmisOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        var liste = new List<AgentGecmisSatiri>(rs.Satirlar.Count);
        foreach (object?[] satir in rs.Satirlar)
        {
            var s = new SatirOkuyucu(rs, satir);
            liste.Add(new AgentGecmisSatiri(
                s.Int("instance_id"), s.Guid("job_id"), s.Metin("job_name"), s.Int("step_id"),
                s.Metin("step_name"), (AgentSonuc)s.Int("run_status"),
                AgentZamani(s.Int("run_date"), s.Int("run_time")), s.Int("run_duration"),
                s.Metin("message"), s.Int("sql_message_id"), s.Int("retries_attempted")));
        }
        return [.. liste.OrderByDescending(g => g.InstanceId)];
    }

    /// <summary>
    /// sp_help_job + geçmiş + aktiviteyi job satırlarına birleştirir. Son koşu GEÇMİŞTEN (adım 0
    /// satırı) alınır — sp_help_job'un <c>last_run_*</c> alanları süre ve hatalı adım vermez; geçmiş
    /// temizlenmişse onlara düşülür. Hatalı adım: son koşunun adım satırlarından hata vereni
    /// (koşunun başlangıcından sonra, kendi sonuç satırından önce yazılmış olanlar).
    /// </summary>
    public static IReadOnlyList<AgentJob> JoblariKur(
        QueryResult jobSonucu, IReadOnlyList<AgentGecmisSatiri> gecmis, QueryResult? aktivite = null)
    {
        if (jobSonucu.ResultSetler.FirstOrDefault() is not { } rs)
            return [];

        var baslangiclar = new Dictionary<Guid, DateTime>();
        if (aktivite?.ResultSetler.FirstOrDefault() is { } ars)
            foreach (object?[] satir in ars.Satirlar)
            {
                var a = new SatirOkuyucu(ars, satir);
                if (a.Tarih("start_execution_date") is { } bas && a.Tarih("stop_execution_date") is null)
                    baslangiclar[a.Guid("job_id")] = bas;
            }

        ILookup<Guid, AgentGecmisSatiri> jobGecmisi = gecmis.ToLookup(g => g.JobId);
        var liste = new List<AgentJob>(rs.Satirlar.Count);
        foreach (object?[] satir in rs.Satirlar)
        {
            var s = new SatirOkuyucu(rs, satir);
            Guid id = s.Guid("job_id");
            List<AgentGecmisSatiri> kosular = [.. jobGecmisi[id].OrderByDescending(g => g.InstanceId)];
            AgentGecmisSatiri? son = kosular.FirstOrDefault(g => g.AdimId == 0);

            DateTime? sonKosu;
            AgentSonuc sonSonuc;
            int? sonSure = null, hataliAdim = null;
            if (son is not null)
            {
                sonKosu = son.Zaman;
                sonSonuc = son.Sonuc;
                sonSure = son.Sure;
                int oncekiSonuc = kosular.FirstOrDefault(g => g.AdimId == 0 && g.InstanceId < son.InstanceId)?.InstanceId ?? 0;
                hataliAdim = son.Sonuc == AgentSonuc.Hata
                    ? kosular.FirstOrDefault(g => g.AdimId > 0 && g.InstanceId < son.InstanceId
                                                  && g.InstanceId > oncekiSonuc && g.Sonuc == AgentSonuc.Hata)?.AdimId
                    : null;
            }
            else
            {
                sonKosu = AgentZamani(s.Int("last_run_date"), s.Int("last_run_time"));
                sonSonuc = sonKosu is null ? AgentSonuc.Bilinmiyor : (AgentSonuc)s.Int("last_run_outcome");
            }

            bool calisiyor = s.Int("current_execution_status") is 1 or 2 or 3 or 7;
            liste.Add(new AgentJob(
                id, s.Metin("name"), s.Bool("enabled"), s.Metin("category"), s.Metin("owner"),
                s.Metin("description"), sonKosu, sonSonuc, sonSure, hataliAdim,
                AgentZamani(s.Int("next_run_date"), s.Int("next_run_time")),
                calisiyor, calisiyor ? BastakiSayi(s.Metin("current_execution_step")) : null,
                calisiyor && baslangiclar.TryGetValue(id, out DateTime b) ? b : null,
                s.Int("has_step"), s.Int("has_schedule") > 0,
                BilinenAd(s.Metin("notify_email_operator")), s.Int("notify_level_email"),
                s.Int("notify_level_eventlog"), Math.Max(1, s.Int("start_step_id"))));
        }
        return [.. liste.OrderBy(j => j.Ad, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static IReadOnlyList<AgentAdim> AdimlariOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        return [.. rs.Satirlar.Select(satir =>
        {
            var s = new SatirOkuyucu(rs, satir);
            return new AgentAdim(
                s.Int("step_id"), s.Metin("step_name"), s.Metin("subsystem"), s.Metin("command"),
                s.MetinYaDaNull("database_name"), s.Int("on_success_action"), s.Int("on_success_step_id"),
                s.Int("on_fail_action"), s.Int("on_fail_step_id"), s.Int("retry_attempts"),
                s.Int("retry_interval"), s.IntYaDaNull("proxy_id"), s.MetinYaDaNull("output_file_name"),
                s.Int("flags"));
        }).OrderBy(a => a.Id)];
    }

    public static IReadOnlyList<AgentZamanlama> ZamanlamalariOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        return [.. rs.Satirlar.Select(satir =>
        {
            var s = new SatirOkuyucu(rs, satir);
            return new AgentZamanlama(
                s.Int("schedule_id"), s.Metin("schedule_name"), s.Bool("enabled"), s.Int("freq_type"),
                s.Int("freq_interval"), s.Int("freq_subday_type"), s.Int("freq_subday_interval"),
                s.Int("freq_relative_interval"), s.Int("freq_recurrence_factor"),
                s.Int("active_start_date"), s.Int("active_end_date"),
                s.Int("active_start_time"), s.Int("active_end_time"));
        })];
    }

    // ── biçim ────────────────────────────────────────────────────────────────

    /// <summary>msdb'nin tamsayı tarih (yyyymmdd) + saat (hhmmss) çiftini DateTime'a çevirir; 0 → null.</summary>
    public static DateTime? AgentZamani(int tarih, int saat)
    {
        if (tarih <= 0)
            return null;
        try
        {
            return new DateTime(tarih / 10000, tarih / 100 % 100, tarih % 100,
                saat / 10000, saat / 100 % 100, saat % 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // bozuk/eski satır — tarih uydurulmaz
        }
    }

    /// <summary>Ham değer kuralı (v23-S13): veritabanının biçimi — yyyy-MM-dd HH:mm:ss.</summary>
    public static string ZamanMetni(DateTime? zaman)
        => zaman?.ToString("yyyy-MM-dd HH:mm:ss", Inv) ?? "";

    /// <summary>msdb süresi (hhmmss tamsayı; saat 99'u aşabilir) → "HH:mm:ss".</summary>
    public static string SureMetni(int hhmmss)
        => string.Create(Inv, $"{hhmmss / 10000:00}:{hhmmss / 100 % 100:00}:{hhmmss % 100:00}");

    public static string SureMetni(TimeSpan sure)
        => string.Create(Inv, $"{(int)sure.TotalHours:00}:{sure.Minutes:00}:{sure.Seconds:00}");

    /// <summary>Adımın başarı/hata sonrası eylemi (1 başarıyla çık · 2 hatayla çık · 3 sonraki · 4 N'e git).</summary>
    public static string EylemMetni(int eylem, int adim) => eylem switch
    {
        1 => "Başarıyla çık",
        2 => "Hatayla çık",
        3 => "Sonraki adım",
        4 => $"{adim}. adıma git",
        _ => "?",
    };

    public static string SonucMetni(AgentSonuc sonuc) => sonuc switch
    {
        AgentSonuc.Basarili => "Başarılı",
        AgentSonuc.Hata => "Hata",
        AgentSonuc.YenidenDeneme => "Yeniden deneniyor",
        AgentSonuc.Iptal => "İptal edildi",
        AgentSonuc.Suruyor => "Sürüyor",
        _ => "—",
    };

    private static readonly string[] GunAdlari = ["pazar", "pazartesi", "salı", "çarşamba", "perşembe", "cuma", "cumartesi"];

    // "ayın ilk pazartesisi" — iyelik eki sözlükten (Türkçe ek uyumu kuralla değil, liste ile)
    private static readonly string[] GunIyelik =
        ["pazarı", "pazartesisi", "salısı", "çarşambası", "perşembesi", "cuması", "cumartesisi"];

    /// <summary>
    /// Zamanlamayı Türkçe cümleye çevirir ("Hafta içi her gün, 08:00–18:00 arası her 15 dakikada bir").
    /// freq_type: 1 bir kez · 4 günlük · 8 haftalık (gün bit maskesi: 1 pazar…64 cumartesi) · 16 aylık
    /// (ayın N. günü) · 32 aylık göreli (ilk/ikinci/üçüncü/dördüncü/son + gün) · 64 Agent başlarken ·
    /// 128 CPU boştayken.
    /// </summary>
    public static string ZamanlamaMetni(AgentZamanlama z)
    {
        string metin = z.FreqType switch
        {
            1 => "Bir kez: " + (AgentZamani(z.BaslangicTarihi, z.BaslangicSaati)?.ToString("yyyy-MM-dd HH:mm", Inv) ?? "?"),
            4 => GunIciEkle(z.FreqInterval <= 1 ? "Her gün" : $"{z.FreqInterval} günde bir", z),
            8 => GunIciEkle(HaftalikGunler(z.FreqInterval, z.RecurrenceFactor), z),
            16 => GunIciEkle(AylikOnek(z.RecurrenceFactor) + $"ayın {z.FreqInterval}. günü", z),
            32 => GunIciEkle(AylikOnek(z.RecurrenceFactor) + $"ayın {Sira(z.RelativeInterval)} {GoreliGun(z.FreqInterval)}", z),
            64 => "SQL Server Agent başlarken",
            128 => "CPU boşta kaldığında",
            _ => $"Bilinmeyen zamanlama türü ({z.FreqType})",
        };
        if (z.FreqType is not (1 or 64 or 128) && z.BitisTarihi is > 0 and < 99991231
            && AgentZamani(z.BitisTarihi, 0) is { } bitis)
            metin += $" · {bitis.ToString("yyyy-MM-dd", Inv)} tarihine kadar";
        return metin;
    }

    private static string GunIciEkle(string gunKismi, AgentZamanlama z)
    {
        string bas = SaatMetni(z.BaslangicSaati);
        if (z.SubdayType is 2 or 4 or 8 && z.SubdayInterval > 0)
        {
            string birim = z.SubdayType switch { 2 => "saniyede", 4 => "dakikada", _ => "saatte" };
            bool gunBoyu = z.BaslangicSaati == 0 && z.BitisSaati >= 235900;
            string aralik = gunBoyu ? "gün boyu" : $"{bas}–{SaatMetni(z.BitisSaati)} arası";
            return $"{gunKismi}, {aralik} her {z.SubdayInterval} {birim} bir";
        }
        return $"{gunKismi} {bas}";
    }

    private static string HaftalikGunler(int maske, int kacHaftada)
    {
        string onek = kacHaftada > 1 ? $"{kacHaftada} haftada bir: " : "";
        string gunler = maske switch
        {
            62 => "hafta içi her gün",
            65 => "hafta sonu her gün",
            127 => "haftanın her günü",
            _ => Liste([.. Enumerable.Range(0, 7)
                .Select(i => (Gun: (i + 1) % 7, Bit: 1 << ((i + 1) % 7)))  // pazartesiden başlat
                .Where(g => (maske & g.Bit) != 0)
                .Select(g => GunAdlari[g.Gun])]),
        };
        if (kacHaftada > 1)
            return onek + gunler;
        return maske is 62 or 65 or 127 ? Buyuk(gunler) : "Her " + gunler;
    }

    private static string AylikOnek(int kacAyda) => kacAyda > 1 ? $"{kacAyda} ayda bir, " : "Her ";

    private static string Sira(int goreli) => goreli switch
    {
        1 => "ilk",
        2 => "ikinci",
        4 => "üçüncü",
        8 => "dördüncü",
        16 => "son",
        _ => "?",
    };

    private static string GoreliGun(int gun) => gun switch
    {
        >= 1 and <= 7 => GunIyelik[gun - 1],
        8 => "günü",
        9 => "hafta içi günü",
        10 => "hafta sonu günü",
        _ => "?",
    };

    /// <summary>hhmmss → "HH:mm" (saniye varsa "HH:mm:ss").</summary>
    public static string SaatMetni(int hhmmss)
    {
        int s = hhmmss % 100;
        string hm = string.Create(Inv, $"{hhmmss / 10000:00}:{hhmmss / 100 % 100:00}");
        return s == 0 ? hm : string.Create(Inv, $"{hm}:{s:00}");
    }

    private static string Liste(IReadOnlyList<string> ogeler) => ogeler.Count switch
    {
        0 => "?",
        1 => ogeler[0],
        _ => string.Join(", ", ogeler.Take(ogeler.Count - 1)) + " ve " + ogeler[^1],
    };

    private static string Buyuk(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.GetCultureInfo("tr-TR")) + s[1..];

    // ── S2: "Script olarak al" (SSMS "Script Job as CREATE") ─────────────────

    /// <summary>
    /// Job'u yeniden oluşturan script — başka sunucuya taşımak/yedeklemek için. ÇALIŞTIRILMAZ,
    /// sekmede açılır. Tek transaction + TRY/CATCH: yarıda kalan job bırakmaz. Kategori yoksa
    /// oluşturulur (köşeli parantezli yerleşik kategoriler hariç). Proxy'li adımda proxy ADI
    /// bilinmediği için not düşülür — uydurma ad yazılmaz.
    /// </summary>
    public static string OlusturmaScripti(
        AgentJob job, IReadOnlyList<AgentAdim> adimlar, IReadOnlyList<AgentZamanlama> zamanlamalar,
        string sunucu, DateTime uretimZamani)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- ⏱ SQL Agent job script'i · {job.Ad} · SQLST üretti ({sunucu}, {ZamanMetni(uretimZamani)})");
        sb.AppendLine("-- Script ÇALIŞTIRILMADI — hedef sunucuda gözden geçirip çalıştırın (msdb'de koşar).");
        sb.AppendLine("-- Aynı adlı job hedefte varsa sp_add_job hata verir ve hiçbir şey oluşmaz (transaction).");
        sb.AppendLine();
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRY");
        sb.AppendLine("    BEGIN TRANSACTION;");
        sb.AppendLine("    DECLARE @jobId uniqueidentifier;");
        bool yerlesikKategori = KategoriYaz(sb, job.Kategori);
        sb.AppendLine("    EXEC msdb.dbo.sp_add_job");
        var jobParam = new List<string>
        {
            $"@job_name = {N(job.Ad)}",
            $"@enabled = {(job.Acik ? 1 : 0)}",
            $"@description = {N(job.Aciklama)}",
            $"@start_step_id = {job.BaslangicAdimi}",
        };
        if (!yerlesikKategori)
            jobParam.Add($"@category_name = {N(job.Kategori)}");
        if (job.Sahip.Length > 0)
            jobParam.Add($"@owner_login_name = {N(job.Sahip)}");
        jobParam.Add($"@notify_level_eventlog = {job.BildirimSeviyesiOlay}");
        if (job.BildirimOperatoru.Length > 0 && job.BildirimSeviyesiEposta > 0)
        {
            jobParam.Add($"@notify_level_email = {job.BildirimSeviyesiEposta}");
            jobParam.Add($"@notify_email_operator_name = {N(job.BildirimOperatoru)}");
        }
        jobParam.Add("@job_id = @jobId OUTPUT");
        sb.AppendLine("        " + string.Join(",\n        ", jobParam) + ";");
        AdimlariYaz(sb, adimlar);
        ZamanlamalariYaz(sb, zamanlamalar);

        sb.AppendLine();
        sb.AppendLine("    EXEC msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)';");
        ScriptKapanisi(sb);
        return sb.ToString();
    }

    /// <summary>Kategori yerleşik değilse yoksa-oluştur satırını yazar; yerleşik mi döner.</summary>
    private static bool KategoriYaz(StringBuilder sb, string kategori)
    {
        bool yerlesik = kategori.StartsWith('[') || kategori.Length == 0;
        if (!yerlesik)
        {
            sb.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.syscategories WHERE name = {N(kategori)} AND category_class = 1)");
            sb.AppendLine($"        EXEC msdb.dbo.sp_add_category @class = N'JOB', @type = N'LOCAL', @name = {N(kategori)};");
        }
        return yerlesik;
    }

    private static void ScriptKapanisi(StringBuilder sb)
    {
        sb.AppendLine("    COMMIT TRANSACTION;");
        sb.AppendLine("END TRY");
        sb.AppendLine("BEGIN CATCH");
        sb.AppendLine("    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        sb.AppendLine("    THROW;");
        sb.Append("END CATCH;");
    }

    private static void AdimlariYaz(StringBuilder sb, IReadOnlyList<AgentAdim> adimlar)
    {
        foreach (AgentAdim a in adimlar)
        {
            sb.AppendLine();
            if (a.ProxyId is { } proxy)
                sb.AppendLine($"    -- ⚠ Bu adım proxy (id {proxy}) ile koşuyor — hedefte proxy adını @proxy_name ile ekleyin.");
            sb.AppendLine("    EXEC msdb.dbo.sp_add_jobstep");
            var p = new List<string>
            {
                "@job_id = @jobId",
                $"@step_id = {a.Id}",
                $"@step_name = {N(a.Ad)}",
                $"@subsystem = {N(a.AltSistem)}",
                $"@command = {N(a.Komut)}",
            };
            if (a.Veritabani is { Length: > 0 } db)
                p.Add($"@database_name = {N(db)}");
            p.Add($"@on_success_action = {a.BasaridaEylem}");
            p.Add($"@on_success_step_id = {a.BasaridaAdim}");
            p.Add($"@on_fail_action = {a.HatadaEylem}");
            p.Add($"@on_fail_step_id = {a.HatadaAdim}");
            p.Add($"@retry_attempts = {a.YenidenDeneme}");
            p.Add($"@retry_interval = {a.DenemeAraligi}");
            if (a.CiktiDosyasi is { Length: > 0 } dosya)
                p.Add($"@output_file_name = {N(dosya)}");
            if (a.Bayraklar != 0)
                p.Add($"@flags = {a.Bayraklar}");
            sb.AppendLine("        " + string.Join(",\n        ", p) + ";");
        }
    }

    private static void ZamanlamalariYaz(StringBuilder sb, IReadOnlyList<AgentZamanlama> zamanlamalar)
    {
        foreach (AgentZamanlama z in zamanlamalar)
        {
            sb.AppendLine();
            sb.AppendLine($"    -- {ZamanlamaMetni(z)}");
            sb.AppendLine("    EXEC msdb.dbo.sp_add_jobschedule");
            sb.AppendLine("        " + string.Join(",\n        ",
                "@job_id = @jobId",
                $"@name = {N(z.Ad)}",
                $"@enabled = {(z.Acik ? 1 : 0)}",
                $"@freq_type = {z.FreqType}",
                $"@freq_interval = {z.FreqInterval}",
                $"@freq_subday_type = {z.SubdayType}",
                $"@freq_subday_interval = {z.SubdayInterval}",
                $"@freq_relative_interval = {z.RelativeInterval}",
                $"@freq_recurrence_factor = {z.RecurrenceFactor}",
                $"@active_start_date = {z.BaslangicTarihi}",
                $"@active_end_date = {z.BitisTarihi}",
                $"@active_start_time = {z.BaslangicSaati}",
                $"@active_end_time = {z.BitisSaati}") + ";");
        }
    }

    // ── S3: sihirbaz — düzenleme script'i · söz dizimi denetimi · doğrulama ──

    /// <summary>
    /// Mevcut job'u SİLMEDEN günceller: adımlar topluca silinip yeniden yazılır (sp_delete_jobstep
    /// @step_id = 0), eski zamanlamalar ayrılır (paylaşılmıyorsa silinir — sp_detach_schedule
    /// @delete_unused_schedule = 1), yenileri eklenir, en son job özellikleri (sp_update_job — başlangıç
    /// adımı ancak adımlar varken geçerli). Job kimliği ve GEÇMİŞİ korunur (LocalDB'de doğrulandı).
    /// ÇALIŞTIRILMAZ, sekmeye açılır; tek transaction.
    /// </summary>
    public static string GuncellemeScripti(
        Guid jobId, AgentJob job, IReadOnlyList<AgentAdim> adimlar, IReadOnlyList<AgentZamanlama> zamanlamalar,
        IReadOnlyList<int> eskiZamanlamaIdleri, string sunucu, DateTime uretimZamani)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- ⏱ SQL Agent job düzenleme · {job.Ad} · SQLST sihirbazı ({sunucu}, {ZamanMetni(uretimZamani)})");
        sb.AppendLine("-- Script ÇALIŞTIRILMADI — gözden geçirip çalıştırın (msdb'de koşar).");
        sb.AppendLine("-- Job SİLİNMEZ: kimliği ve geçmişi korunur; adımlar ve zamanlamalar yeniden yazılır.");
        sb.AppendLine();
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRY");
        sb.AppendLine("    BEGIN TRANSACTION;");
        sb.AppendLine($"    DECLARE @jobId uniqueidentifier = '{jobId}';");
        bool yerlesikKategori = KategoriYaz(sb, job.Kategori);
        sb.AppendLine();
        sb.AppendLine("    EXEC msdb.dbo.sp_delete_jobstep @job_id = @jobId, @step_id = 0;  -- tüm adımlar");
        AdimlariYaz(sb, adimlar);
        if (eskiZamanlamaIdleri.Count > 0)
            sb.AppendLine();
        foreach (int id in eskiZamanlamaIdleri)
            sb.AppendLine($"    EXEC msdb.dbo.sp_detach_schedule @job_id = @jobId, @schedule_id = {id}, @delete_unused_schedule = 1;");
        ZamanlamalariYaz(sb, zamanlamalar);
        sb.AppendLine();
        sb.AppendLine("    EXEC msdb.dbo.sp_update_job");
        var p = new List<string>
        {
            "@job_id = @jobId",
            $"@new_name = {N(job.Ad)}",
            $"@enabled = {(job.Acik ? 1 : 0)}",
            $"@description = {N(job.Aciklama)}",
            $"@start_step_id = {job.BaslangicAdimi}",
        };
        if (!yerlesikKategori)
            p.Add($"@category_name = {N(job.Kategori)}");
        if (job.Sahip.Length > 0)
            p.Add($"@owner_login_name = {N(job.Sahip)}");
        sb.AppendLine("        " + string.Join(",\n        ", p) + ";");
        ScriptKapanisi(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Adım komutunu ÇALIŞTIRMADAN derler: <c>SET NOEXEC ON</c> aynı batch'te sonraki ifadeleri yalnız
    /// derler (PARSEONLY aynı batch'te İŞE YARAMAZ — LocalDB'de komut gerçekten koştu; NOEXEC ile tablo
    /// oluşmadığı doğrulandı). Sözdizimi + var olan nesnelere bağlama hataları yakalanır. Komut GO
    /// içeriyorsa her parça ayrı denetlenir (<see cref="GoIleBol"/>).
    /// </summary>
    public static string DenetimSql(string parca) => "SET NOEXEC ON;\n" + parca + "\nSET NOEXEC OFF;";

    /// <summary>Komutu satır başındaki GO ayırıcılarından böler (Agent T-SQL adımı GO'yu destekler).</summary>
    public static IReadOnlyList<string> GoIleBol(string komut)
        => [.. System.Text.RegularExpressions.Regex
            .Split(komut ?? "", @"^\s*GO\s*(?:\d+\s*)?$",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Where(p => !string.IsNullOrWhiteSpace(p))];

    /// <summary>
    /// Sihirbaz denetimi: (Hata, Mesaj) — Hata=true "Script üret"i kapatır, false yalnız uyarıdır.
    /// Adım hedefleri (eylem 4) ve sıra numaraları çağıranın verdiği SON numaralarla denetlenir.
    /// </summary>
    public static IReadOnlyList<(bool Hata, string Mesaj)> Dogrula(
        AgentJob job, IReadOnlyList<AgentAdim> adimlar, IReadOnlyList<AgentZamanlama> zamanlamalar)
    {
        var l = new List<(bool, string)>();
        if (string.IsNullOrWhiteSpace(job.Ad))
            l.Add((true, "Job adı boş olamaz."));
        if (adimlar.Count == 0)
            l.Add((true, "En az bir adım ekleyin."));
        foreach (IGrouping<string, AgentAdim> g in adimlar.GroupBy(a => a.Ad.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            l.Add((true, $"Adım adı tekrar ediyor: \"{g.Key}\" — her adımın adı farklı olmalı."));
        foreach (AgentAdim a in adimlar)
        {
            string ad = $"Adım {a.Id}";
            if (string.IsNullOrWhiteSpace(a.Ad))
                l.Add((true, $"{ad}: adı boş."));
            if (string.IsNullOrWhiteSpace(a.Komut))
                l.Add((true, $"{ad}: komut boş."));
            foreach ((int eylem, int hedef, string ne) in new[] { (a.BasaridaEylem, a.BasaridaAdim, "başarıda"), (a.HatadaEylem, a.HatadaAdim, "hatada") })
            {
                if (eylem != 4)
                    continue;
                if (hedef < 1 || hedef > adimlar.Count)
                    l.Add((true, $"{ad}: {ne} gidilecek adım yok ({hedef})."));
                else if (hedef == a.Id)
                    l.Add((false, $"{ad} \"{a.Ad}\": {ne} kendine gidiyor — sonsuz döngü riski."));
            }
            if (a.Id == adimlar.Count && a.BasaridaEylem == 3)
                l.Add((false, $"{ad} son adım ve başarıda \"Sonraki adım\" diyor — Agent bunu başarıyla çıkış sayar."));
        }
        foreach (AgentZamanlama z in zamanlamalar)
        {
            string ad = $"Zamanlama \"{z.Ad}\"";
            if (string.IsNullOrWhiteSpace(z.Ad))
                l.Add((true, "Zamanlama adı boş olamaz."));
            if (z.FreqType == 8 && z.FreqInterval == 0)
                l.Add((true, $"{ad}: haftalık zamanlamada en az bir gün seçin."));
            if (z.FreqType == 16 && z.FreqInterval is < 1 or > 31)
                l.Add((true, $"{ad}: ayın günü 1–31 olmalı."));
            if (z.FreqType is 4 or 8 or 16 or 32 && (z.FreqType == 4 ? z.FreqInterval : z.RecurrenceFactor) < 1)
                l.Add((true, $"{ad}: tekrar sıklığı en az 1 olmalı."));
            if (z.SubdayType is 2 or 4 or 8)
            {
                if (z.SubdayInterval < 1)
                    l.Add((true, $"{ad}: gün içi tekrar aralığı en az 1 olmalı."));
                if (z.BitisSaati <= z.BaslangicSaati)
                    l.Add((true, $"{ad}: gün içi bitiş saati başlangıçtan sonra olmalı."));
            }
            if (z.BitisTarihi < z.BaslangicTarihi)
                l.Add((true, $"{ad}: bitiş tarihi başlangıçtan önce."));
        }
        return l;
    }

    // ── yardımcılar ──────────────────────────────────────────────────────────

    private static string N(string metin) => "N'" + metin.Replace("'", "''") + "'";

    /// <summary>"2 (Birleştir)" → 2.</summary>
    private static int? BastakiSayi(string metin)
    {
        int i = 0;
        while (i < metin.Length && char.IsAsciiDigit(metin[i]))
            i++;
        return i > 0 && int.TryParse(metin.AsSpan(0, i), NumberStyles.None, Inv, out int n) && n > 0 ? n : null;
    }

    /// <summary>sp_help_job operatör yoksa "(unknown)" yazar — boş sayılır.</summary>
    private static string BilinenAd(string ad) => ad is "(unknown)" ? "" : ad;

    /// <summary>Sonuç satırını kolon ADIYLA okur (prosedür kolon sırasına bağlanılmaz).</summary>
    private readonly struct SatirOkuyucu(ResultSetData rs, object?[] satir)
    {
        private object? Deger(string kolon)
        {
            for (int i = 0; i < rs.Kolonlar.Count; i++)
                if (string.Equals(rs.Kolonlar[i].Ad, kolon, StringComparison.OrdinalIgnoreCase))
                    return i < satir.Length && satir[i] is not DBNull ? satir[i] : null;
            return null;
        }

        public string Metin(string kolon) => MetinYaDaNull(kolon) ?? "";

        public string? MetinYaDaNull(string kolon) => Deger(kolon) switch
        {
            null => null,
            string s => s,
            object o => Convert.ToString(o, Inv),
        };

        public int Int(string kolon) => IntYaDaNull(kolon) ?? 0;

        public int? IntYaDaNull(string kolon) => Deger(kolon) switch
        {
            null => null,
            int i => i,
            byte b => b,
            short s => s,
            long l => (int)l,
            bool b => b ? 1 : 0,
            string s when int.TryParse(s, NumberStyles.Integer, Inv, out int n) => n,
            IConvertible c => c.ToInt32(Inv),
            _ => null,
        };

        public bool Bool(string kolon) => Int(kolon) != 0;

        public Guid Guid(string kolon) => Deger(kolon) switch
        {
            System.Guid g => g,
            string s when System.Guid.TryParse(s, out System.Guid g) => g,
            _ => System.Guid.Empty,
        };

        public DateTime? Tarih(string kolon) => Deger(kolon) switch
        {
            DateTime t => t,
            DateTimeOffset o => o.DateTime,
            _ => null,
        };
    }
}
