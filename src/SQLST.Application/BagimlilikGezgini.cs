namespace SQLST.Application;

using SQLST.Contracts;

/// <summary>
/// 🔗 Bağımlılık Gezgini (kullanıcı onayı 2026-08-03, öneri 2): bir nesnenin "kim beni kullanıyor /
/// ben kimi kullanıyorum" sorusuna cevap. Kayıt Haritası deseni: yorumlu SELECT script'i sekmede
/// ÇALIŞTIRILIR — çoklu grid + düzenlenebilir sorgu bir arada. SAF üretici.
/// Ağaç görünümü (2026-08-03 devamı): <see cref="YonSorgusu"/> tek yönün DÜZ listesini döndürür —
/// pencere her düğümü açıldıkça aynı yönde yeniden sorgular (SP → SP → tablo zinciri).
///
/// Motorlar (madde 2, 2026-08-03): MSSQL sys.sql_expression_dependencies (kesin);
/// PostgreSQL pg_depend/pg_rewrite (view bağımlılıkları KESİN) + pg_proc.prosrc metin eşleşmesi
/// (fonksiyonlar YAKLAŞIK — PG fonksiyon gövdesi bağımlılıklarını izlemez, Tür'e "~" işlenir).
/// MySQL borç (information_schema.VIEW_TABLE_USAGE ile eklenebilir — roadmap).
/// </summary>
public static class BagimlilikGezgini
{
    /// <summary>
    /// Ağacın bir düğümünün çocukları: <paramref name="kullananlar"/> true ise bu nesneye başvuranlar
    /// (⬅ etki analizi — ALTER'dan kim etkilenir), false ise gövdesinin başvurdukları (➡ SOAP
    /// arkasındaki SP zinciri). Kolonlar: [Nesne] = şema.ad, [Tür] = tür açıklaması.
    /// </summary>
    public static string YonSorgusu(string tamAd, bool kullananlar, string motorId = "mssql")
        => motorId switch
        {
            "postgres" => PgYonSorgusu(tamAd, kullananlar),
            "mysql" => MySqlYonSorgusu(tamAd, kullananlar),
            "oracle" => OracleYonSorgusu(tamAd, kullananlar),
            _ => MssqlYonSorgusu(tamAd, kullananlar),
        };

    /// <summary>
    /// Oracle yönü (2026-08-03 devamı): ALL_DEPENDENCIES sözlüğü — SP/fonksiyon/paket/view/trigger
    /// bağımlılıklarını sunucu KESİN izler (yaklaşık eşleşme gerekmez; MSSQL'deki
    /// sys.sql_expression_dependencies'in birebir karşılığı). Adlar sözlükte BÜYÜK HARF tutulur.
    /// </summary>
    private static string OracleYonSorgusu(string tamAd, bool kullananlar)
    {
        int nokta = tamAd.IndexOf('.');
        string sema = (nokta > 0 ? tamAd[..nokta] : "").Replace("'", "''").ToUpperInvariant();
        string ad = (nokta > 0 ? tamAd[(nokta + 1)..] : tamAd).Replace("'", "''").ToUpperInvariant();
        string semaKosulu = sema.Length > 0 ? $"AND d.referenced_owner = '{sema}' " : "";
        string sahipKosulu = sema.Length > 0 ? $"AND d.owner = '{sema}' " : "";
        return kullananlar
            ? $"""
              SELECT DISTINCT d.owner || '.' || d.name AS "Nesne", d.type AS "Tür"
              FROM all_dependencies d
              WHERE d.referenced_name = '{ad}' {semaKosulu}
              ORDER BY 1
              """
            : $"""
              SELECT DISTINCT d.referenced_owner || '.' || d.referenced_name AS "Nesne",
                     d.referenced_type AS "Tür"
              FROM all_dependencies d
              WHERE d.name = '{ad}' {sahipKosulu}
                AND d.referenced_owner NOT IN ('SYS', 'SYSTEM', 'PUBLIC')
              ORDER BY 1
              """;
    }

    private static string MssqlYonSorgusu(string tamAd, bool kullananlar)
    {
        string guvenli = tamAd.Replace("'", "''");
        return kullananlar
            ? $"""
              SELECT DISTINCT
                  OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id) AS [Nesne],
                  o.type_desc AS [Tür]
              FROM sys.sql_expression_dependencies d
              JOIN sys.objects o ON o.object_id = d.referencing_id
              WHERE d.referenced_id = OBJECT_ID(N'{guvenli}')
              ORDER BY [Nesne];
              """
            : $"""
              SELECT DISTINCT
                  ISNULL(d.referenced_schema_name + N'.', N'') + d.referenced_entity_name AS [Nesne],
                  ISNULL(o.type_desc, N'?') AS [Tür]
              FROM sys.sql_expression_dependencies d
              LEFT JOIN sys.objects o ON o.object_id = d.referenced_id
              WHERE d.referencing_id = OBJECT_ID(N'{guvenli}')
              ORDER BY [Nesne];
              """;
    }

    /// <summary>
    /// PostgreSQL yönü: view/materialized view bağımlılıkları pg_depend→pg_rewrite zinciriyle KESİN;
    /// fonksiyonlar gövde metni eşleşmesiyle YAKLAŞIK (yalnız ⬅ yönünde; Tür "FUNCTION ~" yazar).
    /// to_regclass bilinmeyen adda NULL döner → sorgu hatasız BOŞ gelir (ağaçta "(bağımlılık yok)").
    /// </summary>
    private static string PgYonSorgusu(string tamAd, bool kullananlar)
    {
        string guvenli = tamAd.Replace("'", "''");
        // Fonksiyon gövdesi taraması için yalın nesne adı (şemasız) aranır.
        string ad = (tamAd.Contains('.') ? tamAd[(tamAd.LastIndexOf('.') + 1)..] : tamAd).Replace("'", "''");
        return kullananlar
            ? $"""
              SELECT DISTINCT n.nspname || '.' || c.relname AS "Nesne",
                     CASE c.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW'
                                    ELSE c.relkind::text END AS "Tür"
              FROM pg_depend d
              JOIN pg_rewrite r ON r.oid = d.objid
              JOIN pg_class c ON c.oid = r.ev_class
              JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE d.refobjid = to_regclass('{guvenli}')
                AND d.deptype IN ('n', 'a')
                AND c.oid <> to_regclass('{guvenli}')
              UNION
              SELECT DISTINCT pn.nspname || '.' || p.proname,
                     'FUNCTION ~ (gövde metninde geçiyor)'
              FROM pg_proc p
              JOIN pg_namespace pn ON pn.oid = p.pronamespace
              WHERE pn.nspname NOT IN ('pg_catalog', 'information_schema')
                AND p.prosrc ILIKE '%{ad}%'
              ORDER BY 1;
              """
            : $"""
              SELECT DISTINCT n.nspname || '.' || c.relname AS "Nesne",
                     CASE c.relkind WHEN 'r' THEN 'TABLE' WHEN 'v' THEN 'VIEW'
                                    WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'f' THEN 'FOREIGN TABLE'
                                    WHEN 'p' THEN 'PARTITIONED TABLE' ELSE c.relkind::text END AS "Tür"
              FROM pg_depend d
              JOIN pg_rewrite r ON r.oid = d.objid AND r.ev_class = to_regclass('{guvenli}')
              JOIN pg_class c ON c.oid = d.refobjid
              JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE d.deptype IN ('n', 'a')
                AND c.oid <> to_regclass('{guvenli}')
                AND c.relkind IN ('r', 'v', 'm', 'f', 'p')
              ORDER BY 1;
              """;
    }

    /// <summary>
    /// MySQL yönü (2026-08-03 devamı): view bağımlılıkları information_schema.VIEW_TABLE_USAGE'dan
    /// KESİN (MySQL 8.0.13+ — MariaDB'de bu tablo yoktur, düğümde hata görünür; script başı yorumu
    /// söyler); rutinler ROUTINE_DEFINITION metin eşleşmesiyle YAKLAŞIK (yalnız ⬅ yönünde).
    /// MySQL'de şema = veritabanı → yalın ad DATABASE() kapsamında aranır.
    /// </summary>
    private static string MySqlYonSorgusu(string tamAd, bool kullananlar)
    {
        // MySQL'de nesne adı şemasız gelir; "db.ad" yazıldıysa son parça alınır.
        string ad = (tamAd.Contains('.') ? tamAd[(tamAd.LastIndexOf('.') + 1)..] : tamAd).Replace("'", "''");
        return kullananlar
            ? $"""
              SELECT DISTINCT CONCAT(VIEW_SCHEMA, '.', VIEW_NAME) AS `Nesne`, 'VIEW' AS `Tür`
              FROM information_schema.VIEW_TABLE_USAGE
              WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{ad}'
              UNION
              SELECT DISTINCT CONCAT(ROUTINE_SCHEMA, '.', ROUTINE_NAME),
                     CONCAT(ROUTINE_TYPE, ' ~ (gövde metninde geçiyor)')
              FROM information_schema.ROUTINES
              WHERE ROUTINE_SCHEMA = DATABASE() AND ROUTINE_DEFINITION LIKE '%{ad}%'
              ORDER BY 1;
              """
            : $"""
              SELECT DISTINCT CONCAT(TABLE_SCHEMA, '.', TABLE_NAME) AS `Nesne`, 'TABLE/VIEW' AS `Tür`
              FROM information_schema.VIEW_TABLE_USAGE
              WHERE VIEW_SCHEMA = DATABASE() AND VIEW_NAME = '{ad}'
              ORDER BY 1;
              """;
    }

    /// <summary>ALTER etki analizi script'i: ⬅ bu nesneyi kullananlar + ➡ bu nesnenin kullandıkları.</summary>
    public static string ScriptUret(SemaNesnesi nesne)
        => ScriptUret($"{nesne.Sema}.{nesne.Ad}", nesne.Tur.ToString());

    /// <summary>Ağaç düğümünden script: elimizde yalnız "şema.ad" metni var (2026-08-03 ağaç görünümü).</summary>
    public static string ScriptUret(string sahipliAd, string tur, string motorId = "mssql")
    {
        if (motorId == "postgres")
        {
            return $"""
                -- 🔗 Bağımlılık Gezgini · nesne: {sahipliAd} ({tur}) · PostgreSQL
                -- ⬅ kullananlar: view'lar KESİN (pg_depend); fonksiyonlar gövde metni eşleşmesi (~ YAKLAŞIK)
                -- ➡ kullandıkları: bu view'ın başvurduğu tablolar/view'lar (tabloda boş — FK'lar Kayıt Haritası'nda)

                -- ⬅ Bunu KULLANANLAR
                {YonSorgusu(sahipliAd, kullananlar: true, motorId)}

                -- ➡ Bunun KULLANDIKLARI
                {YonSorgusu(sahipliAd, kullananlar: false, motorId)}
                """;
        }

        if (motorId == "oracle")
        {
            return $"""
                -- 🔗 Bağımlılık Gezgini · nesne: {sahipliAd} ({tur}) · Oracle
                -- İki yön de ALL_DEPENDENCIES'ten KESİNDİR (SP/fonksiyon/paket/view/trigger dahil).

                -- ⬅ Bunu KULLANANLAR
                {YonSorgusu(sahipliAd, kullananlar: true, motorId)};

                -- ➡ Bunun KULLANDIKLARI
                {YonSorgusu(sahipliAd, kullananlar: false, motorId)};
                """;
        }

        if (motorId == "mysql")
        {
            return $"""
                -- 🔗 Bağımlılık Gezgini · nesne: {sahipliAd} ({tur}) · MySQL
                -- ⬅ kullananlar: view'lar KESİN (VIEW_TABLE_USAGE — MySQL 8.0.13+ gerektirir, MariaDB'de yok);
                --    rutinler gövde metni eşleşmesi (~ YAKLAŞIK)
                -- ➡ kullandıkları: bu view'ın başvurduğu tablolar/view'lar (tabloda boş — FK'lar Kayıt Haritası'nda)

                -- ⬅ Bunu KULLANANLAR
                {YonSorgusu(sahipliAd, kullananlar: true, motorId)}

                -- ➡ Bunun KULLANDIKLARI
                {YonSorgusu(sahipliAd, kullananlar: false, motorId)}
                """;
        }

        string tamAd = sahipliAd.Replace("'", "''");
        return $"""
            -- 🔗 Bağımlılık Gezgini · nesne: {sahipliAd} ({tur})
            -- ⬅ kullananlar: bu nesneye başvuran SP/view/fonksiyonlar (ALTER'dan kim etkilenir?)
            -- ➡ kullandıkları: bu nesnenin gövdesinin başvurduğu nesneler (neye bağımlı?)

            -- ⬅ Bunu KULLANANLAR
            SELECT DISTINCT
                N'⬅ kullanan' AS [Yön],
                OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id) AS [Nesne],
                o.type_desc AS [Tür]
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects o ON o.object_id = d.referencing_id
            WHERE d.referenced_id = OBJECT_ID(N'{tamAd}')
            ORDER BY [Nesne];

            -- ➡ Bunun KULLANDIKLARI
            SELECT DISTINCT
                N'➡ kullanılan' AS [Yön],
                ISNULL(d.referenced_schema_name + N'.', N'') + d.referenced_entity_name AS [Nesne],
                ISNULL(o.type_desc, N'?') AS [Tür]
            FROM sys.sql_expression_dependencies d
            LEFT JOIN sys.objects o ON o.object_id = d.referenced_id
            WHERE d.referencing_id = OBJECT_ID(N'{tamAd}')
            ORDER BY [Nesne];
            """;
    }
}
