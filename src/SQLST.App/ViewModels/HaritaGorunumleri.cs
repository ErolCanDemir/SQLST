using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;

namespace SQLST.App.ViewModels;

/// <summary>
/// Haritadaki bir tablo kartı (v9-S2). <see cref="HaritaDugumu"/> modelini sarar; <see cref="X"/>/
/// <see cref="Y"/> tuval konumudur ve sürüklenince güncellenir (kenarlar bunu dinler). <see cref="Vurgulu"/>/
/// <see cref="Soluk"/> hover odağı içindir. Şema rengi (dot + sol şerit) şema adının hash'inden gelir —
/// tema-bağımsız, düşük risk (kalıcı değil; oturum içinde tutarlı).
/// </summary>
public sealed partial class HaritaDugumGorunumu : ObservableObject
{
    public const double Genislik = 220;
    private const double BaslikYuksekligi = 34, SatirYuksekligi = 24;

    // Şemaları ayırt eden orta-ton palet (açık/koyu zeminde de okunur; küçük öğelerde kullanılır).
    private static readonly Color[] SemaRenkleri =
    [
        Color.FromRgb(0x26, 0xA6, 0x9A), Color.FromRgb(0x5A, 0xA9, 0xEE), Color.FromRgb(0xB0, 0x8B, 0xE0),
        Color.FromRgb(0xE0, 0xA9, 0x4E), Color.FromRgb(0xE0, 0x7A, 0x8A), Color.FromRgb(0x66, 0xBB, 0x6A),
    ];

    public HaritaDugumGorunumu(HaritaDugumu model, double x, double y)
    {
        Model = model;
        _x = x;
        _y = y;
        int i = (int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(model.Sema) % (uint)SemaRenkleri.Length);
        var firca = new SolidColorBrush(SemaRenkleri[i]);
        firca.Freeze();
        SemaFircasi = firca;
    }

    public HaritaDugumu Model { get; }
    public string Ad => Model.Ad;
    public string Sema => Model.Sema;
    public string TamAd => Model.TamAd;
    public IReadOnlyList<HaritaKolonu> Kolonlar => Model.Kolonlar;

    /// <summary>Bu düğümün şema rengi (başlık noktası + sol şerit). Donmuş; oturum içinde sabit.</summary>
    public Brush SemaFircasi { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    /// <summary>Hover odağında (kendisi ya da bir komşusu) mi — kart yükselir/vurgulanır.</summary>
    [ObservableProperty] private bool _vurgulu;

    /// <summary>Odak dışında mı — soluklaşır (opacity).</summary>
    [ObservableProperty] private bool _soluk;

    /// <summary>Görsel Sorgu'ya göndermek için seçili mi (v9-S3 köprü) — kart gövdesine tıkla.</summary>
    [ObservableProperty] private bool _secili;

    /// <summary>
    /// Semantik sadelik (#1, 2026-07-25): görünür kart sayısı eşiği aşarsa kart KOMPAKT çizilir —
    /// yalnız başlık (kolon listesi gizli), yükseklik başlığa iner. Kenar uçları da buna uyar.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Yukseklik))]
    [NotifyPropertyChangedFor(nameof(MerkezY))]
    private bool _kompakt;

    public double Yukseklik => Kompakt ? BaslikYuksekligi : BaslikYuksekligi + Kolonlar.Count * SatirYuksekligi;
    public double MerkezY => Y + Yukseklik / 2;
    public double SagX => X + Genislik;
}

/// <summary>
/// İki kart arasındaki FK bağı (v9-S2). Uçlar kartların yan kenarına, düğümün dikey ORTASINA bağlanır;
/// <see cref="Cizim"/> yumuşak bir bezier + hedefe bakan açık ok'tur. Kartlar taşınınca (X/Y değişince)
/// kendini yeniden hesaplar. <see cref="Vurgulu"/>/<see cref="Soluk"/> hover odağı içindir.
/// </summary>
public sealed partial class HaritaKenarGorunumu : ObservableObject
{
    public HaritaKenarGorunumu(HaritaDugumGorunumu kaynak, HaritaDugumGorunumu hedef, HaritaKenari model)
    {
        Kaynak = kaynak;
        Hedef = hedef;
        Model = model;
        kaynak.PropertyChanged += Konum;
        hedef.PropertyChanged += Konum;
        Guncelle();
    }

    public HaritaDugumGorunumu Kaynak { get; }
    public HaritaDugumGorunumu Hedef { get; }
    public HaritaKenari Model { get; }

    [ObservableProperty] private Geometry? _cizim;
    [ObservableProperty] private bool _vurgulu;
    [ObservableProperty] private bool _soluk;

    /// <summary>Tooltip: FK kolon eşleşmesi, ör. "SehirId → Id".</summary>
    public string Ozet =>
        $"{Kaynak.Ad} → {Hedef.Ad}\n{string.Join(", ", Model.Kolonlar.Select(k => $"{k.KaynakKolon} → {k.HedefKolon}"))}";

    private void Konum(object? gonderen, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HaritaDugumGorunumu.X) or nameof(HaritaDugumGorunumu.Y))
            Guncelle();
    }

    /// <summary>Uç noktalarını düğüm konumlarından hesaplayıp bezier + ok geometrisini kurar.</summary>
    public void Guncelle()
    {
        double kMerkez = Kaynak.X + HaritaDugumGorunumu.Genislik / 2;
        double hMerkez = Hedef.X + HaritaDugumGorunumu.Genislik / 2;
        bool hedefSagda = kMerkez < hMerkez;                 // hedef sağdaysa: kaynak sağ kenarı → hedef sol kenarı

        double sx = hedefSagda ? Kaynak.SagX : Kaynak.X;
        double tx = hedefSagda ? Hedef.X : Hedef.SagX;
        double sy = Kaynak.MerkezY, ty = Hedef.MerkezY;
        double dx = Math.Max(50, Math.Abs(tx - sx) * 0.4);
        double c1x = hedefSagda ? sx + dx : sx - dx;
        double c2x = hedefSagda ? tx - dx : tx + dx;

        var g = new StreamGeometry();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(new Point(sx, sy), false, false);
            c.BezierTo(new Point(c1x, sy), new Point(c2x, ty), new Point(tx, ty), true, false);

            // Açık ok, hedefe bakar: hedef sağdaysa ok doğuya (barb'lar batıda), değilse tersi.
            double yon = hedefSagda ? 1 : -1;
            c.BeginFigure(new Point(tx - yon * 9, ty - 5), false, false);
            c.LineTo(new Point(tx, ty), true, false);
            c.LineTo(new Point(tx - yon * 9, ty + 5), true, false);
        }
        g.Freeze();
        Cizim = g;
    }
}
