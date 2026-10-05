using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V4-S2 CANLI KANITI: Güvenli Yazma Modu'nun PostgreSQL'de gerçekten söz tuttuğunu gösterir —
/// V4-S2'ye kadar özellik yalnız SQL Server'da açıktı (PG'de arayüzden gizliydi).
///
/// Birim testleri yalnız GÖNDERİLEN İFADELERİ kanıtlar; burada gerçek satırlar üzerinde
/// ROLLBACK'in veriyi hiç değiştirmediği ve COMMIT'in kalıcı olduğu doğrulanır. Ayrıca
/// PostgreSQL'in ayırt edici özelliği — <b>DDL'in de geri alınabilmesi</b> — sınanır;
/// V4-S2 kararı (PG'de DDL dahil her yazmada bant açılır) buna dayanır.
///
/// Ortam-kapılı: 127.0.0.1:5433'te PostgreSQL + sqlst_demo yoksa test ATLANIR.
/// </summary>
public class GuvenliYazmaPostgresCanliTests : IAsyncLifetime
{
    private const string Host = "127.0.0.1";
    private const int Port = 5433;
    private const string Db = "sqlst_demo";

    private static ConnectionProfile Profil() => new()
    {
        Ad = "pg-guvenli-yazma",
        Motor = MotorTuru.Postgres,
        Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql,   // trust auth
        KullaniciAdi = "postgres",
        BaglantiTimeoutSn = 10,
    };

    private static readonly ExecuteOptions Demo = new() { VeritabaniOverride = Db };

    private readonly string _tablo = $"guvenli_yazma_{Guid.NewGuid():N}";
    private readonly PostgresLehcesi _lehce = new(new DpapiSecretProtector());
    private readonly SqlExecutor _executor;
    private readonly QueryService _servis = new();

    public GuvenliYazmaPostgresCanliTests() => _executor = new SqlExecutor(_lehce);

    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    public async Task InitializeAsync()
    {
        if (!Erisilebilir()) return;
        QueryResult r = await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE public.{_tablo} (x INT);
            INSERT INTO public.{_tablo} (x) VALUES (1), (2);
            """, Demo, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);
    }

    public async Task DisposeAsync()
    {
        if (!Erisilebilir()) return;
        await _executor.ExecuteAsync(Profil(),
            $"DROP TABLE IF EXISTS public.{_tablo};", Demo, CancellationToken.None);
    }

    private async Task<int> SayAsync(IDbOturum oturum)
    {
        QueryResult r = await oturum.CalistirAsync(
            $"SELECT COUNT(*) FROM public.{_tablo};", Demo, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);
        return Convert.ToInt32(r.ResultSetler[0].Satirlar[0][0]);
    }

    [Fact]
    public async Task Rollback_yolu_veriyi_hic_degistirmez()
    {
        if (!Erisilebilir()) return;

        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        QueryResult silme = await GuvenliYazmaYurutucu.CalistirAsync(_servis, _lehce, oturum,
            SqlCozumleyici.BatchlereBol($"DELETE FROM public.{_tablo} WHERE x <= 2"),
            Demo, CancellationToken.None);

        Assert.True(silme.Basarili, silme.Hata?.Mesaj);
        Assert.Equal(2, silme.EtkilenenSatir);          // bandın gösterdiği sayı

        string mesaj = await GuvenliYazmaYurutucu.KararUygulaAsync(
            _servis, _lehce, oturum, commit: false, opts: Demo);

        Assert.Contains("ROLLBACK", mesaj);
        Assert.Equal(2, await SayAsync(oturum));        // veri hiç değişmedi
    }

    [Fact]
    public async Task Commit_yolu_kalici_olur()
    {
        if (!Erisilebilir()) return;

        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        await GuvenliYazmaYurutucu.CalistirAsync(_servis, _lehce, oturum,
            SqlCozumleyici.BatchlereBol($"DELETE FROM public.{_tablo} WHERE x <= 2"),
            Demo, CancellationToken.None);

        string mesaj = await GuvenliYazmaYurutucu.KararUygulaAsync(
            _servis, _lehce, oturum, commit: true, opts: Demo);

        Assert.Contains("COMMIT", mesaj);
        Assert.Equal(0, await SayAsync(oturum));        // silme kalıcı
    }

    [Fact]
    public async Task Postgreste_DDL_de_geri_alinabilir()
    {
        // V4-S2 KARARININ DAYANAĞI: PG'de DDL işlemseldir → bant DDL'de de açılabilir.
        // (MySQL/Oracle'da aynı senaryo örtük COMMIT yapar ve tablo KALIRDI.)
        if (!Erisilebilir()) return;

        string gecici = $"ddl_geri_al_{Guid.NewGuid():N}";
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        QueryResult olustur = await GuvenliYazmaYurutucu.CalistirAsync(_servis, _lehce, oturum,
            SqlCozumleyici.BatchlereBol($"CREATE TABLE public.{gecici} (id INT)"),
            Demo, CancellationToken.None);
        Assert.True(olustur.Basarili, olustur.Hata?.Mesaj);

        // İşlem AÇIKKEN tablo aynı oturumda görünür
        QueryResult icerde = await oturum.CalistirAsync(
            $"SELECT to_regclass('public.{gecici}') IS NOT NULL;", Demo, CancellationToken.None);
        Assert.True((bool)icerde.ResultSetler[0].Satirlar[0][0]!);

        await GuvenliYazmaYurutucu.KararUygulaAsync(_servis, _lehce, oturum, commit: false, opts: Demo);

        // ROLLBACK sonrası tablo HİÇ var olmamış gibi
        QueryResult sonra = await oturum.CalistirAsync(
            $"SELECT to_regclass('public.{gecici}') IS NULL;", Demo, CancellationToken.None);
        Assert.True((bool)sonra.ResultSetler[0].Satirlar[0][0]!,
            "PostgreSQL'de CREATE TABLE geri alınabilmeliydi (transactional DDL)");
    }

    [Fact]
    public async Task Hatali_yazma_aninda_geri_alinir()
    {
        if (!Erisilebilir()) return;

        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        // Geçerli bir silme + ardından hatalı ifade: güvenli mod yarım iş bırakmamalı
        QueryResult sonuc = await GuvenliYazmaYurutucu.CalistirAsync(_servis, _lehce, oturum,
            SqlCozumleyici.BatchlereBol(
                $"DELETE FROM public.{_tablo} WHERE x = 1;\nDELETE FROM public.olmayan_tablo_xyz;"),
            Demo, CancellationToken.None);

        Assert.False(sonuc.Basarili);
        Assert.Contains(sonuc.Mesajlar, m => m.Contains("geri alındı"));
        Assert.Equal(2, await SayAsync(oturum));        // ilk silme de geri alındı
    }
}
