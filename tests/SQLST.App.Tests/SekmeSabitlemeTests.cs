using System.IO;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v20-S21 saha m.11/12/13 (2026-08-14) — sekme sabitleme ve toplu kapatma davranışları:
/// 📌 sabit sekme Ctrl+W/"Tümünü kapat"/"Diğerlerini kapat" yollarının HİÇBİRİYLE kapanmaz;
/// toplu kapatmalar sabitleri atlar; sabitleme sağ tık menüsünden aç/kapa çalışır.
/// </summary>
public class SekmeSabitlemeTests : IDisposable
{
    private readonly string _klasor = Path.Combine(
        Path.GetTempPath(), "sqlst-sabit-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly MainViewModel _vm;

    public SekmeSabitlemeTests()
    {
        Directory.CreateDirectory(_klasor);
        var depo = new YerelDepo(Path.Combine(_klasor, "test.db"));
        var koruyucu = new DpapiSecretProtector();
        var saglayici = new LehceSaglayici(koruyucu);
        var executor = new SahteExecutor();

        _vm = new MainViewModel(
            new BosSemaServisi(), new QueryService(), new OturumFabrikasi(saglayici),
            new SqliteSorguGecmisiDeposu(depo), new SqliteOturumDeposu(depo),
            new SqliteAyarDeposu(depo), new TeshisServisi(executor),
            new MongoTeshisServisi(koruyucu), new SqliteTarihceDeposu(depo),
            executor, saglayici, new SqliteSnippetDeposu(depo), koruyucu,
            new AsistanYonlendirici(koruyucu));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_klasor, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Şema okumayan sahte — bu testler yalnız sekme yaşam döngüsüyle ilgilenir.</summary>
    private sealed class BosSemaServisi : ISchemaService
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

    [Fact]
    public async Task Sabit_sekme_SekmeKapat_ile_kapanmaz() // m.13 — Ctrl+W ve ✕ bu yoldan geçer
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel sekme = _vm.SekmeAc("is.sql", "SELECT 1", null);
        sekme.Sabit = true;
        int sayi = _vm.Sekmeler.Count;

        _vm.SekmeKapat(sekme);

        Assert.Equal(sayi, _vm.Sekmeler.Count);   // sekme yerinde
        Assert.Contains(sekme, _vm.Sekmeler);

        sekme.Sabit = false;                       // sabitlik kalkınca normal kapanır
        _vm.SekmeKapat(sekme);
        Assert.DoesNotContain(sekme, _vm.Sekmeler);
    }

    [Fact]
    public async Task Tumunu_kapat_sabitleri_atlar() // m.12
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel s1 = _vm.SekmeAc("a.sql", "SELECT 1", null);
        SorguSekmesiViewModel s2 = _vm.SekmeAc("b.sql", "SELECT 2", null);
        SorguSekmesiViewModel s3 = _vm.SekmeAc("c.sql", "SELECT 3", null);
        s2.Sabit = true;

        _vm.TumSekmeleriKapat();

        Assert.DoesNotContain(s1, _vm.Sekmeler);
        Assert.DoesNotContain(s3, _vm.Sekmeler);
        ISekme kalan = Assert.Single(_vm.Sekmeler);
        Assert.Same(s2, kalan);                    // yalnız sabit sekme hayatta
    }

    [Fact]
    public async Task Digerlerini_kapat_verilen_ve_sabitler_kalir() // m.12
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel s1 = _vm.SekmeAc("a.sql", "SELECT 1", null);
        SorguSekmesiViewModel s2 = _vm.SekmeAc("b.sql", "SELECT 2", null);
        SorguSekmesiViewModel s3 = _vm.SekmeAc("c.sql", "SELECT 3", null);
        s1.Sabit = true;                           // sabit: kapanmamalı

        _vm.DigerSekmeleriKapat(s3);               // sağ tık s3 üzerinde

        Assert.Contains(s1, _vm.Sekmeler);         // sabit hayatta
        Assert.DoesNotContain(s2, _vm.Sekmeler);   // sıradan sekme kapandı
        Assert.Contains(s3, _vm.Sekmeler);         // sağ tıklanan hayatta
        Assert.Same(s3, _vm.SeciliSekme);          // odak sağ tıklananda kalır
    }

    [Fact]
    public async Task Sabitle_komutu_ac_kapa_calisir() // m.13 — sağ tık menüsü
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel sekme = _vm.SekmeAc("is.sql", "SELECT 1", null);

        Assert.False(sekme.Sabit);
        _vm.SekmeSabitDegistir(sekme);
        Assert.True(sekme.Sabit);
        _vm.SekmeSabitDegistir(sekme);
        Assert.False(sekme.Sabit);
    }

    [Fact]
    public async Task Sabit_sekme_geri_alma_yiginina_dusmez() // m.13 — kapanmadı ki geri gelsin
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel sekme = _vm.SekmeAc("is.sql", "SELECT 'sabit'", null);
        sekme.Sabit = true;
        _vm.SekmeKapat(sekme);                     // no-op olmalı
        int sayi = _vm.Sekmeler.Count;

        _vm.KapatilanSekmeyiGeriAl();              // yığında bir şey OLMAMALI

        Assert.Equal(sayi, _vm.Sekmeler.Count);    // kopya sekme türemedi
    }
}
