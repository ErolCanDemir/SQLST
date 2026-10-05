using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;

namespace SQLST.App.ViewModels;

/// <summary>
/// Mahalle görünümündeki KÜME kartı (v11-öncesi #1, 2026-07-25): büyük üye sayısı + küme adı +
/// öne çıkan 3 tablo. Çift tık kümeyi açar (yalnız o kümenin tabloları çizilir). Konum sabittir
/// (ızgara) — mahalle kartları sürüklenmez; sadelik bilinçli.
/// </summary>
public sealed partial class HaritaKumeKartGorunumu : ObservableObject
{
    public const double Genislik = 240, Yukseklik = 132;

    // Kart üst bandı renkleri — şema paletiyle aynı aile, kümeye indeksle atanır (deterministik).
    private static readonly Color[] Renkler =
    [
        Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x8B, 0x5C, 0xF6),
        Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0xEA, 0xB3, 0x08), Color.FromRgb(0xF4, 0x3F, 0x5E),
        Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0xF9, 0x73, 0x16),
    ];

    public HaritaKumeKartGorunumu(HaritaKumesi kume, int indeks, double x, double y)
    {
        Kume = kume;
        X = x;
        Y = y;
        var firca = new SolidColorBrush(Renkler[indeks % Renkler.Length]);
        firca.Freeze();
        BandFircasi = firca;
    }

    public HaritaKumesi Kume { get; }
    public string Ad => Kume.Ad;
    public int UyeSayisi => Kume.Uyeler.Count;

    /// <summary>Kart gövdesindeki "öne çıkanlar" listesi (en bağlantılı 3 tablonun kısa adı).</summary>
    public string OneCikanlar => string.Join("\n", Kume.OneCikanlar.Select(KisaAd));

    public Brush BandFircasi { get; }
    public double X { get; }
    public double Y { get; }
    public double MerkezX => X + Genislik / 2;
    public double MerkezY => Y + Yukseklik / 2;

    private static string KisaAd(string tamAd)
        => tamAd.Contains('.') ? tamAd[(tamAd.IndexOf('.') + 1)..] : tamAd;
}

/// <summary>İki küme kartı arasındaki toplu bağ: düz çizgi + ortada "N FK" rozeti.</summary>
public sealed class HaritaKumeBagGorunumu(
    HaritaKumeKartGorunumu kaynak, HaritaKumeKartGorunumu hedef, int fkSayisi)
{
    public double X1 => kaynak.MerkezX;
    public double Y1 => kaynak.MerkezY;
    public double X2 => hedef.MerkezX;
    public double Y2 => hedef.MerkezY;
    public double OrtaX => (X1 + X2) / 2 - 18;
    public double OrtaY => (Y1 + Y2) / 2 - 11;
    public string Rozet => $"{fkSayisi} FK";
}
