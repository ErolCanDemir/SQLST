using System.Windows;
using SQLST.App.ViewModels;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>Operatör açılır listesi için (değer + okunur etiket).</summary>
public sealed record OperatorSecimi(KosulOperatoru Deger, string Etiket);

/// <summary>
/// Bir tablonun WHERE koşulları düzenleyicisi (v6-S3): satır satır Kolon · Operatör · Değer +
/// bir öncekiyle AND/OR. Koşulları YERİNDE düzenler (DataContext = kutu). "Tamam" kapatır;
/// üretim sırasında yalnız DOLU koşullar kullanılır.
///
/// Bağlanan property'ler (KolonAdlari, OperatorSecenekleri, Baslik) InitializeComponent'ten
/// ÖNCE atanır — düz CLR property + binding zamanlaması (join editor dersi 2026-07-20).
/// </summary>
public partial class GorselWherePenceresi : Window
{
    private readonly GorselSorguKutusu _kutu;

    // internal: huni popover'ı (#2, MainWindow.GorselHuni_Click) aynı listeyi kullanır — tek kaynak.
    internal static readonly OperatorSecimi[] Operatorler =
    [
        new(KosulOperatoru.Esit, "=  (eşittir)"),
        new(KosulOperatoru.Esitsiz, "≠  (eşit değil)"),
        new(KosulOperatoru.Buyuk, ">  (büyük)"),
        new(KosulOperatoru.Kucuk, "<  (küçük)"),
        new(KosulOperatoru.BuyukEsit, "≥  (büyük eşit)"),
        new(KosulOperatoru.KucukEsit, "≤  (küçük eşit)"),
        new(KosulOperatoru.Icerir, "içerir  (LIKE %…%)"),
        new(KosulOperatoru.Baslar, "ile başlar  (LIKE …%)"),
        new(KosulOperatoru.Biter, "ile biter  (LIKE %…)"),
        new(KosulOperatoru.Bos, "boş  (IS NULL)"),
        new(KosulOperatoru.DoluDegil, "dolu  (IS NOT NULL)"),
    ];

    public GorselWherePenceresi(GorselSorguKutusu kutu)
    {
        _kutu = kutu;

        Baslik = $"WHERE:  {kutu.Nesne.Ad}";
        KolonAdlari = [.. kutu.Kolonlar.Select(k => k.Ad)];
        OperatorSecenekleri = Operatorler;
        DataContext = kutu;

        InitializeComponent();
    }

    public string Baslik { get; }
    public IReadOnlyList<string> KolonAdlari { get; }
    public IReadOnlyList<OperatorSecimi> OperatorSecenekleri { get; }

    private void KosulEkle_Click(object sender, RoutedEventArgs e)
        => _kutu.Kosullar.Add(new GorselKosulGorunumu());

    private void KosulSil_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GorselKosulGorunumu ks)
            _kutu.Kosullar.Remove(ks);
    }

    private void Tamam_Click(object sender, RoutedEventArgs e) => Close();
}
