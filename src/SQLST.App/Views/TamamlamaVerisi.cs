using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>OtoTamamlama önerisinin AvalonEdit CompletionWindow karşılığı (V2-S3, FG-3.8).</summary>
public sealed class TamamlamaVerisi(TamamlamaOnerisi oneri) : ICompletionData
{
    public ImageSource? Image => null;

    public string Text => oneri.Metin;

    /// <summary>
    /// Listede gösterilen satır (kullanıcı isteği 2026-07-20 — "satır satır güzel görünsün"):
    /// [tür simgesi] ad ............ [muted tür etiketi]. Örn. "▦ Musteri … tablo",
    /// "◆ MusteriId … int · PK", "⚡ sel … parça". Simge tür rengiyle boyanır.
    /// </summary>
    public object Content => _icerik ??= IcerikKur();
    private FrameworkElement? _icerik;

    /// <summary>Sağdaki muted tür etiketi (pencere genişliği hesabında da ölçülür).</summary>
    public string TurEtiketi => Bicim().Etiket;

    /// <summary>Snippet'te açıklama olarak gövdenin kendisi gösterilir — ne gireceğini gör.</summary>
    public object? Description => oneri.Snippet is { } s
        ? $"{s.Aciklama}\n\n{s.Govde.Replace(Contracts.Snippet.ImlecIsareti, "▮")}"
        : oneri.Aciklama;

    public double Priority => oneri.Oncelik;

    /// <summary>Öneriyi türüne göre (simge, tür-etiketi, simge-renk anahtarı) biçimler. Öncelik türü
    /// kodlar: 4=snippet · 3=kolon · 2=nesne · 1=anahtar sözcük (bkz. OtoTamamlama).</summary>
    private (string Glif, string Etiket, string RenkAnahtari) Bicim()
    {
        if (oneri.Snippet is not null)
            return ("⚡", "parça", "UyariFircasi");
        if (oneri.Aciklama == OtoTamamlama.SemaEtiketi)                     // şema: nesneyle aynı öncelikte, ayrı görünüm
            return ("▤", "şema", "VurguFircasi");
        return oneri.Oncelik switch
        {
            3 => ("◆", oneri.Aciklama ?? "kolon", "VurguFircasi"),          // kolon: "tip · PK"
            2 => ("▦", NesneTuru(oneri.Aciklama), "BasariFircasi"),          // nesne: Tablo/View…
            1 => ("ᴋ", "anahtar", "SolukMetinFircasi"),                      // anahtar sözcük
            _ => ("•", oneri.Aciklama ?? "", "SolukMetinFircasi"),
        };
    }

    /// <summary>Nesne açıklaması "Tablo — db.sema.ad" biçiminde; tür kısmını (ilk parça) ayıklar.</summary>
    private static string NesneTuru(string? aciklama)
        => string.IsNullOrEmpty(aciklama) ? "nesne" : aciklama.Split(" — ")[0];

    private FrameworkElement IcerikKur()
    {
        (string glif, string etiket, string renkAnahtari) = Bicim();

        var satir = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        satir.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });   // simge
        satir.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // ad
        satir.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // tür etiketi

        var simge = new TextBlock
        {
            Text = glif,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Fircasi(renkAnahtari, "MetinFircasi"),
        };
        Grid.SetColumn(simge, 0);

        var ad = new TextBlock
        {
            Text = oneri.Metin,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Fircasi("MetinFircasi", null),
        };
        Grid.SetColumn(ad, 1);

        var tur = new TextBlock
        {
            Text = etiket,
            FontSize = 10.5,
            Margin = new Thickness(12, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Fircasi("SolukMetinFircasi", null),
        };
        Grid.SetColumn(tur, 2);

        satir.Children.Add(simge);
        satir.Children.Add(ad);
        satir.Children.Add(tur);
        return satir;
    }

    /// <summary>Tema fırçasını kaynak sözlüğünden çözer; yoksa yedeğe, o da yoksa griye düşer.
    /// (<c>Application</c> tam nitelenir — <c>SQLST.Application</c> ad alanıyla çakışmasın.)</summary>
    private static Brush Fircasi(string anahtar, string? yedek)
    {
        System.Windows.Application? uyg = System.Windows.Application.Current;
        return (uyg?.TryFindResource(anahtar) as Brush)
           ?? (yedek is not null ? uyg?.TryFindResource(yedek) as Brush : null)
           ?? Brushes.Gray;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        if (oneri.Snippet is not { } snippet)
        {
            // Nesne önerilerinde Ekle = "şema.ad" (sade Text yerine) — sorgu şema-nitelikli yazılır.
            textArea.Document.Replace(completionSegment, oneri.Ekle ?? Text);
            return;
        }

        // Snippet: kısayol yerine GÖVDE girer ve imleç $0 işaretine taşınır. Ofset, metin
        // yerleştirildikten SONRAKİ mutlak konuma çevrilir; işaret yoksa gövdenin sonudur.
        (string metin, int imlecOfseti) = snippet.Coz();
        int baslangic = completionSegment.Offset;

        textArea.Document.Replace(completionSegment, metin);
        textArea.Caret.Offset = Math.Clamp(
            baslangic + imlecOfseti, 0, textArea.Document.TextLength);
    }
}
