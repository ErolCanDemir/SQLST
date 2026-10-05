using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Edit modu sekmesi (V2-S5, FG-4.9): tek tabloya bağlı düzenlenebilir grid.
/// Değişiklik takibi DataTable satır durumlarıyla (Added/Modified/Deleted + Original);
/// uygulamadan önce üretilen DML önizlenir ("Show Script"), PK yoksa salt-okunur
/// (07-r2 §4). Uygulama tek transaction'dadır; 0 satırlık komut = çakışma → tümü geri.
/// </summary>
public partial class DuzenlemeSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    /// <summary>
    /// v22-S4 saha turu-4 Edit m.1 (kullanıcı: "satır sınırı var, 178 gösteriyor — en az 1000
    /// olmalı"): 200 → 1000. 178'in asıl nedeni satır sınırı bile değildi: geniş satırlı tabloda
    /// 96 MB'lık BAYT bütçesi 200'e varmadan kesiyordu ve kesildiği HİÇBİR YERDE yazılmıyordu —
    /// kullanıcı 178'i sınır sanıyordu. Bütçe de yükseltildi; kesilme nedeni artık bilgi satırında.
    /// </summary>
    public const int SatirLimiti = 1_000;

    /// <summary>Edit yüklemesinin bayt bütçesi: tek tablo + bilinçli kullanım; varsayılan 96 MB
    /// geniş satırda SatirLimiti'ne varmadan kesiyordu (178 ≈ 96 MB / ~550 KB satır).</summary>
    public const long BellekSiniriBayt = 256L * 1024 * 1024;

    private readonly ISchemaService _schemaService;
    private readonly QueryService _queryService;
    private readonly IOturumFabrikasi _oturumFabrikasi;
    private readonly ILehce _lehce;
    private readonly Func<ConnectionProfile?> _profilGetir;
    private readonly SemaNesnesi _tablo;
    /// <summary>DML'in gideceği GERÇEK tablo: sekme içi sorgu tek-tablo SELECT ise onun tablosu,
    /// yoksa açılış tablosu (_tablo). Kullanıcı bulgusu 2026-07-27: sorgu başka tabloyu hedeflerken
    /// UPDATE _tablo'ya gidip yanlış/0 satır ediyordu.</summary>
    private SemaNesnesi _etkinTablo;
    private IDbOturum? _oturum;
    private DataTable? _veri;

    public DuzenlemeSekmesiViewModel(
        ISchemaService schemaService,
        QueryService queryService,
        IOturumFabrikasi oturumFabrikasi,
        ILehce lehce,
        Func<ConnectionProfile?> profilGetir,
        SemaNesnesi tablo)
    {
        _schemaService = schemaService;
        _queryService = queryService;
        _oturumFabrikasi = oturumFabrikasi;
        _lehce = lehce;
        _profilGetir = profilGetir;
        _tablo = tablo;
        _etkinTablo = tablo;
        FiltreBelgesi = new ICSharpCode.AvalonEdit.Document.TextDocument();
        // Editörden yazım → ana kaynağı tazele (TextChanged belge sahibinin thread'inde tetiklenir).
        FiltreBelgesi.TextChanged += (_, _) => _filtreMetni = FiltreBelgesi.Text;
    }

    /// <summary>Kullanıcı kuralı (2026-07-26): TÜM editör sekmeleri SQLST-N — Edit de. ✏ türü,
    /// SQLST{N} adı gösterir; hangi tablo olduğu bilgi bandında zaten yazar. Açan verir.</summary>
    public string Baslik { get; init; } = "✏ Edit";

    /// <summary>View bağlar: DML önizlemesini gösterip Uygula/Vazgeç sorar (roadmap "Show Script").</summary>
    public Func<string, bool>? ScriptOnayiIste { get; set; }

    public DuzenlemeMetasi? Meta { get; private set; }

    /// <summary>Editör tamamlaması için sekmenin veritabanı (Edit m.2 — şema önbelleği bununla çözülür).</summary>
    public string? Veritabani => _etkinTablo.Veritabani;

    [ObservableProperty] private SekmeDurumu _durum = SekmeDurumu.Bosta;
    [ObservableProperty] private DataView? _gorunum;
    [ObservableProperty] private string _bilgi = "";
    /// <summary>PK yok → 07-r2 §4: satır kimliği belirsiz, grid salt-okunur.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Ekleyebilir))] private bool _salt;
    [ObservableProperty] private int _degisiklikSayisi;

    /// <summary>
    /// Sekme İÇİ sorgu (kullanıcı düzeltmesi 2026-07-26: "yeni pencerede değil — sorguyu aynı
    /// pencerede yazıp SONUCUNU düzenlemek istiyorum"; ayrı Sorgu penceresini v6a bu yüzden
    /// emekli etti). Boşsa tablonun ilk 200 satırı yüklenir; doluysa BU SORGUNUN sonucu gride
    /// gelir ve düzenlenir. Düzenleme için sonuçta tablonun TÜM PK kolonları olmalıdır —
    /// yoksa grid salt-okunur kalır ve neden söylenir (UPDATE kimliği PK'dan kurulur).
    /// </summary>
    /// <summary>
    /// Sekme içi sorgunun AvalonEdit belgesi (v22-S4 Edit m.2 — kullanıcı: "Edit modda da sorgu
    /// editöründeki gibi öneriler olsun, normal textbox yerine"). Düz TextBox, gerçek editöre
    /// (renklendirme + tamamlama) çevrildi.
    ///
    /// ⚠ İPLİK MODELİ (ilk deneme testte patladı — "TextDocument can be accessed only from the
    /// thread that owns it"): TextDocument kuran/ilk erişen thread'e KİLİTLENİR ve
    /// <c>SetOwnerThread(null)</c> bunu çözmez (null yalnız "sıradaki erişen sahiplensin" demek).
    /// Bu yüzden ANA KAYNAK düz bir string alandır (<see cref="FiltreSql"/> — her thread'den
    /// güvenli); belge yalnız UI köprüsüdür: editörde yazılınca <c>TextChanged</c> (UI thread'inde
    /// tetiklenir) alanı tazeler; koddan/testten set edilince belgeye ancak sahibi buysa yazılır.
    /// </summary>
    public ICSharpCode.AvalonEdit.Document.TextDocument FiltreBelgesi { get; }

    private volatile string _filtreMetni = "";

    /// <summary>Sorgu metni — mevcut çağıranlar ve testler için API değişmedi.</summary>
    public string FiltreSql
    {
        get => _filtreMetni;
        set
        {
            string yeni = value ?? "";
            if (_filtreMetni == yeni)
                return;
            _filtreMetni = yeni;
            BelgeyeYansit(yeni);
        }
    }

    /// <summary>
    /// Belgeye yalnız SAHİBİ olduğumuz thread'den yazılır; değilsek SESSİZCE geçilir.
    ///
    /// Neden dispatcher fallback'i YOK: ilk sürüm "sahip değilsek UI dispatcher'ına gönder"
    /// diyordu — ama belge sahibi her zaman UI thread'i değil (testlerde VM'yi kuran thread).
    /// Dispatcher'a atılan yazım orada da fırlayıp STA dispatcher'ını kirletiyor, KOMŞU pencere
    /// testlerini düşürüyordu (tam süit 3 kırmızı, izole yeşil — klasik kirlilik izi). Gerçek
    /// kullanımda VM UI thread'inde kurulur, setter da UI'dan çağrılır → bu dal hiç çalışmaz;
    /// testlerde ise ana kaynak zaten <see cref="_filtreMetni"/>, belgeye yansıma gerekmez.
    /// </summary>
    private void BelgeyeYansit(string metin)
    {
        try
        {
            if (FiltreBelgesi.Text != metin)
                FiltreBelgesi.Text = metin;
        }
        catch (InvalidOperationException)
        {
            // sahibi değiliz — ana kaynak (_filtreMetni) günceldir, belge UI'da senkronlanır
        }
    }

    /// <summary>Sekme içi sorguyu çalıştırır: bekleyen düzenlemeler atılır, grid sorgu sonucuyla kurulur.</summary>
    [RelayCommand]
    public Task FiltreCalistir() => YenidenYukleAsync();

    public bool Ekleyebilir => !Salt;

    public bool CalisiyorMu => Durum == SekmeDurumu.Calisiyor;

    // SerbestCalistirAsync (v6a "sorgu penceresi" köprüsü) EMEKLİ — 2026-07-26 kullanıcı
    // düzeltmesiyle yerini sekme içi FiltreSql aldı (sorgu + sonucu düzenleme tek yerde).

    // --- Toplu işlemler (V5-S5) ---

    /// <summary>
    /// Gridde seçili hücreleri (satır indeksi, kolon adı) verir — görünüm bağlar.
    /// Seçim WPF DataGrid'in işidir, VM onu kendi başına bilemez.
    /// </summary>
    public Func<IReadOnlyList<(int Satir, string Kolon)>>? SeciliHucreler { get; set; }

    /// <summary>Görünüm bağlar: yapıştırmanın başlayacağı hücre (satır indeksi, kolon indeksi).</summary>
    public Func<(int Satir, int Kolon)?>? BaslangicHucresi { get; set; }

    /// <summary>Toplu güncelleme için kullanıcıdan kolon + değer ister (görünüm penceresi açar).</summary>
    public Func<IReadOnlyList<string>, (string Kolon, string? Deger)?>? TopluGuncellemeIste { get; set; }

    /// <summary>
    /// Seçili hücrelere NULL yazar (Ctrl+0 — SSMS ile aynı kısayol). Değişiklik yalnız
    /// bekleyen kümeye girer; sunucuya ancak Uygula + Show Script onayından sonra gider.
    /// </summary>
    [RelayCommand]
    public void NullAta()
    {
        if (_veri is null || Meta is null || Salt)
            return;

        IReadOnlyList<(int, string)> hucreler = SeciliHucreler?.Invoke() ?? [];
        if (hucreler.Count == 0)
        {
            Bilgi = "Önce hücre seçin (Ctrl+0 seçili hücrelere NULL yazar).";
            return;
        }

        Bildir(GridTopluIslem.NullAta(_veri, Meta, hucreler));
    }

    /// <summary>Panodaki sekmeyle ayrılmış bloğu seçili hücreden başlayarak yapıştırır.</summary>
    public void Yapistir(string panoMetni)
    {
        if (_veri is null || Meta is null || Salt)
            return;

        if (BaslangicHucresi?.Invoke() is not { } baslangic)
        {
            Bilgi = "Önce yapıştırmanın başlayacağı hücreyi seçin.";
            return;
        }

        Bildir(GridTopluIslem.Yapistir(
            _veri, Meta, panoMetni, baslangic.Satir, baslangic.Kolon, Ekleyebilir));
    }

    /// <summary>
    /// Bir kolonun YÜKLÜ tüm satırlarına aynı değeri yazar. Kapsam sınırı kullanıcıya
    /// açıkça söylenir: bu, tablonun tamamını değil ekrandaki satırları günceller.
    /// </summary>
    [RelayCommand]
    public void TopluGuncelle()
    {
        if (_veri is null || Meta is null || Salt)
            return;

        IReadOnlyList<string> yazilabilirler =
            [.. Meta.Kolonlar.Where(k => k.Yazilabilir).Select(k => k.Ad)];

        if (TopluGuncellemeIste?.Invoke(yazilabilirler) is not { } istek)
            return;

        TopluSonuc sonuc = GridTopluIslem.KolonaAta(_veri, Meta, istek.Kolon, istek.Deger);
        Bildir(sonuc);

        if (sonuc.Uygulanan > 0)
        {
            Bilgi = $"{sonuc.Uygulanan} satırda '{istek.Kolon}' değişti — YALNIZ ekrandaki "
                  + $"(yüklü) satırlar. Uygula'ya basmadan sunucuya gitmez.";
        }
    }

    /// <summary>Toplu işlem sonucunu sayaca ve bilgi satırına yansıtır.</summary>
    private void Bildir(TopluSonuc sonuc)
    {
        DegisiklikleriSay();
        Bilgi = sonuc.Atlananlar.Count == 0
            ? sonuc.Ozet
            : $"{sonuc.Ozet} — {string.Join(" ", sonuc.Atlananlar)}";
    }

    [RelayCommand]
    public async Task YukleAsync()
    {
        ConnectionProfile? profil = _profilGetir();
        if (profil is null || CalisiyorMu)
            return;

        Durum = SekmeDurumu.Calisiyor;
        Bilgi = "Yükleniyor…";
        try
        {
            _oturum ??= _oturumFabrikasi.Olustur(profil);

            // Sekme içi sorgu doluysa ONUN sonucu düzenlenir; boşsa tablonun ilk N satırı.
            bool filtreli = !string.IsNullOrWhiteSpace(FiltreSql);

            // KRİTİK (kullanıcı bulgusu 2026-07-27): DML, sorgunun GERÇEK kaynak tablosuna üretilmeli.
            // Edit tab bir tablo için açılır (_tablo) ama kullanıcı BAŞKA tabloyu sorgulayabilir; o
            // zaman üretilen UPDATE yanlış tabloya gidip 0 satır (çakışma) veriyordu. Tek-tablolu
            // SELECT ise onun tablosunu hedef al; değilse (JOIN/karmaşık) _tablo (zaten düzenlenemez).
            _etkinTablo = _tablo;
            if (filtreli && SqlCozumleyici.TekTabloSelectKaynagi(FiltreSql) is { } kaynak)
                _etkinTablo = new SemaNesnesi(
                    kaynak.Veritabani ?? _tablo.Veritabani, kaynak.Sema, kaynak.Ad,
                    SemaNesneTuru.Tablo, [], []);

            // Meta ETKİN tabloya göre; hedef tablo değiştiyse yeniden yüklenir (sabit cache değil).
            if (Meta is null
                || !Meta.Tablo.Equals(_etkinTablo.Ad, StringComparison.OrdinalIgnoreCase)
                || !Meta.Sema.Equals(_etkinTablo.Sema, StringComparison.OrdinalIgnoreCase))
                Meta = await _schemaService.DuzenlemeMetaAsync(profil, _etkinTablo, CancellationToken.None);

            QueryResult sonuc = await _oturum.CalistirAsync(
                filtreli ? FiltreSql : _lehce.IlkNSatirSorgusu(_tablo, SatirLimiti),
                new ExecuteOptions
                {
                    VeritabaniOverride = _etkinTablo.Veritabani,
                    SatirSiniri = SatirLimiti,
                    BellekSiniriBayt = BellekSiniriBayt, // Edit m.1: 96 MB geniş satırda 178'de kesiyordu
                },
                CancellationToken.None);
            if (!sonuc.Basarili || sonuc.ResultSetler.Count == 0)
            {
                Durum = SekmeDurumu.Hata;
                Bilgi = $"Yüklenemedi: {sonuc.Hata?.Mesaj ?? "beklenmeyen sonuç"}";
                return;
            }

            _veri = TipliTablo(sonuc.ResultSetler[0]);
            _veri.RowChanged += (_, _) => DegisiklikleriSay();
            _veri.RowDeleted += (_, _) => DegisiklikleriSay();
            Gorunum = _veri.DefaultView;

            // Düzenlenebilirlik: PK metası + sonuçta TÜM PK kolonlarının bulunması (UPDATE/DELETE
            // kimliği PK'dan kurulur; filtreli sorgu PK'yı SELECT etmediyse satır kimliği yoktur).
            bool pkSonucta = Meta.PkKolonlari.Count > 0
                && Meta.PkKolonlari.All(pk => _veri.Columns.Contains(pk.Ad));
            Salt = !Meta.DuzenlenebilirMi || !pkSonucta;
            DegisiklikSayisi = 0;
            Durum = SekmeDurumu.Tamamlandi;
            // Edit m.1: kesilme artık SÖYLENİR. Eskiden 96 MB bütçesi 178'de kesiyor ve hiçbir yerde
            // yazmıyordu — kullanıcı 178'i "sınır" sanıp haklı olarak şaşırıyordu.
            string kesikNotu = sonuc switch
            {
                { BellekSiniriAsildi: true } =>
                    $" ⚠ Bellek bütçesinde kesildi (geniş satırlar) — tamamı için sorguya WHERE ekleyin.",
                { SatirSiniriAsildi: true } => $" (sınır {SatirLimiti:N0} — daraltmak için WHERE yazın)",
                _ => "",
            };
            Bilgi = (Meta.DuzenlenebilirMi, pkSonucta, filtreli) switch
            {
                (false, _, _) => $"⚠ {Meta.TamAd}: PK yok — salt-okunur (satır kimliği belirsiz, 07-r2 §4).",
                (true, false, _) => "⚠ Sorgu sonucu SALT-OKUNUR: düzenleyebilmek için sorguya PK kolonlarını "
                    + $"ekleyin ({string.Join(", ", Meta.PkKolonlari.Select(k => k.Ad))}).",
                (true, true, true) => $"Sorgu sonucu — {_veri.Rows.Count:N0} satır.{kesikNotu} "
                    + "Hücreyi düzenle; Uygula DML önizlemesi gösterir.",
                _ => $"{Meta.TamAd} — ilk {_veri.Rows.Count:N0} satır.{kesikNotu} "
                    + "Hücreyi düzenle, satır ekle (alt boş satır) ya da sil (Del).",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Durum = SekmeDurumu.Hata;
            Bilgi = $"Yüklenemedi: {ex.Message}";
        }
    }

    /// <summary>Okuyucunun CLR tipleriyle tipli tablo — grid düzenlemesi kültüre uygun ayrıştırır (tr-TR "3,14").</summary>
    private static DataTable TipliTablo(ResultSetData set)
    {
        var tablo = new DataTable();
        foreach (KolonBilgisi k in set.Kolonlar)
            tablo.Columns.Add(k.Ad, k.ClrTip ?? typeof(object));
        tablo.BeginLoadData();
        foreach (object?[] satir in set.Satirlar)
            tablo.Rows.Add(satir);
        tablo.EndLoadData();
        tablo.AcceptChanges(); // yüklenen veri "değişmemiş" başlar
        return tablo;
    }

    private void DegisiklikleriSay()
    {
        if (_veri is null)
            return;
        int say = 0;
        foreach (DataRow satir in _veri.Rows)
        {
            if (satir.RowState is DataRowState.Added or DataRowState.Deleted or DataRowState.Modified)
                say++;
        }
        DegisiklikSayisi = say;
    }

    /// <summary>
    /// Gridde DÜZENLENMEKTE olan satırın bekleyen (Proposed) değişikliğini KESİNLEŞTİRİR.
    /// Kullanıcı bulgusu 2026-07-27: hücreyi değiştirip Uygula deyince "değişiklik yok" çıkıyordu
    /// ("1 bekleyen değişiklik" görünse de). Kök neden: DataGrid satırı hâlâ BeginEdit'te (Proposed);
    /// DmlUretici DataRowVersion.Current'ı okur ve eski değeri görür. EndEdit, Proposed'ı Current yapar.
    /// </summary>
    private void BekleyenleriIsle()
    {
        if (_veri is null)
            return;
        foreach (DataRow satir in _veri.Rows)
        {
            if (satir.RowState != DataRowState.Deleted && satir.HasVersion(DataRowVersion.Proposed))
                satir.EndEdit();
        }
    }

    [RelayCommand]
    public async Task UygulaAsync()
    {
        if (_veri is null || Meta is null || _oturum is null || CalisiyorMu)
            return;

        BekleyenleriIsle(); // gridde açık kalan düzenlemeyi kesinleştir (yoksa "değişiklik yok" sanılır)

        IReadOnlyList<string> komutlar;
        try
        {
            komutlar = DmlUretici.Uret(_lehce, Meta, _veri);
        }
        catch (NotSupportedException ex)
        {
            Bilgi = $"Script üretilemedi: {ex.Message}";
            return;
        }
        if (komutlar.Count == 0)
        {
            Bilgi = "Uygulanacak değişiklik yok.";
            return;
        }

        // Roadmap "Show Script": üretilecek DML uygulanmadan ÖNCE gösterilir
        if (ScriptOnayiIste is { } onayla && !onayla(DmlUretici.Onizle(komutlar)))
        {
            Bilgi = "Uygulama vazgeçildi — değişiklikler grid'de bekliyor.";
            return;
        }

        Durum = SekmeDurumu.Calisiyor;
        Bilgi = "Uygulanıyor…";
        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            _queryService, _lehce, _oturum, komutlar,
            new ExecuteOptions { VeritabaniOverride = _etkinTablo.Veritabani }, CancellationToken.None);

        if (basarili)
        {
            Durum = SekmeDurumu.Tamamlandi;
            await YenidenYukleAsync(); // identity/rowversion/DEFAULT değerleri tazelensin
            Bilgi = mesaj;
        }
        else
        {
            Durum = SekmeDurumu.Hata;
            Bilgi = mesaj; // düzenlemeler grid'de durur — kullanıcı düzeltip yeniden dener
        }
    }

    [RelayCommand]
    public void Vazgec()
    {
        _veri?.RejectChanges();
        DegisiklikleriSay();
        Bilgi = "Değişiklikler atıldı — grid yüklendiği hâline döndü.";
    }

    [RelayCommand]
    public Task YenileAsync() => YenidenYukleAsync();

    private Task YenidenYukleAsync()
    {
        Durum = SekmeDurumu.Bosta; // Calisiyor kilidini bırak
        return YukleAsync();
    }

    public async Task KapatAsync()
    {
        // v19-S11: dispose (bağlantı kapatma) ağ turu atabilir — UI iş parçacığında bekletme.
        IDbOturum? oturum = _oturum;
        _oturum = null;
        if (oturum is not null)
            await Task.Run(() => oturum.DisposeAsync().AsTask());
    }
}
