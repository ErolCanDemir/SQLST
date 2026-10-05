using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit.Document;
using Serilog;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.ViewModels;

public enum SekmeDurumu
{
    Bosta,
    Calisiyor,
    Tamamlandi,
    IptalEdildi,
    Hata
}

/// <summary>
/// Tek sorgu sekmesi (03-ui-tasarim §3.2 durum makinesi): kendi SQL'i, kendi
/// sonucu, kendi çalışma durumu ve kendi iptal token'ı — diğer sekmeler serbest.
/// </summary>
public partial class SorguSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly QueryService _queryService;
    private readonly IOturumFabrikasi _oturumFabrikasi;
    private readonly ILehceSaglayici _lehceler;
    private readonly Func<ConnectionProfile?> _profilGetir;
    private readonly Func<bool> _kirliOkumaGetir;
    private readonly Func<bool> _guvenliYazmaGetir;
    private readonly Func<int> _rollbackSnGetir;
    private readonly Stopwatch _kronometre = new();
    private readonly DispatcherTimer _sayac;
    private CancellationTokenSource? _cts;
    /// <summary>Koşu nesli (v20-S21 canlı bulgu): Durdur artırır — geciken koşunun devamları
    /// UI'ya yazamaz (m.2'deki Log Analizi deseninin sorgu sekmesi uygulaması).</summary>
    private int _kosuNesli;
    private IDbOturum? _oturum;
    private Guid? _oturumProfilId;
    /// <summary>Açık Güvenli Yazma işlemini başlatan lehçe — kararı AYNI lehçeyle uygularız (V4-S2).</summary>
    private ILehce? _islemLehcesi;

    public SorguSekmesiViewModel(
        QueryService queryService,
        IOturumFabrikasi oturumFabrikasi,
        ILehceSaglayici lehceler,
        Func<ConnectionProfile?> profilGetir,
        Func<bool> kirliOkumaGetir,
        Func<bool> guvenliYazmaGetir,
        Func<int> rollbackSnGetir,
        ObservableCollection<string> veritabaniAdlari,
        string baslik)
    {
        _queryService = queryService;
        _oturumFabrikasi = oturumFabrikasi;
        _lehceler = lehceler;
        _profilGetir = profilGetir;
        _kirliOkumaGetir = kirliOkumaGetir;
        _guvenliYazmaGetir = guvenliYazmaGetir;
        _rollbackSnGetir = rollbackSnGetir;
        VeritabaniAdlari = veritabaniAdlari;
        Baslik = baslik;

        _sayac = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _sayac.Tick += (_, _) => SureMetni = "⏳ " + SonucBicimleyici.SureFormatla(_kronometre.Elapsed);

        // Hata alt çizgisi (kullanıcı isteği 2026-07-23) metin değişince söner: kullanıcı
        // hatalı satırı düzeltmeye başladı — bayat çizgi yanlış yeri işaret ederdi.
        // CANLI DENETİM (kullanıcı isteği 2026-07-26: "kırmızıyı yazarken yapabilir miyiz"):
        // yazım durunca 800 ms sonra sorgu SET PARSEONLY ile sunucuya PARSE ettirilir —
        // hiçbir şey çalışmaz, sözdizimi hatası aynı kırmızı dalgayla yazarken yanar (yalnız MSSQL).
        _canliDenetim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _canliDenetim.Tick += async (_, _) =>
        {
            _canliDenetim.Stop();
            _canliDenetim.Interval = TimeSpan.FromMilliseconds(800); // hızlı (yapıştırma) tetik tek seferlik
            await CanliDenetleAsync();
        };
        // Yapıştırma / hazır metinle açılan sekme BEKLEMEDEN denetlensin (kullanıcı isteği
        // 2026-07-30: "sorguyu yeni pencereye koyduğumda hatalar direk gelsin"): tek seferde
        // büyük ekleme (>40 karakter ≈ yapıştırma/SekmeAc) 800 ms yerine 150 ms sonra denetlenir.
        Belge.Changed += (_, e) =>
        {
            if (e.InsertionLength > 40)
                _canliDenetim.Interval = TimeSpan.FromMilliseconds(150);
        };
        Belge.TextChanged += (_, _) =>
        {
            HataSatiri = null;
            HataSatirlari = [];
            ProvaBandi = null;  // prova metne aittir — metin değişince bayat sayı yanıltmasın
            _canliDenetim.Stop();
            if (_profilGetir()?.Motor == MotorTuru.Mssql && !CalisiyorMu && Belge.TextLength > 0)
                _canliDenetim.Start();
        };
    }

    private readonly DispatcherTimer _canliDenetim;

    /// <summary>
    /// Canlı sözdizimi denetimi: belgeyi SET PARSEONLY ile kısa ömürlü bir bağlantıda parse
    /// ettirir (çalıştırma YOK — DML bile olsa hiçbir şey koşmaz; QueryService rayları bilerek
    /// devrede değil, çünkü onay soracak bir yürütme yoktur). Yarış koruması: cevap geldiğinde
    /// metin değiştiyse sonuç ATILIR (bayat çizgi yanlış satırı gösterirdi). Ağ/sunucu hataları
    /// sessiz yutulur — canlı denetim yardımcı bir konfor, engel değildir.
    /// </summary>
    internal async Task CanliDenetleAsync()
    {
        ConnectionProfile? profil = _profilGetir();
        if (profil is null || profil.Motor != MotorTuru.Mssql || CalisiyorMu)
            return;
        string metin = Belge.Text;
        if (string.IsNullOrWhiteSpace(metin))
            return;

        try
        {
            // DİKKAT — PARSEONLY tuzağı (CanliDenetimTests'in yakaladığı kusur): ON ve OFF
            // İKİSİ DE parse zamanında işlenir. Aynı batch'e "...OFF" da eklenirse parser önce
            // ON sonra OFF görür, yürütme kararında durum OFF kalır ve batch KOMPLE ÇALIŞIR
            // (DELETE dahil). OFF hiç gönderilmez; oturum await using + havuzsuz olduğundan
            // bağlantı denetim bitince ölür, ayar başka yere sızamaz.
            await using IDbOturum oturum = _oturumFabrikasi.Olustur(profil);
            QueryResult sonuc = await oturum.CalistirAsync(
                "SET PARSEONLY ON;\n" + metin,
                new ExecuteOptions { VeritabaniOverride = SecilenVeritabani },
                CancellationToken.None);

            if (Belge.Text != metin || CalisiyorMu)
                return; // bayat sonuç — kullanıcı yazmaya devam etti ya da çalıştırdı

            // Başa eklenen PARSEONLY satırı hata satırını 1 kaydırır — belgeye geri eşlenir.
            HataSatiri = sonuc.Hata is { Satir: >= 2 } h ? h.Satir - 1 : null;

            // TÜM hatalar (kullanıcı isteği 2026-07-30: "çalıştırmadan bütün hatalar gelsin"):
            // her hatalı satır dalgalanır, döküm Mesajlar'a yazılır ve sekme öne gelir — hiçbir
            // şey ÇALIŞTIRILMAZ. Metin düzelince canlı denetim kendi yazdığı dökümü temizler.
            IReadOnlyList<SqlHata> hatalar = sonuc.TumHatalar;
            HataSatirlari = [.. hatalar.Where(x => x.Satir >= 2).Select(x => x.Satir - 1).Distinct()];
            if (hatalar.Count > 0)
            {
                Mesajlar = $"✏ Canlı denetim ({hatalar.Count} hata — sorgu ÇALIŞTIRILMADI):\n" + string.Join("\n",
                    hatalar.Select(x => $"Msg {x.Numara}, Satır {Math.Max(1, x.Satir - 1)}: {x.Mesaj}"));
                SonucSekmeIndex = 1;
                _canliMesajYazildi = true;
            }
            else if (_canliMesajYazildi)
            {
                Mesajlar = ""; // hatalar düzeltildi — bayat döküm kalmasın (çalıştırma mesajına dokunulmaz)
                _canliMesajYazildi = false;
            }
        }
        catch (Exception)
        {
            // bağlantı yok/koptu vb. — canlı denetim sessiz kalır, çalıştırma yolu zaten konuşur
        }
    }

    /// <summary>Bu sekmenin kalıcı oturumunu profil için hazırlar (profil değişince yeniler).</summary>
    private IDbOturum OturumAl(ConnectionProfile profil)
    {
        if (_oturum is not null && _oturumProfilId == profil.Id)
            return _oturum;

        _ = _oturum?.DisposeAsync(); // eski oturumu (varsa açık TRAN'ı rollback ederek) bırak
        _oturum = _oturumFabrikasi.Olustur(profil);
        _oturumProfilId = profil.Id;
        return _oturum;
    }

    // --- Auto-refresh (V5-S4, FG-3.13) ---

    private System.Windows.Threading.DispatcherTimer? _otoYenileSayaci;

    /// <summary>
    /// Sorgunun belirli aralıkla yeniden çalıştırılması. <b>Yalnız OKUMA sorgularında açılır</b>
    /// — bkz. <see cref="OtoYenileAcikDegistir"/>.
    /// </summary>
    [ObservableProperty] private bool _otoYenileAcik;

    /// <summary>
    /// Yenileme aralığı (saniye). Alt sınır 2 sn: daha kısası sunucuyu döver ve sorgu
    /// süresi aralıktan uzunsa kuyruk oluşur (tetikleme sırasında çalışan varsa atlanır).
    /// </summary>
    [ObservableProperty] private int _otoYenileSaniye = 30;

    /// <summary>Son yenilemenin fark özeti — kullanıcı neyin değiştiğini görsün.</summary>
    [ObservableProperty] private string _otoYenileBandi = "";

    /// <summary>
    /// Auto-refresh anahtarı. <b>Yazma sorgusunda AÇILMAZ</b> — bu bir kolaylık değil,
    /// güvenlik kuralıdır: bir <c>DELETE</c>/<c>UPDATE</c>'in 30 saniyede bir kendiliğinden
    /// tekrar çalışması veri kaybı demektir. Tespit <see cref="QueryService.PlanIcinYazmaSayilir"/>
    /// ile yapılır — muhafazakârdır (CTE içindeki DML'i de yazma sayar, V5-S1'de eklenmişti).
    /// </summary>
    public void OtoYenileAcikDegistir(bool acik)
    {
        if (!acik)
        {
            OtoYenileAcik = false;
            _otoYenileSayaci?.Stop();
            OtoYenileBandi = "";
            return;
        }

        MotorTuru motor = _profilGetir()?.Motor ?? MotorTuru.Mssql;
        string sql = MetinSaglayici?.Invoke() ?? Belge.Text;

        if (QueryService.PlanIcinYazmaSayilir(sql, motor))
        {
            OtoYenileAcik = false;
            OtoYenileBandi = "⚠ Bu sekmede YAZMA sorgusu var — otomatik yenileme açılmaz. "
                           + "Aralıklı olarak kendiliğinden tekrar çalışsaydı veri değişirdi.";
            return;
        }

        OtoYenileAcik = true;
        OtoYenileBandi = $"Otomatik yenileme açık — {OtoYenileSaniye} sn'de bir.";
        SayacKurVeBaslat();
    }

    partial void OnOtoYenileSaniyeChanged(int value)
    {
        if (OtoYenileAcik)
            SayacKurVeBaslat();
    }

    /// <summary>Auto-refresh AÇILDIĞI ANDAKİ SQL (inceleme 2026-07-30, KRİTİK): editör TEK örnektir
    /// (TabControl şablonu) — tick anında MetinSaglayici, SEÇİLİ sekmenin (başka sekme!) metnini
    /// döndürebiliyordu; arka plandaki sekme ön plandaki DML'i sessizce çalıştırırdı. Tick artık
    /// yalnız bu anlık görüntüyü koşar.</summary>
    private string? _otoYenileSql;

    private void SayacKurVeBaslat()
    {
        _otoYenileSql = MetinSaglayici?.Invoke() ?? Belge.Text; // aç(ılış)taki metin sabitlenir
        _otoYenileSayaci ??= new System.Windows.Threading.DispatcherTimer();
        _otoYenileSayaci.Stop();
        _otoYenileSayaci.Interval = TimeSpan.FromSeconds(Math.Max(2, OtoYenileSaniye));
        _otoYenileSayaci.Tick -= OtoYenileTick;
        _otoYenileSayaci.Tick += OtoYenileTick;
        _otoYenileSayaci.Start();
    }

    private async void OtoYenileTick(object? gonderen, EventArgs e)
    {
        // Çalışan sorgu varsa TETİKLEME ATLANIR (kuyruk oluşturmaz); açık işlem varken de
        // dokunulmaz — kullanıcı COMMIT/ROLLBACK kararını verirken sorgu tekrarlanmamalı.
        if (CalisiyorMu || IslemAcik)
            return;

        if (_otoYenileSql is { Length: > 0 } sabit)
            await CalistirCoreAsync(sabit, satirTabani: 1);
    }

    /// <summary>Sekme kapanınca kalıcı bağlantıyı ve varsa açık transaction'ı bırak (V2-S1).</summary>
    public async Task KapatAsync()
    {
        _rollbackSayaci?.Stop(); // karar bekleyen işlem varsa oturum kapanışı zaten ROLLBACK eder
        // Auto-refresh sayacı da durmalı, yoksa kapanan sekmenin sorgusu koşmaya devam eder.
        _otoYenileSayaci?.Stop();
        OtoYenileAcik = false;
        IslemAcik = false;
        // Bellek (inceleme 2026-07-30 bekleyeni): sekme kapansa da VM'e sarkan bir referans
        // (olay aboneliği, WPF konteyner önbelleği…) büyük DataTable'ları canlı tutabiliyordu —
        // sonuçlar ve Geri Al yakalaması burada BİLEREK bırakılır ki GC hemen alabilsin.
        SonucSetleri = [];
        _sonYakalama = null;
        DegisiklikGosterilebilir = false;
        // v19-S11 (canlı test 2026-08-04): iptal + oturum kapanışı ARKA iş parçacığında —
        // Cancel ve Dispose (açık TRAN rollback, bağlantı kapatma) ağ turu atabilir; uzak/yavaş
        // sunucuda UI bunu beklediğinde "sekme donuyor gibi kapanıyor"du.
        IDbOturum? oturum = _oturum;
        _oturum = null;
        await Task.Run(async () =>
        {
            Iptal();
            if (oturum is not null)
                await oturum.DisposeAsync();
        });
    }

    /// <summary>
    /// Her çalıştırma bittiğinde (başarı/hata/iptal) kayıtla tetiklenir; MainViewModel
    /// geçmişe yazma ve uzun sorgu bildirimini buradan yürütür (V2-S2).
    /// </summary>
    public event Action<SorguSekmesiViewModel, GecmisKaydi>? CalistirmaTamamlandi;

    /// <summary>
    /// Başlık sorgudan türetilmeye devam edilsin mi (V2-S2 akıllı adlandırma).
    /// "sorguN.sql" sekmelerinde açık; ağaçtan açılan hazır başlıklı sekmelerde kapalı.
    /// </summary>
    public bool BaslikOtomatikMi { get; set; }

    /// <summary>AvalonEdit belgesi — undo geçmişi ve içerik sekmeye aittir.</summary>
    public TextDocument Belge { get; } = new();

    /// <summary>Ana pencerenin veritabanı listesi (paylaşılan); sekme üstündeki seçici bunu gösterir (FG-3.12).</summary>
    public ObservableCollection<string> VeritabaniAdlari { get; }

    /// <summary>View bağlar: seçili metin varsa onu, yoksa tüm metni döndürür (FG-3.3).</summary>
    public Func<string>? MetinSaglayici { get; set; }

    /// <summary>
    /// View bağlar: seçim varsa seçimin başladığı BELGE satırı, yoksa 1 (inceleme bulgusu
    /// 2026-07-23: seçili metin F5 ile koşarken hata satırı/alt çizgi belgeye eşlenmiyordu).
    /// </summary>
    public Func<int>? SecimBasiSatiri { get; set; }

    /// <summary>View bağlar: editördeki imleç ofseti (V2-S3 imleçteki statement).</summary>
    public Func<int>? ImlecSaglayici { get; set; }

    /// <summary>View bağlar: verilen aralığı editörde seçer — kullanıcı neyin çalıştığını görür.</summary>
    public Action<int, int>? AralikSec { get; set; }

    /// <summary>View bağlar: imleci verilen satıra taşır (hataya çift tık — V2-S3).</summary>
    public Action<int>? SatiraGit { get; set; }

    /// <summary>
    /// View bağlar: kullanıcıya evet/hayır sorusu (WHERE'siz DML onayı — FG-6.2).
    /// Bağlı değilse (test) çalıştırma engellenmez.
    /// </summary>
    public Func<string, bool>? OnayIste { get; set; }

    /// <summary>
    /// FG-5.5 (V2-S8): ALTER içeren sorgu çalıştırılmadan ÖNCE hedeflerin mevcut
    /// tanımı yerel tarihçeye yedeklenir — "SP'nin eski hâli neydi?" sigortası.
    /// </summary>
    public Func<string, Task>? AlterOncesiYedekle { get; set; }

    // --- Güvenli Yazma Modu karar durumu (V2-S4, Ö1) ---

    /// <summary>Açık transaction karar bekliyor — band görünür, yeni çalıştırma kilitli.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanYapilabilirMi))]
    private bool _islemAcik;
    /// <summary>Band metni: etkilenen satır + otomatik ROLLBACK geri sayımı.</summary>
    [ObservableProperty] private string _islemBandi = "";
    private DispatcherTimer? _rollbackSayaci;
    private DateTime _rollbackZamani;
    private string _islemOzeti = "";

    [ObservableProperty] private string _baslik;
    [ObservableProperty] private string? _secilenVeritabani;
    // DisaAktarilabilir bildirimi ŞART (2026-07-23 bulgusu: "Tümünü dışa aktar çalışmıyor"):
    // buton IsEnabled'ı bu türetik özelliğe bağlı; Durum/SonCalisanSql değişince bildirilmezse
    // binding sekme kurulduğundaki FALSE'ta kalır ve buton sonsuza dek gri görünür.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalisiyorMu))]
    [NotifyPropertyChangedFor(nameof(DisaAktarilabilir))]
    [NotifyPropertyChangedFor(nameof(PlanYapilabilirMi))]
    private SekmeDurumu _durum = SekmeDurumu.Bosta;
    [ObservableProperty] private string _sureMetni = "";
    [ObservableProperty] private string _sonucOzeti = "";
    [ObservableProperty] private string _mesajlar = "";
    /// <summary>Bağlantı kopması gibi geçici hatalarda "Yeniden dene" düğmesini gösterir (S6 dayanıklılık).</summary>
    [ObservableProperty] private bool _yenidenDenenebilir;
    /// <summary>Tüm result set'ler alt alta grid'lerde (FG-4.6); SP çıktıları böyle görünür.</summary>
    [ObservableProperty] private IReadOnlyList<SonucSetiGorunumu> _sonucSetleri = [];
    /// <summary>0 = Sonuçlar, 1 = Mesajlar; hata/DDL'de Mesajlar otomatik öne gelir (03 §3.2).</summary>
    [ObservableProperty] private int _sonucSekmeIndex;
    /// <summary>Satır sınırı bilgi bandı metni (FG-4.5); null = gizli.</summary>
    [ObservableProperty] private string? _bilgiBandi;
    /// <summary>
    /// Son çalıştırmada hata alınan BELGE satırı (kullanıcı isteği 2026-07-23: "hatalı sorgunun
    /// altı yansın"): editör bu satırın altına kırmızı dalga çizer. null = çizgi yok. Mongo'da
    /// kullanılmaz (satır kavramı anlamsız — kullanıcı da istemedi); metin değişince söner.
    /// </summary>
    [ObservableProperty] private int? _hataSatiri;

    /// <summary>Canlı denetimin bulduğu TÜM hatalı satırlar (kullanıcı isteği 2026-07-30: "bütün
    /// hatalar gelsin") — her biri kırmızı dalgayla çizilir; metin değişince söner.</summary>
    [ObservableProperty] private IReadOnlyList<int> _hataSatirlari = [];

    /// <summary>Mesajlar bölümünü canlı denetim mi doldurdu? (çalıştırma mesajlarını EZMEmek için)</summary>
    private bool _canliMesajYazildi;

    /// <summary>Son çalıştırmanın hata mesajı; başarıda null. 🤖 Asistan "Hatayı çözdür" (v11-S4) okur.</summary>
    public string? SonHataMesaji { get; private set; }

    public bool CalisiyorMu => Durum == SekmeDurumu.Calisiyor;

    /// <summary>Plan/Performans butonları için: sorgu çalışırken ya da açık işlem varken plan
    /// ALINAMAZ. Canlı bulgu 2026-08-09: buton devre dışı değildi → sorgu çalışırken basınca
    /// koruma "önce bitmesini bekleyin" diyordu ama yalnız durum çubuğuna → görünmez no-op; ayrıca
    /// MSSQL gerçek planı sorguyu YENİDEN koşturuyordu. Buton IsEnabled buna bağlandı.</summary>
    public bool PlanYapilabilirMi => !CalisiyorMu && !IslemAcik;

    /// <summary>Son çalıştırılan SQL — HTML raporu (Ö6) sorgu metnini buradan alır.</summary>
    public string? SonCalisanSql { get; private set; }

    [RelayCommand]
    public Task CalistirAsync()
        => CalistirCoreAsync(MetinSaglayici?.Invoke() ?? Belge.Text,
            satirTabani: SecimBasiSatiri?.Invoke() ?? 1); // seçimle F5: hata satırı belgeye eşlensin

    /// <summary>
    /// İmlecin üzerindeki üst-seviye statement'ı çalıştırır (V2-S3, Ctrl+Enter);
    /// çalışan aralık editörde seçilir, hata satırları belgeye göre eşlenir.
    /// </summary>
    [RelayCommand]
    public async Task ImlectekiniCalistirAsync()
    {
        if (CalisiyorMu)
            return;

        // İmleçteki statement'ı bulmak T-SQL ayrıştırıcısına dayanır; diğer motorlarda
        // kullanıcının yazmadığı "söz dizimi hatası" veriyordu (V3 denetimi 2026-07-18).
        // Orada Ctrl+Enter, seçili metni (yoksa tümünü) çalıştırmaya düşer — F5 ile aynı.
        if ((_profilGetir()?.Motor ?? MotorTuru.Mssql) != MotorTuru.Mssql)
        {
            await CalistirAsync();
            return;
        }

        (SqlAralik? aralik, string? hata) =
            SqlCozumleyici.ImlectekiStatement(Belge.Text, ImlecSaglayici?.Invoke() ?? 0);
        if (aralik is null)
        {
            Mesajlar = hata ?? "İmleçte çalıştırılacak bir statement yok.";
            SonucSekmeIndex = 1;
            return;
        }

        AralikSec?.Invoke(aralik.BaslangicOfseti, aralik.Uzunluk);
        await CalistirCoreAsync(aralik.Metin, aralik.BaslangicSatiri);
    }

    /// <summary>
    /// Tam biçimlendirme (FG-3.9, Ctrl+Shift+F) — tek undo adımı; hatada metne dokunmaz.
    /// Motora göre biçimlendirici seçilir (V3): MSSQL T-SQL (ScriptDom), MongoDB JSON.
    /// Diğer motorlarda düğme zaten gizlidir (biçimlendirici yok) — savunma amaçlı burada da korunur.
    /// </summary>
    [RelayCommand]
    public void Bicimlendir()
    {
        MotorTuru motor = _profilGetir()?.Motor ?? MotorTuru.Mssql;
        (string? sonuc, string? hata) = motor switch
        {
            MotorTuru.Mssql => SqlCozumleyici.Bicimlendir(Belge.Text),
            MotorTuru.Mongo => JsonBicimleyici.Bicimlendir(Belge.Text),
            _ => (null, $"{motor} için biçimlendirici yok."),
        };

        if (sonuc is null)
        {
            Mesajlar = $"Biçimlendirilemedi: {hata}";
            SonucSekmeIndex = 1;
            return;
        }

        Belge.Text = sonuc;
    }

    private async Task CalistirCoreAsync(string sql, int satirTabani)
    {
        if (CalisiyorMu)
            return; // F5 çalışırken kilitli (03 §3.2)
        if (IslemAcik)
        {
            Mesajlar = "🛡 Bu sekmede karar bekleyen açık bir işlem var — önce COMMIT ya da ROLLBACK.";
            SonucSekmeIndex = 1;
            return;
        }
        ConnectionProfile? profil = _profilGetir();
        if (profil is null)
            return;

        if (string.IsNullOrWhiteSpace(sql))
        {
            Mesajlar = "Çalıştırılacak sorgu yok.";
            return;
        }

        // FG-6.2 — HER MOTORDA çalışan sigorta: filtresiz UPDATE/DELETE onay ister.
        // (V3: eskiden yalnız T-SQL ayrıştırıcısıyla bakılıyordu, diğer motorlarda hiç
        //  ateşlenmiyordu; Mongo'da boş filtreli delete tüm koleksiyonu siliyordu.)
        IReadOnlyList<string> wheresizler = YazmaSigortasi.WheresizYazmalar(sql, profil.Motor);
        if (wheresizler.Count > 0 && OnayIste is { } sor
            && !sor("Bu sorguda FİLTRESİZ yazma var — TÜM kayıtlar etkilenir:\n\n"
                    + string.Join("\n", wheresizler) + "\n\nYine de çalıştırılsın mı?"))
        {
            Mesajlar = "Filtresiz yazma, kullanıcı onaylamadığı için çalıştırılmadı (FG-6.2).";
            SonucSekmeIndex = 1;
            return;
        }

        // Seçili DB dışına yazma kapısı (2026-07-17): sekmede X seçiliyken sorgu
        // üç parçalı adla/USE ile BAŞKA veritabanına yazıyorsa onaysız gönderilmez.
        // T-SQL'e özgü (üç parçalı ad + USE) — yalnız MSSQL'de anlamlı.
        IReadOnlyList<string> farkliDbler = profil.Motor == MotorTuru.Mssql
            ? SqlCozumleyici.FarkliVeritabaniYazmasi(sql, SecilenVeritabani)
            : [];
        if (farkliDbler.Count > 0 && OnayIste is { } dbSor
            && !dbSor($"Sekmede [{SecilenVeritabani}] seçili; sorgu ise ŞU veritaban(lar)ına yazıyor:\n\n"
                    + string.Join(", ", farkliDbler) + "\n\nBilerek mi? Devam edilsin mi?"))
        {
            Mesajlar = $"Seçili veritabanı dışına ({string.Join(", ", farkliDbler)}) yazma, kullanıcı onaylamadığı için çalıştırılmadı.";
            SonucSekmeIndex = 1;
            return;
        }

        var cts = new CancellationTokenSource(); // yerel referans: bayat koşu finally'de YALNIZ kendininkini bırakır
        _cts = cts;
        int nesil = ++_kosuNesli; // bu koşunun kimliği — Durdur nesli artırınca koşu bayatlar
        Durum = SekmeDurumu.Calisiyor;
        SonCalisanSql = sql;
        OnPropertyChanged(nameof(DisaAktarilabilir)); // SonCalisanSql düz özellik — ilk dolduğunda buton uyanmalı
        SonucSetleri = [];
        BilgiBandi = null;
        Mesajlar = "";
        SonucOzeti = "";
        YenidenDenenebilir = false;
        DateTime baslangicUtc = DateTime.UtcNow;
        GecmisKaydi? kayit = null;
        _kronometre.Restart();
        _sayac.Start();
        try
        {
            // FG-5.5: ALTER hedeflerinin eski tanımı çalıştırmadan önce tarihçeye
            if (AlterOncesiYedekle is { } yedekle)
                await yedekle(sql);

            ExecuteOptions secenekler = SecenekleriKur();
            // 🧱 AKIŞLI ALICI (v22-S3): satırlar okunur okunmaz DOĞRUDAN grid tablosuna yazılır —
            // ara liste hiç oluşmaz (708 → 457 bayt/satır). Tavan bu alıcıda uygulanır: dolduğunda
            // okuma temiz kesilir, çökme olmaz. (v22-S1'in "ilk 1.000 satır" bandı kaldırıldı —
            // kullanıcı kararı 2026-08-18: "bu düzenleme ile ihtiyacımız olmayacak".)
            var tabloAlicisi = new GridTabloAlicisi();
            secenekler.SonucAlici = tabloAlicisi;
            IDbOturum oturum = OturumAl(profil);
            // Batch bölme motora göre (V4-S5): MSSQL GO ile, Oracle ';' + tek başına '/' ile,
            // diğerleri tek parça (sürücüleri çok-ifadeli metni kabul eder; Mongo'da metin JSON'dur).
            IReadOnlyList<SqlBatch> batchler = SqlCozumleyici.BatchlereBol(sql, profil.Motor);

            // Güvenli Yazma (V2-S4): mod açıkken yazma sorgusu açık işlemde çalışır,
            // karar (COMMIT/ROLLBACK) kullanıcıya kalır; diğer her şey olağan yol.
            // Motor kapısı YÜRÜTME yolunda da var — UI gizlemesine tek başına güvenilmez.
            // V4-S2: SQL ailesinin dördünde de çalışır. TEK İSTİSNA, MySQL/Oracle'da DDL:
            // orada DDL örtük COMMIT yapar, yani banttaki ROLLBACK hiçbir şey yapmazdı →
            // bant açılmaz, kullanıcıya nedeni söylenir (sessiz yalan yerine açık uyarı).
            ILehce? lehce = profil.Motor == MotorTuru.Mongo ? null : _lehceler.Getir(profil.Motor);
            // PlanIcinYazmaSayilir (inceleme 2026-07-30, KRİTİK): eski YazmaSorgusuMu ilk-kelimeye
            // bakıyordu → "/* not */ UPDATE …" ve CTE'li DML kapıyı DELİYORDU (salt-okunur kapısı
            // için 2026-07-19'da kapatılan delik Güvenli Yazma'da açık kalmıştı).
            bool yazmaMi = QueryService.PlanIcinYazmaSayilir(sql, profil.Motor);
            bool ortukCommit = lehce is not null && yazmaMi && lehce.OrtukCommitYaparMi(sql);
            // EXEC (saklı yordam) Güvenli Yazma'ya ALINMAZ (kullanıcı bulgusu 2026-07-27): SP kendi
            // işlemini yönetir; dış BEGIN TRAN @@TRANCOUNT'u bozup değişikliği kalıcı olmaktan çıkarıyordu.
            bool execCagrisi = profil.Motor == MotorTuru.Mssql && QueryService.ExecMi(sql);
            // İşlem içinde YASAK ifadeler (ALTER DATABASE, BACKUP…) Güvenli Yazma'ya ALINMAZ
            // (canlı bulgu 2026-08-14: BEGIN TRAN + ALTER DATABASE sunucudan 226 ile dönüyordu).
            bool tranYasak = profil.Motor == MotorTuru.Mssql && QueryService.TranIcindeYasakMi(sql);
            bool guvenli = lehce is { GuvenliYazmaDestekler: true }
                && _guvenliYazmaGetir()
                && yazmaMi
                && !ortukCommit
                && !execCagrisi
                && !tranYasak;

            // ⏪ V15-S3 (BF-1): Güvenli Yazma'da DML çalışmadan ÖNCE etkilenecek satırların
            // eski hali yakalanır; paket ancak kullanıcı COMMIT derse depoya yazılır.
            _bekleyenGeriAl = null;
            _geriAlNotu = null;
            if (guvenli && profil.Motor == MotorTuru.Mssql && GeriAlDeposu is not null)
                _bekleyenGeriAl = await Task.Run(() => GeriAlYakalaAsync(profil, oturum, secenekler, sql));

            // v19-S13 (canlı test 2026-08-04 — "find yardımcısı Not Responding"): yürütme TÜMÜYLE
            // havuz iş parçacığında. Eskiden UI'dan await edildiğinden Infrastructure'daki her
            // await devamı (Mongo imleç turu, satır okuma…) UI dispatcher'ına marshal oluyordu —
            // büyük sonuç AKARKEN UI, açık modal pencereler dahil, devam seliyle boğuluyordu.
            CancellationToken ct = _cts.Token;
            // 🛩 UÇUŞ KAYDEDİCİ (v20-S16, "yine çöktü + yine log yok"): sorgunun her evresi ANINDA
            // diske düşer — süreç kaba abort/donma-öldürme ile giderse logdaki SON satır hangi
            // evrede öldüğünü söyler (teşhissiz çökme kalmasın). Serilog File sink tamponsuz yazar.
            Iz.Faz("sorgu-basladi");
            Log.Information("🛩 Sorgu başladı · db={Db} · {Uzunluk} kr · {Ozet}",
                SecilenVeritabani, sql.Length, sql.Length <= 120 ? sql : sql[..120]);
            QueryResult sonuc = await Task.Run(() => guvenli
                ? GuvenliYazmaYurutucu.CalistirAsync(
                    _queryService, lehce!, oturum, batchler, secenekler, ct, satirTabani)
                : BatchYurutucu.CalistirAsync(
                    _queryService, oturum, batchler, secenekler, ct, satirTabani));
            Iz.Faz("okuma-bitti", sonuc.ToplamSatir);
            Log.Information("🛩 Okuma bitti · {Satir} satır · ~{MB} MB · süre {Sure}",
                sonuc.ToplamSatir, sonuc.ToplamBayt / 1024 / 1024, sonuc.Sure);

            if (nesil != _kosuNesli)
            {
                // Durdur bu koşuyu ÇOKTAN sonlandırdı (UI boşaldı, oturum düşürüldü) — geciken
                // sonuç ekrana YAZMAZ, yalnız geçmişe iptal olarak düşer (finally invoke eder).
                kayit = GecmisKaydiKur(profil, sql, baslangicUtc, GecmisDurumu.IptalEdildi,
                    sonuc.ToplamSatir, sonuc.Hata?.Mesaj);
                return;
            }

            // m.23: ORDER BY'sız tek-tablolu SELECT'te görüntü PK sırasına dizilir (SQL'e dokunulmaz).
            sonuc = await VarsayilanSiralamayiUygulaAsync(profil, sql, sonuc, tabloAlicisi.Setler);

            _islemLehcesi = guvenli ? lehce : null;

            if (ortukCommit && _guvenliYazmaGetir() && lehce is not null)
            {
                sonuc = sonuc with
                {
                    Mesajlar = [.. sonuc.Mesajlar,
                        "🛡 Güvenli Yazma bu ifadede DEVRE DIŞI: bu motorda DDL (CREATE/ALTER/DROP/…) "
                      + "örtük COMMIT yapar — çalıştığı anda kalıcıdır ve geri alınamaz. "
                      + "Değişiklik doğrudan uygulandı."],
                };
            }
            else if (execCagrisi && _guvenliYazmaGetir())
            {
                sonuc = sonuc with
                {
                    Mesajlar = [.. sonuc.Mesajlar,
                        "🛡 Güvenli Yazma bu EXEC'te DEVRE DIŞI: saklı yordam kendi işlemini yönetir; "
                      + "dış BEGIN TRAN yordamın COMMIT/ROLLBACK'iyle çakışıp değişikliği kaybettirebilirdi. "
                      + "Yordam doğrudan çalıştırıldı."],
                };
            }
            else if (tranYasak && _guvenliYazmaGetir())
            {
                sonuc = sonuc with
                {
                    Mesajlar = [.. sonuc.Mesajlar,
                        "🛡 Güvenli Yazma bu ifadede DEVRE DIŞI: ALTER/CREATE/DROP DATABASE ve BACKUP/RESTORE "
                      + "gibi ifadeler SQL Server'da işlem (BEGIN TRAN) içinde ÇALIŞTIRILAMAZ. "
                      + "İfade doğrudan çalıştırıldı — geri alınamaz."],
                };
            }

            SureMetni = SonucBicimleyici.SureFormatla(sonuc.Sure > TimeSpan.Zero ? sonuc.Sure : _kronometre.Elapsed);
            Mesajlar = SonucBicimleyici.MesajlariBirlestir(sonuc);
            if (_geriAlNotu is not null) // ⏪ "paket alınamadı (sınır)" bilgisi sonucun altına düşer
                Mesajlar = Mesajlar.Length > 0 ? $"{Mesajlar}\n{_geriAlNotu}" : _geriAlNotu;

            // Auto-refresh farkı için ÖNCEKİ ilk setin tablosu, yenisi kurulmadan alınır
            // (SonucSetleri bütünüyle değiştirilir — sonra bakmak geç olurdu).
            System.Data.DataTable? oncekiTablo = OtoYenileAcik && SonucSetleri.Count > 0
                ? SonucSetleri[0].Tablo
                : null;

            // DataTable kurma (binlerce satır → binlerce Rows.Add) UI thread'ini DONDURUR:
            // arka planda kur, sonra UI'ya ata (WPF donma düzeltmesi 2026-07-20, kullanıcı
            // bulgusu). DataTable thread-affine DEĞİLDİR — arka planda kurulup UI thread'ine
            // devredilir; devirden sonra yalnız UI dokunur, çakışma yok.
            // 🧱 v22-S3: tablolar okuma sırasında ZATEN kuruldu (GridTabloAlicisi) — burada yalnız
            // görünüm sarmalayıcıları hazırlanır. Eskiden bu noktada DataTable'lar sıfırdan kuruluyor,
            // yani veri ikinci kez materyalize ediliyordu; tepe bellek orada ikiye katlanıyordu.
            IReadOnlyList<SonucSeti> hazirSetler = tabloAlicisi.Setler;

            // 🩹 v22-S4 saha turu-4 m.3 (kullanıcı: "Mongo select'imiz bozulmuş — mesajda 200 belge
            // yazıyor ama grid dolmuyor"). REGRESYON: akışlı alıcıyı YALNIZ SQL yolu (SonucOkuyucu)
            // besler; MongoExecutor sonucu ResultSetler'de döndürür. v22-S3'te görünüm
            // sarmalayıcıları alıcıdan kurulmaya başlayınca Mongo'da liste BOŞ kaldı — mesaj doğru
            // ("200 belge"), grid boş. Alıcı boşsa sonucun KENDİ kümelerinden kurulur; bu
            // motor-bağımsız bir emniyettir: alıcıyı beslemeyen her yürütücü yine çalışır.
            //
            // ⚠ DÜZELTME (çok ajanlı çökme denetimi, 20 Ağu): bu yedek yol İKİ korumayı birden
            // atlıyordu. (a) `durmaliMi` verilmediği için SonucBicimleyici'deki bellek tavanı
            // kontrolü tümüyle devre dışı kalıyordu — yani akışlı alıcının kazandığı koruma tam da
            // Mongo'da yoktu. (b) Tablo kurulumu UI thread'inde koşuyordu; binlerce satırda arayüzü
            // dondurur ve donma nöbetçisi de aynı thread'de olduğu için sessiz kalırdı.
            if (hazirSetler.Count == 0 && sonuc.ResultSetler.Count > 0)
            {
                hazirSetler = await Task.Run(
                    () => sonuc.ResultSetler
                        .Select(s => SonucBicimleyici.TabloyaCevir(s, BellekNobetcisi.OkumaKesilmeli))
                        .ToList(),
                    CancellationToken.None);
            }

            int setSayisi = hazirSetler.Count;
            List<SonucSetiGorunumu> setler =
            [
                .. hazirSetler.Select((set, i) => new SonucSetiGorunumu
                {
                    Set = set,
                    Baslik = $"Sonuç {i + 1}  ({set.SatirSayisi:N0} satır)",
                    BaslikGorunur = setSayisi > 1, // tek sette başlık gizli (03 §3.5)
                }),
            ];
            Iz.Faz("grid-hazir", setler.Count);
            Log.Information("🛩 Grid hazır · {Set} set", setler.Count); // uçuş kaydedici (v20-S16)

            if (nesil != _kosuNesli)
            {
                // Durdur, grid arka planda kurulurken geldi — bayat sonuç ekrana yazılmaz.
                kayit = GecmisKaydiKur(profil, sql, baslangicUtc, GecmisDurumu.IptalEdildi,
                    sonuc.ToplamSatir, sonuc.Hata?.Mesaj);
                return;
            }

            if (OtoYenileAcik && setler.Count > 0)
            {
                YenilemeFarki fark = OtoYenileFarki.Karsilastir(oncekiTablo, setler[0].Tablo);
                OtoYenileBandi = $"🔄 {DateTime.Now:HH:mm:ss} — {fark.Ozet}"
                               + $"  (her {Math.Max(2, OtoYenileSaniye)} sn)";
            }

            SonucSetleri = setler;

            // 🧭 v22-S4 (çökme denetimi): "grid-hazir"dan SONRASI tek işaretsiz bölgeydi — WPF kolon
            // üretimi + measure/arrange. Burada UI'ya devrediyoruz; asıl render henüz yapılmadı.
            // ContextIdle önceliğiyle bir iz daha bırakırız: o iz yoksa ölüm RENDER'da olmuştur.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                () => Iz.Faz("yerlesim-bitti", setler.Sum(x => x.Set.SatirSayisi)));

            BilgiBandi = setler.Any(x => x.Set.Kesildi)
                // v22-S3: tablo kurulumu bellek tavanında kesildi — sessiz eksik sonuç YOK.
                ? $"🧱 Bellek tavanına dayanıldı — {setler.Sum(x => x.Set.SatirSayisi):N0} satır gösteriliyor, "
                    + "gerisi yüklenmedi (uygulama çökmesin diye). Sorguyu daraltın ya da “⬇ Tümünü dışa aktar”."
                : sonuc switch
                {
                    { BellekSiniriAsildi: true } => $"İlk {sonuc.ToplamSatir:N0} satır gösteriliyor — bellek sınırına ulaşıldı (geniş satırlar). Tümü için “⬇ Tümünü dışa aktar”.",
                    { SatirSiniriAsildi: true } => $"İlk {sonuc.ToplamSatir:N0} satır gösteriliyor — satır sınırına ulaşıldı.",
                    _ => null,
                };
            SonucSekmeIndex = sonuc.Hata is not null || setler.Count == 0 ? 1 : 0;

            (Durum, SonucOzeti) = sonuc switch
            {
                { Basarili: true } => (SekmeDurumu.Tamamlandi, $"{sonuc.ToplamSatir} satır"),
                { IptalEdildi: true } => (SekmeDurumu.IptalEdildi, "iptal edildi"),
                _ => (SekmeDurumu.Hata, "hata"),
            };

            // Bağlantı kopması/timeout gibi geçici hatalarda yeniden denemeyi öner (kullanıcı kodu değişmedi).
            YenidenDenenebilir = sonuc.Hata is { Numara: -2 or 2 or 53 or 233 or 10053 or 10054 or 11001 };

            // 🤖 v11-S4: son hata metni asistanın "Hatayı çözdür" köprüsü için (başarıda temizlenir).
            SonHataMesaji = sonuc.Hata?.Mesaj;

            // Hata alt çizgisi (2026-07-23): hata satırı belgeye eşlenmiş gelir (BatchYurutucu satirTabani).
            // Mongo hariç — orada satır kavramı yok (kullanıcı: "mongoda gerek yok").
            HataSatiri = profil.Motor != MotorTuru.Mongo && sonuc.Hata is { Satir: >= 1 } hataBilgisi
                ? hataBilgisi.Satir
                : null;

            // Güvenli Yazma: başarılı DML sonrası işlem gerçekten açıksa karar bandını kur
            // (nesil bekçisi: Durdur oturumu düşürdüyse sorgulama anlamsız + oturum atılmış olabilir)
            if (nesil == _kosuNesli && guvenli && sonuc.Basarili
                && await oturum.IslemDurumuAsync(CancellationToken.None) == IslemDurumu.Acik)
                KararBandiniKur(sonuc.EtkilenenSatir);

            kayit = GecmisKaydiKur(profil, sql, baslangicUtc,
                Durum switch
                {
                    SekmeDurumu.Tamamlandi => GecmisDurumu.Basarili,
                    SekmeDurumu.IptalEdildi => GecmisDurumu.IptalEdildi,
                    _ => GecmisDurumu.Hata,
                },
                sonuc.ToplamSatir, sonuc.Hata?.Mesaj);

            // Akıllı adlandırma (V2-S2): başarıyla biten sorgu sekmeye adını verir;
            // hatalı/iptal edilen sorgu mevcut adı bozmaz.
            if (BaslikOtomatikMi && Durum == SekmeDurumu.Tamamlandi)
                Baslik = SekmeAdlandirici.AdTuret(sql) ?? Baslik;
        }
        catch (Exception ex)
        {
            // Beklenmeyen istisna sekmeyi "Çalışıyor"da kilitlememeli; durum makinesi her yoldan çıkar.
            Log.Error(ex, "🛩 Sorgu beklenmeyen hatayla bitti"); // uçuş kaydedici (v20-S16)
            if (nesil == _kosuNesli)
            {
                Durum = SekmeDurumu.Hata;
                SonucOzeti = "hata";
                Mesajlar = $"Beklenmeyen hata: {ex.Message}";
            }
            // Bayat koşuda istisna BEKLENEN yan etkidir (Durdur oturumu kapattı) — UI'a dokunulmaz.
            kayit = GecmisKaydiKur(profil, sql, baslangicUtc,
                nesil == _kosuNesli ? GecmisDurumu.Hata : GecmisDurumu.IptalEdildi, 0, ex.Message);
        }
        finally
        {
            if (nesil == _kosuNesli)
            {
                _sayac.Stop();
                _kronometre.Stop();
                _cts?.Dispose();
                _cts = null;
            }
            else
                cts.Dispose(); // bayat koşu YALNIZ kendi cts'ini bırakır — yeni koşununkine dokunmaz

            // Invoke finally'de (v20-S21): bayat koşunun erken return'ü de geçmişe düşsün.
            CalistirmaTamamlandi?.Invoke(this, kayit);
        }
    }

    private GecmisKaydi GecmisKaydiKur(
        ConnectionProfile profil, string sql, DateTime baslangicUtc,
        GecmisDurumu durum, int satirSayisi, string? hataMesaji) => new()
    {
        Sunucu = profil.Sunucu,
        Veritabani = SecilenVeritabani,
        Kullanici = DenetimKullanicisi(profil),
        SekmeAdi = Baslik, // hangi sekmeden çalıştı (kullanıcı isteği 2026-07-23)
        Sql = sql,
        BaslangicUtc = baslangicUtc,
        SureMs = (int)Math.Min(int.MaxValue, _kronometre.ElapsedMilliseconds),
        SatirSayisi = satirSayisi,
        Durum = durum,
        HataMesaji = hataMesaji,
    };

    /// <summary>Denetimin "kim"i (v10-S2): Windows kimliğinde OS kullanıcısı, SQL kimliğinde bağlantı
    /// kullanıcı adı. Bağlantının fiilen kullandığı kimliktir (ekstra round-trip yok).</summary>
    private static string DenetimKullanicisi(ConnectionProfile profil)
        => profil.Kimlik == KimlikTuru.Windows
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : profil.KullaniciAdi is { Length: > 0 } k ? k : "?";

    /// <summary>Esc / ■ Durdur — ANINDA keser (v20-S21 canlı bulgu 2026-08-14: "Durdur dediğimde
    /// direkt durması lazım"). UI iptali BEKLEMEZ: nesil artar, durum anında boşalır (m.2 deseni).
    /// Nazik iptal (Attention) yine havuzda gönderilir (m.15) AMA ek olarak sekmenin kalıcı
    /// oturumu DÜŞÜRÜLÜR: iptali dinlemeyen asılı komut bağlantı kapanınca sunucuda da ölür ve
    /// oturum kilidini (SemaphoreSlim) bırakmış olur — eskiden o kilit yüzünden sekmede SONRAKİ
    /// sorgular da sonsuza dek kuyrukta bekliyordu ("SELECT TOP 200'ü çalıştıramıyoruz").
    /// Bedel: Durdur'da #temp/SET sıfırlanır (sonraki çalıştırma yeni bağlantı açar) — asılı
    /// oturumu güvenle kurtarmanın başka yolu yok; mesaj bunu açıkça söyler.</summary>
    [RelayCommand]
    public void Iptal()
    {
        if (_cts is null)
            return;

        _kosuNesli++;                          // geciken koşunun devamları artık UI'ya yazamaz
        IptalYardimcisi.ArkaPlandaIptal(_cts); // m.15: Cancel UI'da bloklayabilir — havuzda
        _cts = null;

        IDbOturum? eski = _oturum;             // oturum ANINDA ayrılır — yeni koşu taze bağlantı açar
        _oturum = null;
        if (eski is not null)
            _ = Task.Run(async () =>
            {
                try { await eski.DisposeAsync(); }
                catch { } // asılı komutla yarışta en iyi çaba — bağlantı finally'sinde yine kapanır
            });

        _sayac.Stop();
        _kronometre.Stop();
        Durum = SekmeDurumu.IptalEdildi;
        SonucOzeti = "iptal edildi";
        Mesajlar = "Durduruldu — sorgu sunucuda da kesildi (sekmenin bağlantısı kapatıldı; "
                 + "bir sonraki çalıştırma yeni bağlantı açar, #temp tablolar sıfırlanır).";
        SonucSekmeIndex = 1;
    }

    // --- 🔍 Sorgu provası (V15-S2, BF-2 — kullanıcı: "her update'ten önce select yapıp bakıyoruz") ---

    /// <summary>Prova bandı metni; null → band gizli. Belge değişince söner (bayat prova yanıltmasın).</summary>
    [ObservableProperty] private string? _provaBandi;

    private string? _provaMetni;       // "Şimdi çalıştır" TAM prova edilen metni koşar (seçim dahil)
    private int _provaSatirTabani = 1;

    /// <summary>
    /// DML'i ÇALIŞTIRMADAN etkisini gösterir: <see cref="ProvaCevirici"/> UPDATE/DELETE'i
    /// salt-SELECT çiftine çevirir (sayım + ilk 10), tek gidişte koşulur; band karar sorar.
    /// Sekmenin KENDİ oturumunda koşar — #temp tablolar ve NOLOCK tercihi provada da geçerli.
    /// </summary>
    [RelayCommand]
    internal async Task ProvaAsync()
    {
        if (CalisiyorMu)
            return;
        ConnectionProfile? profil = _profilGetir();
        if (profil is null)
            return;
        if (profil.Motor != MotorTuru.Mssql)
        {
            Mesajlar = "🔍 Prova şimdilik yalnız SQL Server'da çalışır.";
            SonucSekmeIndex = 1;
            return;
        }

        string metin = MetinSaglayici?.Invoke() ?? Belge.Text;
        (ProvaPlani? plan, string? hata) = ProvaCevirici.Cevir(metin);
        if (plan is null)
        {
            ProvaBandi = null;
            Mesajlar = "🔍 Prova: " + hata;
            SonucSekmeIndex = 1;
            return;
        }

        try
        {
            QueryResult sonuc = await OturumAl(profil).CalistirAsync(
                plan.SayimSql + "\n" + plan.OrnekSql, SecenekleriKur(), CancellationToken.None);
            if (sonuc.Hata is not null || sonuc.ResultSetler.Count < 2)
            {
                Mesajlar = "🔍 Prova koşulamadı: " + (sonuc.Hata?.Mesaj ?? "beklenen sonuç gelmedi.");
                SonucSekmeIndex = 1;
                return;
            }

            long sayi = Convert.ToInt64(sonuc.ResultSetler[0].Satirlar[0][0]);
            SonucSeti ornek = SonucBicimleyici.TabloyaCevir(sonuc.ResultSetler[1]);
            SonucSetleri = [new SonucSetiGorunumu
            {
                Set = ornek,
                Baslik = $"🔍 PROVA — etkilenecek ilk {ornek.SatirSayisi:N0} satır (hiçbir şey değişmedi)",
                BaslikGorunur = true,
            }];
            SonucSekmeIndex = 0;
            Mesajlar = "";

            _provaMetni = metin;
            _provaSatirTabani = SecimBasiSatiri?.Invoke() ?? 1;
            string setNotu = plan.SetKolonlari.Count > 0
                ? $" — SET edilecek kolonlar: {string.Join(", ", plan.SetKolonlari)}" : "";
            ProvaBandi = (plan.WhereYok
                    ? $"🔍 ⚠ WHERE YOK — {plan.Fiil} {plan.Hedef} TÜM TABLOYU etkileyecek: {sayi:N0} satır!"
                    : $"🔍 {plan.Fiil} {plan.Hedef}: {sayi:N0} satır etkilenecek.")
                + setNotu + (plan.TopNotu is null ? "" : " " + plan.TopNotu);
        }
        catch (Exception ex)
        {
            Mesajlar = "🔍 Prova koşulamadı: " + ex.Message;
            SonucSekmeIndex = 1;
        }
    }

    /// <summary>Bandın "▶ Şimdi çalıştır"ı: TAM prova edilen metni olağan yoldan (tüm sigortalarla) koşar.</summary>
    [RelayCommand]
    private async Task ProvaOnayla()
    {
        string? metin = _provaMetni;
        ProvaBandi = null;
        if (metin is not null)
            await CalistirCoreAsync(metin, _provaSatirTabani);
    }

    [RelayCommand]
    private void ProvaKapat() => ProvaBandi = null;

    // --- ⏪ Geri Al paketi (V15-S3, BF-1 "uçuş kaydedici") ---

    /// <summary>Paket deposu köprüsü (MainViewModel bağlar); null → özellik pasif (testler etkilenmez).</summary>
    public IGeriAlDeposu? GeriAlDeposu { get; set; }

    /// <summary>m.23: şema önbelleği köprüsü (MainViewModel bağlar) — ORDER BY'sız tek-tablolu
    /// SELECT'te görüntüyü PK'ya dizmek için; null → özellik pasif (testler etkilenmez).</summary>
    public Func<string?, Task<SemaOnbellegi?>>? OnbellekGetir { get; set; }

    /// <summary>
    /// m.23 ("ORDER BY yokken sıralama karışık"): SQL'e DOKUNMADAN, bellekteki sonucu görüntüde
    /// PK'ya diz — yalnız MSSQL + tek result set + tek-tablolu SELECT + ORDER BY yok + PK kolonları
    /// sonuçta VARSA. Kısıtlanmış/başarısız her durumda sonuç AYNEN döner (sessiz, zararsız).
    /// </summary>
    private async Task<QueryResult> VarsayilanSiralamayiUygulaAsync(
        ConnectionProfile profil, string sql, QueryResult sonuc, IReadOnlyList<SonucSeti> setler)
    {
        // v22-S3: satırlar artık ham listede DEĞİL, doğrudan grid tablosunda. Sıralama da bu yüzden
        // listeyi KOPYALAYIP sıralamak yerine DataView.Sort ile yapılır — ikinci bir kopya oluşmaz
        // (eski yol 100k satırı yeniden diziyordu) ve sıralama tipe uygun/tembel olur.
        if (profil.Motor != MotorTuru.Mssql || OnbellekGetir is null
            || !sonuc.Basarili || setler.Count != 1 || setler[0].SatirSayisi < 2
            || VarsayilanSiralama.OrderByIceriyorMu(sql)
            || KayitHaritasi.TabloCikar(sql) is not { } tabloAd)
            return sonuc;

        SemaOnbellegi? onbellek = await OnbellekGetir(SecilenVeritabani);
        SemaNesnesi? nesne = onbellek?.Nesneler.FirstOrDefault(n =>
            n.Tur == SemaNesneTuru.Tablo
            && (string.Equals(n.TamAd, tabloAd, StringComparison.OrdinalIgnoreCase)
                || string.Equals(n.Ad, tabloAd, StringComparison.OrdinalIgnoreCase)));
        List<string>? pkAdlari = nesne?.Kolonlar.Where(k => k.PkMi).Select(k => k.Ad).ToList();
        if (pkAdlari is not { Count: > 0 })
            return sonuc;

        System.Data.DataTable tablo = setler[0].Tablo;
        if (pkAdlari.Any(a => !tablo.Columns.Contains(a)))
            return sonuc; // PK kolonları sonuçta yoksa (projeksiyon) sıralama uygulanmaz

        tablo.DefaultView.Sort = string.Join(", ", pkAdlari.Select(a => $"[{a.Replace("]", "]]")}] ASC"));
        return sonuc with
        {
            Mesajlar = [.. sonuc.Mesajlar,
                $"ℹ ORDER BY yok — satırlar görüntüde {string.Join(", ", pkAdlari)} sırasına dizildi "
              + "(SQL sırayı garanti etmez; kendi sıralaman için ORDER BY yaz)."],
        };
    }

    private GeriAlPaketi? _bekleyenGeriAl; // DML çalışmadan önce yakalanan eski hal — COMMIT'te depoya

    /// <summary>🔍 Değişiklikleri görüntüle (2026-07-31): son yakalamanın SQL'i + eski satırları.
    /// Karar bandındayken işlem İÇİNDEN aynı SELECT koşulup "Önceki/Şimdiki hal" iki grid basılır.
    /// Yalnız Geri Al yakalamasının çalıştığı durumda dolar (tek ifadeli FROM'suz UPDATE/DELETE).</summary>
    private (string YakalamaSql, ResultSetData EskiHal)? _sonYakalama;

    /// <summary>Karar bandındaki "🔍 Değişiklikleri görüntüle" butonunun görünürlüğü.</summary>
    [ObservableProperty] private bool _degisiklikGosterilebilir;

    [RelayCommand]
    private async Task DegisiklikleriGosterAsync()
    {
        if (_sonYakalama is not { } y || _oturum is null || !IslemAcik)
            return;

        // Şimdiki hal AÇIK İŞLEMİN İÇİNDEN okunur (aynı oturum) — commit edilmemiş değişiklik görünür.
        var opts = new ExecuteOptions { VeritabaniOverride = SecilenVeritabani, KomutTimeoutSnOverride = 30 };
        QueryResult simdiki = await _oturum.CalistirAsync(y.YakalamaSql, opts, CancellationToken.None);

        var setler = new List<SonucSetiGorunumu>();
        SonucSeti eski = await Task.Run(() => SonucBicimleyici.TabloyaCevir(y.EskiHal));
        setler.Add(new SonucSetiGorunumu
        {
            Set = eski, Baslik = $"⏮ Önceki hal ({eski.SatirSayisi:N0} satır)", BaslikGorunur = true,
        });
        if (simdiki.Hata is null && simdiki.ResultSetler.Count > 0)
        {
            SonucSeti yeni = await Task.Run(() => SonucBicimleyici.TabloyaCevir(simdiki.ResultSetler[0]));
            setler.Add(new SonucSetiGorunumu
            {
                Set = yeni,
                Baslik = $"⏭ Şimdiki hal ({yeni.SatirSayisi:N0} satır — işlem içinden; DELETE'te boş kalır)",
                BaslikGorunur = true,
            });
        }
        SonucSetleri = setler;
        SonucSekmeIndex = 0;
    }
    private string? _geriAlNotu;           // "paket alınamadı (sınır)" — sonuç mesajlarına eklenir

    /// <summary>
    /// Etkilenecek satırların ESKİ halini DML'den önce aynı oturumda çeker. BF-1 sınırları:
    /// yalnız tek ifadeli, FROM'suz UPDATE/DELETE; satır sınırı aşılırsa paket alınmaz (not düşer).
    /// Her hata sessizdir — yakalama bir konfor, sorgunun önünde ASLA engel değildir.
    /// </summary>
    private async Task<GeriAlPaketi?> GeriAlYakalaAsync(
        ConnectionProfile profil, IDbOturum oturum, ExecuteOptions secenekler, string sql)
    {
        try
        {
            GeriAlAdayi? aday = GeriAlYakalayici.Uret(sql);
            if (aday is null)
                return null;

            // Yakalama, kilitli tabloda süresiz asılmasın (inceleme 2026-07-30): 30 sn tavan —
            // aşarsa aşağıdaki hata yolu paketsiz devama düşer ("asla engel değil" sözü korunur).
            var yakalamaOpts = new ExecuteOptions
            {
                VeritabaniOverride = secenekler.VeritabaniOverride,
                SatirSiniri = secenekler.SatirSiniri,
                BellekSiniriBayt = secenekler.BellekSiniriBayt,
                KomutTimeoutSnOverride = 30,
            };
            QueryResult eskiHal = await oturum.CalistirAsync(aday.YakalamaSql, yakalamaOpts, CancellationToken.None);
            if (eskiHal.Hata is not null || eskiHal.ResultSetler.Count == 0
                || eskiHal.ResultSetler[0].Satirlar.Count == 0)
                return null; // hata ya da etkilenecek satır yok — paket gereksiz

            // KRİTİK (inceleme 2026-07-30): okuma satır/bellek sınırına takıldıysa paket EKSİKTİR —
            // eksik paketle "Geri Al" kısmi (sessiz) geri yükleme yapardı. Paket alma, not düş.
            if (eskiHal.SatirSiniriAsildi || eskiHal.BellekSiniriAsildi)
            {
                _geriAlNotu = "⏪ Geri Al paketi ALINMADI — etkilenecek veri yakalama sınırını aşıyor (kısmi paket güvenilmez).";
                return null;
            }

            ResultSetData set = eskiHal.ResultSetler[0];
            // 🔍 Değişiklikleri görüntüle (kullanıcı isteği 2026-07-31): karar bandındayken
            // önceki↔şimdiki hal gösterimi için yakalama SQL'i + eski satırlar saklanır.
            _sonYakalama = (aday.YakalamaSql, set);
            if (set.Satirlar.Count > GeriAlYakalayici.SatirSiniri)
            {
                _geriAlNotu = $"⏪ Geri Al paketi ALINMADI — satır sınırı ({GeriAlYakalayici.SatirSiniri:N0}) aşıldı.";
                return null;
            }

            QueryResult pk = await oturum.CalistirAsync(aday.PkSql, yakalamaOpts, CancellationToken.None);
            List<string> pkKolonlari = pk.Hata is null && pk.ResultSetler.Count > 0
                ? [.. pk.ResultSetler[0].Satirlar.Select(s => s[0]?.ToString() ?? "")]
                : [];

            return GeriAlSerilestirici.PaketKur(
                profil.Sunucu, SecilenVeritabani ?? "", aday, sql, set, pkKolonlari, DateTime.UtcNow);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Toplu dışa aktarma sürüyor mu (düğme kapısı + durum göstergesi).</summary>
    [ObservableProperty] private bool _disaAktariliyor;

    /// <summary>Bir sorgu çalıştırıldı mı — "Tümünü dışa aktar" ancak o zaman anlamlı.</summary>
    public bool DisaAktarilabilir => !string.IsNullOrWhiteSpace(SonCalisanSql) && !CalisiyorMu && !DisaAktariliyor;

    /// <summary>
    /// Sekmenin son çalıştırılan sorgusunu TÜM satırlarıyla CSV'ye AKITIR (v6 "Tümünü dışa
    /// aktar"). Grid 100k'da kesilir; bu, sınıra takılmadan SABİT BELLEKLE dosyaya yazar. Aynı
    /// oturumda koşar → izolasyon/#temp grid ile tutarlı. Dönen değer yazılan satır sayısıdır.
    /// </summary>
    public async Task<long> TumunuCsvyeAktarAsync(string dosyaYolu, IProgress<long>? ilerleme, CancellationToken ct)
    {
        ConnectionProfile? profil = _profilGetir();
        if (profil is null || string.IsNullOrWhiteSpace(SonCalisanSql))
            return 0;

        DisaAktariliyor = true;
        OnPropertyChanged(nameof(DisaAktarilabilir));
        try
        {
            IDbOturum oturum = OturumAl(profil);
            // SecenekleriKur DB override'ı taşır; AkisAsync satır sınırını YOK SAYAR (tümü gelir).
            return await CsvYazici.AkislaDosyayaYazAsync(
                oturum, SonCalisanSql!, SecenekleriKur(), dosyaYolu, ilerleme: ilerleme, ct: ct);
        }
        finally
        {
            DisaAktariliyor = false;
            OnPropertyChanged(nameof(DisaAktarilabilir));
        }
    }

    /// <summary>
    /// Execution plan'ı alır (V5-S1). Plan, sekmenin KENDİ kalıcı oturumunda toplanır —
    /// böylece <c>#temp</c> tabloları ve SET seçenekleri gibi oturum durumu görünür
    /// (ayrı bağlantı açılsaydı bu sorgular plan alınırken hata verirdi).
    ///
    /// <paramref name="gercek"/> false: <c>SHOWPLAN_XML</c> — sorgu <b>ÇALIŞTIRILMAZ</b>,
    /// yalnız derlenir; yazma sorgusunda bile veri değişmez.
    /// true: <c>STATISTICS XML</c> — sorgu çalışır, gerçek satır sayıları gelir.
    ///
    /// Plan toplama MUTLAKA kapatılır (finally): açık kalırsa sonraki sorgular sonuç yerine
    /// plan döndürür ve kullanıcı ne olduğunu anlamaz.
    /// </summary>
    public async Task<(SorguPlani? Plan, string? Hata)> PlanAlAsync(bool gercek, CancellationToken ct = default)
    {
        ConnectionProfile? profil = _profilGetir();
        if (profil is null)
            return (null, "Bağlantı yok.");

        // Plan alma da sekmenin DURUM MAKİNESİNE tabidir (A1/B1 bulgusu, 2026-07-19).
        // Önce değildi ve iki gerçek sorun çıkıyordu:
        //  (1) Plan toplanırken F5'e basılınca kullanıcının sorgusu, "SET STATISTICS XML ON"
        //      ile "OFF" arasına giriyordu → sonuç grid'ine satır yerine plan XML'i doluyordu.
        //  (2) Güvenli Yazma'da karar bekleyen AÇIK İŞLEM varken F5 engelliyken plan düğmesi
        //      engellenmiyordu → gerçek plan, o açık işlemin içinde sorguyu çalıştırıyordu.
        if (CalisiyorMu)
            return (null, "Bu sekmede bir sorgu çalışıyor — önce bitmesini bekleyin.");
        if (IslemAcik)
            return (null, "Açık bir işlem var; önce COMMIT/ROLLBACK kararını verin.");

        // MongoDB ayrı ailedir (ILehce uygulamaz) → plan yolu da ayrı yürür
        if (profil.Motor == MotorTuru.Mongo)
            return await MongoPlanAlAsync(profil, gercek, ct);

        if (!_lehceler.Getir(profil.Motor).PlanDestekler)
            return (null, "Execution plan bu motorda henüz desteklenmiyor.");

        // Motor ölçümlü plan veremiyorsa (MySQL/Oracle) HATA VERİLMEZ — tahminî planı biz
        // arkadan alırız (kullanıcı kararı 2026-07-20: "tek bir execution plan var; gerçek
        // plan vermeyen motorlarda tahminiyi biz arkadan alıp getireceğiz").
        // Eski davranış "…— tahmini planı kullanın" diyerek kullanıcıyı, tek düğmeye
        // geçildiğinden beri VAR OLMAYAN bir seçeneğe yönlendiriyordu.
        if (gercek && !_lehceler.Getir(profil.Motor).PlanGercekDestekler)
            gercek = false;

        // Seçili metin varsa yalnız onun planı (F5 ile aynı kural — FG-3.3)
        string sql = MetinSaglayici?.Invoke() ?? "";
        if (string.IsNullOrWhiteSpace(sql))
            return (null, "Plan alınacak sorgu yok.");

        ILehce lehce = _lehceler.Getir(profil.Motor);

        // ── GÜVENLİK KAPISI: denetim HAM sql üzerinde, SARMADAN ÖNCE ──────────
        // PostgreSQL'de plan isteği sorguyu "EXPLAIN (ANALYZE) …" ile sarar; sarılmış
        // metnin ilk anahtar sözcüğü EXPLAIN olduğundan salt-okunur kapısı onu YAZMA
        // SAYMAZ ve geçirir — oysa ANALYZE sorguyu gerçekten çalıştırır.
        // Canlı kanıt (PG 16.4): EXPLAIN (ANALYZE) DELETE … → 1000 satır 900'e düştü.
        // Bu yüzden kapı burada, ham metinle bir kez daha uygulanır.
        //
        // Denetim TEMKİNLİdir (ilk-kelime mantığı burada YETMEZ): PostgreSQL'de veri
        // değiştiren CTE — WITH x AS (DELETE … RETURNING …) SELECT … — ilk sözcüğü WITH
        // olduğu için "okuma" sayılır ve EXPLAIN ANALYZE ile tüm tabloyu sildirirdi.
        bool yazmaMi = QueryService.PlanIcinYazmaSayilir(sql, profil.Motor);

        if (profil.SaltOkunur && yazmaMi)
            return (null, "Bu profil salt-okunur; yazma/DDL sorgusunun planı alınmadı (FG-1.5).");

        // ── YAZMA/DDL → ÖLÇÜMLÜ PLAN İSTENMEZ, sessizce tahmine düşülür ────────
        // (kullanıcı kararı 2026-07-20: "sadece SELECT sorgularında execution planı
        //  çalıştırmamız gerekmiyor mu?")
        //
        // ÖNCEDEN: kullanıcıya "gerçek plan bunu ÇALIŞTIRIR, yine de çalıştırılsın mı?"
        // diye sorulup EVET denirse DROP/CREATE/DELETE gerçekten çalıştırılıyordu. İki
        // ayrı sorun vardı:
        //  (1) Plan düğmesine basan kimse veri değiştirmeyi kastetmez. "Onayladı" demek
        //      savunma değil: kullanıcıya sormamız gereken bir soru değildi, çünkü doğru
        //      cevabı araç zaten biliyor — yazma ifadesinin planı ÇALIŞTIRMADAN alınabilir.
        //  (2) Uyarı, kullanıcıyı "'Tahmini plan' kullanın" diye YOK OLAN bir düğmeye
        //      yönlendiriyordu: 2026-07-19'da motor başına TEK plan düğmesine geçilmişti,
        //      uyarı metni o değişiklikten sağ çıkmıştı. Var olmayan bir çıkış yolu
        //      gösteren uyarı, kullanıcıyı tek gerçek seçenek olan "Evet"e itiyordu.
        //
        // ŞİMDİ: yazma/DDL ise ölçümlü plan hiç denenmez. SHOWPLAN_XML (MSSQL) / ANALYZE'sız
        // EXPLAIN (PG) ifadeyi yalnız DERLER — veri değişmez, kullanıcı yine de tam planı
        // görür. Sorulacak bir şey kalmadığı için filtresiz-DML sigortası da gereksizleşti:
        // hiçbir şey çalışmıyor.
        bool yazmaIcinTahmineDusuldu = gercek && yazmaMi;
        if (yazmaIcinTahmineDusuldu)
            gercek = false;

        if (lehce.PlanTekIfadeIster && CokIfadeliMi(sql))
            return (null, "Bu motorda plan tek bir ifade için alınır — tek bir sorgu seçip yeniden deneyin.");

        IDbOturum oturum = OturumAl(profil);
        ExecuteOptions opts = SecenekleriKur();

        // Plan da İPTAL EDİLEBİLİR olmalı (A1/B1 bulgusu, 2026-07-19). Önce PlanAlAsync
        // hiç CTS kurmuyordu; Esc / ■ Durdur `_cts`'i iptal ediyor ama plan onu hiç
        // kullanmadığından 20 dakikalık bir raporun gerçek planı alınırken kullanıcının
        // tek çıkışı uygulamayı öldürmekti — o da oturumda STATISTICS XML'i AÇIK bırakırdı.
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken iptal = _cts.Token;
        Durum = SekmeDurumu.Calisiyor;

        try
        {
            // Boş dize = oturum düzeyinde açma gerekmez (PostgreSQL sorguyu sarar)
            string acSql = lehce.PlanAcSql(gercek);
            if (acSql.Length > 0)
            {
                QueryResult ac = await _queryService.RunAsync(oturum, acSql, opts, iptal);
                if (ac.Hata is { } acHatasi)
                    return (null, $"Plan toplama açılamadı: {acHatasi.Mesaj}");
            }

            return await PlanGovdesiAsync(
                lehce, oturum, opts, sql, gercek, yazmaIcinTahmineDusuldu, iptal);
        }
        finally
        {
            Durum = SekmeDurumu.Tamamlandi;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Plan toplama gövdesi — açma yapıldıktan sonrası; kapatma her hâlde çalışır.</summary>
    private async Task<(SorguPlani? Plan, string? Hata)> PlanGovdesiAsync(
        ILehce lehce, IDbOturum oturum, ExecuteOptions opts, string sql, bool gercek,
        bool yazmaIcinTahmineDusuldu, CancellationToken ct)
    {
        try
        {
            QueryResult sonuc = await _queryService.RunAsync(
                oturum, lehce.PlanSorgusuYaz(sql, gercek), opts, ct);
            if (sonuc.IptalEdildi)
                return (null, "İptal edildi.");
            if (sonuc.Hata is { } hata)
                return (null, $"Sorgu çalıştırılamadı: {hata.Mesaj}");

            // İki adımlı plan (Oracle): ilk adım PLAN_TABLE'a yazar, plan ikinci sorguyla okunur
            string okumaSql = lehce.PlanOkumaSql(gercek);
            if (okumaSql.Length > 0)
            {
                sonuc = await _queryService.RunAsync(oturum, okumaSql, opts, ct);
                if (sonuc.IptalEdildi)
                    return (null, "İptal edildi.");
                if (sonuc.Hata is { } okumaHatasi)
                    return (null, $"Plan okunamadı: {okumaHatasi.Mesaj}");
            }

            // Sebep plana iliştirilir: sekme "bu motor ölçümlü plan vermiyor" mu yoksa
            // "yazma ifadesi olduğu için çalıştırmadık" mı diyeceğini buradan bilir.
            SorguPlani plan = lehce.PlanCoz(sonuc, gercek);
            return (plan with { YazmaOlduguIcinCalistirilmadi = yazmaIcinTahmineDusuldu }, null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);          // plan çözümlenemedi — açık mesaj
        }
        finally
        {
            string kapatSql = lehce.PlanKapatSql(gercek);
            if (kapatSql.Length > 0)
            {
                // İptal edilemez: açık kalırsa sonraki sorgular sonuç yerine plan döndürür
                await _queryService.RunAsync(oturum, kapatSql, opts, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// MongoDB plan yolu (V5-S1e). Ayrı ailedir: sorgu <c>explain</c> ile sarılır ve tek
    /// adımda çalışır.
    ///
    /// <b>Yazma onayı burada SORULMAZ</b> — ve bu bilinçlidir: PostgreSQL'de
    /// <c>EXPLAIN ANALYZE DELETE</c> satırları gerçekten silerken, MongoDB'de <c>explain</c>
    /// yazmayı <b>UYGULAMAZ</b> (alan adı bile <c>nWouldDelete</c>). Canlı doğrulandı:
    /// explain sonrası koleksiyon aynı belge sayısıyla kaldı. Olmayan bir riski uyarmak
    /// kullanıcıyı yanıltır ve gerçek uyarılara olan güveni aşındırırdı.
    /// </summary>
    private async Task<(SorguPlani? Plan, string? Hata)> MongoPlanAlAsync(
        ConnectionProfile profil, bool gercek, CancellationToken ct)
    {
        string sorgu = MetinSaglayici?.Invoke() ?? "";
        if (string.IsNullOrWhiteSpace(sorgu))
            return (null, "Plan alınacak sorgu yok.");

        IDbOturum oturum = OturumAl(profil);
        ExecuteOptions opts = SecenekleriKur();

        try
        {
            QueryResult sonuc = await _queryService.RunAsync(
                oturum, MongoPlanOkuyucu.SorguYaz(sorgu, gercek), opts, ct);

            if (sonuc.IptalEdildi)
                return (null, "İptal edildi.");
            if (sonuc.Hata is { } hata)
                return (null, $"Plan alınamadı: {hata.Mesaj}");

            return (MongoPlanOkuyucu.Coz(sonuc, gercek), null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Metinde birden çok ifade var mı — yorum ve metin sabitleri ayıklandıktan sonra
    /// kalan ';' sayılır. <b>Motora özgü bir bölücü KULLANILMAZ:</b> burada Oracle'ın
    /// <c>/</c> kuralını PostgreSQL metnine uygulamak çoklu motor kuralının ihlali olurdu
    /// (ve PG'nin <c>$$</c> alıntılamasında yanlış sonuç verirdi). Amaç kesin ayrıştırma
    /// değil, kullanıcıya anlamsız bir sunucu hatası yerine açık mesaj verebilmek.
    /// </summary>
    private static bool CokIfadeliMi(string sql)
    {
        string temiz = YazmaSigortasi.YorumVeMetinleriAyikla(sql);
        int i = temiz.IndexOf(';');
        // Son ';' sondaysa tek ifadedir: kalanın tamamı boşluksa çok ifadeli sayılmaz
        return i >= 0 && !string.IsNullOrWhiteSpace(temiz[(i + 1)..]);
    }

    private ExecuteOptions SecenekleriKur() => new()
    {
        VeritabaniOverride = SecilenVeritabani,
        KirliOkuma = _kirliOkumaGetir(),
    };


    // --- Güvenli Yazma karar akışı (V2-S4) ---

    /// <summary>Kaçırılamaz bandı ve otomatik ROLLBACK geri sayımını başlatır (07-r2 §2: istemci tarafı zaman aşımı).</summary>
    private void KararBandiniKur(int? etkilenenSatir)
    {
        _islemOzeti = etkilenenSatir is { } n
            ? $"🛡 İşlem açık — {n:N0} satır etkilendi. Kalıcı olsun mu?"
            : "🛡 İşlem açık — kalıcı olsun mu?";
        _rollbackZamani = DateTime.UtcNow.AddSeconds(Math.Max(10, _rollbackSnGetir()));
        IslemAcik = true;
        DegisiklikGosterilebilir = _sonYakalama is not null; // 🔍 buton yalnız yakalama varken (2026-07-31)
        BandiGuncelle();

        _rollbackSayaci ??= SayacKur();
        _rollbackSayaci.Start();
    }

    private DispatcherTimer SayacKur()
    {
        var sayac = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        sayac.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow >= _rollbackZamani)
                await KararUygulaAsync(commit: false, otomatik: true); // süre doldu — güvenli taraf
            else
                BandiGuncelle();
        };
        return sayac;
    }

    private void BandiGuncelle()
    {
        TimeSpan kalan = _rollbackZamani - DateTime.UtcNow;
        if (kalan < TimeSpan.Zero)
            kalan = TimeSpan.Zero;
        IslemBandi = $"{_islemOzeti}  ·  otomatik ROLLBACK: {(int)kalan.TotalMinutes}:{kalan.Seconds:00}";
    }

    [RelayCommand]
    public Task CommitAsync() => KararUygulaAsync(commit: true, otomatik: false);

    [RelayCommand]
    public Task RollbackAsync() => KararUygulaAsync(commit: false, otomatik: false);

    private async Task KararUygulaAsync(bool commit, bool otomatik)
    {
        if (!IslemAcik || _oturum is null)
            return;
        _rollbackSayaci?.Stop();
        IslemAcik = false; // çift tık/yeniden giriş kilidi

        GeriAlPaketi? paket = _bekleyenGeriAl; // ⏪ karar ne olursa olsun bekleyen paket düşer
        _bekleyenGeriAl = null;

        string mesaj;
        try
        {
            // İşlemi hangi lehçe açtıysa kararı da o uygular (V4-S2)
            mesaj = await GuvenliYazmaYurutucu.KararUygulaAsync(
                _queryService, _islemLehcesi ?? _lehceler.Getir(MotorTuru.Mssql),
                _oturum, commit, SecenekleriKur(), CancellationToken.None);

            // ⏪ V15-S3: yalnız COMMIT kalıcılaştırırsa paket saklanır (ROLLBACK'te veri zaten geri döndü).
            if (commit && paket is not null && GeriAlDeposu is { } geriAlDeposu)
            {
                await geriAlDeposu.EkleAsync(paket);
                mesaj += $"  ·  ⏪ Geri Al paketi kaydedildi ({paket.SatirSayisi:N0} satır — {paket.Tablo}).";
            }
        }
        catch (Exception ex)
        {
            mesaj = $"Karar uygulanamadı: {ex.Message}";
        }
        if (otomatik)
            mesaj = $"⏱ Karar süresi doldu — {mesaj}";

        IslemBandi = "";
        Mesajlar = Mesajlar.Length > 0 ? $"{Mesajlar}\n{mesaj}" : mesaj;
        SonucSekmeIndex = 1; // karar sonucu görünür olsun
    }
}
