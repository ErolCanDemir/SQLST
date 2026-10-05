using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Görsel Sorgu Tasarımcısı sekmesi (v6). Ağaçtan tablo sürüklenip tuvale bırakılır; kutular
/// birbirine BAĞLANIR (JOIN) ve "Script'e aç" tuvaldeki tablolar + bağlardan <c>SELECT</c>
/// üretip yeni bir sorgu sekmesinde açar.
///
/// <b>Çalıştırma (revizyon 2026-07-23):</b> ▶ Çalıştır artık script sekmesi AÇMAZ — üretilen
/// SELECT stateless yürütücüyle koşulur, sonuç BU SEKMENİN altındaki grid'de görünür (kullanıcı
/// isteği: "scripti açmasın, direkt sonuç gelsin"). Üretim yalnız SELECT olduğundan yazma
/// rayları gerekmez; satır + bellek sınırları yürütücüde aynen geçerli. Kalıcı bağlantı/oturum
/// yine yok; düzenlemek isteyene "Script'e aç" duruyor.
///
/// <b>S1:</b> tablo yerleştirme + <c>SELECT * FROM …</c>. <b>S2:</b> JOIN (FK otomatik önerisi
/// + tip/kolon seçimi). WHERE S3. MongoDB'de bu sekme HİÇ açılmaz — kapı <see cref="MainViewModel"/>'de.
/// </summary>
public sealed partial class GorselSorguSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    /// <summary>Aktif profilin lehçesini verir; profil yoksa/Mongo ise null (tırnaklama motora bağlı).</summary>
    private readonly Func<ILehce?> _lehceGetir;

    /// <summary>Bir veritabanının FK bağlarını getirir (bağ kurarken ON önerisi için). Null olabilir.</summary>
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<YabanciAnahtar>>>? _fkGetir;

    /// <summary>FK listesi veritabanı başına önbelleğe alınır (her bağda yeniden sorulmasın).</summary>
    private readonly Dictionary<string, IReadOnlyList<YabanciAnahtar>> _fkOnbellek = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 🎯 Görsel sorgunun ÇALIŞACAĞI veritabanı (kullanıcı bulgusu 2026-07-31): eskiden profilin
    /// varsayılan (ilk) veritabanında koşuyordu — kullanıcı başka DB'de çalışırken "Invalid object
    /// name" alıyor, hangi DB'de olduğunu göremiyordu. Editördeki gibi seçilebilir; açılışta aktif
    /// sekmenin DB'si, tablo sürüklenince o tablonun DB'si gelir.
    /// </summary>
    [ObservableProperty] private string? _secilenVeritabani;

    /// <summary>DB seçicisinin listesi (MainViewModel doldurur — sunucudaki veritabanları).</summary>
    [ObservableProperty] private IReadOnlyList<string> _veritabanlari = [];

    public GorselSorguSekmesiViewModel(
        Func<ILehce?> lehceGetir,
        Func<string, CancellationToken, Task<IReadOnlyList<YabanciAnahtar>>>? fkGetir = null)
    {
        _lehceGetir = lehceGetir;
        _fkGetir = fkGetir;
    }

    public string Baslik => "🎨 Görsel Sorgu";

    // Çalışan bir işi yok: her zaman tamamlanmış görünür (durum çubuğu/ikon tutarlılığı için).
    public SekmeDurumu Durum => SekmeDurumu.Tamamlandi;

    /// <summary>Tuvaldeki tablo kutuları.</summary>
    public ObservableCollection<GorselSorguKutusu> Kutular { get; } = [];

    /// <summary>Kutular arası bağlar (her biri bir JOIN).</summary>
    public ObservableCollection<GorselBaglanti> Baglantilar { get; } = [];

    /// <summary>Üretilen script'i yeni sorgu sekmesinde açan köprü — MainWindow/MainViewModel bağlar.</summary>
    public Action<string, string>? SekmeyeAc { get; set; }

    /// <summary>Üretilen script'i yeni sekmede açıp HEMEN çalıştıran köprü (F5) — MainViewModel bağlar.
    /// 2026-07-23'ten beri yalnız YEDEK yol: <see cref="SorguCalistirici"/> bağlıysa kullanılmaz.</summary>
    public Action<string, string>? SekmeyeAcVeCalistir { get; set; }

    /// <summary>Üretilen SELECT'i çalıştırıp sonucu döndüren köprü (2026-07-23 "yerinde sonuç") —
    /// MainViewModel stateless yürütücüyle bağlar; satır + bellek sınırları orada geçerlidir.</summary>
    public Func<string, CancellationToken, Task<QueryResult>>? SorguCalistirici { get; set; }

    /// <summary>Yerinde sonuç grid'inin verisi; null = sonuç bölmesi gizli.</summary>
    [ObservableProperty] private System.Data.DataView? _sonucGorunum;
    /// <summary>Sonuç bölmesi başlık bandı: satır sayısı + süre ya da hata; null = gizli.</summary>
    [ObservableProperty] private string? _sonucBilgi;

    [ObservableProperty] private string _bilgi =
        "Soldaki ağaçtan bir tabloyu sürükleyip buraya bırakın; kutuları başlıktaki ⚬'dan sürükleyerek bağlayın.";

    /// <summary>Script üretilebilir mi (düğme kapısı): en az bir tablo gerekir.</summary>
    public bool ScriptUretilebilir => Kutular.Count > 0;

    // ---- WHERE filtre çipleri (v11-öncesi #2, kullanıcı onayı 2026-07-25) ----
    // Model kutuların Kosullar koleksiyonu OLARAK KALDI (SQL üretici değişmedi); çip şeridi
    // yalnız düzleştirilmiş görünümdür. Kolon satırındaki huni → popover → KosulEkle buraya düşer.

    /// <summary>Tuvalin üstündeki filtre çipi şeridi (tüm kutuların DOLU koşulları, sırayla).</summary>
    public ObservableCollection<GorselFiltreCipi> Cipler { get; } = [];

    /// <summary>Şerit görünür mü (hiç çip yoksa şerit tamamen gizli).</summary>
    public bool CiplerVar => Cipler.Count > 0;

    /// <summary>Çip şeridini kutulardan yeniden kurar — koşul ekleme/silme/bağlaç sonrası çağrılır.</summary>
    public void CipleriTazele()
    {
        Cipler.Clear();
        bool ilk = true;
        foreach (GorselSorguKutusu kutu in Kutular)
        {
            foreach (GorselKosulGorunumu kosul in kutu.Kosullar.Where(k => k.Dolu))
            {
                Cipler.Add(new GorselFiltreCipi(this, kutu, kosul, ilk));
                ilk = false;
            }
        }
        OnPropertyChanged(nameof(CiplerVar));
    }

    /// <summary>Huni popover'ından yeni koşul: kutunun koleksiyonuna eklenir, rozet + şerit tazelenir.</summary>
    public void KosulEkle(GorselSorguKutusu kutu, string kolon, KosulOperatoru op, string deger)
    {
        kutu.Kosullar.Add(new GorselKosulGorunumu { Kolon = kolon, Operator = op, Deger = deger });
        kutu.KosullariTazele();
        CipleriTazele();
        Bilgi = $"Koşul eklendi: {kutu.Nesne.TamAd}.{kolon}";
    }

    /// <summary>Çipin ✕'i: koşul kutusundan kaldırılır.</summary>
    public void CipSil(GorselFiltreCipi cip)
    {
        cip.Kutu.Kosullar.Remove(cip.Kosul);
        cip.Kutu.KosullariTazele();
        CipleriTazele();
    }

    /// <summary>Çipin bağlaç anahtarı: bir öncekiyle VE ↔ VEYA.</summary>
    public void CipBaglacDegistir(GorselFiltreCipi cip)
    {
        cip.Kosul.VeyaMi = !cip.Kosul.VeyaMi;
        CipleriTazele();
    }

    /// <summary>
    /// Ağaçtan bırakılan tabloyu <paramref name="x"/>/<paramref name="y"/> konumuna ekler.
    /// Aynı tablo İKİ KEZ eklenmez (takma ad yok; <c>FROM a CROSS JOIN a</c> anlamsız olurdu —
    /// self-join S2+ konusudur). Tekrar bırakılırsa sessizce yok sayılır, bilgi verilir.
    /// </summary>
    public void TabloEkle(SemaNesnesi nesne, double x, double y)
    {
        if (Kutular.Any(k => k.Nesne.TamAd == nesne.TamAd))
        {
            Bilgi = $"{nesne.TamAd} zaten tuvalde.";
            return;
        }

        Kutular.Add(new GorselSorguKutusu(nesne, x, y));
        OnPropertyChanged(nameof(ScriptUretilebilir));
        Bilgi = $"{Kutular.Count} tablo. Kutuları bağlayın (JOIN) ya da \"Script'e aç\".";

        // Sürüklenen tablonun VERİTABANI seçime yansır (kullanıcı bulgusu 2026-07-31: görsel sorgu
        // profilin İLK veritabanında koşup "Invalid object name" veriyordu — kullanıcı hangi DB'de
        // olduğunu göremiyordu). Tabloyu hangi DB'den sürüklediyse sorgu o DB'de koşar.
        if (!string.IsNullOrEmpty(nesne.Veritabani))
            SecilenVeritabani = nesne.Veritabani;

        // FK listesini ŞİMDİDEN arka planda oku (önbelleğe al). Böylece kullanıcı tabloları
        // bağladığında JOIN düzenleyici açılırken FK için DB round-trip'ini BEKLEMEZ — pencerenin
        // "açılırken kasıması"nın (kullanıcı bulgusu 2026-07-20) kök nedeni buydu: ilk bağlamada
        // FK sorgusu tam pencereyle aynı anda koşuyordu.
        FkOnIsit(nesne.Veritabani);
    }

    /// <summary>FK listesini arka planda önden okuyup önbelleğe alır (sessiz — hata bağlamada ele alınır).</summary>
    private async void FkOnIsit(string veritabani)
    {
        try { await FklerAsync(veritabani); }
        catch { /* önden ısıtma; gerçek hata/yetki durumu bağlama anında zaten yönetiliyor */ }
    }

    /// <summary>Bir kutuyu ve ona bağlı TÜM bağları tuvalden kaldırır.</summary>
    public void KutuSil(GorselSorguKutusu kutu)
    {
        foreach (GorselBaglanti bagli in Baglantilar.Where(b => b.Sol == kutu || b.Sag == kutu).ToList())
            Baglantilar.Remove(bagli);

        Kutular.Remove(kutu);
        OnPropertyChanged(nameof(ScriptUretilebilir));
        CipleriTazele(); // kutunun koşulları şeritten de düşsün
        Bilgi = ScriptUretilebilir ? $"{Kutular.Count} tablo." : "Tuval boş. Ağaçtan tablo sürükleyin.";
    }

    /// <summary>Bir bağı kaldırır (çizgi düzenleyicisindeki "sil").</summary>
    public void BaglantiSil(GorselBaglanti bag) => Baglantilar.Remove(bag);

    /// <summary>
    /// İki kutuyu bağlar (yeni JOIN). Kendine bağ ve aynı çift arasında ikinci bağ engellenir.
    /// FK varsa ON kolonları OTOMATİK önerilir (elle düzeltilebilir); yoksa boş bırakılır ve
    /// kullanıcı düzenleyicide seçer. FK okuma başarısız olursa bağ yine kurulur (öneri olmadan).
    /// </summary>
    public async Task<GorselBaglanti?> BaglaAsync(GorselSorguKutusu sol, GorselSorguKutusu sag)
    {
        if (sol == sag)
            return null;
        if (Baglantilar.Any(b => (b.Sol == sol && b.Sag == sag) || (b.Sol == sag && b.Sag == sol)))
        {
            Bilgi = "Bu iki tablo zaten bağlı.";
            return null;
        }

        var bag = new GorselBaglanti(sol, sag);
        await FkOnerAsync(bag);
        Baglantilar.Add(bag);
        Bilgi = bag.Kolonlar.Any(k => k.Dolu)
            ? $"{sol.Nesne.Ad} ↔ {sag.Nesne.Ad} bağlandı (FK'den önerildi). Çizgiye çift tıklayıp düzenleyin."
            : $"{sol.Nesne.Ad} ↔ {sag.Nesne.Ad} bağlandı. Çizgiye çift tıklayıp ON kolonlarını seçin.";
        return bag;
    }

    /// <summary>FK varsa bağın ON kolonlarını doldurur; en az bir dolu satır garanti (boşsa 1 boş satır).</summary>
    private async Task FkOnerAsync(GorselBaglanti bag)
    {
        try
        {
            IReadOnlyList<YabanciAnahtar> fkler = await FklerAsync(bag.Sol.Nesne.Veritabani);
            foreach (YabanciAnahtar fk in fkler)
            {
                if (Eslesme(fk, bag.Sol.Nesne, bag.Sag.Nesne) is { } ciftler)
                {
                    foreach ((string s, string g) in ciftler)
                        bag.Kolonlar.Add(new KolonEsiGorunumu { SolKolon = s, SagKolon = g });
                    bag.OzetiTazele();
                    return;
                }
            }
        }
        catch
        {
            // FK okunamazsa (yetki/eski sunucu) bağ yine kurulur — öneri olmadan, kullanıcı elle girer.
        }

        if (bag.Kolonlar.Count == 0)
            bag.Kolonlar.Add(new KolonEsiGorunumu()); // düzenleyicide boş bir satır hazır dursun
    }

    private async Task<IReadOnlyList<YabanciAnahtar>> FklerAsync(string veritabani)
    {
        if (_fkGetir is null)
            return [];
        if (_fkOnbellek.TryGetValue(veritabani, out IReadOnlyList<YabanciAnahtar>? onbellek))
            return onbellek;

        IReadOnlyList<YabanciAnahtar> fkler = await _fkGetir(veritabani, CancellationToken.None);
        _fkOnbellek[veritabani] = fkler;
        return fkler;
    }

    /// <summary>
    /// Bir FK, sol↔sag kutularıyla eşleşiyorsa (iki yönden biri) kolon çiftlerini
    /// (solKolon, sagKolon) sırasıyla verir; eşleşmiyorsa null.
    /// </summary>
    private static IReadOnlyList<(string Sol, string Sag)>? Eslesme(
        YabanciAnahtar fk, SemaNesnesi sol, SemaNesnesi sag)
    {
        bool ileri = Ayni(fk.KaynakSema, fk.KaynakTablo, sol) && Ayni(fk.HedefSema, fk.HedefTablo, sag);
        bool geri = Ayni(fk.KaynakSema, fk.KaynakTablo, sag) && Ayni(fk.HedefSema, fk.HedefTablo, sol);

        if (ileri) // sol = kaynak (child), sag = hedef (parent)
            return [.. fk.KaynakKolonlar.Zip(fk.HedefKolonlar)];
        if (geri) // sol = hedef (parent), sag = kaynak (child)
            return [.. fk.HedefKolonlar.Zip(fk.KaynakKolonlar)];
        return null;
    }

    private static bool Ayni(string sema, string tablo, SemaNesnesi n)
        => string.Equals(tablo, n.Ad, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(sema) || string.IsNullOrEmpty(n.Sema)
            || string.Equals(sema, n.Sema, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Script → Görsel (v6-S5): ayrıştırılmış bir sorguyu tuvale kurar (tersine yön). Var olan
    /// tuval TEMİZLENİR. <paramref name="tabloBul"/> takma-adlı tabloyu şema önbelleğindeki gerçek
    /// <see cref="SemaNesnesi"/>'ye çözer (bulunamazsa null → o tablo ve ona bağlı join/koşullar
    /// düşer). Join'ler FK'den ÖNERİLMEZ — script'te ne yazıyorsa o kurulur (tür + ON kolonları).
    /// Çözülemeyen/atlanan parçalar <see cref="Bilgi"/>'ye özetlenir.
    /// </summary>
    public void TuvaliKur(CozumlenmisSorgu sorgu, Func<CozumlenmisTablo, SemaNesnesi?> tabloBul)
    {
        Baglantilar.Clear();
        Kutular.Clear();

        // Takma ad → tuvaldeki kutu. Self-join (aynı nesne iki takma) tek kutuya düşer (görsel
        // model kutuları SemaNesnesi ile anar); bu durumda ilgili join sessizce atlanır.
        var kutuByTakma = new Dictionary<string, GorselSorguKutusu>(StringComparer.OrdinalIgnoreCase);
        var atlananTablo = new List<string>();
        var uyarilar = new List<string>(sorgu.Uyarilar);

        double x = 24, y = 24;
        int sutun = 0;
        foreach (CozumlenmisTablo t in sorgu.Tablolar)
        {
            if (tabloBul(t) is not { } nesne)
            {
                atlananTablo.Add(t.Sema is { } s ? $"{s}.{t.Ad}" : t.Ad);
                continue;
            }

            GorselSorguKutusu? mevcut = Kutular.FirstOrDefault(k => k.Nesne.TamAd == nesne.TamAd);
            if (mevcut is null)
            {
                mevcut = new GorselSorguKutusu(nesne, x, y);
                Kutular.Add(mevcut);
                if (++sutun % 3 == 0) { x = 24; y += 210; } else x += 260; // basit 3 sütunlu ızgara
            }
            kutuByTakma[t.Takma] = mevcut;
        }

        foreach (CozumlenmisJoin j in sorgu.Joinler)
        {
            if (!kutuByTakma.TryGetValue(j.SolTakma, out GorselSorguKutusu? sol)
                || !kutuByTakma.TryGetValue(j.SagTakma, out GorselSorguKutusu? sag) || sol == sag)
            {
                uyarilar.Add($"JOIN atlandı ({j.SolTakma} ↔ {j.SagTakma}): tablo şemada yok veya self-join.");
                continue;
            }
            if (Baglantilar.Any(b => (b.Sol == sol && b.Sag == sag) || (b.Sol == sag && b.Sag == sol)))
                continue;

            var bag = new GorselBaglanti(sol, sag) { Tur = j.Tur };
            foreach (GorselKolonEsi e in j.Kolonlar)
                bag.Kolonlar.Add(new KolonEsiGorunumu { SolKolon = e.SolKolon, SagKolon = e.SagKolon });
            if (bag.Kolonlar.Count == 0)
                bag.Kolonlar.Add(new KolonEsiGorunumu()); // ON kolonsuz (ör. çevrilemedi) → boş satır
            bag.OzetiTazele();
            Baglantilar.Add(bag);
        }

        foreach (CozumlenmisKosul k in sorgu.Kosullar)
        {
            if (!kutuByTakma.TryGetValue(k.Takma, out GorselSorguKutusu? kutu))
                continue;
            kutu.Kosullar.Add(new GorselKosulGorunumu
            {
                Kolon = k.Kolon, Operator = k.Operator, Deger = k.Deger, VeyaMi = k.VeyaMi,
            });
            kutu.KosullariTazele();
        }

        foreach (CozumlenmisSecim sec in sorgu.Secimler)
        {
            if (!kutuByTakma.TryGetValue(sec.Takma, out GorselSorguKutusu? kutu))
                continue;
            if (kutu.KolonSecimleri.FirstOrDefault(
                    c => string.Equals(c.Ad, sec.Kolon, StringComparison.OrdinalIgnoreCase)) is { } isaret)
                isaret.Secili = true;
        }

        OnPropertyChanged(nameof(ScriptUretilebilir));
        CipleriTazele(); // geri-ayrıştırılan koşullar şeride de düşsün (#2)

        if (atlananTablo.Count > 0)
            uyarilar.Add($"Şemada bulunamayan tablo(lar) atlandı: {string.Join(", ", atlananTablo)}.");

        Bilgi = Kutular.Count == 0
            ? "Sorgudaki tablolar şema önbelleğinde bulunamadı; tuval kurulamadı."
            : uyarilar.Count == 0
                ? $"Sorgu tuvale alındı: {Kutular.Count} tablo, {Baglantilar.Count} bağ."
                : $"Sorgu tuvale alındı ({Kutular.Count} tablo, {Baglantilar.Count} bağ). "
                  + $"Kısmen: {string.Join(" ", uyarilar)}";
    }

    /// <summary>Tuvali temizler (kutular + bağlar).</summary>
    [RelayCommand]
    private void Temizle()
    {
        Baglantilar.Clear();
        Kutular.Clear();
        OnPropertyChanged(nameof(ScriptUretilebilir));
        CipleriTazele(); // şerit de boşalır (#2)
        SonucGorunum = null;   // yerinde sonuç bölmesi de kapanır (2026-07-23)
        SonucBilgi = null;
        Bilgi = "Tuval boş. Ağaçtan tablo sürükleyin.";
    }

    /// <summary>
    /// Tuvaldeki tablolar + bağlar + koşullar + kolon seçimlerinden SELECT üretir. Üretim saf
    /// mantık (<see cref="GorselSorguUretici"/>); üretilemezse <see cref="Bilgi"/>'yi doldurup
    /// null döner (profil yok / tuval boş). <see cref="ScripteAc"/> ve <see cref="Calistir"/> ortak
    /// kullanır.
    /// </summary>
    private string? SqlUret()
    {
        if (!ScriptUretilebilir)
            return null;

        if (_lehceGetir() is not { } lehce)
        {
            // Profil kapanmış ya da MongoDB'ye geçilmiş olabilir — sessiz üretmektense söyle.
            Bilgi = "Aktif bir SQL profili yok; script üretilemedi.";
            return null;
        }

        // Tüm kutuların WHERE koşulları toplanır (yalnız dolu olanlar).
        var kosullar = Kutular
            .SelectMany(k => k.Kosullar.Select(ks => ks.Yap(k.Nesne)))
            .OfType<GorselKosul>()
            .ToList();

        // Seçili kolonlar (S4): hiçbiri seçilmezse boş → üretici SELECT * yazar.
        var secilenler = Kutular
            .SelectMany(k => k.KolonSecimleri.Where(c => c.Secili).Select(c => new GorselKolonAlani(k.Nesne, c.Ad)))
            .ToList();

        return GorselSorguUretici.Uret(
            lehce, [.. Kutular.Select(k => k.Nesne)],
            [.. Baglantilar.Select(b => b.JoinYap())], kosullar, secilenler);
    }

    /// <summary>Üretilen SELECT'i yeni bir sorgu sekmesinde açar (çalıştırmadan).</summary>
    [RelayCommand]
    private void ScripteAc()
    {
        if (SqlUret() is { } sql)
            SekmeyeAc?.Invoke("görsel-sorgu.sql", sql);
    }

    /// <summary>Tuvalden üretilen SELECT'i döndürür (Ctrl+S ile dosyaya kaydetmek için); üretilemezse null.</summary>
    public string? ScriptiUret() => SqlUret();

    /// <summary>
    /// Üretilen SELECT'i çalıştırır ve sonucu BU SEKMEDE, tuvalin altındaki grid'de gösterir
    /// (kullanıcı isteği 2026-07-23: "scripti açmasın, direkt sonuç gelsin" — 2026-07-20'deki
    /// "yeni sekmede çalıştır" davranışının yerini aldı). DataTable kurulumu arka planda
    /// (UI donması dersi); köprü bağlanmamışsa (test/eski yol) sekme açma yoluna düşer.
    /// </summary>
    [RelayCommand]
    private async Task CalistirAsync()
    {
        if (SqlUret() is not { } sql)
            return;
        if (SorguCalistirici is null)
        {
            SekmeyeAcVeCalistir?.Invoke("görsel-sorgu.sql", sql);
            return;
        }

        SonucBilgi = "⏳ Çalışıyor…";
        try
        {
            QueryResult sonuc = await SorguCalistirici(sql, CancellationToken.None);
            if (sonuc.Hata is { } hata)
            {
                SonucGorunum = null;
                SonucBilgi = $"Hata: {hata.Mesaj}";
                return;
            }
            if (sonuc.ResultSetler.Count == 0)
            {
                SonucGorunum = null;
                SonucBilgi = "Sorgu sonuç döndürmedi.";
                return;
            }

            SonucSeti set = await Task.Run(() => SonucBicimleyici.TabloyaCevir(sonuc.ResultSetler[0]));
            SonucGorunum = set.Tablo.DefaultView;
            SonucBilgi = $"{set.SatirSayisi:N0} satır · {SonucBicimleyici.SureFormatla(sonuc.Sure)}"
                       + (sonuc.SatirSiniriAsildi ? " · sınıra takıldı (ilk kısım)" : "");
        }
        catch (Exception ex)
        {
            // async void KOMUT sınırı (AGENTS §4.6 izinli bölge): buradan kaçan istisna
            // AsyncRelayCommand'in async void Execute'unda kaybolur, "⏳ Çalışıyor…" asılı
            // kalır (inceleme bulgusu 2026-07-23) — her türü kullanıcıya söylenen hataya çevir.
            SonucGorunum = null;
            SonucBilgi = $"Hata: {ex.Message}";
        }
    }

    public Task KapatAsync()
    {
        SonucGorunum = null; // bellek: alt grid'in DataTable'ı kapanışta bırakılır (inceleme 2026-07-30)
        return Task.CompletedTask;
    }
}
