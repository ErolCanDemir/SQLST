using System.Text;
using System.Windows;

namespace SQLST.App.Views;

/// <summary>
/// v20-S21 m.10 fikir 5: kolon istatistiği (MIN/MAX/AVG/SUM/DISTINCT/NULL) — küçük, salt okunur
/// pencere. Sorgu ARKA PLANDA koşar: pencere anında açılır ("hesaplanıyor…"), sonuç gelince dolar;
/// hata olursa nedeni yazılır (sessiz boş pencere yok).
/// </summary>
public partial class KolonIstatistikPenceresi : Window
{
    public sealed record Satir(string Etiket, string Deger);

    private IReadOnlyList<Satir> _satirlar = [];
    private readonly string _kolonAd;

    public KolonIstatistikPenceresi(
        string kolonAd,
        Func<Task<(IReadOnlyList<(string Etiket, string Deger)>? Satirlar, string? Not, string? Hata)>> getir)
    {
        InitializeComponent();
        _kolonAd = kolonAd;
        Basligi.Text = $"📊 {kolonAd}";
        Durum.Text = "Hesaplanıyor… (tek sorgu — sorgunuzun süzgeci korunur)";

        Loaded += async (_, _) =>
        {
            (IReadOnlyList<(string Etiket, string Deger)>? satirlar, string? not, string? hata) = await getir();
            if (hata is not null || satirlar is null)
            {
                Durum.Text = hata ?? "İstatistik alınamadı.";
                return;
            }

            _satirlar = [.. satirlar.Select(s => new Satir(s.Etiket, s.Deger))];
            Satirlar.ItemsSource = _satirlar;
            Durum.Visibility = Visibility.Collapsed;
            if (not is not null)
            {
                NotMetni.Text = "⚠ " + not;
                NotMetni.Visibility = Visibility.Visible;
            }
        };
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        if (_satirlar.Count == 0)
            return;
        var sb = new StringBuilder($"{_kolonAd}\n");
        foreach (Satir s in _satirlar)
            sb.AppendLine($"{s.Etiket}\t{s.Deger}");
        Clipboard.SetText(sb.ToString());
        Durum.Text = "Panoya kopyalandı.";
        Durum.Visibility = Visibility.Visible;
    }
}
