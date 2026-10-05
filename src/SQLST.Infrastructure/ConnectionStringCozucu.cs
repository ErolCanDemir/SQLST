using System.Data.Common;
using Microsoft.Data.SqlClient;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Connection string'den profil alanları (V2-S10, FG-3.14 → V4-S3'te motor farkındalığı).
/// Kullanıcı app.config/appsettings'ten kopyaladığı dizeyi yapıştırır, form dolar.
///
/// V4-S4: beş motorun biçimlerini anlar ve <b>motoru dizeden tanır</b> (URI şeması ya da
/// motora özgü anahtarlar). Tanıyamazsa formda seçili motoru varsayar ve bunu NOT olarak
/// söyler — sessizce yanlış motor seçmez.
///
/// <b>Güvenlik kuralı:</b> URI biçimindeki kimlik bilgisi (<c>user:pass@host</c>) Sunucu
/// alanına GÖMÜLMEZ; ayrıştırılıp kullanıcı adı/parola alanlarına konur. Aksi hâlde parola
/// <c>profiles.json</c>'a düz metin olarak yazılırdı — parola daima DPAPI yolundan geçer.
/// Parola bu sınıftan düz metin DÖNER, kaydetme aşamasında şifrelenir; burada saklanmaz.
/// </summary>
public static class ConnectionStringCozucu
{
    public sealed record Cozum(
        MotorTuru Motor,
        string Sunucu,
        KimlikTuru Kimlik,
        string? KullaniciAdi,
        string? Parola,
        int? BaglantiTimeoutSn,
        string OnerilenAd,
        IReadOnlyList<string> Notlar);

    /// <param name="varsayilanMotor">
    /// Dizeden motor kesin anlaşılamazsa varsayılacak motor — formda seçili olan.
    /// </param>
    public static (Cozum? Sonuc, string? Hata) Coz(string connectionString, MotorTuru varsayilanMotor = MotorTuru.Mssql)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return (null, "Connection string boş.");

        string cs = connectionString.Trim();

        // 1) URI biçimleri kesin konuşur: şema motoru belirler
        if (UriMotoru(cs) is { } uriMotor)
            return UridenCoz(cs, uriMotor);

        // 2) Oracle TNS tanımlayıcısı: (DESCRIPTION=(ADDRESS=...))
        if (cs.StartsWith("(", StringComparison.Ordinal))
        {
            return (new Cozum(MotorTuru.Oracle, cs, KimlikTuru.Sql, null, null, null,
                OnerilenAd: "Oracle (TNS)",
                Notlar: ["TNS tanımlayıcısı sunucu alanına olduğu gibi alındı; kullanıcı adı ve parolayı elle girin."]), null);
        }

        // 3) anahtar=değer biçimi
        if (cs.Contains('=', StringComparison.Ordinal))
            return AnahtarDegerdenCoz(cs, varsayilanMotor);

        // 4) Çıplak metin: Oracle Easy Connect (host:port/servis) ya da düz sunucu adı
        if (cs.Contains('/', StringComparison.Ordinal))
        {
            return (new Cozum(MotorTuru.Oracle, cs, KimlikTuru.Sql, null, null, null,
                OnerilenAd: cs,
                Notlar: ["Oracle Easy Connect biçimi tanındı; kullanıcı adı ve parolayı elle girin."]), null);
        }

        return (new Cozum(varsayilanMotor, cs, VarsayilanKimlik(varsayilanMotor), null, null, null,
            OnerilenAd: cs,
            Notlar: [$"Dize yalnız sunucu adı içeriyor — motor olarak {MotorAdi(varsayilanMotor)} varsayıldı."]), null);
    }

    // ── URI yolu ────────────────────────────────────────────────────────────

    private static MotorTuru? UriMotoru(string cs)
    {
        int iki = cs.IndexOf("://", StringComparison.Ordinal);
        if (iki <= 0)
            return null;

        return cs[..iki].ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => MotorTuru.Postgres,
            "mysql" or "mariadb" => MotorTuru.MySql,
            "mongodb" or "mongodb+srv" => MotorTuru.Mongo,
            "oracle" => MotorTuru.Oracle,
            "sqlserver" or "mssql" => MotorTuru.Mssql,
            _ => null,
        };
    }

    private static (Cozum?, string?) UridenCoz(string cs, MotorTuru motor)
    {
        // mongodb+srv gibi şemalar Uri için de geçerlidir; yine de dayanıklı olalım
        if (!Uri.TryCreate(cs, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host))
            return (null, "URI çözümlenemedi — host bulunamadı.");

        var notlar = new List<string>();

        string? kullanici = null, parola = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            string[] parca = uri.UserInfo.Split(':', 2);
            kullanici = Uri.UnescapeDataString(parca[0]);
            if (parca.Length == 2)
            {
                parola = Uri.UnescapeDataString(parca[1]);
                // Parola dizede vardı → alana taşındı; Sunucu'ya GÖMÜLMEDİ (DPAPI yolu korunur)
                notlar.Add("Parola dizeden alındı ve parola alanına konuldu — kaydedince DPAPI ile şifrelenir.");
            }
        }
        if (string.IsNullOrWhiteSpace(kullanici))
            notlar.Add("Kullanıcı adı dizede yok — elle girin.");
        else if (parola is null)
            notlar.Add("Parola dizede yok — kaydetmeden önce girin.");

        // mongodb+srv'de port YOKTUR (SRV kaydı çözer) — eklemek bağlantıyı bozar
        bool srv = cs.StartsWith("mongodb+srv", StringComparison.OrdinalIgnoreCase);
        string sunucu = uri.Port > 0 && !uri.IsDefaultPort && !srv
            ? $"{uri.Host}:{uri.Port}"
            : uri.Host;
        if (srv)
            sunucu = $"mongodb+srv://{uri.Host}";   // SRV yalnız tam URI olarak anlamlıdır

        string? db = uri.AbsolutePath.Trim('/');
        if (!string.IsNullOrWhiteSpace(db))
            notlar.Add(VeritabaniNotu(db));

        return (new Cozum(
            Motor: motor,
            Sunucu: sunucu,
            Kimlik: KimlikTuru.Sql,
            KullaniciAdi: kullanici,
            Parola: parola,
            BaglantiTimeoutSn: null,
            OnerilenAd: string.IsNullOrWhiteSpace(db) ? uri.Host : $"{uri.Host} ({db})",
            Notlar: notlar), null);
    }

    // ── anahtar=değer yolu ──────────────────────────────────────────────────

    private static (Cozum?, string?) AnahtarDegerdenCoz(string cs, MotorTuru varsayilan)
    {
        DbConnectionStringBuilder b;
        try
        {
            // Doğrulamasız genel ayrıştırıcı: her motorun anahtarını kabul eder
            b = new DbConnectionStringBuilder { ConnectionString = cs };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return (null, $"Çözümlenemedi: {ex.Message}");
        }

        (MotorTuru motor, bool kesin) = MotorTani(b, varsayilan);

        // SQL Server'da eski yol korunur: SqlConnectionStringBuilder eş anlamlıları
        // (Addr, Network Address, Trusted_Connection…) ve tip dönüşümlerini bilir.
        if (motor == MotorTuru.Mssql)
            return MssqldenCoz(cs, kesin, varsayilan);

        var notlar = new List<string>();
        if (!kesin)
            notlar.Add($"Motor dizeden kesin anlaşılamadı — {MotorAdi(motor)} varsayıldı; yanlışsa motoru elle değiştirin.");

        string host = Deger(b, "host", "server", "data source", "datasource", "addr", "address") ?? "";
        if (string.IsNullOrWhiteSpace(host))
            return (null, "Sunucu (Host/Server/Data Source) bulunamadı.");

        string? port = Deger(b, "port");
        // Oracle'da Data Source zaten host:port/servis taşır — port ayrıca eklenmez
        string sunucu = port is { Length: > 0 } && motor != MotorTuru.Oracle && !host.Contains(':')
            ? $"{host}:{port}"
            : host;

        string? kullanici = Deger(b, "username", "user id", "userid", "uid", "user");
        string? parola = Deger(b, "password", "pwd");

        if (string.IsNullOrWhiteSpace(kullanici))
            notlar.Add("Kullanıcı adı yok — elle girin.");
        if (string.IsNullOrEmpty(parola))
            notlar.Add("Parola dizede yok — kaydetmeden önce girin.");

        string? db = Deger(b, "database", "initial catalog", "dbname");
        if (!string.IsNullOrWhiteSpace(db))
            notlar.Add(VeritabaniNotu(db));

        int? zamanAsimi = null;
        if (Deger(b, "connection timeout", "connect timeout", "timeout") is { } sn
            && int.TryParse(sn, out int saniye) && saniye > 0)
            zamanAsimi = saniye;

        return (new Cozum(
            Motor: motor,
            Sunucu: sunucu,
            Kimlik: KimlikTuru.Sql,
            KullaniciAdi: string.IsNullOrWhiteSpace(kullanici) ? null : kullanici,
            Parola: string.IsNullOrEmpty(parola) ? null : parola,
            BaglantiTimeoutSn: zamanAsimi,
            OnerilenAd: string.IsNullOrWhiteSpace(db) ? sunucu : $"{sunucu} ({db})",
            Notlar: notlar), null);
    }

    private static (Cozum?, string?) MssqldenCoz(string cs, bool kesin, MotorTuru varsayilan)
    {
        SqlConnectionStringBuilder b;
        try
        {
            b = new SqlConnectionStringBuilder(cs);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return (null, $"Çözümlenemedi: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(b.DataSource))
            return (null, "Sunucu (Server/Data Source) bulunamadı.");

        var notlar = new List<string>();
        if (!kesin && varsayilan != MotorTuru.Mssql)
            notlar.Add("Motor dizeden kesin anlaşılamadı — SQL Server varsayıldı; yanlışsa motoru elle değiştirin.");

        KimlikTuru kimlik = b.IntegratedSecurity ? KimlikTuru.Windows : KimlikTuru.Sql;
        if (kimlik == KimlikTuru.Sql && string.IsNullOrWhiteSpace(b.UserID))
            notlar.Add("Kullanıcı adı yok — SQL kimliği için elle girin.");
        if (kimlik == KimlikTuru.Sql && string.IsNullOrEmpty(b.Password))
            notlar.Add("Parola dizede yok — kaydetmeden önce girin.");
        if (!string.IsNullOrWhiteSpace(b.InitialCatalog))
            notlar.Add(VeritabaniNotu(b.InitialCatalog));
        if (b.AttachDBFilename.Length > 0)
            notlar.Add("AttachDbFilename desteklenmez, yok sayıldı.");

        string ad = b.DataSource;
        if (!string.IsNullOrWhiteSpace(b.InitialCatalog))
            ad += $" ({b.InitialCatalog})";

        return (new Cozum(
            Motor: MotorTuru.Mssql,
            Sunucu: b.DataSource,
            Kimlik: kimlik,
            KullaniciAdi: kimlik == KimlikTuru.Sql && b.UserID.Length > 0 ? b.UserID : null,
            Parola: kimlik == KimlikTuru.Sql && b.Password.Length > 0 ? b.Password : null,
            BaglantiTimeoutSn: b.ShouldSerialize("Connect Timeout") ? b.ConnectTimeout : null,
            OnerilenAd: ad,
            Notlar: notlar), null);
    }

    /// <summary>
    /// Anahtar kümesinden motoru tanır. Dönen ikinci değer KESİN mi — belirsizse çağıran
    /// kullanıcıya "varsayıldı" notu düşer. `Server=host;Uid=u;Pwd=p` gibi diziler SQL Server
    /// ile MySQL arasında gerçekten ayırt edilemez; orada formdaki seçim kazanır.
    /// </summary>
    private static (MotorTuru Motor, bool Kesin) MotorTani(DbConnectionStringBuilder b, MotorTuru varsayilan)
    {
        if (Var(b, "integrated security", "trusted_connection", "initial catalog",
                   "trustservercertificate", "attachdbfilename", "multipleactiveresultsets",
                   "applicationintent", "multisubnetfailover"))
            return (MotorTuru.Mssql, true);

        if (Var(b, "allowpublickeyretrieval", "allowuservariables", "treattinyasboolean",
                   "oldguids", "sslmode") && Var(b, "server", "host"))
        {
            // sslmode PG'de de var → Host varsa PostgreSQL, Server varsa MySQL
            return Var(b, "host") ? (MotorTuru.Postgres, true) : (MotorTuru.MySql, true);
        }

        if (Var(b, "host", "username", "search path", "no reset on close", "include error detail"))
            return (MotorTuru.Postgres, true);

        if (Var(b, "dba privilege", "tns admin", "connection lifetime", "self tuning"))
            return (MotorTuru.Oracle, true);

        // Data Source host:port/servis biçimindeyse Oracle Easy Connect'tir
        if (Deger(b, "data source", "datasource") is { } ds && ds.Contains('/') && !ds.StartsWith("\\\\"))
            return (MotorTuru.Oracle, true);

        // Ayırt edilemedi: formdaki seçim kazanır (Mssql varsayılan)
        return (varsayilan, false);
    }

    // ── küçük yardımcılar ───────────────────────────────────────────────────

    private static bool Var(DbConnectionStringBuilder b, params string[] anahtarlar)
        => anahtarlar.Any(a => b.ContainsKey(a));

    private static string? Deger(DbConnectionStringBuilder b, params string[] anahtarlar)
    {
        foreach (string a in anahtarlar)
        {
            if (b.TryGetValue(a, out object? v) && v?.ToString() is { Length: > 0 } s)
                return s;
        }
        return null;
    }

    private static KimlikTuru VarsayilanKimlik(MotorTuru motor)
        => motor == MotorTuru.Mssql ? KimlikTuru.Windows : KimlikTuru.Sql;

    private static string VeritabaniNotu(string db)
        => $"Veritabanı ({db}) profile alınmaz — bağlantı sunucuyadır (FG-1.1), veritabanı sekmeden seçilir.";

    private static string MotorAdi(MotorTuru motor) => motor switch
    {
        MotorTuru.Mssql => "SQL Server",
        MotorTuru.Postgres => "PostgreSQL",
        MotorTuru.MySql => "MySQL/MariaDB",
        MotorTuru.Oracle => "Oracle",
        MotorTuru.Mongo => "MongoDB",
        _ => motor.ToString(),
    };
}
