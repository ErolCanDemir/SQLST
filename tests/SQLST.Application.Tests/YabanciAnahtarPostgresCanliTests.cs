using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v6-S2 — FK okuma CANLI doğrulaması (PostgreSQL). PG'nin FK sorgusu MSSQL'den daha risklidir:
/// <c>unnest(conkey, confkey) WITH ORDINALITY</c> ile kaynak↔hedef kolonları POZİSYONA göre
/// eşler; söz dizimi hatası ancak gerçek sunucuda çıkar. Bu test bileşik FK'nin kolon sırasını
/// da kanıtlar.
///
/// Ortam-kapılı: 127.0.0.1:5433'te PostgreSQL yoksa ATLANIR.
/// </summary>
public class YabanciAnahtarPostgresCanliTests : IAsyncLifetime
{
    private const string Host = "127.0.0.1";
    private const int Port = 5433;
    private const string Db = "sqlst_demo";

    private static ConnectionProfile Profil() => new()
    {
        Ad = "pg-fk", Motor = MotorTuru.Postgres, Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql, KullaniciAdi = "postgres", BaglantiTimeoutSn = 10,
    };

    private static readonly ExecuteOptions Demo = new() { VeritabaniOverride = Db };
    private readonly string _ek = Guid.NewGuid().ToString("N")[..8];
    private readonly PostgresLehcesi _lehce = new(new DpapiSecretProtector());
    private readonly SqlExecutor _executor;
    private readonly SchemaService _schema;

    public YabanciAnahtarPostgresCanliTests()
    {
        _executor = new SqlExecutor(_lehce);
        _schema = new SchemaService(_executor, _lehce);
    }

    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    private string Musteri => $"fk_musteri_{_ek}";
    private string Kalem => $"fk_kalem_{_ek}";

    public async Task InitializeAsync()
    {
        if (!Erisilebilir()) return;
        await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE public.{Musteri} (no INT, yil INT, PRIMARY KEY (no, yil));
            CREATE TABLE public.{Kalem} (
                id INT PRIMARY KEY, m_no INT, m_yil INT,
                CONSTRAINT fk_{_ek} FOREIGN KEY (m_no, m_yil) REFERENCES public.{Musteri}(no, yil));
            """, Demo, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (!Erisilebilir()) return;
        await _executor.ExecuteAsync(Profil(),
            $"DROP TABLE IF EXISTS public.{Kalem}; DROP TABLE IF EXISTS public.{Musteri};",
            Demo, CancellationToken.None);
    }

    [Fact]
    public async Task Bilesik_FK_kolon_sirasiyla_okunur()
    {
        if (!Erisilebilir()) return;

        IReadOnlyList<YabanciAnahtar> fkler =
            await _schema.YabanciAnahtarlarAsync(Profil(), Db, CancellationToken.None);

        YabanciAnahtar fk = fkler.Single(f => f.KaynakTablo == Kalem && f.HedefTablo == Musteri);
        Assert.Equal(["m_no", "m_yil"], fk.KaynakKolonlar);   // ordinality → sıra korunur
        Assert.Equal(["no", "yil"], fk.HedefKolonlar);
        Assert.Equal("public", fk.KaynakSema);
    }
}
