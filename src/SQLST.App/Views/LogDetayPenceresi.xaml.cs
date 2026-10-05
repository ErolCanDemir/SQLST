using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// Log Analizi "detaya in" penceresi (v20-S3): bir imza grubunun LIKE kalıbına uyan HAM kayıtları
/// (tüm kolonlar) bir grid'de gösterir ve bu kayıtların zaman damgalarından SAAT/GÜN/HAFTA/AY kovalı
/// bir <b>zaman dağılımı (sparkline)</b> çizer — "bu hata artıyor mu, ne zaman yoğunlaştı" sorusu için.
/// Sparkline ayrı bir sorgu istemez: ana pencerenin çektiği detay satırlarından hesaplanır.
/// </summary>
public partial class LogDetayPenceresi : Window
{
    private enum Kova { Saat, Gun, Hafta, Ay }

    public LogDetayPenceresi(string baslik, int sayi, ResultSetData veri, string? zamanKolon)
    {
        InitializeComponent();
        BaslikMetni.Text = $"🔬 {baslik}";
        OzetMetni.Text = $"Grup toplamı ~{sayi:N0} kayıt · aşağıda örnek olarak {veri.Satirlar.Count:N0} ham kayıt "
            + "(kalıba uyan en yeni kayıtlar).";

        DetayGrid.ItemsSource = TabloYap(veri).DefaultView;
        SparklineCiz(veri, zamanKolon);
    }

    /// <summary>ResultSetData → DataTable (AutoGenerateColumns için); null → DBNull, mükerrer kolon adı teklenir.</summary>
    private static DataTable TabloYap(ResultSetData veri)
    {
        var t = new DataTable();
        var adlar = new List<string>();
        foreach (KolonBilgisi k in veri.Kolonlar)
        {
            string ad = k.Ad;
            int n = 1;
            while (adlar.Contains(ad, StringComparer.OrdinalIgnoreCase))
                ad = $"{k.Ad}_{++n}";
            adlar.Add(ad);
            t.Columns.Add(ad, typeof(object));
        }
        foreach (object?[] satir in veri.Satirlar)
        {
            object[] hucreler = new object[adlar.Count];
            for (int i = 0; i < adlar.Count; i++)
                hucreler[i] = (i < satir.Length ? satir[i] : null) ?? DBNull.Value;
            t.Rows.Add(hucreler);
        }
        return t;
    }

    private void SparklineCiz(ResultSetData veri, string? zamanKolon)
    {
        int zamanIdx = zamanKolon is null ? -1 : KolonIndeksi(veri.Kolonlar, zamanKolon);
        List<DateTime> zamanlar = zamanIdx < 0
            ? []
            : [.. veri.Satirlar
                .Select(s => zamanIdx < s.Length ? ZamanCoz(s[zamanIdx]) : null)
                .Where(d => d is not null)
                .Select(d => d!.Value)];

        if (zamanlar.Count < 2)
        {
            SparklineKap.Visibility = Visibility.Collapsed; // zaman kolonu yok/çözülemedi → dağılım yok
            return;
        }

        DateTime enAz = zamanlar.Min(), enCok = zamanlar.Max();
        Kova kova = KovaSec(enAz, enCok);
        SparklineBaslik.Text = $"Zaman dağılımı ({KovaAdi(kova)} · {zamanlar.Count:N0} örnek kayıt)";

        Dictionary<DateTime, int> sayimlar = zamanlar
            .GroupBy(z => KovaBasi(z, kova))
            .ToDictionary(g => g.Key, g => g.Count());

        // enAz→enCok arası TÜM kovalar (boş kovalar da 0 çubuk olarak görünsün — süreklilik).
        List<DateTime> eksen = [];
        for (DateTime k = KovaBasi(enAz, kova); k <= enCok; k = KovaSonraki(k, kova))
            eksen.Add(k);

        int enBuyuk = Math.Max(1, sayimlar.Values.Max());
        var vurgu = FindResource("VurguFircasi") as Brush ?? Brushes.SteelBlue;

        Sparkline.Children.Clear();
        foreach (DateTime k in eksen)
        {
            int c = sayimlar.GetValueOrDefault(k, 0);
            double h = c == 0 ? 2 : 6 + (c / (double)enBuyuk) * 48;
            Sparkline.Children.Add(new Border
            {
                Width = 13,
                Height = h,
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(2, 2, 0, 0),
                Background = c == 0 ? (FindResource("KenarFircasi") as Brush ?? Brushes.Gray) : vurgu,
                ToolTip = $"{KovaEtiketi(k, kova)} — {c:N0} kayıt",
            });
        }
    }

    private static Kova KovaSec(DateTime enAz, DateTime enCok)
    {
        // ~60 çubuğu geçmeyen en ince kovayı seç.
        double saat = (enCok - enAz).TotalHours;
        if (saat <= 60) return Kova.Saat;
        if (saat / 24 <= 60) return Kova.Gun;
        if (saat / (24 * 7) <= 60) return Kova.Hafta;
        return Kova.Ay;
    }

    private static DateTime KovaBasi(DateTime t, Kova k) => k switch
    {
        Kova.Saat => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0),
        Kova.Gun => t.Date,
        Kova.Hafta => t.Date.AddDays(-(int)t.DayOfWeek),
        _ => new DateTime(t.Year, t.Month, 1),
    };

    private static DateTime KovaSonraki(DateTime t, Kova k) => k switch
    {
        Kova.Saat => t.AddHours(1),
        Kova.Gun => t.AddDays(1),
        Kova.Hafta => t.AddDays(7),
        _ => t.AddMonths(1),
    };

    private static string KovaEtiketi(DateTime t, Kova k) => k switch
    {
        Kova.Saat => t.ToString("dd.MM HH:00", CultureInfo.InvariantCulture),
        Kova.Gun => t.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        Kova.Hafta => t.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + " haftası",
        _ => t.ToString("MM.yyyy", CultureInfo.InvariantCulture),
    };

    private static string KovaAdi(Kova k) => k switch
    {
        Kova.Saat => "saatlik",
        Kova.Gun => "günlük",
        Kova.Hafta => "haftalık",
        _ => "aylık",
    };

    private static int KolonIndeksi(IReadOnlyList<KolonBilgisi> kolonlar, string ad)
    {
        for (int i = 0; i < kolonlar.Count; i++)
            if (string.Equals(kolonlar[i].Ad, ad, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static DateTime? ZamanCoz(object? deger) => deger switch
    {
        null or DBNull => null,
        DateTime dt => dt,
        DateTimeOffset dto => dto.LocalDateTime,
        _ => DateTime.TryParse(deger.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime p) ? p : null,
    };
}
