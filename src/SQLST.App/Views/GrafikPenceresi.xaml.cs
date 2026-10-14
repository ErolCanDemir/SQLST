using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SQLST.App.Converters;
using SQLST.App.ViewModels;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>📊 Grafik penceresi kod-arkası (v23-S30): editör ↔ VM SQL eşitlemesi, SQL'i al menüsü, ham hücre gösterimi,
/// grafik → veri tablosu köprüsü, HTML rapor (grafik gömülü).</summary>
public partial class GrafikPenceresi : Window
{
    private readonly GrafikPenceresiViewModel _vm;

    public GrafikPenceresi(GrafikPenceresiViewModel vm, bool koyuTema)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Editor.SyntaxHighlighting = EditorTema.Tanim(koyuTema);
        Editor.Text = vm.Sql;
        Editor.TextChanged += (_, _) => _vm.Sql = Editor.Text;
        Panel.HedefGrid = VeriGrid;
        Closed += (_, _) => _vm.Dispose();
        Loaded += (_, _) => { if (string.IsNullOrWhiteSpace(_vm.Sql)) Editor.Focus(); };
    }

    /// <summary>Hücreler ana grid gibi HAM gösterilir (v23-S13 kuralı: kültürle biçimleme yok).</summary>
    private void VeriGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.Column is DataGridCheckBoxColumn)
            e.Column = new DataGridTextColumn { Header = e.PropertyName };
        if (e.Column is DataGridTextColumn t)
        {
            string? sqlTip = _vm.Tablo?.Columns[e.PropertyName]?.ExtendedProperties[HamDeger.TipAnahtari] as string;
            string yol = e.PropertyName.All(c => char.IsLetterOrDigit(c) || c == '_')
                ? e.PropertyName : "[" + e.PropertyName.Replace("^", "^^").Replace("]", "^]") + "]";
            t.Binding = new Binding(yol) { Converter = HucreMetniConverter.Ornek, ConverterParameter = sqlTip, Mode = BindingMode.OneWay };
        }
        e.Column.Header = e.PropertyName;
        e.Column.MaxWidth = 400;
    }

    private void SqlAl_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SqlAlDugmesi };
        IReadOnlyList<(string Baslik, string Sql)> sekmeler = _vm.SekmeSqlleri();
        if (sekmeler.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(açık sorgu sekmesi yok)", IsEnabled = false });
        foreach ((string baslik, string sql) in sekmeler)
        {
            string kisa = sql.Replace('\n', ' ').Replace('\r', ' ').Trim();
            var oge = new MenuItem { Header = baslik + (kisa.Length > 0 ? "  —  " + (kisa.Length > 60 ? kisa[..60] + "…" : kisa) : "  (boş)"), IsEnabled = kisa.Length > 0 };
            oge.Click += (_, _) => Editor.Text = sql;
            menu.Items.Add(oge);
        }
        menu.IsOpen = true;
    }

    private async void Rapor_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Tablo is not { } tablo)
            return;
        var diyalog = new SaveFileDialog
        {
            Filter = "HTML raporu (*.html)|*.html",
            FileName = $"sqlst-grafik-rapor-{DateTime.Now:yyyyMMdd-HHmmss}.html",
        };
        if (diyalog.ShowDialog(this) != true)
            return;
        var grafikler = new Dictionary<int, string>();
        if (Panel.GoruntuAl() is { } bitmap)
        {
            var kodlayici = new PngBitmapEncoder();
            kodlayici.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            kodlayici.Save(ms);
            grafikler[0] = Convert.ToBase64String(ms.ToArray());
        }
        try
        {
            string html = HtmlRaporYazici.Yaz(_vm.Sunucu, _vm.SeciliDb, _vm.Sql, _vm.SureMetni, [("Sonuç", tablo)], DateTime.Now, grafikler);
            await HtmlRaporYazici.DosyayaYazAsync(html, diyalog.FileName);
            _vm.Durum = $"HTML raporu kaydedildi (grafik gömülü): {Path.GetFileName(diyalog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(this, "SQLST", "Rapor yazılamadı", ex);
        }
    }
}
