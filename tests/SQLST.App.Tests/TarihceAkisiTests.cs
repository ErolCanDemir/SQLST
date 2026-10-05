using System.IO;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// Nesne tarihçesinin ALTER akışı.
///
/// <b>Kullanıcı bulgusu 2026-07-19:</b> <i>"Bir SP'de güncelleme yaptım, veritabanını refresh
/// butonuna basmadan tarihçe bilgisi gelmedi. Bunun hemen gelmesi lazımdı."</i>
///
/// Sebep: tarihçe iki uçtan beslenmeliydi ama biri hiç yoktu — ALTER'dan ÖNCEKİ sürüm
/// kaydediliyor, ALTER'dan SONRAKİ kaydedilmiyordu. Yeni sürüm ancak tanım bir daha
/// OKUNDUĞUNDA giriyordu; tarihçe penceresi ise yerel liste boş değilse sunucuya hiç
/// gitmiyordu ve "alter-öncesi" kaydı listeyi zaten doldurduğu için hiç boş olmuyordu.
/// </summary>
public class TarihceAkisiTests : IDisposable
{
    private readonly string _klasor = Path.Combine(
        Path.GetTempPath(), "sqlst-tarihce-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly MainViewModel _vm;
    private readonly SahteSemaTanimlari _sema = new();
    private readonly ITarihceDeposu _tarihce;

    private static readonly SemaNesnesi Sp = new(
        "MiniSsmsDemo", "dbo", "spTest", SemaNesneTuru.StoredProcedure, [], []);

    public TarihceAkisiTests()
    {
        Directory.CreateDirectory(_klasor);
        var depo = new YerelDepo(Path.Combine(_klasor, "test.db"));
        var koruyucu = new DpapiSecretProtector();
        var saglayici = new LehceSaglayici(koruyucu);
        var executor = new SahteExecutor();
        _tarihce = new SqliteTarihceDeposu(depo);

        _vm = new MainViewModel(
            _sema, new QueryService(), new OturumFabrikasi(saglayici),
            new SqliteSorguGecmisiDeposu(depo), new SqliteOturumDeposu(depo),
            new SqliteAyarDeposu(depo), new TeshisServisi(executor),
            new MongoTeshisServisi(koruyucu), _tarihce,
            executor, saglayici, new SqliteSnippetDeposu(depo), koruyucu,
            new AsistanYonlendirici(koruyucu)); // v11-S1: bu testlerde cagrilmaz
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_klasor, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// ASIL REGRESYON: ALTER başarıyla bitince YENİ tanım hiçbir şeye basmadan tarihçeye
    /// girmeli. Düzeltmeden önce yalnız "alter-öncesi" kaydı vardı.
    /// </summary>
    [Fact]
    public async Task ALTER_basariliysa_YENI_tanim_hemen_tarihceye_girer()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());

        _sema.Tanim = "CREATE PROCEDURE dbo.spTest AS SELECT 1";
        SorguSekmesiViewModel sekme = _vm.SekmeAc("t.sql", "", "MiniSsmsDemo");

        // Kullanıcı SP'yi güncelledi ve F5'e bastı
        _sema.Tanim = "CREATE PROCEDURE dbo.spTest AS SELECT 2";
        await CalistirmaBittiTetikle(sekme, "ALTER PROCEDURE dbo.spTest AS SELECT 2",
            GecmisDurumu.Basarili);

        IReadOnlyList<TarihceKaydi> kayitlar = await _vm.TarihceListesiAsync(Sp);

        Assert.Contains(kayitlar, k => k.Kaynak == "alter-sonrası");
        Assert.Contains(kayitlar, k => k.Tanim.Contains("SELECT 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALTER_HATALIYSA_tarihceye_yazilmaz()
    {
        // Başarısız çalıştırmada sunucudaki tanım değişmemiştir; sahte bir sürüm eklemek
        // tarihçeyi kirletir ve "dışarıda değişti" karşılaştırmasını bozar.
        await _vm.ProfilYukleAsync(Profiller.Yap());
        _sema.Tanim = "CREATE PROCEDURE dbo.spTest AS SELECT 1";
        SorguSekmesiViewModel sekme = _vm.SekmeAc("t.sql", "", "MiniSsmsDemo");

        await CalistirmaBittiTetikle(sekme, "ALTER PROCEDURE dbo.spTest AS SELECT 9",
            GecmisDurumu.Hata);

        Assert.DoesNotContain(await _vm.TarihceListesiAsync(Sp), k => k.Kaynak == "alter-sonrası");
    }

    [Fact]
    public async Task ALTER_olmayan_sorgu_tarihceyi_ETKILEMEZ()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        _sema.Tanim = "CREATE PROCEDURE dbo.spTest AS SELECT 1";
        SorguSekmesiViewModel sekme = _vm.SekmeAc("t.sql", "", "MiniSsmsDemo");

        await CalistirmaBittiTetikle(sekme, "SELECT * FROM dbo.Musteri;", GecmisDurumu.Basarili);

        Assert.Empty(await _vm.TarihceListesiAsync(Sp));
    }

    /// <summary>
    /// Tarihçe penceresi HER AÇILIŞTA sunucudaki güncel tanımı da işlemeli — nesne
    /// dışarıdan değiştirilmiş olabilir. Eskiden bu yalnız yerel liste BOŞSA yapılıyordu.
    /// </summary>
    [Fact]
    public async Task Tarihce_acilisi_DISARIDAKI_degisikligi_de_yakalar()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());

        _sema.Tanim = "CREATE PROCEDURE dbo.spTest AS SELECT 1";
        await _vm.TarihceyiBaslatAsync(Sp);                    // ilk görüş

        _sema.Tanim = "CREATE PROCEDURE dbo.spTest AS SELECT 42";   // başkası değiştirdi

        IReadOnlyList<TarihceKaydi> kayitlar = await _vm.TarihceyiBaslatAsync(Sp);

        Assert.Equal(2, kayitlar.Count);
        Assert.Contains(kayitlar, k => k.Tanim.Contains("SELECT 42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ayni_tanim_MUKERRER_surum_uretmez()
    {
        // Pencereyi defalarca açmak tarihçeyi şişirmemeli (depo içerik hash'iyle eler).
        await _vm.ProfilYukleAsync(Profiller.Yap());
        _sema.Tanim = "CREATE PROCEDURE dbo.spTest AS SELECT 1";

        await _vm.TarihceyiBaslatAsync(Sp);
        await _vm.TarihceyiBaslatAsync(Sp);
        await _vm.TarihceyiBaslatAsync(Sp);

        Assert.Single(await _vm.TarihceListesiAsync(Sp));
    }

    /// <summary>
    /// Sekmenin çalıştırma-bitti işleyicisini doğrudan çağırır. Olay yalnız tanımlayan
    /// sınıftan tetiklenebildiğinden işleyici <c>internal</c> yapıldı — dal mantığı
    /// (başarılıysa tarihçeye işle) ancak böyle ölçülebiliyor.
    /// </summary>
    private async Task CalistirmaBittiTetikle(
        SorguSekmesiViewModel sekme, string sql, GecmisDurumu durum)
    {
        _vm.CalistirmaBitti(sekme, new GecmisKaydi
        {
            Sunucu = "sunucu",
            Veritabani = "MiniSsmsDemo",
            Sql = sql,
            BaslangicUtc = DateTime.UtcNow,
            SureMs = 5,
            Durum = durum,
        });

        // Tarihçe işleme fire-and-forget: yazılması için kısa bir tur beklenir.
        for (int i = 0; i < 50; i++)
            await Task.Delay(10);
    }

    /// <summary>Tanımı testin kontrol ettiği sahte şema servisi.</summary>
    private sealed class SahteSemaTanimlari : ISchemaService
    {
        public string? Tanim { get; set; }

        public Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(
            ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VeritabaniBilgisi>>([]);

        public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
            => Task.FromResult(new SemaOnbellegi { Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow });

        public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult(Tanim);

        public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(
            ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult(new DuzenlemeMetasi("db", "dbo", nesne.Ad, []));

        public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(
            ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);
    }
}
