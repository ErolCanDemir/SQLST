using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SQLST.App.ViewModels;
using SQLST.App.Views;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;
using Serilog;

namespace SQLST.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    /// <summary>
    /// 📂 Çift tık / "Birlikte aç" köprüsü (v23-S7 — kullanıcı bulgusu 28 Eyl 2026: "var olan
    /// bir sql dosyasını bizde açamıyoruz"): komut satırından gelen .sql/.txt yolları burada
    /// bekler; bağlantı kurulup ana pencere açılınca <c>MainWindow.ProfilUygula</c> BİR kez
    /// sekmelerde açar (aynı kodlama çözümü zinciri — Ctrl+O/sürükle-bırak ile birebir yol).
    /// </summary>
    public static string[]? BekleyenDosyalar { get; set; }

    /// <summary>Argümanlardan açılabilir dosyaları süzer: yalnız VAR OLAN .sql/.txt
    /// (Windows "%1" tek yol gönderir ama çoklu seçim/gelecek senaryolar için hepsi taranır).</summary>
    public static string[] AcilacakDosyalar(string[] args)
        => [.. args.Where(a =>
        {
            try
            {
                return Path.GetExtension(a).ToLowerInvariant() is ".sql" or ".txt" && File.Exists(a);
            }
            catch (ArgumentException)
            {
                return false; // geçersiz yol karakterleri (örn. başka amaçlı bir anahtar) — dosya değil
            }
        })];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        KulturuKur();
        BekleyenDosyalar = AcilacakDosyalar(e.Args);

        // Veri klasörünü UygulamaVeriYolu verir; ilk erişimde %APPDATA%\MiniSSMS →
        // %APPDATA%\SQLST göçünü de o yapar (ad değişimi 2026-07-20).
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(UygulamaVeriYolu.GunlukKlasoru, "sqlst-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        // Göç başarısızsa SESSİZ KALINMAZ: uygulama boş profil listesiyle açılırsa kullanıcı
        // verilerinin silindiğini sanır. Eski klasör diskte durur ve kurtarılabilir.
        if (UygulamaVeriYolu.GocHatasi is { } gocHatasi)
        {
            Log.Error("Veri klasörü göçü başarısız: {Hata}", gocHatasi);
            Iletisim.Uyari(null, "SQLST — veri taşınamadı", "Eski veri klasörü taşınamadı",
                "Eski sürümün veri klasörü (%APPDATA%\\MiniSSMS) yeni adına (%APPDATA%\\SQLST) taşınamadı:\n\n"
                + gocHatasi + "\n\nProfilleriniz ve geçmişiniz SİLİNMEDİ — eski klasörde duruyor. "
                + "Uygulama şimdilik boş verilerle açılacak.");
        }

        // FOG-7: beklenmeyen hata uygulamayı düşürmez — logla, kibarca bildir.
        DispatcherUnhandledException += UiHatasi;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Gözlenmemiş task hatası");
            args.SetObserved();
        };
        // Madde 3 (2026-07-30): Dispatcher/Task DIŞI thread'deki ölümcül hata (ör. arka plan sonuç
        // materyalizasyonunda OOM) süreci SESSİZCE öldürmesin — logla (02-mimari §139'daki söz).
        // Handler süreci KURTARAMAZ (IsTerminating genelde true) ama teşhis izi bırakır.
        // v20-S15 sertleştirme: OOM sırasında Serilog'un KENDİSİ de düşebilir (tahsis ister) —
        // her adım ayrı try'da + ham dosyaya kısa iz; teşhissiz çökme kalmasın.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is not Exception ex)
                return;
            try
            {
                Log.Fatal(ex, "AppDomain işlenmemiş hata (IsTerminating={Terminating})", args.IsTerminating);
                Log.CloseAndFlush();
            }
            catch { /* OOM: loglayıcı da tahsis edemeyebilir — aşağıdaki ham iz denenir */ }
            try
            {
                File.AppendAllText(
                    Path.Combine(UygulamaVeriYolu.GunlukKlasoru, "son-cokme.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch { /* son çare de düşerse yapacak bir şey yok — süreç zaten ölüyor */ }
        };

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(HizmetleriKur)
            .UseSerilog()
            .Build();

        BaslangicDevami();
    }

    /// <summary>
    /// DI kayıtları — hem üretim başlangıcı hem de test/ekran-görüntüsü harness'ı aynı grafı kursun diye
    /// ayrı statik metot (2026-07-27). Buraya eklenen her servis MainWindow'u kurabilmek için gereklidir.
    /// </summary>
    internal static void HizmetleriKur(IServiceCollection services)
    {
                services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
                services.AddSingleton<IProfileStore, JsonProfileStore>();
                // Çoklu motor (V3-S1/Faz 3): üst sözleşme TEK, altta iki aile (08-v3r1 §3) —
                // SQL ailesi (ADO.NET + lehçe sağlayıcı) ve Mongo ailesi; yönlendirici
                // profilin motoruna göre seçer, çağıranlar aile ayrımını görmez.
                services.AddSingleton<ILehceSaglayici>(sp => new LehceSaglayici(sp.GetRequiredService<ISecretProtector>()));
                services.AddSingleton<ISqlExecutor>(sp => new MotorYonlendiriciExecutor(
                    new SqlExecutor(sp.GetRequiredService<ILehceSaglayici>()),
                    new MongoExecutor(sp.GetRequiredService<ISecretProtector>())));
                services.AddSingleton<IOturumFabrikasi>(sp => new MotorYonlendiriciOturumFabrikasi(
                    new OturumFabrikasi(sp.GetRequiredService<ILehceSaglayici>()),
                    new MongoOturumFabrikasi(sp.GetRequiredService<ISecretProtector>())));
                services.AddSingleton<YerelDepo>();
                services.AddSingleton<IAyarDeposu, SqliteAyarDeposu>();
                services.AddSingleton<ISorguGecmisiDeposu, SqliteSorguGecmisiDeposu>();
                services.AddSingleton<IOturumDeposu, SqliteOturumDeposu>();
                services.AddSingleton<ITarihceDeposu, SqliteTarihceDeposu>();
                services.AddSingleton<ISnippetDeposu, SqliteSnippetDeposu>();
                services.AddSingleton<QueryService>();
                services.AddSingleton<ISchemaService>(sp => new MotorYonlendiriciSchemaService(
                    new SchemaService(sp.GetRequiredService<ISqlExecutor>(), sp.GetRequiredService<ILehceSaglayici>()),
                    new MongoSchemaService(sp.GetRequiredService<ISecretProtector>())));
                services.AddSingleton<TeshisServisi>();
                services.AddSingleton<MongoTeshisServisi>();
                // 🤖 Asistan (v11-S1; v22-S6): 🏠 Yerel/Ollama (varsayılan) + OpenAI-uyumlu uç
                services.AddSingleton<IAsistanServisi, AsistanYonlendirici>();
                // 📦 Paket Aktarim (v12): akisli okuma + toplu parametreli INSERT motoru;
                // Mongo ailesi icin ayri motor (S5 — koleksiyon→koleksiyon arsivleme)
                services.AddSingleton<AktarimServisi>();
                services.AddSingleton<MongoAktarimServisi>();
                // 📤 Şema Kopyalama (2026-07-31): kaynak DB şemasını başka bağlantıya kurar (yalnız şema)
                services.AddSingleton<SemaKopyalamaServisi>();
                // 📥 Excel/TXT Import (v13): dosyadan tabloya, kulturlu tip donusumuyle
                services.AddSingleton<DosyaAktarimServisi>();
                // 🔌 SOAP Istemcisi (v14): WSDL indirme + SOAP cagri katmani
                services.AddSingleton<SoapIstemcisi>();
                services.AddSingleton<ISoapDeposu, SqliteSoapDeposu>();
                // 🌐 REST Istemcisi (v20-S8): HTTP gonderim + gecmis/ortam/kayitli depo (SOAP kardesi)
                services.AddSingleton<RestIstemcisi>();
                services.AddSingleton<IRestDeposu, SqliteRestDeposu>();
                // ⏪ Geri Al paketi (V15-S3): Güvenli Yazma COMMIT'inden önce eski satırlar
                services.AddSingleton<IGeriAlDeposu, SqliteGeriAlDeposu>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddTransient<BaglantiEkraniViewModel>();
                services.AddTransient<BaglantiPenceresi>(sp => new BaglantiPenceresi(
                    sp.GetRequiredService<BaglantiEkraniViewModel>(),
                    sp.GetRequiredService<MainWindow>));
                services.AddSingleton<Func<BaglantiPenceresi>>(
                    sp => sp.GetRequiredService<BaglantiPenceresi>);
    }

    /// <summary>DI kurulduktan sonrası: başlık-çubuğu teması + bağlantı ekranı + renklendirme ön-yükleme.</summary>
    private void BaslangicDevami()
    {
        // HER pencere yüklenince başlık çubuğunu temaya uydur. Sınıf düzeyinde kanca
        // kullanılıyor çünkü tek tek pencerelere eklemek unutulmaya açıktır — nitekim
        // koyu temada beyaz başlık çubuğu sorunu böyle doğdu. Sınıf işleyicileri TÜRETİLMİŞ
        // pencereleri de yakalar (örtük Style'ın aksine: o yalnız tam tip eşleşmesinde çalışır).
        EventManager.RegisterClassHandler(
            typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((gonderen, _) =>
            {
                if (gonderen is Window pencere)
                    BaslikCubuguRengi(pencere, KoyuTemaAcik);
            }));

        // Karanlık tema tercihi pencere açılmadan uygulanır (V2-S10; sonrası canlı takas).
        // v19-S9: palet VARYANT tercihleri de okunur (açık/koyu ayrı — 5'er seçenek).
        IAyarDeposu ayarlar = _host.Services.GetRequiredService<IAyarDeposu>();
        bool koyu = ayarlar.BoolOkuAsync(AyarAnahtari.KoyuTema, varsayilan: false).GetAwaiter().GetResult();
        SeciliAcikPalet = ayarlar.OkuAsync(AyarAnahtari.AcikPalet).GetAwaiter().GetResult() ?? "indigo";
        SeciliKoyuPalet = ayarlar.OkuAsync(AyarAnahtari.KoyuPalet).GetAwaiter().GetResult() ?? "indigo";
        TemaUygula(koyu);

        // 03-ui-tasarim §6: bağlantı yokken ana pencere yerine bağlantı ekranı açılır.
        _host.Services.GetRequiredService<BaglantiPenceresi>().Show();

        // İlk sekmenin doğuşunu hızlandır: TSQL renklendirme tanımını (xshd parse)
        // pencere açıldıktan sonra boşta önceden yükle (kasma geri bildirimi, 2026-07-16).
        Dispatcher.BeginInvoke(
            () => _ = ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("TSQL"),
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Palet takası (V2-S10): MergedDictionaries[0] = palet, [1] = stiller (App.xaml).
    /// Tüm renk başvuruları DynamicResource olduğundan geçiş uygulama açıkken anında işler.
    /// v19-S9: kip başına 5 palet varyantı — aktif kipin SEÇİLİ varyant dosyası yüklenir.
    /// </summary>
    public static void TemaUygula(bool koyu)
    {
        // Pack URI (v19-S9): göreli URI test host'unda (Application ana derlemesi SQLST.App
        // olmadığında) çözülemiyordu — tam pack adresi iki ortamda da çalışır. Test host'unda
        // sözlük listesi boş da olabilir → [0] yerine güvenli ekle/değiştir.
        string dosya = PaletDosyasi(koyu, koyu ? SeciliKoyuPalet : SeciliAcikPalet);
        var palet = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SQLST.App;component/{dosya}", UriKind.Absolute),
        };
        if (Current.Resources.MergedDictionaries.Count == 0)
            Current.Resources.MergedDictionaries.Add(palet);
        else
            Current.Resources.MergedDictionaries[0] = palet;

        KoyuTemaAcik = koyu;
        // v23-S14: Antrasit = klasik (B) DÜZEN — şablonlar TemaDurumu.Klasik'e tetikleyiciyle bağlı.
        TemaDurumu.Klasik = TemaDurumu.KlasikAileMi(koyu ? SeciliKoyuPalet : SeciliAcikPalet);

        // Başlık çubuğu da temayla birlikte döner (kullanıcı bulgusu 2026-07-19: koyu temada
        // pencere gövdesi koyu ama başlık çubuğu BEYAZ kalıyordu). Başlık çubuğunu WPF değil
        // pencere yöneticisi çizer; rengi yalnız DWM özniteliğiyle istenebilir.
        foreach (Window pencere in Current.Windows)
            BaslikCubuguRengi(pencere, koyu);
    }

    /// <summary>Palet ailesi (v19-S9): kip + varyant adı → XAML dosyası. Bilinmeyen ad
    /// varsayılana (İndigo — mevcut tasarım) düşer; eski ayar dosyaları kırılmaz.</summary>
    public static string PaletDosyasi(bool koyu, string? palet) => (koyu, palet) switch
    {
        (true, "kor") => "PaletKoyuKor.xaml",
        (true, "grafit") => "PaletKoyuGrafit.xaml",
        (true, "slate") => "PaletKoyuSlate.xaml",
        (true, "petrol") => "PaletKoyuPetrol.xaml",
        (true, "amber") => "PaletKoyuAmber.xaml",
        (true, "vs") => "PaletKoyuVS.xaml",       // v22-S15: kullanıcı 12 mockup'tan 6'yı seçti
        (true, "antrasit") => "PaletKoyuAntrasit.xaml", // v23-S14: eski tema çalışmasındaki "B"
        (true, _) => "PaletKoyu.xaml",
        (false, "kor") => "PaletAcikKor.xaml",
        (false, "grafit") => "PaletAcikGrafit.xaml",
        (false, "slate") => "PaletAcikSlate.xaml",
        (false, "petrol") => "PaletAcikPetrol.xaml",
        (false, "amber") => "PaletAcikAmber.xaml",
        (false, "vs") => "PaletAcikVS.xaml",      // v22-S15
        (false, "antrasit") => "PaletAcikAntrasit.xaml", // v23-S14
        (false, _) => "PaletAcik.xaml",
    };

    /// <summary>Seçili palet varyantları (v19-S9) — açılışta ayardan okunur, menüden değişir.</summary>
    public static string SeciliAcikPalet { get; set; } = "indigo";

    public static string SeciliKoyuPalet { get; set; } = "indigo";

    /// <summary>Geçerli tema — yeni açılan pencereler başlık çubuğunu buna göre ayarlar.</summary>
    public static bool KoyuTemaAcik { get; private set; }

    /// <summary>
    /// Pencerenin başlık çubuğunu koyu/açık yapar (DWM). Windows 10 20H1 öncesinde öznitelik
    /// numarası farklıydı; ikisi de denenir. Desteklenmeyen sürümde <b>sessizce yok sayılır</b>
    /// — kozmetik bir isteğin uygulamayı düşürmesi kabul edilemez.
    /// </summary>
    public static void BaslikCubuguRengi(Window pencere, bool koyu)
    {
        IntPtr tanitici = new System.Windows.Interop.WindowInteropHelper(pencere).Handle;
        if (tanitici == IntPtr.Zero)
        {
            // Henüz gösterilmemiş: tanıtıcı oluşunca yeniden dene.
            pencere.SourceInitialized += (_, _) => BaslikCubuguRengi(pencere, koyu);
            return;
        }

        int deger = koyu ? 1 : 0;
        foreach (int oznitelik in (int[])[20, 19])   // 20 = 20H1+, 19 = eski derlemeler
        {
            if (DwmSetWindowAttribute(tanitici, oznitelik, ref deger, sizeof(int)) == 0)
                return;
        }
    }

    // DllImport (LibraryImport değil): LibraryImport üreteci unsafe blok üretiyor ve proje
    // AllowUnsafeBlocks açmıyor — tek bir kozmetik çağrı için o kapıyı açmaya değmez.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr pencere, int oznitelik, ref int deger, int boyut);

    protected override void OnExit(ExitEventArgs e)
    {
        // 🧭 v22-S4 (çökme denetimi): TEMİZ kapanış damgası. Bu damga olmadan logda çökme ile normal
        // çıkış BİREBİR aynı görünüyordu (ikisi de "Grid hazır" ile bitiyor) — dört turdur hangi
        // oturumun gerçekten çöktüğünü ayıramamamızın sebeplerinden biri buydu.
        SQLST.Infrastructure.Iz.TemizCikis();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// FOG-9: SQLST'nin KENDİ tarih/saat ve sayıları (süre, sayaç, durum) Türkçe biçimde görünür.
    /// ⚠ v23-S13 (kullanıcı kararı 5 Eki 2026: "hiçbir alan formatlanmasın, db nasıl ise öyle"):
    /// VERİTABANI DEĞERLERİ bu kültürden ETKİLENMEZ — grid/pano/CSV/rapor hepsi HamDeger'den geçer.
    /// İki ayar birden şart — WPF bağlamaları <see cref="FrameworkElement.LanguageProperty"/>'yi
    /// (varsayılanı en-US) kullanır, ToString() ise iş parçacığı kültürünü; ikisi ayrılırsa
    /// grid "7/16/2026 12:16:39 AM" derken pano "1250,75" yazar.
    /// Not: SQL literalleri kültürden etkilenmez — sunucuya giden metin bu ayarlardan bağımsızdır.
    /// </summary>
    private static void KulturuKur()
    {
        var tr = new CultureInfo("tr-TR");
        CultureInfo.DefaultThreadCurrentCulture = tr;
        CultureInfo.DefaultThreadCurrentUICulture = tr;
        Thread.CurrentThread.CurrentCulture = tr;
        Thread.CurrentThread.CurrentUICulture = tr;

        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(tr.IetfLanguageTag)));
    }

    private readonly HashSet<string> _gosterilenHatalar = [];
    private DateTime _sonHataAnUtc = DateTime.MinValue;

    private void UiHatasi(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Her istisna LOG'a yazılır ve YUTULUR (Handled=true): uygulama tek bir hatadan ölmez.
        Log.Error(e.Exception, "Yakalanmamış UI hatası");
        e.Handled = true;

        // ÇAĞLAMA KORUMASI (kullanıcı bulgusu 2026-07-27: bir timer istisnası onlarca modal
        // pencere açıp uygulamayı kilitliyordu, kapanışta çökertiyordu). Aynı mesaj oturumda
        // yalnız BİR KEZ gösterilir; ayrıca 3 sn içinde ikinci bir kutu açılmaz. Böylece
        // tekrarlayan bir hata en fazla tek pencere üretir — uygulama kullanılabilir kalır.
        string ozet = e.Exception.Message;
        if (_gosterilenHatalar.Contains(ozet))
            return;
        if ((DateTime.UtcNow - _sonHataAnUtc).TotalSeconds < 3)
            return;
        _gosterilenHatalar.Add(ozet);
        _sonHataAnUtc = DateTime.UtcNow;

        Iletisim.Hata(null, "SQLST", "Beklenmeyen bir hata oluştu",
            "Uygulama çalışmaya devam ediyor; ayrıntı log dosyasında.", $"{e.Exception.GetType().Name}: {ozet}");
    }
}
