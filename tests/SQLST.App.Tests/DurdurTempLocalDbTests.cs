using System.IO;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v23-S22 (kullanıcı 2026-10-10: "sorguyu durdurunca #temp'ler neden kalkıyor") — GERÇEK LocalDB'de:
/// sekmede #temp tablo kurulur, uzun WAITFOR koşusu ■ Durdur ile kesilir, aynı sekmedeki sonraki sorgu
/// #temp tabloyu hâlâ görür (SSMS davranışı). Eskiden Durdur bağlantıyı kapattığından #temp yok oluyordu.
/// </summary>
public class DurdurTempLocalDbTests : IDisposable
{
    private readonly string _klasor = Path.Combine(Path.GetTempPath(), "sqlst-durdur-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_klasor, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Durdur_sonrasi_temp_tablo_ayni_sekmede_yasar()
    {
        Directory.CreateDirectory(_klasor);
        var depo = new YerelDepo(Path.Combine(_klasor, "test.db"));
        var koruyucu = new DpapiSecretProtector();
        var saglayici = new LehceSaglayici(koruyucu);
        var executor = new SqlExecutor(koruyucu);
        var vm = new MainViewModel(
            new BosSema(), new QueryService(), new OturumFabrikasi(saglayici),
            new SqliteSorguGecmisiDeposu(depo), new SqliteOturumDeposu(depo), new SqliteAyarDeposu(depo),
            new TeshisServisi(executor), new MongoTeshisServisi(koruyucu), new SqliteTarihceDeposu(depo),
            executor, saglayici, new SqliteSnippetDeposu(depo), koruyucu, new AsistanYonlendirici(koruyucu));
        await vm.ProfilYukleAsync(new ConnectionProfile
        {
            Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB", Kimlik = KimlikTuru.Windows,
            Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
        });

        SorguSekmesiViewModel a = vm.SekmeAc("durdur.sql", "", "tempdb");
        string sql = "CREATE TABLE #sqlst_durdur (x int); INSERT INTO #sqlst_durdur VALUES (42);";
        a.MetinSaglayici = () => sql;
        await a.CalistirAsync();
        Assert.Equal(SekmeDurumu.Tamamlandi, a.Durum);

        sql = "WAITFOR DELAY '00:01:00';";
        Task uzun = a.CalistirAsync();
        for (int i = 0; i < 200 && !a.CalisiyorMu; i++)
            await Task.Delay(20);
        await Task.Delay(500); // komut sunucuda gerçekten beklemeye girsin
        a.IptalCommand.Execute(null);
        Assert.Contains("Bağlantı korunuyor", a.Mesajlar);
        await uzun;

        sql = "SELECT x FROM #sqlst_durdur;";
        await a.CalistirAsync();
        Assert.True(a.Durum == SekmeDurumu.Tamamlandi, a.Mesajlar); // #temp hâlâ var — "Invalid object name" yok
        Assert.Equal(42, Convert.ToInt32(a.SonucSetleri[0].Tablo.Rows[0][0]));
    }

    /// <summary>Bu test şema okumaz — yalnız sorgu sekmesinin oturum davranışı.</summary>
    private sealed class BosSema : ISchemaService
    {
        public Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VeritabaniBilgisi>>([]);

        public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
            => Task.FromResult(new SemaOnbellegi { Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow });

        public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult(new DuzenlemeMetasi("db", "dbo", nesne.Ad, []));

        public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);
    }
}
