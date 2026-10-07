using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// 🕸 Kilitlenme şeması (v23-S3): Profiler'ın yakaladığı xml_deadlock_report'u "kim kimi
/// bekliyor" çizimine açar — SSMS'in .xdl görüntüleyicisinin gömülü karşılığı. Üstte süreçler
/// (KURBAN kırmızı), altta çekişilen kaynaklar; yeşil ok = sahip, turuncu kesikli ok = bekleyen
/// (kilit kipi etikette). Sürece tıklayınca sorgusu altta. XML çözülemezse ham metin gösterilir —
/// bilgi kaybolmaz. Renkler palet fırçalarından (tema uyumu).
/// </summary>
public partial class DeadlockPenceresi : Window
{
    private readonly string _xml;

    public DeadlockPenceresi(string xml, DateTime zaman)
    {
        InitializeComponent();
        _xml = xml;

        DeadlockGrafigi? grafik = DeadlockCozumleyici.Coz(xml);
        if (grafik is null)
        {
            Baslik.Text = $"Kilitlenme · {zaman:dd.MM.yyyy HH:mm:ss} — XML çözülemedi, ham rapor aşağıda.";
            SorguBaslik.Text = "Ham deadlock XML'i";
            Sorgu.Text = xml;
            return;
        }

        Baslik.Text = $"Kilitlenme · {zaman:dd.MM.yyyy HH:mm:ss} — "
            + $"{grafik.Surecler.Count} süreç · {grafik.Kaynaklar.Count} kaynak · "
            + $"kurban: SPID {grafik.Surecler.FirstOrDefault(s => s.KurbanMi)?.Spid ?? 0}";
        Ciz(grafik);
    }

    /// <summary>Süreçler üst sırada, kaynaklar alt sırada; oklar aradan geçer. Yerleşim basit ve
    /// deterministiktir — tipik kilitlenme 2-4 düğümdür, karmaşık graf düzeni gerekmez.</summary>
    private void Ciz(DeadlockGrafigi g)
    {
        const double SutunGen = 280, SurecY = 30, KaynakY = 250, KutuGen = 230;
        Tuval.Children.Clear();
        Tuval.Width = Math.Max(820, Math.Max(g.Surecler.Count, g.Kaynaklar.Count) * SutunGen + 40);
        Tuval.Height = 400;

        // Düğüm merkezleri (oklar için) — önce hesap, sonra çizim.
        var surecMerkez = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        var kaynakMerkez = new Dictionary<string, Point>();
        double surecBasX = (Tuval.Width - g.Surecler.Count * SutunGen) / 2 + 20;
        double kaynakBasX = (Tuval.Width - g.Kaynaklar.Count * SutunGen) / 2 + 20;
        for (int i = 0; i < g.Surecler.Count; i++)
            surecMerkez[g.Surecler[i].Id] = new Point(surecBasX + i * SutunGen + KutuGen / 2, SurecY + 45);
        for (int i = 0; i < g.Kaynaklar.Count; i++)
            kaynakMerkez[KaynakAnahtari(g.Kaynaklar[i], i)] = new Point(kaynakBasX + i * SutunGen + KutuGen / 2, KaynakY + 35);

        // Oklar ÖNCE (düğümlerin altında kalsın).
        for (int i = 0; i < g.Kaynaklar.Count; i++)
        {
            DeadlockKaynagi k = g.Kaynaklar[i];
            Point km = kaynakMerkez[KaynakAnahtari(k, i)];
            foreach ((string surecId, string kip) in k.Sahipler)
            {
                if (surecMerkez.TryGetValue(surecId, out Point sm))
                    Ok(km, sm, $"Sahip: {kip}", (Brush)FindResource("BasariFircasi"), kesikli: false);
            }
            foreach ((string surecId, string kip) in k.Bekleyenler)
            {
                if (surecMerkez.TryGetValue(surecId, out Point sm))
                    Ok(sm, km, $"Bekliyor: {kip}", (Brush)FindResource("UyariFircasi"), kesikli: true);
            }
        }

        // Süreç düğümleri.
        for (int i = 0; i < g.Surecler.Count; i++)
        {
            DeadlockSureci s = g.Surecler[i];
            var govde = new StackPanel();
            govde.Children.Add(new TextBlock
            {
                Text = (s.KurbanMi ? "⛔ KURBAN · " : "") + $"SPID {s.Spid}",
                FontWeight = FontWeights.Bold,
                Foreground = s.KurbanMi
                    ? (Brush)FindResource("TehlikeFircasi") : (Brush)FindResource("MetinFircasi"),
            });
            govde.Children.Add(Satir($"{s.Uygulama} · {s.Kullanici}"));
            if (s.YalitimDuzeyi is { Length: > 0 })
                govde.Children.Add(Satir(s.YalitimDuzeyi));

            var kutu = new Border
            {
                Width = KutuGen,
                CornerRadius = new CornerRadius(22),
                Padding = new Thickness(14, 10, 14, 10),
                Background = (Brush)FindResource("PanelZeminFircasi"),
                BorderBrush = s.KurbanMi
                    ? (Brush)FindResource("TehlikeFircasi") : (Brush)FindResource("VurguFircasi"),
                BorderThickness = new Thickness(s.KurbanMi ? 2.5 : 1.5),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = govde,
                ToolTip = "Tıkla: sorgusu altta",
            };
            DeadlockSureci sabit = s;
            kutu.MouseLeftButtonUp += (_, _) =>
            {
                SorguBaslik.Text = $"SPID {sabit.Spid} ({sabit.Uygulama}) — çalıştırdığı sorgu"
                    + (sabit.KurbanMi ? " · ⛔ sunucu bu süreci geri aldı" : "");
                Sorgu.Text = sabit.Sorgu ?? "(sorgu metni raporda yok)";
            };
            Canvas.SetLeft(kutu, surecBasX + i * SutunGen);
            Canvas.SetTop(kutu, SurecY);
            Tuval.Children.Add(kutu);
        }

        // Kaynak düğümleri.
        for (int i = 0; i < g.Kaynaklar.Count; i++)
        {
            DeadlockKaynagi k = g.Kaynaklar[i];
            var govde = new StackPanel();
            govde.Children.Add(new TextBlock
            {
                Text = $"🔒 {k.Tur}",
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("MetinFircasi"),
            });
            govde.Children.Add(Satir(k.Ad));
            var kutu = new Border
            {
                Width = KutuGen,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14, 8, 14, 8),
                Background = (Brush)FindResource("BilgiZeminFircasi"),
                BorderBrush = (Brush)FindResource("KenarFircasi"),
                BorderThickness = new Thickness(1.5),
                Child = govde,
            };
            Canvas.SetLeft(kutu, kaynakBasX + i * SutunGen);
            Canvas.SetTop(kutu, KaynakY);
            Tuval.Children.Add(kutu);
        }
    }

    private static string KaynakAnahtari(DeadlockKaynagi k, int sira) => $"{sira}·{k.Ad}";

    private TextBlock Satir(string metin) => new()
    {
        Text = metin,
        FontSize = 11.5,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = (Brush)FindResource("IkincilFircasi"),
    };

    /// <summary>İki merkez arasına ok + uç + orta noktada kip etiketi çizer.</summary>
    private void Ok(Point bas, Point son, string etiket, Brush firca, bool kesikli)
    {
        var cizgi = new Line
        {
            X1 = bas.X, Y1 = bas.Y, X2 = son.X, Y2 = son.Y,
            Stroke = firca, StrokeThickness = 2,
        };
        if (kesikli)
            cizgi.StrokeDashArray = [4, 3];
        Tuval.Children.Add(cizgi);

        // Ok ucu: yön vektöründen küçük üçgen.
        Vector yon = son - bas;
        yon.Normalize();
        Point uc = son - yon * 26; // düğüm kutusuna girmesin
        var normal = new Vector(-yon.Y, yon.X);
        var ucgen = new Polygon
        {
            Points = [uc, uc - yon * 12 + normal * 5, uc - yon * 12 - normal * 5],
            Fill = firca,
        };
        Tuval.Children.Add(ucgen);

        Point orta = bas + (son - bas) / 2;
        var yazi = new TextBlock
        {
            Text = etiket, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = firca,
            Background = (Brush)FindResource("PencereZeminFircasi"), Padding = new Thickness(4, 0, 4, 0),
        };
        Canvas.SetLeft(yazi, orta.X - 34);
        Canvas.SetTop(yazi, orta.Y - 10);
        Tuval.Children.Add(yazi);
    }

    private void XmlKopyala_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_xml); Baslik.Text += "  ·  ⧉ kopyalandı"; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        { /* pano meşgul — nadir, sessiz geç */ }
    }

    /// <summary>.xdl olarak kaydeder — SSMS aynı XML'i çift tıkla kendi görüntüleyicisinde açar
    /// (ekip arkadaşına gönderilebilir kanıt).</summary>
    private void XdlKaydet_Click(object sender, RoutedEventArgs e)
    {
        var kutu = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Deadlock dosyası (*.xdl)|*.xdl",
            FileName = $"deadlock-{DateTime.Now:yyyyMMdd-HHmm}.xdl",
        };
        if (kutu.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(kutu.FileName, _xml, new System.Text.UTF8Encoding(false));
            Baslik.Text += "  ·  ⬇ kaydedildi";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(this, "SQLST — Kilitlenme", "Dosya kaydedilemedi", ex);
        }
    }
}
