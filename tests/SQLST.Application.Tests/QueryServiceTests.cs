using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

public class QueryServiceTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Orders", false)]
    [InlineData("  select 1", false)]
    [InlineData("UPDATE dbo.Orders SET X=1", true)]
    [InlineData("delete from dbo.Orders", true)]
    [InlineData("-- yorum satırı\nDROP TABLE dbo.Orders", true)]
    [InlineData("\t EXEC dbo.spGetOrders", true)]
    [InlineData("TRUNCATE TABLE dbo.Orders", true)]
    [InlineData("", false)]
    [InlineData("-- sadece yorum", false)]
    public void YazmaSorgusuMu_ilk_anlamli_tokena_gore_siniflar(string sql, bool beklenen)
    {
        Assert.Equal(beklenen, QueryService.YazmaSorgusuMu(sql));
    }

    [Theory]
    [InlineData("EXEC dbo.spGuncelle @id=1", true)]
    [InlineData("  execute dbo.sp @x=2", true)]
    [InlineData("-- yorum\nEXEC dbo.sp", true)]
    [InlineData("UPDATE dbo.T SET x=1", false)]  // düz DML EXEC değil
    [InlineData("SELECT * FROM dbo.T", false)]
    [InlineData("EXECUTED_ALREADY = 1", false)]  // token EXEC değil, EXECUTED
    public void ExecMi_saklı_yordam_cagrisini_tanir(string sql, bool beklenen)
        => Assert.Equal(beklenen, QueryService.ExecMi(sql));

    [Fact]
    public async Task SaltOkunur_profilde_yazma_sorgusu_gonderilmeden_engellenir()
    {
        var servis = new QueryService();
        var oturum = new PatlayanOturum(new ConnectionProfile { SaltOkunur = true });

        QueryResult sonuc = await servis.RunAsync(
            oturum, "DELETE FROM dbo.Orders", ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.False(sonuc.Basarili);
        Assert.NotNull(sonuc.Hata);
        Assert.Contains("salt-okunur", sonuc.Hata!.Mesaj);
    }

    [Theory]
    [InlineData(true, "READ UNCOMMITTED")]
    [InlineData(false, "READ COMMITTED")]
    public void Izolasyon_ifadesi_seviyeye_gore_uretilir(bool kirli, string beklenenSeviye)
        => Assert.Equal($"SET TRANSACTION ISOLATION LEVEL {beklenenSeviye};", QueryService.IzolasyonSql(kirli));

    /// <summary>
    /// KULLANICI BULGUSUNUN REGRESYONU (2026-07-19): izolasyon ifadesi kullanıcının SQL'iyle
    /// AYNI BATCH'E konulursa T-SQL'in "batch'in ilk ifadesi olmalı" kuralına takılan her şey
    /// bozulur (CREATE/ALTER PROCEDURE|VIEW|FUNCTION|TRIGGER, SET SHOWPLAN_XML) →
    /// Msg 111. Ayrı komut olarak gitmeli ve kullanıcının metnine DOKUNULMAMALI.
    /// </summary>
    [Fact]
    public async Task Izolasyon_AYRI_komut_gider_kullanici_sqli_DEGISMEZ()
    {
        var casus = new CasusOturum(new ConnectionProfile());
        var servis = new QueryService();
        const string kullaniciSql = "CREATE PROCEDURE dbo.sp1 AS SELECT 1;";

        await servis.RunAsync(casus, kullaniciSql,
            new ExecuteOptions { KirliOkuma = true }, CancellationToken.None);

        Assert.Equal(2, casus.Sqller.Count);
        Assert.Equal("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", casus.Sqller[0]);
        Assert.Equal(kullaniciSql, casus.Sqller[1]);          // birebir, dokunulmamış
    }

    [Fact]
    public async Task Kirli_okuma_secenegi_izolasyon_komutuna_yansir()
    {
        var casus = new CasusOturum(new ConnectionProfile());
        var servis = new QueryService();

        await servis.RunAsync(casus, "SELECT 1",
            new ExecuteOptions { KirliOkuma = true }, CancellationToken.None);
        Assert.Contains("READ UNCOMMITTED", casus.Sqller[0], StringComparison.Ordinal);

        await servis.RunAsync(casus, "SELECT 1", ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.Contains("READ COMMITTED", casus.Sqller[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Izolasyon_kurulamazsa_kullanici_sorgusu_GONDERILMEZ()
    {
        var casus = new CasusOturum(new ConnectionProfile()) { IlkKomutHata = true };
        var servis = new QueryService();

        QueryResult sonuc = await servis.RunAsync(
            casus, "DELETE FROM dbo.Musteri;", ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.NotNull(sonuc.Hata);
        Assert.Single(casus.Sqller);                          // yalnız SET denendi
    }

    [Fact]
    public async Task MSSQL_disinda_izolasyon_komutu_GONDERILMEZ()
    {
        // İzolasyon ifadesi T-SQL'dir; PG/MySQL/Oracle/Mongo'da gönderilmemeli.
        var casus = new CasusOturum(new ConnectionProfile { Motor = MotorTuru.Postgres });
        var servis = new QueryService();

        await servis.RunAsync(casus, "SELECT 1", ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.Single(casus.Sqller);
        Assert.Equal("SELECT 1", casus.Sqller[0]);
    }

    private sealed class CasusOturum(ConnectionProfile profil) : IDbOturum
    {
        public ConnectionProfile Profil { get; } = profil;

        /// <summary>Oturuma giden TÜM komutlar, sırasıyla (izolasyon artık ayrı komuttur).</summary>
        public List<string> Sqller { get; } = [];

        public string SonSql => Sqller.Count > 0 ? Sqller[^1] : "";

        /// <summary>İlk komut (izolasyon) hata döndürsün — "kurulamazsa gönderme" testi için.</summary>
        public bool IlkKomutHata { get; init; }

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
        {
            Sqller.Add(sql);
            return Task.FromResult(IlkKomutHata && Sqller.Count == 1
                ? new QueryResult { Hata = new SqlHata("izolasyon kurulamadı", 0, 0, 0) }
                : new QueryResult { Basarili = true });
        }

        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => Task.FromResult(IslemDurumu.Yok);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Çağrılırsa test patlar — sorgunun sunucuya hiç gitmediğini kanıtlar.</summary>
    private sealed class PatlayanOturum(ConnectionProfile profil) : IDbOturum
    {
        public ConnectionProfile Profil { get; } = profil;

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
            => throw new InvalidOperationException("Salt-okunur profilde sorgu sunucuya gitmemeliydi.");

        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => throw new InvalidOperationException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
