using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;

namespace SQLST.App.ViewModels;

/// <summary>
/// Veritabanı Haritası sekmesi (v9-S2): şemayı ER-diyagramı olarak resmeder — tablolar kart, FK'ler
/// yumuşak bağ. SAF model/yerleşim (<see cref="HaritaKurucu"/>/<see cref="HaritaYerlesim"/>) UI'ya
/// bağlanır. <b>Hiçbir şey çalıştırmaz</b> — salt keşif/belgeleme. Tek örnek tutulur; her açılışta
/// güncel şemayla yeniden kurulur (konum kalıcılığı S3'te).
/// </summary>
public sealed partial class HaritaSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    /// <summary>Bu sayıdan çok tablo varsa açılış MAHALLE görünümüdür (Odak+Bağlam, #1 2026-07-25).</summary>
    public const int MahalleEsigi = 30;

    private HaritaModeli? _model;          // TAM model (tüm şema)
    private HaritaModeli? _gorunurModel;   // o an çizilen alt küme (mahalle değilken kullanılır)
    private HaritaKumeHaritasi? _kumeHaritasi;
    private string? _odakMerkezi;          // odak modundaysa merkez tablo (derinlik değişince yeniden kur)

    public string Baslik => VeritabaniAdi.Length == 0 ? "🗺 Harita" : $"🗺 Harita · {VeritabaniAdi}";
    public SekmeDurumu Durum => SekmeDurumu.Tamamlandi;

    /// <summary>
    /// Haritanın resmettiği veritabanı (v19-S10, canlı test 2026-08-04): harita artık AKTİF
    /// sekmenin veritabanıyla açıldığından hangi DB'nin çizildiği sekme başlığında da görünür.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Baslik))]
    private string _veritabaniAdi = "";

    /// <summary>Konumlar değişince (sürükleme/otomatik yerleştir) host'tan kalıcılaştırma ister (v9-S3).</summary>
    public Action? KonumlariKaydetIstendi { get; set; }

    /// <summary>Seçili kartları (TamAd) Görsel Sorgu tuvaline gönderir — host bağlar (v9-S3 köprü).</summary>
    public Action<IReadOnlyList<string>>? GorselleGonder { get; set; }

    /// <summary>Kart düğümler (KATMAN 2 — önde).</summary>
    public ObservableCollection<HaritaDugumGorunumu> Dugumler { get; } = [];

    /// <summary>FK bağları (KATMAN 1 — arkada).</summary>
    public ObservableCollection<HaritaKenarGorunumu> Kenarlar { get; } = [];

    // ---- Odak + Bağlam (v11-öncesi #1, 2026-07-25) ----

    /// <summary>Mahalle görünümü açık mı — küme kartları çizilir, tablo kartları gizlenir.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MahalleDonGorunur))]
    private bool _mahalleModu;

    /// <summary>Mahalle küme kartları.</summary>
    public ObservableCollection<HaritaKumeKartGorunumu> KumeKartlari { get; } = [];

    /// <summary>Kümeler arası toplu bağlar (çizgi + "N FK" rozeti).</summary>
    public ObservableCollection<HaritaKumeBagGorunumu> KumeBaglari { get; } = [];

    /// <summary>Bu şema için mahalle görünümü var mı (eşik aşıldı mı).</summary>
    public bool MahalleVar => _kumeHaritasi is not null;

    /// <summary>"🏘 Mahalle" düğmesi: mahalle varken ve içindeyken değilken görünür.</summary>
    public bool MahalleDonGorunur => MahalleVar && !MahalleModu;

    /// <summary>Odak modunda komşuluk derinliği (1 ya da 2).</summary>
    [ObservableProperty] private int _odakDerinligi = 1;

    /// <summary>Odak modunda mıyız (derinlik düğmesi yalnız o zaman görünür).</summary>
    public bool OdakModunda => _odakMerkezi is not null;

    [ObservableProperty] private string _bilgi = "Şema okunuyor…";

    /// <summary>Yakınlaştırma (Ctrl+tekerlek). 1.0 = %100.</summary>
    [ObservableProperty] private double _olcek = 1.0;

    /// <summary>Arama kutusu — yazınca eşleşen tablolar vurgulanır, gerisi soluklaşır (v9-S3).</summary>
    [ObservableProperty] private string _arama = "";

    partial void OnAramaChanged(string value) => AramaUygula();

    /// <summary>Seçili kart var mı (Görsele gönder düğmesi kapısı) + kaç tane.</summary>
    public bool SecililerVar => Dugumler.Any(d => d.Secili);
    public int SeciliSayisi => Dugumler.Count(d => d.Secili);

    /// <summary>
    /// Modeli alır ve kartları kurar. <paramref name="kayitli"/> varsa o konumlar kullanılır (DB
    /// başına kalıcılık, v9-S3); eksik/yoksa ızgara. Yeniden çağrılırsa tuval sıfırlanır.
    /// </summary>
    public void Yukle(HaritaModeli model, IReadOnlyDictionary<string, Nokta>? kayitli = null, string db = "")
    {
        _model = model;
        _odakMerkezi = null;
        Arama = "";
        VeritabaniAdi = db;

        // Odak+Bağlam (#1): büyük şema mahalle görünümüyle açılır — "ekranda aynı anda ~30 karttan
        // fazlası asla çizilmez" ilkesi. Küçük şemada eski davranış (tüm kartlar) korunur.
        if (model.Dugumler.Count > MahalleEsigi)
        {
            _kumeHaritasi = HaritaKumeleme.Kur(model);
            MahalleKur();
            return;
        }

        _kumeHaritasi = null;
        MahalleModu = false;
        KumeKartlari.Clear();
        KumeBaglari.Clear();
        OnPropertyChanged(nameof(MahalleVar));
        OnPropertyChanged(nameof(MahalleDonGorunur));

        _gorunurModel = model;
        IReadOnlyDictionary<string, Nokta> izgara = HaritaYerlesim.Izgara(model);
        var yer = new Dictionary<string, Nokta>(StringComparer.OrdinalIgnoreCase);
        foreach (HaritaDugumu d in model.Dugumler)
            yer[d.TamAd] = kayitli is not null && kayitli.TryGetValue(d.TamAd, out Nokta? k)
                ? k
                : izgara.TryGetValue(d.TamAd, out Nokta? g) ? g : new Nokta(0, 0);

        Kur(yer);
        OnPropertyChanged(nameof(DisaAktarilabilir));
        Bilgi = model.Dugumler.Count == 0
            ? "Bu veritabanında tablo/görünüm yok."
            : $"{model.Dugumler.Count} tablo · {model.Kenarlar.Count} ilişki. Kartın üstüne gel = ilişkileri aydınlat; başlıktan sürükle; gövdeye tık = seç; çift tık = ODAKLA.";
    }

    /// <summary>Mahalle görünümünü kurar: küme kartları + aralarındaki toplu bağlar.</summary>
    private void MahalleKur()
    {
        if (_kumeHaritasi is null)
            return;
        Dugumler.Clear();
        Kenarlar.Clear();
        KumeKartlari.Clear();
        KumeBaglari.Clear();
        _gorunurModel = null;
        _odakMerkezi = null;

        IReadOnlyList<Nokta> yerler = HaritaKumeleme.KumeYerlesimi(_kumeHaritasi.Kumeler.Count);
        for (int i = 0; i < _kumeHaritasi.Kumeler.Count; i++)
            KumeKartlari.Add(new HaritaKumeKartGorunumu(_kumeHaritasi.Kumeler[i], i, yerler[i].X, yerler[i].Y));
        foreach (HaritaKumeKenari k in _kumeHaritasi.Kenarlar)
            KumeBaglari.Add(new HaritaKumeBagGorunumu(KumeKartlari[k.Kaynak], KumeKartlari[k.Hedef], k.FkSayisi));

        MahalleModu = true;
        Olcek = 1.0;
        OnPropertyChanged(nameof(MahalleVar));
        OnPropertyChanged(nameof(MahalleDonGorunur));
        OnPropertyChanged(nameof(OdakModunda));
        OnPropertyChanged(nameof(DisaAktarilabilir));
        Bilgi = $"{_model!.Dugumler.Count} tablo → {KumeKartlari.Count} küme. Çift tık = kümeyi aç; "
            + "arama kutusuna tablo yazıp Enter = o tabloya ODAKLAN.";
    }

    /// <summary>Mahalleye dön (küme/odak görünümünden çıkış).</summary>
    [RelayCommand]
    private void MahalleyeDon() => MahalleKur();

    /// <summary>Küme kartına çift tık: yalnız o kümenin tabloları çizilir (kuvvet-yönlü yerleşim).</summary>
    [RelayCommand]
    private void KumeAc(HaritaKumeKartGorunumu? kart)
    {
        if (kart is null || _model is null)
            return;

        HaritaModeli alt = AltModel(kart.Kume.Uyeler);
        _gorunurModel = alt;
        _odakMerkezi = null;
        MahalleModu = false;
        Kur(HaritaYerlesim.KuvvetYonlu(alt));
        KompaktUygula();
        Olcek = 1.0;
        OnPropertyChanged(nameof(OdakModunda));
        OnPropertyChanged(nameof(DisaAktarilabilir));
        Bilgi = $"Küme: {kart.Ad} — {alt.Dugumler.Count} tablo, {alt.Kenarlar.Count} ilişki. "
            + "Çift tık = ODAKLA; 🏘 Mahalle = geri dön.";
    }

    /// <summary>Bir tabloya ODAKLAN: merkez + komşuluk (derinlik 1/2) radyal dizilir, gerisi çizilmez.</summary>
    public void TabloOdakla(string tamAd)
    {
        if (_model is null || !_model.Dugumler.Any(d => d.TamAd.Equals(tamAd, StringComparison.OrdinalIgnoreCase)))
            return;

        _odakMerkezi = tamAd;
        IReadOnlyList<string> gorunur = HaritaKumeleme.Komsular(_model, tamAd, OdakDerinligi);
        HaritaModeli alt = AltModel(gorunur);
        _gorunurModel = alt;
        MahalleModu = false;
        Kur(HaritaKumeleme.Radyal(_model, tamAd, gorunur));
        KompaktUygula();
        foreach (HaritaDugumGorunumu d in Dugumler)
            d.Vurgulu = d.TamAd.Equals(tamAd, StringComparison.OrdinalIgnoreCase);
        Olcek = 1.0;
        OnPropertyChanged(nameof(OdakModunda));
        OnPropertyChanged(nameof(DisaAktarilabilir));
        Bilgi = $"Odak: {tamAd} — {alt.Dugumler.Count} tablo (derinlik {OdakDerinligi}). "
            + (MahalleVar ? "🏘 Mahalle = geri dön." : "⤢ Sığdır = tüm harita.");
    }

    /// <summary>Karta çift tık: o tabloya odaklan. (Ad "Odaklan" — hover'ın Odakla'sıyla çakışmasın.)</summary>
    [RelayCommand]
    private void Odaklan(HaritaDugumGorunumu? dugum)
    {
        if (dugum is not null)
            TabloOdakla(dugum.TamAd);
    }

    /// <summary>Odak derinliğini 1↔2 değiştirir; odak açıksa yeniden kurar.</summary>
    [RelayCommand]
    private void DerinlikDegistir()
    {
        OdakDerinligi = OdakDerinligi == 1 ? 2 : 1;
        if (_odakMerkezi is { } merkez)
            TabloOdakla(merkez);
    }

    /// <summary>Arama kutusunda Enter: ilk eşleşen tabloya odaklan (mahalledeyken tek yol).</summary>
    public void AramaOdakla()
    {
        string q = Arama.Trim();
        if (q.Length == 0 || _model is null)
            return;
        if (_model.Dugumler.FirstOrDefault(
                d => d.TamAd.Contains(q, StringComparison.OrdinalIgnoreCase)) is { } bulunan)
            TabloOdakla(bulunan.TamAd);
        else
            Bilgi = $"“{q}” ile eşleşen tablo yok.";
    }

    /// <summary>TAM modelden alt model: yalnız verilen tablolar + aralarındaki kenarlar.</summary>
    private HaritaModeli AltModel(IReadOnlyList<string> uyeler)
    {
        var kume = new HashSet<string>(uyeler, StringComparer.OrdinalIgnoreCase);
        return new HaritaModeli(
            [.. _model!.Dugumler.Where(d => kume.Contains(d.TamAd))],
            [.. _model.Kenarlar.Where(e => kume.Contains(e.KaynakTamAd) && kume.Contains(e.HedefTamAd))]);
    }

    /// <summary>Semantik sadelik: görünür kart sayısı 30'u aşarsa kartlar KOMPAKT (yalnız başlık) çizilir.</summary>
    private void KompaktUygula()
    {
        bool kompakt = Dugumler.Count > MahalleEsigi;
        foreach (HaritaDugumGorunumu d in Dugumler)
            d.Kompakt = kompakt;
        // Kenar uçları MerkezY'ye (yükseklik/2) bağlanır ama kenar yalnız X/Y değişimini dinler
        // (inceleme 2026-07-25): Kompakt yüksekliği değiştirince uçlar elle tazelenir — yoksa
        // çizgiler tam-boy kartın ortasına asılı kalırdı.
        foreach (HaritaKenarGorunumu e in Kenarlar)
            e.Guncelle();
    }

    /// <summary>Kartların güncel konumları (kalıcılaştırmak için).</summary>
    public IReadOnlyDictionary<string, Nokta> KonumSozlugu()
        => Dugumler.ToDictionary(d => d.TamAd, d => new Nokta(d.X, d.Y), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// TAM harita mı görünüyor? Kalıcı konum yalnız tam görünümde yazılır (inceleme 2026-07-25):
    /// host kaydı sözlüğü olduğu gibi DEĞİŞTİRİR — odak/küme alt kümesinde yazılsaydı DB'nin
    /// kayıtlı yerleşimi 3-5 tabloya inerdi.
    /// </summary>
    private bool TamGorunum => ReferenceEquals(_gorunurModel, _model);

    /// <summary>Host'tan konumları kalıcılaştırmasını ister (code-behind sürükleme bitince çağırır).</summary>
    public void KaydetIstensin()
    {
        if (TamGorunum)
            KonumlariKaydetIstendi?.Invoke();
    }

    /// <summary>Güncel yerleşimle vektör SVG üretir (v9-S4 dışa aktarma) — o an GÖRÜNEN alt küme.</summary>
    public string SvgUret(bool koyu)
        => _gorunurModel is null ? "" : HaritaSvg.Uret(_gorunurModel, KonumSozlugu(), koyu);

    /// <summary>Haritada gösterilecek bir şey var mı (dışa aktarma düğmeleri kapısı).</summary>
    public bool DisaAktarilabilir => Dugumler.Count > 0;

    private void Kur(IReadOnlyDictionary<string, Nokta> yer)
    {
        Dugumler.Clear();
        Kenarlar.Clear();
        if (_gorunurModel is null)
            return;

        var indeks = new Dictionary<string, HaritaDugumGorunumu>(StringComparer.OrdinalIgnoreCase);
        foreach (HaritaDugumu d in _gorunurModel.Dugumler)
        {
            Nokta n = yer.TryGetValue(d.TamAd, out Nokta? p) ? p : new Nokta(0, 0);
            var gv = new HaritaDugumGorunumu(d, n.X, n.Y);
            Dugumler.Add(gv);
            indeks[d.TamAd] = gv;
        }
        foreach (HaritaKenari e in _gorunurModel.Kenarlar)
            if (indeks.TryGetValue(e.KaynakTamAd, out HaritaDugumGorunumu? k)
                && indeks.TryGetValue(e.HedefTamAd, out HaritaDugumGorunumu? h))
                Kenarlar.Add(new HaritaKenarGorunumu(k, h, e));
    }

    /// <summary>Kuvvet-yönlü otomatik yerleşim — o an GÖRÜNEN alt kümeye uygulanır.</summary>
    [RelayCommand]
    private void OtomatikYerlestir()
    {
        if (_gorunurModel is null)
            return;
        Konumla(HaritaYerlesim.KuvvetYonlu(_gorunurModel));
    }

    /// <summary>Izgara yerleşimine döner ve yakınlaştırmayı sıfırlar (görünen alt küme).</summary>
    [RelayCommand]
    private void Sigdir()
    {
        if (_gorunurModel is null)
            return;
        Konumla(HaritaYerlesim.Izgara(_gorunurModel));
        Olcek = 1.0;
    }

    private void Konumla(IReadOnlyDictionary<string, Nokta> yer)
    {
        foreach (HaritaDugumGorunumu d in Dugumler)
            if (yer.TryGetValue(d.TamAd, out Nokta? p))
                (d.X, d.Y) = (p.X, p.Y); // kenarlar X/Y PropertyChanged'ini dinliyor
        if (TamGorunum)
            KonumlariKaydetIstendi?.Invoke(); // alt kümede kayıt yazılmaz (yukarıdaki not)
    }

    /// <summary>Bir kartın üstüne gelinince: o kart + komşuları vurgulanır, gerisi soluklaşır.</summary>
    public void Odakla(HaritaDugumGorunumu dugum)
    {
        var komsu = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dugum.TamAd };
        foreach (HaritaKenarGorunumu e in Kenarlar)
        {
            if (e.Kaynak == dugum) komsu.Add(e.Hedef.TamAd);
            if (e.Hedef == dugum) komsu.Add(e.Kaynak.TamAd);
        }
        foreach (HaritaDugumGorunumu n in Dugumler)
        {
            n.Vurgulu = komsu.Contains(n.TamAd);
            n.Soluk = !n.Vurgulu;
        }
        foreach (HaritaKenarGorunumu e in Kenarlar)
        {
            bool bagli = e.Kaynak == dugum || e.Hedef == dugum;
            e.Vurgulu = bagli;
            e.Soluk = !bagli;
        }
    }

    /// <summary>Odağı kaldırır (hover çıkışı). Aktif bir arama varsa arama vurgusu geri yüklenir.</summary>
    public void OdakTemizle()
    {
        if (!string.IsNullOrWhiteSpace(Arama))
            AramaUygula();
        else
            OdakSifirla();
    }

    private void OdakSifirla()
    {
        foreach (HaritaDugumGorunumu n in Dugumler) { n.Vurgulu = false; n.Soluk = false; }
        foreach (HaritaKenarGorunumu e in Kenarlar) { e.Vurgulu = false; e.Soluk = false; }
    }

    /// <summary>Arama metnini uygular: eşleşen kartlar vurgulanır; iki ucu da eşleşen kenarlar aydınlanır.</summary>
    private void AramaUygula()
    {
        string q = Arama.Trim();
        if (q.Length == 0)
        {
            OdakSifirla();
            return;
        }
        foreach (HaritaDugumGorunumu n in Dugumler)
        {
            bool eslesme = n.TamAd.Contains(q, StringComparison.OrdinalIgnoreCase);
            n.Vurgulu = eslesme;
            n.Soluk = !eslesme;
        }
        foreach (HaritaKenarGorunumu e in Kenarlar)
        {
            bool ikisi = e.Kaynak.Vurgulu && e.Hedef.Vurgulu;
            e.Vurgulu = ikisi;
            e.Soluk = !ikisi;
        }
    }

    /// <summary>Kartın seçimini değiştirir (Görsele gönder için). Code-behind gövde tıkında çağırır.</summary>
    public void SecimiDegistir(HaritaDugumGorunumu dugum)
    {
        dugum.Secili = !dugum.Secili;
        OnPropertyChanged(nameof(SecililerVar));
        OnPropertyChanged(nameof(SeciliSayisi));
    }

    /// <summary>Seçili kartları Görsel Sorgu tuvaline gönderir; seçim temizlenir.</summary>
    [RelayCommand]
    private void GorseleGonder()
    {
        List<string> secili = [.. Dugumler.Where(d => d.Secili).Select(d => d.TamAd)];
        if (secili.Count == 0)
        {
            Bilgi = "Önce göndermek için tablo(lar) seçin — kart gövdesine tıklayın.";
            return;
        }
        GorselleGonder?.Invoke(secili);
        foreach (HaritaDugumGorunumu d in Dugumler) d.Secili = false;
        OnPropertyChanged(nameof(SecililerVar));
        OnPropertyChanged(nameof(SeciliSayisi));
        Bilgi = $"{secili.Count} tablo Görsel Sorgu tuvaline gönderildi.";
    }

    public Task KapatAsync() => Task.CompletedTask; // kalıcı kaynak yok
}
