using System.Data.Common;
using SQLST.Contracts;
using Npgsql;

namespace SQLST.Infrastructure;

/// <summary>
/// PostgreSQL lehçesi (V3-S1 / Faz 1 — 08-v3r1 §4). Npgsql 10.x (PostgreSQL License).
///
/// Amaç: veri erişim gövdesine (SonucOkuyucu/SqlExecutor/DbOturum) DOKUNMADAN ikinci bir
/// motoru sürmek — soyutlamanın kanıtı. MSSQL'den sapan her şey burada:
/// çift tırnak, veritabanının dizeye gömülü olması (USE yok), Notice aboneliği,
/// SQLSTATE → SqlHata, information_schema/pg_catalog katalog sorguları.
///
/// Not: kanonik şema sonuç biçimi MssqlLehcesi ile AYNI kolon sırasını üretmek zorundadır
/// (SchemaService ikisini de aynı ayrıştırır).
/// </summary>
public sealed class PostgresLehcesi : ILehce
{
    private readonly ISecretProtector _protector;

    public PostgresLehcesi(ISecretProtector protector) => _protector = protector;

    public string MotorId => "postgres";

    /// <summary>ANSI çift tırnak; içindeki '"' ikilenir. Postgres tırnaksız adı küçük harfe katlar,
    /// bu yüzden katalogdaki tam yazım her zaman tırnaklanır (08-v3r1 §4).</summary>
    public string TirnaklaTanimlayici(string ad) => $"\"{ad.Replace("\"", "\"\"")}\"";

    public DbConnection BaglantiOlustur(ConnectionProfile profil, string? veritabaniOverride, bool havuz)
    {
        // Sunucu alanı "host" veya "host:port" olabilir (IPv6: "[::1]:5432" ya da çıplak "::1").
        (string host, int? port) = HostPortAyristirici.Ayir(profil.Sunucu);

        var b = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Timeout = profil.BaglantiTimeoutSn,
            ApplicationName = "SQLST",
            Pooling = havuz,
        };
        if (port is not null)
            b.Port = port.Value;

        // Postgres'te veritabanı bağlantı dizesine gömülüdür (USE yok) — override varsa oraya bağlanılır.
        if (!string.IsNullOrWhiteSpace(veritabaniOverride))
            b.Database = veritabaniOverride;

        // Windows kimliğinde kullanıcı/parola verilmez — Npgsql 10 GSS/SSPI'yi OS kullanıcısıyla
        // kendiliğinden müzakere eder (eski IntegratedSecurity özelliği kaldırıldı).
        if (profil.Kimlik == KimlikTuru.Sql)
        {
            b.Username = profil.KullaniciAdi ?? "";
            b.Password = profil.ParolaSifreli is null ? "" : _protector.Coz(profil.ParolaSifreli);
        }

        return new NpgsqlConnection(b.ConnectionString);
    }

    public void KomutuAyarla(DbCommand komut) { /* Npgsql'de ek ayar gerekmez */ }

    public IDisposable BilgiMesajlariniDinle(DbConnection baglanti, Action<string> topla)
    {
        var pg = (NpgsqlConnection)baglanti;
        void Dinleyici(object _, NpgsqlNoticeEventArgs e) => topla(e.Notice.MessageText);
        pg.Notice += Dinleyici;
        return new Abonelik(() => pg.Notice -= Dinleyici);
    }

    public SqlHata HataYorumla(Exception ex)
    {
        if (ex is PostgresException pg)
        {
            // SQLSTATE (5 karakterli metin) → SqlHata.Numara alanına sayısal olarak sıkıştırılamaz;
            // anlamlı kısmı mesaja katıp Numara'yı 0 bırakıyoruz.
            // Satir=0 BİLEREK (inceleme bulgusu 2026-07-23): PostgresException.Line SQL metninin
            // satırı DEĞİL, hatayı üreten PG SUNUCU C-kaynağının satırıdır (ör. 1176) — bunu
            // geçirmek "Satır 1176" yazdırıp hata alt çizgisini alakasız satıra indiriyordu.
            // Sorgu-içi konum pg.Position'dır (karakter ofseti) ama SQL metni bu katmanda yok →
            // satıra çevrilemez; 0 = "satır bilinmiyor" (çizgi çizilmez, yanlış yönlendirmez).
            string mesaj = string.IsNullOrEmpty(pg.SqlState) ? pg.MessageText : $"{pg.MessageText} (SQLSTATE {pg.SqlState})";
            return new SqlHata(mesaj, 0, 0, 0);
        }
        return new SqlHata(ex.Message, 0, 0, 0);
    }

    /// <summary>Postgres'te veritabanı bağlantıya bağlıdır — açıkken değiştirilemez, yeniden bağlanılır.</summary>
    public bool AcikBaglantidaVeritabaniDegisir => false;

    public string VeritabaniSecSql(string veritabani)
        => throw new NotSupportedException("PostgreSQL'de USE yok; veritabanı bağlantı dizesiyle seçilir.");

    // CREATE DATABASE PG'de transaction içinde koşamaz — yürütücü zaten tek komutu autocommit gönderir.
    public string? VeritabaniOlusturSql(string ad) => $"CREATE DATABASE {TirnaklaTanimlayici(ad)};";

    /// <summary>
    /// Güvenli Yazma işlem-durumu tespiti (V2-S4) Faz 1'de yalnız MSSQL'de aktiftir; PostgreSQL
    /// için "Yok" döner. Güvenli: Npgsql, bağlantı kapanışında açık transaction'ı kendiliğinden
    /// geri alır (SqlClient'in aksine — 07-r2 §2'deki explicit ROLLBACK gerekçesi Postgres'te yok).
    /// Postgres için Güvenli Yazma ileride ayrı ele alınacak (yetenek-kapılı).
    /// </summary>
    public Task<IslemDurumu> IslemDurumuAsync(DbConnection baglanti, CancellationToken ct)
        => Task.FromResult(IslemDurumu.Yok);

    public bool IslemDurumuBilinir => false;

    public string GeriAlSql() => "ROLLBACK;";

    // ── Katalog (information_schema + pg_catalog) ────────────────────────────

    public string VeritabanlariSorgusu => """
        SELECT datname,
               CASE WHEN datname IN ('postgres','template0','template1') THEN 1 ELSE 0 END AS sistem
        FROM pg_database
        WHERE datallowconn AND NOT datistemplate
        ORDER BY sistem, datname;
        """;

    /// <summary>
    /// Tek batch, dört kanonik sonuç kümesi (MssqlLehcesi ile aynı kolon sırası).
    /// </summary>
    public IReadOnlyList<string> SemaSorgulari => [SemaSorgusu];

    // FK bağları (v6-S2): pg_constraint.conkey/confkey iki paralel dizidir; unnest ... WITH
    // ORDINALITY ile pozisyona göre eşlenip kaynak↔hedef kolon çiftleri çıkarılır.
    // fk_id = con.oid (gruplama), sira = ordinality. STRING_AGG yok.
    public string YabanciAnahtarSorgusu => """
        SELECT con.oid AS fk_id, u.sira,
               nsp.nspname AS kaynak_sema, rel.relname AS kaynak_tablo, att.attname AS kaynak_kolon,
               fnsp.nspname AS hedef_sema, frel.relname AS hedef_tablo, fatt.attname AS hedef_kolon,
               con.conname AS kisit_adi
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
        JOIN pg_class frel ON frel.oid = con.confrelid
        JOIN pg_namespace fnsp ON fnsp.oid = frel.relnamespace
        JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS u(kaynak_attnum, hedef_attnum, sira) ON true
        JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = u.kaynak_attnum
        JOIN pg_attribute fatt ON fatt.attrelid = con.confrelid AND fatt.attnum = u.hedef_attnum
        WHERE con.contype = 'f' AND nsp.nspname NOT IN ('pg_catalog', 'information_schema')
        ORDER BY con.oid, u.sira
        """;

    // Index farkı (v7 borç kapanışı): PK-destekli indeks (indisprimary) HARİÇ; benzersizlik
    // indisunique'ten. indkey=0 (ifade indeksleri) atlanır — attnum 0'ın pg_attribute karşılığı yok.
    // benzersiz CASE→1/0 (dört motorla aynı; SchemaService Convert.ToInt32 bekler). STRING_AGG yok.
    public string IndeksSorgusu => """
        SELECT nsp.nspname AS sema, tbl.relname AS tablo, idx.relname AS indeks,
               CASE WHEN i.indisunique THEN 1 ELSE 0 END AS benzersiz, k.sira AS sira, att.attname AS kolon
        FROM pg_index i
        JOIN pg_class idx ON idx.oid = i.indexrelid
        JOIN pg_class tbl ON tbl.oid = i.indrelid
        JOIN pg_namespace nsp ON nsp.oid = tbl.relnamespace
        JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, sira) ON true
        JOIN pg_attribute att ON att.attrelid = i.indrelid AND att.attnum = k.attnum
        WHERE i.indisprimary = false AND k.attnum <> 0
          AND nsp.nspname NOT IN ('pg_catalog', 'information_schema')
        ORDER BY nsp.nspname, tbl.relname, idx.relname, k.sira
        """;

    private static string SemaSorgusu => """
        SELECT current_database();

        SELECT table_schema AS sema, table_name AS ad,
               CASE WHEN table_type = 'VIEW' THEN 'V' ELSE 'U' END AS tur
        FROM information_schema.tables
        WHERE table_schema NOT IN ('pg_catalog','information_schema')
        UNION ALL
        SELECT routine_schema, routine_name,
               CASE WHEN routine_type = 'PROCEDURE' THEN 'P' ELSE 'FN' END
        FROM information_schema.routines
        WHERE routine_schema NOT IN ('pg_catalog','information_schema')
        ORDER BY sema, ad;

        SELECT c.table_schema AS sema, c.table_name AS ad, c.column_name AS kolon,
               c.udt_name AS tip,
               COALESCE(c.character_maximum_length, 0) AS uzunluk,
               COALESCE(c.numeric_precision, 0) AS kesinlik,
               COALESCE(c.numeric_scale, 0) AS olcek,
               (c.is_nullable = 'YES') AS null_olabilir,
               CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS pk
        FROM information_schema.columns c
        LEFT JOIN (
            SELECT kcu.table_schema, kcu.table_name, kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name
             AND kcu.constraint_schema = tc.constraint_schema
            WHERE tc.constraint_type = 'PRIMARY KEY'
        ) pk ON pk.table_schema = c.table_schema AND pk.table_name = c.table_name AND pk.column_name = c.column_name
        WHERE c.table_schema NOT IN ('pg_catalog','information_schema')
        ORDER BY c.table_schema, c.table_name, c.ordinal_position;

        SELECT r.routine_schema AS sema, r.routine_name AS ad,
               COALESCE(p.parameter_name, '$' || p.ordinal_position::text) AS parametre,
               COALESCE(p.data_type, '') AS tip,
               0 AS uzunluk, 0 AS kesinlik, 0 AS olcek,
               (p.parameter_mode IN ('OUT','INOUT')) AS cikis
        FROM information_schema.routines r
        JOIN information_schema.parameters p
          ON p.specific_schema = r.specific_schema AND p.specific_name = r.specific_name
        WHERE r.routine_schema NOT IN ('pg_catalog','information_schema')
        ORDER BY r.routine_schema, r.routine_name, p.ordinal_position;
        """;

    public string TanimSorgusu(SemaNesnesi nesne)
    {
        string sema = KacisliLiteral(nesne.Sema);
        string ad = KacisliLiteral(nesne.Ad);
        string tamAd = KacisliLiteral($"{TirnaklaTanimlayici(nesne.Sema)}.{TirnaklaTanimlayici(nesne.Ad)}");
        return nesne.Tur == SemaNesneTuru.View
            ? $"SELECT pg_get_viewdef('{tamAd}'::regclass, true) AS tanim;"
            : $"""
                SELECT pg_get_functiondef(p.oid) AS tanim
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = '{sema}' AND p.proname = '{ad}'
                LIMIT 1;
                """;
    }

    public SemaNesneTuru TurCevir(string kod) => kod.Trim() switch
    {
        "U" => SemaNesneTuru.Tablo,
        "V" => SemaNesneTuru.View,
        "P" => SemaNesneTuru.StoredProcedure,
        "FN" => SemaNesneTuru.Fonksiyon,
        _ => throw new ArgumentOutOfRangeException(nameof(kod), kod, "Beklenmeyen tür kodu"),
    };

    public string IlkNSatirSorgusu(SemaNesnesi nesne, int n)
        => $"SELECT * FROM {TirnaklaTanimlayici(nesne.Sema)}.{TirnaklaTanimlayici(nesne.Ad)} LIMIT {n};";

    /// <summary>udt_name + uzunluk/kesinlik/ölçek → görüntülük tip: varchar(50), numeric(18,2), int4…</summary>
    public string TipYaz(string tip, int uzunluk, int kesinlik, int olcek) => tip switch
    {
        "varchar" or "bpchar" or "char" when uzunluk > 0 => $"{tip}({uzunluk})",
        "numeric" or "decimal" when kesinlik > 0 => $"{tip}({kesinlik},{olcek})",
        _ => tip,
    };

    /// <summary>
    /// Edit modu kolon üst verisi (V4-S1). Kaynak <c>pg_attribute</c> — information_schema'dan
    /// daha eksiksizdir: identity (<c>attidentity</c>, PG 10+) ile eski <c>serial</c> (nextval
    /// default'u) AYRI kavramlardır ve ikisi de "sunucu üretir" sayılmalıdır; üretilmiş kolon
    /// <c>attgenerated</c> (PG 12+). Uzunluk/kesinlik <c>atttypmod</c>'dan çözülür.
    /// <b>Sürüm kolonu 0 döner:</b> PG'de rowversion yoktur — satır kimliği PK + değişen
    /// kolonların eski değerleriyle korunur (07-r2 §4 yedek yolu). *(PG'nin sistem kolonu
    /// <c>xmin</c> gerçek bir iyimser kilit belirtecidir; grid'in SELECT'ini de değiştirmek
    /// gerektiğinden V4-S1 kapsamına alınmadı — ileride ayrı iş.)*
    /// </summary>
    public string DuzenlemeMetaSorgusu(SemaNesnesi tablo)
    {
        string sema = KacisliLiteral(tablo.Sema);
        string ad = KacisliLiteral(tablo.Ad);
        return $"""
            SELECT a.attname AS ad,
                   t.typname AS tip,
                   COALESCE(CASE WHEN a.atttypmod > 0 AND t.typname IN ('varchar','bpchar','char')
                                 THEN a.atttypmod - 4 END, 0) AS uzunluk,
                   COALESCE(CASE WHEN a.atttypmod > 0 AND t.typname IN ('numeric','decimal')
                                 THEN ((a.atttypmod - 4) >> 16) & 65535 END, 0) AS kesinlik,
                   COALESCE(CASE WHEN a.atttypmod > 0 AND t.typname IN ('numeric','decimal')
                                 THEN (a.atttypmod - 4) & 65535 END, 0) AS olcek,
                   NOT a.attnotnull AS null_olabilir,
                   (a.attidentity <> '' OR COALESCE(pg_get_expr(d.adbin, d.adrelid), '') LIKE 'nextval(%')
                       AS identity_mi,
                   (a.attgenerated <> '') AS computed_mi,
                   0 AS surum,
                   CASE WHEN pk.attnum IS NOT NULL THEN 1 ELSE 0 END AS pk
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN (
                SELECT i.indrelid, UNNEST(i.indkey) AS attnum
                FROM pg_index i
                WHERE i.indisprimary
            ) pk ON pk.indrelid = a.attrelid AND pk.attnum = a.attnum
            WHERE n.nspname = '{sema}' AND c.relname = '{ad}'
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum;
            """;
    }

    public bool DuzenlemeDestekler => true;

    public LiteralKurallari LiteralKurallari => LiteralKurallari.Postgres;

    public string TamAdYaz(string sema, string tablo)
        => $"{TirnaklaTanimlayici(sema)}.{TirnaklaTanimlayici(tablo)}";

    /// <summary>
    /// Veri karşılaştırma satır parmak izi (v7-S2): <c>md5(satır::text)</c>. PostgreSQL'de tablo adı
    /// bir ifadede satırın kendisini (composite) verir; <c>::text</c> tüm kolonları sıralı metne çevirir
    /// — tip dönüşümü/kolon listesi gerekmez (<paramref name="tumKolonlar"/> kullanılmaz).
    /// </summary>
    public string? SatirHashSorgusu(
        string? sema, string tablo, IReadOnlyList<string> anahtarKolonlar,
        IReadOnlyList<string> tumKolonlar, string? whereKosulu)
    {
        if (anahtarKolonlar.Count == 0)
            return null;
        string satirRef = TirnaklaTanimlayici(tablo); // FROM'daki tablonun satır değişkeni = adı
        string tam = sema is null ? satirRef : TamAdYaz(sema, tablo);
        string anahtar = string.Join(", ", anahtarKolonlar.Select(TirnaklaTanimlayici));
        string where = string.IsNullOrWhiteSpace(whereKosulu) ? "" : $" WHERE {whereKosulu}";
        return $"SELECT {anahtar}, md5({satirRef}::text) AS __hash FROM {tam}{where}";
    }

    public string BosSatirEkleSql(string tamAd) => $"INSERT INTO {tamAd} DEFAULT VALUES";

    public string IfadeSonu => ";";

    public string IslemBaslatSql => "BEGIN;";

    public string CommitSql => "COMMIT;";

    public bool GuvenliYazmaDestekler => true;

    /// <summary>
    /// PostgreSQL'in gerçek gücü: <b>DDL de işlemseldir</b>. <c>CREATE TABLE</c>,
    /// <c>ALTER TABLE</c>, hatta <c>DROP TABLE</c> açık bir işlem içinde geri alınabilir —
    /// MySQL/Oracle'ın aksine. Bu yüzden Güvenli Yazma PG'de HER yazma türünde geçerlidir.
    /// </summary>
    public bool OrtukCommitYaparMi(string sql) => false;

    public bool PlanDestekler => true;

    /// <summary>PostgreSQL planı sorgunun kendisini sarar — oturum düzeyinde açma/kapama YOKTUR.</summary>
    public string PlanAcSql(bool gercek) => "";

    public string PlanKapatSql(bool gercek) => "";

    /// <summary>
    /// Sorguyu <c>EXPLAIN</c> ile sarar (V5-S1b).
    /// <list type="bullet">
    /// <item><b>VERBOSE şart:</b> şema adı ve paralel planlarda DOLU <c>Workers[]</c> yalnız
    /// onunla gelir (yalnız ANALYZE ile <c>Workers: []</c> boş dizi döner — canlı doğrulandı).</item>
    /// <item><b>BUFFERS yalnız ANALYZE ile</b> geçerlidir; tahmini planda kullanılamaz.</item>
    /// <item><b>ANALYZE sorguyu GERÇEKTEN çalıştırır</b> — bir <c>DELETE</c> planı alınırken
    /// satırlar silinir (canlı kanıt: 1000 → 900). Çağıran yazma denetimini HAM sql üzerinde,
    /// sarmadan önce yapar; bkz. <see cref="PlanSorgusuYaz"/> sözleşme notu.</item>
    /// </list>
    /// </summary>
    public string PlanSorgusuYaz(string sql, bool gercek)
    {
        string govde = sql.Trim().TrimEnd(';');
        return gercek
            ? $"EXPLAIN (ANALYZE, VERBOSE, BUFFERS, FORMAT JSON) {govde}"
            : $"EXPLAIN (VERBOSE, FORMAT JSON) {govde}";
    }

    /// <summary><c>EXPLAIN</c> tek ifade sarar; çok ifadeli metin anlamsız sözdizimi hatası verir.</summary>
    public bool PlanTekIfadeIster => true;

    /// <summary><c>EXPLAIN ANALYZE</c> gerçek satır sayaçlarını getirir (sorguyu çalıştırarak).</summary>
    public bool PlanGercekDestekler => true;

    /// <summary>Plan tek adımlıdır — <c>EXPLAIN</c> sonucu doğrudan döner.</summary>
    public string PlanOkumaSql(bool gercek) => "";

    /// <summary>Plan tek satır/tek kolonda gelir; kolon adı <c>QUERY PLAN</c>'dır (MSSQL'deki gibi değil).</summary>
    public SorguPlani PlanCoz(QueryResult sonuc, bool gercek)
    {
        foreach (ResultSetData set in sonuc.ResultSetler)
        {
            if (set.Kolonlar.Count != 1 || set.Satirlar.Count == 0)
                continue;

            // Satırlar birden çok parçaya bölünmüş olabilir → birleştir
            string json = string.Concat(set.Satirlar.Select(s => s[0]?.ToString() ?? ""));
            if (json.TrimStart().StartsWith('['))
                return PostgresPlanOkuyucu.Coz(json, gercek);
        }

        throw new InvalidOperationException(
            "Sunucu plan döndürmedi (sorgu plan üretmeyen bir ifade olabilir).");
    }

    /// <summary>
    /// Fonksiyon/prosedür gövdeleri <c>pg_get_functiondef</c>, view'lar
    /// <c>pg_get_viewdef</c> ile okunur. Sistem şemaları (<c>pg_*</c>, <c>information_schema</c>)
    /// dışlanır — kullanıcı kendi nesnelerini arar, katalogda boğulmaz.
    /// </summary>
    public string MetinAramaSorgusu(string aranan)
    {
        string desen = LikeKacir(aranan);
        return $"""
            SELECT n.nspname AS sema, p.proname AS ad, 'P' AS tur, pg_get_functiondef(p.oid) AS tanim
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
              -- pg_get_functiondef AGGREGATE ve WINDOW fonksiyonlarında HATA fırlatır
              -- ("array_agg is an aggregate function"), tek bir tanesi bile tüm aramayı
              -- düşürür → yalnız normal fonksiyon ('f') ve prosedür ('p') taranır.
              AND p.prokind IN ('f', 'p')
              AND pg_get_functiondef(p.oid) LIKE '%{desen}%' ESCAPE '\'
            UNION ALL
            SELECT n.nspname, c.relname, 'V', pg_get_viewdef(c.oid, true)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('v', 'm')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND pg_get_viewdef(c.oid, true) LIKE '%{desen}%' ESCAPE '\'
            ORDER BY 1, 2
            LIMIT {ILehce.AramaTavani};
            """;
    }

    /// <summary>LIKE jokerlerini (<c>%</c>, <c>_</c>) kaçırır; yoksa yanlış eşleşme olur.</summary>
    private static string LikeKacir(string s) => s
        .Replace("'", "''")
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    public bool KiyasGuvenliMi(string tip, int uzunluk) => tip is not ("float4" or "float8" or "text" or "xml" or "json" or "jsonb");

    /// <summary>
    /// PostgreSQL Yönetim Paneli (V3): SQL Server DMV panelinin PG karşılıkları —
    /// pg_stat_user_indexes (kullanılmayan index), tablo/index boyutları, canlı oturumlar
    /// ve kilitler, vacuum/ölü satır sağlığı, veritabanı geneli önbellek isabeti.
    /// </summary>
    public IReadOnlyList<TeshisBolumu> TeshisBolumleri =>
    [
        new("Kullanılmayan indexler",
            "idx_scan = 0 → sayaç başlangıcından beri hiç kullanılmamış. Silmeden önce uygulamanın tüm yollarını düşünün (PK/unique kısıtları da index'tir).",
            """
            SELECT s.schemaname AS sema, s.relname AS tablo, s.indexrelname AS index_adi,
                   s.idx_scan AS taramalar,
                   pg_size_pretty(pg_relation_size(s.indexrelid)) AS boyut,
                   CASE WHEN s.idx_scan = 0 THEN '⚠ hiç kullanılmadı' ELSE '' END AS not
            FROM pg_stat_user_indexes s
            JOIN pg_index i ON i.indexrelid = s.indexrelid
            WHERE NOT i.indisprimary AND NOT i.indisunique
            ORDER BY s.idx_scan, pg_relation_size(s.indexrelid) DESC
            LIMIT 200
            """,
            Takip: "TAKİP: taramalar = 0 olan ve boyutu büyük index'ler. Her index yazma "
                 + "maliyetidir: tabloya her INSERT/UPDATE'te güncellenir ama hiç okunmuyorsa "
                 + "bedelini boşuna ödersiniz. SİLMEDEN ÖNCE: sayaç sunucunun son açılışından "
                 + "beri sayar — ayda bir çalışan rapor sorgusu henüz görülmemiş olabilir; "
                 + "en az bir tam iş döngüsü (ay sonu/yıl sonu dahil) beklemeden silmeyin.",
            VeritabaniGerekir: true),

        // 2026-07-19 SADELEŞTİRME: last_analyze kolonu buradan çıkarıldı — ANALYZE tazeliği
        // "İstatistik tazeliği" bölümünün işidir ve orada oranıyla birlikte verilir.
        // Burada VACUUM tarihleri kalır: n_dead_tup (şişme) hikâyesini onlar tamamlar.
        new("Tablo boyutları ve tarama biçimi",
            "seq_scan yüksek + idx_scan düşük olan büyük tablolar index adayıdır; n_dead_tup şişmeyi gösterir, VACUUM tarihleri de o şişmenin neden temizlenmediğini.",
            """
            SELECT s.schemaname AS sema, s.relname AS tablo,
                   pg_size_pretty(pg_total_relation_size(s.relid)) AS toplam,
                   pg_size_pretty(pg_relation_size(s.relid)) AS veri,
                   s.n_live_tup AS canli_satir, s.n_dead_tup AS olu_satir,
                   s.seq_scan AS ardisik_tarama, s.idx_scan AS index_tarama,
                   s.last_vacuum, s.last_autovacuum
            FROM pg_stat_user_tables s
            ORDER BY pg_total_relation_size(s.relid) DESC
            LIMIT 100
            """,
            Takip: "TAKİP: (1) ardisik_tarama yüksek + index_tarama düşük olan BÜYÜK tablolar — "
                 + "index adayıdır; küçük tabloda ardışık tarama normaldir, PG onu bilerek seçer. "
                 + "(2) olu_satir / canli_satir oranı kabaca %20'yi geçiyorsa şişme var: yer "
                 + "kaplar ve taramayı yavaşlatır. (3) O satırda last_vacuum ve last_autovacuum "
                 + "boş ya da çok eskiyse şişmenin NEDENİ budur — autovacuum bu tabloya "
                 + "yetişemiyor demektir.",
            VeritabaniGerekir: true),

        new("Canlı oturumlar ve bekleyenler",
            "wait_event_type dolu olan satırlar bir şeyi bekliyor; state='idle in transaction' uzun sürerse kilit tutuyor olabilir.",
            """
            SELECT pid, usename AS kullanici, datname AS veritabani, state AS durum,
                   wait_event_type AS bekleme_turu, wait_event AS bekleme,
                   now() - query_start AS suredir_calisiyor,
                   now() - state_change AS durumda_suredir,
                   left(query, 200) AS sorgu
            FROM pg_stat_activity
            WHERE pid <> pg_backend_pid() AND state IS NOT NULL
            ORDER BY (state = 'active') DESC, query_start
            LIMIT 100
            """,
            Takip: "TAKİP: (1) durum = 'idle in transaction' VE durumda_suredir dakikalar "
                 + "mertebesindeyse — panelde görebileceğiniz en tehlikeli satır budur: açık "
                 + "işlem hem kilit tutar hem autovacuum'u engeller, üstelik hiçbir sorgu "
                 + "çalıştırmadığı için masum görünür. (2) bekleme_turu dolu olan satırlar bir "
                 + "şey bekliyor; 'Lock' ise ayrıntısı bloklama zinciri bölümündedir. "
                 + "(3) suredir_calisiyor beklenenden uzun sorgular."),

        // pg_blocking_pids() "kim kimi bekletiyor"un tam karşılığıdır; MSSQL bakım panelindeki
        // bloklama zincirinin PostgreSQL eşleniğidir.
        //
        // 2026-07-19 SADELEŞTİRME: buradaki eski "Kilit çakışmaları" bölümü (pg_locks,
        // granted = false) KALDIRILDI. Bekleyenleri gösteriyordu ama KÖK bloklayanı
        // söylemiyordu — MSSQL'de aynı gerekçeyle silinen "Bloklanan oturumlar" setiyle
        // birebir aynı durum. Aynı soruyu iki yerde, biri eksik cevaplamak kullanıcıyı
        // yanlış oturuma yönlendirir.
        new("Bloklama zinciri (kök bloklayan)",
            "Kimin kimi beklettiğini gösterir. Asıl suçlu genelde zincirin KÖKÜDÜR ve çoğu zaman 'idle in transaction' durumundadır — yani hiçbir sorgu çalıştırmadığı için 'çalışan sorgu' listelerinde göze çarpmaz.",
            """
            SELECT bekleyen.pid AS bekleyen_pid,
                   bekleyen.usename AS bekleyen_kullanici,
                   bekleyen.wait_event_type AS bekleme_turu,
                   now() - bekleyen.query_start AS bekleme_suresi,
                   left(bekleyen.query, 200) AS bekleyen_sorgu,
                   bloklayan.pid AS bloklayan_pid,
                   bloklayan.usename AS bloklayan_kullanici,
                   bloklayan.state AS bloklayan_durum,
                   now() - bloklayan.state_change AS bloklayan_durumda_suredir,
                   left(bloklayan.query, 200) AS bloklayan_son_sorgu,
                   CASE WHEN cardinality(pg_blocking_pids(bloklayan.pid)) = 0
                        THEN '⚠ KÖK bloklayan' ELSE '' END AS not
            FROM pg_stat_activity bekleyen
            JOIN LATERAL unnest(pg_blocking_pids(bekleyen.pid)) AS engel(pid) ON true
            JOIN pg_stat_activity bloklayan ON bloklayan.pid = engel.pid
            ORDER BY bekleyen.query_start
            LIMIT 100
            """,
            Takip: "TAKİP: '⚠ KÖK bloklayan' işaretli satır — müdahale edilecek oturum ODUR, "
                 + "bekleyenler değil. Kökü kurtarınca zincirin tamamı çözülür. bloklayan_durum "
                 + "sütunu genelde 'idle in transaction' çıkar: sorumlu, COMMIT/ROLLBACK "
                 + "yapmadan bekleyen uygulamadır — çoğu zaman kullanıcının açık bıraktığı bir "
                 + "ekran ya da hata sonrası işlemi kapatmayan bir kod yolu. bloklayan_son_sorgu "
                 + "hangi kod olduğunu söyler. Aynı kök tekrar tekrar çıkıyorsa çözüm oturumu "
                 + "öldürmek değil, o kod yolundaki işlem yönetimini düzeltmektir."),

        // V5-S3: kötü plan seçiminin bir numaralı sebebi eskimiş istatistiktir; asıl bilgi
        // tarihin kendisi değil BAYATLIK ORANIDIR (n_mod_since_analyze / n_live_tup).
        // ANALYZE tazeliğinin tek adresi burasıdır (2026-07-19 sadeleştirmesi).
        new("İstatistik tazeliği",
            "n_mod_since_analyze, son ANALYZE'dan beri değişen satır sayısıdır. Canlı satıra oranı büyüdükçe planlayıcının tahminleri bozulur — execution plan'daki tahmin/gerçek sapmalarının kök nedeni genelde buradadır.",
            """
            SELECT s.schemaname AS sema, s.relname AS tablo,
                   s.n_live_tup AS canli_satir,
                   s.n_mod_since_analyze AS analiz_sonrasi_degisiklik,
                   CASE WHEN s.n_live_tup > 0
                        THEN ROUND(100.0 * s.n_mod_since_analyze / s.n_live_tup, 1)
                   END AS degisim_yuzde,
                   s.last_analyze, s.last_autoanalyze
            FROM pg_stat_user_tables s
            WHERE s.n_live_tup > 0
            ORDER BY CASE WHEN s.n_live_tup > 0
                          THEN s.n_mod_since_analyze::numeric / s.n_live_tup END DESC NULLS LAST
            LIMIT 100
            """,
            Takip: "TAKİP: degisim_yuzde sütunu — liste zaten en bayattan başlar, yani ÜST "
                 + "SATIRLAR sorunlu olanlardır. Kabaca %10-20'yi geçen ve büyük olan tablolarda "
                 + "planlayıcının satır tahminleri bozulur. BELİRTİ: bir sorgu dün hızlıyken "
                 + "bugün yavaşladıysa ve planında tahmin ile gerçek satır sayısı arasında "
                 + "büyük fark varsa, önce buraya bakın. ÇÖZÜM: ANALYZE <tablo>; — ucuzdur, "
                 + "kilitlemez. last_autoanalyze hiç dolmuyorsa autovacuum ayarları o tabloya "
                 + "yetişmiyordur."),

        new("Veritabanı sağlığı",
            "Önbellek isabeti (%99 üstü iyi), işlem sayıları, ölü kilit ve geçici dosya göstergeleri.",
            """
            SELECT datname AS veritabani, numbackends AS baglanti,
                   xact_commit AS commit_sayisi, xact_rollback AS rollback_sayisi,
                   ROUND(100.0 * blks_hit / NULLIF(blks_hit + blks_read, 0), 2) AS onbellek_isabeti_yuzde,
                   tup_returned AS okunan_satir, tup_fetched AS getirilen_satir,
                   deadlocks AS olu_kilit, temp_files AS gecici_dosya,
                   pg_size_pretty(temp_bytes) AS gecici_boyut,
                   pg_size_pretty(pg_database_size(datname)) AS boyut
            FROM pg_stat_database
            WHERE datname NOT IN ('template0','template1')
            ORDER BY pg_database_size(datname) DESC
            """,
            Takip: "TAKİP: (1) onbellek_isabeti_yuzde — %99 üstü sağlıklıdır; kalıcı olarak "
                 + "%90'ın altındaysa çalışma kümesi belleğe sığmıyor (shared_buffers ya da RAM "
                 + "yetersiz). Yeni açılmış sunucuda düşük çıkması normaldir, önbellek henüz "
                 + "ısınmamıştır. (2) olu_kilit sürekli artıyorsa uygulama iki kaynağı FARKLI "
                 + "SIRAYLA kilitliyor demektir — kod düzeyinde bir hatadır, ayarla çözülmez. "
                 + "(3) gecici_dosya / gecici_boyut büyüyorsa sıralama ve hash işlemleri belleğe "
                 + "sığmayıp diske taşıyor (work_mem). (4) rollback_sayisi / commit_sayisi oranı "
                 + "beklenmedik ölçüde yüksekse uygulama sessizce hata alıyor olabilir."),
    ];

    private static string KacisliLiteral(string s) => s.Replace("'", "''");

    private sealed class Abonelik : IDisposable
    {
        private readonly Action _birak;
        private bool _atildi;

        public Abonelik(Action birak) => _birak = birak;

        public void Dispose()
        {
            if (_atildi)
                return;
            _atildi = true;
            _birak();
        }
    }
}
