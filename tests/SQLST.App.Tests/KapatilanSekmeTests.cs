using System.IO;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v19-S1 (2026-08-03) — "Kapatılan sekmeyi geri aç" (Ctrl+Shift+T + palet). Özellik FG-3.6'da
/// zaten vardı; v19 turunda TESTSİZ olduğu görüldü — bu sınıf davranışı sabitler: metin+DB
/// geri gelir, sıra LIFO'dur, yığın 10 ile sınırlıdır, boş yığında geri alma sessiz no-op'tur.
/// </summary>
public class KapatilanSekmeTests : IDisposable
{
    private readonly string _klasor = Path.Combine(
        Path.GetTempPath(), "sqlst-kapatilan-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly MainViewModel _vm;

    public KapatilanSekmeTests()
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

    // ── v19-S6/S7 (2026-08-03): sekme-veritabanı davranışları ────────────────

    [Fact]
    public async Task Yeni_sekme_AKTIF_sekmenin_veritabaniyla_acilir() // v19-S6
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel aktif = _vm.SekmeAc("is.sql", "SELECT 1", null);
        aktif.SecilenVeritabani = "SatisDb"; // kullanıcı bu veritabanında çalışıyor
        _vm.SeciliSekme = aktif;

        _vm.YeniSekme();

        var yeni = Assert.IsType<SorguSekmesiViewModel>(_vm.SeciliSekme);
        Assert.NotSame(aktif, yeni);
        Assert.Equal("SatisDb", yeni.SecilenVeritabani); // en üsttekine DÖNMEZ, aktifinkini alır
    }

    [Fact]
    public async Task Gezginden_veritabani_secimi_aktif_sekmeyi_gecirir() // v19-S7
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        _vm.VeritabaniAdlari.Add("A");
        _vm.VeritabaniAdlari.Add("B");
        SorguSekmesiViewModel sekme = _vm.SekmeAc("is.sql", "SELECT 1", "A");
        _vm.SeciliSekme = sekme;

        _vm.GezgindenVeritabaniSecildi("B");
        Assert.Equal("B", sekme.SecilenVeritabani); // combobox bağlaması bunu gösterir

        _vm.GezgindenVeritabaniSecildi("ListedeYok");
        Assert.Equal("B", sekme.SecilenVeritabani); // bilinmeyen ad sessizce yok sayılır
    }

    [Fact]
    public async Task Harita_AKTIF_sekmenin_veritabaniyla_acilir() // v19-S10 (canlı test 2026-08-04)
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel aktif = _vm.SekmeAc("is.sql", "SELECT 1", null);
        aktif.SecilenVeritabani = "SatisDb"; // kullanıcı bu veritabanında çalışıyor
        _vm.SeciliSekme = aktif;

        await _vm.HaritaAcCommand.ExecuteAsync(null);

        var harita = Assert.IsType<HaritaSekmesiViewModel>(_vm.SeciliSekme);
        Assert.Equal("SatisDb", harita.VeritabaniAdi); // bağlantı varsayılanına (ilk DB) DÖNMEZ
        Assert.Contains("SatisDb", harita.Baslik);     // hangi DB'nin çizildiği başlıkta görünür
    }

    [Fact]
    public async Task Kapatilan_sekme_metniyle_geri_gelir()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel sekme = _vm.SekmeAc("is.sql", "SELECT 42 AS cevap", "MiniSsmsDemo");
        int oncekiSayi = _vm.Sekmeler.Count;

        _vm.SekmeKapat(sekme);
        Assert.Equal(oncekiSayi - 1, _vm.Sekmeler.Count);

        _vm.KapatilanSekmeyiGeriAl();

        SorguSekmesiViewModel geri = Assert.IsType<SorguSekmesiViewModel>(_vm.SeciliSekme);
        Assert.Equal("SELECT 42 AS cevap", geri.Belge.Text);
    }

    [Fact]
    public async Task Geri_alma_LIFO_sirasiyla_calisir()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel ilk = _vm.SekmeAc("a.sql", "SELECT 'ilk'", null);
        SorguSekmesiViewModel son = _vm.SekmeAc("b.sql", "SELECT 'son'", null);

        _vm.SekmeKapat(ilk);
        _vm.SekmeKapat(son);

        _vm.KapatilanSekmeyiGeriAl();
        Assert.Equal("SELECT 'son'", ((SorguSekmesiViewModel)_vm.SeciliSekme!).Belge.Text);

        _vm.KapatilanSekmeyiGeriAl();
        Assert.Equal("SELECT 'ilk'", ((SorguSekmesiViewModel)_vm.SeciliSekme!).Belge.Text);
    }

    [Fact]
    public async Task Bos_yiginda_geri_alma_sessiz_nooptur()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        int sayi = _vm.Sekmeler.Count;

        _vm.KapatilanSekmeyiGeriAl(); // hiç kapatılan yok

        Assert.Equal(sayi, _vm.Sekmeler.Count);
    }

    [Fact]
    public async Task Yigin_10_ile_sinirlidir_en_eskiler_duser()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        for (int i = 1; i <= 12; i++)
            _vm.SekmeKapat(_vm.SekmeAc($"s{i}.sql", $"SELECT {i}", null));

        var geriGelenler = new List<string>();
        int onceki = -1;
        while (_vm.Sekmeler.Count != onceki)
        {
            onceki = _vm.Sekmeler.Count;
            _vm.KapatilanSekmeyiGeriAl();
            if (_vm.Sekmeler.Count != onceki)
                geriGelenler.Add(((SorguSekmesiViewModel)_vm.SeciliSekme!).Belge.Text);
        }

        Assert.Equal(10, geriGelenler.Count);                 // 12 kapatıldı, son 10 tutuldu
        Assert.Equal("SELECT 12", geriGelenler[0]);           // en son kapatılan ilk gelir
        Assert.DoesNotContain("SELECT 1", geriGelenler);      // en eski ikisi düştü
        Assert.DoesNotContain("SELECT 2", geriGelenler);
    }
}
