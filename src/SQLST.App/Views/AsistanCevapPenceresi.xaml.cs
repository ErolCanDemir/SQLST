using System.Windows;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// Ortak AI cevap penceresi (v11-S7, 2026-07-25): bağlam-yüzeyi düğmeleri (AI Değerlendir /
/// AI Açıkla / AI Hatayı çözdür / AI Planı yorumlat) sonucu BURADA gösterir — asistan sekmesi
/// açılmaz. Cevapta sorgu bulunursa "Sekmede aç" belirir (çalıştırmaz).
/// </summary>
public partial class AsistanCevapPenceresi : Window
{
    private readonly Action<string, string>? _sekmeyeAc;
    private string? _sorgu;

    public AsistanCevapPenceresi(
        string baslik, Task<AsistanCevabi?> cevapGorevi, Action<string, string>? sekmeyeAc = null)
    {
        _sekmeyeAc = sekmeyeAc;
        InitializeComponent();
        Title = $"SQLST — {baslik}";
        DurumMetni.Text = $"{baslik} — model düşünüyor…";

        // async void Loaded sınırı: hata pencereyi düşürmesin; null görev = "girdi yok" mesajı.
        Loaded += async (_, _) =>
        {
            try
            {
                AsistanCevabi? cevap = await cevapGorevi;
                if (cevap is null)
                {
                    DurumMetni.Text = "Girdi bulunamadı.";
                    CevapAlani.Content = MarkdownGorunum.Olustur("Bu eylem için gerekli içerik yok — ör. editörde sorgu ya da çözülecek bir hata bulunmalı.");
                    return;
                }

                CevapAlani.Content = MarkdownGorunum.Olustur(cevap.Metin);
                DurumMetni.Text = cevap switch
                {
                    { LimitAsildi: true } => "⏳ Ücretsiz katman limiti — birkaç saniye sonra tekrar deneyin.",
                    { Basarili: false } => "⚠ Cevap alınamadı.",
                    _ => baslik,
                };
                _sorgu = cevap.Basarili ? AsistanIstemleri.CevaptanSorguAyikla(cevap.Metin) : null;
                if (_sorgu is not null && _sekmeyeAc is not null)
                    SekmedeAcDugmesi.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                DurumMetni.Text = "⚠ Beklenmeyen hata.";
                CevapAlani.Content = MarkdownGorunum.Olustur(ex.Message);
            }
        };
    }

    private void SekmedeAc_Click(object sender, RoutedEventArgs e)
    {
        if (_sorgu is not null && _sekmeyeAc is not null)
        {
            _sekmeyeAc("asistan.sql", _sorgu);
            Close();
        }
    }

    private void Kapat_Click(object sender, RoutedEventArgs e) => Close();
}
