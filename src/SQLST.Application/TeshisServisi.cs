using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Teşhis Merkezi veri katmanı (V2-S7): DMV sorguları — eksik/kullanılmayan index,
/// RCSI karar verileri, tek-tuş havuz (R1.3). Stateless executor yeterli; hiçbir
/// sorgu yazma yapmaz. VIEW_SERVER_STATE izni yoksa sonuç hatası UI'da dürüstçe görünür.
/// </summary>
public sealed class TeshisServisi
{
    private readonly ISqlExecutor _executor;

    public TeshisServisi(ISqlExecutor executor) => _executor = executor;

    /// <summary>Uptime kapısı (R1.1 kural 2): DMV verileri bu andan beri birikiyor.</summary>
    public async Task<DateTime> SunucuBaslangiciAsync(ConnectionProfile profil, CancellationToken ct)
    {
        QueryResult sonuc = await _executor.ExecuteAsync(profil,
            "SELECT sqlserver_start_time FROM sys.dm_os_sys_info;", ExecuteOptions.Varsayilan, ct);
        Dogrula(sonuc, "sunucu başlangıcı");
        return (DateTime)sonuc.ResultSetler[0].Satirlar[0][0]!;
    }

    // 2019+ (major 15): öneriyi İSTEYEN sorgu da gelir (R1.1 kural 6); eski sürümde sade sorgu.
    private const string EksikIndexZengin = """
        SELECT DB_NAME(d.database_id) AS vt,
               QUOTENAME(OBJECT_SCHEMA_NAME(d.object_id, d.database_id)) + '.' + QUOTENAME(OBJECT_NAME(d.object_id, d.database_id)) AS tablo,
               d.equality_columns, d.inequality_columns, d.included_columns,
               CONVERT(float, gs.avg_total_user_cost * gs.avg_user_impact * (gs.user_seeks + gs.user_scans)) AS skor,
               CAST(gs.user_seeks + gs.user_scans AS bigint) AS kullanim,
               gs.last_user_seek,
               isteyen.metin AS isteyen_sorgu
        FROM sys.dm_db_missing_index_details d
        JOIN sys.dm_db_missing_index_groups g ON g.index_handle = d.index_handle
        JOIN sys.dm_db_missing_index_group_stats gs ON gs.group_handle = g.index_group_handle
        OUTER APPLY (
            SELECT TOP 1 t.text AS metin
            FROM sys.dm_db_missing_index_group_stats_query q
            CROSS APPLY sys.dm_exec_sql_text(q.last_sql_handle) t
            WHERE q.group_handle = g.index_group_handle
        ) isteyen
        WHERE d.database_id > 4
        ORDER BY skor DESC;
        """;

    private const string EksikIndexSade = """
        SELECT DB_NAME(d.database_id) AS vt,
               QUOTENAME(OBJECT_SCHEMA_NAME(d.object_id, d.database_id)) + '.' + QUOTENAME(OBJECT_NAME(d.object_id, d.database_id)) AS tablo,
               d.equality_columns, d.inequality_columns, d.included_columns,
               CONVERT(float, gs.avg_total_user_cost * gs.avg_user_impact * (gs.user_seeks + gs.user_scans)) AS skor,
               CAST(gs.user_seeks + gs.user_scans AS bigint) AS kullanim,
               gs.last_user_seek,
               CAST(NULL AS nvarchar(max)) AS isteyen_sorgu
        FROM sys.dm_db_missing_index_details d
        JOIN sys.dm_db_missing_index_groups g ON g.index_handle = d.index_handle
        JOIN sys.dm_db_missing_index_group_stats gs ON gs.group_handle = g.index_group_handle
        WHERE d.database_id > 4
        ORDER BY skor DESC;
        """;

    /// <summary>Sunucu genelindeki eksik index önerileri (skor = Glenn Berry index_advantage; YALNIZ sıralama).</summary>
    public async Task<IReadOnlyList<EksikIndexOnerisi>> EksikIndexlerAsync(ConnectionProfile profil, CancellationToken ct)
    {
        QueryResult sonuc = await _executor.ExecuteAsync(profil, EksikIndexZengin, ExecuteOptions.Varsayilan, ct);
        if (sonuc.Hata is not null) // 2019 öncesi: _group_stats_query yok — sade sorguya düş
            sonuc = await _executor.ExecuteAsync(profil, EksikIndexSade, ExecuteOptions.Varsayilan, ct);
        Dogrula(sonuc, "eksik index");

        return [.. sonuc.ResultSetler[0].Satirlar.Select(r => new EksikIndexOnerisi(
            Veritabani: (string)r[0]!,
            Tablo: (string)r[1]!,
            EsitlikKolonlari: r[2] as string,
            EsitsizlikKolonlari: r[3] as string,
            IncludeKolonlari: r[4] as string,
            Skor: Convert.ToDouble(r[5]),
            KullanimSayisi: Convert.ToInt64(r[6]),
            SonKullanim: r[7] as DateTime?,
            IsteyenSorgu: r[8] as string))];
    }

    /// <summary>Örtüşme analizi girdisi: verilen veritabanındaki mevcut index'ler (anahtar sıralı).</summary>
    public async Task<IReadOnlyList<MevcutIndex>> MevcutIndexlerAsync(
        ConnectionProfile profil, string veritabani, CancellationToken ct)
    {
        // Kolon adlarını virgülle birleştirmek için FOR XML PATH + STUFF kullanılır —
        // STRING_AGG DEĞİL (kullanıcı bulgusu 2026-07-20): STRING_AGG yalnız SQL Server
        // 2017+'da vardır ve MERSIS gibi eski sunucularda "'STRING_AGG' is not a recognized
        // built-in function name" hatası verip TÜM paneli düşürüyordu. Bu araç "SSMS
        // kurulamayan / eski, kısıtlı makineler" içindir — sorgular en düşük ortak paydaya
        // (FOR XML PATH, 2005+) göre yazılmalı. .value('.', ...) XML varlıklarını geri çözer
        // (kolon adında & < > olsa bile bozulmaz).
        const string sorgu = """
            SELECT QUOTENAME(s.name) + '.' + QUOTENAME(o.name) AS tablo, i.name,
                   i.is_unique, i.is_primary_key,
                   anahtar = STUFF((SELECT ',' + c.name
                              FROM sys.index_columns ic
                              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                              ORDER BY ic.key_ordinal
                              FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''),
                   dahil = STUFF((SELECT ',' + c.name
                            FROM sys.index_columns ic
                            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                            ORDER BY ic.key_ordinal
                            FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, '')
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE i.index_id > 0;
            """;
        QueryResult sonuc = await _executor.ExecuteAsync(profil, sorgu,
            new ExecuteOptions { VeritabaniOverride = veritabani }, ct);
        Dogrula(sonuc, "mevcut index");

        return [.. sonuc.ResultSetler[0].Satirlar.Select(r => new MevcutIndex(
            Tablo: (string)r[0]!,
            Ad: (string?)(r[1] as string) ?? "(heap)",
            UniqueMi: (bool)r[2]!,
            PkMi: (bool)r[3]!,
            AnahtarKolonlar: Ayir(r[4] as string),
            IncludeKolonlar: Ayir(r[5] as string)))];

        static IReadOnlyList<string> Ayir(string? liste)
            => string.IsNullOrWhiteSpace(liste) ? [] : [.. liste.Split(',')];
    }

    /// <summary>
    /// Kullanılmayan index adayları (R1.1 kural 5): restart'tan beri hiç okunmamış ama
    /// güncelleme maliyeti ödeyen NONCLUSTERED'lar; unique/PK ve FK öncü kolonu
    /// taşıyanlar aday DIŞI. Öneri DISABLE'dır, silme değil.
    /// </summary>
    public async Task<IReadOnlyList<KullanilmayanIndex>> KullanilmayanIndexlerAsync(
        ConnectionProfile profil, string veritabani, CancellationToken ct)
    {
        const string sorgu = """
            SELECT QUOTENAME(s.name) + '.' + QUOTENAME(o.name) AS tablo, i.name,
                   ISNULL(us.user_seeks, 0) + ISNULL(us.user_scans, 0) + ISNULL(us.user_lookups, 0) AS okuma,
                   ISNULL(us.user_updates, 0) AS guncelleme
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.dm_db_index_usage_stats us
                ON us.database_id = DB_ID() AND us.object_id = i.object_id AND us.index_id = i.index_id
            WHERE i.type = 2 AND i.is_unique = 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND ISNULL(us.user_updates, 0) > 0
              AND ISNULL(us.user_seeks, 0) + ISNULL(us.user_scans, 0) + ISNULL(us.user_lookups, 0) = 0
              AND NOT EXISTS (
                    SELECT 1
                    FROM sys.foreign_key_columns fk
                    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                             AND ic.key_ordinal = 1 AND ic.column_id = fk.parent_column_id
                    WHERE fk.parent_object_id = i.object_id)
            ORDER BY guncelleme DESC;
            """;
        QueryResult sonuc = await _executor.ExecuteAsync(profil, sorgu,
            new ExecuteOptions { VeritabaniOverride = veritabani }, ct);
        Dogrula(sonuc, "kullanılmayan index");

        return [.. sonuc.ResultSetler[0].Satirlar.Select(r =>
        {
            string tablo = (string)r[0]!;
            string ad = (string)r[1]!;
            return new KullanilmayanIndex(tablo, ad,
                Convert.ToInt64(r[2]), Convert.ToInt64(r[3]),
                IndexAnalizcisi.DisableScripti(tablo, ad));
        })];
    }

    /// <summary>
    /// RCSI KARAR paneli verileri (R1.5) — <b>tek set:</b> veritabanı başına RCSI/snapshot durumu.
    ///
    /// <b>Sadeleştirme (kullanıcı kararı 2026-07-19):</b> panelde iki set daha vardı ve ikisi de
    /// V5-S3'te eklenen Bakım sekmesiyle MÜKERRERDİ — *tempdb version store* oradaki tempdb
    /// dökümünün alt kümesiydi, *en eski açık işlemler* ise bloklama zinciriyle örtüşüyordu.
    /// Bu sekme bir izleme paneli değil, <b>bir kez verilen bir kararın</b> ekranıdır:
    /// durum tablosu + aşamalı geçiş script'i. Mükerrer setler onu bulanıklaştırıyordu.
    /// </summary>
    public Task<QueryResult> RcsiVerileriAsync(ConnectionProfile profil, CancellationToken ct)
        => _executor.ExecuteAsync(profil, """
            SELECT name AS veritabani,
                   CASE is_read_committed_snapshot_on WHEN 1 THEN N'AÇIK' ELSE N'kapalı' END AS [RCSI],
                   snapshot_isolation_state_desc AS [ALLOW_SNAPSHOT]
            FROM sys.databases WHERE database_id > 4 ORDER BY name;
            """, ExecuteOptions.Varsayilan, ct);

    /// <summary>
    /// Tek-tuş havuz (R1.3): pahalı sorgular, bağlantılar, son yedekler — <b>3 set</b>.
    ///
    /// <b>Sadeleştirme (kullanıcı kararı 2026-07-19):</b> dördüncü set "bloklanan oturumlar"
    /// SİLİNDİ. Yalnız DOĞRUDAN bloklayanı gösteriyordu; V5-S3'te Bakım sekmesine eklenen
    /// <b>bloklama zinciri</b> ise kökü buluyor — üstelik kök çoğu zaman UYKUDA olduğu için
    /// eski set onu hiç göremiyordu. Aynı soruyu iki yerde, biri eksik cevaplamak
    /// kullanıcıyı yanıltır.
    /// </summary>
    public Task<QueryResult> HavuzVerileriAsync(ConnectionProfile profil, CancellationToken ct)
        => _executor.ExecuteAsync(profil, """
            SELECT TOP 20
                   CAST(qs.total_worker_time / 1000.0 AS decimal(18,1)) AS [toplam CPU (ms)],
                   qs.execution_count AS [çalıştırma],
                   CAST(qs.total_worker_time / qs.execution_count / 1000.0 AS decimal(18,1)) AS [ort CPU (ms)],
                   CAST(qs.total_elapsed_time / qs.execution_count / 1000.0 AS decimal(18,1)) AS [ort süre (ms)],
                   qs.total_logical_reads AS [mantıksal okuma],
                   SUBSTRING(t.text, (qs.statement_start_offset / 2) + 1,
                       ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(t.text)
                         ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1) AS sorgu
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
            ORDER BY qs.total_worker_time DESC;

            SELECT ISNULL(DB_NAME(database_id), '?') AS veritabani, login_name AS [giriş], COUNT(*) AS [bağlantı]
            FROM sys.dm_exec_sessions WHERE is_user_process = 1
            GROUP BY database_id, login_name ORDER BY COUNT(*) DESC;

            SELECT d.name AS veritabani, MAX(b.backup_finish_date) AS [son tam yedek]
            FROM sys.databases d
            LEFT JOIN msdb.dbo.backupset b ON b.database_name = d.name AND b.type = 'D'
            WHERE d.database_id > 4
            GROUP BY d.name ORDER BY 2;
            """, ExecuteOptions.Varsayilan, ct);

    /// <summary>
    /// YAVAŞ SORGULAR (v22-S1, saha turu-2 m.14 — kullanıcı: "index önerileri ve yavaş sorgular alanı
    /// SQL'de neden yok"): Mongo panelindeki karşılığının SQL'i. İKİ ayrı soru iki ayrı listedir:
    /// (1) tek çalışışı en uzun süren sorgular (ORTALAMA süre) — "ekran kilitlendi" şikâyetinin yeri;
    /// (2) toplam süreyi en çok tüketen sorgular — tek başına hızlı ama çok koşan; sunucu genelinde
    /// kazanç oradadır. Kaynak plan önbelleğidir: restart/önbellek temizliğinde sayaçlar sıfırlanır
    /// (panelin uptime bandı bunu zaten söylüyor). Havuz sekmesindeki liste CPU'ya göredir — bu ikisi
    /// SÜREYE göre, farklı soru.
    /// </summary>
    public Task<QueryResult> YavasSorgularAsync(ConnectionProfile profil, CancellationToken ct)
        => _executor.ExecuteAsync(profil, """
            SELECT TOP 25
                   CAST(qs.total_elapsed_time / qs.execution_count / 1000.0 AS decimal(18,1)) AS [ort süre (ms)],
                   CAST(qs.max_elapsed_time / 1000.0 AS decimal(18,1)) AS [en uzun (ms)],
                   qs.execution_count AS [çalıştırma],
                   CAST(qs.total_elapsed_time / 1000.0 AS decimal(18,1)) AS [toplam süre (ms)],
                   CAST(qs.total_worker_time / qs.execution_count / 1000.0 AS decimal(18,1)) AS [ort CPU (ms)],
                   qs.total_logical_reads / qs.execution_count AS [ort mantıksal okuma],
                   ISNULL(DB_NAME(t.dbid), '') AS veritabani,
                   qs.last_execution_time AS [son çalıştırma],
                   SUBSTRING(t.text, (qs.statement_start_offset / 2) + 1,
                       ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(t.text)
                         ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1) AS sorgu
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
            WHERE qs.execution_count > 0
            ORDER BY qs.total_elapsed_time / qs.execution_count DESC;

            SELECT TOP 25
                   CAST(qs.total_elapsed_time / 1000.0 AS decimal(18,1)) AS [toplam süre (ms)],
                   qs.execution_count AS [çalıştırma],
                   CAST(qs.total_elapsed_time / qs.execution_count / 1000.0 AS decimal(18,1)) AS [ort süre (ms)],
                   CAST(qs.total_worker_time / 1000.0 AS decimal(18,1)) AS [toplam CPU (ms)],
                   qs.total_logical_reads AS [mantıksal okuma],
                   ISNULL(DB_NAME(t.dbid), '') AS veritabani,
                   qs.last_execution_time AS [son çalıştırma],
                   SUBSTRING(t.text, (qs.statement_start_offset / 2) + 1,
                       ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(t.text)
                         ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1) AS sorgu
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
            WHERE qs.execution_count > 0
            ORDER BY qs.total_elapsed_time DESC;
            """, ExecuteOptions.Varsayilan, ct);

    /// <summary>
    /// Aktivite/Denetim (v10-S3, B): ŞU AN kim bağlı ve ne çalıştırıyor — tüm kullanıcı oturumları,
    /// aktifse çalışan komut + SQL metni + süre. "Kim ne yapıyor"un canlı yüzü (geçmiş = denetim izi/A).
    /// </summary>
    public Task<QueryResult> CanliAktiviteAsync(ConnectionProfile profil, CancellationToken ct)
        => _executor.ExecuteAsync(profil, """
            SELECT s.session_id AS oturum,
                   ISNULL(s.login_name, '?') AS [giriş],
                   ISNULL(s.host_name, '') AS makine,
                   ISNULL(s.program_name, '') AS program,
                   ISNULL(DB_NAME(ISNULL(r.database_id, s.database_id)), '?') AS veritabani,
                   ISNULL(r.status, s.status) AS durum,
                   r.command AS komut,
                   CAST(ISNULL(r.total_elapsed_time, 0) / 1000.0 AS decimal(18,1)) AS [süre (ms)],
                   SUBSTRING(t.text, 1, 300) AS [çalışan sorgu],
                   s.last_request_start_time AS [son istek]
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE s.is_user_process = 1
            ORDER BY ISNULL(r.total_elapsed_time, 0) DESC, s.last_request_start_time DESC;
            """, ExecuteOptions.Varsayilan, ct);

    /// <summary>Seçili veritabanının en büyük 50 tablosu (R1.3).</summary>
    public Task<QueryResult> TabloBoyutlariAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
        => _executor.ExecuteAsync(profil, """
            SELECT TOP 50 QUOTENAME(s.name) + '.' + QUOTENAME(o.name) AS tablo,
                   SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count END) AS [satır],
                   CAST(SUM(ps.reserved_page_count) * 8 / 1024.0 AS decimal(18,1)) AS [ayrılan (MB)]
            FROM sys.dm_db_partition_stats ps
            JOIN sys.objects o ON o.object_id = ps.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            GROUP BY s.name, o.name
            ORDER BY SUM(ps.reserved_page_count) DESC;
            """, new ExecuteOptions { VeritabaniOverride = veritabani }, ct);

    /// <summary>
    /// Bakım paneli (V5-S3 + R1.3 kapanışı 2026-07-26): tempdb şişmesi · bloklama ZİNCİRİ ·
    /// istatistik tazeliği · BEKLEME ANALİZİ — 6 set.
    /// Hepsi UCUZ katalog/DMV okumasıdır, panel yenilemesinde koşulabilir.
    ///
    /// Panelin en değerli sekmesi (kullanıcı değerlendirmesi 2026-07-19): istatistik tazeliği
    /// execution plan'daki tahmin/gerçek sapmasının kök nedenini gösterir, bloklama zinciri de
    /// UYKUDAKİ kök bloklayanı — başka hiçbir yerde görünmeyen bilgi.
    /// </summary>
    public Task<QueryResult> BakimVerileriAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
        => _executor.ExecuteAsync(profil, """
            -- 1) tempdb neyle dolu? Şişmenin KAYNAĞINI ayırt eder: kullanıcı nesnesi
            -- (geçici tablo), iç nesne (sort/hash taşması) ya da version store (RCSI/snapshot).
            SELECT CAST(SUM(user_object_reserved_page_count) * 8 / 1024.0 AS decimal(18,1)) AS [kullanıcı nesnesi (MB)],
                   CAST(SUM(internal_object_reserved_page_count) * 8 / 1024.0 AS decimal(18,1)) AS [iç nesne (MB)],
                   CAST(SUM(version_store_reserved_page_count) * 8 / 1024.0 AS decimal(18,1)) AS [version store (MB)],
                   CAST(SUM(unallocated_extent_page_count) * 8 / 1024.0 AS decimal(18,1)) AS [boş (MB)]
            FROM tempdb.sys.dm_db_file_space_usage;

            -- 2) tempdb'yi en çok tüketen oturumlar — "kim şişiriyor" sorusunun cevabı.
            SELECT TOP 10 su.session_id AS oturum,
                   ISNULL(s.login_name, '?') AS [giriş],
                   ISNULL(DB_NAME(s.database_id), '?') AS veritabani,
                   CAST((su.user_objects_alloc_page_count - su.user_objects_dealloc_page_count)
                        * 8 / 1024.0 AS decimal(18,1)) AS [kullanıcı nesnesi (MB)],
                   CAST((su.internal_objects_alloc_page_count - su.internal_objects_dealloc_page_count)
                        * 8 / 1024.0 AS decimal(18,1)) AS [iç nesne (MB)],
                   ISNULL(t.text, '') AS [son sorgu]
            FROM sys.dm_db_session_space_usage su
            JOIN sys.dm_exec_sessions s ON s.session_id = su.session_id
            LEFT JOIN sys.dm_exec_connections c ON c.session_id = su.session_id
            OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) t
            WHERE su.session_id > 50
              AND (su.user_objects_alloc_page_count + su.internal_objects_alloc_page_count) > 0
            ORDER BY (su.user_objects_alloc_page_count + su.internal_objects_alloc_page_count) DESC;

            -- 3) Bloklama ZİNCİRİ. Havuz'daki bloklama listesi yalnız DOĞRUDAN bloklayanı
            -- gösterir; asıl suçlu genelde zincirin KÖKÜDÜR ve çoğu zaman UYKUDA olduğu için
            -- dm_exec_requests'te HİÇ görünmez (açık işlem tutup bekleyen uygulama). Burada
            -- kök bloklayan ayrıca listelenir; bu, panelin en işe yarar bakım sinyalidir.
            WITH bloklu AS (
                SELECT session_id, blocking_session_id, wait_type, wait_time
                FROM sys.dm_exec_requests
                WHERE blocking_session_id <> 0
            ),
            zincir AS (
                -- KÖK: birini bloklayan ama kendisi bloklanmayan oturum. Kritik nokta:
                -- kök çoğu zaman UYKUDADIR (açık işlem tutup bekleyen uygulama) ve o hâlde
                -- dm_exec_requests'te SATIRI YOKTUR — bu yüzden kök, "bloklayan" sütunundan
                -- türetilir, requests'ten seçilmez. Aksi hâlde en tipik senaryoda zincir
                -- bomboş dönerdi.
                SELECT DISTINCT b.blocking_session_id AS session_id,
                       -- smallint: özyinelemeli CTE'de anchor ile tekrar eden kısmın
                       -- tipleri BİREBİR aynı olmalı (DMV'de smallint, düz 0 int'tir).
                       CAST(0 AS smallint) AS blocking_session_id,
                       CAST(NULL AS nvarchar(60)) AS wait_type,
                       CAST(NULL AS int) AS wait_time,
                       CAST(b.blocking_session_id AS nvarchar(max)) AS yol,
                       0 AS derinlik
                FROM bloklu b
                WHERE NOT EXISTS (SELECT 1 FROM bloklu x WHERE x.session_id = b.blocking_session_id)
                UNION ALL
                SELECT b.session_id, b.blocking_session_id, b.wait_type, b.wait_time,
                       z.yol + N' → ' + CAST(b.session_id AS nvarchar(max)), z.derinlik + 1
                FROM bloklu b
                JOIN zincir z ON b.blocking_session_id = z.session_id
            )
            SELECT z.derinlik AS [derinlik], z.yol AS [zincir],
                   z.session_id AS oturum, z.wait_type AS [bekleme türü],
                   z.wait_time AS [bekleme (ms)],
                   ISNULL(s.login_name, '?') AS [giriş], ISNULL(s.host_name, '') AS makine,
                   ISNULL(s.program_name, '') AS uygulama, s.status AS durum,
                   ISNULL(t.text, '') AS sorgu
            FROM zincir z
            LEFT JOIN sys.dm_exec_sessions s ON s.session_id = z.session_id
            LEFT JOIN sys.dm_exec_connections c ON c.session_id = z.session_id
            OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) t
            ORDER BY z.yol, z.derinlik
            OPTION (MAXRECURSION 100);

            -- 4) İstatistik tazeliği: optimizer'ın yanlış plan seçmesinin bir numaralı
            -- sebebi eskimiş istatistiktir (V5-S1 plan sekmesindeki "tahmin/gerçek sapması"
            -- uyarısının kök nedeni genelde burada görünür).
            SELECT TOP 50 QUOTENAME(sc.name) + '.' + QUOTENAME(o.name) AS tablo,
                   st.name AS istatistik,
                   sp.last_updated AS [son güncelleme],
                   DATEDIFF(DAY, sp.last_updated, GETDATE()) AS [yaş (gün)],
                   sp.rows AS [satır], sp.modification_counter AS [değişiklik],
                   CASE WHEN sp.rows > 0
                        THEN CAST(sp.modification_counter * 100.0 / sp.rows AS decimal(18,1))
                   END AS [değişim %]
            FROM sys.stats st
            JOIN sys.objects o ON o.object_id = st.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
            JOIN sys.schemas sc ON sc.schema_id = o.schema_id
            OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) sp
            WHERE sp.rows > 0
            ORDER BY CASE WHEN sp.rows > 0 THEN sp.modification_counter * 1.0 / sp.rows END DESC,
                     sp.last_updated;

            -- 5) ŞU AN bekleyen istekler (R1.3 "bekleyen oturumlar" — bloklamayla SINIRLI DEĞİL):
            -- IO/latch/CPU/ağ beklemeleri de görünür; bloklama zinciri yalnız kilit kavgasını
            -- gösterirdi, yavaşlığın öbür yüzü buradadır.
            SELECT r.session_id AS oturum,
                   r.wait_type AS [bekleme türü],
                   r.wait_time AS [bekleme (ms)],
                   NULLIF(r.blocking_session_id, 0) AS [bloklayan],
                   ISNULL(DB_NAME(r.database_id), '?') AS veritabani,
                   r.command AS komut, r.status AS durum,
                   ISNULL(SUBSTRING(t.text, 1, 300), '') AS sorgu
            FROM sys.dm_exec_requests r
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.session_id > 50 AND r.session_id <> @@SPID AND r.wait_type IS NOT NULL
            ORDER BY r.wait_time DESC;

            -- 6) Sunucu geneli BİRİKMİŞ beklemeler (başlangıçtan beri; uptime kapısı üstte).
            -- Zararsız/bekçi türler süzülür (Glenn Berry süzgecinin kısaltılmışı) — kalan yüzde,
            -- sunucunun neyle vakit kaybettiğinin tek bakışlık özetidir.
            SELECT TOP 15 wait_type AS [bekleme türü],
                   CAST(wait_time_ms / 1000.0 AS decimal(18,1)) AS [toplam (sn)],
                   CAST(signal_wait_time_ms / 1000.0 AS decimal(18,1)) AS [CPU kuyruğu (sn)],
                   waiting_tasks_count AS [bekleme sayısı],
                   CAST(100.0 * wait_time_ms / NULLIF(SUM(wait_time_ms) OVER (), 0) AS decimal(5,1)) AS [yüzde]
            FROM sys.dm_os_wait_stats
            WHERE waiting_tasks_count > 0 AND wait_time_ms > 0
              AND wait_type NOT LIKE N'XE%' AND wait_type NOT LIKE N'HADR%'
              AND wait_type NOT LIKE N'SQLTRACE%' AND wait_type NOT LIKE N'BROKER%'
              AND wait_type NOT LIKE N'SLEEP%' AND wait_type NOT LIKE N'PREEMPTIVE%'
              AND wait_type NOT LIKE N'QDS%' AND wait_type NOT LIKE N'CLR%'
              AND wait_type NOT IN (N'LAZYWRITER_SLEEP', N'CHECKPOINT_QUEUE', N'DIRTY_PAGE_POLL',
                   N'LOGMGR_QUEUE', N'REQUEST_FOR_DEADLOCK_SEARCH', N'WAITFOR',
                   N'FT_IFTS_SCHEDULER_IDLE_WAIT', N'SP_SERVER_DIAGNOSTICS_SLEEP',
                   N'DISPATCHER_QUEUE_SEMAPHORE', N'SOS_WORK_DISPATCHER', N'VDI_CLIENT_OTHER')
            ORDER BY wait_time_ms DESC;
            """, new ExecuteOptions { VeritabaniOverride = veritabani }, ct);

    /// <summary>Aşamalı RCSI geçiş script'i (R1.5) — YALNIZ incelenmek üzere üretilir, çalıştırılmaz.</summary>
    public static string RcsiGecisScripti(string veritabani)
    {
        string koseli = veritabani.Replace("]", "]]");
        return $"""
            -- SQLST RCSI aşamalı geçiş taslağı — TEK TUŞLA AÇILMAZ (06-r1 R1.5):
            -- RCSI davranış değiştirir: tempdb version store yükü (+14 bayt/satır),
            -- check-then-act desenlerinde LOST UPDATE, watermark'lı ETL'lerde satır atlama.
            -- Koddaki NOLOCK hint'leri RCSI'yi BYPASS eder — önce onları temizleyin.

            -- AŞAMA 1: önce ALLOW_SNAPSHOT_ISOLATION açıp ~1 hafta version store'u izleyin:
            ALTER DATABASE [{koseli}] SET ALLOW_SNAPSHOT_ISOLATION ON;

            -- AŞAMA 2 (izleme sonrası; tek aktif bağlantı penceresi gerektirir):
            -- ALTER DATABASE [{koseli}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
            """;
    }

    private static void Dogrula(QueryResult sonuc, string ad)
    {
        if (sonuc.IptalEdildi)
            throw new OperationCanceledException();
        if (!sonuc.Basarili || sonuc.ResultSetler.Count == 0)
            throw new InvalidOperationException($"Teşhis verisi okunamadı ({ad}): {sonuc.Hata?.Mesaj ?? "beklenmeyen sonuç"}");
    }
}
