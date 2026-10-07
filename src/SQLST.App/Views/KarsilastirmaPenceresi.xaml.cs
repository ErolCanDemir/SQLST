using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// Karşılaştırma araçları (V2-S9): Ö4 sonuç diff'i (anahtar kolonlu, renkli tek grid)
/// + Ö5 şema snapshot/fark. Kaynaklar açık sekmelerin SON sonuçlarıdır; şema
/// karşılaştırması canlı DB'yi ya da JSON snapshot dosyalarını kıyaslar.
/// </summary>
public partial class KarsilastirmaPenceresi : Window
{
    /// <summary>Diff kaynağı: açık bir sekmenin bir result set'i.</summary>
    public sealed record SonucKaynagi(string Gosterim, DataTable Tablo);

    private readonly MainViewModel _vm;
    private readonly ConnectionProfile? _profil;
    private DataTable? _sonFarkTablosu;
    private DataTable? _sonSemaTablosu;
    private SemaSnapshotu? _eskiSnapshot;
    private SemaSnapshotu? _yeniDosyaSnapshot;

    public KarsilastirmaPenceresi(MainViewModel vm, ConnectionProfile? profil)
    {
        InitializeComponent();
        _vm = vm;
        _profil = profil;

        List<SonucKaynagi> kaynaklar = [.. vm.Sekmeler.OfType<SorguSekmesiViewModel>()
            .Where(s => s.SonucSetleri.Count > 0)
            .SelectMany(s => s.SonucSetleri.Select(set =>
                new SonucKaynagi($"{s.Baslik} — {set.Baslik}", set.Tablo)))];
        KaynakA.ItemsSource = kaynaklar;
        KaynakB.ItemsSource = kaynaklar;
        if (kaynaklar.Count >= 2)
        {
            KaynakA.SelectedIndex = 0;
            KaynakB.SelectedIndex = 1;
        }
        else if (kaynaklar.Count == 1)
        {
            KaynakA.SelectedIndex = 0;
        }

        SemaDb.ItemsSource = vm.VeritabaniAdlari;
        if (vm.VeritabaniAdlari.Count > 0)
            SemaDb.SelectedIndex = 0;
    }

    // ---- Ö4 · Sonuç diff'i ----

    /// <summary>Kaynak değişince anahtar aday listesi = iki tarafın ORTAK kolonları.</summary>
    private void Kaynak_Degisti(object sender, SelectionChangedEventArgs e)
    {
        if (KaynakA.SelectedItem is not SonucKaynagi a || KaynakB.SelectedItem is not SonucKaynagi b)
            return;
        AnahtarKolonlar.ItemsSource = a.Tablo.Columns.Cast<DataColumn>().Select(k => k.ColumnName)
            .Intersect(b.Tablo.Columns.Cast<DataColumn>().Select(k => k.ColumnName),
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (AnahtarKolonlar.Items.Count > 0)
            AnahtarKolonlar.SelectedIndex = 0; // ilk kolon çoğunlukla kimliktir (Id)
    }

    private void SonucKarsilastir_Click(object sender, RoutedEventArgs e)
    {
        if (KaynakA.SelectedItem is not SonucKaynagi a || KaynakB.SelectedItem is not SonucKaynagi b)
        {
            SonucOzeti.Text = "İki kaynak seçin (önce sekmelerde sorgu çalıştırın).";
            return;
        }

        List<string> anahtarlar = [.. AnahtarKolonlar.SelectedItems.Cast<string>()];
        (SonucKarsilastirici.KarsilastirmaSonucu? sonuc, string? hata) = SonucKarsilastirici.Karsilastir(
            a.Tablo, b.Tablo, anahtarlar, AynilarDaGorunsun.IsChecked == true);

        if (sonuc is null)
        {
            SonucOzeti.Text = $"⚠ {hata}";
            FarkGrid.ItemsSource = null;
            _sonFarkTablosu = null;
            return;
        }

        _sonFarkTablosu = SonucKarsilastirici.GridTablosuKur(sonuc, anahtarlar);
        FarkGrid.ItemsSource = _sonFarkTablosu.DefaultView;
        SonucOzeti.Text = sonuc.Ozet;
    }

    private async void FarkCsv_Click(object sender, RoutedEventArgs e)
        => await CsvKaydetAsync(_sonFarkTablosu, "sqlst-fark");

    // ---- Ö5 · Şema snapshot + fark ----

    private async void SnapshotKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (await CanliSnapshotAlAsync() is not { } snapshot)
            return;
        var diyalog = new SaveFileDialog
        {
            Filter = "Şema snapshot (*.json)|*.json",
            FileName = $"sema-{snapshot.Veritabani}-{DateTime.Now:yyyyMMdd-HHmmss}.json",
        };
        if (diyalog.ShowDialog(this) != true)
            return;
        try
        {
            await SemaSnapshotYazici.DosyayaYazAsync(snapshot, diyalog.FileName);
            SemaOzeti.Text = $"Snapshot kaydedildi: {Path.GetFileName(diyalog.FileName)} ({snapshot.Nesneler.Count} nesne)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SemaOzeti.Text = $"⚠ Yazılamadı: {ex.Message}";
        }
    }

    private void EskiGozat_Click(object sender, RoutedEventArgs e)
    {
        if (DosyaSec() is not { } yol)
            return;
        _eskiSnapshot = null;
        EskiYol.Text = yol;
        // Placeholder soluk rengi normale döner — sabit Brushes.Black koyu temada görünmezdi (3. tur)
        EskiYol.SetResourceReference(ForegroundProperty, "MetinFircasi");
    }

    private void YeniGozat_Click(object sender, RoutedEventArgs e)
    {
        if (DosyaSec() is not { } yol)
            return;
        _yeniDosyaSnapshot = null;
        YeniDosyaSecenegi.Content = yol;
        YeniKaynak.SelectedIndex = 1;
    }

    private async void SemaKarsilastir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SemaSnapshotu? eski = _eskiSnapshot ??=
                File.Exists(EskiYol.Text) ? await SemaSnapshotYazici.DosyadanOkuAsync(EskiYol.Text) : null;
            if (eski is null)
            {
                SemaOzeti.Text = "⚠ Eski (A) için geçerli bir snapshot dosyası seçin.";
                return;
            }

            SemaSnapshotu? yeni = YeniKaynak.SelectedIndex == 0
                ? await CanliSnapshotAlAsync()
                : _yeniDosyaSnapshot ??= YeniDosyaSecenegi.Content is string yol && File.Exists(yol)
                    ? await SemaSnapshotYazici.DosyadanOkuAsync(yol)
                    : null;
            if (yeni is null)
            {
                SemaOzeti.Text = "⚠ Yeni (B) kaynağı okunamadı.";
                return;
            }

            IReadOnlyList<SemaFarkAlici.Fark> farklar = SemaFarkAlici.Karsilastir(eski, yeni);
            var tablo = new DataTable();
            tablo.Columns.Add("Değişim", typeof(string));
            tablo.Columns.Add("Tür", typeof(string));
            tablo.Columns.Add("Nesne", typeof(string));
            tablo.Columns.Add("Detay", typeof(string));
            foreach (SemaFarkAlici.Fark f in farklar)
            {
                tablo.Rows.Add(f.Tur switch
                {
                    SemaFarkAlici.DegisimTuru.Eklendi => "＋ eklendi",
                    SemaFarkAlici.DegisimTuru.Silindi => "－ silindi",
                    _ => "≠ değişti",
                }, f.NesneTuru.ToString(), f.TamAd, f.Detay);
            }
            _sonSemaTablosu = tablo;
            SemaGrid.ItemsSource = tablo.DefaultView;
            SemaOzeti.Text = farklar.Count == 0
                ? $"Fark yok — şemalar eş ({eski.Nesneler.Count} nesne)."
                : $"{farklar.Count} fark: "
                  + $"eklendi {farklar.Count(f => f.Tur == SemaFarkAlici.DegisimTuru.Eklendi)} · "
                  + $"silindi {farklar.Count(f => f.Tur == SemaFarkAlici.DegisimTuru.Silindi)} · "
                  + $"değişti {farklar.Count(f => f.Tur == SemaFarkAlici.DegisimTuru.Degisti)}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            SemaOzeti.Text = $"⚠ Karşılaştırılamadı: {ex.Message}";
        }
    }

    private async Task<SemaSnapshotu?> CanliSnapshotAlAsync()
    {
        if (_profil is null || SemaDb.SelectedItem is not string db)
        {
            SemaOzeti.Text = "⚠ Bağlantı ve veritabanı seçimi gerekli.";
            return null;
        }
        SemaOzeti.Text = $"{db} şeması okunuyor…";
        SemaOnbellegi? onbellek = await _vm.OnbellekGetirAsync(db);
        if (onbellek is null)
        {
            SemaOzeti.Text = $"⚠ {db} şeması okunamadı.";
            return null;
        }
        return new SemaSnapshotu(_profil.Sunucu, db, DateTime.UtcNow, onbellek.Nesneler);
    }

    private string? DosyaSec()
    {
        var diyalog = new OpenFileDialog { Filter = "Şema snapshot (*.json)|*.json|Tüm dosyalar (*.*)|*.*" };
        return diyalog.ShowDialog(this) == true ? diyalog.FileName : null;
    }

    private async Task CsvKaydetAsync(DataTable? tablo, string dosyaOnEki)
    {
        if (tablo is null)
            return;
        var diyalog = new SaveFileDialog
        {
            Filter = "CSV dosyası (*.csv)|*.csv",
            FileName = $"{dosyaOnEki}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (diyalog.ShowDialog(this) != true)
            return;
        try
        {
            await CsvYazici.DosyayaYazAsync(tablo, diyalog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(this, "SQLST — Karşılaştır", "CSV yazılamadı", ex);
        }
    }

    private async void SemaCsv_Click(object sender, RoutedEventArgs e)
        => await CsvKaydetAsync(_sonSemaTablosu, "sqlst-sema-fark");
}
