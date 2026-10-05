using System.Data.Common;
using SQLST.Contracts;
using MySqlConnector;

namespace SQLST.Infrastructure;

/// <summary>
/// MySQL / MariaDB lehçesi (V3-S1 Faz 2 — 08-v3r1 §4). Sürücü: MySqlConnector (MIT) —
/// MariaDB'nin resmî önerisi de bu pakettir; Oracle'ın MySql.Data'sı (GPL) BİLİNÇLİ
/// kullanılmaz (lisans tuzağı, 08-v3r1 §1).
///
/// MSSQL'den sapmalar: backtick tırnak; şema = veritabanı (dbo katmanı yok, USE çalışır);
/// katalog information_schema; tanım okuma information_schema.VIEWS/ROUTINES (SHOW CREATE
/// çok kolonlu döndüğü için kanonik "ilk kolon" sözleşmesine uymaz); boolean tipi yok —
/// truthy 1/0 döner (SchemaService Convert.ToBoolean ile okur).
/// </summary>
public sealed class MySqlLehcesi : ILehce
{
    private readonly ISecretProtector _protector;

    public MySqlLehcesi(ISecretProtector protector) => _protector = protector;

    public string MotorId => "mysql";

    /// <summary>Backtick; içindeki '`' ikilenir (ANSI_QUOTES moduna güvenilmez).</summary>
    public string TirnaklaTanimlayici(string ad) => $"`{ad.Replace("`", "``")}`";

    public DbConnection BaglantiOlustur(ConnectionProfile profil, string? veritabaniOverride, bool havuz)
    {
        // Sunucu alanı "host" veya "host:port" olabilir (PG ile aynı desen; IPv6 dahil).
        (string host, int? portInt) = HostPortAyristirici.Ayir(profil.Sunucu);
        uint? port = portInt is { } pi ? (uint)pi : null;

        var b = new MySqlConnectionStringBuilder
        {
            Server = host,
            ConnectionTimeout = (uint)profil.BaglantiTimeoutSn,
            ApplicationName = "SQLST",
            Pooling = havuz,
            AllowPublicKeyRetrieval = true, // caching_sha2_password + SSL'siz iç ağ senaryosu
            // Edit modu çakışma tespiti (V4-S1) "0 satır = başkası değiştirdi" der. Bunun için
            // sunucunun EŞLEŞEN satırı (CLIENT_FOUND_ROWS) bildirmesi gerekir; "değişen satır"
            // semantiğinde değeri aynı kalan güncelleme 0 döner ve YANLIŞ çakışma üretir.
            // Sürücü varsayılanı bugün budur — güvenlik açısından kritik olduğu için açıkça yazıldı.
            UseAffectedRows = false,
        };
        if (port is not null)
            b.Port = port.Value;

        if (!string.IsNullOrWhiteSpace(veritabaniOverride))
            b.Database = veritabaniOverride;

        // MySQL/MariaDB'de Windows entegre kimlik yok — kullanıcı/parola beklenir;
        // Windows seçilirse boş kullanıcıyla denenir ve sunucu anlaşılır hata döndürür.
        if (profil.Kimlik == KimlikTuru.Sql)
        {
            b.UserID = profil.KullaniciAdi ?? "";
            b.Password = profil.ParolaSifreli is null ? "" : _protector.Coz(profil.ParolaSifreli);
        }

        return new MySqlConnection(b.ConnectionString);
    }

    public void KomutuAyarla(DbCommand komut) { /* MySqlConnector'da ek ayar gerekmez */ }

    public IDisposable BilgiMesajlariniDinle(DbConnection baglanti, Action<string> topla)
    {
        var my = (MySqlConnection)baglanti;
        void Dinleyici(object? _, MySqlInfoMessageEventArgs e)
        {
            foreach (MySqlError hata in e.Errors)
                topla(hata.Message);
        }
        my.InfoMessage += Dinleyici;
        return new Abonelik(() => my.InfoMessage -= Dinleyici);
    }

    public SqlHata HataYorumla(Exception ex) => ex is MySqlException m
        ? new SqlHata(m.Message, m.Number, 0, 0)
        : new SqlHata(ex.Message, 0, 0, 0);

    /// <summary>MySQL'de şema = veritabanı; USE ile açık bağlantıda geçilir (MSSQL gibi).</summary>
    public bool AcikBaglantidaVeritabaniDegisir => true;

    public string VeritabaniSecSql(string veritabani) => $"USE {TirnaklaTanimlayici(veritabani)};";

    public string? VeritabaniOlusturSql(string ad) => $"CREATE DATABASE {TirnaklaTanimlayici(ad)};";

    /// <summary>Güvenli Yazma (V2-S4) Faz 2'de MSSQL'e özgü kalır — UI zaten kapılıyor.</summary>
    public Task<IslemDurumu> IslemDurumuAsync(DbConnection baglanti, CancellationToken ct)
        => Task.FromResult(IslemDurumu.Yok);

    public bool IslemDurumuBilinir => false;

    public string GeriAlSql() => "ROLLBACK;";

    // ── Katalog (information_schema — MySQL + MariaDB ortak) ─────────────────

    public string VeritabanlariSorgusu => """
        SELECT SCHEMA_NAME,
               CASE WHEN SCHEMA_NAME IN ('mysql','information_schema','performance_schema','sys')
                    THEN 1 ELSE 0 END AS sistem
        FROM information_schema.SCHEMATA
        ORDER BY sistem, SCHEMA_NAME;
        """;

    public IReadOnlyList<string> SemaSorgulari => [SemaSorgusu];

    // FK bağları (v6-S2): KEY_COLUMN_USAGE'da REFERENCED_* dolu satırlar FK'dır. MySQL'de
    // şema = veritabanı (DATABASE()). fk_id = tablo.constraint (kısıt adı tablo içinde
    // tekil), sira = ORDINAL_POSITION. STRING_AGG yok.
    public string YabanciAnahtarSorgusu => """
        SELECT CONCAT(k.TABLE_NAME, '.', k.CONSTRAINT_NAME) AS fk_id, k.ORDINAL_POSITION AS sira,
               k.TABLE_SCHEMA AS kaynak_sema, k.TABLE_NAME AS kaynak_tablo, k.COLUMN_NAME AS kaynak_kolon,
               k.REFERENCED_TABLE_SCHEMA AS hedef_sema, k.REFERENCED_TABLE_NAME AS hedef_tablo,
               k.REFERENCED_COLUMN_NAME AS hedef_kolon, k.CONSTRAINT_NAME AS kisit_adi
        FROM information_schema.KEY_COLUMN_USAGE k
        WHERE k.REFERENCED_TABLE_NAME IS NOT NULL AND k.TABLE_SCHEMA = DATABASE()
        ORDER BY fk_id, k.ORDINAL_POSITION
        """;

    // Index farkı (v7 borç kapanışı): PK indeksi ('PRIMARY') HARİÇ; benzersizlik NON_UNIQUE=0'dan.
    // İşlevsel indekste COLUMN_NAME null (MySQL 8 EXPRESSION) → atlanır. benzersiz CASE→1/0.
    public string IndeksSorgusu => """
        SELECT s.INDEX_SCHEMA AS sema, s.TABLE_NAME AS tablo, s.INDEX_NAME AS indeks,
               CASE WHEN s.NON_UNIQUE = 0 THEN 1 ELSE 0 END AS benzersiz,
               s.SEQ_IN_INDEX AS sira, s.COLUMN_NAME AS kolon
        FROM information_schema.STATISTICS s
        WHERE s.TABLE_SCHEMA = DATABASE() AND s.INDEX_NAME <> 'PRIMARY' AND s.COLUMN_NAME IS NOT NULL
        ORDER BY s.INDEX_SCHEMA, s.TABLE_NAME, s.INDEX_NAME, s.SEQ_IN_INDEX
        """;

    /// <summary>Tek batch dört kanonik küme; null_olabilir/cikis truthy 1/0 döner (boolean yok).</summary>
    private static string SemaSorgusu => """
        SELECT DATABASE();

        SELECT TABLE_SCHEMA AS sema, TABLE_NAME AS ad,
               CASE WHEN TABLE_TYPE = 'VIEW' THEN 'V' ELSE 'U' END AS tur
        FROM information_schema.TABLES
        WHERE TABLE_SCHEMA = DATABASE()
        UNION ALL
        SELECT ROUTINE_SCHEMA, ROUTINE_NAME,
               CASE WHEN ROUTINE_TYPE = 'PROCEDURE' THEN 'P' ELSE 'FN' END
        FROM information_schema.ROUTINES
        WHERE ROUTINE_SCHEMA = DATABASE()
        ORDER BY sema, ad;

        SELECT c.TABLE_SCHEMA AS sema, c.TABLE_NAME AS ad, c.COLUMN_NAME AS kolon,
               c.DATA_TYPE AS tip,
               COALESCE(c.CHARACTER_MAXIMUM_LENGTH, 0) AS uzunluk,
               COALESCE(c.NUMERIC_PRECISION, 0) AS kesinlik,
               COALESCE(c.NUMERIC_SCALE, 0) AS olcek,
               CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS null_olabilir,
               CASE WHEN c.COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END AS pk
        FROM information_schema.COLUMNS c
        WHERE c.TABLE_SCHEMA = DATABASE()
        ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION;

        SELECT r.ROUTINE_SCHEMA AS sema, r.ROUTINE_NAME AS ad,
               COALESCE(p.PARAMETER_NAME, CONCAT('$', p.ORDINAL_POSITION)) AS parametre,
               COALESCE(p.DATA_TYPE, '') AS tip,
               0 AS uzunluk, 0 AS kesinlik, 0 AS olcek,
               CASE WHEN p.PARAMETER_MODE IN ('OUT','INOUT') THEN 1 ELSE 0 END AS cikis
        FROM information_schema.ROUTINES r
        JOIN information_schema.PARAMETERS p
          ON p.SPECIFIC_SCHEMA = r.ROUTINE_SCHEMA AND p.SPECIFIC_NAME = r.SPECIFIC_NAME
        WHERE r.ROUTINE_SCHEMA = DATABASE() AND p.ORDINAL_POSITION > 0
        ORDER BY r.ROUTINE_SCHEMA, r.ROUTINE_NAME, p.ORDINAL_POSITION;
        """;

    /// <summary>
    /// SHOW CREATE çok kolonlu döndürdüğünden kullanılmaz; information_schema tanımları
    /// tek kolonda verir (routine'de yalnız gövde — MSSQL'in tam-CREATE'inden farklı,
    /// dürüst sınırlama; tam DDL V3'te ayrıca ele alınabilir).
    /// </summary>
    public string TanimSorgusu(SemaNesnesi nesne)
    {
        string sema = nesne.Sema.Replace("'", "''");
        string ad = nesne.Ad.Replace("'", "''");
        return nesne.Tur == SemaNesneTuru.View
            ? $"SELECT VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{sema}' AND TABLE_NAME = '{ad}';"
            : $"SELECT ROUTINE_DEFINITION FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{sema}' AND ROUTINE_NAME = '{ad}';";
    }

    public SemaNesneTuru TurCevir(string kod) => kod.Trim() switch
    {
        "U" => SemaNesneTuru.Tablo,
        "V" => SemaNesneTuru.View,
        "P" => SemaNesneTuru.StoredProcedure,
        "FN" => SemaNesneTuru.Fonksiyon,
        _ => throw new ArgumentOutOfRangeException(nameof(kod), kod, "Beklenmeyen tür kodu"),
    };

    /// <summary>MySQL'de şema = veritabanı; sekme zaten o DB'ye bağlı olduğundan tablo adı yeterli.</summary>
    public string IlkNSatirSorgusu(SemaNesnesi nesne, int n)
        => $"SELECT * FROM {TirnaklaTanimlayici(nesne.Ad)} LIMIT {n};";

    public string TipYaz(string tip, int uzunluk, int kesinlik, int olcek) => tip switch
    {
        "varchar" or "char" or "varbinary" or "binary" when uzunluk > 0 => $"{tip}({uzunluk})",
        "decimal" or "numeric" when kesinlik > 0 => $"{tip}({kesinlik},{olcek})",
        _ => tip,
    };

    /// <summary>
    /// Edit modu kolon üst verisi (V4-S1). MySQL/MariaDB'de "sunucu üretir" bilgisi tek bir
    /// <c>EXTRA</c> alanında toplanır: <c>auto_increment</c> = identity, <c>… GENERATED</c> =
    /// üretilmiş kolon (VIRTUAL/STORED). PK bilgisi <c>COLUMN_KEY = 'PRI'</c>.
    /// <b>Sürüm kolonu 0 döner:</b> rowversion karşılığı yoktur — <c>TIMESTAMP … ON UPDATE
    /// CURRENT_TIMESTAMP</c> otomatik güncellenir ama iyimser kilit belirteci DEĞİLDİR
    /// (çakışan iki yazma aynı saniyede aynı değeri alabilir), bu yüzden bilinçli olarak
    /// kullanılmadı; kimlik PK + değişen kolonların eski değerleriyle korunur (07-r2 §4).
    /// Şema = veritabanı olduğundan <c>TABLE_SCHEMA</c> tablonun şeması alanından gelir.
    /// </summary>
    public string DuzenlemeMetaSorgusu(SemaNesnesi tablo)
    {
        string sema = tablo.Sema.Replace("'", "''");
        string ad = tablo.Ad.Replace("'", "''");
        return $"""
            SELECT COLUMN_NAME AS ad,
                   DATA_TYPE AS tip,
                   COALESCE(CHARACTER_MAXIMUM_LENGTH, 0) AS uzunluk,
                   COALESCE(NUMERIC_PRECISION, 0) AS kesinlik,
                   COALESCE(NUMERIC_SCALE, 0) AS olcek,
                   CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS null_olabilir,
                   CASE WHEN EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END AS identity_mi,
                   CASE WHEN EXTRA LIKE '%GENERATED%' THEN 1 ELSE 0 END AS computed_mi,
                   0 AS surum,
                   CASE WHEN COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END AS pk
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = '{sema}' AND TABLE_NAME = '{ad}'
            ORDER BY ORDINAL_POSITION;
            """;
    }

    public bool DuzenlemeDestekler => true;

    public LiteralKurallari LiteralKurallari => LiteralKurallari.MySql;

    /// <summary>Şema = veritabanı; oturum zaten o veritabanındadır → yalnız tablo adı.</summary>
    public string TamAdYaz(string sema, string tablo) => TirnaklaTanimlayici(tablo);

    /// <summary>
    /// Veri karşılaştırma satır parmak izi (v7-S2): <c>MD5(CONCAT_WS(CHAR(31), …))</c>. Her kolon CHAR'a
    /// çevrilip NULL'lar CHAR(0) işaretiyle ayrılır (NULL ile '' karışmasın), kolonlar birim-ayıracıyla
    /// (CHAR(31)) birleşir. <b>Canlı doğrulama borcu</b> (elde MySQL sunucusu yok) — SQL üretimi test edilir.
    /// </summary>
    public string? SatirHashSorgusu(
        string? sema, string tablo, IReadOnlyList<string> anahtarKolonlar,
        IReadOnlyList<string> tumKolonlar, string? whereKosulu)
    {
        if (anahtarKolonlar.Count == 0 || tumKolonlar.Count == 0)
            return null;
        string tam = TirnaklaTanimlayici(tablo);
        string anahtar = string.Join(", ", anahtarKolonlar.Select(TirnaklaTanimlayici));
        string parcalar = string.Join(", ",
            tumKolonlar.Select(k => $"COALESCE(CAST({TirnaklaTanimlayici(k)} AS CHAR), CHAR(0))"));
        string where = string.IsNullOrWhiteSpace(whereKosulu) ? "" : $" WHERE {whereKosulu}";
        return $"SELECT {anahtar}, MD5(CONCAT_WS(CHAR(31), {parcalar})) AS __hash FROM {tam}{where}";
    }

    /// <summary>MySQL'de <c>DEFAULT VALUES</c> yoktur; boş kolon listesi aynı işi görür.</summary>
    public string BosSatirEkleSql(string tamAd) => $"INSERT INTO {tamAd} () VALUES ()";

    public string IfadeSonu => ";";

    public string IslemBaslatSql => "START TRANSACTION;";

    public string CommitSql => "COMMIT;";

    public bool GuvenliYazmaDestekler => true;

    /// <summary>
    /// MySQL/MariaDB'de DDL <b>örtük COMMIT</b> yapar: <c>CREATE/ALTER/DROP/TRUNCATE/RENAME</c>
    /// ve yetki ifadeleri çalıştığı anda açık işlemi kalıcılaştırır ve geri alınamaz
    /// (dev.mysql.com "Statements That Cause an Implicit Commit"). Böyle bir ifadeye
    /// Güvenli Yazma bandı açmak, hiçbir şey yapmayan bir ROLLBACK düğmesi göstermek olurdu.
    /// <b>Ek dürüst sınır:</b> MyISAM gibi işlemsiz tablo motorlarında DML de geri alınamaz;
    /// bu ifade düzeyinde anlaşılamaz (tablo motoru sorgudan belli olmaz) — kılavuzda yazılıdır.
    /// </summary>
    public bool PlanDestekler => true;

    /// <summary>
    /// <b>GERÇEK plan bu motorda KAPALI</b> (V5-S1c dürüst sınırı). MySQL ile MariaDB burada
    /// ayrışır: MariaDB <c>ANALYZE FORMAT=JSON</c> ile aynı JSON'u döndürür, MySQL 8 ise
    /// <c>EXPLAIN ANALYZE</c> ile <b>JSON değil TREE metni</b> verir — iki ayrı çözümleyici
    /// gerekir. Hangi sunucuda olduğumuzu sürüm sorgulamadan bilemiyoruz ve <b>kalıcı bir
    /// MariaDB/MySQL test sunucusu olmadığından canlı doğrulama yapılamıyor</b>.
    /// Yarım/doğrulanmamış bir yol açmak yerine kapalı bırakıldı; düğme görünmez.
    /// </summary>
    public bool PlanGercekDestekler => false;

    /// <summary>Plan sorgunun kendisini sarar — oturum düzeyinde açma/kapama yoktur.</summary>
    public string PlanAcSql(bool gercek) => "";

    public string PlanKapatSql(bool gercek) => "";

    public string PlanSorgusuYaz(string sql, bool gercek)
    {
        if (gercek)
        {
            throw new NotSupportedException(
                "Gerçek plan MySQL/MariaDB'de desteklenmiyor (MySQL TREE metni, MariaDB JSON döndürür) "
              + "— tahmini planı kullanın.");
        }

        return $"EXPLAIN FORMAT=JSON {sql.Trim().TrimEnd(';')}";
    }

    /// <summary><c>EXPLAIN</c> tek ifade sarar.</summary>
    public bool PlanTekIfadeIster => true;

    /// <summary>Plan tek adımlıdır — <c>EXPLAIN FORMAT=JSON</c> sonucu doğrudan döner.</summary>
    public string PlanOkumaSql(bool gercek) => "";

    /// <summary>Plan tek satır/tek kolonda JSON olarak gelir (kolon adı <c>EXPLAIN</c>).</summary>
    public SorguPlani PlanCoz(QueryResult sonuc, bool gercek)
    {
        foreach (ResultSetData set in sonuc.ResultSetler)
        {
            if (set.Kolonlar.Count != 1 || set.Satirlar.Count == 0)
                continue;

            string json = string.Concat(set.Satirlar.Select(s => s[0]?.ToString() ?? ""));
            if (json.TrimStart().StartsWith('{'))
                return MySqlPlanOkuyucu.Coz(json, gercek);
        }

        throw new InvalidOperationException(
            "Sunucu plan döndürmedi (sorgu plan üretmeyen bir ifade olabilir).");
    }

    public bool OrtukCommitYaparMi(string sql) => SqlAnahtar.IlkKelime(sql) is
        "CREATE" or "ALTER" or "DROP" or "TRUNCATE" or "RENAME"
        or "GRANT" or "REVOKE" or "LOCK" or "UNLOCK" or "FLUSH" or "ANALYZE" or "OPTIMIZE";

    /// <summary>
    /// <c>information_schema.ROUTINES</c> (SP/fonksiyon) + <c>VIEWS</c>. Şema = veritabanı
    /// olduğundan arama <c>DATABASE()</c> ile seçili veritabanına sınırlanır.
    /// </summary>
    public string MetinAramaSorgusu(string aranan)
    {
        string desen = LikeKacir(aranan);
        return $"""
            SELECT ROUTINE_SCHEMA AS sema, ROUTINE_NAME AS ad,
                   CASE WHEN ROUTINE_TYPE = 'FUNCTION' THEN 'FN' ELSE 'P' END AS tur,
                   ROUTINE_DEFINITION AS tanim
            FROM information_schema.ROUTINES
            WHERE ROUTINE_SCHEMA = DATABASE()
              AND ROUTINE_DEFINITION LIKE '%{desen}%' ESCAPE '\\'
            UNION ALL
            SELECT TABLE_SCHEMA, TABLE_NAME, 'V', VIEW_DEFINITION
            FROM information_schema.VIEWS
            WHERE TABLE_SCHEMA = DATABASE()
              AND VIEW_DEFINITION LIKE '%{desen}%' ESCAPE '\\'
            ORDER BY 1, 2
            LIMIT {ILehce.AramaTavani};
            """;
    }

    /// <summary>
    /// MySQL'de ters bölen HEM dize kaçışı HEM LIKE kaçışıdır → iki kez korunur.
    /// Kaçırılmazsa "kdv_orani" araması "kdvXorani"yi de bulurdu.
    /// </summary>
    private static string LikeKacir(string s) => s
        .Replace("\\", "\\\\\\\\")
        .Replace("'", "''")
        .Replace("%", "\\\\%")
        .Replace("_", "\\\\_");

    public bool KiyasGuvenliMi(string tip, int uzunluk)
        => tip is not ("float" or "double" or "text" or "mediumtext" or "longtext" or "blob" or "mediumblob" or "longblob" or "json");

    /// <summary>
    /// MySQL/MariaDB Yönetim Paneli (V3). performance_schema tabanlı bölümler sunucuda
    /// kapalıysa boş/hata dönebilir — panel hatayı sekmede gösterir, uygulama akışını bozmaz.
    /// </summary>
    public IReadOnlyList<TeshisBolumu> TeshisBolumleri =>
    [
        new("Index kullanımı",
            "performance_schema'ya göre hiç okunmamış index'ler (COUNT_STAR = 0). PK'lar hariç tutuldu. performance_schema kapalıysa bu bölüm boş gelir.",
            """
            SELECT s.object_schema AS sema, s.object_name AS tablo, s.index_name AS index_adi,
                   s.count_star AS erisim, s.count_read AS okuma, s.count_write AS yazma,
                   CASE WHEN s.count_star = 0 THEN '⚠ hiç kullanılmadı' ELSE '' END AS not
            FROM performance_schema.table_io_waits_summary_by_index_usage s
            WHERE s.object_schema = DATABASE()
              AND s.index_name IS NOT NULL AND s.index_name <> 'PRIMARY'
            ORDER BY s.count_star, s.object_name
            LIMIT 200
            """,
            Takip: "TAKİP: erisim = 0 olan index'ler ('⚠ hiç kullanılmadı'). Her index yazma "
                 + "maliyetidir: hiç okunmuyorsa bedelini boşuna ödersiniz. SİLMEDEN ÖNCE İKİ "
                 + "UYARI: (1) sayaç sunucunun son açılışından beri sayar — ay sonu raporu henüz "
                 + "çalışmamış olabilir. (2) performance_schema kapalıysa bölüm BOŞ gelir; bunu "
                 + "'hiçbir index kullanılmıyor' diye okumayın. yazma sütunu yüksek + okuma = 0 "
                 + "olan satır en net adaydır.",
            VeritabaniGerekir: true),

        new("Tablo boyutları",
            "Veri ve index boyutları; DATA_FREE parçalanmayı (OPTIMIZE TABLE adayı) gösterir.",
            """
            SELECT TABLE_NAME AS tablo, ENGINE AS motor, TABLE_ROWS AS yaklasik_satir,
                   ROUND(DATA_LENGTH/1024/1024, 2) AS veri_mb,
                   ROUND(INDEX_LENGTH/1024/1024, 2) AS index_mb,
                   ROUND(DATA_FREE/1024/1024, 2) AS bos_alan_mb,
                   TABLE_COLLATION AS harmanlama, UPDATE_TIME AS son_guncelleme
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY (DATA_LENGTH + INDEX_LENGTH) DESC
            LIMIT 100
            """,
            Takip: "TAKİP: (1) bos_alan_mb (DATA_FREE) büyük olan tablolar — çok silme/güncelleme "
                 + "görmüş, yer geri verilmemiş demektir; OPTIMIZE TABLE adayıdır (tabloyu "
                 + "kilitler, yoğun saatte çalıştırmayın). (2) index_mb > veri_mb olan tablolar: "
                 + "veriden çok index taşıyorsunuz, 'Index kullanımı' bölümüyle birlikte okuyun. "
                 + "(3) yaklasik_satir DEĞERİNE GÜVENMEYİN — InnoDB'de tahminîdir, kesin sayı "
                 + "için COUNT(*) gerekir.",
            VeritabaniGerekir: true),

        new("Çalışan işlemler",
            "Uzun süren ve kilit bekleyen bağlantılar (PROCESSLIST). Command='Sleep' olanlar boştaki bağlantılardır.",
            """
            SELECT ID AS baglanti, USER AS kullanici, HOST AS istemci, DB AS veritabani,
                   COMMAND AS komut, TIME AS sure_sn, STATE AS durum,
                   LEFT(COALESCE(INFO, ''), 200) AS sorgu
            FROM information_schema.PROCESSLIST
            WHERE ID <> CONNECTION_ID()
            ORDER BY (COMMAND <> 'Sleep') DESC, TIME DESC
            LIMIT 100
            """,
            Takip: "TAKİP: (1) durum sütununda 'Waiting for table metadata lock' — bir DDL "
                 + "(ALTER TABLE) açık bir işlem yüzünden bekliyor ve ARKASINDA SIRA BİRİKİYOR "
                 + "demektir; MySQL'de üretimi en hızlı kilitleyen durumlardan biridir. "
                 + "(2) 'Copying to tmp table' / 'Sorting result' uzun sürüyorsa sorgu diske "
                 + "taşıyor. (3) komut = 'Sleep' ve sure_sn çok büyük olan bağlantılar boştadır "
                 + "— tek başına zararsızdır, AMA açık işlem tutuyorsa asıl suçlu odur; bunu "
                 + "'Açık işlemler ve kilit beklemeleri' bölümünden doğrulayın."),

        // V5-S3. TAŞINABİLİRLİK KARARI: bloklama için performance_schema.data_lock_waits
        // (MySQL 8) KULLANILMADI — MariaDB'de yoktur ve tek lehçe iki sunucuya birden hizmet
        // eder. INNODB_TRX ikisinde de vardır; "kim kimi bekletiyor" grafiğini vermez ama
        // LOCK WAIT durumundaki işlemleri ve onları bekleten uzun açık işlemleri gösterir.
        // (Aynı sınıf bir hata 2026-07-18'de canlı doğrulamada yakalanmıştı: global_status.)
        new("Açık işlemler ve kilit beklemeleri",
            "trx_state = 'LOCK WAIT' olan işlemler bir kilit bekliyor. Asıl suçlu genelde uzun süredir AÇIK kalmış (trx_started eski) ama şu an sorgu çalıştırmayan işlemdir — 'Çalışan işlemler' listesinde Sleep göründüğü için göze çarpmaz.",
            """
            SELECT t.trx_id AS islem, t.trx_state AS durum, t.trx_started AS baslangic,
                   TIMESTAMPDIFF(SECOND, t.trx_started, NOW()) AS acik_sn,
                   t.trx_mysql_thread_id AS baglanti,
                   t.trx_rows_locked AS kilitli_satir, t.trx_rows_modified AS degisen_satir,
                   t.trx_isolation_level AS izolasyon,
                   LEFT(COALESCE(t.trx_query, ''), 200) AS sorgu,
                   CASE WHEN t.trx_state = 'LOCK WAIT' THEN '⚠ kilit bekliyor'
                        WHEN t.trx_query IS NULL THEN '⚠ açık ama boşta'
                        ELSE '' END AS not
            FROM information_schema.INNODB_TRX t
            ORDER BY t.trx_started
            LIMIT 100
            """,
            Takip: "TAKİP: '⚠ açık ama boşta' işaretli satır — liste en eskiden başlar, yani EN "
                 + "ÜSTTEKİ genelde asıl suçludur. acik_sn dakikalar mertebesindeyken sorgu boşsa, "
                 + "o işlem hiçbir iş yapmadan kilit tutuyor demektir; 'Çalışan işlemler'de "
                 + "yalnızca 'Sleep' göründüğü için orada masum durur. '⚠ kilit bekliyor' satırları "
                 + "ise KURBANDIR, sebep değil. kilitli_satir sayısı büyükse işlem kapsamı fazla "
                 + "geniştir. ÇÖZÜM oturumu öldürmek değil, o kod yolunda COMMIT/ROLLBACK'i "
                 + "garantiye almaktır."),

        new("İstatistik tazeliği",
            "InnoDB istatistiklerinin son güncelleme zamanı. Eskimiş istatistik, optimizer'ın yanlış plan seçmesinin bir numaralı sebebidir; ANALYZE TABLE ile tazelenir.",
            """
            SELECT s.table_name AS tablo, s.n_rows AS istatistikteki_satir,
                   t.TABLE_ROWS AS suanki_yaklasik_satir,
                   s.last_update AS istatistik_guncellemesi,
                   TIMESTAMPDIFF(DAY, s.last_update, NOW()) AS yas_gun,
                   s.clustered_index_size AS kumelenmis_sayfa
            FROM mysql.innodb_table_stats s
            LEFT JOIN information_schema.TABLES t
                   ON t.TABLE_SCHEMA = s.database_name AND t.TABLE_NAME = s.table_name
            WHERE s.database_name = DATABASE()
            ORDER BY s.last_update
            LIMIT 100
            """,
            Takip: "TAKİP: (1) yas_gun yüksek olan tablolar — liste en bayattan başlar. "
                 + "(2) Asıl sinyal İKİ SATIR SAYISININ FARKIDIR: istatistikteki_satir ile "
                 + "suanki_yaklasik_satir arasında kat farkı varsa optimizer artık var olmayan "
                 + "bir tabloya göre plan seçiyordur. BELİRTİ: bir sorgu dün hızlıyken bugün "
                 + "yavaşladıysa önce buraya bakın. ÇÖZÜM: ANALYZE TABLE <tablo>; — ucuzdur.",
            VeritabaniGerekir: true),

        // SHOW GLOBAL STATUS hem MySQL hem MariaDB'de çalışır; performance_schema.global_status
        // MariaDB'de yoktur (canlı doğrulamada yakalandı, 2026-07-18).
        //
        // 2026-07-19 DEĞERLENDİRMESİ: bu bölüm ham bir sayaç dökümüdür (~500 satır) ve
        // panelin tek yorumlanmamış bölümüdür — kaldırılması düşünüldü. KALDI, çünkü
        // MySQL panelinin tek sunucu-geneli görünümü budur (PG'de "Veritabanı sağlığı"nın
        // karşılığı). Süzülmüş bir sürüm TAŞINABİLİR DEĞİL: information_schema.GLOBAL_STATUS
        // MySQL 8'de kaldırıldı, performance_schema.global_status ise MariaDB'de yok —
        // ikisini birden karşılayan tek portatif yol SHOW'un kendisi. Süzemediğimiz için
        // en azından NEREYE BAKILACAĞINI açıklamada sayıyoruz.
        new("Sunucu durumu",
            "Ham sayaç dökümü (SHOW GLOBAL STATUS) — süzülmemiştir, aradığınızı listede aratın. Bakmaya değer olanlar: Threads_running (anlık yük), Threads_connected, Slow_queries, Innodb_row_lock_waits ve Innodb_row_lock_time_avg (kilit baskısı), Innodb_buffer_pool_reads / Innodb_buffer_pool_read_requests (önbellek isabeti), Aborted_connects, Created_tmp_disk_tables (diske taşan geçici tablolar).",
            "SHOW GLOBAL STATUS",
            Takip: "TAKİP (bu liste süzülmemiştir — aşağıdaki adları listede aratın): "
                 + "Threads_running (anlık yük; CPU çekirdek sayısını sürekli aşıyorsa sunucu "
                 + "boğuluyor) · Threads_connected ile max_connections farkı (kapanmaya yakınsa "
                 + "yeni bağlantılar reddedilecek) · Slow_queries (artış hızı önemlidir, mutlak "
                 + "değeri değil) · Innodb_row_lock_waits ve Innodb_row_lock_time_avg (kilit "
                 + "baskısı — yükseliyorsa yukarıdaki açık işlem bölümüne bakın) · "
                 + "Innodb_buffer_pool_reads / Innodb_buffer_pool_read_requests oranı "
                 + "(diskten okuma payı; %1'in altında kalmalı) · Created_tmp_disk_tables "
                 + "(diske taşan geçici tablolar — sorgu ya da tmp_table_size sorunu) · "
                 + "Aborted_connects (kimlik/ağ sorunu). "
                 + "SAYAÇLAR KÜMÜLATİFTİR: tek bakışta değil, iki ölçüm arasındaki ARTIŞTA "
                 + "anlam taşırlar."),
    ];

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
