using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// 🔁 LINQ → SQL penceresi (v20-S11-S12, kullanıcı kararı: "AI olmadan kendi motorumuzu yazalım").
/// Sol: C# LINQ (metod zinciri ya da query syntax) · sağ: KENDİ motorumuzun (<see cref="LinqCevirici"/>)
/// ürettiği lehçeli SQL. Tablolar aktif veritabanının şema önbelleğinden eşlenir; navigation'lar FK
/// grafından JOIN/EXISTS olur. Çevrilemeyen yapı NET hata metniyle gösterilir — sessiz yanlış yok.
/// </summary>
public partial class LinqSqlPenceresi : Window
{
    private readonly Func<Task<LinqBaglam?>> _baglamAl;
    private readonly Func<string, string, Task> _sekmedeAc; // (sql, veritabani)
    private LinqBaglam? _baglam;

    public LinqSqlPenceresi(Func<Task<LinqBaglam?>> baglamAl, Func<string, string, Task> sekmedeAc)
    {
        _baglamAl = baglamAl;
        _sekmedeAc = sekmedeAc;

        InitializeComponent();
        CSharpTemasiUygula(LinqEditor);
        SqlTemasiUygula(SqlEditor);
        LinqEditor.Text = """
            db.Musteriler
              .Where(m => m.Ad.StartsWith("A"))
              .OrderBy(m => m.Ad)
              .Select(m => new { m.Id, m.Ad })
            """;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F5)
                return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                TersCevir_Click(this, new RoutedEventArgs());
            else
                Cevir_Click(this, new RoutedEventArgs());
            e.Handled = true;
        };

        // SQL tarafında ana editör deneyimi (kullanıcı isteği 2026-08-07): yazarken +
        // Ctrl+Space ile şema önerileri (OtoTamamlama — tablo/kolon/anahtar sözcük)
        SqlEditor.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text.Length == 1 && (char.IsLetter(e.Text[0]) || e.Text[0] == '.'))
                TamamlamaGoster();
        };
        SqlEditor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            {
                TamamlamaGoster();
                e.Handled = true;
            }
        };

        Loaded += async (_, _) => await BaglamYukleAsync();
    }

    private async Task BaglamYukleAsync()
    {
        _baglam = await _baglamAl();
        HedefMetni.Text = _baglam is { } b
            ? $"Hedef: {b.Veritabani} · {b.Lehce.MotorId} · {b.Nesneler.Count} nesne, {b.Fkler.Count} FK"
            : "Bağlam yok — SQL motoruna bağlı bir profil ve açık bir sorgu sekmesi gerekir.";
    }

    /// <summary>Koyu temada AvalonEdit'in gömülü C# renkleri (koyu mavi/kızıl) zeminde kaybolur —
    /// koyu eşdeğerlere çevrilir. Tanım uygulama ömrü boyunca tek instance'tır; ışık temada dokunulmaz.</summary>
    private static void CSharpTemasiUygula(TextEditor ed)
    {
        bool koyu = App.KoyuTemaAcik;
        IHighlightingDefinition? tanim = HighlightingManager.Instance.GetDefinition("C#");
        if (koyu && tanim is not null)
        {
            foreach (HighlightingColor renk in tanim.NamedHighlightingColors)
            {
                System.Windows.Media.Color? f = renk.Foreground?.GetColor(null);
                if (f is { } c && (int)c.R + c.G + c.B < 250)
                    renk.Foreground = new SimpleHighlightingBrush(AcikKarsilik(renk.Name));
            }
        }
        ed.SyntaxHighlighting = tanim;
        ZeminUygula(ed, koyu);
    }

    /// <summary>vs-dark paleti: yorum yeşil, string turuncu, anahtar sözcük mavi, kalanı açık mavi.</summary>
    private static System.Windows.Media.Color AcikKarsilik(string? ad) => ad switch
    {
        "Comment" or "XmlDoc" => System.Windows.Media.Color.FromRgb(0x6A, 0x99, 0x55),
        "String" or "Char" => System.Windows.Media.Color.FromRgb(0xCE, 0x91, 0x78),
        "NumberLiteral" => System.Windows.Media.Color.FromRgb(0xB5, 0xCE, 0xA8),
        "MethodCall" => System.Windows.Media.Color.FromRgb(0xDC, 0xDC, 0xAA),
        _ => System.Windows.Media.Color.FromRgb(0x56, 0x9C, 0xD6),
    };

    private static void SqlTemasiUygula(TextEditor ed)
    {
        bool koyu = App.KoyuTemaAcik;
        ed.SyntaxHighlighting = EditorTema.Tanim(koyu);
        ZeminUygula(ed, koyu);
    }

    private static void ZeminUygula(TextEditor ed, bool koyu)
    {
        ed.Background = koyu ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x14, 0x1a)) : Brushes.White;
        ed.Foreground = koyu ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xCD, 0xD6)) : Brushes.Black;
        ed.Options.HighlightCurrentLine = false;
    }

    // ── çeviri ──────────────────────────────────────────────────────────────────────────────

    private async void Cevir_Click(object sender, RoutedEventArgs e)
    {
        if (_baglam is null)
            await BaglamYukleAsync(); // bağlantı sonradan gelmiş olabilir — bir daha dene
        if (_baglam is not { } b)
        {
            DurumMetni.Text = "Çeviri bağlamı yok: SQL motoruna bağlı bir profil ve açık bir sorgu sekmesi gerekir.";
            return;
        }
        if (string.IsNullOrWhiteSpace(LinqEditor.Text))
        {
            DurumMetni.Text = "SAĞDAKİ C# LINQ paneli boş — LINQ'u sağ panele yazın/yapıştırın.";
            return;
        }

        // v20-S21 saha m.8 (canlı bulgu): kullanıcı SQL'i yanlışlıkla LINQ paneline yapıştırıp F5'e
        // bastı → "C# ifadesi ayrıştırılamadı". SQL algılanırsa kibarca doğru yöne taşı/yönlendir.
        if (SqlGibiMi(LinqEditor.Text))
        {
            if (string.IsNullOrWhiteSpace(SqlEditor.Text))
            {
                SqlEditor.Text = LinqEditor.Text;
                LinqEditor.Text = "";
                DurumMetni.Text = "Bu metin SQL görünüyordu — SOL panele taşındı ve LINQ'a çevriliyor…";
                TersCevir_Click(sender, e);
                return;
            }
            DurumMetni.Text = "Bu panel C# LINQ içindir — yapıştırdığınız metin SQL görünüyor; "
                + "SQL'i SOL panele koyup oradaki 'LINQ'a Çevir' düğmesini kullanın.";
            return;
        }

        try
        {
            LinqCeviriSonucu sonuc = LinqCevirici.Cevir(LinqEditor.Text, b.Nesneler, b.Fkler, b.Lehce);
            SqlEditor.Text = sonuc.Sql;
            KopyalaDugmesi.IsEnabled = CalistirDugmesi.IsEnabled = true;
            DurumMetni.Text = sonuc.Uyarilar.Count == 0
                ? $"✔ Çevrildi ({b.Lehce.MotorId}). ▶ ile yeni sekmede çalıştırabilirsiniz."
                : "⚠ " + string.Join("  ·  ", sonuc.Uyarilar);
        }
        catch (LinqCeviriHatasi hata)
        {
            SqlEditor.Text = "";
            KopyalaDugmesi.IsEnabled = CalistirDugmesi.IsEnabled = false;
            DurumMetni.Text = $"✖ {hata.Message}";
        }
    }

    // ── SQL önerileri (v20-S13): ana editörün OtoTamamlama'sı bu pencerede de ──────────────

    private CompletionWindow? _tamamlama;

    private void TamamlamaGoster()
    {
        if (_baglam is not { } b)
            return;
        IReadOnlyList<TamamlamaOnerisi> oneriler = OtoTamamlama.Oner(
            SqlEditor.Text, SqlEditor.CaretOffset, b.Onbellek, out int kelimeBasi, b.Motor);
        if (oneriler.Count == 0 || kelimeBasi > SqlEditor.CaretOffset)
        {
            _tamamlama?.Close();
            return;
        }

        _tamamlama?.Close();
        var pencere = new CompletionWindow(SqlEditor.TextArea) { StartOffset = kelimeBasi, Width = 340 };
        pencere.CompletionList.IsFiltering = true;
        foreach (TamamlamaOnerisi o in oneriler.OrderByDescending(o => o.Oncelik).ThenBy(o => o.Metin))
            pencere.CompletionList.CompletionData.Add(new TamamlamaVerisi(o));

        // Tamamlama penceresi ayrı Window'dur — koyu temada zemin beyaz kalmasın (ana editör deseni)
        if (TryFindResource("PanelZeminFircasi") is Brush zemin) pencere.Background = zemin;
        if (TryFindResource("MetinFircasi") is Brush metin) pencere.Foreground = metin;
        if (TryFindResource("KenarFircasi") is Brush kenar) pencere.BorderBrush = kenar;

        pencere.Closed += (_, _) => _tamamlama = null;
        _tamamlama = pencere;
        pencere.Show();
    }

    /// <summary>Metin SQL mi görünüyor (yanlış panele yapıştırma algısı — v20-S21 saha m.8)?</summary>
    private static bool SqlGibiMi(string metin) =>
        System.Text.RegularExpressions.Regex.IsMatch(metin.TrimStart(),
            @"^(select|with|insert|update|delete|declare|exec)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>➜ SQL → LINQ (v20-S12): ScriptDom AST'sinden query-syntax LINQ — şema gerekmez.</summary>
    private void TersCevir_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SqlEditor.Text))
        {
            DurumMetni.Text = "SOLDAKİ SQL paneli boş — SELECT'i sol panele yazın/yapıştırın.";
            return;
        }
        try
        {
            SqlLinqSonucu sonuc = SqlLinqCevirici.Cevir(SqlEditor.Text);
            LinqEditor.Text = sonuc.Linq;
            KopyalaDugmesi.IsEnabled = CalistirDugmesi.IsEnabled = SqlEditor.Text.Trim().Length > 0;
            DurumMetni.Text = sonuc.Uyarilar.Count == 0
                ? "✔ LINQ'a çevrildi."
                : "⚠ " + string.Join("  ·  ", sonuc.Uyarilar);
        }
        catch (LinqCeviriHatasi hata)
        {
            DurumMetni.Text = $"✖ {hata.Message}";
        }
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(SqlEditor.Text); DurumMetni.Text = "SQL panoya kopyalandı."; }
        catch (Exception ex) { DurumMetni.Text = $"Kopyalanamadı: {ex.Message}"; }
    }

    private async void SekmedeAc_Click(object sender, RoutedEventArgs e)
    {
        if (_baglam is { } b && SqlEditor.Text.Length > 0)
            await _sekmedeAc(SqlEditor.Text, b.Veritabani);
    }
}
