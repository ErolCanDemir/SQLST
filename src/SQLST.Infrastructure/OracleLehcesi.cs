using System.Data.Common;
using SQLST.Contracts;
using Oracle.ManagedDataAccess.Client;

namespace SQLST.Infrastructure;

/// <summary>
/// Oracle Database lehçesi (V3-S1 Faz 2 — 08-v3r1 §4). Sürücü: Oracle.ManagedDataAccess.Core
/// (FUTC — ücretsiz + royalty-free tescilli EULA; kullanıcı istisna kararı 2026-07-18,
/// 07-r2 lisans maddesi). %100 managed — Oracle Client kurulumu gerektirmez.
///
/// MSSQL'den sapmalar (08-v3r1 §4 + Faz 2 bulguları):
/// - "Veritabanı" kavramı yok: ağaçtaki veritabanı düzeyi = ŞEMA (kullanıcı); geçiş
///   ALTER SESSION SET CURRENT_SCHEMA iledir (USE benzeri — açık bağlantıda çalışır).
/// - Tek komut = tek SELECT: kanonik 4 küme TEK batch'te dönemez → SemaSorgulari 4 ayrı sorgu.
/// - Tırnaksız ad BÜYÜK HARFE katlanır; katalog adları UPPERCASE saklanır.
/// - Tanım: ALL_VIEWS.TEXT (view) / ALL_SOURCE satırları (SP-fonksiyon; okuyucu birleştirir).
/// - LONG/CLOB kolonlarının düz string dönmesi için InitialLONG/LOBFetchSize=-1 (statik yapılandırma).
///
/// DÜRÜSTLÜK NOTU: Bu lehçe canlı bir Oracle sunucusuna karşı DOĞRULANMADI (ortamda Oracle
/// yok; taşınabilir/kurulumsuz Oracle da yok). Birim testleri SQL şekli düzeyindedir; ilk
/// gerçek bağlantıda ince ayar gerekebilir — roadmap'te işaretli.
/// </summary>
public sealed class OracleLehcesi : ILehce
{
    private readonly ISecretProtector _protector;

    public OracleLehcesi(ISecretProtector protector) => _protector = protector;

    /// <summary>LONG (ALL_VIEWS.TEXT) ve CLOB değerleri GetValues'ta düz string gelsin —
    /// varsayılan 0'da kesik/sarmalayıcı döner. ODP.NET bunu komut düzeyinde tanır.</summary>
    public void KomutuAyarla(DbCommand komut)
    {
        var ora = (OracleCommand)komut;
        ora.InitialLONGFetchSize = -1;
        ora.InitialLOBFetchSize = -1;
    }

    public string MotorId => "oracle";

    /// <summary>ANSI çift tırnak; '"' ikilenir. Tırnaksız ad Oracle'da BÜYÜK HARFE katlanır —
    /// katalogdaki tam yazım her zaman tırnaklanır.</summary>
    public string TirnaklaTanimlayici(string ad) => $"\"{ad.Replace("\"", "\"\"")}\"";

    public DbConnection BaglantiOlustur(ConnectionProfile profil, string? veritabaniOverride, bool havuz)
    {
        // Sunucu alanı Easy Connect biçimi bekler: host:port/servis (ör. srv:1521/XEPDB1)
        // veya tnsnames.ora'daki bir alias. Şema geçişi bağlantı SONRASI ALTER SESSION'la.
        var b = new OracleConnectionStringBuilder
        {
            DataSource = profil.Sunucu,
            ConnectionTimeout = profil.BaglantiTimeoutSn,
            Pooling = havuz,
        };

        if (profil.Kimlik == KimlikTuru.Windows)
        {
            b.UserID = "/"; // harici (OS) kimlik — sunucu yapılandırmasına bağlıdır
        }
        else
        {
            b.UserID = profil.KullaniciAdi ?? "";
            b.Password = profil.ParolaSifreli is null ? "" : _protector.Coz(profil.ParolaSifreli);
        }

        return new OracleConnection(b.ConnectionString);
    }

    public IDisposable BilgiMesajlariniDinle(DbConnection baglanti, Action<string> topla)
    {
        var ora = (OracleConnection)baglanti;
        void Dinleyici(object _, OracleInfoMessageEventArgs e) => topla(e.Message);
        ora.InfoMessage += Dinleyici;
        return new Abonelik(() => ora.InfoMessage -= Dinleyici);
    }

    public SqlHata HataYorumla(Exception ex) => ex is OracleException o
        ? new SqlHata(o.Message, o.Number, 0, 0)
        : new SqlHata(ex.Message, 0, 0, 0);

    /// <summary>Şema geçişi ALTER SESSION ile — açık bağlantıda çalışır (USE benzeri).</summary>
    public bool AcikBaglantidaVeritabaniDegisir => true;

    /// <summary>Trailing ';' YOK — Oracle sunucusu düz SQL'de noktalı virgül kabul etmez (Faz 2).</summary>
    public string VeritabaniSecSql(string veritabani)
        => $"ALTER SESSION SET CURRENT_SCHEMA = {TirnaklaTanimlayici(veritabani)}";

    /// <summary>Güvenli Yazma (V2-S4) Faz 2'de MSSQL'e özgü kalır — UI zaten kapılıyor.</summary>
    public Task<IslemDurumu> IslemDurumuAsync(DbConnection baglanti, CancellationToken ct)
        => Task.FromResult(IslemDurumu.Yok);

    public bool IslemDurumuBilinir => false;

    public string GeriAlSql() => "ROLLBACK";

    // ── Katalog (ALL_* sözlük view'ları; adlar UPPERCASE) ────────────────────

    /// <summary>"Veritabanı" listesi = erişilebilir şemalar; ORACLE_MAINTAINED sistem şemalarını ayırır (12c+).</summary>
    public string VeritabanlariSorgusu => """
        SELECT username,
               CASE WHEN oracle_maintained = 'Y' THEN 1 ELSE 0 END AS sistem
        FROM all_users
        ORDER BY sistem, username
        """;

    /// <summary>Oracle tek komutta tek SELECT kabul eder → dört AYRI sorgu (kanonik sıra korunur).</summary>
    public IReadOnlyList<string> SemaSorgulari =>
    [
        "SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM dual",

        """
        SELECT owner AS sema, object_name AS ad,
               CASE object_type WHEN 'TABLE' THEN 'U' WHEN 'VIEW' THEN 'V'
                                WHEN 'PROCEDURE' THEN 'P' ELSE 'FN' END AS tur
        FROM all_objects
        WHERE owner = SYS_CONTEXT('USERENV','CURRENT_SCHEMA')
          AND object_type IN ('TABLE','VIEW','PROCEDURE','FUNCTION')
        ORDER BY object_type, object_name
        """,

        """
        SELECT c.owner AS sema, c.table_name AS ad, c.column_name AS kolon,
               c.data_type AS tip,
               COALESCE(c.data_length, 0) AS uzunluk,
               COALESCE(c.data_precision, 0) AS kesinlik,
               COALESCE(c.data_scale, 0) AS olcek,
               CASE WHEN c.nullable = 'Y' THEN 1 ELSE 0 END AS null_olabilir,
               CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS pk
        FROM all_tab_columns c
        LEFT JOIN (
            SELECT cc.owner, cc.table_name, cc.column_name
            FROM all_constraints k
            JOIN all_cons_columns cc
              ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name
            WHERE k.constraint_type = 'P'
        ) pk ON pk.owner = c.owner AND pk.table_name = c.table_name AND pk.column_name = c.column_name
        WHERE c.owner = SYS_CONTEXT('USERENV','CURRENT_SCHEMA')
        ORDER BY c.table_name, c.column_id
        """,

        """
        SELECT a.owner AS sema, a.object_name AS ad,
               COALESCE(a.argument_name, '$' || a.position) AS parametre,
               COALESCE(a.data_type, '') AS tip,
               0 AS uzunluk, 0 AS kesinlik, 0 AS olcek,
               CASE WHEN a.in_out IN ('OUT','IN/OUT') THEN 1 ELSE 0 END AS cikis
        FROM all_arguments a
        WHERE a.owner = SYS_CONTEXT('USERENV','CURRENT_SCHEMA')
          AND a.position > 0 AND a.data_level = 0
        ORDER BY a.object_name, a.position
        """,
    ];

    // FK bağları (v6-S2): all_constraints (type='R') kaynak, r_constraint_name ile hedef kısma
    // bağlanır; kaynak↔hedef kolonları POSITION eşleştirir. fk_id = constraint_name (owner
    // içinde tekil), sira = position. Oracle'da sondaki ';' YOK. STRING_AGG yok.
    public string YabanciAnahtarSorgusu => """
        SELECT ac.constraint_name AS fk_id, acc.position AS sira,
               acc.owner AS kaynak_sema, acc.table_name AS kaynak_tablo, acc.column_name AS kaynak_kolon,
               rcc.owner AS hedef_sema, rcc.table_name AS hedef_tablo, rcc.column_name AS hedef_kolon,
               ac.constraint_name AS kisit_adi
        FROM all_constraints ac
        JOIN all_cons_columns acc ON acc.owner = ac.owner AND acc.constraint_name = ac.constraint_name
        JOIN all_cons_columns rcc ON rcc.owner = ac.r_owner AND rcc.constraint_name = ac.r_constraint_name
                                 AND rcc.position = acc.position
        WHERE ac.constraint_type = 'R' AND ac.owner = SYS_CONTEXT('USERENV','CURRENT_SCHEMA')
        ORDER BY ac.constraint_name, acc.position
        """;

    // Index farkı (v7 borç kapanışı): PK kısıtını DESTEKLEYEN indeks HARİÇ (all_constraints P);
    // benzersizlik uniqueness='UNIQUE'ten. Oracle'da sondaki ';' YOK. benzersiz CASE→1/0.
    public string IndeksSorgusu => """
        SELECT ai.table_owner AS sema, ai.table_name AS tablo, ai.index_name AS indeks,
               CASE WHEN ai.uniqueness = 'UNIQUE' THEN 1 ELSE 0 END AS benzersiz,
               aic.column_position AS sira, aic.column_name AS kolon
        FROM all_indexes ai
        JOIN all_ind_columns aic ON aic.index_owner = ai.owner AND aic.index_name = ai.index_name
        WHERE ai.table_owner = SYS_CONTEXT('USERENV','CURRENT_SCHEMA')
          AND ai.index_name NOT IN (
              SELECT ac.index_name FROM all_constraints ac
              WHERE ac.owner = ai.table_owner AND ac.constraint_type = 'P' AND ac.index_name IS NOT NULL)
        ORDER BY ai.table_owner, ai.table_name, ai.index_name, aic.column_position
        """;

    /// <summary>View: ALL_VIEWS.TEXT (LONG — statik yapılandırmayla düz string). SP/FN: ALL_SOURCE
    /// satırları; okuyucu birleştirir (satırlar kendi \n'ini taşır).</summary>
    public string TanimSorgusu(SemaNesnesi nesne)
    {
        string sema = nesne.Sema.Replace("'", "''").ToUpperInvariant();
        string ad = nesne.Ad.Replace("'", "''");
        return nesne.Tur == SemaNesneTuru.View
            ? $"SELECT text FROM all_views WHERE owner = '{sema}' AND view_name = '{ad}'"
            : $"""
                SELECT text FROM all_source
                WHERE owner = '{sema}' AND name = '{ad}'
                  AND type IN ('PROCEDURE','FUNCTION')
                ORDER BY line
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

    /// <summary>Oracle 12c+ FETCH FIRST; sonda ';' YOK (ORA-00933).</summary>
    public string IlkNSatirSorgusu(SemaNesnesi nesne, int n)
        => $"SELECT * FROM {TirnaklaTanimlayici(nesne.Sema)}.{TirnaklaTanimlayici(nesne.Ad)} FETCH FIRST {n} ROWS ONLY";

    /// <summary>Özel SELECT sınırı Oracle'da FETCH FIRST kuyruğudur (ILehce LIMIT varsayılanını ezer).</summary>
    public string SatirSinirSonu(int n) => $" FETCH FIRST {n} ROWS ONLY";

    public string TipYaz(string tip, int uzunluk, int kesinlik, int olcek) => tip switch
    {
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" when uzunluk > 0 => $"{tip}({uzunluk})",
        "NUMBER" when kesinlik > 0 => $"{tip}({kesinlik},{olcek})",
        _ => tip,
    };

    /// <summary>
    /// Edit modu kolon üst verisi (V4-S1). Kaynak <c>ALL_TAB_COLS</c> — <c>ALL_TAB_COLUMNS</c>'ın
    /// aksine <c>IDENTITY_COLUMN</c>/<c>VIRTUAL_COLUMN</c>/<c>HIDDEN_COLUMN</c> alanlarını taşır
    /// (gizli kolonlar dışlanır: üretilmiş kolonlar arkada gizli kolon açar). PK,
    /// <c>ALL_CONSTRAINTS</c>+<c>ALL_CONS_COLUMNS</c>'tan (<c>constraint_type = 'P'</c>).
    /// <b>Sürüm kolonu 0 döner:</b> rowversion karşılığı yoktur — <c>ORA_ROWSCN</c> ancak tablo
    /// <c>ROWDEPENDENCIES</c> ile yaratılmışsa satır düzeyinde anlamlıdır (varsayılan blok
    /// düzeyidir → yanlış çakışma verir), bu yüzden bilinçli kullanılmadı; kimlik PK +
    /// değişen kolonların eski değerleriyle korunur (07-r2 §4).
    /// <b>Sürüm gereksinimi:</b> IDENTITY_COLUMN 12c ile geldi — 11g'de bu sorgu hata verir
    /// (Edit modu o sürümde kullanılamaz; dürüst sınır).
    /// Sonda ';' YOKTUR (ORA-00933).
    /// </summary>
    public string DuzenlemeMetaSorgusu(SemaNesnesi tablo)
    {
        string sema = tablo.Sema.Replace("'", "''").ToUpperInvariant();
        string ad = tablo.Ad.Replace("'", "''");
        return $"""
            SELECT c.column_name AS ad,
                   c.data_type AS tip,
                   NVL(c.char_length, 0) AS uzunluk,
                   NVL(c.data_precision, 0) AS kesinlik,
                   NVL(c.data_scale, 0) AS olcek,
                   CASE WHEN c.nullable = 'Y' THEN 1 ELSE 0 END AS null_olabilir,
                   CASE WHEN c.identity_column = 'YES' THEN 1 ELSE 0 END AS identity_mi,
                   CASE WHEN c.virtual_column = 'YES' THEN 1 ELSE 0 END AS computed_mi,
                   0 AS surum,
                   CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS pk
            FROM all_tab_cols c
            LEFT JOIN (
                SELECT cc.column_name
                FROM all_constraints k
                JOIN all_cons_columns cc
                  ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name
                WHERE k.constraint_type = 'P'
                  AND k.owner = '{sema}' AND k.table_name = '{ad}'
            ) pk ON pk.column_name = c.column_name
            WHERE c.owner = '{sema}' AND c.table_name = '{ad}'
              AND c.hidden_column = 'NO'
            ORDER BY c.column_id
            """;
    }

    public bool DuzenlemeDestekler => true;

    public LiteralKurallari LiteralKurallari => LiteralKurallari.Oracle;

    public string TamAdYaz(string sema, string tablo)
        => $"{TirnaklaTanimlayici(sema)}.{TirnaklaTanimlayici(tablo)}";

    /// <summary>
    /// Veri karşılaştırma satır parmak izi (v7-S2): <c>RAWTOHEX(STANDARD_HASH(…, 'MD5'))</c>. Kolonlar
    /// TO_CHAR ile metne, NULL'lar CHR(0) işaretiyle, birim-ayıracıyla (CHR(31)) birleşir; RAW sonuç
    /// HEX'e çevrilir (istemci "byte[]" görmesin). STANDARD_HASH 12c+; <b>canlı doğrulama borcu</b>.
    /// </summary>
    public string? SatirHashSorgusu(
        string? sema, string tablo, IReadOnlyList<string> anahtarKolonlar,
        IReadOnlyList<string> tumKolonlar, string? whereKosulu)
    {
        if (anahtarKolonlar.Count == 0 || tumKolonlar.Count == 0)
            return null;
        string tam = sema is null ? TirnaklaTanimlayici(tablo) : TamAdYaz(sema, tablo);
        string anahtar = string.Join(", ", anahtarKolonlar.Select(TirnaklaTanimlayici));
        string birlesim = string.Join(" || CHR(31) || ",
            tumKolonlar.Select(k => $"NVL(TO_CHAR({TirnaklaTanimlayici(k)}), CHR(0))"));
        string where = string.IsNullOrWhiteSpace(whereKosulu) ? "" : $" WHERE {whereKosulu}";
        return $"SELECT {anahtar}, RAWTOHEX(STANDARD_HASH({birlesim}, 'MD5')) AS __hash FROM {tam}{where}";
    }

    /// <summary>Oracle'da <c>DEFAULT VALUES</c> yoktur — en az bir kolon verilmelidir.</summary>
    public string BosSatirEkleSql(string tamAd) => throw new NotSupportedException(
        "Oracle'da tüm kolonları varsayılan bırakan INSERT yoktur — yeni satırda en az bir kolon doldurun.");

    /// <summary>Oracle'da sondaki ';' düz SQL'de kabul edilmez (ORA-00933).</summary>
    public string IfadeSonu => "";

    /// <summary>Oracle'da işlem ilk DML ile ÖRTÜK başlar — ayrı bir "begin" ifadesi yoktur.</summary>
    public string IslemBaslatSql => "";

    public string CommitSql => "COMMIT";

    public bool GuvenliYazmaDestekler => true;

    /// <summary>
    /// Oracle'da DDL <b>örtük COMMIT</b> yapar — üstelik iki kez: DDL'den ÖNCE bekleyen işlemi
    /// kalıcılaştırır, sonra DDL'i çalıştırıp bir daha commit eder. Yani DDL çevresindeki
    /// hiçbir şey geri alınamaz. Böyle bir ifadeye Güvenli Yazma bandı açmak yanıltıcı olurdu.
    /// </summary>
    public bool PlanDestekler => true;

    /// <summary>
    /// <b>GERÇEK plan Oracle'da KAPALI</b> (V5-S1d dürüst sınırı): ölçülen satır sayıları için
    /// sorgunun <c>GATHER_PLAN_STATISTICS</c> ipucuyla çalıştırılıp
    /// <c>V$SQL_PLAN_STATISTICS_ALL</c>'ün okunması gerekir — bu görünüm EK YETKİ ister
    /// (Yönetim Paneli'nde de bilinçli olarak yalnız USER_*/V$ kullanıldı) ve canlı Oracle
    /// sunucusu olmadığından doğrulanamaz. Düğme görünmez.
    /// </summary>
    public bool PlanGercekDestekler => false;

    /// <summary>Plan iki adımlıdır; oturum düzeyinde açma/kapama yerine PLAN_TABLE kullanılır.</summary>
    public string PlanAcSql(bool gercek) => "";

    /// <summary>
    /// Bu aracın PLAN_TABLE'a yazdığı satırları temizler.
    ///
    /// <b>Neden <c>statement_id</c> ile sınırlı (A1/B1 bulgusu, 2026-07-19):</b> önce
    /// <c>DELETE FROM plan_table</c> idi ve gerekçesi "PLAN_TABLE oturuma özel geçici
    /// tablodur" diye yazılmıştı. Bu yalnız varsayılan <c>SYS.PLAN_TABLE$</c> eşanlamlısı
    /// için doğrudur; şemada <b>elle oluşturulmuş bir PLAN_TABLE</b> (hâlâ çok yaygın) sıradan
    /// bir tablodur ve orada filtresiz DELETE <b>başka oturumların ve kullanıcıların</b> plan
    /// satırlarını siler — üstelik kullanıcıya sorulmadan ve WHERE'siz DML sigortasına
    /// takılmadan. Artık yalnız kendi yazdığımız satırlar silinir.
    /// </summary>
    /// <summary>
    /// Temizlik ESKİ etiketi de kapsar (ad değişimi 2026-07-20). Aksi hâlde 0.5.0 ve
    /// öncesinin <c>MINISSMS</c> etiketiyle yazdığı satırlar paylaşımlı bir PLAN_TABLE'da
    /// <b>sonsuza dek</b> kalırdı: yeni sürüm yalnız kendi etiketini silecek, eskisini
    /// silecek bir sürüm de artık çalışmayacaktı.
    /// </summary>
    public string PlanKapatSql(bool gercek)
        => $"DELETE FROM plan_table WHERE statement_id IN ('{IfadeEtiketi}', '{EskiIfadeEtiketi}')";

    /// <summary>
    /// Bu aracın PLAN_TABLE satırlarını işaretleyen etiket. Hem yazma hem okuma hem temizlik
    /// bununla sınırlanır — paylaşımlı bir PLAN_TABLE'da başka bir oturumun planını okumayı
    /// ya da silmeyi engeller.
    /// </summary>
    private const string IfadeEtiketi = "SQLST";

    /// <summary>0.5.0 ve öncesinde kullanılan etiket — yalnız temizlikte geçer.</summary>
    private const string EskiIfadeEtiketi = "MINISSMS";

    /// <summary>
    /// <c>EXPLAIN PLAN FOR …</c> hiç satır DÖNDÜRMEZ; planı PLAN_TABLE'a yazar
    /// (bkz. <see cref="PlanOkumaSql"/>). Sorgu ÇALIŞTIRILMAZ — yalnız derlenir.
    /// Sondaki ';' Oracle'da kabul edilmez (ORA-00933).
    /// </summary>
    public string PlanSorgusuYaz(string sql, bool gercek)
    {
        if (gercek)
        {
            throw new NotSupportedException(
                "Gerçek plan Oracle'da desteklenmiyor (V$SQL_PLAN_STATISTICS_ALL ek yetki ister) "
              + "— tahmini planı kullanın.");
        }

        // SET STATEMENT_ID: yazdığımız satırlar işaretlenir ki okuma ve temizlik yalnız
        // onları kapsasın (bkz. IfadeEtiketi).
        return $"EXPLAIN PLAN SET STATEMENT_ID = '{IfadeEtiketi}' FOR {sql.Trim().TrimEnd(';')}";
    }

    /// <summary>
    /// Planın okunduğu ikinci adım. Ağaç, metin girintisinden değil <c>ID</c>/<c>PARENT_ID</c>
    /// sütunlarından kurulur.
    ///
    /// Süzme İKİ ölçütlüdür: önce <c>statement_id</c> (yalnız bizim yazdıklarımız), sonra
    /// bunların en yenisi. Yalnız <c>MAX(plan_id)</c> bakılıyordu ve paylaşımlı bir
    /// PLAN_TABLE'da araya giren başka bir oturumun <c>EXPLAIN PLAN</c>'i daha büyük
    /// <c>plan_id</c> ürettiğinde <b>başkasının sorgusunun planı</b> bu sekmenin planı diye
    /// gösteriliyordu — kullanıcının anlamasının hiçbir yolu yoktu (A1/B1, 2026-07-19).
    ///
    /// <b>Kolon SIRASI <see cref="OraclePlanOkuyucu"/> ile birebir eşleşmelidir.</b>
    /// </summary>
    public string PlanOkumaSql(bool gercek) => $"""
        SELECT id, parent_id, operation, options, object_owner, object_name,
               cardinality, cost, access_predicates, filter_predicates
        FROM plan_table
        WHERE statement_id = '{IfadeEtiketi}'
          AND plan_id = (SELECT MAX(plan_id) FROM plan_table WHERE statement_id = '{IfadeEtiketi}')
        ORDER BY id
        """;

    /// <summary><c>EXPLAIN PLAN FOR</c> tek ifade sarar.</summary>
    public bool PlanTekIfadeIster => true;

    public SorguPlani PlanCoz(QueryResult sonuc, bool gercek) => OraclePlanOkuyucu.Coz(sonuc, gercek);

    public bool OrtukCommitYaparMi(string sql) => SqlAnahtar.IlkKelime(sql) is
        "CREATE" or "ALTER" or "DROP" or "TRUNCATE" or "RENAME"
        or "GRANT" or "REVOKE" or "COMMENT" or "ANALYZE" or "AUDIT" or "NOAUDIT";

    /// <summary>
    /// <c>ALL_SOURCE</c> kaynağı SATIR SATIR tutar (her satır ayrı kayıt). Bu yüzden önce
    /// eşleşen satırı olan nesneler bulunur, sonra o nesnenin TÜM satırları birleştirilerek
    /// kanonik "tanım" üretilir — diğer motorlarla aynı biçim. Sonda ';' YOKTUR (ORA-00933).
    ///
    /// <b>B3/A4'te düzeltilen dört kusur (2026-07-19):</b>
    ///
    /// <b>(a) View'lar eklendi.</b> Oracle view metnini <c>ALL_SOURCE</c>'ta DEĞİL
    /// <c>ALL_VIEWS</c>'ta tutar; <c>ALL_SOURCE</c> yalnız PROCEDURE/FUNCTION/PACKAGE/
    /// TRIGGER/TYPE içerir. Diğer üç motorda view'lar aranırken Oracle'da <b>sessizce
    /// atlanıyordu</b> — kullanıcı "yok" sanıyordu. Artık iki kaynak <c>UNION ALL</c> ile
    /// birleşiyor.
    /// <i>Kalan sınır:</i> <c>ALL_VIEWS.TEXT</c> bir <b>LONG</b> kolondur ve LONG'a SQL'de
    /// <c>LIKE</c>/<c>UPPER</c> uygulanamaz, bu yüzden VARCHAR2 karşılığı <c>TEXT_VC</c>
    /// kullanılıyor. <c>TEXT_VC</c> <b>Oracle 12.2+</b> gerektirir ve tanımın ilk 4000
    /// karakteriyle sınırlıdır — daha uzun view tanımlarında sonrası aranmaz. Bu, hiç
    /// aramamaktan iyidir ama tam değildir; canlı doğrulamada ölçülecek.
    ///
    /// <b>(b) LISTAGG taşması.</b> <c>LISTAGG</c> VARCHAR2 döndürür (4000 bayt); büyük bir
    /// package body sınırı aşınca <b>ORA-01489 ile TÜM arama düşerdi</b> — PG'deki
    /// <c>pg_get_functiondef</c> hatasıyla aynı sınıf. Artık <c>XMLAGG</c> +
    /// <c>getclobval()</c> ile CLOB toplanıyor: uzunluk sınırı yok.
    /// <i>Neden <c>ON OVERFLOW TRUNCATE</c> değil:</i> o 12.2+ ister ve tanımı sessizce
    /// KESERDİ — arama sonucu eksik satır gösterirdi. XMLAGG 11g'de de çalışır ve kesmez.
    ///
    /// <b>(c) Package spec + body ayrıldı.</b> <c>GROUP BY owner, name</c> türü içermiyordu;
    /// aynı adlı PACKAGE ve PACKAGE BODY tek satırda toplanıyor ve ikisi de 1. satırdan
    /// başladığı için metinler <b>iç içe geçip satır numaralarını anlamsızlaştırıyordu</b>.
    /// Artık <c>type</c> de grup anahtarında.
    ///
    /// <b>(d) Tür kodu normalize edildi.</b> Ham Oracle türü ('PROCEDURE', 'FUNCTION'…)
    /// dönüyordu; <see cref="TurCevir"/> bunları tanımadığından her şey StoredProcedure
    /// görünüyordu. Artık diğer üç lehçe gibi MSSQL kodlarına çevriliyor.
    ///
    /// <b>⚠ CANLI DOĞRULANMADI</b> — Oracle sunucusu yok. Canlı doğrulama borcunda.
    /// </summary>
    public string MetinAramaSorgusu(string aranan)
    {
        string desen = LikeKacir(aranan);
        return $"""
            SELECT sema, ad, tur, tanim FROM (
                SELECT s.owner AS sema, s.name AS ad,
                       CASE s.type
                           WHEN 'PROCEDURE' THEN 'P'
                           WHEN 'FUNCTION'  THEN 'FN'
                           ELSE 'P'
                       END AS tur,
                       XMLAGG(XMLELEMENT(e, s.text) ORDER BY s.line).EXTRACT('//text()').getclobval() AS tanim
                FROM all_source s
                WHERE s.owner = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
                  AND (s.owner, s.name, s.type) IN (
                        SELECT owner, name, type FROM all_source
                        WHERE owner = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
                          AND UPPER(text) LIKE UPPER('%{desen}%') ESCAPE '\'
                      )
                GROUP BY s.owner, s.name, s.type
                UNION ALL
                -- TEXT değil TEXT_VC: ALL_VIEWS.TEXT bir LONG kolondur ve LONG'a SQL'de
                -- LIKE/UPPER UYGULANAMAZ (ilk yazımda bu hata yapıldı ve Oracle'da sorgu
                -- hiç çalışmazdı). TEXT_VC aynı metnin VARCHAR2 karşılığıdır (12.2+).
                SELECT v.owner AS sema, v.view_name AS ad, 'V' AS tur, v.text_vc AS tanim
                FROM all_views v
                WHERE v.owner = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
                  AND v.text_vc IS NOT NULL
                  AND UPPER(v.text_vc) LIKE UPPER('%{desen}%') ESCAPE '\'
            )
            ORDER BY sema, ad
            FETCH FIRST {ILehce.AramaTavani} ROWS ONLY
            """;
    }

    /// <summary>LIKE jokerlerini kaçırır; yoksa "kdv_orani" araması "kdvXorani"yi de bulurdu.</summary>
    private static string LikeKacir(string s) => s
        .Replace("'", "''")
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    public bool KiyasGuvenliMi(string tip, int uzunluk)
        => tip is not ("FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE" or "CLOB" or "NCLOB" or "BLOB" or "LONG" or "LONG RAW" or "XMLTYPE");

    /// <summary>
    /// Oracle Yönetim Paneli (V3). Yalnız kullanıcının KENDİ şemasına bakan (USER_*/V$) salt
    /// okunur sorgular — DBA_* ayrıcalık ister, AWR/ASH ise ek lisans gerektirir (kullanılmaz).
    /// Sorgularda sondaki ';' YOKTUR (Oracle düz SQL'de kabul etmez).
    /// </summary>
    public IReadOnlyList<TeshisBolumu> TeshisBolumleri =>
    [
        new("Index kullanımı",
            "Şemadaki index'ler ve durumları. Oracle kullanım sayacı varsayılan KAPALIDIR; açmak için ALTER INDEX … MONITORING USAGE gerekir (V$OBJECT_USAGE).",
            """
            SELECT i.index_name AS index_adi, i.table_name AS tablo, i.uniqueness AS teklik,
                   i.status AS durum, i.num_rows AS satir, i.last_analyzed AS son_analiz,
                   LISTAGG(c.column_name, ', ') WITHIN GROUP (ORDER BY c.column_position) AS kolonlar
            FROM user_indexes i
            LEFT JOIN user_ind_columns c ON c.index_name = i.index_name
            GROUP BY i.index_name, i.table_name, i.uniqueness, i.status, i.num_rows, i.last_analyzed
            ORDER BY i.table_name, i.index_name
            """,
            Takip: "TAKİP: (1) durum sütunu VALID dışında bir şeyse (UNUSABLE/N/A) — o index "
                 + "KULLANILMIYOR ve optimizer onu yok sayıyor; genelde bir partition işlemi ya "
                 + "da direct-path yükleme sonrası kalır, REBUILD gerekir. (2) son_analiz boş "
                 + "ya da çok eskiyse istatistik yok demektir. (3) Aynı tabloda AYNI KOLONLA "
                 + "BAŞLAYAN birden çok index varsa biri muhtemelen gereksizdir. "
                 + "DİKKAT: Oracle'da index kullanım sayacı VARSAYILAN OLARAK KAPALIDIR — bu "
                 + "bölüm 'kaç kez kullanıldı' diyemez, yalnız neyin VAR olduğunu söyler. "
                 + "Kullanım ölçmek için: ALTER INDEX <ad> MONITORING USAGE;"),

        new("Segment boyutları",
            "Şemadaki en büyük tablo/index segmentleri (USER_SEGMENTS).",
            """
            SELECT segment_name AS nesne, segment_type AS tur,
                   ROUND(bytes/1024/1024, 2) AS boyut_mb, blocks AS blok, extents AS uzanti
            FROM user_segments
            ORDER BY bytes DESC
            FETCH FIRST 100 ROWS ONLY
            """,
            Takip: "TAKİP: (1) En büyük segmentler zaten en üstte — yer sorununda buradan "
                 + "başlanır. (2) tur = INDEX olan segmentlerin toplamı TABLE toplamına yaklaşıyorsa "
                 + "index yükü fazladır. (3) uzanti (extent) sayısı binlerse segment çok parça "
                 + "hâlinde büyümüştür. (4) Bir TABLO segmenti beklediğinizden büyükse silinen "
                 + "satırların yeri geri verilmemiş olabilir — Oracle'da DELETE segmenti "
                 + "küçültmez; yer geri almak için SHRINK ya da MOVE gerekir."),

        new("Aktif oturumlar",
            "Şu an çalışan oturumlar; BLOCKING_SESSION dolu olanlar başkasını bekliyordur.",
            """
            SELECT s.sid, s.serial# AS seri, s.username AS kullanici, s.status AS durum,
                   s.osuser AS os_kullanici, s.machine AS makine, s.program,
                   s.blocking_session AS bloklayan, s.event AS bekleme_olayi,
                   s.seconds_in_wait AS bekleme_sn
            FROM v$session s
            WHERE s.type = 'USER' AND s.sid <> SYS_CONTEXT('USERENV','SID')
            ORDER BY DECODE(s.status, 'ACTIVE', 0, 1), s.seconds_in_wait DESC
            """,
            Takip: "TAKİP: (1) bloklayan sütunu DOLU olan satırlar birini bekliyor — ayrıntısı "
                 + "'Bloklama zinciri' bölümündedir, kimin kökte olduğunu orası söyler. "
                 + "(2) bekleme_olayi 'enq: TX - row lock contention' ise klasik satır kilidi "
                 + "çakışmasıdır. 'log file sync' yüksekse commit'ler diske yazılmayı bekliyor. "
                 + "(3) bekleme_sn büyük ve durum ACTIVE olan satırlar. (4) makine / program "
                 + "sütunları hangi uygulamanın sorumlu olduğunu söyler — üretimde asıl işe "
                 + "yarayan bilgi budur."),

        new("Tablo istatistik tazeliği",
            "LAST_ANALYZED eski/boş olan tablolarda optimizer yanlış plan seçebilir.",
            """
            SELECT table_name AS tablo, num_rows AS satir, blocks AS blok,
                   last_analyzed AS son_analiz,
                   CASE WHEN last_analyzed IS NULL THEN '⚠ hiç analiz edilmedi'
                        WHEN last_analyzed < SYSDATE - 30 THEN '⚠ 30 günden eski'
                        ELSE '' END AS not
            FROM user_tables
            ORDER BY NVL(last_analyzed, DATE '1900-01-01'), num_rows DESC
            FETCH FIRST 100 ROWS ONLY
            """,
            Takip: "TAKİP: '⚠ hiç analiz edilmedi' ve '⚠ 30 günden eski' işaretli satırlar — "
                 + "liste zaten en bayattan başlar. HİÇ analiz edilmemiş bir tabloda optimizer "
                 + "kör sayılır ve dinamik örneklemeye düşer; büyük tabloda bu ciddi biçimde "
                 + "yanlış plan üretir. satir sütunu istatistikteki değerdir — gerçekten "
                 + "bildiğiniz satır sayısından çok farklıysa istatistik bayat demektir. "
                 + "ÇÖZÜM: DBMS_STATS.GATHER_TABLE_STATS(USER, '<tablo>');"),

        // V5-S3. "Aktif oturumlar" bölümü blocking_session sütununu zaten gösteriyor ama
        // bloklayanın KİM olduğunu ve zincirin kökünü söylemiyor. Burada bekleyen ile
        // bloklayan yan yana gelir; kökü, kendisi bloklanmayan bloklayandır — ve o oturum
        // genelde INACTIVE'dir (açık işlem tutup bekleyen uygulama), bu yüzden aktif oturum
        // listelerinde göze çarpmaz.
        new("Bloklama zinciri (kök bloklayan)",
            "Bekleyen oturum ile onu bloklayan oturum yan yana. 'KÖK bloklayan' işaretli satır zincirin başıdır; durumu çoğu zaman INACTIVE'dir — hiçbir sorgu çalıştırmadığı için aktif oturum listesinde dikkat çekmez.",
            """
            SELECT bekleyen.sid AS bekleyen_sid, bekleyen.username AS bekleyen_kullanici,
                   bekleyen.event AS bekleme_olayi, bekleyen.seconds_in_wait AS bekleme_sn,
                   bloklayan.sid AS bloklayan_sid, bloklayan.username AS bloklayan_kullanici,
                   bloklayan.status AS bloklayan_durum, bloklayan.machine AS bloklayan_makine,
                   bloklayan.program AS bloklayan_program,
                   CASE WHEN bloklayan.blocking_session IS NULL
                        THEN 'KÖK bloklayan' ELSE '' END AS not
            FROM v$session bekleyen
            JOIN v$session bloklayan ON bloklayan.sid = bekleyen.blocking_session
            WHERE bekleyen.blocking_session IS NOT NULL
            ORDER BY bekleyen.seconds_in_wait DESC
            FETCH FIRST 100 ROWS ONLY
            """,
            Takip: "TAKİP: 'KÖK bloklayan' işaretli satır — müdahale edilecek oturum ODUR, "
                 + "bekleyenler değil. Kökü çözünce zincirin tamamı açılır. bloklayan_durum "
                 + "genelde INACTIVE çıkar: sorumlu, COMMIT/ROLLBACK yapmadan bekleyen "
                 + "uygulamadır ve hiçbir sorgu çalıştırmadığı için 'Aktif oturumlar' "
                 + "listesinde dikkat çekmez. bloklayan_program ve bloklayan_makine hangi "
                 + "uygulamanın sorumlu olduğunu söyler — aynı program tekrar tekrar kökte "
                 + "çıkıyorsa çözüm oturum öldürmek değil, o kod yolundaki işlem yönetimini "
                 + "düzeltmektir."),

        new("Geçici alan kullanımı",
            "Geçici tablo alanını kim tüketiyor? Büyük sıralama ve hash birleştirmeleri belleğe sığmayınca buraya taşar — SQL Server'daki tempdb şişmesinin karşılığıdır.",
            """
            SELECT u.tablespace AS tablo_alani, u.segtype AS segment_turu,
                   u.session_addr, u.sqlid AS sql_id,
                   ROUND(u.blocks * 8 / 1024, 1) AS kullanim_mb,
                   s.sid, s.username AS kullanici, s.program, s.status AS durum
            FROM v$tempseg_usage u
            LEFT JOIN v$session s ON s.saddr = u.session_addr
            ORDER BY u.blocks DESC
            FETCH FIRST 50 ROWS ONLY
            """,
            Takip: "TAKİP: kullanim_mb'si büyük olan İLK SATIR — geçici alanı dolduran oturum "
                 + "odur ve 'ORA-01652: unable to extend temp segment' hatası geldiğinde "
                 + "bakılacak yer burasıdır. segment_turu SORT ise devasa bir sıralama, HASH ise "
                 + "belleğe sığmayan bir birleştirme var demektir; ikisi de sorgunun kendisini "
                 + "gösterir (sql_id ile planına bakın), tablo alanını büyütmek yalnız belirtiyi "
                 + "erteler. kullanici / program sütunu sorumluyu söyler. Tek bir oturum "
                 + "toplamın çoğunu tüketiyorsa sorun sunucu kapasitesi değil, o sorgudur."),
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
