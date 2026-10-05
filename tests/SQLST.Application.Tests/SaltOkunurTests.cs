using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// Salt-okunur profil kapısı — 2026-07-19 gözden geçirmesi (kullanıcı isteği).
///
/// <b>Bulunan iki açık kapı:</b> kapı ilk-kelime denetimine bağlıydı, bu yüzden
/// (1) veri değiştiren CTE ve (2) blok yorumla başlayan script yazma sayılmıyordu —
/// kullanıcı korunduğunu sanırken DELETE gidebiliyordu. Kapı temkinli denetime bağlandı.
/// </summary>
public class SaltOkunurTests
{
    private static async Task<QueryResult> CalistirAsync(string sql, MotorTuru motor = MotorTuru.Mssql)
    {
        var oturum = new CasusOturum(new ConnectionProfile { Motor = motor, SaltOkunur = true });
        await new QueryService().RunAsync(oturum, sql, ExecuteOptions.Varsayilan, CancellationToken.None);
        return new QueryResult { ToplamSatir = oturum.Gonderilenler.Count };
    }

    private static async Task<bool> GonderildiMi(string sql, MotorTuru motor = MotorTuru.Mssql)
        => (await CalistirAsync(sql, motor)).ToplamSatir > 0;

    // ── Engellenmesi gerekenler (bağlantı ekranındaki parantez içi liste) ──

    [Theory]
    [InlineData("UPDATE Musteri SET Ad = 'x';")]
    [InlineData("DELETE FROM Musteri;")]
    [InlineData("INSERT INTO Musteri (Ad) VALUES ('x');")]
    [InlineData("MERGE Musteri AS h USING Kaynak AS k ON 1=1 WHEN MATCHED THEN DELETE;")]
    [InlineData("CREATE TABLE t (a int);")]
    [InlineData("ALTER PROCEDURE dbo.sp1 AS SELECT 1;")]
    [InlineData("DROP TABLE t;")]
    [InlineData("TRUNCATE TABLE t;")]
    [InlineData("EXEC dbo.spSil;")]
    public async Task Yazma_sorgulari_SUNUCUYA_GITMEZ(string sql)
        => Assert.False(await GonderildiMi(sql), $"salt-okunurda gönderilmemeliydi: {sql}");

    // ── 2026-07-19'da kapatılan açık kapılar ──

    [Fact]
    public async Task CTE_icine_gizlenmis_DELETE_de_ENGELLENIR()
    {
        // İlk sözcük WITH olduğundan eski kapı bunu OKUMA sayıp geçiriyordu.
        Assert.False(await GonderildiMi(
            "WITH silinen AS (DELETE FROM Musteri RETURNING *) SELECT count(*) FROM silinen;",
            MotorTuru.Postgres));
    }

    [Fact]
    public async Task BLOK_YORUMLA_baslayan_DELETE_de_ENGELLENIR()
    {
        // İlk "sözcük" /* olduğundan eski kapı bunu da geçiriyordu.
        Assert.False(await GonderildiMi("/* temizlik */ DELETE FROM Musteri;"));
    }

    [Fact]
    public async Task Satir_yorumundan_SONRAKI_DELETE_de_ENGELLENIR()
        => Assert.False(await GonderildiMi("-- günlük iş\nDELETE FROM Musteri;"));

    // ── Okuma sorguları geçmeli ──

    [Theory]
    [InlineData("SELECT * FROM Musteri;")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT * FROM x;")]
    [InlineData("-- not\nSELECT 1;")]
    public async Task Okuma_sorgulari_GONDERILIR(string sql)
        => Assert.True(await GonderildiMi(sql), $"okuma sorgusu gönderilmeliydi: {sql}");

    [Fact]
    public async Task Metin_sabitindeki_DELETE_yaniltmaz()
    {
        // Dize içindeki anahtar sözcük ayıklanır — yoksa sıradan bir SELECT reddedilirdi.
        Assert.True(await GonderildiMi("SELECT * FROM Log WHERE Islem = 'DELETE';"));
    }

    // ── Motor kapsamı: BEŞİNDE DE çalışır ──

    [Theory]
    [InlineData(MotorTuru.Mssql, "DELETE FROM t;")]
    [InlineData(MotorTuru.Postgres, "DELETE FROM t;")]
    [InlineData(MotorTuru.MySql, "DELETE FROM t;")]
    [InlineData(MotorTuru.Oracle, "DELETE FROM t")]
    [InlineData(MotorTuru.Mongo, """{ "delete": "musteri", "deletes": [] }""")]
    public async Task Koruma_BES_MOTORDA_da_isler(MotorTuru motor, string sql)
        => Assert.False(await GonderildiMi(sql, motor),
            $"{motor}: salt-okunur profilde yazma gönderilmemeliydi");

    [Theory]
    [InlineData(MotorTuru.Mssql, "SELECT 1;")]
    [InlineData(MotorTuru.Mongo, """{ "find": "musteri", "limit": 10 }""")]
    public async Task Okuma_BES_MOTORDA_da_gecer(MotorTuru motor, string sql)
        => Assert.True(await GonderildiMi(sql, motor));

    [Fact]
    public async Task Mongoda_COZUMLENEMEYEN_metin_gonderilmez()
    {
        // Temkinli taraf: JSON çözülemiyorsa ne olduğu bilinmiyor demektir.
        Assert.False(await GonderildiMi("bu json degil", MotorTuru.Mongo));
    }

    [Fact]
    public async Task SALT_OKUNUR_DEGILSE_yazma_gider()
    {
        var oturum = new CasusOturum(new ConnectionProfile { SaltOkunur = false });
        await new QueryService().RunAsync(
            oturum, "DELETE FROM Musteri;", ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.Contains(oturum.Gonderilenler, s => s.Contains("DELETE", StringComparison.Ordinal));
    }

    private sealed class CasusOturum(ConnectionProfile profil) : IDbOturum
    {
        public ConnectionProfile Profil { get; } = profil;

        /// <summary>İzolasyon komutu sayılmaz — ölçülen şey KULLANICININ sorgusunun gidip gitmediği.</summary>
        public List<string> Gonderilenler { get; } = [];

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
        {
            if (!sql.StartsWith("SET TRANSACTION ISOLATION LEVEL", StringComparison.Ordinal))
                Gonderilenler.Add(sql);
            return Task.FromResult(new QueryResult { Basarili = true });
        }

        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => Task.FromResult(IslemDurumu.Yok);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
