using System.Globalization;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Sunucu özeti: kimlik modu, sürüm, izinler, kendi login'i, erişilebilir veritabanları.</summary>
public sealed record GuvenlikOzet(
    bool KarmaKimlik, string Surum, string Edition, int EngineEdition, bool TumunuGorur, bool LoginDegistirir,
    bool Sysadmin, string KendiLogin, IReadOnlyList<string> Veritabanlari)
{
    public bool Azure => EngineEdition == 5;
}

/// <summary>sys.server_principals satırı (+ sql_logins ayrıntısı, roller, oturum sayısı).</summary>
public sealed record Login(
    string Ad, string Tur, bool Kapali, bool Kilitli, bool SuresiDolmus, bool PolitikaAcik, DateTime? ParolaDegisti,
    string VarsayilanDb, IReadOnlyList<string> SunucuRolleri, int OturumSayisi, DateTime? Olusturma, string Sid)
{
    public bool SqlLogin => Tur == "SQL";
    public bool Sorunlu => Kilitli || SuresiDolmus;
}

/// <summary>Bir veritabanındaki kullanıcı (login eşlemesi SID ile).</summary>
public sealed record Kullanici(
    string Ad, string Tur, string Kimlik, string? BagliLogin, bool Yetim, IReadOnlyList<string> Roller, string Sema, string Sid, DateTime? Olusturma);

/// <summary>Login'in bir veritabanındaki kullanıcısı (DB eşlemeleri tablosu).</summary>
public sealed record LoginEslemesi(string Veritabani, string Kullanici, IReadOnlyList<string> Roller);

/// <summary>Açık yetki satırı (sunucu ya da veritabanı).</summary>
public sealed record Yetki(string Sinif, string Nesne, string Izin, string Durum, string Veren)
{
    public bool Deny => Durum.StartsWith("DENY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 🔐 Login / kullanıcı / yetki SAF çekirdeği (v23-S29 S1 — docs/15-guvenlik-arastirma.md). Sorgu metinleri (özet, login'ler,
/// oturum sayıları, sunucu yetkileri, DB eşlemeleri, kullanıcılar, DB yetkileri, roller), DDL (aç/kapat, rol ekle-çıkar,
/// kullanıcı oluştur, yetim bağla), ayrıştırma, tehlike sınıflaması ve hata çevirisi. WPF yalnız gösterir ve onaylatır.
/// </summary>
public static class GuvenlikSorgulari
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Sabit sunucu rolleri (SSMS sırası); özel roller sorgudan eklenir.</summary>
    public static readonly string[] SabitSunucuRolleri =
        ["sysadmin", "securityadmin", "serveradmin", "setupadmin", "processadmin", "diskadmin", "dbcreator", "bulkadmin"];

    /// <summary>Kırmızı onay isteyen roller (K5).</summary>
    public static bool TehlikeliRol(string rol) => rol is "sysadmin" or "securityadmin" or "db_owner";

    /// <summary>Sistem kullanıcıları — listede gösterilmez.</summary>
    private const string SistemKullanicilari = "N'guest', N'INFORMATION_SCHEMA', N'sys'";

    // ---- sorgular ----

    public static string OzetSql() => """
        SELECT CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS int) AS yalniz_windows,
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) AS surum,
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition,
               CAST(SERVERPROPERTY('EngineEdition') AS int) AS engine_edition,
               ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DEFINITION'), 0) AS tumunu_gorur,
               ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY LOGIN'), 0) AS login_degistirir,
               ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) AS sysadmin,
               SUSER_SNAME() AS kendi_login,
               STUFF((SELECT N'|' + name FROM sys.databases WHERE state_desc = N'ONLINE' AND HAS_DBACCESS(name) = 1
                      ORDER BY name FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, N'') AS veritabanlari;
        """;

    /// <summary>Login'ler: SQL/Windows/grup + sql_logins ayrıntısı + sunucu rolleri (FOR XML — 2012+ uyumlu).</summary>
    public static string LoginlerSql() => """
        SELECT p.name, p.type, p.is_disabled, p.default_database_name, p.create_date, CONVERT(varchar(85), p.sid, 1) AS sid,
               ISNULL(l.is_policy_checked, 0) AS politika,
               CASE WHEN l.name IS NULL THEN 0 ELSE ISNULL(CAST(LOGINPROPERTY(p.name, 'IsLocked') AS int), 0) END AS kilitli,
               CASE WHEN l.name IS NULL THEN 0 ELSE ISNULL(CAST(LOGINPROPERTY(p.name, 'IsExpired') AS int), 0) END AS suresi_dolmus,
               CASE WHEN l.name IS NULL THEN NULL ELSE CAST(LOGINPROPERTY(p.name, 'PasswordLastSetTime') AS datetime) END AS parola_degisti,
               STUFF((SELECT N'|' + r.name FROM sys.server_role_members rm JOIN sys.server_principals r ON r.principal_id = rm.role_principal_id
                      WHERE rm.member_principal_id = p.principal_id ORDER BY r.name FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, N'') AS roller
        FROM sys.server_principals p
        LEFT JOIN sys.sql_logins l ON l.principal_id = p.principal_id
        WHERE p.type IN ('S', 'U', 'G') AND p.name NOT LIKE N'##%'
        ORDER BY p.name;
        """;

    /// <summary>Login başına açık oturum (VIEW SERVER STATE ister — hata olursa sayılar boş kalır, K9).</summary>
    public static string OturumSayilariSql() => """
        SELECT login_name, COUNT(*) AS oturum FROM sys.dm_exec_sessions WHERE is_user_process = 1 GROUP BY login_name;
        """;

    public static string SunucuRolleriSql() => "SELECT name FROM sys.server_principals WHERE type = 'R' ORDER BY name;";

    public static string SunucuYetkileriSql(string login) => $"""
        SELECT pe.class_desc AS sinif, N'' AS nesne, pe.permission_name AS izin, pe.state_desc AS durum, ISNULL(g.name, N'') AS veren
        FROM sys.server_permissions pe
        JOIN sys.server_principals pr ON pr.principal_id = pe.grantee_principal_id
        LEFT JOIN sys.server_principals g ON g.principal_id = pe.grantor_principal_id
        WHERE pr.name = {YedekSorgulari.Literal(login)}
        ORDER BY pe.permission_name;
        """;

    /// <summary>Tüm erişilebilir veritabanlarındaki kullanıcılar (SID + roller) — login eşlemesi bellekte SID ile kurulur.</summary>
    public static string EslemeSql(IReadOnlyList<string> veritabanlari)
    {
        if (veritabanlari.Count == 0)
            return "SELECT TOP (0) N'' AS veritabani, N'' AS kullanici, N'' AS sid, N'' AS roller;";
        var sb = new StringBuilder();
        for (int i = 0; i < veritabanlari.Count; i++)
        {
            string db = YedekSorgulari.Koseli(veritabanlari[i]);
            if (i > 0) sb.Append("UNION ALL\n");
            sb.Append($"""
                SELECT {YedekSorgulari.Literal(veritabanlari[i])} AS veritabani, dp.name AS kullanici, CONVERT(varchar(85), dp.sid, 1) AS sid,
                       STUFF((SELECT N'|' + r.name FROM {db}.sys.database_role_members rm JOIN {db}.sys.database_principals r ON r.principal_id = rm.role_principal_id
                              WHERE rm.member_principal_id = dp.principal_id ORDER BY r.name FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, N'') AS roller
                FROM {db}.sys.database_principals dp
                WHERE dp.type IN ('S', 'U', 'G', 'E', 'X') AND dp.sid IS NOT NULL AND dp.name NOT IN ({SistemKullanicilari})

                """);
        }
        sb.Append("ORDER BY veritabani, kullanici;");
        return sb.ToString();
    }

    /// <summary>Seçili veritabanının kullanıcıları (DB bağlamında): tür, kimlik, bağlı login (SID), yetim, roller, şema.</summary>
    public static string KullanicilarSql() => $"""
        SELECT dp.name, dp.type, dp.authentication_type_desc AS kimlik, sp.name AS login, dp.default_schema_name AS sema,
               dp.create_date, CONVERT(varchar(85), dp.sid, 1) AS sid,
               -- yetim = login'e bağlıydı (INSTANCE/WINDOWS) ama login yok (geri yükleme/taşıma); NONE = WITHOUT LOGIN ile
               -- bilerek login'siz — ALTER USER … WITH LOGIN onu bağlayamaz (33016, LocalDB'de doğrulandı)
               CASE WHEN sp.sid IS NULL AND dp.type IN ('S', 'U', 'G') AND dp.authentication_type_desc IN (N'INSTANCE', N'WINDOWS') AND dp.name <> N'dbo' THEN 1 ELSE 0 END AS yetim,
               STUFF((SELECT N'|' + r.name FROM sys.database_role_members rm JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
                      WHERE rm.member_principal_id = dp.principal_id ORDER BY r.name FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, N'') AS roller
        FROM sys.database_principals dp
        LEFT JOIN sys.server_principals sp ON sp.sid = dp.sid
        WHERE dp.type IN ('S', 'U', 'G', 'E', 'X') AND dp.name NOT IN ({SistemKullanicilari})
        ORDER BY dp.name;
        """;

    public static string DbRolleriSql() => "SELECT name FROM sys.database_principals WHERE type = 'R' ORDER BY CASE WHEN is_fixed_role = 1 THEN 0 ELSE 1 END, name;";

    /// <summary>Kullanıcının açık yetkileri (DB bağlamında): sınıf · nesne/şema adı · izin · durum · veren.</summary>
    public static string DbYetkileriSql(string kullanici) => $"""
        SELECT pe.class_desc AS sinif,
               CASE pe.class_desc
                    WHEN N'OBJECT_OR_COLUMN' THEN ISNULL(OBJECT_SCHEMA_NAME(pe.major_id) + N'.' + OBJECT_NAME(pe.major_id), N'')
                         + CASE WHEN pe.minor_id > 0 THEN N' (' + ISNULL((SELECT name FROM sys.columns WHERE object_id = pe.major_id AND column_id = pe.minor_id), N'?') + N')' ELSE N'' END
                    WHEN N'SCHEMA' THEN ISNULL(SCHEMA_NAME(pe.major_id), N'')
                    WHEN N'DATABASE' THEN DB_NAME()
                    ELSE ISNULL(CAST(pe.major_id AS nvarchar(20)), N'') END AS nesne,
               pe.permission_name AS izin, pe.state_desc AS durum, ISNULL(g.name, N'') AS veren
        FROM sys.database_permissions pe
        JOIN sys.database_principals pr ON pr.principal_id = pe.grantee_principal_id
        LEFT JOIN sys.database_principals g ON g.principal_id = pe.grantor_principal_id
        WHERE pr.name = {YedekSorgulari.Literal(kullanici)}
        ORDER BY pe.class_desc, nesne, pe.permission_name;
        """;

    // ---- DDL ----

    public static string LoginAcKapatSql(string login, bool kapat)
        => $"ALTER LOGIN {YedekSorgulari.Koseli(login)} {(kapat ? "DISABLE" : "ENABLE")};";

    public static string SunucuRolSql(string rol, string login, bool ekle)
        => $"ALTER SERVER ROLE {YedekSorgulari.Koseli(rol)} {(ekle ? "ADD" : "DROP")} MEMBER {YedekSorgulari.Koseli(login)};";

    public static string DbRolSql(string rol, string kullanici, bool ekle)
        => $"ALTER ROLE {YedekSorgulari.Koseli(rol)} {(ekle ? "ADD" : "DROP")} MEMBER {YedekSorgulari.Koseli(kullanici)};";

    public static string KullaniciOlusturSql(string login, string? kullaniciAdi = null)
        => $"CREATE USER {YedekSorgulari.Koseli(kullaniciAdi ?? login)} FOR LOGIN {YedekSorgulari.Koseli(login)};";

    public static string YetimBaglaSql(string kullanici, string login)
        => $"ALTER USER {YedekSorgulari.Koseli(kullanici)} WITH LOGIN = {YedekSorgulari.Koseli(login)};";

    /// <summary>"Script olarak al": login'i yeniden kuran script (parola YAZILMAZ — K4), rolleri ve DB eşlemeleri.</summary>
    public static string LoginScripti(Login l, IReadOnlyList<LoginEslemesi> eslemeler, DateTime zaman, string sunucu)
    {
        var sb = new StringBuilder();
        string ad = YedekSorgulari.Koseli(l.Ad);
        sb.Append("USE [master];\n");
        if (l.SqlLogin)
            sb.Append($"-- Parola üründe tutulmaz/gösterilmez; <parola> yerine yenisini yazın (K4)\nCREATE LOGIN {ad} WITH PASSWORD = N'<parola>', DEFAULT_DATABASE = {YedekSorgulari.Koseli(l.VarsayilanDb)}, CHECK_POLICY = {(l.PolitikaAcik ? "ON" : "OFF")};\n");
        else
            sb.Append($"CREATE LOGIN {ad} FROM WINDOWS WITH DEFAULT_DATABASE = {YedekSorgulari.Koseli(l.VarsayilanDb)};\n");
        if (l.Kapali)
            sb.Append($"ALTER LOGIN {ad} DISABLE;\n");
        foreach (string rol in l.SunucuRolleri.Where(r => r != "public"))
            sb.Append(SunucuRolSql(rol, l.Ad, true)).Append('\n');
        foreach (LoginEslemesi e in eslemeler)
        {
            sb.Append($"USE {YedekSorgulari.Koseli(e.Veritabani)};\n");
            sb.Append(KullaniciOlusturSql(l.Ad, e.Kullanici)).Append('\n');
            foreach (string rol in e.Roller.Where(r => r != "public"))
                sb.Append(DbRolSql(rol, e.Kullanici, true)).Append('\n');
        }
        sb.Append("-- SQLST ").Append(zaman.ToString("dd.MM.yyyy HH:mm", Inv)).Append(" · ").Append(sunucu);
        return sb.ToString();
    }

    /// <summary>Kullanıcıyı yeniden kuran script: CREATE USER (login'li/login'siz) + roller + açık yetkiler.</summary>
    public static string KullaniciScripti(Kullanici k, string veritabani, IReadOnlyList<Yetki> yetkiler, DateTime zaman, string sunucu)
    {
        var sb = new StringBuilder();
        string ad = YedekSorgulari.Koseli(k.Ad);
        sb.Append($"USE {YedekSorgulari.Koseli(veritabani)};\n");
        sb.Append(k.BagliLogin is { } l
            ? $"CREATE USER {ad} FOR LOGIN {YedekSorgulari.Koseli(l)} WITH DEFAULT_SCHEMA = {YedekSorgulari.Koseli(k.Sema.Length > 0 ? k.Sema : "dbo")};\n"
            : $"CREATE USER {ad} WITHOUT LOGIN WITH DEFAULT_SCHEMA = {YedekSorgulari.Koseli(k.Sema.Length > 0 ? k.Sema : "dbo")};\n");
        foreach (string rol in k.Roller.Where(r => r != "public"))
            sb.Append(DbRolSql(rol, k.Ad, true)).Append('\n');
        foreach (Yetki y in yetkiler)
        {
            string fiil = y.Durum.StartsWith("DENY") ? "DENY" : "GRANT";
            string hedef = y.Sinif switch
            {
                "OBJECT_OR_COLUMN" => " ON OBJECT::" + NesneKoseli(y.Nesne),
                "SCHEMA" => " ON SCHEMA::" + YedekSorgulari.Koseli(y.Nesne),
                _ => "",
            };
            sb.Append($"{fiil} {y.Izin}{hedef} TO {ad}{(y.Durum == "GRANT_WITH_GRANT_OPTION" ? " WITH GRANT OPTION" : "")};\n");
        }
        sb.Append("-- SQLST ").Append(zaman.ToString("dd.MM.yyyy HH:mm", Inv)).Append(" · ").Append(sunucu);
        return sb.ToString();
    }

    private static string NesneKoseli(string nesne)
    {
        // "dbo.T (Kolon)" → [dbo].[T] (Kolon)
        string kolon = "";
        int p = nesne.IndexOf(" (", StringComparison.Ordinal);
        if (p > 0) { kolon = nesne[p..]; nesne = nesne[..p]; }
        return string.Join('.', nesne.Split('.').Select(YedekSorgulari.Koseli)) + kolon;
    }

    // ---- okuma ----

    public static GuvenlikOzet? OzetOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { Satirlar.Count: > 0 } rs)
            return null;
        YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, rs.Satirlar[0]);
        return new GuvenlikOzet(!s.Bool("yalniz_windows"), s.Metin("surum"), s.Metin("edition"), s.Int("engine_edition"),
            s.Bool("tumunu_gorur"), s.Bool("login_degistirir"), s.Bool("sysadmin"), s.Metin("kendi_login"),
            [.. s.Metin("veritabanlari").Split('|', StringSplitOptions.RemoveEmptyEntries)]);
    }

    public static string TurAdi(string tip) => tip switch
    {
        "S" => "SQL", "U" => "Windows", "G" => "Windows grup", "E" => "Azure AD kullanıcı", "X" => "Azure AD grup", _ => tip,
    };

    public static IReadOnlyList<Login> LoginleriOku(QueryResult sonuc, IReadOnlyDictionary<string, int>? oturumlar = null)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        List<Login> liste = [];
        foreach (object?[] satir in rs.Satirlar)
        {
            YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, satir);
            string ad = s.Metin("name");
            liste.Add(new Login(ad, TurAdi(s.Metin("type").Trim()), s.Bool("is_disabled"), s.Bool("kilitli"), s.Bool("suresi_dolmus"),
                s.Bool("politika"), s.Tarih("parola_degisti"), s.Metin("default_database_name"), Roller(s.Metin("roller")),
                oturumlar is not null && oturumlar.TryGetValue(ad, out int n) ? n : 0, s.Tarih("create_date"), s.Metin("sid")));
        }
        return liste;
    }

    public static IReadOnlyDictionary<string, int> OturumlariOku(QueryResult sonuc)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (sonuc.Hata is not null || sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return d;
        foreach (object?[] satir in rs.Satirlar)
        {
            YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, satir);
            d[s.Metin("login_name")] = s.Int("oturum");
        }
        return d;
    }

    public static IReadOnlyList<string> AdlariOku(QueryResult sonuc)
        => sonuc.ResultSetler.FirstOrDefault() is { } rs ? [.. rs.Satirlar.Select(r => YedekSorgulari.Satir(rs, r).Metin("name"))] : [];

    public static IReadOnlyList<Yetki> YetkileriOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        return [.. rs.Satirlar.Select(r => { YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, r);
            return new Yetki(s.Metin("sinif"), s.Metin("nesne"), s.Metin("izin"), s.Metin("durum"), s.Metin("veren")); })];
    }

    /// <summary>EslemeSql sonucu → login SID'ine göre eşlemeler (SID eşleşmeyen satırlar o DB'nin yetimleridir).</summary>
    public static IReadOnlyDictionary<string, List<LoginEslemesi>> EslemeleriOku(QueryResult sonuc)
    {
        var d = new Dictionary<string, List<LoginEslemesi>>(StringComparer.OrdinalIgnoreCase);
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return d;
        foreach (object?[] satir in rs.Satirlar)
        {
            YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, satir);
            string sid = s.Metin("sid");
            if (!d.TryGetValue(sid, out List<LoginEslemesi>? liste))
                d[sid] = liste = [];
            liste.Add(new LoginEslemesi(s.Metin("veritabani"), s.Metin("kullanici"), Roller(s.Metin("roller"))));
        }
        return d;
    }

    public static IReadOnlyList<Kullanici> KullanicilariOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        List<Kullanici> liste = [];
        foreach (object?[] satir in rs.Satirlar)
        {
            YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, satir);
            liste.Add(new Kullanici(s.Metin("name"), TurAdi(s.Metin("type").Trim()), s.Metin("kimlik"), s.MetinYaDaNull("login"),
                s.Bool("yetim"), Roller(s.Metin("roller")), s.Metin("sema"), s.Metin("sid"), s.Tarih("create_date")));
        }
        return liste;
    }

    private static IReadOnlyList<string> Roller(string metin)
        => [.. metin.Split('|', StringSplitOptions.RemoveEmptyEntries)];

    // ---- hata ----

    public static string HataMetni(SqlHata hata) => hata.Numara switch
    {
        15151 => "Bulunamadı ya da izniniz yok: " + hata.Mesaj,
        15434 => "Login'in açık oturumu var — önce oturumları kapatın (KILL) ya da login'i devre dışı bırakın.",
        15405 => "Özel kullanıcı 'dbo' için bu işlem yapılamaz.",
        15023 => "Bu adda kullanıcı/login zaten var.",
        15247 or 229 or 300 => "Bu işlem için izniniz yok (ALTER ANY LOGIN / ALTER ANY ROLE / rolün ALTER izni).",
        15063 => "Login zaten bu rolde değil / zaten üye.",
        15291 => "Bu login için kendi kendini sysadmin'den çıkaramazsınız.",
        15433 => "Bu login'in SID'si bu kullanıcıya eşlenemez (kaynağa bakın).",
        _ => hata.Mesaj,
    };
}
