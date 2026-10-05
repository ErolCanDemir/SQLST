using System.Windows;
using SQLST.App.ViewModels;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// Görsel Sorgu Tasarımcısı'nda bir bağın (JOIN) düzenleyicisi (v6-S2): join tipi + ON kolon
/// çiftleri. Bağı YERİNDE düzenler (DataContext = bağ); "Tamam" değişiklikleri saklar, "Bağı
/// sil" bağı kaldırma isteğini <see cref="Silindi"/> ile bildirir (çağıran uygular).
///
/// Kolon açılır listeleri iki tablonun kolon adlarıdır. FULL OUTER JOIN, MySQL'de listeye
/// KONMAZ (motor desteklemez — "yarım özellik yok").
/// </summary>
public partial class GorselJoinPenceresi : Window
{
    private readonly GorselBaglanti _bag;

    public GorselJoinPenceresi(GorselBaglanti bag, bool mysql)
    {
        _bag = bag;

        // ÖNEMLİ: bağlanan property'ler InitializeComponent'ten ÖNCE atanır. Bunlar düz CLR
        // property (değişiklik bildirimi yok); XAML binding'i çözülünce değer HAZIR olmalı,
        // yoksa ComboBox'lar boş kalır ("açılmıyor" bulgusu 2026-07-20).
        Baslik = $"JOIN:  {bag.Sol.Nesne.Ad}  ↔  {bag.Sag.Nesne.Ad}";
        SolKolonAdlari = [.. bag.Sol.Kolonlar.Select(k => k.Ad)];
        SagKolonAdlari = [.. bag.Sag.Kolonlar.Select(k => k.Ad)];
        JoinTurleri = mysql
            ? [JoinTuru.Inner, JoinTuru.Left, JoinTuru.Right]   // MySQL'de FULL yok
            : [JoinTuru.Inner, JoinTuru.Left, JoinTuru.Right, JoinTuru.Full];
        DataContext = bag;

        InitializeComponent();
    }

    /// <summary>Başlık metni (XAML bağlar).</summary>
    public string Baslik { get; }

    public IReadOnlyList<string> SolKolonAdlari { get; }
    public IReadOnlyList<string> SagKolonAdlari { get; }
    public IReadOnlyList<JoinTuru> JoinTurleri { get; }

    /// <summary>Kullanıcı "Bağı sil" dediyse true — çağıran bağı tuvalden kaldırır.</summary>
    public bool Silindi { get; private set; }

    private void KolonCiftEkle_Click(object sender, RoutedEventArgs e)
        => _bag.Kolonlar.Add(new KolonEsiGorunumu());

    private void KolonCiftSil_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KolonEsiGorunumu es)
            _bag.Kolonlar.Remove(es);
    }

    private void Sil_Click(object sender, RoutedEventArgs e)
    {
        Silindi = true;
        Close();
    }

    private void Tamam_Click(object sender, RoutedEventArgs e) => Close();
}
