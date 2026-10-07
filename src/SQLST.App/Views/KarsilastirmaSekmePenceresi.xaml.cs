using System.IO;
using System.Windows;
using Microsoft.Win32;
using SQLST.App.ViewModels;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// 🔀 Karşılaştırma penceresi (kullanıcı isteği 2026-08-09: sekme yerine AYRI PENCERE — SOAP
/// istemcisi gibi bağımsız). İçeriği <see cref="KarsilastirmaSekmesiViewModel"/> sürer; eşitleme
/// script'i ayrı kopyala/kaydet penceresinde açılır (hedef ayrı bağlantı olabilir → sekmede AÇILMAZ).
/// </summary>
public partial class KarsilastirmaSekmePenceresi : Window
{
    private readonly KarsilastirmaSekmesiViewModel _vm;

    public KarsilastirmaSekmePenceresi(KarsilastirmaSekmesiViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        // Script SEKMEDE AÇILMAZ (sekme aktif profile bağlı, hedef ayrı bağlantı olabilir) —
        // kopyala/kaydet penceresi gösterilir.
        vm.ScriptGoster ??= (baslik, script) =>
            new EsitlemeScriptPenceresi(baslik, script) { Owner = this }.Show();

        // Sol (aktif profil) veritabanlarını doldur (pencere Loaded karşılığı).
        Loaded += async (_, _) =>
        {
            try { await vm.YukleAsync(); }
            catch (Exception ex) { vm.Bilgi = $"Yüklenemedi: {ex.Message}"; }
        };
    }

    /// <summary>Sağ bağlantı parolası (PasswordBox bağlanamaz) → VM'ye elle taşınır.</summary>
    private void SagParola_Changed(object sender, RoutedEventArgs e)
        => _vm.SagParola = SagParolaKutusu.Password;

    // ── 🔀 Şema eşitleme ──────────────────────────────────────────────────────
    /// <summary>🔀 Şema eşitleme: ☑ işaretli şema farkları için DDL script'i (v19-S12).</summary>
    private async void SecilenleriSemaEsitle_Click(object sender, RoutedEventArgs e)
        => await _vm.SemaEsitleAsync([.. _vm.Farklar.Where(f => f.Secili)]);

    private async void TumSemayiEsitle_Click(object sender, RoutedEventArgs e)
        => await _vm.TumSemayiEsitleAsync();

    // ── 🔀 Veri eşitleme (madde 4, 2026-08-03) ────────────────────────────────
    /// <summary>Fark grid'inde ☑ İŞARETLİ satırlar için eşitleme script'i.</summary>
    private async void SecilenleriEsitle_Click(object sender, RoutedEventArgs e)
        => await _vm.EsitleAsync([.. _vm.VeriFarklari.Where(f => f.Secili)]);

    private async void TumunuEsitle_Click(object sender, RoutedEventArgs e)
        => await _vm.TumunuEsitleAsync();

    /// <summary>Veri karşılaştırma satır farklarını (Yön · Anahtar) CSV'ye aktarır (v7-S3).</summary>
    private async void VeriFarkCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.VeriFarklari.Count == 0)
            return;

        var tablo = new System.Data.DataTable();
        tablo.Columns.Add("Yön", typeof(string));
        tablo.Columns.Add("Anahtar", typeof(string));
        tablo.Columns.Add("Kolon farkı", typeof(string));
        foreach (VeriFarkGorunumu f in _vm.VeriFarklari)
            tablo.Rows.Add(f.Yon, f.Anahtar, f.Detay);

        var diyalog = new SaveFileDialog
        {
            Filter = "CSV dosyası (*.csv)|*.csv",
            FileName = $"veri-fark-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (diyalog.ShowDialog(this) != true)
            return;
        try
        {
            await CsvYazici.DosyayaYazAsync(tablo, diyalog.FileName);
            _vm.VeriBilgi = $"Fark CSV'ye yazıldı: {Path.GetFileName(diyalog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(this, "SQLST — Karşılaştır", "CSV yazılamadı", ex);
        }
    }
}
