using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SQLST.App;

/// <summary>
/// Asistan cevabı için HAFİF Markdown çizici (v11-S7 rötuş 2, kullanıcı bulgusu 2026-07-25 gece:
/// "yazılar güzel olmadı" — model Markdown döner, ham yıldız/backtick basmak çirkindi).
/// Desteklenen alt küme (modelin fiilen kullandığı): <c>**kalın**</c>, <c>`satır içi kod`</c>
/// (çip zeminli), <c>```</c> kod blokları (kutulu monospace), <c>*</c>/<c>-</c> maddeler,
/// <c>#</c> başlıklar. Tam CommonMark DEĞİL — bilinçli: bağımlılık yok, öngörülebilir çıktı.
/// </summary>
public static class MarkdownGorunum
{
    public static FrameworkElement Olustur(string metin)
    {
        var kok = new StackPanel();
        string[] satirlar = metin.Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < satirlar.Length; i++)
        {
            string satir = satirlar[i];

            // ``` kod bloğu: kapanışa kadar topla, kutu içinde monospace (kopyalanabilir TextBox)
            if (satir.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var kod = new System.Text.StringBuilder();
                i++;
                while (i < satirlar.Length && !satirlar[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    kod.AppendLine(satirlar[i++]);
                kok.Children.Add(KodKutusu(kod.ToString().TrimEnd()));
                continue;
            }

            if (string.IsNullOrWhiteSpace(satir))
                continue; // paragraf boşlukları Margin'lerden gelir

            string kirpik = satir.TrimStart();

            // Başlık: # ...
            if (kirpik.StartsWith('#'))
            {
                var baslik = new TextBlock
                {
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Margin = new Thickness(0, 8, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                };
                baslik.SetResourceReference(TextBlock.ForegroundProperty, "MetinFircasi");
                SatirIciEkle(baslik.Inlines, kirpik.TrimStart('#', ' '));
                kok.Children.Add(baslik);
                continue;
            }

            // Madde: * ... / - ... (girintili alt maddeler de)
            if (kirpik.StartsWith("* ", StringComparison.Ordinal)
                || kirpik.StartsWith("- ", StringComparison.Ordinal))
            {
                int girinti = satir.Length - kirpik.Length;
                var madde = new DockPanel { Margin = new Thickness(6 + girinti * 3, 2, 0, 2) };
                var isaret = new TextBlock
                {
                    Text = "•",
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                isaret.SetResourceReference(TextBlock.ForegroundProperty, "VurguFircasi");
                DockPanel.SetDock(isaret, Dock.Left);
                madde.Children.Add(isaret);
                madde.Children.Add(SatirBlogu(kirpik[2..]));
                kok.Children.Add(madde);
                continue;
            }

            var paragraf = SatirBlogu(kirpik);
            paragraf.Margin = new Thickness(0, 2, 0, 2);
            kok.Children.Add(paragraf);
        }
        return kok;
    }

    private static TextBlock SatirBlogu(string metin)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "MetinFircasi");
        SatirIciEkle(tb.Inlines, metin);
        return tb;
    }

    private static Border KodKutusu(string kod)
    {
        var icerik = new TextBox
        {
            Text = kod,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        icerik.SetResourceReference(TextBox.FontFamilyProperty, "KodYazisi");
        icerik.SetResourceReference(TextBox.ForegroundProperty, "MetinFircasi");
        var kutu = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 6),
            Child = icerik,
        };
        kutu.SetResourceReference(Border.BackgroundProperty, "PanelZeminFircasi");
        kutu.SetResourceReference(Border.BorderBrushProperty, "KenarFircasi");
        return kutu;
    }

    /// <summary>Satır içi: **kalın** ve `kod` (çip zeminli mono Run). Sıra: önce **, segment içinde `.</summary>
    private static void SatirIciEkle(InlineCollection hedef, string metin)
    {
        string[] kalinParcalar = metin.Split("**");
        for (int i = 0; i < kalinParcalar.Length; i++)
        {
            bool kalin = i % 2 == 1; // ** aralarındaki tek indeksler kalındır
            string[] kodParcalar = kalinParcalar[i].Split('`');
            for (int j = 0; j < kodParcalar.Length; j++)
            {
                if (kodParcalar[j].Length == 0)
                    continue;
                var run = new Run(kodParcalar[j]);
                if (kalin)
                    run.FontWeight = FontWeights.Bold;
                if (j % 2 == 1) // ` aralarındaki: satır içi kod çipi
                {
                    run.SetResourceReference(TextElement.FontFamilyProperty, "KodYazisi");
                    run.SetResourceReference(TextElement.BackgroundProperty, "VurguZeminFircasi");
                    run.SetResourceReference(TextElement.ForegroundProperty, "VurguFircasi");
                }
                hedef.Add(run);
            }
        }
    }
}
