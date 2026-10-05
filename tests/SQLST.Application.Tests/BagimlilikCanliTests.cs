using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 🔗 Bağımlılık sorgularının CANLI doğrulaması (roadmap borcu 2026-08-03). Ortam-kapılı
/// (BakimPaneliPostgresCanliTests deseni): sunucu portu kapalıysa ya da "SELECT 1" bile
/// koşamıyorsa (kimlik/DB hazır değil) test SESSİZCE atlanır — ortam kurulduğunda otomatik
/// koşup pg_depend / VIEW_TABLE_USAGE / ALL_DEPENDENCIES sorgularını gerçek sunucuda kanıtlar.
/// Parolalar ortam değişkeninden: SQLST_PG_PAROLA · SQLST_MYSQL_PAROLA · SQLST_ORACLE_PAROLA.
/// 2026-08-03 durumu: bu makinede PG/MySQL/Oracle YOK — üçü de atlanıyor (bilinçli).
/// </summary>
public class BagimlilikCanliTests
{
    private static readonly DpapiSecretProtector Koruyucu = new();

    private static bool PortAcik(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    private static ConnectionProfile Profil(MotorTuru motor, string sunucu, string kullanici, string parolaDegiskeni)
    {
        string? parola = Environment.GetEnvironmentVariable(parolaDegiskeni);
        return new ConnectionProfile
        {
            Ad = $"canli-{motor}", Motor = motor, Sunucu = sunucu, BaglantiTimeoutSn = 10,
            Kimlik = KimlikTuru.Sql, KullaniciAdi = kullanici,
            ParolaSifreli = string.IsNullOrEmpty(parola) ? null : Koruyucu.Sifrele(parola),
        };
    }

    /// <summary>"SELECT 1" bile koşmuyorsa ortam hazır değildir → null (test atlar).</summary>
    private static async Task<SqlExecutor?> HazirsaAsync(
        ILehce lehce, ConnectionProfile profil, string probe, ExecuteOptions opts)
    {
        var executor = new SqlExecutor(lehce);
        try
        {
            QueryResult r = await executor.ExecuteAsync(profil, probe, opts, CancellationToken.None);
            return r.Basarili ? executor : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Pg_bagimlilik_sorgulari_gercek_sunucuda()
    {
        if (!PortAcik("127.0.0.1", 5433))
            return;
        var lehce = new PostgresLehcesi(Koruyucu);
        ConnectionProfile profil = Profil(MotorTuru.Postgres, "127.0.0.1:5433", "postgres", "SQLST_PG_PAROLA");
        var opts = new ExecuteOptions { VeritabaniOverride = "sqlst_demo" };
        if (await HazirsaAsync(lehce, profil, "SELECT 1", opts) is not { } executor)
            return;

        await executor.ExecuteAsync(profil, """
            DROP VIEW IF EXISTS sqlst_canli_v;
            DROP TABLE IF EXISTS sqlst_canli_t;
            CREATE TABLE sqlst_canli_t (id int PRIMARY KEY, ad text);
            CREATE VIEW sqlst_canli_v AS SELECT id, ad FROM sqlst_canli_t;
            """, opts, CancellationToken.None);
        try
        {
            QueryResult kullananlar = await executor.ExecuteAsync(profil,
                BagimlilikGezgini.YonSorgusu("public.sqlst_canli_t", kullananlar: true, "postgres"),
                opts, CancellationToken.None);
            Assert.True(kullananlar.Basarili, kullananlar.Hata?.Mesaj);
            Assert.Contains(kullananlar.ResultSetler[0].Satirlar,
                s => (s[0]?.ToString() ?? "").Contains("sqlst_canli_v", StringComparison.OrdinalIgnoreCase));

            QueryResult kullandiklari = await executor.ExecuteAsync(profil,
                BagimlilikGezgini.YonSorgusu("public.sqlst_canli_v", kullananlar: false, "postgres"),
                opts, CancellationToken.None);
            Assert.True(kullandiklari.Basarili, kullandiklari.Hata?.Mesaj);
            Assert.Contains(kullandiklari.ResultSetler[0].Satirlar,
                s => (s[0]?.ToString() ?? "").Contains("sqlst_canli_t", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await executor.ExecuteAsync(profil,
                "DROP VIEW IF EXISTS sqlst_canli_v; DROP TABLE IF EXISTS sqlst_canli_t;",
                opts, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Mysql_bagimlilik_sorgulari_gercek_sunucuda()
    {
        if (!PortAcik("127.0.0.1", 3306))
            return;
        var lehce = new MySqlLehcesi(Koruyucu);
        ConnectionProfile profil = Profil(MotorTuru.MySql, "127.0.0.1:3306", "root", "SQLST_MYSQL_PAROLA");
        var kokOpts = new ExecuteOptions();
        if (await HazirsaAsync(lehce, profil, "SELECT 1", kokOpts) is not { } executor)
            return;

        await executor.ExecuteAsync(profil, "CREATE DATABASE IF NOT EXISTS sqlst_canli;", kokOpts, CancellationToken.None);
        var opts = new ExecuteOptions { VeritabaniOverride = "sqlst_canli" };
        await executor.ExecuteAsync(profil, """
            DROP VIEW IF EXISTS sqlst_canli_v;
            DROP TABLE IF EXISTS sqlst_canli_t;
            CREATE TABLE sqlst_canli_t (id int PRIMARY KEY, ad varchar(50));
            CREATE VIEW sqlst_canli_v AS SELECT id, ad FROM sqlst_canli_t;
            """, opts, CancellationToken.None);
        try
        {
            QueryResult kullananlar = await executor.ExecuteAsync(profil,
                BagimlilikGezgini.YonSorgusu("sqlst_canli_t", kullananlar: true, "mysql"),
                opts, CancellationToken.None);
            if (kullananlar.Hata?.Mesaj.Contains("VIEW_TABLE_USAGE", StringComparison.OrdinalIgnoreCase) == true)
                return; // MariaDB / MySQL < 8.0.13 — bilinen sınır (pencere/script zaten uyarıyor)
            Assert.True(kullananlar.Basarili, kullananlar.Hata?.Mesaj);
            Assert.Contains(kullananlar.ResultSetler[0].Satirlar,
                s => (s[0]?.ToString() ?? "").Contains("sqlst_canli_v", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await executor.ExecuteAsync(profil, "DROP DATABASE IF EXISTS sqlst_canli;", kokOpts, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Oracle_bagimlilik_sorgulari_gercek_sunucuda()
    {
        if (!PortAcik("127.0.0.1", 1521))
            return;
        var lehce = new OracleLehcesi(Koruyucu);
        ConnectionProfile profil = Profil(MotorTuru.Oracle, "127.0.0.1:1521/XEPDB1", "system", "SQLST_ORACLE_PAROLA");
        var opts = new ExecuteOptions();
        if (await HazirsaAsync(lehce, profil, "SELECT 1 FROM dual", opts) is not { } executor)
            return;

        await executor.ExecuteAsync(profil,
            "CREATE TABLE SQLST_CANLI_T (ID NUMBER PRIMARY KEY, AD VARCHAR2(50))", opts, CancellationToken.None);
        await executor.ExecuteAsync(profil,
            "CREATE VIEW SQLST_CANLI_V AS SELECT ID, AD FROM SQLST_CANLI_T", opts, CancellationToken.None);
        try
        {
            QueryResult kullananlar = await executor.ExecuteAsync(profil,
                BagimlilikGezgini.YonSorgusu("SQLST_CANLI_T", kullananlar: true, "oracle"),
                opts, CancellationToken.None);
            Assert.True(kullananlar.Basarili, kullananlar.Hata?.Mesaj);
            Assert.Contains(kullananlar.ResultSetler[0].Satirlar,
                s => (s[0]?.ToString() ?? "").Contains("SQLST_CANLI_V", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await executor.ExecuteAsync(profil, "DROP VIEW SQLST_CANLI_V", opts, CancellationToken.None);
            await executor.ExecuteAsync(profil, "DROP TABLE SQLST_CANLI_T", opts, CancellationToken.None);
        }
    }
}
