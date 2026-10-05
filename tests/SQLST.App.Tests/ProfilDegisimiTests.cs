using System.IO;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// Profil (bağlantı) değişiminde EKRAN DURUMUNUN sıfırlanması.
///
/// <b>Kullanıcı bulgusu 2026-07-19:</b> bir profilde gezgin filtresine tablo adı yazıp başka
/// profile geçince <b>filtre metni ekranda kalıyordu</b>; yeni bağlantının ağacı eski filtreyle
/// süzülü görünüyor, kullanıcı "tablolarım nerede?" diye bakıyordu.
///
/// Bu, V5-S2'deki arama sekmesi hatasıyla <b>aynı sınıf</b>: profile ait bir durumun profil
/// değişiminde temizlenmemesi (bayatlama). A3 test projesi tam bu sınıf için kurulmuştu.
/// </summary>
public class ProfilDegisimiTests : IDisposable
{
    private readonly string _klasor = Path.Combine(
        Path.GetTempPath(), "sqlst-profil-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly MainViewModel _vm;

    public ProfilDegisimiTests()
    {
        Directory.CreateDirectory(_klasor);
        var depo = new YerelDepo(Path.Combine(_klasor, "test.db"));
        var koruyucu = new DpapiSecretProtector();
        var saglayici = new LehceSaglayici(koruyucu);
        var executor = new SahteExecutor();

        _vm = new MainViewModel(
            new SahteSchemaService(),
            new QueryService(),
            new OturumFabrikasi(saglayici),
            new SqliteSorguGecmisiDeposu(depo),
            new SqliteOturumDeposu(depo),
            new SqliteAyarDeposu(depo),
            new TeshisServisi(executor),
            new MongoTeshisServisi(koruyucu),
            new SqliteTarihceDeposu(depo),
            executor,
            saglayici,
            new SqliteSnippetDeposu(depo),
            koruyucu,
            new AsistanYonlendirici(koruyucu)); // v11-S1: bu testlerde cagrilmaz, gercek tip yeter
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_klasor, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Profil_degisince_gezgin_FILTRESI_sifirlanir()
    {
        ConnectionProfile birinci = Profiller.Yap(ad: "birinci");
        ConnectionProfile ikinci = Profiller.Yap(ad: "ikinci");

        await _vm.ProfilYukleAsync(birinci);
        _vm.Arama = "musteri";                 // kullanıcı ağaçta tablo aradı
        Assert.Equal("musteri", _vm.Arama);

        await _vm.ProfilYukleAsync(ikinci);    // başka bağlantıya geçti

        Assert.Equal("", _vm.Arama);
        Assert.False(_vm.AramaBos);
    }

    [Fact]
    public async Task AYNI_profile_yeniden_baglaninca_filtre_KORUNUR()
    {
        // Sıfırlama yalnız profil DEĞİŞİMİNE bağlıdır; aynı profili yenilemek kullanıcının
        // yazdığı filtreyi silmemeli (yenile düğmesi arama kutusunu boşaltmasın).
        ConnectionProfile profil = Profiller.Yap();

        await _vm.ProfilYukleAsync(profil);
        _vm.Arama = "siparis";

        await _vm.ProfilYukleAsync(profil);

        Assert.Equal("siparis", _vm.Arama);
    }

    [Fact]
    public async Task Profil_degisince_veritabani_listesi_ve_agac_da_temizlenir()
    {
        // Filtreyle birlikte bu ikisinin de sıfırlandığını sabitler — biri eklenip diğeri
        // unutulursa yine bayat ekran oluşur.
        await _vm.ProfilYukleAsync(Profiller.Yap(ad: "birinci"));
        await _vm.ProfilYukleAsync(Profiller.Yap(ad: "ikinci"));

        Assert.Empty(_vm.VeritabaniAdlari);
        Assert.Empty(_vm.KokDugumler);
        Assert.Empty(_vm.GecmisKayitlari);
    }

    [Theory]
    [InlineData(MotorTuru.Mssql, true)]
    [InlineData(MotorTuru.Postgres, true)]
    [InlineData(MotorTuru.MySql, true)]
    [InlineData(MotorTuru.Oracle, true)]   // madde 3 (2026-08-03): Oracle üçlüye dahil edildi
    [InlineData(MotorTuru.Mongo, false)]
    public async Task Script_ozellik_kapisi_motora_gore_acilir(MotorTuru motor, bool beklenen)
    {
        // Özellik eşitliği (2026-08-03): Veri Arama / Kayıt Haritası / INSERT örneği üçlüsünün
        // görünürlüğü MotorScriptDestekli'den okunur — MSSQL tekelinden çıktı.
        await _vm.ProfilYukleAsync(Profiller.Yap(motor, ad: $"m-{motor}"));
        Assert.Equal(beklenen, _vm.MotorScriptDestekli);
    }

    [Theory]
    [InlineData(MotorTuru.Mssql, true)]
    [InlineData(MotorTuru.Postgres, true)]  // madde 2 (2026-08-03): pg_depend yolu
    [InlineData(MotorTuru.MySql, true)]     // 2026-08-03 devamı: VIEW_TABLE_USAGE + ROUTINES ~
    [InlineData(MotorTuru.Oracle, true)]    // 2026-08-03 devamı: ALL_DEPENDENCIES (kesin)
    [InlineData(MotorTuru.Mongo, false)]
    public async Task Bagimlilik_agaci_kapisi_motora_gore_acilir(MotorTuru motor, bool beklenen)
    {
        await _vm.ProfilYukleAsync(Profiller.Yap(motor, ad: $"b-{motor}"));
        Assert.Equal(beklenen, _vm.MotorBagimlilikDestekli);
    }

    [Fact]
    public async Task Gec_donen_eski_yukleme_yeni_profilin_listesini_EZMEZ()
    {
        // Yarış bekçisi (inceleme 2026-07-30): yavaş sunucunun veritabanı listesi, kullanıcı
        // başka profile geçtikten SONRA dönerse çöpe atılmalı — yeni profilin ekranını ezmemeli.
        var servis = new SahneliSchemaService();
        MainViewModel vm = VmYap(servis, "yaris.db");

        ConnectionProfile yavas = Profiller.Yap(ad: "yavas");
        ConnectionProfile hizli = Profiller.Yap(ad: "hizli");

        servis.Listeler["yavas"] = [new VeritabaniBilgisi("eski_db", false)];
        servis.Listeler["hizli"] = [new VeritabaniBilgisi("yeni_db", false)];
        await vm.ProfilYukleAsync(yavas);
        Assert.Equal(["eski_db"], vm.VeritabaniAdlari);

        // Sonraki listeleme çağrısı ASKIDA kalsın (yavaş sunucu simülasyonu)…
        servis.SonrakiCagriyiBeklet();
        Task eskiYukleme = vm.YenileAsync();
        Assert.False(eskiYukleme.IsCompleted);

        // …kullanıcı beklemeden başka profile geçer (hızlı sunucu anında döner)…
        await vm.ProfilYukleAsync(hizli);
        Assert.Equal(["yeni_db"], vm.VeritabaniAdlari);

        // …ve yavaş sunucu ANCAK ŞİMDİ cevap verir: sonuç çöpe gitmeli.
        servis.Birak();
        await eskiYukleme;

        Assert.Equal(["yeni_db"], vm.VeritabaniAdlari);
        Assert.False(vm.Yukleniyor); // bayat çağrının finally'si spinner durumunu da bozmamalı
    }

    private MainViewModel VmYap(ISchemaService schemaService, string dbAdi)
    {
        var depo = new YerelDepo(Path.Combine(_klasor, dbAdi));
        var koruyucu = new DpapiSecretProtector();
        var saglayici = new LehceSaglayici(koruyucu);
        var executor = new SahteExecutor();
        return new MainViewModel(
            schemaService,
            new QueryService(),
            new OturumFabrikasi(saglayici),
            new SqliteSorguGecmisiDeposu(depo),
            new SqliteOturumDeposu(depo),
            new SqliteAyarDeposu(depo),
            new TeshisServisi(executor),
            new MongoTeshisServisi(koruyucu),
            new SqliteTarihceDeposu(depo),
            executor,
            saglayici,
            new SqliteSnippetDeposu(depo),
            koruyucu,
            new AsistanYonlendirici(koruyucu));
    }

    /// <summary>Profil adına göre liste dönen, istenirse SONRAKİ çağrıyı askıda tutan sahne servisi.</summary>
    private sealed class SahneliSchemaService : ISchemaService
    {
        public Dictionary<string, IReadOnlyList<VeritabaniBilgisi>> Listeler { get; } = [];
        private TaskCompletionSource? _kapi;
        private int _kapiKullanildi;

        public void SonrakiCagriyiBeklet()
        {
            _kapi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _kapiKullanildi = 0;
        }

        public void Birak() => _kapi?.TrySetResult();

        public async Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(
            ConnectionProfile profil, CancellationToken ct)
        {
            // Yalnız kurulumdan sonraki İLK çağrı askıda kalır; sonrakiler (yeni profilin
            // yüklemesi) beklemeden geçer. Birak() kapıyı açana dek ilk çağrı dönemez.
            if (_kapi is { } kapi && Interlocked.Exchange(ref _kapiKullanildi, 1) == 0)
                await kapi.Task;
            return Listeler.TryGetValue(profil.Ad, out IReadOnlyList<VeritabaniBilgisi>? liste) ? liste : [];
        }

        public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
            => Task.FromResult(new SemaOnbellegi { Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow });

        public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(
            ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult(new DuzenlemeMetasi("db", "dbo", nesne.Ad, []));

        public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(
            ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);
    }

    /// <summary>Şema okumayan sahte servis — bu testler ağaç İÇERİĞİYLE ilgilenmiyor.</summary>
    private sealed class SahteSchemaService : ISchemaService
    {
        public Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(
            ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VeritabaniBilgisi>>([]);

        public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
            => Task.FromResult(new SemaOnbellegi { Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow });

        public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(
            ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult(new DuzenlemeMetasi("db", "dbo", nesne.Ad, []));

        public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(
            ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);
    }
}
