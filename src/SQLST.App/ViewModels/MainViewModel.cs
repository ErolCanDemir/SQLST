using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure; // MongoTeshisServisi (Mongo yönetim paneli — V3)

namespace SQLST.App.ViewModels;

public sealed class GezginGrubu
{
    public required string Baslik { get; init; }
    public required IReadOnlyList<SemaNesnesi> Nesneler { get; init; }
    /// <summary>Grup düğümleri açık başlar (TwoWay IsExpanded bağı için yazılabilir).</summary>
    public bool AcikMi { get; set; } = true;
}

/// <summary>Ağacın kök düğümü: sunucudaki bir veritabanı (FG-2.1). İçeriği ilk açılışta yüklenir.</summary>
public partial class GezginVeritabani : ObservableObject
{
    /// <summary>
    /// Yüklenmemiş düğümün genişletme oku çıksın diye konan sahte çocuk;
    /// gerçek içerik yüklenince GruplariKur tarafından süpürülür.
    /// </summary>
    public static readonly GezginGrubu YerTutucu = new() { Baslik = "…", Nesneler = [] };

    public required string Ad { get; init; }
    public ObservableCollection<GezginGrubu> Gruplar { get; } = [YerTutucu];

    [ObservableProperty] private bool _acikMi;
    /// <summary>Düğüm yanı küçük durum notu: "yükleniyor…" / hata metni.</summary>
    [ObservableProperty] private string? _bilgi;

    public bool Yuklendi { get; set; }
}

/// <summary>master/model/msdb/tempdb'yi katlayan klasör düğümü (SSMS "System Databases" karşılığı).</summary>
public sealed class GezginSistemKlasoru
{
    public required string Baslik { get; init; }
    public ObservableCollection<GezginVeritabani> Veritabanlari { get; } = [];
    /// <summary>Kapalı başlar; TwoWay IsExpanded bağı için yazılabilir.</summary>
    public bool AcikMi { get; set; }
}

/// <summary>
/// Ana pencerenin durumu: nesne gezgini (FG-2.1–2.4). Kök = veritabanları; bir
/// veritabanı ilk açıldığında şeması yüklenip önbelleğe alınır, arama bellekten süzer.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ISchemaService _schemaService;
    private readonly QueryService _queryService;
    private readonly IOturumFabrikasi _oturumFabrikasi;
    private readonly ISorguGecmisiDeposu _gecmisDeposu;
    private readonly IOturumDeposu _oturumDeposu;
    private readonly IAyarDeposu _ayarDeposu;
    private readonly Dictionary<string, SemaOnbellegi> _onbellekler = new(StringComparer.OrdinalIgnoreCase);
    private ConnectionProfile? _profil;
    /// <summary>Profil değişimi yarışı bekçisi (inceleme 2026-07-30): her ProfilYukleAsync nesli
    /// artırır; uçuştaki eski yükleme dönünce nesli değişmiş bulur ve sonucunu ÇÖPE atar —
    /// eski profilin veritabanı listesi/şeması yeni profilin ekranına/önbelleğine sızamaz.</summary>
    private int _profilNesli;
    private string? _varsayilanVeritabani;
    private int _sekmeSayaci;

    private readonly TeshisServisi _teshisServisi;
    private readonly MongoTeshisServisi _mongoTeshisServisi;
    private readonly ITarihceDeposu _tarihceDeposu;
    private readonly ISqlExecutor _executor;
    private readonly ILehceSaglayici _lehceler;

    private readonly ISnippetDeposu _snippetDeposu;
    private readonly ISecretProtector _protector;

    /// <summary>
    /// MongoDB metin arama köprüsü (B5/A6). Mongo ayrı ailedir ve <see cref="ILehce"/>
    /// uygulamaz; arama sekmesi ona bu delege üzerinden ulaşır.
    /// </summary>
    private readonly AramaSekmesiViewModel.MongoAramaDelegesi? _mongoArayici;

    /// <summary>
    /// Aktif motorda geçerli kod parçaları (V5-S4). Bağlantı değişince yeniden yüklenir;
    /// tamamlama penceresi her tuşta depoya gitmesin diye bellekte tutulur.
    /// </summary>
    public IReadOnlyList<Snippet> Snippetler { get; private set; } = [];

    /// <summary>Snippet düzenlendikten sonra listeyi tazeler (yönetim ekranı çağırır).</summary>
    public async Task SnippetleriYenileAsync()
    {
        if (_profil is { } profil)
            Snippetler = await _snippetDeposu.ListeleAsync(profil.Motor, CancellationToken.None);
    }

    public MainViewModel(
        ISchemaService schemaService, QueryService queryService, IOturumFabrikasi oturumFabrikasi,
        ISorguGecmisiDeposu gecmisDeposu, IOturumDeposu oturumDeposu, IAyarDeposu ayarDeposu,
        TeshisServisi teshisServisi, MongoTeshisServisi mongoTeshisServisi, ITarihceDeposu tarihceDeposu,
        ISqlExecutor executor, ILehceSaglayici lehceler, ISnippetDeposu snippetDeposu,
        ISecretProtector protector, IAsistanServisi asistanServisi)
    {
        _snippetDeposu = snippetDeposu;
        _protector = protector;
        _asistanServisi = asistanServisi;
        _mongoArayici = (profil, veritabani, aranan, duyarli, ct)
            => MongoMetinArayici.AraAsync(profil, protector, veritabani, aranan, duyarli, ct);
        _mongoTeshisServisi = mongoTeshisServisi;
        _executor = executor;
        _lehceler = lehceler;
        _schemaService = schemaService;
        _queryService = queryService;
        _oturumFabrikasi = oturumFabrikasi;
        _gecmisDeposu = gecmisDeposu;
        _oturumDeposu = oturumDeposu;
        _ayarDeposu = ayarDeposu;
        _teshisServisi = teshisServisi;
        _tarihceDeposu = tarihceDeposu;
    }

    // --- Nesne tarihçesi (V2-S8, Ö2) ---

    /// <summary>Tanımı yerel tarihçeye işler; "dışarıda değişti" rozetini döner (Ö2).</summary>
    private async Task<bool> TarihceyeIsleAsync(SemaNesnesi nesne, string tanim, string kaynak)
    {
        if (_profil is null)
            return false;
        try
        {
            // Tarihçe de PROFİL kapsamlı (V3): aynı sunucuya bakan iki profil birbirinin
            // sürüm zincirini görmez/bozmaz.
            TarihceKaydi? son = await _tarihceDeposu.SonAsync(
                _profil.Id, _profil.Sunucu, nesne.Veritabani, nesne.Sema, nesne.Ad);
            bool disaridaDegisti = TarihceYardimcisi.DisaridaDegisti(son, tanim);
            await _tarihceDeposu.EkleAsync(
                TarihceYardimcisi.KayitKur(_profil.Sunucu, nesne.Veritabani, nesne.Sema, nesne.Ad, tanim, kaynak)
                    with { ProfilId = _profil.Id });
            return disaridaDegisti;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Nesne tarihçesine yazılamadı"); // tarihçe asla akışı engellemez
            return false;
        }
    }

    /// <summary>FG-5.5: ALTER çalıştırılmadan ÖNCE hedef nesnelerin mevcut tanımı yedeklenir.</summary>
    private async Task AlterOncesiYedekleAsync(string sql, string? veritabani)
    {
        // ALTER hedefi bulma T-SQL ayrıştırıcısına dayanır; diğer motorlarda sessizce
        // boş dönüyordu (V3 denetimi). Yanlış güven vermemek için MSSQL'e sınırlandı —
        // diğer motorlarda tanım okuma zaten "Script: CREATE" ile elle yapılır.
        if (_profil is null || veritabani is null || _profil.Motor != MotorTuru.Mssql)
            return;
        foreach ((string sema, string ad) in SqlCozumleyici.AlterHedefleri(sql))
        {
            try
            {
                var nesne = new SemaNesnesi(veritabani, sema, ad, SemaNesneTuru.StoredProcedure, [], []);
                string? tanim = await _schemaService.TanimGetirAsync(_profil, nesne, CancellationToken.None);
                if (tanim is not null)
                    await TarihceyeIsleAsync(nesne, tanim, "alter-öncesi");
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                Serilog.Log.Warning(ex, "ALTER öncesi yedek alınamadı: {Sema}.{Ad}", sema, ad);
            }
        }
    }

    /// <summary>
    /// ALTER başarıyla çalıştıktan SONRA yeni tanımı tarihçeye işler (kullanıcı bulgusu
    /// 2026-07-19). <see cref="AlterOncesiYedekleAsync"/>'in eşi: o "önce"yi, bu "sonra"yı
    /// kaydeder — ikisi birlikte değişikliğin karşılaştırılabilir iki ucunu verir.
    /// Tarihçe asla akışı engellemez; hata yalnız günlüğe yazılır.
    /// </summary>
    private async Task AlterSonrasiIsleAsync(string sql, string? veritabani)
    {
        // ALTER hedefi bulma T-SQL ayrıştırıcısına dayanır (AlterOncesiYedekleAsync ile
        // aynı sınır): diğer motorlarda sessizce boş döner, yanlış güven vermeyelim.
        if (_profil is null || veritabani is null || _profil.Motor != MotorTuru.Mssql)
            return;

        foreach ((string sema, string ad) in SqlCozumleyici.AlterHedefleri(sql))
        {
            try
            {
                var nesne = new SemaNesnesi(veritabani, sema, ad, SemaNesneTuru.StoredProcedure, [], []);
                string? tanim = await _schemaService.TanimGetirAsync(_profil, nesne, CancellationToken.None);
                if (tanim is not null)
                    await TarihceyeIsleAsync(nesne, tanim, "alter-sonrası");
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                Serilog.Log.Warning(ex, "ALTER sonrası tanım işlenemedi: {Sema}.{Ad}", sema, ad);
            }
        }
    }

    public Task<IReadOnlyList<TarihceKaydi>> TarihceListesiAsync(SemaNesnesi nesne)
        => _profil is null
            ? Task.FromResult<IReadOnlyList<TarihceKaydi>>([])
            : _tarihceDeposu.ListeAsync(_profil.Id, _profil.Sunucu, nesne.Veritabani, nesne.Sema, nesne.Ad);

    /// <summary>
    /// Tarihçe boşken pencere "açılmıyor" gibi görünmesin (kullanıcı bulgusu 2026-07-17):
    /// güncel tanım İLK sürüm olarak kaydedilir, liste yeniden okunur. Tanım
    /// okunamazsa (şifreli vb.) boş döner — çağıran açıklayıcı mesaj gösterir.
    /// </summary>
    public async Task<IReadOnlyList<TarihceKaydi>> TarihceyiBaslatAsync(SemaNesnesi nesne)
    {
        if (_profil is null)
            return [];
        try
        {
            string? tanim = await _schemaService.TanimGetirAsync(_profil, nesne, CancellationToken.None);
            if (tanim is null)
                return [];
            await TarihceyeIsleAsync(nesne, tanim, "okuma");
            return await TarihceListesiAsync(nesne);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Durum = $"{nesne.TamAd} tanımı okunamadı: {ex.Message}";
            return [];
        }
    }

    /// <summary>FG-5.3 Script as CREATE (ham tanım) ya da DROP+CREATE.</summary>
    public async Task ScriptAsAcAsync(SemaNesnesi nesne, bool dropCreate)
    {
        if (_profil is null)
            return;
        Durum = $"{nesne.TamAd} tanımı alınıyor…";
        try
        {
            string? tanim = await _schemaService.TanimGetirAsync(_profil, nesne, CancellationToken.None);
            if (tanim is null)
            {
                Durum = $"{nesne.TamAd}: tanım okunamadı (şifreli olabilir).";
                return;
            }
            bool rozet = await TarihceyeIsleAsync(nesne, tanim, "okuma");
            SekmeAc(dropCreate ? $"DROP+CREATE {nesne.Ad}" : $"CREATE {nesne.Ad}",
                dropCreate ? NesneScriptleyici.DropVeCreate(nesne, tanim) : tanim, nesne.Veritabani);
            Durum = rozet ? $"⚠ {nesne.TamAd} son görüşünüzden beri DIŞARIDA değişmiş — tarihçeden karşılaştırabilirsiniz." : "Hazır";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Durum = $"{nesne.TamAd} script'i alınamadı: {ex.Message}";
        }
    }

    /// <summary>FG-5.4: tablo için CREATE TABLE script'i (kolon + PK + index).</summary>
    public async Task CreateTableAcAsync(SemaNesnesi nesne)
    {
        if (_profil is null)
            return;
        Durum = $"{nesne.TamAd} şeması okunuyor…";
        try
        {
            DuzenlemeMetasi meta = await _schemaService.DuzenlemeMetaAsync(_profil, nesne, CancellationToken.None);
            IReadOnlyList<MevcutIndex> indexler =
                await _teshisServisi.MevcutIndexlerAsync(_profil, nesne.Veritabani, CancellationToken.None);
            SekmeAc($"CREATE TABLE {nesne.Ad}",
                NesneScriptleyici.CreateTableScripti(meta, indexler), nesne.Veritabani);
            Durum = "Hazır";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Durum = $"CREATE TABLE üretilemedi: {ex.Message}";
        }
    }

    // --- Ctrl+P "her yere atla" paleti (V2-S10, FG-2.9) ---

    /// <param name="Kategori">"sekme" | "nesne" | "veritabanı" | "komut" — palette rozet olarak görünür.</param>
    public sealed record PaletOgesi(string Gosterim, string Kategori, Func<Task> Calistir);

    /// <summary>View bağlar: ⇄ Karşılaştır penceresini açar (pencere açma view işidir).</summary>
    public Action? KarsilastirAc { get; set; }

    /// <summary>Palet aday havuzu: açık sekmeler + yüklü nesneler + veritabanları + komutlar.</summary>
    public IReadOnlyList<PaletOgesi> PaletOgeleriKur()
    {
        var ogeler = new List<PaletOgesi>();

        foreach (ISekme sekme in Sekmeler)
        {
            ISekme s = sekme;
            ogeler.Add(new PaletOgesi(s.Baslik, "sekme", () => { SeciliSekme = s; return Task.CompletedTask; }));
        }

        foreach (string vt in VeritabaniAdlari)
        {
            string ad = vt;
            ogeler.Add(new PaletOgesi(ad, "veritabanı", () =>
            {
                if (SeciliSekme is SorguSekmesiViewModel sorgu)
                    sorgu.SecilenVeritabani = ad;
                return Task.CompletedTask;
            }));
        }

        foreach (SemaOnbellegi onbellek in _onbellekler.Values)
        {
            foreach (SemaNesnesi nesne in onbellek.Nesneler)
            {
                SemaNesnesi n = nesne;
                ogeler.Add(new PaletOgesi($"{n.TamAd}  ({n.Veritabani})", "nesne", () =>
                {
                    // Koleksiyon (Mongo) ve MSSQL dışı motorlarda T-SQL script üretilmez:
                    // ilk N satır/belge açılır (V3 denetimi — palet de menüyle aynı kapıdan geçer).
                    if (n.Tur == SemaNesneTuru.Koleksiyon || !MotorMssqlMu)
                        return IlkNSatirAsync(n);
                    if (n.Tur == SemaNesneTuru.Tablo)
                    {
                        SelectScriptiAc(n); // tablo: SELECT iskeleti (çalıştırmadan)
                        return Task.CompletedTask;
                    }
                    return NesneScriptiAcAsync(n); // SP/view/fonksiyon: ALTER script'i
                }));
            }
        }

        ogeler.Add(new PaletOgesi("Yeni sekme", "komut", () => { YeniSekme(); return Task.CompletedTask; }));
        if (TeshisGorunur) // şerit düğmesiyle aynı kapı — palet kaçak yolu olmasın (V3)
            ogeler.Add(new PaletOgesi("Yönetim Paneli", "komut", TeshisAcAsync));
        ogeler.Add(new PaletOgesi("Karşılaştır (sonuç diff / şema)", "komut", () => { KarsilastirAc?.Invoke(); return Task.CompletedTask; }));
        ogeler.Add(new PaletOgesi("Sorgu geçmişi panelini aç/kapat", "komut", GecmisiGizleGosterAsync));
        ogeler.Add(new PaletOgesi("Kapatılan sekmeyi geri aç", "komut", () => { KapatilanSekmeyiGeriAl(); return Task.CompletedTask; }));

        return ogeler;
    }

    /// <summary>
    /// Yönetim Paneli (V2-S7; V3'te motora göre dallanır): tek örnek — açıksa seçilir,
    /// yoksa açılıp yüklenir. MongoDB'de SQL Server DMV paneli yerine Mongo'nun kendi
    /// paneli açılır (dbStats/$collStats/$indexStats/currentOp — kullanıcı isteği 2026-07-18).
    /// </summary>
    [RelayCommand]
    public async Task TeshisAcAsync()
    {
        if (MotorMongoMu)
        {
            if (Sekmeler.OfType<MongoTeshisSekmesiViewModel>().FirstOrDefault() is { } acikMongo)
            {
                SeciliSekme = acikMongo;
                return;
            }

            var mongoSekme = new MongoTeshisSekmesiViewModel(_mongoTeshisServisi, () => _profil, VeritabaniAdlari, _gecmisDeposu)
            {
                SecilenVeritabani = _varsayilanVeritabani,
            };
            Sekmeler.Add(mongoSekme);
            SeciliSekme = mongoSekme;
            await mongoSekme.YenileAsync();
            return;
        }

        // MSSQL dışı SQL motorları: lehçenin bildirdiği bölümlerle genel panel (V3).
        if (!MotorMssqlMu && _profil is { } aktif)
        {
            if (Sekmeler.OfType<LehceTeshisSekmesiViewModel>().FirstOrDefault() is { } acikLehce)
            {
                SeciliSekme = acikLehce;
                return;
            }

            var lehceSekme = new LehceTeshisSekmesiViewModel(
                _executor, _lehceler.Getir(aktif.Motor), () => _profil, VeritabaniAdlari, _gecmisDeposu)
            {
                SecilenVeritabani = _varsayilanVeritabani,
            };
            Sekmeler.Add(lehceSekme);
            SeciliSekme = lehceSekme;
            await lehceSekme.YenileAsync();
            return;
        }

        if (Sekmeler.OfType<TeshisSekmesiViewModel>().FirstOrDefault() is { } acik)
        {
            SeciliSekme = acik;
            return;
        }

        var sekme = new TeshisSekmesiViewModel(_teshisServisi, () => _profil, VeritabaniAdlari, _gecmisDeposu)
        {
            SekmeyeAc = (baslik, sql) => SekmeAc(baslik, sql, veritabani: null),
            SecilenVeritabani = _varsayilanVeritabani,
        };
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
        await sekme.YenileAsync();
    }

    /// <summary>
    /// 🔍 Metin arama sekmesini açar (V5-S2). Aynı anda tek arama sekmesi tutulur — kullanıcı
    /// arka arkaya arama yapar, her arama için yeni sekme birikmesi istemez.
    /// </summary>
    [RelayCommand]
    public void MetinAramaAc()
    {
        if (_profil is null)
            return;

        if (Sekmeler.OfType<AramaSekmesiViewModel>().FirstOrDefault() is { } acik)
        {
            SeciliSekme = acik;
            return;
        }

        var sekme = new AramaSekmesiViewModel(
            _executor, () => _profil, _lehceler.Getir, VeritabaniAdlari, _varsayilanVeritabani,
            _mongoArayici);

        // Tanım YENİDEN SORGULANMAZ: arama sonucunda zaten tutuluyor. Açılan sekme aramanın
        // yapıldığı veritabanına bağlanır — varsayılana değil (kullanıcı bulgusu 2026-07-19).
        sekme.TanimiSekmedeAc = (ad, tanim, satir) =>
        {
            SorguSekmesiViewModel hedef = SekmeAc(ad, tanim, sekme.SecilenVeritabani);
            SatirdaAc?.Invoke(hedef, satir);
        };

        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
    }

    /// <summary>
    /// Görünüm köprüsü: yeni açılan sorgu sekmesinde imleci belirtilen satıra taşır. Editör
    /// o an henüz yüklenmemiş olabileceğinden erteleme MainWindow'un işidir (V5-S2).
    /// </summary>
    public Action<SorguSekmesiViewModel, int>? SatirdaAc { get; set; }

    /// <summary>
    /// 🎨 Görsel Sorgu Tasarımcısı sekmesini açar (v6-S1). Aynı anda tek tasarımcı sekmesi
    /// tutulur — kullanıcı ağaçtan tablo sürükleyerek üzerinde çalışır, her açışta yeni sekme
    /// birikmesi istemez (Metin Arama'daki desenin aynısı).
    /// </summary>
    [RelayCommand]
    public void GorselSorguAc()
    {
        if (_profil is null || _profil.Motor == MotorTuru.Mongo)
            return;

        SeciliSekme = GorselSekmeGetir();
    }

    /// <summary>
    /// 🔀 Karşılaştırma VM'ini kurar (v7-S1): sol = aktif profil, sağ = elle girilen (aynı motor)
    /// bağlantı; şema + veri farkı. Kullanıcı isteği 2026-08-09: sekme yerine AYRI PENCERE (SOAP
    /// istemcisi gibi) — ana pencere Karsilastir_Click bunu KarsilastirmaSekmePenceresi içinde açar
    /// (YukleAsync'i pencere çağırır). MongoDB'de/bağlantısızsa null (kıyas SQL'de anlamlı).
    /// </summary>
    public KarsilastirmaSekmesiViewModel? KarsilastirmaVmKur()
    {
        if (_profil is null || _profil.Motor == MotorTuru.Mongo)
            return null;

        return new KarsilastirmaSekmesiViewModel(
            _schemaService, _protector, _executor, _lehceler, () => _profil);
    }

    /// <summary>
    /// 🔎 Veri Arama sekmesini açar (kullanıcı isteği 2026-08-09: ayrı pencere yerine sekme —
    /// Kod Arama gibi). Aynı anda tek Veri Arama sekmesi tutulur.
    /// </summary>
    public void VeriAramaAc()
    {
        if (_profil is not { } profil)
            return;

        if (Sekmeler.OfType<VeriAramaSekmesiViewModel>().FirstOrDefault() is { } acik)
        {
            SeciliSekme = acik;
            return;
        }

        string? aktifDb = (SeciliSekme as SorguSekmesiViewModel)?.SecilenVeritabani ?? _varsayilanVeritabani;
        var sekme = new VeriAramaSekmesiViewModel(
            [.. VeritabaniAdlari], aktifDb, _lehceler.Getir(profil.Motor),
            db => OnbellekGetirAsync(db),
            (db, sql, sinirli, ct) => VeriAramaSorgusuAsync(db, sql, sinirli, ct), // kapsam seçiliyse sınırsız
            (baslik, sql, db) => SekmeAcVeCalistir(baslik, sql, db));
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
    }

    /// <summary>
    /// 🧾 Log Analizi sekmesini açar (kullanıcı isteği 2026-08-09: ayrı pencere yerine sekme).
    /// Aynı anda tek Log Analizi sekmesi tutulur. Mongo dahil (orada "tablo"=koleksiyon).
    /// </summary>
    public void LogAnalizAc()
    {
        if (_profil is not { } profil)
            return;

        if (Sekmeler.OfType<LogAnalizSekmesiViewModel>().FirstOrDefault() is { } acik)
        {
            SeciliSekme = acik;
            return;
        }

        var sekme = new LogAnalizSekmesiViewModel(
            profil.Motor, LogAnalizVeritabanlari, LogAnalizAktifVeritabani,
            LogAnalizTablolariAsync, LogAnalizCalistirAsync,   // v20-S6: sınırsız süre + ⏹ Durdur (ct)
            istem => AsistanSorAsync(istem),                    // v11-S6: 🤖 Özetle köprüsü
            LogSonSecimAsync, LogSecimKaydet);                  // v20-S3: son seçim hatırlama
        // v22-S1 (saha turu-2 m.2): Mongo'da strateji index'e göre seçilir — zaman alanı index'siz
        // iken 24 saat süzgeci TÜM koleksiyonu tarıyordu (ölçüm: 600k belgede 961 ms ↔ 11 ms).
        if (profil.Motor == MotorTuru.Mongo)
            sekme.MongoIndexAlanlari = (db, koleksiyon) =>
                MongoIndexAlanlariAsync(db, koleksiyon, CancellationToken.None);
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
        _ = sekme.IlkYukleAsync(); // pencere Loaded karşılığı: son seçim → tablolar
    }

    /// <summary>🔍 Profiler sekmesini açar (v23-S1) — tek örnek; yalnız MSSQL (ProfilerGorunur).</summary>
    public void ProfilerAc()
    {
        if (_profil is null || !MotorMssqlMu)
            return;
        if (Sekmeler.OfType<ProfilerSekmesiViewModel>().FirstOrDefault() is { } acik)
        {
            SeciliSekme = acik;
            return;
        }
        var sekme = new ProfilerSekmesiViewModel(LogAnalizVeritabanlari, ProfilerCalistirAsync,
            istem => AsistanSorAsync(istem)); // v23-S4: 🤖 yorum köprüsü (LogAnaliz ile aynı)
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
    }

    /// <summary>🔎 FTS sekmesini açar (v23 S1+S2) — tek örnek; yalnız MSSQL (FtsGorunur).</summary>
    public void FtsAc()
    {
        if (_profil is null || !MotorMssqlMu)
            return;
        if (Sekmeler.OfType<FtsSekmesiViewModel>().FirstOrDefault() is { } acik)
        {
            SeciliSekme = acik;
            return;
        }
        var sekme = new FtsSekmesiViewModel(
            [.. VeritabaniAdlari], LogAnalizAktifVeritabani,
            db => OnbellekGetirAsync(db), FtsSorguAsync,
            (baslik, sql, db) => SekmeAc(baslik, sql, db),              // DDL — çalıştırmadan
            (baslik, sql, db) => SekmeAcVeCalistir(baslik, sql, db));   // arama — koşarak
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
        _ = sekme.YenileAsync(); // açılışta keşif (FTS kurulu mu + envanter)
    }

    /// <summary>FTS köprüsü: keşif/metadata sorguları — seçilen DB'de, 30 sn tavan + 1.000 satır
    /// (envanter küçüktür; tavanlar donma kalkanı).</summary>
    public Task<QueryResult> FtsSorguAsync(string sql, string? veritabani, CancellationToken ct)
        => _profil is null
            ? Task.FromResult(new QueryResult { Hata = new SqlHata("Bağlantı yok.", 0, 0, 0) })
            : _executor.ExecuteAsync(_profil, sql,
                new ExecuteOptions
                {
                    VeritabaniOverride = veritabani,
                    SatirSiniri = 1_000,
                    KomutTimeoutSnOverride = 30,
                }, ct);

    /// <summary>
    /// Profiler köprüsü (v23-S1): oturum DDL'leri + ring_buffer okuması SUNUCU düzeyidir —
    /// veritabanı override YOK (profilin varsayılanında koşar). 30 sn tavan: bunlar kısa
    /// sorgulardır, asılırsa sonsuza dek beklenmesin; okuma tek satır döndürür (XML metni tek
    /// hücre — SatirSiniri'ne takılmaz).
    /// </summary>
    public Task<QueryResult> ProfilerCalistirAsync(string sql, CancellationToken ct)
        => _profil is null
            ? Task.FromResult(new QueryResult { Hata = new SqlHata("Bağlantı yok.", 0, 0, 0) })
            : _executor.ExecuteAsync(_profil, sql,
                new ExecuteOptions { KomutTimeoutSnOverride = 30 }, ct);

    /// <summary>
    /// Görsel Sorgu sekmesini döndürür — açıksa var olanı, yoksa oluşturup ekler (henüz seçmeden).
    /// <see cref="GorselSorguAc"/> ve Script→Görsel köprüsü aynı tek-örnek sekmeyi paylaşır.
    /// </summary>
    private GorselSorguSekmesiViewModel GorselSekmeGetir()
    {
        if (Sekmeler.OfType<GorselSorguSekmesiViewModel>().FirstOrDefault() is { } acik)
            return acik;

        // Lehçe üretim ANINDA çözülür (o an aktif profilin motoruna göre tırnaklama).
        // FK getirici: bağ kurarken ON kolonlarını önermek için (S2). Profil yoksa boş.
        var sekme = new GorselSorguSekmesiViewModel(
            () => _profil is { } p && p.Motor != MotorTuru.Mongo ? _lehceler.Getir(p.Motor) : null,
            (db, ct) => _profil is { } p && p.Motor != MotorTuru.Mongo
                ? _schemaService.YabanciAnahtarlarAsync(p, db, ct)
                : Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]));

        // 🎯 DB seçimi (kullanıcı bulgusu 2026-07-31): görsel sorgu SABİT varsayılan DB'de koşuyordu —
        // kullanıcı Mersis'te çalışırken ilk DB'ye gidip "Invalid object name" alıyordu. Artık sekmenin
        // KENDİ seçimi kullanılır; açılış varsayılanı AKTİF sorgu sekmesinin DB'sidir. Köprüler nesne
        // kurulduktan SONRA bağlanır — sekme kendi SecilenVeritabani'sına başvurur (self-ref).
        sekme.Veritabanlari = VeritabaniAdlari;
        sekme.SecilenVeritabani =
            (SeciliSekme as SorguSekmesiViewModel)?.SecilenVeritabani ?? _varsayilanVeritabani;
        sekme.SekmeyeAc = (baslik, sql) => SekmeAc(baslik, sql, sekme.SecilenVeritabani ?? _varsayilanVeritabani);
        sekme.SekmeyeAcVeCalistir = (baslik, sql) => SekmeAcVeCalistir(baslik, sql, sekme.SecilenVeritabani ?? _varsayilanVeritabani);
        // Yerinde sonuç (2026-07-23): SELECT stateless yürütücüde koşar, sonuç görsel sekmede
        // gösterilir — script sekmesi açılmaz. Satır + bellek sınırları varsayılan.
        sekme.SorguCalistirici = (sql, ct) => _profil is { } p
            ? _executor.ExecuteAsync(p, sql,
                new ExecuteOptions { VeritabaniOverride = sekme.SecilenVeritabani ?? _varsayilanVeritabani }, ct)
            : Task.FromResult(new QueryResult { Hata = new SqlHata("Bağlantı yok.", 0, 0, 0) });
        Sekmeler.Add(sekme);
        return sekme;
    }

    /// <summary>
    /// 🗺 Veritabanı Haritası (v9): aktif veritabanının şemasını + FK'lerini okuyup ER haritasını
    /// kurar (tablolar kart, FK yumuşak bağ). Tek örnek; her açılışta güncel şemayla yeniden yüklenir.
    /// <b>MongoDB'de</b> koleksiyonlar bağsız kart olur (FK yok — v9-S5). Hiçbir şey çalıştırmaz.
    /// </summary>
    [RelayCommand]
    private async Task HaritaAc()
    {
        if (_profil is null)
            return;

        bool mongo = _profil.Motor == MotorTuru.Mongo;
        // v19-S10 (canlı test 2026-08-04): harita hep bağlantı varsayılanıyla (listedeki ilk DB)
        // açılıyordu — AKTİF sekmenin veritabanıyla açılır (log analizi/yeni sekme davranışıyla
        // tutarlı). Harita sekmesine GEÇMEDEN önce okunmalı: SeciliSekme harita olunca aktif
        // sorgu sekmesinin veritabanı bilgisine erişilemez.
        string? db = LogAnalizAktifVeritabani;
        HaritaSekmesiViewModel sekme = Sekmeler.OfType<HaritaSekmesiViewModel>().FirstOrDefault() ?? YeniHarita();
        SeciliSekme = sekme;
        sekme.Bilgi = "Şema okunuyor…";
        SemaOnbellegi? onbellek = await OnbellekGetirAsync(db);
        if (onbellek is null)
        {
            sekme.Bilgi = "Şema okunamadı (bağlantı yok ya da erişim reddedildi).";
            return;
        }

        IReadOnlyList<YabanciAnahtar> fkler = [];
        try
        {
            if (db is not null && !mongo) // MongoDB'de FK yok — sorulmaz
                fkler = await _schemaService.YabanciAnahtarlarAsync(_profil, db, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // FK okunamadı (yetki/eski sunucu) → harita FK'siz kurulur; tablolar yine görünür.
        }

        // Kayıtlı konumlar (DB başına, v9-S3): varsa kullanıcının dizdiği gibi gelir, yoksa ızgara.
        IReadOnlyDictionary<string, Nokta>? kayitli = null;
        if (db is not null)
        {
            string? json = await _ayarDeposu.OkuAsync(HaritaKonumAnahtari(db), _profil.Id);
            if (json is not null)
                kayitli = HaritaKonumSerisi.Coz(json);
        }

        sekme.Yukle(HaritaKurucu.Kur(onbellek.Nesneler, fkler), kayitli, db ?? "");

        if (mongo)
            sekme.Bilgi = $"{onbellek.Nesneler.Count} koleksiyon · ilişki yok (MongoDB'de FK yoktur). "
                + "Kartları sürükleyin, arayın, PNG/SVG olarak dışa aktarın.";

        if (db is not null)
        {
            Guid profilId = _profil.Id;
            string anahtar = HaritaKonumAnahtari(db);
            sekme.KonumlariKaydetIstendi =
                () => _ = _ayarDeposu.YazAsync(anahtar, HaritaKonumSerisi.Serile(sekme.KonumSozlugu()), profilId);
        }
        // v6 köprüsü yalnız SQL ailesinde anlamlı (Görsel Sorgu Mongo'da yok).
        sekme.GorselleGonder = mongo ? null : (tamAdlar => HaritadanGorsele(tamAdlar, onbellek));
    }

    private static string HaritaKonumAnahtari(string db) => $"harita.konum.{db}";

    /// <summary>Haritada seçili tabloları Görsel Sorgu (v6) tuvaline ekler ve o sekmeye geçer.</summary>
    private void HaritadanGorsele(IReadOnlyList<string> tamAdlar, SemaOnbellegi onbellek)
    {
        GorselSorguSekmesiViewModel gorsel = GorselSekmeGetir();
        double x = 24, y = 24;
        int i = 0;
        foreach (string tamAd in tamAdlar)
        {
            if (onbellek.Nesneler.FirstOrDefault(
                    n => string.Equals(n.TamAd, tamAd, StringComparison.OrdinalIgnoreCase)) is not { } nesne)
                continue;
            gorsel.TabloEkle(nesne, x, y);
            if (++i % 3 == 0) { x = 24; y += 210; } else x += 260;
        }
        SeciliSekme = gorsel;
    }

    private HaritaSekmesiViewModel YeniHarita()
    {
        var sekme = new HaritaSekmesiViewModel();
        Sekmeler.Add(sekme);
        return sekme;
    }

    /// <summary>
    /// v20-S4 (kullanıcı isteği 2026-08-05: "görsel sorguya in özelliği"): nesne gezgininden bir
    /// tabloyu Görsel Sorgu tasarımcısına "indirir" — sürükle-bırakın menü karşılığı. Var olan görsel
    /// sekme varsa ona ekler, yoksa kurar; yeni kutuyu tuvaldeki mevcut kutu sayısına göre kaydırarak
    /// yerleştirir (üst üste binmesin) ve sekmeyi öne getirir. Yalnız SQL Server'da (görsel sorgu kapısı).
    /// </summary>
    public void GorseleTabloEkle(SemaNesnesi nesne)
    {
        if (!GorselSorguGorunur)
            return;
        GorselSorguSekmesiViewModel gorsel = GorselSekmeGetir();
        int n = gorsel.Kutular.Count;
        gorsel.TabloEkle(nesne, 24 + n % 3 * 260, 24 + n / 3 * 210);
        SeciliSekme = gorsel;
    }

    /// <summary>
    /// Script → Görsel (v6-S5): aktif sorgu sekmesindeki SELECT'i ayrıştırıp Görsel Sorgu
    /// tuvaline döker (tersine yön). ScriptDom T-SQL olduğundan YALNIZ SQL Server'da çalışır
    /// (şeritte de yalnız o zaman görünür). Tablolar sekmenin veritabanının şema önbelleğinden
    /// çözülür; bulunamayanlar atlanıp kullanıcıya bildirilir.
    /// </summary>
    [RelayCommand]
    public async Task ScriptiGorseleCevirAsync()
    {
        if (!MotorMssqlMu || SeciliSekme is not SorguSekmesiViewModel sorgu)
            return;

        string metin = sorgu.Belge.Text?.Trim() ?? "";
        if (metin.Length == 0)
        {
            Durum = "Görsele çevrilecek bir sorgu yok (sekme boş).";
            return;
        }

        CozumlenmisSorgu? cozum = GorselSorguCozumleyici.Coz(metin, out string hata);
        if (cozum is null)
        {
            Durum = $"Görsele çevrilemedi: {hata}";
            return;
        }

        // Sorgunun bazı parçaları görsel modelde TEMSİL EDİLEMİYORSA (alt sorgu, GROUP BY, karmaşık
        // ON…) sessizce basitleştirmek görsel sorgunun ORİJİNALDEN FARKLI sonuç vermesine yol açar
        // (kullanıcı bulgusu 2026-07-21). Bunu yapmadan önce ne atlanacağını açıkça gösterip SOR.
        if (cozum.Uyarilar.Count > 0)
        {
            string mesaj = "Bu sorgunun bazı parçaları Görsel Tasarımcı'da temsil EDİLEMİYOR ve "
                + "atlanacak. Görsel sorgu ORİJİNALDEN FARKLI sonuç verebilir:\n\n• "
                + string.Join("\n• ", cozum.Uyarilar.Distinct())
                + "\n\nYine de görsele çevrilsin mi?";
            if (GorseleCevirmeOnayiIste?.Invoke(mesaj) != true)
            {
                Durum = "Görsele çevirme iptal edildi (sorgu tam temsil edilemiyor).";
                return;
            }
        }

        // Tabloları çözmek için sekmenin veritabanının şeması gerekir (yoksa yüklenir).
        string? veritabani = sorgu.SecilenVeritabani ?? _varsayilanVeritabani;
        SemaOnbellegi? onbellek = await OnbellekGetirAsync(veritabani);
        IReadOnlyList<SemaNesnesi> nesneler = onbellek?.Nesneler ?? [];

        GorselSorguSekmesiViewModel gorsel = GorselSekmeGetir();
        gorsel.TuvaliKur(cozum, ct => SemaNesnesiBul(nesneler, ct));
        SeciliSekme = gorsel;
        Durum = gorsel.Bilgi;
    }

    /// <summary>
    /// Script → Görsel çevriminde kayıp olacaksa (temsil edilemeyen parçalar) kullanıcıya onay
    /// penceresi açan görünüm köprüsü (MessageBox) — MainWindow bağlar. Yes → çevir, No → iptal.
    /// </summary>
    public Func<string, bool>? GorseleCevirmeOnayiIste { get; set; }

    /// <summary>Ayrıştırılmış bir tabloyu (şema opsiyonel) şema önbelleğindeki nesneye eşler.</summary>
    private static SemaNesnesi? SemaNesnesiBul(IReadOnlyList<SemaNesnesi> nesneler, CozumlenmisTablo t)
        => nesneler.FirstOrDefault(n =>
               string.Equals(n.Ad, t.Ad, StringComparison.OrdinalIgnoreCase)
               && (t.Sema is null || string.Equals(n.Sema, t.Sema, StringComparison.OrdinalIgnoreCase)));

    // --- Sekmeler (S3, FG-3.1; V2-S5'ten beri iki tür: sorgu + düzenleme) ---
    public ObservableCollection<ISekme> Sekmeler { get; } = [];
    public ObservableCollection<string> VeritabaniAdlari { get; } = [];
    [ObservableProperty] private ISekme? _seciliSekme;

    /// <summary>Son seçili SORGU sekmesi — Asistan "editördeki sorgu"yu buradan alır (v11-S1):
    /// asistan sekmesi seçiliyken SeciliSekme sorgu değildir, kullanıcının kastı son çalıştığıdır.</summary>
    private SorguSekmesiViewModel? _sonSorguSekmesi;

    /// <summary>Son seçili PLAN sekmesi — Asistan "Planı yorumlat" (v11-S5b) bunu metinleştirir.</summary>
    private PlanSekmesiViewModel? _sonPlanSekmesi;

    partial void OnSeciliSekmeChanged(ISekme? value)
    {
        if (value is SorguSekmesiViewModel sorgu)
            _sonSorguSekmesi = sorgu;
        if (value is PlanSekmesiViewModel plan)
            _sonPlanSekmesi = plan;
    }

    [RelayCommand]
    public void YeniSekme()
    {
        // Yeni sekme "SQLST{N}" (kullanıcı isteği 2026-07-23): markalı + sıralı. otomatikAd=false →
        // içerik-otomatik-adı KAPALI; ad kaydedilene kadar sabit kalır (kaydedince dosya adını alır).
        SorguSekmesiViewModel sekme = SekmeKur($"SQLST{++_sekmeSayaci}", otomatikAd: false);
        // v19-S6 (kullanıcı bulgusu 2026-08-03): yeni sekme AKTİF sekmenin veritabanıyla açılır —
        // "bir veritabanında çalışırken yeni sekme en üsttekine dönüyordu, her seferinde geri
        // seçmek zordu". Aktif sorgu sekmesi yoksa (ilk sekme) varsayılana düşer.
        sekme.SecilenVeritabani =
            (SeciliSekme as SorguSekmesiViewModel)?.SecilenVeritabani ?? _varsayilanVeritabani;
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
    }

    /// <summary>Sekme + V2-S2/S4 kablolaması tek yerde: geçmiş, bildirim, güvenli yazma, onay köprüsü.</summary>
    private SorguSekmesiViewModel SekmeKur(string baslik, bool otomatikAd)
    {
        var sekme = new SorguSekmesiViewModel(
            _queryService, _oturumFabrikasi, _lehceler, () => _profil, () => KirliOkuma,
            () => GuvenliYazma, () => GuvenliYazmaRollbackSn, VeritabaniAdlari, baslik)
        {
            BaslikOtomatikMi = otomatikAd,
        };
        sekme.CalistirmaTamamlandi += CalistirmaBitti;
        sekme.OnayIste = mesaj => YazmaOnayiIste?.Invoke(mesaj) ?? true;
        sekme.AlterOncesiYedekle = sql => AlterOncesiYedekleAsync(sql, sekme.SecilenVeritabani); // FG-5.5
        sekme.GeriAlDeposu = GeriAlDeposu;
        sekme.OnbellekGetir = db => OnbellekGetirAsync(db); // m.23: PK'ya varsayılan görüntü sıralaması // ⏪ V15-S3: Güvenli Yazma COMMIT'inde eski satırlar paketlenir
        return sekme;
    }

    /// <summary>⏪ Geri Al paket deposu (V15-S3, BF-1) — MainWindow bağlar; null → özellik pasif.</summary>
    public IGeriAlDeposu? GeriAlDeposu { get; set; }

    // --- Güvenli Yazma Modu (V2-S4, Ö1) — kullanıcı isterse açar, tercih kalıcıdır ---

    /// <summary>Açıkken yazma sorguları açık TRAN'da çalışır, COMMIT/ROLLBACK kararı kullanıcıya kalır.</summary>
    [ObservableProperty] private bool _guvenliYazma;

    /// <summary>
    /// Aktif profil SQL Server mı? (V3-S1) Güvenli Yazma, NOLOCK ve Yönetim Paneli T-SQL'e /
    /// SQL Server DMV'lerine özgüdür — diğer motorlarda bu düğmeler ARAYÜZDEN GİZLENİR
    /// (kullanıcı kararı 2026-07-18: "yapamıyorsak kapatmamız lazım"). Motor-özgü karşılıkları
    /// roadmap'te borç olarak kayıtlı.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BicimlendirGorunur))]
    [NotifyPropertyChangedFor(nameof(TeshisGorunur))]
    [NotifyPropertyChangedFor(nameof(ProfilerGorunur))]
    [NotifyPropertyChangedFor(nameof(FtsGorunur))]
    private bool _motorMssqlMu = true;

    /// <summary>Bağlı bir profil var mı (v22-S3: motor farkı gözetmeyen menü öğeleri için —
    /// ör. 👁 Önizleme hem SQL ailesinde hem MongoDB'de anlamlıdır).</summary>
    public bool BaglantiVar => _profil is not null;

    /// <summary>Aktif profil MongoDB mi? (JSON biçimlendirici + Mongo yönetim paneli için)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BicimlendirGorunur))]
    [NotifyPropertyChangedFor(nameof(TeshisGorunur))]
    [NotifyPropertyChangedFor(nameof(TopluAktarGorunur))]
    [NotifyPropertyChangedFor(nameof(LogAnalizGorunur))]
    private bool _motorMongoMu;

    /// <summary>
    /// Script üretimli özellik üçlüsünün (🔎 Veri Arama · 🕸 Kayıt Haritası · INSERT örneği)
    /// motor kapısı — özellik eşitliği (kullanıcı onayı 2026-08-03): SQL ailesinin DÖRDÜ.
    /// Üreticiler ILehce ile motor-parametriktir; Oracle madde 3'te eklendi (VARCHAR2/NUMBER tip
    /// eşlemeleri + SYSDATE/SYSTIMESTAMP + FETCH FIRST; CLOB '=' kıyası bilinçli dışarıda).
    /// </summary>
    [ObservableProperty]
    private bool _motorScriptDestekli = true;

    /// <summary>🔗 Bağımlılık Ağacı kapısı (2026-08-03): SQL ailesinin DÖRDÜ — MSSQL
    /// (sys.sql_expression_dependencies) · PostgreSQL (pg_depend + gövde ~) · MySQL
    /// (VIEW_TABLE_USAGE 8.0.13+ + ROUTINES ~) · Oracle (ALL_DEPENDENCIES, kesin).</summary>
    [ObservableProperty]
    private bool _motorBagimlilikDestekli = true;

    /// <summary>
    /// Log analizi düğmesi: BAĞLI her motorda görünür (kullanıcı 2026-07-25: "Mongo'ya girince log
    /// analizi yok, orada da olmalı"). Mongo'da "tablo" = koleksiyon, "kolon" = alan; sorgu SQL yerine
    /// find/aggregate JSON'dur (<see cref="LogTabloAnaliz"/> motora göre üretir).
    /// </summary>
    public bool LogAnalizGorunur => _profil is not null;

    /// <summary>
    /// "⬇ Tümünü dışa aktar" düğmesi: SQL ailesinde görünür (v6). MongoDB'de HİÇ görünmez —
    /// dinamik şema (belge başına farklı alanlar) CSV kolonlarını ayrı bir tasarım sorunu yapar.
    /// </summary>
    public bool TopluAktarGorunur => !MotorMongoMu;

    /// <summary>
    /// Yönetim Paneli düğmesi: HER motorda görünür ve O MOTORUN panelini açar (V3 kuralı) —
    /// SQL Server kendi DMV paneli, MongoDB koleksiyon/index paneli, PostgreSQL/MySQL/Oracle
    /// lehçelerinin bildirdiği bölümlerle genel panel. Bölüm bildirmeyen motorda gizlenir.
    /// </summary>
    public bool TeshisGorunur => MotorMssqlMu || MotorMongoMu || _lehceBolumVar;

    /// <summary>🔍 Profiler düğmesi (v23-S1, K1 kararı): YALNIZ MSSQL — izleme Extended Events
    /// üstünde; diğer motorların karşılığı (pg_stat_activity/performance_schema/currentOp) ayrı
    /// iş, o güne dek düğme gizli (yarım özellik yok kuralı). Bağlantı da şart.</summary>
    public bool ProfilerGorunur => MotorMssqlMu && _profil is not null;

    /// <summary>🔎 FTS düğmesi (v23, K1 kararı): YALNIZ MSSQL — diğer motorların tam metin
    /// mekanizmaları (tsvector/MATCH/Oracle Text) bambaşka, ayrı dilim. Bağlantı da şart.
    /// Sunucuda FTS bileşeni yoksa düğme yine görünür — sekme Türkçe yol gösterir
    /// (profil sunucusu değişebilir; gizlemek "neden yok?" sorusu bırakırdı).</summary>
    public bool FtsGorunur => MotorMssqlMu && _profil is not null;

    private bool _lehceBolumVar;

    /// <summary>
    /// "✏ Düzenle" menüsü: Edit modunu DESTEKLEYEN motorlarda görünür (V4-S1). SQL ailesinin
    /// dördü de destekler; MongoDB desteklemez — belge düzenleme ayrı bir tasarımdır
    /// (şemasız belge, iç içe alan, <c>_id</c> dışında satır kimliği yok). "Yarım özellik yok"
    /// ilkesi: desteklenmeyen motorda öğe gri değil, HİÇ görünmez.
    /// </summary>
    public bool DuzenlemeGorunur => _duzenlemeDestekli;

    private bool _duzenlemeDestekli;

    /// <summary>
    /// 🛡 Güvenli Yazma anahtarı: modu DESTEKLEYEN motorlarda görünür (V4-S2). SQL ailesinin
    /// dördü de destekler (MSSQL/PG'de DDL dahil her yazma, MySQL/Oracle'da DML — orada DDL
    /// örtük COMMIT yaptığından bant açılmaz, kullanıcıya nedeni yazılır). MongoDB'de yoktur:
    /// çok-belgeli işlem replica set ister, tek düğümlü kurulumda söz tutulamaz.
    /// </summary>
    public bool GuvenliYazmaGorunur => _guvenliYazmaDestekli;

    private bool _guvenliYazmaDestekli;

    /// <summary>
    /// Execution plan düğmeleri: planı DESTEKLEYEN motorlarda görünür (V5-S1). Şimdilik yalnız
    /// SQL Server — PostgreSQL/MySQL/Oracle/MongoDB adım adım eklenecek. "Yarım özellik yok":
    /// desteklenmeyen motorda düğme gri değil, HİÇ görünmez.
    /// </summary>
    public bool PlanGorunur => _planDestekli;

    private bool _planDestekli;

    /// <summary>
    /// 🎨 Görsel Sorgu düğmesi: SQL ailesinde görünür (v6). MongoDB'de HİÇ görünmez — orada
    /// JOIN yerine <c>$lookup</c>/aggregation pipeline vardır ve görsel tasarımı ayrı bir
    /// problemdir ("yarım özellik yok"). Profil yokken de gizli.
    /// </summary>
    public bool GorselSorguGorunur => _gorselSorguDestekli;

    /// <summary>🗺 Harita düğmesi: BAĞLI her motorda görünür (SQL ailesi + MongoDB koleksiyon kartları).</summary>
    public bool HaritaGorunur => _profil is not null;

    /// <summary>Aktif profilin lehçesi (SQL ailesi); Mongo/profil yoksa null. Tablo sihirbazı kullanır.</summary>
    public ILehce? AktifLehce => _profil is { } p && p.Motor != MotorTuru.Mongo ? _lehceler.Getir(p.Motor) : null;

    /// <summary>Varsayılan veritabanı (ağaç bağlamı çözemezse tablo sihirbazı buna düşer).</summary>
    public string? VarsayilanVeritabani => _varsayilanVeritabani;

    /// <summary>Aktif bağlantı profili (📤 Şema Kopyalama penceresi kaynak olarak kullanır — 2026-07-31).</summary>
    public ConnectionProfile? AktifProfil => _profil;

    private bool _gorselSorguDestekli;

    /// <summary>
    /// Plan düğmesinin yazısı — motora göre değişir (kullanıcı kararı 2026-07-19).
    /// Her motorda TEK düğme vardır; ikinci bir "tahmini/gerçek" seçeneği kullanıcıya
    /// sunulmaz. Ölçümlü planı verebilen motorlarda "Execution plan"; yalnız tahmin
    /// üretebilen MySQL/Oracle'da bunu SAKLAMADAN "Tahmini execution plan" yazar.
    /// </summary>
    public string PlanDugmeBasligi => _gercekPlanDestekli ? "Plan" : "Tahmini plan";

    private bool _gercekPlanDestekli;

    /// <summary>
    /// 🔍 Metin arama düğmesi (V5-S2): nesne TANIMI kavramı olan motorlarda görünür — SQL
    /// ailesinin dördü. MongoDB'de HİÇ görünmez: koleksiyonların tanım metni yoktur
    /// (<c>MongoSchemaService.TanimGetirAsync</c> null döner) ve Mongo <see cref="ILehce"/>
    /// uygulamadığından <c>MetinAramaSorgusu</c> karşılığı da yoktur. "Yarım özellik yok".
    /// </summary>
    public bool AramaGorunur => _aramaDestekli;

    private bool _aramaDestekli;

    /// <summary>
    /// Biçimlendir düğmesi yalnız biçimlendiricisi OLAN motorlarda görünür:
    /// MSSQL (T-SQL/ScriptDom) ve MongoDB (JSON). PostgreSQL/MySQL/Oracle'da gizli —
    /// T-SQL çözümleyicisine gidip "söz dizimi hatası" vermesin (kullanıcı bulgusu 2026-07-18).
    /// </summary>
    public bool BicimlendirGorunur => MotorMssqlMu || MotorMongoMu;

    /// <summary>Karar beklerken otomatik ROLLBACK süresi, sn (ayar deposundan; varsayılan 300).</summary>
    public int GuvenliYazmaRollbackSn { get; private set; } = 300;

    /// <summary>WHERE'siz DML onayı için görünüm köprüsü (MessageBox) — MainWindow bağlar.</summary>
    public Func<string, bool>? YazmaOnayiIste { get; set; }

    partial void OnGuvenliYazmaChanged(bool value)
    {
        if (!_ayarlarYukleniyor)
            _ = _ayarDeposu.YazAsync(AyarAnahtari.GuvenliYazmaAcik, value ? "1" : "0", _profil?.Id);
    }

    /// <summary>Karanlık tema (V2-S10): palet takası canlı işler, tercih kalıcıdır.</summary>
    [ObservableProperty] private bool _koyuTema;

    partial void OnKoyuTemaChanged(bool value)
    {
        App.TemaUygula(value);
        if (!_ayarlarYukleniyor)
            _ = _ayarDeposu.YazAsync(AyarAnahtari.KoyuTema, value ? "1" : "0");
    }

    [RelayCommand]
    public void TemaDegistir() => KoyuTema = !KoyuTema;

    /// <summary>
    /// v19-S9 (kullanıcı isteği 2026-08-03): tema butonu açılır menüsünden palet seçimi —
    /// kip başına 5 varyant (indigo · grafit · slate · petrol · amber). Seçim o kipe geçer,
    /// varyantı uygular ve tercihi kalıcılaştırır. YERLEŞİM DEĞİŞMEZ — yalnız renk tonları.
    /// </summary>
    public void TemaPaletiSec(bool koyu, string palet)
    {
        if (koyu)
            App.SeciliKoyuPalet = palet;
        else
            App.SeciliAcikPalet = palet;
        _ = _ayarDeposu.YazAsync(koyu ? AyarAnahtari.KoyuPalet : AyarAnahtari.AcikPalet, palet);

        if (KoyuTema != koyu)
            KoyuTema = koyu;      // kip değişimi TemaUygula'yı zaten tetikler (yeni varyantla)
        else
            App.TemaUygula(koyu); // aynı kipte yalnız varyant değişti — canlı takas
    }

    [RelayCommand]
    public void SekmeKapat(ISekme? sekme)
    {
        sekme ??= SeciliSekme;
        if (sekme is null)
            return;
        // 📌 Sabit sekme kapatılamaz (v20-S21 saha m.13) — ✕ zaten gizli ama Ctrl+W /
        // "Tümünü kapat" da buradan geçer; koruma tek yerde durur.
        if (sekme.Sabit)
            return;
        // Geri alma yığını (FG-3.6): son 10 kapatılan SORGU sekmesi Ctrl+Shift+T ile geri gelir
        // (düzenleme sekmesi tablo bağlıdır — menüden yeniden açılır).
        if (sekme is SorguSekmesiViewModel sorgu)
        {
            _kapatilanlar.Push(SekmeyiYansit(sorgu));
            if (_kapatilanlar.Count > 10)
                _kapatilanlar = new Stack<OturumSekmeKaydi>(_kapatilanlar.Reverse().Skip(_kapatilanlar.Count - 10));
        }
        // v19-S11 (canlı test 2026-08-04 "sekme donuyor gibi geç kapanıyor"): sekme ÖNCE
        // koleksiyondan çıkarılır (UI anında kapanır), temizlik SONRA koşar — eski sırada
        // KapatAsync'in senkron başı (sonuçları boşaltma) sekme hâlâ ekrandayken görünür
        // grid'i çözüyor, boşuna render tetikliyordu.
        int i = Sekmeler.IndexOf(sekme);
        Sekmeler.Remove(sekme);
        SeciliSekme ??= Sekmeler.Count > 0 ? Sekmeler[Math.Min(i, Sekmeler.Count - 1)] : null;
        _ = sekme.KapatAsync(); // iptal + kalıcı oturumu bırak (açık TRAN rollback)
    }

    /// <summary>Araç sekmesi aç ya da açıksa ona geç (m.22 devamı — LINQ⇄SQL / SP Sihirbazı):
    /// aynı araç ikinci kez istenince kopya sekme açılmaz, mevcut seçilir.</summary>
    public void AracSekmesiAcVeyaSec(string baslik, Func<System.Windows.Window> pencereYap)
    {
        if (Sekmeler.FirstOrDefault(s => s is AracSekmesiViewModel a && a.Baslik == baslik) is { } acik)
        {
            SeciliSekme = acik;
            return;
        }
        var sekme = new AracSekmesiViewModel(baslik, pencereYap());
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
    }

    /// <summary>📌 Sekme sabitle / sabitliği kaldır (v20-S21 saha m.13, sağ tık menüsü).</summary>
    [RelayCommand]
    public void SekmeSabitDegistir(ISekme? sekme)
    {
        if (sekme is not null)
            sekme.Sabit = !sekme.Sabit;
    }

    /// <summary>Sağ tık → "Diğerlerini kapat" (v20-S21 saha m.12): sabitler de hayatta kalır.</summary>
    [RelayCommand]
    public void DigerSekmeleriKapat(ISekme? kalan)
    {
        if (kalan is null)
            return;
        foreach (ISekme sekme in Sekmeler.Where(s => s != kalan && !s.Sabit).ToList())
            SekmeKapat(sekme);
        SeciliSekme = kalan;
    }

    /// <summary>Sağ tık → "Tümünü kapat" (v20-S21 saha m.12): sabit sekmeler atlanır.</summary>
    [RelayCommand]
    public void TumSekmeleriKapat()
    {
        foreach (ISekme sekme in Sekmeler.Where(s => !s.Sabit).ToList())
            SekmeKapat(sekme);
    }

    /// <summary>
    /// Bağlantı değişiminde tüm sekmeleri kapatır (V3): her sekme kendi kalıcı oturumunu
    /// bırakır (açık TRAN rollback edilir). Geri-alma yığını da temizlenir — başka
    /// bağlantının sorgusu Ctrl+Shift+T ile geri gelmemeli.
    /// </summary>
    private async Task SekmeleriKapatAsync()
    {
        foreach (ISekme sekme in Sekmeler.ToList())
        {
            try
            {
                await sekme.KapatAsync();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Sekme kapatılamadı (bağlantı değişimi)");
            }
        }
        Sekmeler.Clear();
        SeciliSekme = null;
        _kapatilanlar.Clear();
    }

    /// <summary>Ağaçtan "✏ Düzenle": tabloya bağlı düzenleme sekmesi (V2-S5, FG-4.9).</summary>
    public async Task DuzenlemeAcAsync(SemaNesnesi tablo)
    {
        if (_profil is null)
            return;

        var sekme = new DuzenlemeSekmesiViewModel(
            _schemaService, _queryService, _oturumFabrikasi,
            _lehceler.Getir(_profil.Motor), () => _profil, tablo)
        {
            // Edit sekmesi de SQLST-N kuralına uyar (kullanıcı bulgusu 2026-07-26:
            // "Edit için açtığımız pencerenin adı yine doğru değil").
            Baslik = $"✏ SQLST{++_sekmeSayaci}",
            ScriptOnayiIste = script => DuzenlemeOnayiIste?.Invoke(script) ?? false,
        };
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
        await sekme.YukleAsync();
    }

    /// <summary>DML önizleme onayı için görünüm köprüsü (önizleme penceresi) — MainWindow bağlar.</summary>
    public Func<string, bool>? DuzenlemeOnayiIste { get; set; }

    // --- Execution plan (V5-S1) ---

    /// <summary>
    /// Seçili sorgu sekmesinin planını alır ve AYRI bir plan sekmesinde açar.
    ///
    /// Kullanıcıya "tahmini mi gerçek mi" SEÇTİRİLMEZ (kullanıcı kararı 2026-07-19):
    /// motor ölçümlü plan verebiliyorsa o alınır, veremiyorsa (MySQL/Oracle) sunucunun
    /// verebildiği tek plan alınır ve hem düğme hem sekme bunu açıkça "tahmini" der.
    /// </summary>
    public async Task PlanAcAsync()
    {
        if (SeciliSekme is not SorguSekmesiViewModel sorgu)
        {
            Durum = "Plan için önce bir sorgu sekmesi seçin.";
            return;
        }

        bool gercek = _gercekPlanDestekli;
        Durum = gercek ? "Sorgu çalıştırılıyor ve plan toplanıyor…" : "Tahmini plan alınıyor…";
        (SorguPlani? plan, string? hata) = await sorgu.PlanAlAsync(gercek);

        if (plan is null)
        {
            Durum = $"Plan alınamadı: {hata}";
            return;
        }

        var sekme = new PlanSekmesiViewModel(plan, sorgu.Baslik)
        {
            // Eksik index önerisi YALNIZCA sekmede açılır — asla kendiliğinden çalıştırılmaz
            // (V2-S7'deki "incele-kopyala" kuralının aynısı).
            ScriptiSekmedeAc = script => SekmeAc("onerilen-index.sql", script, sorgu.SecilenVeritabani),
        };
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
        // Ölçümlü plan İSTENMİŞ ama ifade yazma/DDL olduğu için tahmine düşülmüş olabilir
        // (2026-07-20) — durum çubuğu bunu saklamaz.
        Durum = plan.YazmaOlduguIcinCalistirilmadi
            ? "Tahmini execution plan hazır — yazma/DDL ifadesi ÇALIŞTIRILMADI, veri değişmedi."
            : gercek
                ? "Execution plan hazır."
                : "Tahmini execution plan hazır (sorgu çalıştırılmadı).";
    }

    /// <summary>
    /// BF-7 "Neden yavaş?": seçili sorgunun planını (destekleniyorsa GERÇEK — ölçülü satırlarla, ki
    /// istatistik tazeliği sinyali gelsin) alır ve <see cref="YavaslikCozumleyici"/> ile tek Türkçe
    /// rapora çevirir. Pencere açmak View işidir → rapor + bağlam döner, MainWindow gösterir.
    /// </summary>
    public async Task<(YavaslikRaporu? Rapor, string SekmeAdi, string? Db, string? Hata)> NedenYavasAsync()
    {
        if (SeciliSekme is not SorguSekmesiViewModel sorgu)
        {
            Durum = "Performans değerlendirmesi için önce bir sorgu sekmesi seçin.";
            return (null, "", null, "sekme yok");
        }

        bool gercek = _gercekPlanDestekli;
        Durum = gercek
            ? "Performans değerlendirmesi — sorgu çalıştırılıp plan ölçülüyor…"
            : "Performans değerlendirmesi — tahmini plan alınıyor…";
        (SorguPlani? plan, string? hata) = await sorgu.PlanAlAsync(gercek);
        if (plan is null)
        {
            Durum = $"Performans değerlendirmesi — plan alınamadı: {hata}";
            return (null, "", null, hata);
        }

        YavaslikRaporu rapor = YavaslikCozumleyici.Coz(plan);
        Durum = rapor.DarbogazVar
            ? "Performans değerlendirmesi — olası darboğazlar bulundu (rapor açıldı)."
            : "Performans değerlendirmesi — belirgin bir darboğaz görünmüyor.";
        return (rapor, sorgu.Baslik, sorgu.SecilenVeritabani, null);
    }

    // --- Kapatılan sekme kurtarma + oturum kalıcılığı (V2-S2, FG-3.6) ---

    private Stack<OturumSekmeKaydi> _kapatilanlar = new();

    private OturumSekmeKaydi SekmeyiYansit(SorguSekmesiViewModel sekme) => new()
    {
        Baslik = sekme.Baslik,
        Veritabani = sekme.SecilenVeritabani,
        Sql = sekme.Belge.Text,
        SeciliMi = ReferenceEquals(sekme, SeciliSekme),
        OtomatikAd = sekme.BaslikOtomatikMi,
    };

    private SorguSekmesiViewModel SekmedenGeriYukle(OturumSekmeKaydi kayit)
    {
        SorguSekmesiViewModel sekme = SekmeKur(kayit.Baslik, kayit.OtomatikAd);
        sekme.SecilenVeritabani = kayit.Veritabani is not null && VeritabaniAdlari.Contains(kayit.Veritabani)
            ? kayit.Veritabani
            : _varsayilanVeritabani;
        sekme.Belge.Text = kayit.Sql;
        Sekmeler.Add(sekme);
        return sekme;
    }

    [RelayCommand]
    public void KapatilanSekmeyiGeriAl()
    {
        if (_kapatilanlar.Count == 0)
            return;
        SeciliSekme = SekmedenGeriYukle(_kapatilanlar.Pop());
    }

    /// <summary>Kapanışta çağrılır: açık SORGU sekmeleri diske yazılır (düzenleme sekmesi tablo bağlıdır, kaydedilmez).</summary>
    public void OturumuKaydet()
    {
        try
        {
            _oturumDeposu.KaydetAsync(
                    [.. Sekmeler.OfType<SorguSekmesiViewModel>().Select(SekmeyiYansit)], _profil?.Id)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Oturum kaydedilemedi"); // kapanışı asla engelleme
        }
    }

    private async Task OturumuGeriYukleAsync()
    {
        IReadOnlyList<OturumSekmeKaydi> kayitlar = [];
        try
        {
            kayitlar = await _oturumDeposu.YukleAsync(_profil?.Id);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Oturum geri yüklenemedi");
        }

        foreach (OturumSekmeKaydi kayit in kayitlar)
        {
            OturumSekmeKaydi k = kayit;
            // Eski otomatik adlar ("sorguN.sql" / "sorgu N" — 0.9.1 öncesi kalıp) SQLST{N}
            // markasına taşınır (kullanıcı bulgusu 2026-07-24: "ilk açılışta hâlâ sorgu1").
            // Kullanıcının ELLE verdiği adlara dokunulmaz — yalnız eski kalıp eşleşirse.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    k.Baslik, @"^sorgu ?\d+(\.sql)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                k = k with { Baslik = $"SQLST{_sekmeSayaci + 1}", OtomatikAd = false };

            SorguSekmesiViewModel sekme = SekmedenGeriYukle(k);
            if (k.SeciliMi)
                SeciliSekme = sekme;
            _sekmeSayaci++; // SQLST{N} adları geri yüklenenlerle çakışmasın
        }
        SeciliSekme ??= Sekmeler.LastOrDefault();
        if (kayitlar.Count > 0)
            Durum = $"{kayitlar.Count} sekme önceki oturumdan geri yüklendi.";
    }

    // --- Çalıştırma bitişi: geçmişe kayıt + uzun sorgu bildirimi (V2-S2) ---

    /// <summary>Bundan uzun süren sorgu, kullanıcı başka yerdeyse bitişte snackbar çıkarır.</summary>
    private static readonly TimeSpan UzunSorguEsigi = TimeSpan.FromSeconds(10);

    /// <summary>Ana pencere önde mi — MainWindow bağlar; bilinmiyorsa "önde" varsayılır.</summary>
    public Func<bool>? PencereAktifMi { get; set; }

    /// <summary>
    /// Sekmenin <c>CalistirmaTamamlandi</c> olayının işleyicisi. <b>internal</b>: olay yalnız
    /// tanımlayan sınıftan tetiklenebildiğinden testler bu dal mantığını (geçmişe yazma,
    /// ALTER sonrası tarihçe, uzun sorgu bildirimi) ancak buradan ölçebilir.
    /// </summary>
    internal void CalistirmaBitti(SorguSekmesiViewModel sekme, GecmisKaydi kayit)
    {
        // Kullanıcı bulgusu 2026-07-30: sonuç bölgesi splitter'la kapatılmış/çok küçükken sorgu
        // çalışınca grid HİÇ görünmüyordu (0 satırda "grid yok" sanılıyordu). SSMS davranışı:
        // sorgu bitince sonuç bölgesi kendini gösterir — kapalıysa varsayılan yüksekliğe açılır.
        if (SonucYuksekligi.Value < 60)
            SonucYuksekligi = new GridLength(220);

        if (GecmisAcik)
            _ = GecmiseYazAsync(kayit);

        // ALTER BAŞARIYLA bittiyse YENİ tanımı hemen tarihçeye işle (kullanıcı bulgusu
        // 2026-07-19: "bir SP'de güncelleme yaptım, refresh'e basmadan tarihçe bilgisi
        // gelmedi — hemen gelmesi lazımdı").
        //
        // Eskiden yalnız ALTER'dan ÖNCEKİ sürüm yedekleniyordu; yeni sürüm ancak tanım bir
        // daha OKUNDUĞUNDA kaydediliyordu. Tarihçe penceresi ise yerel liste BOŞ DEĞİLSE
        // sunucuya hiç gitmiyor — ve "alter-öncesi" kaydı onu zaten doldurduğu için liste
        // hiç boş olmuyordu. Sonuç: yaptığın değişiklik tarihçede görünmüyordu.
        if (kayit.Durum == GecmisDurumu.Basarili)
            _ = AlterSonrasiIsleAsync(kayit.Sql, sekme.SecilenVeritabani);

        bool baskaYerde = !ReferenceEquals(sekme, SeciliSekme) || !(PencereAktifMi?.Invoke() ?? true);
        if (kayit.SureMs >= UzunSorguEsigi.TotalMilliseconds && baskaYerde)
        {
            string sonucu = kayit.Durum switch
            {
                GecmisDurumu.Basarili => $"tamamlandı — {kayit.SatirSayisi:N0} satır",
                GecmisDurumu.IptalEdildi => "iptal edildi",
                _ => "hatayla bitti",
            };
            SnackbarSekmesi = sekme;
            SnackbarMetni = $"⏱ '{sekme.Baslik}' {sonucu} ({SonucBicimleyici.SureFormatla(TimeSpan.FromMilliseconds(kayit.SureMs))})";
        }
    }

    private async Task GecmiseYazAsync(GecmisKaydi kayit)
    {
        try
        {
            // Kayıt aktif PROFİLE bağlanır (V3): her profil kendi geçmişini görür.
            await _gecmisDeposu.EkleAsync(kayit with { ProfilId = _profil?.Id });
            if (GecmisGorunur)
                await GecmisiYenileAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Sorgu geçmişine yazılamadı"); // geçmiş asla sorguyu engellemez
        }
    }

    // --- Snackbar (uygulama-içi bildirim — 07-r2 §1 kararı: OS toast değil) ---

    [ObservableProperty] private string? _snackbarMetni;
    public SorguSekmesiViewModel? SnackbarSekmesi { get; private set; }
    private System.Windows.Threading.DispatcherTimer? _snackbarZamanlayici;

    partial void OnSnackbarMetniChanged(string? value)
    {
        _snackbarZamanlayici ??= YeniSnackbarZamanlayici();
        _snackbarZamanlayici.Stop();
        if (value is not null)
            _snackbarZamanlayici.Start(); // 8 sn sonra kendiliğinden kaybolur
    }

    private System.Windows.Threading.DispatcherTimer YeniSnackbarZamanlayici()
    {
        var zamanlayici = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8),
        };
        zamanlayici.Tick += (_, _) => SnackbarKapat();
        return zamanlayici;
    }

    [RelayCommand]
    public void SnackbarTikla()
    {
        if (SnackbarSekmesi is { } sekme && Sekmeler.Contains(sekme))
            SeciliSekme = sekme;
        SnackbarKapat();
    }

    [RelayCommand]
    public void SnackbarKapat()
    {
        SnackbarMetni = null;
        SnackbarSekmesi = null;
    }

    // --- Geçmiş paneli (V2-S2, FG-3.7) ---

    public ObservableCollection<GecmisGorunumu> GecmisKayitlari { get; } = [];

    [ObservableProperty] private bool _gecmisGorunur;
    [ObservableProperty] private string _gecmisAramasi = "";
    /// <summary>Geçmiş kaydı açık mı (gizlilik anahtarı) — ayar deposuna kalıcı yazılır.</summary>
    [ObservableProperty] private bool _gecmisAcik = true;
    /// <summary>Kalıcı ayarlar depodan okunurken değişiklik geri yazılmasın (yankı önleme).</summary>
    private bool _ayarlarYukleniyor;

    [RelayCommand]
    public async Task GecmisiGizleGosterAsync()
    {
        GecmisGorunur = !GecmisGorunur;
        if (GecmisGorunur)
            await GecmisiYenileAsync();
    }

    [RelayCommand]
    public async Task GecmisiTemizleAsync()
    {
        try
        {
            // Yalnız aktif profilin geçmişi silinir (V3) — diğer bağlantılarınki durur.
            await _gecmisDeposu.TemizleAsync(_profil?.Id);
            GecmisKayitlari.Clear();
            Durum = $"'{_profil?.Ad ?? "bu bağlantı"}' profilinin sorgu geçmişi temizlendi.";
        }
        catch (Exception ex)
        {
            Durum = $"Geçmiş temizlenemedi: {ex.Message}";
        }
    }

    /// <summary>
    /// Geçmiş kaydına çift tık: sorgu yeni sekmede açılır (çalıştırılmaz). Kullanıcı isteği
    /// 2026-07-25 (gece): kayıtta SEKME ADI varsa pencere AYNI adla açılır (SQLST3 gibi) —
    /// ad türetme yalnız eski (adsız) kayıtların yedek yoludur.
    /// </summary>
    public void GecmistenSekmeAc(GecmisGorunumu kayit)
    {
        // Kayıtlı ad AYNEN geri gelir (kullanıcı isteği 2026-07-25); adsız eski kayıt da artık
        // SQLST{N} alır (2026-07-26 "tüm durumlarda" kararıyla AdTuret yedeği emekli).
        string? kayitliAd = kayit.Kayit.SekmeAdi;
        SorguSekmesiViewModel sekme = kayitliAd is null
            ? SekmeAc("", kayit.Kayit.Sql, kayit.Kayit.Veritabani)
            : AdliSekmeAc(kayitliAd, kayit.Kayit.Sql, kayit.Kayit.Veritabani);
        sekme.BaslikOtomatikMi = kayitliAd is null;
    }

    partial void OnGecmisAramasiChanged(string value) => _ = GecmisiYenileAsync();

    partial void OnGecmisAcikChanged(bool value)
    {
        if (!_ayarlarYukleniyor)
            _ = _ayarDeposu.YazAsync(AyarAnahtari.GecmisAcik, value ? "1" : "0", _profil?.Id);
    }

    private async Task GecmisiYenileAsync()
    {
        try
        {
            // Yalnız aktif profilin geçmişi (V3) — başka bağlantının sorguları karışmaz.
            IReadOnlyList<GecmisKaydi> kayitlar = await _gecmisDeposu.AraAsync(GecmisAramasi, profilId: _profil?.Id);
            GecmisKayitlari.Clear();
            foreach (GecmisKaydi k in kayitlar)
                GecmisKayitlari.Add(new GecmisGorunumu(k));
        }
        catch (Exception ex)
        {
            Durum = $"Geçmiş okunamadı: {ex.Message}";
        }
    }

    /// <summary>Klavye kısayolları yalnız sorgu sekmesinde anlamlı — düzenleme sekmesinde sessizce yok sayılır.</summary>
    private SorguSekmesiViewModel? SeciliSorgu => SeciliSekme as SorguSekmesiViewModel;

    // v20-S21 saha m.16 ("F5 çalışmıyor"): bu ÜÇ komut TÜM sekmelerin paylaştığı tek
    // AsyncRelayCommand'dır ve varsayılanı, Task'i uçuştayken CanExecute=false tutmaktır —
    // A sekmesinde sorgu sürerken (ya da asılı kalmışken) B sekmesinde F5/Ctrl+E/Ctrl+Enter/
    // Ctrl+Shift+P ÖLÜYORDU (▶ butonu sekmenin KENDİ komutuna bağlı olduğundan çalışıyordu —
    // fark buradan). AllowConcurrentExecutions=true: kısayol her sekmede daima canlı; aynı
    // sekmede çifte koşuyu zaten CalistirCoreAsync/ProvaAsync başındaki CalisiyorMu bekçisi önler.
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task SeciliyiCalistirAsync()
    {
        // F5 Görsel Sorgu sekmesinde de çalışsın: tuvaldeki sorguyu üretip yeni sekmede çalıştırır
        // (kullanıcı isteği 2026-07-20). Diğer sekmelerde eskisi gibi seçili sorguyu çalıştırır.
        if (SeciliSekme is GorselSorguSekmesiViewModel gorsel)
        {
            gorsel.CalistirCommand.Execute(null);
            return Task.CompletedTask;
        }
        return SeciliSorgu?.CalistirAsync() ?? Task.CompletedTask;
    }

    [RelayCommand(AllowConcurrentExecutions = true)] // m.16 — üstteki nota bak
    public Task SeciliyiImlectenCalistirAsync() => SeciliSorgu?.ImlectekiniCalistirAsync() ?? Task.CompletedTask;

    [RelayCommand]
    public void SeciliyiBicimlendir() => SeciliSorgu?.Bicimlendir();

    /// <summary>🔍 Sorgu provası (V15-S2, Ctrl+Shift+P) — aktif sorgu sekmesine yönlendirir.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)] // m.16 — üstteki nota bak
    public Task SeciliyiProvaAsync() => SeciliSorgu?.ProvaAsync() ?? Task.CompletedTask;

    [RelayCommand]
    public void SeciliyiIptalEt() => SeciliSorgu?.Iptal();

    /// <summary>
    /// v19-S7 (kullanıcı isteği 2026-08-03): gezginde bir VERİTABANI düğümü seçilince aktif sorgu
    /// sekmesi o veritabanına geçer — üstteki veritabanı combobox'ı da (SecilenVeritabani
    /// bağlaması üzerinden) kendiliğinden güncellenir. Listede olmayan ad sessizce yok sayılır.
    /// </summary>
    public void GezgindenVeritabaniSecildi(string veritabani)
    {
        if (SeciliSorgu is { } sekme && VeritabaniAdlari.Contains(veritabani))
            sekme.SecilenVeritabani = veritabani;
    }

    /// <summary>
    /// IntelliSense için şema önbelleği (V2-S3, FG-3.8): varsa bellekten; yoksa bir kez
    /// yükler (ağaç açılmamış olsa da tamamlama çalışsın). Hatada null — tamamlama sessizce
    /// nesnesiz kalır, yazmayı asla engellemez.
    /// </summary>
    /// <summary>Şema önbelleği BELLEKTEYSE senkron döner (m.10 fikir 2: tooltip açılırken await
    /// edilemez — yüklü değilse tooltip sessizce çıkmaz).</summary>
    public SemaOnbellegi? OnbellekVarsa(string? veritabani)
        => veritabani is not null && _onbellekler.TryGetValue(veritabani, out SemaOnbellegi? o) ? o : null;

    /// <summary>Şema önbelleği temizlendi (profil/bağlantı değişimi) — ona dayanan yardımcı
    /// önbellekler (lookup sözlükleri) de bayatlar.</summary>
    public event Action? OnbellekTemizlendi;

    /// <summary>m.10 fikir 2: tanım tablosunu toptan okur (anahtar+açıklama). Hata/iptalde null —
    /// tooltip özelliği kullanıcının işini bölmez.</summary>
    public async Task<ResultSetData?> LookupSozlukSorgusuAsync(string sql, string? veritabani, int satirSiniri)
        => (await TekSetSorguAsync(sql, veritabani, satirSiniri)).Set;

    /// <summary>
    /// TEK koleksiyonun alan envanteri (v22-S3 saha turu-3 m.5). Mongo'da koleksiyon LİSTESİ
    /// bilerek alansız çekilir (<see cref="LogTabloNesneleriAsync"/> — koleksiyon başı $sample
    /// süpürmesi listeyi çok yavaşlatıyordu), bu yüzden find yardımcısındaki Filter/Sort/Project
    /// kutuları alan önerisi ALAMIYORDU. Alanlar artık SEÇİLEN koleksiyon için, tek örneklemeyle
    /// burada keşfedilir. Okunamazsa boş döner — öneri sessizce kapalı kalır, ekran çalışır.
    /// </summary>
    public async Task<IReadOnlyList<string>> MongoKoleksiyonAlanlariAsync(string? veritabani, string koleksiyon)
    {
        if (_profil is not { Motor: MotorTuru.Mongo } || string.IsNullOrWhiteSpace(koleksiyon))
            return [];

        // ⚠ v22-S4 turu-4 Find m.3 (kullanıcı: "kolon önerisi bozulmuş — ⚠ alan okunamadı"):
        // buradaki sınır 0'DI ve 0 "sınırsız" DEĞİL "hiç satır" demek — Mongo okuyucusunda tavan 0
        // olunca ilk belge eklenir eklenmez kırpılıyor, sorgu 0 satırla dönüyor ve alan listesi hep
        // boş kalıyordu. GERÇEK Mongo'da ölçüldü: sinir=0 → 0 satır/0 alan; sinir=50 → 4 alan.
        // Sahte köprülü testler gerçek yolu koşmadığı için yakalayamadı — canlı doğrulama şart.
        (ResultSetData? set, string? _) = await TekSetSorguAsync(
            MongoAlanEnvanteri.Sorgu(koleksiyon), veritabani,
            satirSiniri: MongoAlanEnvanteri.OrneklemBelge);
        return set is null
            ? []
            : [.. MongoAlanEnvanteri.Coz(set.Satirlar.Select(s => s.Length > 0 ? s[0] : null)).Select(k => k.Ad)];
    }

    /// <summary>Yardımcı özelliklerin (m.10) tek sonuç kümesi okuması — hata METNİYLE döner
    /// (istatistik penceresi nedeni gösterir; lookup tooltip'i sessizce yutar).</summary>
    public async Task<(ResultSetData? Set, string? Hata)> TekSetSorguAsync(
        string sql, string? veritabani, int satirSiniri, CancellationToken ct = default)
    {
        if (_profil is null)
            return (null, "Bağlantı yok.");
        // v22-S1 (saha turu-2 m.11): ct ARTIK GEÇİLİYOR. Eskiden CancellationToken.None gidiyordu →
        // ObjectId izi taramasında ⏹ Durdur, o an koşan sayımı hiç etkilemiyor, istemci 60 sn'lik
        // komut tavanına kadar await'te parkediyordu ("Durduruluyor…" ekranda asılı kalıyordu).
        QueryResult sonuc = await _executor.ExecuteAsync(_profil, sql,
            new ExecuteOptions
            {
                VeritabaniOverride = veritabani,
                SatirSiniri = satirSiniri,
                KomutTimeoutSnOverride = 60,
            }, ct);

        if (sonuc.Hata is { } h)
            return (null, h.Mesaj);
        return sonuc.ResultSetler.Count > 0 ? (sonuc.ResultSetler[0], null) : (null, "Sonuç dönmedi.");
    }

    /// <summary>
    /// Mongo koleksiyonunun index'lerinde ÖNDE GELEN alan adları (v22-S1). İki yerde kullanılır:
    /// log analizi strateji seçimi (m.2) ve ObjectId izi taramasında index'li adayları öne alma
    /// (m.12). Bağlantı yoksa/okunamazsa BOŞ döner — çağıranlar hızlı/eski yola düşer.
    /// </summary>
    public Task<IReadOnlyList<string>> MongoIndexAlanlariAsync(
        string veritabani, string koleksiyon, CancellationToken ct)
        => _profil is null
            ? Task.FromResult<IReadOnlyList<string>>([])
            : _mongoTeshisServisi.IndexOnAlanlariAsync(_profil, veritabani, koleksiyon, ct);

    /// <param name="ct">
    /// v22-S3 saha turu-3 m.3: şema yükü BÜYÜK veritabanında saniyeler sürer ve eskiden
    /// <c>CancellationToken.None</c> ile beklendiği için hiçbir yerden kesilemezdi. Artık çağıran
    /// (ör. ObjectId izi penceresinin ⏹ Durdur'u) bekleyişi bırakabilir; iptal edilen yükleme
    /// önbelleğe YAZILMAZ, sonraki çağrı baştan dener.
    /// </param>
    public async Task<SemaOnbellegi?> OnbellekGetirAsync(string? veritabani, CancellationToken ct = default)
    {
        if (veritabani is null || _profil is null)
            return null;
        if (_onbellekler.TryGetValue(veritabani, out SemaOnbellegi? mevcut))
            return mevcut;

        int nesil = _profilNesli; // yarış bekçisi (inceleme 2026-07-30)
        try
        {
            SemaOnbellegi yeni = await SemaVeFkYukleAsync(veritabani, ct);
            if (nesil != _profilNesli)
                return null; // profil değişti — eski şema yeni profilin önbelleğine yazılmaz
            _onbellekler[veritabani] = yeni;
            return yeni;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Şemayı + FK'ları (v2b #8, 2026-07-29 — oto-tamamlama JOIN…ON önerisi için) birlikte yükler.
    /// FK'lar şema yüküyle PARALEL çekilir (gecikme eklemez) ve BONUS'tur: FK sorgusu başarısız olsa
    /// bile şema yine döner (boş FK). Böylece önbellek her iki yükleme yolunda da (bu ve ağaç düğümü) FK'lı olur.
    /// </summary>
    private async Task<SemaOnbellegi> SemaVeFkYukleAsync(string veritabani, CancellationToken ct)
    {
        Task<SemaOnbellegi> semaGorevi = _schemaService.YukleAsync(_profil!, veritabani, ct);
        Task<IReadOnlyList<YabanciAnahtar>> fkGorevi = FkleriGuvenliGetirAsync(veritabani, ct);
        await Task.WhenAll(semaGorevi, fkGorevi);
        SemaOnbellegi sema = semaGorevi.Result;
        return new SemaOnbellegi
        {
            // m.17: FK'lar kolonlara işlenir — ağaçta 🔗 + hedef tablo görünür (üç yükleme
            // yolu da buradan geçtiğinden işaretleme tek yerde).
            Nesneler = FkIsaretleyici.Isaretle(sema.Nesneler, fkGorevi.Result),
            YuklenmeZamaniUtc = sema.YuklenmeZamaniUtc,
            YabanciAnahtarlar = fkGorevi.Result,
        };
    }

    /// <summary>FK'ları çeker; oto-tamamlama için BONUS olduğundan (Görsel Tasarımcı'nın kullandığı
    /// sorgu) hata olursa boş liste döner — şema yüklemesi FK yüzünden çökmesin. Mongo'da FK yoktur.</summary>
    private async Task<IReadOnlyList<YabanciAnahtar>> FkleriGuvenliGetirAsync(string veritabani, CancellationToken ct)
    {
        try
        {
            return await _schemaService.YabanciAnahtarlarAsync(_profil!, veritabani, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// 🔗 Kolonun FK bağları (v20-S10): önbellekteki FK grafından ▲ giden + ▼ gelen bağlar.
    /// Mongo/bağlantısız durumda null döner (pencere açılmaz, neden durum çubuğunda).
    /// </summary>
    public async Task<IReadOnlyList<KolonBaglari.Bag>?> KolonBaglariAsync(SemaNesnesi tablo, string kolonAd)
    {
        if (_profil is null)
        {
            Durum = "Kolon bağları için önce bir sunucuya bağlanın.";
            return null;
        }
        if (_profil.Motor == MotorTuru.Mongo)
        {
            Durum = "MongoDB'de yabancı anahtar yoktur — kolon bağları SQL motorlarına özgüdür.";
            return null;
        }
        IReadOnlyList<YabanciAnahtar> fkler = await FkleriGuvenliGetirAsync(tablo.Veritabani, CancellationToken.None);
        return KolonBaglari.Bul(tablo.Sema, tablo.Ad, kolonAd, fkler);
    }

    /// <summary>Bağ satırına çift tık: ilişkiyi ortaya çıkaran JOIN sorgusu yeni sekmede çalışır.</summary>
    public async Task KolonBagiJoinAcAsync(SemaNesnesi tablo, KolonBaglari.Bag bag)
    {
        if (_profil is null || _profil.Motor == MotorTuru.Mongo)
            return;
        SorguSekmesiViewModel sekme = SekmeAc(
            "", KolonBaglari.JoinSorgusu(bag, _lehceler.Getir(_profil.Motor)), tablo.Veritabani);
        await sekme.CalistirAsync();
    }

    /// <summary>
    /// 🔁 LINQ→SQL bağlamı (v20-S11): aktif sekmenin veritabanının şeması + FK grafı + lehçe.
    /// Mongo/bağlantısız → null (neden durum çubuğunda; pencere kendi metnini de gösterir).
    /// </summary>
    public async Task<LinqBaglam?> LinqBaglamiAsync()
    {
        if (_profil is null)
        {
            Durum = "LINQ çevirisi için önce bir sunucuya bağlanın.";
            return null;
        }
        if (_profil.Motor == MotorTuru.Mongo)
        {
            Durum = "LINQ → SQL çevirisi SQL motorları içindir (MongoDB profili SQL üretmez).";
            return null;
        }
        string? db = (SeciliSekme as SorguSekmesiViewModel)?.SecilenVeritabani ?? _varsayilanVeritabani;
        if (db is null)
        {
            Durum = "LINQ çevirisi için hedef veritabanı belirlenemedi — bir sorgu sekmesi açın.";
            return null;
        }
        SemaOnbellegi sema = await SemaVeFkYukleAsync(db, CancellationToken.None);
        return new LinqBaglam(
            sema.Nesneler, sema.YabanciAnahtarlar, _lehceler.Getir(_profil.Motor), db, _profil.Motor);
    }

    /// <summary>Çevrilen SQL'i yeni sekmede açar ve çalıştırır (LINQ→SQL penceresinin ▶ düğmesi).</summary>
    public async Task LinqSqlSekmedeAcAsync(string sql, string? veritabani)
    {
        SorguSekmesiViewModel sekme = SekmeAc("", sql, veritabani);
        await sekme.CalistirAsync();
    }

    // --- 🤖 Asistan (v11-S1, 2026-07-25 — "müşteri için en can alıcı nokta") ---

    private readonly IAsistanServisi _asistanServisi;
    private AsistanAyarlari? _asistanAyarlari; // config dosyasından bir kez okunur

    /// <summary>Asistan config dosyası okuma köprüsü (MainWindow bağlar — asistan.config).
    /// Uygulama İÇİNDEN ayar değiştirilemez (kullanıcı kararı 2026-07-26).</summary>
    public Func<AsistanAyarlari?>? AsistanConfigOku { get; set; }

    /// <summary>Eski SQLite ayarının dosyaya TEK SEFERLİK göçü için yazma köprüsü.</summary>
    public Action<AsistanAyarlari>? AsistanConfigYaz { get; set; }

    /// <summary>🤖 Asistan düğmesi: bağlı her motorda görünür (v11-S1).</summary>
    public bool AsistanGorunur => _profil is not null;

    /// <summary>📦 Paket Aktarım düğmesi (v12-S2): her motorda görünür — Mongo'da (v12-S5)
    /// koleksiyon→koleksiyon arşivleme penceresine yönlenir.</summary>
    public bool AktarimGorunur => _profil is not null;

    /// <summary>📥 Excel/TXT İçe Aktar (v13-S2): hedef SQL tablosu — SQL ailesinde görünür.</summary>
    public bool IceAktarGorunur => _profil is not null && !MotorMongoMu;

    /// <summary>Asistan sekmesini açar (tek örnek — arka arkaya soru sorulur, sekme birikmez).</summary>
    [RelayCommand]
    public async Task AsistanAc()
    {
        if (Sekmeler.OfType<AsistanSekmesiViewModel>().FirstOrDefault() is { } acik)
        {
            SeciliSekme = acik;
            return;
        }

        AsistanAyarlari ayar = await AsistanAyarlariAlAsync();
        var sekme = new AsistanSekmesiViewModel(
            _asistanServisi,
            // Her soruda taze oku: config sonradan düzeltilirse (kullanıcı anahtarı ekler) sekme
            // yeniden açılmadan devreye girer; anahtarsız bir okuma da sekmeye KİLİTLENMEZ.
            () => _asistanAyarlari is { Hazir: true } h ? h : (AsistanConfigOku?.Invoke() ?? ayar),
            () => OnbellekGetirAsync(LogAnalizAktifVeritabani),
            () => AktifMotor,
            () => _sonSorguSekmesi?.MetinSaglayici?.Invoke(),
            () => _sonSorguSekmesi?.SonHataMesaji,  // S4: hatayı çözdür köprüsü
            () => _sonPlanSekmesi is { } p ? AsistanIstemleri.PlanMetni(p.Plan) : null); // S5b
        // S3: ayıklanan sorgu AKTİF DB'ye açılır — çalıştırılmaz (kullanıcı inceler, F5 der).
        sekme.SekmeyeAc = (baslik, sorgu) => SekmeAc(baslik, sorgu, LogAnalizAktifVeritabani);
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;

        // 🔥 Ön yükleme (v22-S6): sekme AÇILDIĞI anda Yerel modeli belleğe aldır — kullanıcı sorusunu
        // yazarken 4,4 GB'lık yükleme arka planda bitsin. Bilerek BEKLENMEZ (fire-and-forget): sekme
        // anında açılmalı, ön yükleme bir hızlandırmadır; hatası da sessizdir (OnYukleAsync).
        _ = sekme.IsitAsync();
    }

    /// <summary>
    /// Asistan yapılandırması — TEK kaynak config DOSYASI (kullanıcı kararı 2026-07-26:
    /// uygulama içinden değiştirilemez). Dosya yoksa eski SQLite ayarına düşülür ve anahtar
    /// kayıtlıysa dosyaya TEK SEFERLİK göçürülür (şifreli haliyle — çözülmez).
    /// </summary>
    /// <summary>
    /// Önbellekli okuma: yalnız KULLANILABİLİR (Hazir) sonuç önbelleğe alınır. Böylece config
    /// dosyası oluşmadan önce yapılan bir okuma (anahtarsız) sekmeye/servise KİLİTLENMEZ — kullanıcı
    /// bulgusu 2026-07-27: "AI çalışmadı"; kök neden göç öncesi anahtarsız okumanın önbelleğe takılmasıydı.
    /// </summary>
    public async Task<AsistanAyarlari> AsistanAyarlariAlAsync()
    {
        if (_asistanAyarlari is { Hazir: true })
            return _asistanAyarlari;
        AsistanAyarlari taze = await AsistanAyarlariOkuAsync();
        if (taze.Hazir)
            _asistanAyarlari = taze; // anahtarsız/eksik okuma önbelleğe alınmaz → sonraki denemede tazelenir
        return taze;
    }

    public async Task<AsistanAyarlari> AsistanAyarlariOkuAsync()
    {
        if (AsistanConfigOku?.Invoke() is { } config)
            return config;

        string saglayici = await _ayarDeposu.OkuAsync(AyarAnahtari.AsistanSaglayici) ?? "";
        string model = await _ayarDeposu.OkuAsync(AyarAnahtari.AsistanModel) ?? "";
        string? taban = await _ayarDeposu.OkuAsync(AyarAnahtari.AsistanTabanAdres);
        string? anahtar64 = await _ayarDeposu.OkuAsync(AyarAnahtari.AsistanAnahtarSifreli);

        // v22-S6: eski SQLite ayarında "Gemini" yazıyor olabilir — o sağlayıcı kaldırıldı, tanınmaz
        // ve 🏠 Yerel'e düşer. Model adı da o sağlayıcıya aitse (gemini-*) taşınmaz; yerel varsayılan
        // kurulur, yoksa Ollama'ya "gemini-flash-latest" gönderilip "model bulunamadı" alınırdı.
        bool tanindi = Enum.TryParse(saglayici, out AsistanSaglayici s);
        var eski = new AsistanAyarlari(
            tanindi ? s : AsistanSaglayici.Yerel,
            tanindi && model.Length > 0 ? model : AsistanYonlendirici.YerelVarsayilanModel,
            tanindi && !string.IsNullOrWhiteSpace(taban) ? taban : AsistanYonlendirici.YerelVarsayilanTaban,
            string.IsNullOrWhiteSpace(anahtar64) ? null : anahtar64);

        if (eski.AnahtarSifreli is not null)
            AsistanConfigYaz?.Invoke(eski); // v11 döneminde ⚙ ile girilmiş ayar dosyaya taşınır

        return eski;
    }

    /// <summary>Asistan olmayan yüzeyler için tek atış köprüsü (v11-S6: Log Analizi "AI Özetle").</summary>
    public async Task<AsistanCevabi> AsistanSorAsync(string istem, CancellationToken ct = default)
        => await _asistanServisi.SorAsync(istem, await AsistanAyarlariAlAsync(), ct);

    // ---- S7 (2026-07-25): bağlam-yüzeyi eylemleri — düğme kendi sekmesinde, cevap AYRI pencerede ----

    // baglamMetni (sorgu+hata) dolduysa yalnız İLGİLİ tabloların şeması → istem küçülür (v21 2026-08-09,
    // AsistanSekmesiViewModel ile aynı süzme); boşsa tam şemaya düşer.
    private async Task<string> AsistanSemaOzetiAsync(string? baglamMetni = null)
    {
        SemaOnbellegi? onbellek = await OnbellekGetirAsync(LogAnalizAktifVeritabani);
        return AsistanIstemleri.SemaOzeti(onbellek, AsistanIstemleri.IlgiliTablolar(baglamMetni, onbellek));
    }

    /// <summary>AI Değerlendir (sorgu sekmesi): editördeki sorguyu denetletir; sorgu boşsa null.</summary>
    public async Task<AsistanCevabi?> AsistanDegerlendirAsync(string? sorgu, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(sorgu) ? null : await AsistanSorAsync(
            AsistanIstemleri.SorguDegerlendir(sorgu, await AsistanSemaOzetiAsync(sorgu),
                AsistanIstemleri.MotorAdi(AktifMotor)), ct);

    /// <summary>AI Açıkla (sorgu sekmesi): editördeki sorguyu düz Türkçe anlattırır; boşsa null.</summary>
    public async Task<AsistanCevabi?> AsistanAciklaAsync(string? sorgu, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(sorgu) ? null : await AsistanSorAsync(
            AsistanIstemleri.SorguAcikla(sorgu, await AsistanSemaOzetiAsync(sorgu),
                AsistanIstemleri.MotorAdi(AktifMotor)), ct);

    /// <summary>AI Hatayı çözdür (sorgu sekmesi): son hata + sorgu; ikisinden biri yoksa null.</summary>
    public async Task<AsistanCevabi?> AsistanHataCozAsync(
        string? sorgu, string? hata, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(sorgu) || string.IsNullOrWhiteSpace(hata) ? null : await AsistanSorAsync(
            AsistanIstemleri.HataCozdur(sorgu, hata, await AsistanSemaOzetiAsync(sorgu + " " + hata),
                AsistanIstemleri.MotorAdi(AktifMotor)), ct);

    /// <summary>AI Planı yorumlat (plan sekmesi): alttaki planı otomatik metinleştirip yorumlatır.</summary>
    public Task<AsistanCevabi> AsistanPlanYorumlaAsync(SorguPlani plan, CancellationToken ct = default)
        => AsistanSorAsync(AsistanIstemleri.PlanYorumla(
            AsistanIstemleri.PlanMetni(plan), AsistanIstemleri.MotorAdi(AktifMotor)), ct);

    /// <summary>AI SOAP yorumlat (v14-S3): istek+yanıt zarfını özetletir/Fault'u çözdürtür.</summary>
    public Task<AsistanCevabi> AsistanSoapYorumlaAsync(
        string istekZarfi, string yanitZarfi, bool faultMu, CancellationToken ct = default)
        => AsistanSorAsync(AsistanIstemleri.SoapYorumla(istekZarfi, yanitZarfi, faultMu), ct);

    /// <summary>AI REST yorumlat (v20-S8): istek özeti + yanıt gövdesini özetletir/HTTP hatasını çözdürtür.</summary>
    public Task<AsistanCevabi> AsistanRestYorumlaAsync(
        string istekOzeti, string yanitGovdesi, int httpDurum, CancellationToken ct = default)
        => AsistanSorAsync(AsistanIstemleri.RestYorumla(istekOzeti, yanitGovdesi, httpDurum), ct);

    /// <summary>AI "tarifle → istek üret" (v20-S13): doğal dil açıklamadan yapılandırılmış REST isteği (JSON) ürettirir.</summary>
    public Task<AsistanCevabi> AsistanRestIstekUretAsync(string aciklama, CancellationToken ct = default)
        => AsistanSorAsync(AsistanIstemleri.RestIstekUret(aciklama), ct);

    /// <summary>AI "yanıttan CREATE TABLE öner" (v20-S13, madde 6): örnek veri özetinden aktif motora uygun şema (tip/pk/null) ürettirir.</summary>
    public Task<AsistanCevabi> AsistanRestSemaOnerAsync(string veriOzeti, CancellationToken ct = default)
        => AsistanSorAsync(AsistanIstemleri.SemaOner(veriOzeti, AsistanIstemleri.MotorAdi(AktifMotor)), ct);

    /// <summary>AI "iki REST yanıtını diff yorumla" (v20-S13, madde 7): sabitlenen (önceki) ile şimdiki yanıtın farkını açıklatır.</summary>
    public Task<AsistanCevabi> AsistanRestDiffAsync(string onceki, string simdiki, CancellationToken ct = default)
        => AsistanSorAsync(AsistanIstemleri.RestDiffYorumla(onceki, simdiki), ct);

    // --- Log Analizi köprüleri (kullanıcı tasarımı 2026-07-23): pencere tablo listesini ve
    //     sorgu çalıştırmayı buradan alır — kendi bağlantı/önbellek yolu açmaz. ---

    /// <summary>Aktif bağlantının motoru (log analizi sorgu lehçesi için); bağlantı yoksa null.</summary>
    public MotorTuru? AktifMotor => _profil?.Motor;

    /// <summary>Log analizi penceresinin veritabanı seçicisini besleyen liste.</summary>
    public IReadOnlyList<string> LogAnalizVeritabanlari => [.. VeritabaniAdlari];

    /// <summary>
    /// Log analizi penceresinin AÇILIŞTA seçili gelecek veritabanı: aktif sekmenin baktığı DB,
    /// yoksa varsayılan. Kullanıcı bulgusu 2026-07-24: pencere hep "ilk sistem-dışı DB"yi
    /// analiz ediyordu; kullanıcı başka DB'de çalışıyorsa log tablosu listeye gelmiyordu.
    /// </summary>
    public string? LogAnalizAktifVeritabani =>
        (SeciliSekme as SorguSekmesiViewModel)?.SecilenVeritabani ?? _varsayilanVeritabani;

    /// <summary>Seçilen veritabanının TABLO/VIEW/KOLEKSİYON nesneleri (log analizi/tablolar seçicisi; Mongo dahil).</summary>
    public async Task<IReadOnlyList<SemaNesnesi>> LogAnalizTablolariAsync(string veritabani)
        => [.. (await LogTabloNesneleriAsync(veritabani))
            .Where(n => n.Tur is SemaNesneTuru.Tablo or SemaNesneTuru.View or SemaNesneTuru.Koleksiyon)
            .OrderBy(n => n.TamAd, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Seçici için nesne listesi. v20-S7 (kullanıcı bulgusu 2026-08-06: "Mongo koleksiyonlar çok geç
    /// yükleniyor — hem tablolar hem log analizi"): MONGO'da koleksiyon LİSTESİ için pahalı koleksiyon-başı
    /// <c>$sample</c> ALAN süpürmesini BEKLEME — yalnız ADLARI çek (tek round-trip, <see cref="ISchemaService.AdlariYukleAsync"/>).
    /// Alanlar seçilen koleksiyonda on-demand keşfedilir (v19-S8), o yüzden liste alansız yeterli. Faz-1
    /// sonucu ÖNBELLEĞE YAZILMAZ (alansız girdi otomatik-tamamlama/asistan önbelleğini kirletmesin — ağaç
    /// tam yükü ayrı, iki fazlı yapar). SQL ailesinde tam yük (adlar+kolonlar tek batch) + önbellek korunur;
    /// İçe/Paket Aktarım Mongo'da gizli olduğundan onlar bu değişimden etkilenmez.
    /// </summary>
    private async Task<IReadOnlyList<SemaNesnesi>> LogTabloNesneleriAsync(string veritabani)
    {
        if (_onbellekler.TryGetValue(veritabani, out SemaOnbellegi? mevcut))
            return mevcut.Nesneler; // zaten tam yüklü
        if (_profil is { Motor: MotorTuru.Mongo } mongoProfil)
        {
            try { return (await _schemaService.AdlariYukleAsync(mongoProfil, veritabani, CancellationToken.None)).Nesneler; }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { return []; }
        }
        return (await OnbellekGetirAsync(veritabani))?.Nesneler ?? [];
    }

    /// <summary>
    /// 🔎 Veri Arama sorgusu (2026-08-03 büyük-DB kontrolü: 400 tablo × milyonlarca satır):
    /// tablo başına 10 sn tavan (indekssiz dev tabloda full scan aramayı KİLİTLEMESİN — süreni
    /// aşan tablo atlanıp raporlanır) + iptal token'ı GEÇİLİR (Durdur, çalışan sorguyu ANINDA
    /// keser; tablolar arasını beklemez) + satır sınırı 100 (bellek).
    /// </summary>
    public Task<QueryResult> VeriAramaSorgusuAsync(string veritabani, string sql, bool sinirli, CancellationToken ct)
        => _profil is null
            ? Task.FromResult(new QueryResult { Hata = new SqlHata("Bağlantı yok.", 0, 0, 0) })
            : _executor.ExecuteAsync(_profil, sql,
                new ExecuteOptions
                {
                    VeritabaniOverride = veritabani,
                    SatirSiniri = 100,
                    // Kapsam seçildiyse (sinirli=false) süre SINIRSIZ (0) — kullanıcı bilinçli tercih
                    // etti, Durdur zaten anında keser (kullanıcı fikri 2026-08-03: "30 sn'yi hep aşan
                    // tabloyu hiç sorgulayamam — seçince sınırsız arayayım").
                    KomutTimeoutSnOverride = sinirli ? 10 : 0,
                }, ct);

    /// <summary>Log analizi örneklem sorgusu — seçilen DB üzerinde, satır sınırı örnekleme kadar.</summary>
    public Task<QueryResult> LogAnalizSorgusuAsync(string veritabani, string sql)
        => _profil is null
            ? Task.FromResult(new QueryResult { Hata = new SqlHata("Bağlantı yok.", 0, 0, 0) })
            : _executor.ExecuteAsync(_profil, sql,
                // Madde 2 (2026-07-30): indekssiz zaman kolonunda full scan sonsuz donmasın → 30 sn tavan.
                // NOT: Log Analizi PENCERESİ artık bunu DEĞİL, sınırsız + iptal edilebilir LogAnalizCalistirAsync'i
                // kullanır (v20-S6). Bu yol Bağımlılık Ağacı gibi 30 sn tavanına dayanan çağıranlarda kalır.
                new ExecuteOptions { VeritabaniOverride = veritabani, SatirSiniri = 20_000, KomutTimeoutSnOverride = 30 },
                CancellationToken.None);

    /// <summary>
    /// v20-S13 "DB değeri → değişken": REST istemcisi bir değişkeni SQL'den doldurabilsin diye scalar
    /// sorgu — SQL'i AKTİF profil + seçili DB'de çalıştırır, İLK sonuç kümesinin ilk satır-ilk kolonunu
    /// Invariant metne çevirir (null/DBNull → boş string). SatirSiniri=1 + 30 sn timeout (indekssiz
    /// taramada donma kalkanı). Bağlantı yoksa ya da SQL hata verirse çağıran (REST penceresi) net
    /// görsün diye InvalidOperationException fırlatır — orada yakalanıp değişkenin Değer'ine yazılır.
    /// </summary>
    public async Task<string> RestScalarSorguAsync(string sql)
    {
        if (_profil is null)
            throw new InvalidOperationException("Bağlantı yok.");

        QueryResult sonuc = await _executor.ExecuteAsync(_profil, sql,
            new ExecuteOptions
            {
                VeritabaniOverride = LogAnalizAktifVeritabani,
                SatirSiniri = 1,
                KomutTimeoutSnOverride = 30,
            }, CancellationToken.None);

        if (sonuc.Hata is { } h)
            throw new InvalidOperationException(h.Mesaj);
        if (sonuc.ResultSetler.Count == 0 || sonuc.ResultSetler[0].Satirlar.Count == 0)
            return "";
        object? hucre = sonuc.ResultSetler[0].Satirlar[0][0];
        return hucre is null or DBNull
            ? ""
            : Convert.ToString(hucre, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>
    /// v20-S13 "Yanıt ↔ DB karşılaştır": REST yanıtıyla kıyaslanacak DB tarafını getirir — SQL'i aktif
    /// profil + seçili DB'de çalıştırır, İLK sonuç kümesinin kolon ADLARI + ham satırlarını döner
    /// (SatirSiniri 10.000 = karşılaştırma tavanı; 30 sn timeout). Bağlantı yoksa ya da SQL hata verirse
    /// çağıran (karşılaştırma penceresi) net görsün diye InvalidOperationException fırlatır.
    /// </summary>
    public async Task<(IReadOnlyList<string> Kolonlar, IReadOnlyList<object?[]> Satirlar)> RestSorguSonucAsync(string sql)
    {
        if (_profil is null)
            throw new InvalidOperationException("Bağlantı yok.");

        QueryResult sonuc = await _executor.ExecuteAsync(_profil, sql,
            new ExecuteOptions
            {
                VeritabaniOverride = LogAnalizAktifVeritabani,
                SatirSiniri = 10_000,
                KomutTimeoutSnOverride = 30,
            }, CancellationToken.None);

        if (sonuc.Hata is { } h)
            throw new InvalidOperationException(h.Mesaj);
        if (sonuc.ResultSetler.Count == 0)
            return ([], []);
        ResultSetData rs = sonuc.ResultSetler[0];
        return ([.. rs.Kolonlar.Select(k => k.Ad)], rs.Satirlar);
    }

    /// <summary>
    /// Log Analizi PENCERESİNE özel koşu (v20-S6, kullanıcı bulgusu 2026-08-06: "30 sn sınırı yüzünden dev
    /// tablolar HEP timeout — büyük tabloda 30 sn'yi aşması normal"): SÜRE SINIRSIZ (0). Sonsuz donma riskini
    /// pencerede "⏹ Durdur" karşılar — çalışan sorguyu <paramref name="ct"/> ile ANINDA keser (Veri Arama deseni).
    /// Bağımlılık Ağacı vb. hâlâ 30 sn'li <see cref="LogAnalizSorgusuAsync"/>'i kullanır.
    /// </summary>
    /// <summary>
    /// ⏱ Log analizi SUNUCU TARAFI süre tavanı (v22-S4 saha turu-4 m.6). v20-S6'da süre SINIRSIZ
    /// yapılmıştı ("30 sn yüzünden dev tablolar HEP timeout") ve güvence "⏹ Durdur çalışan sorguyu
    /// ct ile keser" idi. Denetimde bu güvencenin MONGO'da tutmadığı görüldü: <c>KomutTimeoutSnOverride
    /// = 0</c> ⇒ <c>maxTime = null</c> ⇒ sunucuya <c>maxTimeMS</c> HİÇ gitmiyor. Yani kaçan bir tarama
    /// sunucuda gerçekten sonsuza kadar sürer; ct yalnız İSTEMCİ beklemesini keser, sunucudaki iş
    /// devam eder. Kullanıcının "analiz çalışamıyor, hiç bitmiyor" dediği tablo budur.
    ///
    /// 120 sn bilinçli bir orta yol: eski 30 sn'nin dört katı (o şikâyeti geri getirmez) ama SONLU —
    /// aşılırsa sunucu işi durdurur ve kullanıcı ne yapacağını söyleyen bir mesaj alır (sorguyu
    /// daralt / index aç), sonsuza kadar boş ekrana bakmaz. Erken kesmek için ⏹ Durdur yerinde durur.
    /// </summary>
    public const int LogAnalizTavaniSn = 120;

    public Task<QueryResult> LogAnalizCalistirAsync(string veritabani, string sql, CancellationToken ct)
        => _profil is null
            ? Task.FromResult(new QueryResult { Hata = new SqlHata("Bağlantı yok.", 0, 0, 0) })
            : _executor.ExecuteAsync(_profil, sql,
                new ExecuteOptions
                {
                    VeritabaniOverride = veritabani,
                    SatirSiniri = 20_000,
                    KomutTimeoutSnOverride = LogAnalizTavaniSn,
                },
                ct);

    /// <summary>Log Analizi son seçim hatırlama (v20-S3): profil kapsamında son DB/tablo/mesaj-kolonu.</summary>
    public async Task<(string?, string?, string?)> LogSonSecimAsync()
        => (await _ayarDeposu.OkuAsync(AyarAnahtari.LogSonVeritabani, _profil?.Id),
            await _ayarDeposu.OkuAsync(AyarAnahtari.LogSonTablo, _profil?.Id),
            await _ayarDeposu.OkuAsync(AyarAnahtari.LogSonMesajKolon, _profil?.Id));

    /// <summary>Log Analizi'nde analiz yapılınca çağrılır — son seçimi profil kapsamında saklar.</summary>
    public void LogSecimKaydet(string? db, string? tablo, string? kolon)
    {
        if (!string.IsNullOrEmpty(db)) _ = _ayarDeposu.YazAsync(AyarAnahtari.LogSonVeritabani, db, _profil?.Id);
        if (!string.IsNullOrEmpty(tablo)) _ = _ayarDeposu.YazAsync(AyarAnahtari.LogSonTablo, tablo, _profil?.Id);
        if (!string.IsNullOrEmpty(kolon)) _ = _ayarDeposu.YazAsync(AyarAnahtari.LogSonMesajKolon, kolon, _profil?.Id);
    }

    // --- Nesne eylemleri (S5): ağaçtan çift tık / sağ tık ---

    /// <summary>Hazır SQL ile yeni sekme açar; nesnenin veritabanı sekmeye taşınır.</summary>
    /// <summary>
    /// Hazır SQL ile yeni sekme açar. KULLANICI KARARI (2026-07-26): editör sekmesi HANGİ yoldan
    /// açılırsa açılsın ad HEP <c>SQLST{N}</c>'dir — "tabloyu select yaptığımda ya da editörle
    /// açtığımda da aynı adlandırma". <paramref name="baslik"/> bu yüzden YOK SAYILIR; imzada
    /// durmasının tek nedeni çok sayıda köprü delegesinin (Action&lt;string,string&gt;) imzasını
    /// kırmamaktır. Tek istisna geçmişten KAYITLI adla açılış — o <see cref="GecmistenSekmeAc"/>'ta.
    /// </summary>
    public SorguSekmesiViewModel SekmeAc(string baslik, string sql, string? veritabani)
        => AdliSekmeAc($"SQLST{++_sekmeSayaci}", sql, veritabani);

    private SorguSekmesiViewModel AdliSekmeAc(string ad, string sql, string? veritabani)
    {
        SorguSekmesiViewModel sekme = SekmeKur(ad, otomatikAd: false);
        sekme.SecilenVeritabani = veritabani ?? _varsayilanVeritabani;
        sekme.Belge.Text = sql;
        Sekmeler.Add(sekme);
        SeciliSekme = sekme;
        return sekme;
    }

    /// <summary>
    /// 📂 Dosya Aç (v20-S14): .sql dosyasını KENDİ ADIYLA yeni sekmede açar — SQLST{N} kuralının
    /// bilinçli ikinci istisnası (ilki geçmişten kayıtlı ad): dosyayla çalışan kullanıcı sekmede
    /// dosya adını görmek ister (Ctrl+S kaydedişinin ayna davranışı, 2026-07-23 kararı gibi).
    /// </summary>
    public void DosyadanSekmeAc(string dosyaAdi, string sql, string kodlama, string? uyari)
    {
        AdliSekmeAc(dosyaAdi, sql, _varsayilanVeritabani);
        Durum = uyari is null
            ? $"Açıldı: {dosyaAdi} ({kodlama})"
            : $"Açıldı: {dosyaAdi} ({kodlama}) — ⚠ {uyari}";
    }

    /// <summary>
    /// Hazır SQL ile yeni sekme açar ve HEMEN çalıştırır — Görsel Sorgu'nun "F5" köprüsü
    /// (kullanıcı isteği 2026-07-20: script'e geçmeden doğrudan çalıştır). Çalıştırma normal
    /// sorgu sekmesinde, tüm koruma raylarıyla olur.
    /// </summary>
    public SorguSekmesiViewModel SekmeAcVeCalistir(string baslik, string sql, string? veritabani)
    {
        SorguSekmesiViewModel sekme = SekmeAc(baslik, sql, veritabani);
        sekme.CalistirCommand.Execute(null); // Belge.Text zaten kuruldu; editör henüz yoksa metin sağlayıcı ona düşer
        return sekme;
    }

    /// <summary>SP/view/fonksiyon → OBJECT_DEFINITION → ALTER script'i yeni sekmede (FG-5.1/5.2).</summary>
    public async Task NesneScriptiAcAsync(SemaNesnesi nesne)
    {
        if (_profil is null)
            return;

        Durum = $"{nesne.TamAd} tanımı alınıyor…";
        try
        {
            string? tanim = await _schemaService.TanimGetirAsync(_profil, nesne, CancellationToken.None);
            if (tanim is null)
            {
                Durum = $"{nesne.TamAd}: tanım okunamadı (şifreli olabilir).";
                return;
            }

            // V2-S8 (Ö2): görülen her tanım yerel tarihçeye — "dışarıda değişti" rozetiyle
            bool disaridaDegisti = await TarihceyeIsleAsync(nesne, tanim, "okuma");

            string script = NesneScriptleyici.AlterEDonustur(tanim);

            // Tek satıra sıkışmış gövdeyi okunabilir hale getir; zaten biçimliyse dokunma.
            bool bicimlendi = SqlBicimleyici.BicimlendirmeyeDegerMi(script);
            if (bicimlendi)
                script = SqlBicimleyici.Bicimlendir(script);

            SekmeAc(nesne.TamAd, script, nesne.Veritabani);
            Durum = disaridaDegisti
                ? $"⚠ {nesne.TamAd} son görüşünüzden beri DIŞARIDA değişmiş — sağ tık → Tarihçe ile karşılaştırın."
                : $"{nesne.TamAd} — ALTER olarak açıldı; düzenleyip F5 ile güncelleyin."
                  + (bicimlendi ? " (okunabilirlik için satırlara bölündü)" : "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Durum = $"{nesne.TamAd} tanımı alınamadı: {ex.Message}";
        }
    }

    /// <summary>Tablo/view/koleksiyon → "İlk N satır": sorgu açılır ve hemen çalışır (FG-5.7).</summary>
    public async Task IlkNSatirAsync(SemaNesnesi nesne, int n = 200)
    {
        SorguSekmesiViewModel sekme = SekmeAc(
            $"{nesne.Ad} (ilk {n})", IlkNSorgusu(nesne, n), nesne.Veritabani);
        await sekme.CalistirAsync();
    }

    /// <summary>Tablo/view → SELECT script'i (çalıştırmadan, FG-2.6) — yalnız MSSQL menüsünde.</summary>
    public void SelectScriptiAc(SemaNesnesi nesne, int n = 200)
        => SekmeAc(nesne.Ad, IlkNSorgusu(nesne, n), nesne.Veritabani);

    /// <summary>Ağaçtan "INSERT örneği" (#11, 2026-07-29): tablonun yazılabilir kolonları için çok
    /// satırlı, tip-farkında INSERT şablonu (identity/computed/rowversion atlanır). Kolon üst verisi
    /// (identity/computed) DuzenlemeMetaAsync'ten gelir → tek DB çağrısı; çalıştırmadan sekmede açılır.</summary>
    public async Task InsertSablonuAcAsync(SemaNesnesi nesne, int satirSayisi = 3)
    {
        if (_profil is null || !MotorScriptDestekli)
            return;
        try
        {
            DuzenlemeMetasi meta = await _schemaService.DuzenlemeMetaAsync(_profil, nesne, CancellationToken.None);
            SekmeAc(nesne.Ad, NesneScriptleyici.InsertSablonu(
                meta, satirSayisi, _lehceler.Getir(_profil.Motor)), nesne.Veritabani);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Durum = $"{nesne.TamAd}: INSERT örneği üretilemedi — {ex.Message}";
        }
    }

    /// <summary>Ağaçtan "UPDATE örneği" (v20-S21 saha m.29): INSERT örneğinin eşi — SET'te yazılabilir
    /// kolonlar tip-farkında örnek değerlerle, WHERE PK üzerinden; çalıştırmadan sekmede açılır.</summary>
    public async Task UpdateSablonuAcAsync(SemaNesnesi nesne)
    {
        if (_profil is null || !MotorScriptDestekli)
            return;
        try
        {
            DuzenlemeMetasi meta = await _schemaService.DuzenlemeMetaAsync(_profil, nesne, CancellationToken.None);
            SekmeAc(nesne.Ad, NesneScriptleyici.UpdateSablonu(
                meta, _lehceler.Getir(_profil.Motor)), nesne.Veritabani);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Durum = $"{nesne.TamAd}: UPDATE örneği üretilemedi — {ex.Message}";
        }
    }

    /// <summary>
    /// MOTORA GÖRE "ilk N" sorgusu (kullanıcı bulgusu 2026-07-18: PostgreSQL'de sağ tık →
    /// İlk 200 satır, T-SQL script üretip hata veriyordu). Koleksiyon (Mongo) find JSON'u
    /// alır; SQL ailesinde lehçe kendi satır sınırlama ve tırnaklama biçimini verir.
    /// </summary>
    private string IlkNSorgusu(SemaNesnesi nesne, int n)
        => nesne.Tur == SemaNesneTuru.Koleksiyon || _profil is null
            ? NesneScriptleyici.SelectScripti(nesne, n)
            : _lehceler.Getir(_profil.Motor).IlkNSatirSorgusu(nesne, n);

    /// <summary>SP → EXEC iskeleti; parametresizse doğrudan çalıştırılır (FG-5.6/5.7).</summary>
    public async Task SpCalistirAsync(SemaNesnesi sp)
    {
        SorguSekmesiViewModel sekme = SekmeAc(
            $"EXEC {sp.Ad}", NesneScriptleyici.ExecIskeleti(sp), sp.Veritabani);

        if (sp.Parametreler.Count == 0)
        {
            await sekme.CalistirAsync();
            return;
        }

        Durum = $"{sp.TamAd}: {sp.Parametreler.Count} parametre — değerleri doldurup F5'e basın.";
    }

    /// <summary>Ağaç kökü: kullanıcı veritabanları + (varsa) "Sistem Veritabanları" klasörü.</summary>
    public ObservableCollection<object> KokDugumler { get; } = [];

    private readonly List<GezginVeritabani> _tumDugumler = [];

    [ObservableProperty] private string _arama = "";
    [ObservableProperty] private bool _yukleniyor;
    [ObservableProperty] private string _durum = "Hazır";

    /// <summary>
    /// Kirli okuma anahtarı (FG-6.4) — tüm sekmelerde ortak. Açıkken sorgular
    /// READ UNCOMMITTED izolasyonuyla çalışır (kilit almaz ama kirli okur).
    /// Başlangıcı AÇIK (kullanıcı isteği 2026-07-26: "uygulama her açıldığında NOLOCK
    /// seçili gelsin"); MSSQL dışı motora bağlanınca yine kapanır (T-SQL'e özgü).
    /// </summary>
    [ObservableProperty] private bool _kirliOkuma = true;

    /// <summary>Arama bir şey eşleştirmedi mi — "eşleşme yok" mesajını gösterir (03 §6).</summary>
    [ObservableProperty] private bool _aramaBos;

    // --- Panel görünürlükleri (03 §4: Ctrl+B / Ctrl+R) ---
    [ObservableProperty] private GridLength _gezginGenisligi = new(300);
    [ObservableProperty] private GridLength _sonucYuksekligi = new(220);

    public bool GezginGorunur => GezginGenisligi.Value > 0;
    public bool SonucGorunur => SonucYuksekligi.Value > 0;

    partial void OnGezginGenisligiChanged(GridLength value) => OnPropertyChanged(nameof(GezginGorunur));
    partial void OnSonucYuksekligiChanged(GridLength value) => OnPropertyChanged(nameof(SonucGorunur));

    [RelayCommand]
    public void GezginiGizleGoster()
        => GezginGenisligi = GezginGorunur ? new GridLength(0) : new GridLength(300);

    [RelayCommand]
    public void SonuclariGizleGoster()
        => SonucYuksekligi = SonucGorunur ? new GridLength(0) : new GridLength(220);

    // --- Sol komut rayı açık/kapalı (kullanıcı isteği 2026-07-24): kapalıyken yalnız renkli
    //     çipler (ikon), etiketler gizli; genişlik 216→72. AÇILIŞTA KAPALI (kullanıcı isteği
    //     2026-07-24): editör hemen geniş. HOVER davranışı (kullanıcı isteği 2026-07-25):
    //     sabit değilken fare üstüne gelince açılır, çekilince kapanır; ☰ SABİTLER (hep açık),
    //     tekrar basınca sabit kalkar ve hover moduna dönülür. ---
    [ObservableProperty] private bool _rayGenisMi;

    /// <summary>Ray ☰ ile SABİT açık mı — sabitken hover kapatmaz, değilken genişleme hover'a bağlı.</summary>
    private bool _raySabit;

    /// <summary>
    /// ☰ düğmesi: rayı SABİT açık yapar / sabitliği kaldırır (2026-07-25 — eskiden düz aç/kapaydı).
    /// Sabitlik kalkınca ray hemen kapanır; fare bir sonraki gelişinde hover yine açar.
    /// </summary>
    [RelayCommand]
    public void RayAcKapa()
    {
        _raySabit = !_raySabit;
        RayGenisMi = _raySabit;
    }

    /// <summary>Fare rayın üstüne girince/çıkınca (code-behind köprüsü): SABİT değilken hover genişletir.</summary>
    public void RayHover(bool icinde)
    {
        if (!_raySabit)
            RayGenisMi = icinde;
    }

    [RelayCommand]
    public void AramayiTemizle() => Arama = "";

    partial void OnAramaChanged(string value)
    {
        List<GezginVeritabani> yukluler = [.. _tumDugumler.Where(v => v.Yuklendi)];
        foreach (GezginVeritabani vt in yukluler)
            GruplariKur(vt);

        AramaBos = value.Trim().Length > 0
                && yukluler.Count > 0
                && yukluler.All(v => v.Gruplar.All(g => g.Nesneler.Count == 0));
    }

    /// <summary>Oturum geri yükleme yalnız ilk bağlantıda denenir (V2-S2, FG-3.6).</summary>
    private bool _oturumDenendi;

    /// <summary>＋ butonu görünürlüğü (2026-07-31): yalnız lehçesi CREATE DATABASE üreten motorlarda
    /// (MSSQL/PG/MySQL). Oracle/Mongo'da gizli — çoklu-motor kuralı (çalışmayacak özellik kapatılır).</summary>
    public bool YeniVeritabaniDestekli =>
        _profil is { } p && p.Motor != MotorTuru.Mongo
        && _lehceler.Getir(p.Motor).VeritabaniOlusturSql("x") is not null;

    /// <summary>
    /// Yeni veritabanı oluşturur (kullanıcı isteği 2026-07-31: "yeni database oluşturacağımız alan
    /// yok"). Başarıda profil yeniden yüklenir — ağaç ve tüm DB listeleri yeni veritabanını görür.
    /// </summary>
    public async Task YeniVeritabaniOlusturAsync(string ad)
    {
        if (_profil is not { } p || p.Motor == MotorTuru.Mongo)
            return;
        if (_lehceler.Getir(p.Motor).VeritabaniOlusturSql(ad) is not { } sql)
        {
            Durum = "Bu motorda uygulama içinden veritabanı oluşturma desteklenmiyor.";
            return;
        }

        Durum = $"[{ad}] oluşturuluyor…";
        QueryResult sonuc = await _executor.ExecuteAsync(p, sql, new ExecuteOptions(), CancellationToken.None);
        if (sonuc.Hata is { } hata)
        {
            Durum = $"⚠ Veritabanı oluşturulamadı: {hata.Mesaj}";
            return;
        }

        Durum = $"✔ [{ad}] oluşturuldu.";
        await ProfilYukleAsync(p); // ağaç + DB listeleri tazelensin (yeni DB görünsün)
    }

    public async Task ProfilYukleAsync(ConnectionProfile profil)
    {
        _profilNesli++; // uçuştaki eski yüklemeler sonuçlarını bıraksın (yarış bekçisi)
        // BAĞLANTI DEĞİŞİMİ (kullanıcı bulgusu 2026-07-18): önceki profilin çalışma alanı
        // yeni bağlantıya sızmamalı — sekmeler, şema önbellekleri ve geçmiş listesi o profile
        // aittir. Önceki oturum kendi profiline kaydedilir, sonra ekran sıfırlanır.
        bool profilDegisti = _profil is not null && _profil.Id != profil.Id;
        if (profilDegisti)
        {
            OturumuKaydet();                 // eski profilin sekmeleri kendi kaydına yazılır
            await SekmeleriKapatAsync();

            // Gezgin ARAMA KUTUSU da sıfırlanır (kullanıcı bulgusu 2026-07-19): bir profilde
            // tablo adı arayıp başka profile geçince filtre metni ekranda kalıyordu ve yeni
            // bağlantının ağacı o eski filtreyle süzülü görünüyordu — kullanıcı "tablolarım
            // nerede?" diye bakıyordu. Filtre PROFİLE aittir, tıpkı sekmeler ve geçmiş gibi.
            // Ağaç temizlenmeden ÖNCE sıfırlanır ki süzme mantığı tutarlı durumda koşsun.
            Arama = "";
            AramaBos = false;

            _onbellekler.Clear();            // şema önbelleği db adına göreydi — motorlar arası sızardı
            OnbellekTemizlendi?.Invoke();    // m.10: lookup sözlükleri de bayatlar
            GecmisKayitlari.Clear();
            KokDugumler.Clear();       // gezgin ağacı (veritabanı düğümleri buradan asılı)
            VeritabaniAdlari.Clear();
            _varsayilanVeritabani = null;
            _sekmeSayaci = 0;
            _oturumDenendi = false;          // yeni profilin kendi sekmeleri geri yüklensin
        }

        _profil = profil;
        OnPropertyChanged(nameof(BaglantiVar)); // v22-S3: menü öğeleri (👁 Önizleme) buna bakar
        // Motor DEĞİŞMEDİYSE MotorMssqlMu setter'ı tetiklenmez ama _profil null→dolu geçişi bu
        // kapıları yine değiştirir (ilk bağlantı MSSQL ise düğmeler görünmezdi — v23 FTS ile fark edildi).
        OnPropertyChanged(nameof(ProfilerGorunur));
        OnPropertyChanged(nameof(FtsGorunur));
        MotorMssqlMu = profil.Motor == MotorTuru.Mssql;
        MotorMongoMu = profil.Motor == MotorTuru.Mongo;
        MotorScriptDestekli = profil.Motor is not MotorTuru.Mongo; // SQL ailesinin dördü (madde 3: Oracle dahil)
        MotorBagimlilikDestekli = profil.Motor is not MotorTuru.Mongo; // SQL ailesinin dördü (2026-08-03)
        OnPropertyChanged(nameof(YeniVeritabaniDestekli)); // ＋ butonu motora göre görünür (2026-07-31)
        // Lehçe teşhis bölümü bildiriyorsa panel düğmesi o motorda da açılır (V3).
        _lehceBolumVar = profil.Motor != MotorTuru.Mongo
            && _lehceler.Getir(profil.Motor).TeshisBolumleri.Count > 0;
        OnPropertyChanged(nameof(TeshisGorunur));

        // Edit modu artık SQL ailesinin dördünde de var (V4-S1); Mongo ailesinde yok.
        _duzenlemeDestekli = profil.Motor != MotorTuru.Mongo
            && _lehceler.Getir(profil.Motor).DuzenlemeDestekler;
        OnPropertyChanged(nameof(DuzenlemeGorunur));

        // Güvenli Yazma da SQL ailesinin dördünde (V4-S2); MongoDB'de yok.
        _guvenliYazmaDestekli = profil.Motor != MotorTuru.Mongo
            && _lehceler.Getir(profil.Motor).GuvenliYazmaDestekler;
        OnPropertyChanged(nameof(GuvenliYazmaGorunur));

        // Execution plan artık BEŞ motorda da var (V5-S1a…S1e). Gerçek plan yalnız MSSQL,
        // PostgreSQL ve MongoDB'de: MySQL/Oracle'da sunucu biçimi/yetki engeli var.
        // MongoDB ayrı ailedir (ILehce yok) → yeteneği doğrudan belirlenir.
        _planDestekli = profil.Motor == MotorTuru.Mongo
            || _lehceler.Getir(profil.Motor).PlanDestekler;
        _gercekPlanDestekli = profil.Motor == MotorTuru.Mongo
            || (_planDestekli && _lehceler.Getir(profil.Motor).PlanGercekDestekler);
        OnPropertyChanged(nameof(PlanGorunur));
        OnPropertyChanged(nameof(PlanDugmeBasligi));


        // B5/A6 (2026-07-19): MongoDB'de de arama var artık — view pipeline'ları ve index
        // tanımları taranıyor (SQL ailesindeki "nesne tanımı"nın Mongo'daki karşılığı).
        _aramaDestekli = true;
        OnPropertyChanged(nameof(AramaGorunur));

        // Görsel Sorgu Tasarımcısı (v6): SQL ailesinde (JOIN/WHERE ile SELECT kurma) — MongoDB
        // ayrı ailedir, $lookup görsel tasarımı bambaşka bir problemdir, orada HİÇ görünmez.
        _gorselSorguDestekli = profil.Motor != MotorTuru.Mongo;
        OnPropertyChanged(nameof(GorselSorguGorunur));
        OnPropertyChanged(nameof(HaritaGorunur)); // harita her motorda (Mongo koleksiyon kartları — v9-S5)
        OnPropertyChanged(nameof(LogAnalizGorunur)); // log analizi de her bağlı motorda (Mongo dahil — 2026-07-25)
        OnPropertyChanged(nameof(AsistanGorunur));   // 🤖 asistan da her bağlı motorda (v11-S1)
        OnPropertyChanged(nameof(IceAktarGorunur)); // 📥 v13-S2
        OnPropertyChanged(nameof(AktarimGorunur)); // 📦 paket aktarım SQL ailesinde (v12-S2)

        // Snippet'ler MOTORA GÖRE süzülür (V5-S4): T-SQL kalıbı Mongo sekmesinde önerilmez.
        await SnippetleriYenileAsync();

        _ayarlarYukleniyor = true;
        // Ayarlar PROFİL kapsamlı (V3): profil değeri yoksa genel değere düşer.
        // Tema bilinçli olarak GENEL kalır — görünüm bağlantıya göre değişmez.
        Guid? kapsam = profil.Id;
        GecmisAcik = await _ayarDeposu.BoolOkuAsync(AyarAnahtari.GecmisAcik, varsayilan: true, kapsam);
        GuvenliYazma = await _ayarDeposu.BoolOkuAsync(AyarAnahtari.GuvenliYazmaAcik, varsayilan: false, kapsam);
        GuvenliYazmaRollbackSn = await _ayarDeposu.IntOkuAsync(AyarAnahtari.GuvenliYazmaRollbackSn, 300, kapsam);
        KoyuTema = await _ayarDeposu.BoolOkuAsync(AyarAnahtari.KoyuTema, varsayilan: false);
        _ayarlarYukleniyor = false;

        // T-SQL'e özgü anahtarlar MSSQL dışı motorda kapalı kalır (tercih ezilmez — yalnız bu oturum).
        if (!MotorMssqlMu)
        {
            _ayarlarYukleniyor = true;
            GuvenliYazma = false;
            KirliOkuma = false;
            _ayarlarYukleniyor = false;
        }

        // Geçmiş paneli AÇIKKEN bağlantı değiştirilirse eski profilin sorguları ekranda
        // kalıyordu (kullanıcı bulgusu 2026-07-18) — yeni profilin geçmişiyle tazelenir.
        if (GecmisGorunur)
            await GecmisiYenileAsync();

        await YenileAsync();
    }

    /// <summary>Veritabanı listesini tazeler; profilin veritabanını otomatik açar (FG-2.4: yenile = önbelleği tazele).</summary>
    public async Task YenileAsync()
    {
        if (_profil is null)
            return;

        int nesil = _profilNesli; // yarış bekçisi: dönüşte profil değiştiyse sonuç çöpe
        Yukleniyor = true;
        Durum = "Veritabanları listeleniyor…";
        _onbellekler.Clear();
        OnbellekTemizlendi?.Invoke(); // m.10: lookup sözlükleri de bayatlar
        KokDugumler.Clear();
        _tumDugumler.Clear();
        try
        {
            IReadOnlyList<VeritabaniBilgisi> bilgiler =
                await _schemaService.VeritabanlariAsync(_profil, CancellationToken.None);
            if (nesil != _profilNesli)
                return; // bu arada başka profile geçildi — eski sunucunun listesi ekrana yazılmaz

            List<VeritabaniBilgisi> sistemler = [.. bilgiler.Where(b => b.SistemMi)];
            var klasor = new GezginSistemKlasoru { Baslik = $"Sistem Veritabanları ({sistemler.Count})" };

            VeritabaniAdlari.Clear();
            foreach (VeritabaniBilgisi bilgi in bilgiler)
            {
                VeritabaniAdlari.Add(bilgi.Ad);
                var dugum = new GezginVeritabani { Ad = bilgi.Ad };
                dugum.PropertyChanged += VeritabaniAcildi;
                _tumDugumler.Add(dugum);
                if (bilgi.SistemMi)
                    klasor.Veritabanlari.Add(dugum);
                else
                    KokDugumler.Add(dugum);
            }

            if (klasor.Veritabanlari.Count > 0)
                KokDugumler.Add(klasor); // kapalı başlar, listenin sonunda durur

            // Yeni sekmelerin varsayılanı: ilk kullanıcı veritabanı, yoksa master
            _varsayilanVeritabani = bilgiler.FirstOrDefault(b => !b.SistemMi)?.Ad ?? bilgiler.FirstOrDefault()?.Ad;

            // Sunucu değişmiş olabilir: sekmelerin seçili veritabanı listede yoksa varsayılana çek
            foreach (SorguSekmesiViewModel sekme in Sekmeler.OfType<SorguSekmesiViewModel>())
            {
                if (sekme.SecilenVeritabani is null || !VeritabaniAdlari.Contains(sekme.SecilenVeritabani))
                    sekme.SecilenVeritabani = _varsayilanVeritabani;
            }

            // İlk bağlantı: önceki oturumun sekmeleri geri gelir (FG-3.6); yoksa boş sekme.
            if (!_oturumDenendi)
            {
                _oturumDenendi = true;
                await OturumuGeriYukleAsync();
            }
            if (Sekmeler.Count == 0)
                YeniSekme(); // bağlanınca boş bir sorgu sekmesi hazır bekler

            if (Durum.EndsWith("geri yüklendi.", StringComparison.Ordinal) == false)
                Durum = $"{bilgiler.Count} veritabanı";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            if (nesil == _profilNesli)
                Durum = $"Veritabanları listelenemedi: {ex.Message}";
        }
        finally
        {
            if (nesil == _profilNesli)
                Yukleniyor = false; // bayat çağrı, yeni yüklemenin spinner'ını söndürmesin
        }
    }

    private void VeritabaniAcildi(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GezginVeritabani.AcikMi)
            && sender is GezginVeritabani { AcikMi: true, Yuklendi: false } dugum)
        {
            _ = VeritabaniYukleAsync(dugum); // hata yönetimi metot içinde
        }
    }

    public async Task VeritabaniYukleAsync(GezginVeritabani dugum)
    {
        if (_profil is null || dugum.Yuklendi)
            return;

        dugum.Yuklendi = true; // eşzamanlı ikinci tetiklemeyi kes
        dugum.Bilgi = "yükleniyor…";
        int nesil = _profilNesli; // yarış bekçisi: dönüşte profil değiştiyse önbelleğe yazma
        var sure = Stopwatch.StartNew();
        try
        {
            // Madde 1 (2026-07-30): Mongo'da koleksiyon ADLARI hızlı (tek round-trip), $sample ALAN
            // envanteri yavaş → önce adları göster (ağaç anında dolar), sonra tam envanteri yükle.
            // Adlar ekranda dururken alanlar arka planda gelir. Diğer motorlarda AdlariYukleAsync =
            // YukleAsync (tek batch) → bu dal atlanır, davranış değişmez.
            if (_profil.Motor == MotorTuru.Mongo)
            {
                SemaOnbellegi adlar = await _schemaService.AdlariYukleAsync(_profil, dugum.Ad, CancellationToken.None);
                if (nesil != _profilNesli)
                    return; // profil değişti — eski sunucunun şeması yeni önbelleğe sızmasın
                _onbellekler[dugum.Ad] = adlar;
                GruplariKur(dugum);                  // koleksiyon adları ANINDA görünür
                dugum.Bilgi = "alanlar yükleniyor…";
            }

            SemaOnbellegi tam = await SemaVeFkYukleAsync(dugum.Ad, CancellationToken.None);
            if (nesil != _profilNesli)
                return;
            _onbellekler[dugum.Ad] = tam;
            GruplariKur(dugum);
            dugum.Bilgi = null;
            Durum = $"{dugum.Ad}: {tam.Nesneler.Count} nesne · {sure.ElapsedMilliseconds} ms";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            dugum.Yuklendi = false; // tekrar açılınca yeniden denesin
            dugum.Bilgi = "yüklenemedi";
            if (nesil != _profilNesli)
                return; // bayat çağrının hatası yeni profilin durumunu/önbelleğini bozmasın
            Durum = $"{dugum.Ad} yüklenemedi: {ex.Message}";
            // İnceleme 2026-07-30: Mongo iki fazlı yüklemede faz 2 düşerse faz-1'in ALANSIZ önbelleği
            // kalıcı kalıyordu (tamamlama/asistan bir daha alan göremezdi) — eksik girdiyi sil ki
            // bir sonraki istek tam yüklemeyi yeniden denesin.
            _onbellekler.Remove(dugum.Ad);
        }
    }

    /// <summary>Anlık arama (FG-2.3): yazdıkça, yüklü veritabanlarında tam ad üzerinden süzer.</summary>
    private void GruplariKur(GezginVeritabani dugum)
    {
        dugum.Gruplar.Clear();
        if (!_onbellekler.TryGetValue(dugum.Ad, out SemaOnbellegi? onbellek))
            return;

        // v20-S4 "kolon arama" (kullanıcı isteği 2026-08-05): süzgeç nesne ADININ yanı sıra KOLON
        // adlarıyla da eşleşir — "bir kolon hangi tablolarda var" sorusu tek kutuda yanıtlanır
        // (kolonlar zaten bellekte; ağ yok). Eşleşen kolon gezginde KALIN gösterilir (XAML converter).
        string suzgec = Arama.Trim();
        List<SemaNesnesi> nesneler = suzgec.Length == 0
            ? [.. onbellek.Nesneler]
            : [.. onbellek.Nesneler.Where(n =>
                n.TamAd.Contains(suzgec, StringComparison.OrdinalIgnoreCase)
                || n.Kolonlar.Any(k => k.Ad.Contains(suzgec, StringComparison.OrdinalIgnoreCase)))];

        GrupEkle("Tablolar", SemaNesneTuru.Tablo);
        GrupEkle("View'lar", SemaNesneTuru.View);
        GrupEkle("Stored Procedure'ler", SemaNesneTuru.StoredProcedure);
        GrupEkle("Fonksiyonlar", SemaNesneTuru.Fonksiyon);
        // MongoDB (V3-S2): koleksiyon grubu yalnız koleksiyon VARSA eklenir —
        // MSSQL ağacında "Koleksiyonlar (0)" gürültüsü olmaz.
        if (nesneler.Any(n => n.Tur == SemaNesneTuru.Koleksiyon))
            GrupEkle("Koleksiyonlar", SemaNesneTuru.Koleksiyon);

        void GrupEkle(string baslik, SemaNesneTuru tur)
        {
            List<SemaNesnesi> grupNesneleri = [.. nesneler.Where(n => n.Tur == tur)];
            dugum.Gruplar.Add(new GezginGrubu
            {
                Baslik = $"{baslik} ({grupNesneleri.Count})",
                Nesneler = grupNesneleri,
            });
        }
    }
}
