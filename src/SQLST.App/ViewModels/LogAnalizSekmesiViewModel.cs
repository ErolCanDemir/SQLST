using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;
using LogGrupGorunum = SQLST.App.Views.LogGrupGorunum;

namespace SQLST.App.ViewModels;

/// <summary>
/// Log Analizi SEKMESİ (kullanıcı isteği 2026-08-09: ayrı pencere yerine sekme). Mantık pencere
/// sürümündekiyle (<see cref="SQLST.App.Views.LogAnalizPenceresi"/>) aynı SAF çekirdeği
/// (<see cref="LogTabloAnaliz"/>) sürer: veritabanı → tablo → mesaj kolonu (+ istenirse EK kolonlar)
/// seçilir; son 24 saat / tarih aralığı / tüm zamanlar kapsamında en çok tekrarlayan hata imzaları
/// GERÇEK toplam sayısıyla listelenir. Süre SINIRSIZ; "⏹ Durdur" çalışan sorguyu ct ile anında keser.
///
/// SEVİYE alanı/süzgeci KALDIRILDI (kullanıcı 2026-09-17: "seviye alanını belirleyemiyoruz ve
/// gereksiz — hem combodan hem griddeki kolondan kaldıralım"); yerine EK KOLONLAR geldi: gruplanan
/// her imzanın örnek satırından, seçilen ek kolonların değerleri grid'de yan yana gösterilir
/// (ör. StackTrace'e göre grupla, yanında ExceptionMessage'ı da gör).
///
/// Pano/CSV/Detay gibi görünüm-hizmetleri ana pencere olay işleyicilerinde (Karşılaştırma sekmesi
/// deseni) yürür — VM veriyi/sorguyu üretir, pencere I/O'yu yapar.
/// </summary>
public sealed partial class LogAnalizSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private const int SqlOrneklem = 20_000; // keşif (imza öğrenme) örneklemi — SQL ailesi
    private const int DetayEnFazla = 500;   // bir grubun ham kayıt tavanı (drill-down + sparkline)

    /// <summary>
    /// v22-S2 (saha turu-2 m.2, İKİNCİ tur — kullanıcı 0.23.0'te de "5 dakika bekledim, cevap
    /// gelmedi"): MongoDB'de örneklem 20.000 değil 5.000. Ölçüm darboğazın sunucu değil AĞ olduğunu
    /// gösterdi (20.000 belge = 19,8 MB tel üstünde; VPN arkasındaki gerçek QueryLog'da mesajlar
    /// KB'larca olduğundan çok daha fazla). 5.000 kayıt imza ÖĞRENMEK için fazlasıyla yeter; mesaj
    /// da sunucuda 300 karaktere kırpıldığından tel üstündeki veri iki kademe birden küçülür.
    /// </summary>
    private const int MongoOrneklem = 5_000;

    private int Orneklem => _motor == MotorTuru.Mongo ? MongoOrneklem : SqlOrneklem;

    private enum Kapsam { Son24, Aralik, Tumu }

    private readonly MotorTuru _motor;
    private readonly Func<string, Task<IReadOnlyList<SemaNesnesi>>> _tablolariGetir;
    private readonly Func<string, string, CancellationToken, Task<QueryResult>> _calistir;
    private readonly Func<string, Task<AsistanCevabi>>? _asistanSor;
    private readonly Func<Task<(string? Db, string? Tablo, string? Kolon)>>? _sonSecimOku;
    private readonly Action<string?, string?, string?>? _secimKaydet;

    private CancellationTokenSource? _analizCts; // ⏹ Durdur — çalışan analizi/detayı keser

    /// <summary>
    /// Koşu NESLİ (v20-S21 saha m.2): Durdur yalnız iptal İSTEĞİ gönderir; iptali dinlemeyen bir
    /// sürücüde (ör. Mongo find) await hiç dönmez ve buton/durum sonsuza dek asılı kalırdı. Durdur
    /// nesli artırıp UI'yı ANINDA boşa çıkarır; geciken koşunun devamları eski nesilden geldiği
    /// için duruma yazamaz (sessizce yok sayılır).
    /// </summary>
    private int _kosuNesli;

    private IReadOnlyList<SemaNesnesi> _tablolar = [];
    private bool _ilkDolum = true;

    /// <summary>Seçili tablonun ETKİN kolon envanteri (v20-S21 saha m.3): SQL'de şema kolonları,
    /// Mongo'da (faz-1 önbelleği BOŞ) seçilen koleksiyondan lazy yüklenen alanlar — zaman/mesaj
    /// tahminleri bunu kullanır (önceden boş şemadan tahmin → İlk/Son görülme Mongo'da hep boştu).</summary>
    private IReadOnlyList<SemaKolonu> _seciliKolonlar = [];

    // Son çalıştırılan analizin bağlamı (gerçek-sayım + detay bunu kullanır)
    private string _veritabani = "";
    private string _hedef = "";        // SQL: TamAd · Mongo: çıplak koleksiyon
    private string _mesajKolon = "";
    private string? _zamanKolon;
    private Kapsam _kapsamModu = Kapsam.Tumu;
    private DateTime? _kapsamBas, _kapsamBit;

    /// <summary>Son koşuda sorguya katılan EK kolon adları (v22-S12) — sonuç kümesinden değer
    /// okuma ve grid başlıkları bunun sırasını kullanır.</summary>
    private IReadOnlyList<string> _ekKolonAdlari = [];

    private List<LogGrupGorunum> _tumGruplar = [];
    private bool _gercekSayimTamam;

    // v20-S21 ara madde (kullanıcı: "log analizi çok yavaş"): gerçek sayım artık OTOMATİK koşmaz —
    // son örneklemin grupları saklanır, tam tarama yalnız "🔢 Gerçek toplamları getir" ile koşar.
    private IReadOnlyList<TabloLogGrubu>? _sonOrneklemGruplar;
    private DateTime _sonEsik;

    // Son seçim hatırlama (tek seferlik ilk yükleme tercihleri)
    private string? _hatirlaTablo, _hatirlaKolon;

    public LogAnalizSekmesiViewModel(
        MotorTuru motor,
        IReadOnlyList<string> veritabanlari,
        string? seciliVeritabani,
        Func<string, Task<IReadOnlyList<SemaNesnesi>>> tablolariGetir,
        Func<string, string, CancellationToken, Task<QueryResult>> calistir,
        Func<string, Task<AsistanCevabi>>? asistanSor = null,
        Func<Task<(string?, string?, string?)>>? sonSecimOku = null,
        Action<string?, string?, string?>? secimKaydet = null)
    {
        _motor = motor;
        _tablolariGetir = tablolariGetir;
        _calistir = calistir;
        _asistanSor = asistanSor;
        _sonSecimOku = sonSecimOku;
        _secimKaydet = secimKaydet;

        if (_motor == MotorTuru.Mongo)
        {
            _tabloEtiketi = "Koleksiyon:";
            _kolonEtiketi = "Mesaj alanı:";
            _ekKolonEtiketi = "Ek alanlar:";
            _logNotu = "Not: Bu ekranı exception/log KOLEKSİYONLARI için kullanınız. Koleksiyonu ve mesaj "
                + "alanını seçin — en çok tekrarlayan kayıtlar GERÇEK toplam sayısıyla listelenir. Bir gruba "
                + "çift tıklayarak arkasındaki ham belgeleri ve zaman dağılımını görebilirsiniz.";
            _ozet = "Veritabanı, koleksiyon ve mesaj alanını seçip “Analiz et”e basın.";
        }

        foreach (string db in veritabanlari)
            Veritabanlari.Add(db);
        _secilenVeritabani =
            seciliVeritabani is not null && veritabanlari.Contains(seciliVeritabani)
                ? seciliVeritabani
                : veritabanlari.FirstOrDefault();
    }

    // ── Görünüme bağlı durum ──────────────────────────────────────────────────
    public System.Collections.ObjectModel.ObservableCollection<string> Veritabanlari { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<string> Tablolar { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<string> Kolonlar { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<LogEkKolonOgesi> EkKolonOgeleri { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<LogGrupGorunum> Gruplar { get; } = [];

    [ObservableProperty] private string? _secilenVeritabani;
    [ObservableProperty] private string? _secilenTablo;
    [ObservableProperty] private string? _secilenKolon;
    [ObservableProperty] private LogGrupGorunum? _seciliGrup;

    [ObservableProperty] private bool _kolonKutusuAktif = true;

    [ObservableProperty] private string _tabloEtiketi = "Tablo:";
    [ObservableProperty] private string _kolonEtiketi = "Mesaj kolonu:";

    // ── EK KOLONLAR (v22-S12 — "birden fazla kolonu yan yana") ─────────────────
    /// <summary>Ek kolon açılır listesi görünür mü (ToggleButton + Popup — Veri Arama kapsam deseni).</summary>
    [ObservableProperty] private bool _ekKolonAcik;

    [ObservableProperty] private string _ekKolonEtiketi = "Ek kolonlar:";

    /// <summary>SON KOŞUNUN grid'e yansıyan ek kolon başlıkları — pencere bu değişince grid'e
    /// dinamik kolon ekler/söker (WPF DataGridColumn veriye bağlanamaz; köprü PropertyChanged).</summary>
    [ObservableProperty] private IReadOnlyList<string> _ekKolonBasliklari = [];

    /// <summary>Açılır düğme metni: "(yok) ▾" ya da "N kolon ▾".</summary>
    public string EkKolonOzeti
    {
        get
        {
            int n = EkKolonOgeleri.Count(o => o.Secili);
            return n == 0 ? "(yok) ▾" : $"{n} kolon ▾";
        }
    }

    private void EkKolonSecimiDegisti() => OnPropertyChanged(nameof(EkKolonOzeti));

    [RelayCommand]
    private void EkKolonTemizle()
    {
        foreach (LogEkKolonOgesi o in EkKolonOgeleri)
            o.Secili = false;
    }

    /// <summary>Seçili ek kolonlar (tablo kolon sırasıyla); mesaj kolonu ELENİR — örnek mesaj zaten
    /// kendi kolonunda, aynısını ikinci kez taşımanın/göstermenin anlamı yok.</summary>
    private IReadOnlyList<string> SeciliEkKolonlar(string mesajKolon)
        => [.. EkKolonOgeleri
            .Where(o => o.Secili && !string.Equals(o.Ad, mesajKolon, StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Ad)];
    [ObservableProperty] private string _logNotu =
        "Not: Bu ekranı exception/log tabloları için kullanınız; aksi takdirde verim alınamayabilir. "
        + "Tabloyu ve mesaj kolonunu seçin — en çok tekrarlayan kayıtlar GERÇEK toplam sayısıyla listelenir "
        + "(parametre farkları elenerek aynı hata tek grupta toplanır). Bir gruba çift tıklayarak arkasındaki "
        + "ham kayıtları ve zaman dağılımını görebilirsiniz.";

    [ObservableProperty] private DateTime? _baslangicTarihi;
    [ObservableProperty] private DateTime? _bitisTarihi;

    [ObservableProperty] private string _sonucSuz = "";
    [ObservableProperty] private string _ozet = "Veritabanı, tablo ve mesaj kolonunu seçip “Analiz et”e basın.";

    [ObservableProperty] private bool _durdurGorunur;

    /// <summary>"🔢 Gerçek toplamları getir" düğmesi (v20-S21 ara madde): yalnız örneklem KESİLDİYSE görünür.</summary>
    [ObservableProperty] private bool _gercekSayimGorunur;
    [ObservableProperty] private string _aiOzet = "";
    [ObservableProperty] private bool _aiOzetAcik;

    private bool _analizCalisiyor;
    public bool AnalizCalisiyor
    {
        get => _analizCalisiyor;
        private set
        {
            if (SetProperty(ref _analizCalisiyor, value))
            {
                OnPropertyChanged(nameof(Durum));
                OnPropertyChanged(nameof(AnalizBosta));
                AnalizCommand.NotifyCanExecuteChanged();
                AralikAnalizKomutCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool AnalizBosta => !AnalizCalisiyor;

    /// <summary>AI özet düğmesi yalnız asistan köprüsü varsa görünür.</summary>
    public bool AiGorunur => _asistanSor is not null;

    // ── İlk yükleme + seçim kademesi ──────────────────────────────────────────
    /// <summary>Sekme açılınca çalışır (pencere Loaded karşılığı): son seçim → tablolar.</summary>
    public async Task IlkYukleAsync()
    {
        _ilkDolum = false;
        try
        {
            if (_sonSecimOku is not null)
            {
                (string? db, string? tablo, string? kolon) = await _sonSecimOku();
                _hatirlaTablo = tablo;
                _hatirlaKolon = kolon;
                if (db is not null && Veritabanlari.Contains(db))
                {
                    _ilkDolum = true;          // load'ı tetiklemeden ata
                    SecilenVeritabani = db;
                    _ilkDolum = false;
                }
            }
            await TablolariYukleAsync();
        }
        catch (Exception ex) { Ozet = $"Tablolar yüklenemedi: {ex.Message}"; }
    }

    partial void OnSecilenVeritabaniChanged(string? value)
    {
        if (_ilkDolum)
            return;
        Tablolar.Clear();
        Kolonlar.Clear();
        EkKolonOgeleri.Clear();
        OnPropertyChanged(nameof(EkKolonOzeti));
        Gruplar.Clear();
        _ = VeritabaniDegistiAsync();
    }

    private async Task VeritabaniDegistiAsync()
    {
        try { await TablolariYukleAsync(); }
        catch (Exception ex) { Ozet = $"Tablolar yüklenemedi: {ex.Message}"; }
    }

    private async Task TablolariYukleAsync()
    {
        if (SecilenVeritabani is not string veritabani)
        {
            Ozet = "Analiz edilecek veritabanı seçin.";
            return;
        }

        _tablolar = await _tablolariGetir(veritabani);
        Tablolar.Clear();
        foreach (SemaNesnesi t in _tablolar)
            Tablolar.Add(t.TamAd);

        // Hatırlanan tablo varsa onu, yoksa adı log/exception çağrıştıran ilk tabloyu ön-seç.
        string? aday = _hatirlaTablo is { } ht && _tablolar.Any(t => t.TamAd == ht)
            ? ht
            : _tablolar.Select(t => t.TamAd).FirstOrDefault(ad =>
                ad.Contains("log", StringComparison.OrdinalIgnoreCase)
                || ad.Contains("exception", StringComparison.OrdinalIgnoreCase)
                || ad.Contains("hata", StringComparison.OrdinalIgnoreCase));
        _hatirlaTablo = null; // tek seferlik
        if (aday is not null)
            SecilenTablo = aday;
        else if (_tablolar.Count == 0)
            Ozet = $"{veritabani} içinde tablo bulunamadı (şema henüz yüklenmemiş olabilir).";
    }

    partial void OnSecilenTabloChanged(string? value) => _ = TabloDegistiAsync(value);

    private async Task TabloDegistiAsync(string? value)
    {
        SemaNesnesi? tablo = _tablolar.FirstOrDefault(t => t.TamAd == value);
        if (tablo is null)
            return;
        _seciliKolonlar = []; // yeni tablo: eski alan envanteri geçersiz

        // Mongo'da alanlar faz-1 önbelleğinde boştur → seçilen koleksiyondan hafif find(limit 50).
        IReadOnlyList<SemaKolonu> kolonlar = tablo.Kolonlar;
        if (kolonlar.Count == 0 && _motor == MotorTuru.Mongo
            && SecilenVeritabani is string veritabani)
        {
            Kolonlar.Clear();
            Kolonlar.Add("alanlar yükleniyor…");
            SecilenKolon = "alanlar yükleniyor…";
            KolonKutusuAktif = false;
            try
            {
                // v22-S2 (m.2 ikinci tur): eskiden {"find": X, "limit": 50} koşuyordu — yani 50 TAM
                // BELGE ağdan geçiyordu. QueryLog gibi mesaj/gövde alanları KB'larca olan
                // koleksiyonlarda bu tek başına megabaytlar (VPN arkasında dakikalar). Artık yalnız
                // ALAN ADI + TİPİ taşınıyor ($objectToArray + $type) — maliyet belge boyutundan bağımsız.
                QueryResult sonuc = await _calistir(veritabani,
                    MongoAlanEnvanteri.Sorgu(tablo.Ad), CancellationToken.None);
                // v20-S21 (saha m.3): GERÇEK tip taşınır ("date" dahil) — hepsi "string" yazılınca
                // zaman alanı tahmini hiç eşleşmiyor, İlk/Son görülme Mongo'da hep boş kalıyordu.
                kolonlar = sonuc.Basarili && sonuc.ResultSetler.Count > 0
                    ? MongoAlanEnvanteri.Coz(sonuc.ResultSetler[0].Satirlar.Select(s => s.Length > 0 ? s[0] : null))
                    : [];
                if (kolonlar.Count == 0)
                    Ozet = sonuc.Hata is not null
                        ? $"⚠ Alanlar okunamadı: {sonuc.Hata.Mesaj}"
                        : "⚠ Koleksiyon boş görünüyor — alan listesi çıkarılamadı (başka koleksiyon seçin).";
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                kolonlar = [];
                Ozet = $"⚠ Alanlar okunamadı: {ex.Message}";
            }
            finally { KolonKutusuAktif = true; }

            if (SecilenTablo != tablo.TamAd)
                return;
        }

        _seciliKolonlar = kolonlar; // zaman/mesaj tahminleri bu envanteri kullanır (m.3)
        List<string> adlar = [.. kolonlar.Select(k => k.Ad)];
        Kolonlar.Clear();
        foreach (string a in adlar)
            Kolonlar.Add(a);
        SecilenKolon = _hatirlaKolon is { } hk && adlar.Contains(hk)
            ? hk
            : LogTabloAnaliz.MesajKolonuTahmini(kolonlar);
        _hatirlaKolon = null; // tek seferlik

        // Ek kolon adayları (v22-S12): tablonun TÜM kolonları işaretlenebilir liste olarak sunulur;
        // seçim tablo değişince sıfırlanır (yeni tablonun kolonları farklıdır).
        EkKolonOgeleri.Clear();
        foreach (string a in adlar)
            EkKolonOgeleri.Add(new LogEkKolonOgesi(a, EkKolonSecimiDegisti));
        OnPropertyChanged(nameof(EkKolonOzeti));
    }

    /// <summary>Ortak seçim doğrulaması: DB + tablo + kolon; şema nesnesi ve Mongo için ÇIPLAK ad.</summary>
    private (string Veritabani, string Tablo, string Kolon, SemaNesnesi? Nesne, string Hedef)? SecimAl()
    {
        if (SecilenVeritabani is not string veritabani)
        {
            Ozet = "Önce veritabanını seçin.";
            return null;
        }
        string? tablo = SecilenTablo;
        string? kolon = SecilenKolon;
        if (string.IsNullOrWhiteSpace(tablo) || string.IsNullOrWhiteSpace(kolon))
        {
            Ozet = "Önce tabloyu ve mesaj kolonunu seçin.";
            return null;
        }
        SemaNesnesi? nesne = _tablolar.FirstOrDefault(t => t.TamAd == tablo);
        string hedef = _motor == MotorTuru.Mongo ? nesne?.Ad ?? tablo : tablo;
        return (veritabani, tablo, kolon, nesne, hedef);
    }

    // ── Analiz koşuları ────────────────────────────────────────────────────────
    // AllowConcurrentExecutions (v20-S21 saha m.2 canlı test): AsyncRelayCommand varsayılanı, kendi
    // Task'i bitmeden CanExecute'u false tutar — Durdur UI'yı boşaltsa bile iptali dinlemeyen sorgunun
    // task'i uçuşta kaldığından "Analiz et" PASİF kalıyordu. Eşzamanlılık korkusu yok: buton zaten
    // AnalizBosta'yla kapanır, geciken koşuyu nesil bekçisi etkisizleştirir.
    [RelayCommand(CanExecute = nameof(AnalizBosta), AllowConcurrentExecutions = true)]
    private async Task AnalizAsync() => await Son24Analiz();

    [RelayCommand(CanExecute = nameof(AnalizBosta), AllowConcurrentExecutions = true)]
    private async Task AralikAnalizKomutAsync() => await AralikAnaliz();

    /// <summary>Zaman tahmini için etkin kolonlar: şema doluysa şema, değilse lazy yüklenen alanlar (m.3).</summary>
    private IReadOnlyList<SemaKolonu> AktifKolonlar(SemaNesnesi? nesne)
        => nesne is { Kolonlar.Count: > 0 } n ? n.Kolonlar : _seciliKolonlar;

    /// <summary>
    /// Mongo index köprüsü (MainViewModel bağlar): (veritabanı, koleksiyon) → index'lerin ÖNDE GELEN
    /// alanları. Log analizi strateji seçmek için sorar (v22-S1 m.2). null → köprü yok sayılır ve
    /// eski davranış (24 saat süzgeci) korunur — testler/SQL motorları etkilenmez.
    /// </summary>
    public Func<string, string, Task<IReadOnlyList<string>>>? MongoIndexAlanlari { get; set; }

    private readonly Dictionary<string, IReadOnlyList<string>> _indexOnbellek = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Zaman alanı bir index'in İLK alanı mı (aralık taraması onu kullanabilir mi)? Köprü yoksa
    /// "evet" varsayılır; sonuç koleksiyon başına önbelleklenir (her analizde sormamak için).
    ///
    /// ⏱ SÜRE SINIRI (v22-S4 saha turu-4 m.6 — kullanıcı: "analiz ÇALIŞAMIYOR"): bu kontrol analiz
    /// sorgusundan ÖNCE, kullanıcıya hiçbir durum yazılmadan ve ⏹ Durdur daha ortada yokken koşuyor;
    /// üstelik jeton verilmediği için (MongoIndexAlanlari köprüsü CancellationToken.None kullanır)
    /// kesilemiyordu. Yanıt gelmezse ekranda HİÇBİR ŞEY olmuyor — kullanıcının gördüğü tam olarak bu.
    /// Bu bir HIZLANDIRMA ipucudur, zorunlu bilgi değil: 3 sn'de gelmezse "index yok" varsayılır ve
    /// zaten güvenli/hızlı olan "son N kayıt" yoluna düşülür.
    /// </summary>
    private async Task<bool> ZamanAlaniIndexliMi(string veritabani, string koleksiyon, string alan)
    {
        if (MongoIndexAlanlari is null)
            return true;
        string anahtar = $"{veritabani}.{koleksiyon}";
        if (!_indexOnbellek.TryGetValue(anahtar, out IReadOnlyList<string>? alanlar))
        {
            try
            {
                alanlar = await MongoIndexAlanlari(veritabani, koleksiyon)
                    .WaitAsync(TimeSpan.FromSeconds(IndexSorgusuTavaniSn));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException
                                        or OperationCanceledException)
            {
                Serilog.Log.Information(
                    "🧾 Log analizi · index bilgisi {Sn} sn'de gelmedi ({Hedef}) — index YOK varsayıldı",
                    IndexSorgusuTavaniSn, anahtar);
                return false; // güvenli varsayım: hızlı "son N kayıt" yoluna düş
            }
            _indexOnbellek[anahtar] = alanlar;
        }
        return alanlar.Any(a => string.Equals(a, alan, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Index bilgisi bu süreyi aşarsa beklenmez (m.6) — yalnız bir hız ipucudur.</summary>
    private const int IndexSorgusuTavaniSn = 3;

    /// <summary>Hız notu (v22-S1): index olmadığı için strateji değiştiyse özete eklenir.</summary>
    private string? _indexOnerisi;

    /// <summary>Günlük panel: SON 24 SAAT (zaman kolonu otomatik; yoksa tüm tablo + açık bildirim).</summary>
    private async Task Son24Analiz()
    {
        if (SecimAl() is not { } s)
            return;
        string? zamanKolonu = LogTabloAnaliz.ZamanKolonuTahmini(AktifKolonlar(s.Nesne));
        IReadOnlyList<string> ekler = SeciliEkKolonlar(s.Kolon); // v22-S12: yan yana ek kolonlar
        BaglamKur(s, zamanKolonu, zamanKolonu is null ? Kapsam.Tumu : Kapsam.Son24, null, null, ekler);

        // 🐢→⚡ v22-S1 (saha turu-2 m.2 "Mongo'da log analizi çok geç cevap veriyor"): Mongo'da zaman
        // alanı INDEX'Lİ DEĞİLSE "son 24 saat" süzgeci sunucuda TÜM koleksiyonu tarar — ölçüldü
        // (600k belge, son 24 saatte ~2.000 kayıt): 961 ms ↔ index'liyken 11 ms; kullanıcının
        // milyonlarca kayıtlı QueryLog'unda bu onlarca saniye. Zaman süzgeci OLMAYAN "son N kayıt"
        // ($natural:-1) index'ten bağımsız 86 ms. Bu yüzden index yoksa oraya düşülür.
        // m.6: index kontrolü analiz sorgusundan ÖNCE koşuyor — o sırada ekran sessiz kalmasın.
        if (_motor == MotorTuru.Mongo && zamanKolonu is not null)
            Ozet = $"{s.Veritabani} · {s.Tablo} — index bilgisi okunuyor…";
        bool zamanIndexsiz = _motor == MotorTuru.Mongo && zamanKolonu is not null
            && !await ZamanAlaniIndexliMi(s.Veritabani, s.Hedef, zamanKolonu);

        string sql = (zamanKolonu, zamanIndexsiz) switch
        {
            (null, _) when _motor == MotorTuru.Mongo =>
                LogTabloAnaliz.SonKayitlarSorgusu(_motor, s.Hedef, s.Kolon, Orneklem, null, ekKolonlar: ekler),
            (null, _) => LogTabloAnaliz.OrneklemSorgusu(_motor, s.Hedef, s.Kolon, Orneklem, null, ekKolonlar: ekler),
            (not null, true) => LogTabloAnaliz.SonKayitlarSorgusu(
                _motor, s.Hedef, s.Kolon, Orneklem, zamanKolonu, ekKolonlar: ekler),
            _ => LogTabloAnaliz.Orneklem24SaatSorgusu(
                _motor, s.Hedef, s.Kolon, zamanKolonu!, Orneklem, ekKolonlar: ekler),
        };
        string kapsam = (zamanKolonu, zamanIndexsiz) switch
        {
            (null, _) when _motor == MotorTuru.Mongo => $"son {Orneklem:N0} kayıt (zaman alanı bulunamadı)",
            (null, _) => "zaman kolonu bulunamadı — tüm tablodan örneklendi",
            (not null, true) => $"son {Orneklem:N0} kayıt ({zamanKolonu} alanında index yok)",
            _ => "son 24 saat",
        };
        string durum = $"{s.Veritabani} · {s.Tablo} — {kapsam} okunuyor…";
        _indexOnerisi = zamanIndexsiz
            ? $"⚡ Hız notu: '{zamanKolonu}' alanında index olmadığı için 24 saat süzgeci sunucuda TÜM "
                + $"koleksiyonu tarardı; onun yerine son {Orneklem:N0} kayıt okundu. Kalıcı çözüm (Mongo'da "
                + $"bir kez): db.{s.Hedef}.createIndex({{ {zamanKolonu}: -1 }})"
            : null;
        await AnalizKos(sql, durum, kapsam);
    }

    /// <summary>Tüm Zamanlar paneli: tarih aralığı ya da (iki tarih boşsa) tüm tablo örneklemi.</summary>
    private async Task AralikAnaliz()
    {
        if (SecimAl() is not { } s)
            return;
        string? zamanKolonu = LogTabloAnaliz.ZamanKolonuTahmini(AktifKolonlar(s.Nesne));
        IReadOnlyList<string> ekler = SeciliEkKolonlar(s.Kolon); // v22-S12: yan yana ek kolonlar

        DateTime? bas = BaslangicTarihi, bit = BitisTarihi;
        if (bas is null && bit is null)
        {
            BaglamKur(s, zamanKolonu, Kapsam.Tumu, null, null, ekler);
            // v22-S1: Mongo'da "tüm zamanlar" artık ilk N (en ESKİ) değil SON N kayıt — log ekranında
            // anlamlı dilim en yenidir ve $natural:-1 index'siz de sabit maliyetlidir (ölçüm: 86 ms).
            _indexOnerisi = null;
            bool mongo = _motor == MotorTuru.Mongo;
            await AnalizKos(
                mongo
                    ? LogTabloAnaliz.SonKayitlarSorgusu(_motor, s.Hedef, s.Kolon, Orneklem, zamanKolonu, ekKolonlar: ekler)
                    : LogTabloAnaliz.OrneklemSorgusu(_motor, s.Hedef, s.Kolon, Orneklem, zamanKolonu, ekKolonlar: ekler),
                $"{s.Veritabani} · {s.Tablo} — {(mongo ? "son" : "ilk")} {Orneklem:N0} kayıt okunuyor…",
                mongo ? $"tüm zamanlar — son {Orneklem:N0} kayıt" : "tüm zamanlar");
            return;
        }
        if (bas is null || bit is null)
        {
            Ozet = "Aralık için iki tarihi de seçin (ya da ikisini de boş bırakın = tüm zamanlar).";
            return;
        }
        if (bit < bas)
        {
            Ozet = "Bitiş tarihi başlangıçtan önce olamaz.";
            return;
        }
        if (zamanKolonu is null)
        {
            Ozet = $"{s.Tablo} içinde tarih/zaman kolonu bulunamadı — tarih aralığı uygulanamıyor. "
                + "(İki tarihi boş bırakırsanız tüm tablo örneklenir.)";
            return;
        }

        DateTime basT = bas.Value.Date, bitT = bit.Value.Date.AddDays(1); // bitiş günü DAHİL
        BaglamKur(s, zamanKolonu, Kapsam.Aralik, basT, bitT, ekler);
        string aralikMetni = $"{basT:dd.MM.yyyy} – {bit.Value:dd.MM.yyyy}";
        // Aralık GEÇMİŞTE bir dilim olabilir → "son N kayıt" kısayolu burada işe yaramaz; sorgu aynen
        // koşar ama index yoksa yavaşlığın NEDENİ ve kalıcı çözümü kullanıcıya yazılır (v22-S1).
        if (_motor == MotorTuru.Mongo)
            Ozet = $"{s.Veritabani} · {s.Tablo} — index bilgisi okunuyor…"; // m.6: ekran sessiz kalmasın
        _indexOnerisi = _motor == MotorTuru.Mongo
            && !await ZamanAlaniIndexliMi(s.Veritabani, s.Hedef, zamanKolonu)
            ? $"⚡ Hız notu: '{zamanKolonu}' alanında index yok — tarih aralığı süzgeci sunucuda TÜM "
                + $"koleksiyonu tarıyor (yavaşlığın nedeni bu). Kalıcı çözüm (Mongo'da bir kez): "
                + $"db.{s.Hedef}.createIndex({{ {zamanKolonu}: -1 }})"
            : null;
        await AnalizKos(
            LogTabloAnaliz.OrneklemAralikSorgusu(
                _motor, s.Hedef, s.Kolon, zamanKolonu, basT, bitT, Orneklem, ekKolonlar: ekler),
            $"{s.Veritabani} · {s.Tablo} — {aralikMetni} okunuyor…", aralikMetni);
    }

    private void BaglamKur((string Veritabani, string Tablo, string Kolon, SemaNesnesi? Nesne, string Hedef) s,
        string? zamanKolonu, Kapsam kapsam, DateTime? bas, DateTime? bit, IReadOnlyList<string> ekKolonlar)
    {
        _veritabani = s.Veritabani;
        _hedef = s.Hedef;
        _mesajKolon = s.Kolon;
        _zamanKolon = zamanKolonu;
        _kapsamModu = kapsam;
        _kapsamBas = bas;
        _kapsamBit = bit;
        _ekKolonAdlari = ekKolonlar;
    }

    /// <summary>
    /// Ortak koşu: keşif → gruplama → örneklem sonuçlarını HEMEN göster → GERÇEK sayımı arkadan getirip
    /// güncelle. Süre SINIRSIZ; "⏹ Durdur" çalışan sorguyu ANINDA keser — o an örneklem sonuçları kalır.
    /// </summary>
    private async Task AnalizKos(string sql, string durum, string kapsam)
    {
        CancellationToken ct = IslemBaslat();
        int nesil = _kosuNesli; // Durdur/yeni koşu nesli artırırsa bu koşunun devamları yazamaz
        AnalizCalisiyor = true;
        Ozet = durum;
        Gruplar.Clear();
        _tumGruplar = [];
        GercekSayimGorunur = false; // yeni koşu: eski örneklemin sayım düğmesi geçersiz
        _sonOrneklemGruplar = null;
        try
        {
            // v22-S2 (m.2 ikinci tur): FAZ SÜRELERİ ölçülür. Kullanıcı "yavaş" dediğinde hangi fazın
            // yediğini tahmin etmek yerine görelim: sunucu sorgusu mu, gruplama mı, taşınan veri mi.
            var kronometre = System.Diagnostics.Stopwatch.StartNew();

            // 🔎 v22-S4 saha turu-4 m.6 (kullanıcı: "analiz ÇALIŞAMIYOR, çalışsa ekran görüntüsünü
            // alacağım"): analiz bilerek SÜRESİZDİR (v20-S6). Dönmezse ekranda tek bir "okunuyor…"
            // kalıyordu ve ⏱ faz satırı hiç yazılmıyordu — o satır ancak sonuç dönünce oluşur. Yani
            // teşhize en çok ihtiyaç duyulan durumda elimizde HİÇBİR kayıt olmuyordu. İki ekleme:
            //   (a) sorgu METNİ koşmadan ÖNCE loga düşer → hiç dönmese de hangi şeklin asıldığı bilinir;
            //   (b) durum satırı saniye sayar → "uygulama dondu" ile "sunucu hâlâ düşünüyor" ayrışır.
            Serilog.Log.Information(
                "🧾 Log analizi BAŞLADI · {Hedef} · {Kapsam} · örneklem {Orneklem} · {Sql}",
                _hedef, kapsam, Orneklem, sql);
            using SureSayaci sayac = SureSayaci.Baslat(() =>
            {
                if (nesil == _kosuNesli && AnalizCalisiyor)
                    Ozet = $"{durum}  ({kronometre.Elapsed.TotalSeconds:N0} sn — ⏹ Durdur ile kesebilirsiniz)";
            });

            QueryResult sonuc = await _calistir(_veritabani, sql, ct);
            TimeSpan sorguSuresi = kronometre.Elapsed;
            if (nesil != _kosuNesli) return; // durduruldu/yenisi başladı — geciken sonuç yok sayılır
            if (sonuc.IptalEdildi) { Ozet = "Analiz durduruldu."; return; }
            if (sonuc.Hata is { } hata) { Ozet = $"Sorgu hatası: {hata.Mesaj}"; return; }
            if (sonuc.ResultSetler.Count == 0) { Ozet = "Sonuç dönmedi."; return; }

            ResultSetData rs = sonuc.ResultSetler[0];
            int mesajIdx = Math.Max(0, KolonIndeksi(rs.Kolonlar, _mesajKolon));
            int zamanIdx = _zamanKolon is null ? -1 : KolonIndeksi(rs.Kolonlar, _zamanKolon);
            // v22-S12: ek kolonların sonuç kümesindeki yerleri — ada göre (SELECT tekrarları ayıklar,
            // ör. ek kolon == zaman kolonu; adla arandığından sıra kaymaz).
            int[] ekIdx = [.. _ekKolonAdlari.Select(a => KolonIndeksi(rs.Kolonlar, a))];

            // v20-S14 hız: gruplama (20k satır × ~6 regex/imza) UI thread'ini dondurmasın — arka plana al.
            // rs.Satirlar materyalize (salt-okunur) → başka thread'ten güvenle gezilir.
            var projeksiyon = rs.Satirlar.Select(satir => (
                Mesaj: satir.Length > mesajIdx ? satir[mesajIdx] : null,
                Zaman: zamanIdx >= 0 && satir.Length > zamanIdx ? satir[zamanIdx] : null,
                Seviye: (object?)null, // seviye kavramı sekmeden kaldırıldı (2026-09-17)
                Ekler: ekIdx.Length == 0
                    ? null
                    : ekIdx.Select(i => i >= 0 && satir.Length > i ? satir[i] : null).ToArray()));
            TimeSpan gruplamaBasi = kronometre.Elapsed;
            IReadOnlyList<TabloLogGrubu> gruplar =
                await Task.Run(() => LogTabloAnaliz.Grupla(projeksiyon, enFazla: 50), ct);
            _fazNotu = $"⏱ sorgu {sorguSuresi.TotalSeconds:N1} sn · "
                + $"gruplama {(kronometre.Elapsed - gruplamaBasi).TotalSeconds:N1} sn · "
                + $"{rs.Satirlar.Count:N0} kayıt · ~{sonuc.ToplamBayt / 1024.0 / 1024.0:N1} MB";
            Serilog.Log.Information("🧾 Log analizi · {Faz} · {Hedef}", _fazNotu, _hedef);
            if (nesil != _kosuNesli) return;

            DateTime esik = DateTime.Now.AddHours(-24);
            GruplariGoster(gruplar, esik);   // örneklem sonuçları HEMEN görünsün
            EkKolonBasliklari = _ekKolonAdlari; // v22-S12: pencere bu bildirimde grid kolonlarını kurar
            KaydetSecim();

            if (_tumGruplar.Count == 0)
            {
                Ozet = HizNotuEkle(
                    $"{sonuc.ToplamSatir:N0} kayıt okundu ({kapsam}) — gruplanacak metin bulunamadı (kolon boş olabilir).");
                return;
            }

            // v20-S14 hız: örneklem tabloyu/pencereyi TAM kapsadıysa grup sayıları ZATEN gerçektir →
            // pahalı %-başlı 50×LIKE tam-tarama sayımını ATLA (çoğu analizde kayıt < örneklem).
            //
            // 🩹 v22-S4 saha turu-4 m.6 (denetim bulgusu): kapsama ölçütü YANLIŞTI. `SatirSiniriAsildi`
            // OKUYUCUNUN sınırında (20.000) doğar; oysa sorgu bizim ÖRNEKLEM tavanımızla (Mongo'da
            // 5.000) zaten kesilmiş oluyor — okuyucu sınırına asla varılmıyor, bayrak HİÇ true olmuyor.
            // Sonuç: milyonlarca kayıtlık koleksiyonun 5.000'i okunuyor ve ekran bunu "gerçek toplam"
            // diye yazıyordu; üstelik "🔢 Gerçek toplamları getir" düğmesi hiç çıkmıyordu. Yani
            // kullanıcının bir kez şikâyet ettiği "yanıltıcı sayı" sessizce geri gelmişti.
            // Doğru ölçüt: okunan satır BİZİM tavanımıza dayandıysa daha fazlası olabilir.
            bool orneklemTavanindaKesildi = rs.Satirlar.Count >= Orneklem || sonuc.SatirSiniriAsildi;
            if (!orneklemTavanindaKesildi)
            {
                _gercekSayimTamam = true;
                Ozet = HizNotuEkle($"{_tumGruplar.Count} grup · {kapsam}. En çok: “{Kisalt(_tumGruplar[0].OrnekMesaj)}” — "
                    + $"{_tumGruplar[0].Sayi:N0} kez (gerçek toplam · {sonuc.ToplamSatir:N0} kayıt).");
                return;
            }

            // v20-S21 ara madde (kullanıcı: "log analizi çok yavaş"): gerçek sayım (grup başına %-başlı
            // LIKE'lı TAM TARAMA, süresiz) artık burada OTOMATİK koşmaz — dev tabloda her analizi
            // dakikalarca kilitliyordu. Örneklem anında kalır; tam sayım "🔢 Gerçek toplamları getir" ile.
            _gercekSayimTamam = false;
            _sonOrneklemGruplar = gruplar;
            _sonEsik = esik;
            GercekSayimGorunur = true;
            Ozet = HizNotuEkle($"{_tumGruplar.Count} grup · {kapsam}. En çok: “{Kisalt(_tumGruplar[0].OrnekMesaj)}” — "
                + $"{_tumGruplar[0].Sayi:N0} kez (örneklem — {Orneklem:N0} kayıt; "
                + "tam sayı için 🔢 Gerçek toplamları getir).");
        }
        catch (OperationCanceledException)
        {
            if (nesil == _kosuNesli)
                Ozet = "Analiz durduruldu (örneklem gösteriliyor).";
        }
        finally
        {
            if (nesil == _kosuNesli) // Durdur zaten boşalttıysa (ya da yenisi başladıysa) dokunma
            {
                AnalizCalisiyor = false;
                IslemBitti();
            }
        }
    }

    /// <summary>Son analizin faz süreleri (v22-S2) — özete eklenir, aynısı loga da düşer.</summary>
    private string? _fazNotu;

    /// <summary>Faz sürelerini + hız notunu (index önerisi) özetin sonuna ekler (v22-S1/S2).</summary>
    private string HizNotuEkle(string ozet)
    {
        if (_fazNotu is { Length: > 0 } faz)
            ozet = $"{ozet}\n{faz}";
        return _indexOnerisi is { Length: > 0 } not ? $"{ozet}\n{not}" : ozet;
    }

    /// <summary>Grupları görünüm sarmalayıcısına çevirip ("YENİ" bayrağıyla) grid'e süzerek yansıtır.</summary>
    private void GruplariGoster(IReadOnlyList<TabloLogGrubu> gruplar, DateTime yeniEsik)
    {
        _tumGruplar = [.. gruplar.Select(g => new LogGrupGorunum(g, g.IlkGorulme is { } ig && ig >= yeniEsik))];
        SuzUygula();
    }

    /// <summary>
    /// GERÇEK sayım geçişi: örneklemde bulunan imzaların LIKE kalıplarını TEK taramada sayar, grup
    /// sayılarını gerçek toplamla değiştirir ve yeniden sıralar. İptal/başarısızlıkta örneklem korunur.
    /// </summary>
    private async Task<IReadOnlyList<TabloLogGrubu>> GercekSayimUygula(IReadOnlyList<TabloLogGrubu> gruplar, CancellationToken ct)
    {
        _gercekSayimTamam = false;
        List<TabloLogGrubu> desenli = [.. gruplar.Where(g => !string.IsNullOrEmpty(g.Desen))];
        if (desenli.Count == 0)
            return gruplar;
        try
        {
            string sql = LogTabloAnaliz.GercekSayimSorgusu(
                _motor, _hedef, _mesajKolon, [.. desenli.Select(g => g.Desen!)],
                _zamanKolon, _kapsamModu == Kapsam.Son24, _kapsamBas, _kapsamBit);
            QueryResult r = await _calistir(_veritabani, sql, ct);
            if (r.IptalEdildi || !r.Basarili || r.ResultSetler.Count == 0 || r.ResultSetler[0].Satirlar.Count == 0)
                return gruplar;

            ResultSetData rs = r.ResultSetler[0];
            object?[] satir = rs.Satirlar[0];
            List<TabloLogGrubu> yeni = new(desenli.Count);
            for (int i = 0; i < desenli.Count; i++)
            {
                int idx = KolonIndeksi(rs.Kolonlar, $"c{i}");
                int sayi = idx >= 0 && satir.Length > idx ? KonvInt(satir[idx]) : desenli[i].Sayi;
                yeni.Add(desenli[i] with { Sayi = sayi, GercekSayimMi = true });
            }
            _gercekSayimTamam = true;
            return [.. yeni.OrderByDescending(g => g.Sayi).ThenBy(g => g.Imza, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            return gruplar; // örneklem sayılarıyla devam
        }
    }

    /// <summary>Yeni kesilebilir işlem başlatır (öncekini iptal eder), "⏹ Durdur"u gösterir, token döner.</summary>
    private CancellationToken IslemBaslat()
    {
        IptalYardimcisi.ArkaPlandaIptal(_analizCts, birak: true); // m.15: Cancel UI'da bloklayabilir
        _analizCts = new CancellationTokenSource();
        _kosuNesli++; // önceki koşunun geciken devamları artık duruma yazamaz
        DurdurGorunur = true;
        return _analizCts.Token;
    }

    private void IslemBitti() => DurdurGorunur = false;

    /// <summary>Hızlı tarih aralığı (v20-S21 saha m.5): "0"=bugün, "7"=son 7 gün, "30"=son 30 gün.</summary>
    [RelayCommand]
    private void HizliAralik(string gun)
    {
        int g = int.TryParse(gun, out int n) ? n : 0;
        BitisTarihi = DateTime.Today;
        BaslangicTarihi = DateTime.Today.AddDays(-g);
    }

    /// <summary>Tarih aralığını temizler → iki tarih boş = tüm tablo örneklenir (v20-S21 saha m.5).</summary>
    [RelayCommand]
    private void AralikTemizle()
    {
        BaslangicTarihi = null;
        BitisTarihi = null;
    }

    [RelayCommand]
    private void Durdur()
    {
        // v20-S21 (saha m.2): iptal isteği gönderilir ama UI ona MAHKUM DEĞİLDİR — nesil artar,
        // durum ANINDA boşalır; sorgu iptali dinlemiyorsa arka planda sonlanır ve yok sayılır.
        IptalYardimcisi.ArkaPlandaIptal(_analizCts); // m.15: Cancel UI'da bloklayabilir — havuzda
        _kosuNesli++;
        AnalizCalisiyor = false;
        IslemBitti();
        Ozet = "Durduruldu.";
    }

    /// <summary>
    /// 🔢 Gerçek toplamları getir (v20-S21 ara madde): örneklemde bulunan grupların GERÇEK toplamları —
    /// grup başına %-başlı LIKE'lı TAM TARAMA (süresiz; ⏹ Durdur keser). Eskiden her analizden sonra
    /// otomatik koşup dev tabloda ekranı dakikalarca meşgul ediyordu; artık yalnız istenince koşar.
    /// </summary>
    [RelayCommand(CanExecute = nameof(AnalizBosta), AllowConcurrentExecutions = true)]
    private async Task GercekSayimGetirAsync()
    {
        if (_sonOrneklemGruplar is not { Count: > 0 } gruplar)
            return;
        CancellationToken ct = IslemBaslat();
        int nesil = _kosuNesli;
        AnalizCalisiyor = true;
        Ozet = "Gerçek toplamlar hesaplanıyor — tam tablo taraması… (⏹ Durdur ile örneklemle kalırsınız)";
        try
        {
            IReadOnlyList<TabloLogGrubu> gercek = await GercekSayimUygula(gruplar, ct);
            if (nesil != _kosuNesli) return;
            GruplariGoster(gercek, _sonEsik);
            GercekSayimGorunur = !_gercekSayimTamam; // başarıldıysa düğme gizlenir
            Ozet = _tumGruplar.Count == 0
                ? "Gruplanacak kayıt kalmadı."
                : $"{_tumGruplar.Count} grup. En çok: “{Kisalt(_tumGruplar[0].OrnekMesaj)}” — {_tumGruplar[0].Sayi:N0} kez"
                    + (_gercekSayimTamam ? " (gerçek toplam)." : " (örneklem — gerçek sayım alınamadı).");
        }
        catch (OperationCanceledException)
        {
            if (nesil == _kosuNesli)
                Ozet = "Gerçek sayım durduruldu (örneklem gösteriliyor).";
        }
        finally
        {
            if (nesil == _kosuNesli)
            {
                AnalizCalisiyor = false;
                IslemBitti();
            }
        }
    }

    /// <summary>Sonuç listesini arama metnine göre süzer (istemci tarafı).</summary>
    private void SuzUygula()
    {
        string q = SonucSuz?.Trim() ?? "";
        IEnumerable<LogGrupGorunum> gor = _tumGruplar;
        if (q.Length > 0)
            gor = gor.Where(g => g.OrnekMesaj.Contains(q, StringComparison.OrdinalIgnoreCase));
        Gruplar.Clear();
        foreach (LogGrupGorunum g in gor)
            Gruplar.Add(g);
    }

    partial void OnSonucSuzChanged(string value) => SuzUygula();

    // ── Görünüm-hizmetleri için köprüler (ana pencere işleyicileri çağırır) ────
    /// <summary>Seçili grubun LIKE kalıbına uyan HAM kayıtları getirir; pencere LogDetayPenceresi açar.</summary>
    public async Task<(ResultSetData? Rs, string Baslik, int Sayi, string? ZamanKolon)> DetayGetirAsync()
    {
        if (SeciliGrup is not { } g || string.IsNullOrEmpty(g.Desen))
            return (null, "", 0, null);
        CancellationToken ct = IslemBaslat();  // dev tabloda detay taraması da uzun sürebilir → Durdur'la kesilir
        int nesil = _kosuNesli; // Durdur UI'yı boşaltırsa geciken detay sonucu yok sayılır (m.2)
        Ozet = "Ham kayıtlar getiriliyor… (⏹ Durdur ile iptal)";
        try
        {
            string sql = LogTabloAnaliz.DetaySorgusu(
                _motor, _hedef, _mesajKolon, g.Desen!, DetayEnFazla,
                _zamanKolon, _kapsamModu == Kapsam.Son24, _kapsamBas, _kapsamBit);
            QueryResult r = await _calistir(_veritabani, sql, ct);
            if (nesil != _kosuNesli) return (null, "", 0, null);
            if (r.IptalEdildi) { Ozet = "Detay durduruldu."; return (null, "", 0, null); }
            if (r.Hata is { } h) { Ozet = $"Detay hatası: {h.Mesaj}"; return (null, "", 0, null); }
            if (r.ResultSetler.Count == 0 || r.ResultSetler[0].Satirlar.Count == 0)
            {
                Ozet = "Bu grubun ham kaydı gelmedi (kapsam dışında olabilir).";
                return (null, "", 0, null);
            }
            Ozet = $"“{Kisalt(g.OrnekMesaj)}” — {r.ResultSetler[0].Satirlar.Count:N0} ham kayıt açıldı.";
            return (r.ResultSetler[0], Kisalt(g.OrnekMesaj), g.Sayi, _zamanKolon);
        }
        catch (OperationCanceledException)
        {
            if (nesil == _kosuNesli)
                Ozet = "Detay durduruldu.";
            return (null, "", 0, null);
        }
        finally
        {
            if (nesil == _kosuNesli)
                IslemBitti();
        }
    }

    /// <summary>Pano/CSV için satır metni (Karşılaştırma sekmesindeki gibi pencere I/O'yu yapar).
    /// Ek kolonlar (v22-S12) başlıklarıyla birlikte sona eklenir — grid'de ne varsa dışarı o çıkar.</summary>
    public string SatirMetni(char ayrac, bool csv = false)
    {
        var sb = new System.Text.StringBuilder();
        string[] baslik = ["Kez", "Ilk gorulme", "Son gorulme", "Yeni", "Ornek mesaj", .. EkKolonBasliklari];
        sb.AppendLine(string.Join(ayrac, csv ? baslik.Select(h => Alan(h, ayrac, csv)) : baslik));
        foreach (LogGrupGorunum g in Gruplar)
        {
            List<string> hucreler =
            [
                g.Sayi.ToString(),
                g.IlkGorulme?.ToString("dd.MM.yyyy HH:mm") ?? "",
                g.SonGorulme?.ToString("dd.MM.yyyy HH:mm") ?? "",
                g.Yeni ? "YENI" : "",
                TekSatirMetin(g.OrnekMesaj),
            ];
            for (int i = 0; i < EkKolonBasliklari.Count; i++)
                hucreler.Add(TekSatirMetin(i < g.EkDegerler.Count ? g.EkDegerler[i] : ""));
            sb.AppendLine(string.Join(ayrac, hucreler.Select(h => Alan(h, ayrac, csv))));
        }
        return sb.ToString();
    }

    public bool SonucVar => Gruplar.Count > 0;

    public void DurumBildir(string mesaj) => Ozet = mesaj;

    private static string Alan(string s, char ayrac, bool csv)
    {
        if (!csv)
            return s;
        return s.Contains(ayrac) || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    private static string TekSatirMetin(string s)
        => s.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ").Replace("\r", " ⏎ ").Replace('\t', ' ');

    private void KaydetSecim() => _secimKaydet?.Invoke(_veritabani, SecilenTablo, SecilenKolon);

    private static int KolonIndeksi(IReadOnlyList<KolonBilgisi> kolonlar, string ad)
    {
        for (int i = 0; i < kolonlar.Count; i++)
            if (string.Equals(kolonlar[i].Ad, ad, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static int KonvInt(object? d)
    {
        try { return d is null or DBNull ? 0 : (int)Math.Min(int.MaxValue, Convert.ToInt64(d)); }
        catch { return 0; }
    }

    private static string Kisalt(string s) => s.Length <= 90 ? s : s[..90] + "…";

    [RelayCommand]
    private async Task AiOzetleAsync()
    {
        if (_asistanSor is null)
            return;
        if (Gruplar.Count == 0)
        {
            Ozet = "Özetlenecek grup yok — önce Analiz et.";
            return;
        }
        AiOzetAcik = true;
        AiOzet = "🤖 Model düşünüyor…";
        string metin = string.Join("\n", Gruplar.Take(25).Select(g => $"{g.Sayi}× {g.OrnekMesaj}"));
        AsistanCevabi cevap = await _asistanSor(AsistanIstemleri.LogOzetle(metin));
        AiOzet = cevap.Metin;
    }

    // ── ISekme ─────────────────────────────────────────────────────────────────
    public string Baslik => $"🧾 Log analizi · {SecilenVeritabani}";

    public SekmeDurumu Durum => AnalizCalisiyor ? SekmeDurumu.Calisiyor : SekmeDurumu.Tamamlandi;

    public Task KapatAsync()
    {
        IptalYardimcisi.ArkaPlandaIptal(_analizCts, birak: true); // m.15: Cancel UI'da bloklayabilir
        _analizCts = null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Ek kolon açılır listesi öğesi (v22-S12) — Veri Arama'nın kapsam deseni
/// (<see cref="VeriAramaKapsamOgesi"/>): işaretlenince VM düğme metnini (EkKolonOzeti) tazeler.
/// </summary>
public sealed partial class LogEkKolonOgesi : ObservableObject
{
    private readonly Action _degisti;

    public LogEkKolonOgesi(string ad, Action degisti)
    {
        Ad = ad;
        _degisti = degisti;
    }

    public string Ad { get; }

    [ObservableProperty] private bool _secili;

    partial void OnSeciliChanged(bool value) => _degisti();
}
