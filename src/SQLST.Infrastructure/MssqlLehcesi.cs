using System.Data.Common;
using Microsoft.Data.SqlClient;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// SQL Server lehçesi (V3-S1 / Faz 0): v1/v2 boyunca gövdeye serpiştirilmiş MSSQL'e
/// özgü parçaların tek adresi — köşeli tırnak, USE, XACT_STATE, @@TRANCOUNT, SqlClient
/// bağlantı üretimi, InfoMessage aboneliği ve SqlException → SqlHata eşlemesi.
///
/// Davranış v2 ile BİREBİR aynıdır; bu sınıf yeni kural getirmez, var olanı toplar.
/// </summary>
public sealed class MssqlLehcesi : ILehce
{
    private readonly BaglantiDizesiKurucu _dizeKurucu;

    public MssqlLehcesi(ISecretProtector protector) => _dizeKurucu = new BaglantiDizesiKurucu(protector);

    public string MotorId => "mssql";

    /// <summary>QUOTENAME mantığı (00 §4 ders 4): ']' ikilenir.</summary>
    public string TirnaklaTanimlayici(string ad) => $"[{ad.Replace("]", "]]")}]";

    public DbConnection BaglantiOlustur(ConnectionProfile profil, string? veritabaniOverride, bool havuz)
    {
        var baglanti = (SqlConnection)SqlClientFactory.Instance.CreateConnection()!;
        baglanti.ConnectionString = _dizeKurucu.Kur(profil, veritabaniOverride, havuz);
        baglanti.FireInfoMessageEventOnUserErrors = false;
        return baglanti;
    }

    public void KomutuAyarla(DbCommand komut) { /* SqlClient'ta ek ayar gerekmez */ }

    public IDisposable BilgiMesajlariniDinle(DbConnection baglanti, Action<string> topla)
    {
        var sqlBaglanti = (SqlConnection)baglanti;
        void Dinleyici(object _, SqlInfoMessageEventArgs e) => topla(e.Message);
        sqlBaglanti.InfoMessage += Dinleyici;
        return new Abonelik(() => sqlBaglanti.InfoMessage -= Dinleyici);
    }

    public SqlHata HataYorumla(Exception ex) => ex is SqlException s
        ? new SqlHata(s.Message, s.Number, s.LineNumber, s.Class)
        : new SqlHata(ex.Message, 0, 0, 0); // sağlayıcı dışı istisnada da mesaj kaybolmaz

    /// <summary>TÜM hatalar (kullanıcı isteği 2026-07-30): SqlException.Errors — PARSEONLY birden çok
    /// sözdizimi hatası döndürebilir; canlı denetim hepsini gösterir (tek Hata yalnız ilkini taşırdı).</summary>
    public IReadOnlyList<SqlHata> HatalariYorumla(Exception ex) => ex is SqlException { Errors.Count: > 0 } s
        ? [.. s.Errors.Cast<SqlError>().Select(e => new SqlHata(e.Message, e.Number, e.LineNumber, e.Class))]
        : [HataYorumla(ex)];

    /// <summary>SQL Server'da veritabanı bağlantı ömrü boyunca USE ile değiştirilebilir.</summary>
    public bool AcikBaglantidaVeritabaniDegisir => true;

    public string VeritabaniSecSql(string veritabani) => $"USE {TirnaklaTanimlayici(veritabani)};";

    public string? VeritabaniOlusturSql(string ad) => $"CREATE DATABASE {TirnaklaTanimlayici(ad)};";

    /// <summary>XACT_STATE(): 1 = commit edilebilir, -1 = "doomed" (yalnız ROLLBACK), 0 = işlem yok.</summary>
    public async Task<IslemDurumu> IslemDurumuAsync(DbConnection baglanti, CancellationToken ct)
    {
        await using DbCommand komut = baglanti.CreateCommand();
        komut.CommandText = "SELECT XACT_STATE();";
        object? deger = await komut.ExecuteScalarAsync(ct);
        return Convert.ToInt32(deger) switch
        {
            1 => IslemDurumu.Acik,
            -1 => IslemDurumu.Mahkum,
            _ => IslemDurumu.Yok,
        };
    }

    public bool IslemDurumuBilinir => true;

    public string GeriAlSql() => "IF @@TRANCOUNT > 0 ROLLBACK;";

    // ── Katalog (v2 SchemaService'ten verbatim taşındı — davranış aynı) ───────

    public string VeritabanlariSorgusu => """
        SELECT name, CASE WHEN database_id <= 4 THEN 1 ELSE 0 END AS sistem
        FROM sys.databases
        WHERE state = 0 AND HAS_DBACCESS(name) = 1
        ORDER BY sistem, name;
        """;

    /// <summary>Kaynak sys.* katalog view'ları (INFORMATION_SCHEMA değil), tek batch dört result set.</summary>
    public IReadOnlyList<string> SemaSorgulari => [SemaSorgusu];

    // FK bağları satır-başına-kolon (v6-S2): fk_id = object_id (int, gruplama), sira =
    // constraint_column_id. STRING_AGG YOK (eski sunucu uyumu) — SchemaService gruplar.
    public string YabanciAnahtarSorgusu => """
        SELECT fk.object_id AS fk_id, fkc.constraint_column_id AS sira,
               sch.name AS kaynak_sema, tp.name AS kaynak_tablo, cp.name AS kaynak_kolon,
               rsch.name AS hedef_sema, tr.name AS hedef_tablo, cr.name AS hedef_kolon,
               fk.name AS kisit_adi
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
        JOIN sys.schemas sch ON sch.schema_id = tp.schema_id
        JOIN sys.columns cp ON cp.object_id = fk.parent_object_id AND cp.column_id = fkc.parent_column_id
        JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
        JOIN sys.schemas rsch ON rsch.schema_id = tr.schema_id
        JOIN sys.columns cr ON cr.object_id = fk.referenced_object_id AND cr.column_id = fkc.referenced_column_id
        ORDER BY fk.object_id, fkc.constraint_column_id;
        """;

    // v7 index farkı: kanonik satır-başına-kolon (sema, tablo, indeks, benzersiz, sıra, kolon).
    // PK-destekli indeks hariç (is_primary_key=0); INCLUDE kolonları hariç (key_ordinal>0);
    // heap yok (type>0). STRING_AGG YOK — satır-satır, SchemaService gruplar.
    public string IndeksSorgusu => """
        SELECT s.name AS sema, t.name AS tablo, i.name AS indeks,
               i.is_unique AS benzersiz, ic.key_ordinal AS sira, c.name AS kolon
        FROM sys.indexes i
        JOIN sys.tables  t ON t.object_id = i.object_id AND t.is_ms_shipped = 0
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                 AND ic.is_included_column = 0
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.is_primary_key = 0 AND i.type > 0 AND i.is_hypothetical = 0
        ORDER BY s.name, t.name, i.name, ic.key_ordinal;
        """;

    private static string SemaSorgusu => """
        SELECT DB_NAME() AS db;

        SELECT s.name AS sema, o.name AS ad, o.type AS tur
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.type IN ('U','V','P','FN','IF','TF') AND o.is_ms_shipped = 0
        ORDER BY s.name, o.name;

        SELECT s.name AS sema, o.name AS ad, c.name AS kolon, t.name AS tip,
               c.max_length, c.precision, c.scale, c.is_nullable,
               CASE WHEN pkc.column_id IS NOT NULL THEN 1 ELSE 0 END AS pk
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id AND o.type IN ('U','V') AND o.is_ms_shipped = 0
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.types  t ON t.user_type_id = c.user_type_id
        LEFT JOIN (
            SELECT ic.object_id, ic.column_id
            FROM sys.index_columns ic
            JOIN sys.indexes i ON i.object_id = ic.object_id
                              AND i.index_id = ic.index_id
                              AND i.is_primary_key = 1
        ) pkc ON pkc.object_id = c.object_id AND pkc.column_id = c.column_id
        ORDER BY s.name, o.name, c.column_id;

        SELECT s.name AS sema, o.name AS ad, p.name AS parametre, t.name AS tip,
               p.max_length, p.precision, p.scale, p.is_output
        FROM sys.parameters p
        JOIN sys.objects o ON o.object_id = p.object_id
                         AND o.type IN ('P','FN','IF','TF') AND o.is_ms_shipped = 0
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.types   t ON t.user_type_id = p.user_type_id
        WHERE p.parameter_id > 0
        ORDER BY s.name, o.name, p.parameter_id;
        """;

    public string TanimSorgusu(SemaNesnesi nesne)
    {
        // ISqlExecutor parametre almaz; ad tek tırnak ikilenerek literale gömülür
        // (adın kendisi zaten TamAdKoseli ile parantezlenmiş ve ']' kaçırılmıştır).
        string literal = nesne.TamAdKoseli.Replace("'", "''");
        return $"SELECT OBJECT_DEFINITION(OBJECT_ID(N'{literal}')) AS tanim;";
    }

    public string DuzenlemeMetaSorgusu(SemaNesnesi tablo)
    {
        string literal = tablo.TamAdKoseli.Replace("'", "''");
        // ANAHTAR = satır kimliği. Kullanıcı bulgusu 2026-07-27: PK CONSTRAINT'i olmayan ama
        // benzersiz index'li tablolar (ör. Yonetim.KullaniciRolleri) edit modda düzenlenemiyordu.
        // Artık kimlik için sırayla: PRIMARY KEY → UNIQUE CONSTRAINT → UNIQUE INDEX seçilir (en
        // uygun TEK index). Böylece PK constraint'i olmayan ama benzersiz kimliği olan tablolar da
        // düzenlenebilir. (Hiç PK/unique yoksa ve tek IDENTITY varsa SchemaService onu anahtar sayar.)
        return $"""
            SELECT c.name, t.name AS tip, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed,
                   CASE WHEN t.name IN ('timestamp','rowversion') THEN 1 ELSE 0 END AS rv,
                   CASE WHEN pkc.column_id IS NOT NULL THEN 1 ELSE 0 END AS pk
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN (
                SELECT ic.column_id
                FROM sys.index_columns ic
                WHERE ic.object_id = OBJECT_ID(N'{literal}')
                  AND ic.key_ordinal > 0   -- INCLUDE kolonları anahtar değildir
                  AND ic.index_id = (
                      SELECT TOP 1 i.index_id
                      FROM sys.indexes i
                      WHERE i.object_id = OBJECT_ID(N'{literal}')
                        AND i.is_hypothetical = 0 AND i.index_id > 0   -- heap (0) hariç
                        AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1 OR i.is_unique = 1)
                      ORDER BY i.is_primary_key DESC, i.is_unique_constraint DESC, i.index_id
                  )
            ) pkc ON pkc.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(N'{literal}')
            ORDER BY c.column_id;
            """;
    }

    public bool DuzenlemeDestekler => true;

    public LiteralKurallari LiteralKurallari => LiteralKurallari.TSql;

    public string TamAdYaz(string sema, string tablo)
        => $"{TirnaklaTanimlayici(sema)}.{TirnaklaTanimlayici(tablo)}";

    /// <summary>
    /// Veri karşılaştırma satır parmak izi (v7-S2). <c>BINARY_CHECKSUM(*)</c>: sürümden bağımsız,
    /// tüm sürümlerde var, tip dönüşümü gerektirmez (HASHBYTES'ın 8000-bayt/eski-sürüm ve NULL/tip
    /// tuzaklarını atlar). Sınır: çakışma olasılığı düşük ama sıfır değildir ve XML/text/image/CLR
    /// tipli kolonlar içeren tabloda sunucu hata verir — bu durum kullanıcıya iletilir.
    /// </summary>
    public string? SatirHashSorgusu(
        string? sema, string tablo, IReadOnlyList<string> anahtarKolonlar,
        IReadOnlyList<string> tumKolonlar, string? whereKosulu)
    {
        if (anahtarKolonlar.Count == 0)
            return null;
        string tam = sema is null ? TirnaklaTanimlayici(tablo) : TamAdYaz(sema, tablo);
        string anahtar = string.Join(", ", anahtarKolonlar.Select(TirnaklaTanimlayici));
        string where = string.IsNullOrWhiteSpace(whereKosulu) ? "" : $" WHERE {whereKosulu}";
        return $"SELECT {anahtar}, BINARY_CHECKSUM(*) AS __hash FROM {tam}{where}";
    }

    public string BosSatirEkleSql(string tamAd) => $"INSERT INTO {tamAd} DEFAULT VALUES";

    public string IfadeSonu => ";";

    public string IslemBaslatSql => "BEGIN TRAN;";

    // İÇ-İÇE TRANSACTION'A DAYANIKLI COMMIT (kullanıcı bulgusu 2026-07-27: "update commit diyorum
    // ama değişmiyor"). Kök neden: Mersis tabloları TETİKLEYİCİ/iç BEGIN TRAN içerebilir; o zaman
    // @@TRANCOUNT > 1 olur. Tek "COMMIT" yalnız bir düzey kapatır, işlem AÇIK kalır ve bağlantı
    // havuza dönüp SIFIRLANINCA ROLLBACK edilir → değişiklik SESSİZCE KAYBOLUR (kullanıcı "kalıcı"
    // mesajı görür ama veri değişmez). WHILE tüm düzeyleri kapatır; @@TRANCOUNT=0 ise hiç dönmez
    // (düz "COMMIT"in "no corresponding BEGIN TRANSACTION" hatasını da eler).
    public string CommitSql => "WHILE @@TRANCOUNT > 0 COMMIT;";

    public bool GuvenliYazmaDestekler => true;

    /// <summary>SQL Server'da DDL de işlemseldir — CREATE TABLE bile geri alınabilir.</summary>
    public bool OrtukCommitYaparMi(string sql) => false;

    public bool PlanDestekler => true;

    /// <summary>
    /// SHOWPLAN_XML: sorguyu yalnız DERLER, çalıştırmaz (yazma sorgusunda bile veri değişmez).
    /// STATISTICS XML: sorgu çalışır, planla birlikte GERÇEK satır sayıları da gelir.
    /// </summary>
    /// <summary>
    /// <b>UYARI (2026-07-19):</b> <c>SET SHOWPLAN_XML</c> "batch'teki TEK ifade" olmak zorundadır.
    /// <c>QueryService</c> MSSQL sorgularına izolasyon ön eki eklediğinden aynı batch'e iki ifade
    /// girer ve sunucu <i>"The SET SHOWPLAN statements must be the only statements in the batch"</i>
    /// hatası verir — canlı doğrulandı. <c>SET STATISTICS XML</c>'de böyle bir kısıt YOKTUR.
    /// Kullanıcı kararıyla tahmini plan arayüzden kaldırıldığı için bu dal artık ÇAĞRILMIYOR;
    /// yeniden açılırsa ön ekin atlanması gerekir.
    /// </summary>
    public string PlanAcSql(bool gercek) => gercek ? "SET STATISTICS XML ON;" : "SET SHOWPLAN_XML ON;";

    public string PlanKapatSql(bool gercek) => gercek ? "SET STATISTICS XML OFF;" : "SET SHOWPLAN_XML OFF;";

    /// <summary>SQL Server'da plan oturum ayarıyla toplanır — sorgu metni AYNEN gider.</summary>
    public string PlanSorgusuYaz(string sql, bool gercek) => sql;

    /// <summary>
    /// MSSQL çok ifadeli batch'in planını verebilir — ama <b>şekli moda göre değişir</b>:
    /// <c>SHOWPLAN_XML</c> tüm ifadeleri TEK belgede verir, kullandığımız
    /// <c>STATISTICS XML</c> ise <b>ifade başına AYRI sonuç kümesi</b> döndürür.
    /// Bu ayrım <see cref="PlanCoz"/>'da atlanmıştı (A1/B1 bulgusu, 2026-07-19).
    /// </summary>
    public bool PlanTekIfadeIster => false;

    /// <summary><c>SET STATISTICS XML</c> ile gerçek satır sayaçları gelir.</summary>
    public bool PlanGercekDestekler => true;

    /// <summary>Plan tek adımlıdır — sorgunun kendi sonucuyla birlikte gelir.</summary>
    public string PlanOkumaSql(bool gercek) => "";

    /// <summary>
    /// Plan XML'i kendi sonuç kümesinde gelir ("Microsoft SQL Server 2005 XML Showplan").
    /// GERÇEK planda sorgunun kendi sonuçları da döner ve plan kümesi onlardan SONRA gelir —
    /// bu yüzden küme sırasına değil KOLON ADINA bakılır.
    ///
    /// <b>TÜM plan kümeleri toplanır.</b> Önce ilk kümede <c>return</c> ediliyordu ve bu,
    /// çok ifadeli bir batch'te 2..N. ifadelerin planını <b>sessizce atıyordu</b>: kullanıcı
    /// eksiksiz görünen bir plan görüyor, geri kalanın incelenip atıldığını hiç bilmiyordu.
    /// Sebep <see cref="PlanTekIfadeIster"/>'deki mod farkı — <c>STATISTICS XML</c> ifade
    /// başına ayrı küme döndürür (A1/B1 bulgusu, 2026-07-19).
    /// </summary>
    public SorguPlani PlanCoz(QueryResult sonuc, bool gercek)
    {
        var ifadeler = new List<IfadePlani>();

        foreach (ResultSetData set in sonuc.ResultSetler)
        {
            if (set.Kolonlar.Count != 1 || set.Satirlar.Count == 0)
                continue;
            if (!set.Kolonlar[0].Ad.Contains("Showplan", StringComparison.OrdinalIgnoreCase))
                continue;
            if (set.Satirlar[0][0]?.ToString() is { Length: > 0 } xml)
                ifadeler.AddRange(MssqlPlanOkuyucu.Coz(xml, gercek).Ifadeler);
        }

        return ifadeler.Count > 0
            ? new SorguPlani(gercek, ifadeler)
            : throw new InvalidOperationException(
                "Sunucu plan döndürmedi (sorgu plan üretmeyen bir ifade olabilir).");
    }

    /// <summary>
    /// <c>sys.sql_modules</c>: SP/view/fonksiyon/trigger gövdeleri. Şifreli nesnelerin
    /// tanımı NULL'dur (aranamaz) — <c>definition IS NOT NULL</c> ile elenir.
    /// <c>TOP</c> ile <see cref="ILehce.AramaTavani"/> nesne (B4/A5).
    /// </summary>
    public string MetinAramaSorgusu(string aranan) => $"""
        SELECT TOP {ILehce.AramaTavani}
               s.name AS sema, o.name AS ad, o.type AS tur, m.definition AS tanim
        FROM sys.sql_modules m
        JOIN sys.objects o ON o.object_id = m.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE m.definition IS NOT NULL
          AND m.definition LIKE '%{LikeKacir(aranan)}%' ESCAPE '\'
        ORDER BY s.name, o.name;
        """;

    /// <summary>
    /// LIKE deseninde <c>%</c>, <c>_</c> ve <c>[</c> joker anlamı taşır; kullanıcı metnindeki
    /// bu karakterler kaçırılmazsa <b>yanlış eşleşme</b> olur (ör. "kdv_orani" araması
    /// "kdvXorani"yi de bulurdu). Tek tırnak ayrıca literal kaçışı için ikilenir.
    /// </summary>
    private static string LikeKacir(string s) => s
        .Replace("'", "''")
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_")
        .Replace("[", "\\[");

    public SemaNesneTuru TurCevir(string kod) => kod.Trim() switch
    {
        "U" => SemaNesneTuru.Tablo,
        "V" => SemaNesneTuru.View,
        "P" => SemaNesneTuru.StoredProcedure,
        "FN" or "IF" or "TF" => SemaNesneTuru.Fonksiyon,
        _ => throw new ArgumentOutOfRangeException(nameof(kod), kod, "Beklenmeyen sys.objects.type"),
    };

    public string IlkNSatirSorgusu(SemaNesnesi nesne, int n)
        => $"SELECT TOP {n} * FROM {TamAd(nesne)};";

    /// <summary>Özel SELECT sınırı T-SQL'de baştadır: TOP (n); kuyruk boş (ILehce varsayılanını ezer).</summary>
    public string SatirSinirBasi(int n) => $"TOP ({n}) ";

    public string SatirSinirSonu(int n) => "";

    private string TamAd(SemaNesnesi nesne)
        => $"{TirnaklaTanimlayici(nesne.Sema)}.{TirnaklaTanimlayici(nesne.Ad)}";

    /// <summary>sys tip bilgisinden görüntülük tam ad: nvarchar(50), varbinary(max), decimal(18,2), datetime2(7)…</summary>
    public string TipYaz(string tip, int uzunluk, int kesinlik, int olcek) => Bicimle(tip, uzunluk, kesinlik, olcek);

    /// <summary>Saf tip biçimleyici — birim testleri (SchemaServiceTests) buradan çağırır.</summary>
    public static string Bicimle(string tip, int maxLength, int precision, int scale) => tip switch
    {
        "nvarchar" or "nchar" => maxLength == -1 ? $"{tip}(max)" : $"{tip}({maxLength / 2})",
        "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{tip}(max)" : $"{tip}({maxLength})",
        "decimal" or "numeric" => $"{tip}({precision},{scale})",
        "datetime2" or "time" or "datetimeoffset" => $"{tip}({scale})",
        _ => tip,
    };

    /// <summary>Eski-değer kıyasına girmesi kırılgan tipler (07-r2 §4): kıyas WHERE'inden dışlanır.</summary>
    private static readonly string[] KiyasKirilganTipler =
        ["float", "real", "text", "ntext", "image", "xml", "sql_variant", "geography", "geometry", "hierarchyid"];

    public bool KiyasGuvenliMi(string tip, int uzunluk) => uzunluk != -1 && !KiyasKirilganTipler.Contains(tip);

    /// <summary>
    /// SQL Server'ın KENDİ zengin Yönetim Paneli vardır (V2-S7: eksik index skorlaması,
    /// örtüşme analizi, RCSI karar paneli, hazır script'ler) — genel panel kullanılmaz.
    /// </summary>
    public IReadOnlyList<TeshisBolumu> TeshisBolumleri => [];

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
