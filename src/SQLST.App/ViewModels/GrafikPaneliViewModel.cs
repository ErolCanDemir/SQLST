using System.Collections.ObjectModel;
using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;

namespace SQLST.App.ViewModels;

/// <summary>Y çipi: kolon adı + seçili mi + rengi (seri sırası tuvalle aynı).</summary>
public sealed partial class YSecimi : ObservableObject
{
    public required string Ad { get; init; }
    public int RenkIndeksi { get; set; }
    [ObservableProperty] private bool _secili;
    public override string ToString() => Ad;
}

/// <summary>Toplama seçeneği — tema ComboBox'ı DisplayMemberPath'i yok saydığından ToString ile görünür.</summary>
public sealed record ToplamaSecenegi(GrafikToplama Deger, string Ad)
{
    public override string ToString() => Ad;
}

/// <summary>Açıklama (legend) satırı: ad + renk sırası + gizli mi (S2: tıklayınca seri/dilim gizlenir).</summary>
public sealed record AciklamaOgesi(string Ad, int RenkIndeksi, bool Gizli = false);

/// <summary>
/// 📊 Grafik panelinin durumu (v23-S24 S1 — K4/K7): result set başına bir tane, sekme ömrünce yaşar. Seçim
/// değişince modeli SAF çekirdekten (<see cref="GrafikHazirlayici"/>) yeniden kurar; tablo değişince
/// (oto-yenile, yeniden çalıştırma) ayarı korur — kolonlar artık yoksa yeniden tahmin eder.
/// S2: açıklamadan seri/dilim gizleme (renk kaymaz), yığılmış çubuk, vuruş → grid satırları.
/// </summary>
public sealed partial class GrafikPaneliViewModel : ObservableObject
{
    public const string SeriYok = "— yok —";
    /// <summary>Grid'de tek tıkla seçilecek satır tavanı — WPF seçim nesneleri patlamasın (v19-S15 dersi).</summary>
    public const int SecimSatirTavani = 5_000;

    public static readonly IReadOnlyList<ToplamaSecenegi> ToplamaSecenekleri =
    [
        new(GrafikToplama.Yok, "Yok"), new(GrafikToplama.Toplam, "Toplam"), new(GrafikToplama.Ortalama, "Ortalama"),
        new(GrafikToplama.Adet, "Adet"), new(GrafikToplama.EnKucuk, "En küçük"), new(GrafikToplama.EnBuyuk, "En büyük"),
    ];

    private DataTable _tablo;
    private bool _kuruluyor; // seçimler toplu değişirken ara yenilemeleri atla
    private GrafikModeli? _tamModel; // gizleme uygulanmadan önceki model
    private readonly HashSet<string> _gizliler = new(StringComparer.Ordinal);

    [ObservableProperty] private IReadOnlyList<string> _kolonlar = [];
    [ObservableProperty] private IReadOnlyList<string> _seriSecenekleri = [];
    public ObservableCollection<YSecimi> YSecimleri { get; } = [];

    [ObservableProperty] private GrafikTuru _tur;
    [ObservableProperty] private string? _xKolon;
    [ObservableProperty] private string _seriKolon = SeriYok;
    [ObservableProperty] private ToplamaSecenegi _toplama = ToplamaSecenekleri[0];
    [ObservableProperty] private bool _yigilmis;
    /// <summary>Yığılmış çipi yalnız çubuk/yatay + çoklu seride görünür.</summary>
    [ObservableProperty] private bool _yigilmisSecilebilir;

    [ObservableProperty] private GrafikModeli? _model;
    [ObservableProperty] private string? _uyari;
    [ObservableProperty] private string _ozet = "";
    /// <summary>Kısa geri bildirim: "PNG panoya kopyalandı" gibi — bir sonraki yenilemede silinir.</summary>
    [ObservableProperty] private string _durum = "";
    [ObservableProperty] private IReadOnlyList<AciklamaOgesi> _aciklama = [];

    private int _ekranGenisligi;

    public GrafikPaneliViewModel(DataTable tablo, GrafikAyari? ayar = null)
    {
        _tablo = tablo;
        TabloyuDegistir(tablo, ayar);
    }

    /// <summary>Panelin o anki seçimi — yeniden çalıştırmada yeni set'e taşınır (K8: açık kalır, tazelenir).</summary>
    public GrafikAyari Ayar => new(Tur, XKolon,
        [.. YSecimleri.Where(y => y.Secili).Select(y => y.Ad)],
        SeriKolon == SeriYok ? null : SeriKolon, Toplama.Deger, Yigilmis);

    /// <summary>Tuval genişliği — çizgi/alan indirgemesinde kova sayısı (K5). Yalnız indirgenmiş modelde yeniden kurar.</summary>
    public int EkranGenisligi
    {
        get => _ekranGenisligi;
        set
        {
            if (Math.Abs(value - _ekranGenisligi) < 80) return;
            _ekranGenisligi = value;
            if (Model is { } m && m.ToplamKategori > GrafikHazirlayici.NoktaTavani && Tur is GrafikTuru.Cizgi or GrafikTuru.Alan)
                Yenile();
        }
    }

    /// <summary>Yeni tablo (aynı sekmede yeniden koşan sorgu): ayar uyuyorsa korunur, uymuyorsa tahmin edilir.</summary>
    public void TabloyuDegistir(DataTable tablo, GrafikAyari? ayar = null)
    {
        _tablo = tablo;
        ayar ??= Ayar;
        _kuruluyor = true;
        try
        {
            Kolonlar = GrafikHazirlayici.KategoriKolonlari(tablo);
            SeriSecenekleri = [SeriYok, .. Kolonlar];
            IReadOnlyList<string> sayisallar = GrafikHazirlayici.SayisalKolonlar(tablo);

            bool uyuyor = ayar.XKolon is not null && Kolonlar.Contains(ayar.XKolon)
                       && (ayar.YKolonlar.Count == 0 || ayar.YKolonlar.Any(sayisallar.Contains));
            if (!uyuyor)
                ayar = GrafikHazirlayici.Tahmin(tablo);

            foreach (YSecimi y in YSecimleri) y.PropertyChanged -= YDegisti;
            YSecimleri.Clear();
            int renk = 0;
            foreach (string ad in sayisallar)
            {
                var y = new YSecimi { Ad = ad, Secili = ayar.YKolonlar.Contains(ad), RenkIndeksi = renk++ };
                y.PropertyChanged += YDegisti;
                YSecimleri.Add(y);
            }

            Tur = ayar.Tur;
            XKolon = ayar.XKolon;
            SeriKolon = ayar.SeriKolon is not null && Kolonlar.Contains(ayar.SeriKolon) ? ayar.SeriKolon : SeriYok;
            Toplama = ToplamaSecenekleri.First(t => t.Deger == ayar.Toplama);
            Yigilmis = ayar.Yigilmis;
        }
        finally
        {
            _kuruluyor = false;
        }
        Yenile();
    }

    private void YDegisti(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(YSecimi.Secili)) Yenile();
    }

    partial void OnTurChanged(GrafikTuru value) => Yenile();
    partial void OnXKolonChanged(string? value) => Yenile();
    partial void OnSeriKolonChanged(string value) => Yenile();
    partial void OnToplamaChanged(ToplamaSecenegi value) => Yenile();
    partial void OnYigilmisChanged(bool value) => Yenile();

    /// <summary>Açıklamadaki öğeye tık (S2): seri (çubuk/çizgi) ya da dilim (pasta) gizlenir/geri gelir.</summary>
    public void AciklamaDegistir(string ad)
    {
        if (!_gizliler.Remove(ad))
            _gizliler.Add(ad);
        GizlemeyiUygula();
    }

    /// <summary>Vuruşa (kategori, seri) karşılık gelen grid satırları: X etiketi (ve seri kolonu varsa onun etiketi)
    /// eşleşenler. Toplama Yok'ta aynı etiket birden fazla satırda geçebilir — hepsi döner (dürüst). Tavan: 5.000.</summary>
    public (List<DataRowView> Satirlar, int Toplam) SatirlariBul(int kategori, int seri)
    {
        List<DataRowView> sonuc = [];
        if (Model is not { } m || XKolon is null || kategori < 0 || kategori >= m.Kategoriler.Count || !_tablo.Columns.Contains(XKolon))
            return (sonuc, 0);
        string kat = m.Kategoriler[kategori];
        DataColumn x = _tablo.Columns[XKolon]!;
        DataColumn? s = SeriKolon != SeriYok && _tablo.Columns.Contains(SeriKolon) ? _tablo.Columns[SeriKolon] : null;
        string? seriAd = s is not null && seri >= 0 && seri < m.Seriler.Count ? m.Seriler[seri].Ad : null;
        int toplam = 0;
        foreach (DataRowView satir in _tablo.DefaultView)
        {
            if (GrafikHazirlayici.KategoriMetni(satir.Row[x]) != kat) continue;
            if (seriAd is not null && GrafikHazirlayici.KategoriMetni(satir.Row[s!]) != seriAd) continue;
            toplam++;
            if (sonuc.Count < SecimSatirTavani) sonuc.Add(satir);
        }
        return (sonuc, toplam);
    }

    private void Yenile()
    {
        if (_kuruluyor) return;
        Durum = "";
        _tamModel = GrafikHazirlayici.Kur(_tablo, Ayar, _ekranGenisligi);
        YigilmisSecilebilir = Tur is GrafikTuru.Cubuk or GrafikTuru.YatayCubuk && _tamModel.Seriler.Count > 1;
        RenkleriEsle();
        GizlemeyiUygula();
    }

    /// <summary>Tam modelden gizli serileri/dilimleri düşer; açıklama hepsini (gizliler soluk) listeler.</summary>
    private void GizlemeyiUygula()
    {
        if (_tamModel is not { } tam)
            return;
        GrafikModeli m = tam;
        if (!tam.BosMu && _gizliler.Count > 0)
        {
            if (tam.Tur == GrafikTuru.Pasta)
            {
                GrafikSerisi s = tam.Seriler[0];
                m = tam with
                {
                    Seriler = [s with { Degerler = [.. s.Degerler.Select((d, i) => _gizliler.Contains(tam.Kategoriler[i]) ? null : d)] }],
                };
            }
            else
            {
                List<GrafikSerisi> kalan = [.. tam.Seriler.Where(s => !_gizliler.Contains(s.Ad))];
                m = kalan.Count == 0 ? GrafikModeli.Bos(tam.Tur, "Tüm seriler gizli — açıklamadan birini geri açın.")
                                     : tam with { Seriler = kalan };
            }
        }
        Model = m;
        Uyari = m.Uyari ?? (m.BosMu ? null : tam.Uyari);
        Aciklama = tam.BosMu ? []
            : tam.Tur == GrafikTuru.Pasta
                ? [.. tam.Kategoriler.Select((k, i) => new AciklamaOgesi(k, i, _gizliler.Contains(k)))]
                : [.. tam.Seriler.Select(s => new AciklamaOgesi(s.Ad, s.Renk, _gizliler.Contains(s.Ad)))];
        Ozet = m.BosMu ? ""
            : $"{m.Kategoriler.Count:N0} kategori · {m.Seriler.Count} seri · {m.NoktaSayisi:N0} nokta"
              + (_gizliler.Count > 0 ? $" · {_gizliler.Count} gizli" : "");
    }

    /// <summary>Y çipinin rengi = serinin tuvaldeki rengi: seçili olanlar sırayla 0,1,2…; seçilmeyenler renksiz (-1).</summary>
    private void RenkleriEsle()
    {
        int i = 0;
        foreach (YSecimi y in YSecimleri)
            y.RenkIndeksi = y.Secili && SeriKolon == SeriYok ? i++ : -1;
        // RenkIndeksi düz özellik: çipler Secili değişiminde yeniden çizilsin diye koleksiyonu bildiririz
        OnPropertyChanged(nameof(YSecimleri));
    }
}
