using System.Windows;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// "Yanıt ↔ DB karşılaştır" penceresi (v20-S13): REST JSON yanıtından çıkarılmış tablo (kolon+satır) ile
/// kullanıcının girdiği bir SQL sorgusunun sonucunu ANAHTAR KOLONDA karşılaştırır (<see cref="YanitDbKarsilastirici"/>).
/// Her anahtar: yalnız API / yalnız DB / eşit / farklı (+ hangi kolonlar). DB tarafı <c>dbSorgu</c>
/// delegesiyle aktif bağlantıda çalıştırılır. Salt bilgi; hata net mesajla gösterilir, çökme yok.
/// </summary>
public partial class RestKarsilastirPenceresi : Window
{
    private readonly IReadOnlyList<string> _apiKolonlar;
    private readonly IReadOnlyList<object?[]> _apiSatirlar;
    private readonly Func<string, Task<(IReadOnlyList<string> Kolonlar, IReadOnlyList<object?[]> Satirlar)>> _dbSorgu;

    public RestKarsilastirPenceresi(
        DosyaOnizleme apiOnizleme,
        Func<string, Task<(IReadOnlyList<string> Kolonlar, IReadOnlyList<object?[]> Satirlar)>> dbSorgu)
    {
        InitializeComponent();
        _apiKolonlar = [.. apiOnizleme.Kolonlar.Select(k => k.Ad)];
        _apiSatirlar = apiOnizleme.Satirlar;
        _dbSorgu = dbSorgu;

        ApiBilgi.Text = $"API yanıtı: {_apiSatirlar.Count} satır · kolonlar: {string.Join(", ", _apiKolonlar)}";
        // Makul varsayılan anahtar: "id" varsa onu, yoksa ilk kolonu öner (kullanıcı değiştirir).
        AnahtarKutusu.Text = _apiKolonlar.FirstOrDefault(k => k.Equals("id", StringComparison.OrdinalIgnoreCase))
            ?? _apiKolonlar.FirstOrDefault() ?? "";
    }

    private async void Karsilastir_Click(object sender, RoutedEventArgs e)
    {
        string sql = SqlKutusu.Text?.Trim() ?? "";
        string anahtar = AnahtarKutusu.Text?.Trim() ?? "";
        if (sql.Length == 0)
        {
            Ozet.Text = "DB tarafı için bir SQL girin.";
            return;
        }
        if (anahtar.Length == 0)
        {
            Ozet.Text = "Anahtar kolon girin (API ve DB'de ortak).";
            return;
        }

        KarsilastirDugmesi.IsEnabled = false;
        Ozet.Text = "DB sorgusu çalıştırılıyor…";
        try
        {
            (IReadOnlyList<string> dbKolonlar, IReadOnlyList<object?[]> dbSatirlar) = await _dbSorgu(sql);
            KarsilastirmaSonucu sonuc = YanitDbKarsilastirici.Karsilastir(
                _apiKolonlar, _apiSatirlar, dbKolonlar, dbSatirlar, anahtar);

            if (sonuc.Hata is { } hata)
            {
                Ozet.Text = $"⚠ {hata}";
                SonucGrid.ItemsSource = null;
                return;
            }

            SonucGrid.ItemsSource = sonuc.Satirlar.Select(GorunumeCevir).ToList();
            Ozet.Text = $"{sonuc.Satirlar.Count} anahtar · ✓ {sonuc.Esit} eşit · ≠ {sonuc.Farkli} farklı · "
                + $"◀ {sonuc.YalnizApi} yalnız API · ▶ {sonuc.YalnizDb} yalnız DB · ortak kolon: "
                + (sonuc.OrtakKolonlar.Count > 0 ? string.Join(", ", sonuc.OrtakKolonlar) : "(yok)");
        }
        catch (Exception ex)
        {
            Ozet.Text = $"⚠ DB sorgusu hatası: {ex.Message}";
            SonucGrid.ItemsSource = null;
        }
        finally { KarsilastirDugmesi.IsEnabled = true; }
    }

    private static RestKarsilastirmaGorunum GorunumeCevir(KarsilastirmaSatiri s) => new(
        s.Anahtar,
        s.Durum switch
        {
            KarsilastirmaDurumu.YalnizApi => "◀ yalnız API",
            KarsilastirmaDurumu.YalnizDb => "▶ yalnız DB",
            KarsilastirmaDurumu.Esit => "✓ eşit",
            _ => "≠ farklı",
        },
        string.Join(", ", s.FarkliKolonlar),
        s.Durum != KarsilastirmaDurumu.Esit);
}

/// <summary>Karşılaştırma grid satırı (görünüm): <see cref="Dikkat"/> eşit-olmayanları renklendirir.</summary>
public sealed record RestKarsilastirmaGorunum(string Anahtar, string Durum, string Farklar, bool Dikkat);
