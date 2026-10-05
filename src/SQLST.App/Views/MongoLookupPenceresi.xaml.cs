using System.Windows;
using System.Windows.Controls;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// v20-S21 m.26 fikir 7: $lookup sihirbazı — iki koleksiyon + eşleşen alanlar seçilir, pipeline
/// SAĞDA CANLI oluşur. Alan listeleri şema önbelleğinden gelir (yeni sorgu yok); "Sekmede aç"
/// çalıştırılabilir hâlde açar. Hiçbir şey kendiliğinden çalışmaz.
/// </summary>
public partial class MongoLookupPenceresi : Window
{
    private readonly IReadOnlyList<SemaNesnesi> _koleksiyonlar;
    private readonly Action<string, string> _sekmedeAc; // (başlık, sorgu)

    /// <summary>Koleksiyonun index'lerinde ÖNDE GELEN alanlar (v22-S1 m.13) — hedef alan index'siz
    /// olduğunda $lookup her ana belge için hedef koleksiyonu TARAR (ölçümde 60 sn'de timeout).</summary>
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _indexAlanlari;

    private readonly Dictionary<string, IReadOnlyList<string>> _indexOnbellek = new(StringComparer.OrdinalIgnoreCase);
    private bool _kuruluyor = true;

    public MongoLookupPenceresi(
        IReadOnlyList<SemaNesnesi> koleksiyonlar,
        Action<string, string> sekmedeAc,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? indexAlanlari = null)
    {
        InitializeComponent();
        _koleksiyonlar = [.. koleksiyonlar.Where(k => k.Tur == SemaNesneTuru.Koleksiyon)];
        _sekmedeAc = sekmedeAc;
        _indexAlanlari = indexAlanlari;

        string[] adlar = [.. _koleksiyonlar.Select(k => k.Ad)];
        AnaKoleksiyon.ItemsSource = adlar;
        BaglanacakKoleksiyon.ItemsSource = adlar;
        if (adlar.Length > 0)
            AnaKoleksiyon.SelectedIndex = 0;
        if (adlar.Length > 1)
            BaglanacakKoleksiyon.SelectedIndex = 1;

        _kuruluyor = false;
        // İlk seçimler _kuruluyor bayrağı AÇIKKEN yapıldı → SelectionChanged bastırıldı; alan
        // listeleri elle doldurulmalı (görsel test bunu boş liste olarak yakaladı, 2026-08-14).
        AlanlariTazele(ana: true);
        AlanlariTazele(ana: false);
        Yenile();
    }

    private void Secim_Degisti(object sender, RoutedEventArgs e)
    {
        if (_kuruluyor)
            return;
        // Koleksiyon değişince alan listeleri tazelenir; sonuç alanı kullanıcı dokunmadıysa önerilir.
        if (ReferenceEquals(sender, AnaKoleksiyon) || ReferenceEquals(sender, BaglanacakKoleksiyon))
            AlanlariTazele(ReferenceEquals(sender, AnaKoleksiyon));
        Yenile();
    }

    private void Secim_Degisti(object sender, TextChangedEventArgs e) => Yenile();

    private void AlanlariTazele(bool ana)
    {
        _kuruluyor = true;
        try
        {
            if (ana)
            {
                YerelAlan.ItemsSource = Alanlar(AnaKoleksiyon.SelectedItem as string);
                YerelAlan.SelectedItem = ((IReadOnlyList<string>)YerelAlan.ItemsSource)
                    .FirstOrDefault(a => a.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && a != "_id");
            }
            else
            {
                IReadOnlyList<string> hedefler = Alanlar(BaglanacakKoleksiyon.SelectedItem as string);
                HedefAlan.ItemsSource = hedefler;
                HedefAlan.SelectedItem = hedefler.FirstOrDefault(a => a == "_id") ?? hedefler.FirstOrDefault();
                if (BaglanacakKoleksiyon.SelectedItem is string ad)
                    SonucAlani.Text = MongoLookupSihirbazi.VarsayilanSonucAlani(ad);
            }
        }
        finally
        {
            _kuruluyor = false;
        }
    }

    private IReadOnlyList<string> Alanlar(string? koleksiyon)
        => _koleksiyonlar.FirstOrDefault(k => k.Ad == koleksiyon) is { } k
            ? [.. k.Kolonlar.Select(a => a.Ad)]
            : [];

    private void Yenile()
    {
        if (AnaKoleksiyon.SelectedItem is not string ana || BaglanacakKoleksiyon.SelectedItem is not string bagli)
            return;
        string yerel = (YerelAlan.SelectedItem ?? YerelAlan.Text) as string ?? "";
        string hedef = (HedefAlan.SelectedItem ?? HedefAlan.Text) as string ?? "";
        if (yerel.Length == 0 || hedef.Length == 0 || SonucAlani.Text.Trim().Length == 0)
        {
            Pipeline.Text = "// Koleksiyonları ve eşleşen alanları seçin.";
            return;
        }

        bool duzlestir = Duzlestir.IsChecked == true;
        Eslesmeyenler.IsEnabled = duzlestir; // düzleştirme yoksa LEFT JOIN anlamsız (dizi zaten boş gelir)
        int? onizleme = int.TryParse(Onizleme.Text.Trim(), out int n) && n > 0 ? n : null;
        Pipeline.Text = MongoLookupSihirbazi.PipelineYaz(
            ana, bagli, yerel, hedef, SonucAlani.Text.Trim(), duzlestir,
            duzlestir && Eslesmeyenler.IsChecked == true, onizleme);
        Ipucu.Text = (duzlestir
                ? "$unwind tek eşleşmeyi belgeye indirger (JOIN'e en yakın hâl)."
                : "Düzleştirme kapalı: eşleşenler DİZİ olarak sonuç alanında kalır.")
            + (onizleme is { } o
                ? $"  ·  Sorgu ilk {o:N0} eşleşmede DURUR (keşif önizlemesi) — tamamı için kutuyu 0 yapın."
                : "  ·  ⚠ Sınırsız: dev koleksiyonda çok uzun sürebilir.");
        _ = IndexUyarisiniTazeleAsync(bagli, hedef);
    }

    /// <summary>
    /// $lookup'ın HEDEF alanı index'siz mi? Öyleyse Mongo her ana belge için hedef koleksiyonu baştan
    /// tarar — asıl yavaşlık nedeni budur (v22-S1 m.13 ölçümü: 50k×10k belgede limitsiz sorgu 60 sn
    /// tavanında iptal oldu; index + $limit ile 143 ms). Uyarı, çalıştırılacak createIndex komutunu
    /// da verir. Köprü yoksa/okunamazsa uyarı GÖSTERİLMEZ (yanlış alarm yerine sessizlik).
    /// </summary>
    private async Task IndexUyarisiniTazeleAsync(string koleksiyon, string alan)
    {
        if (_indexAlanlari is null)
            return;
        if (!_indexOnbellek.TryGetValue(koleksiyon, out IReadOnlyList<string>? alanlar))
            _indexOnbellek[koleksiyon] = alanlar = await _indexAlanlari(koleksiyon, CancellationToken.None);

        bool indexli = alan == "_id"
            || alanlar.Any(a => string.Equals(a, alan, StringComparison.OrdinalIgnoreCase));
        IndexUyarisi.Visibility = indexli ? Visibility.Collapsed : Visibility.Visible;
        if (!indexli)
            IndexUyarisi.Text = $"⚡ '{koleksiyon}.{alan}' index'siz — $lookup her ana belge için bu "
                + $"koleksiyonu baştan tarar (dev veride dakikalar). Kalıcı çözüm:\n"
                + $"db.{koleksiyon}.createIndex({{ {alan}: 1 }})";
    }

    private void SekmedeAc_Click(object sender, RoutedEventArgs e)
    {
        if (Pipeline.Text.StartsWith("//", StringComparison.Ordinal))
            return;
        _sekmedeAc($"🔗 {AnaKoleksiyon.SelectedItem}", Pipeline.Text);
        Close();
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        if (!Pipeline.Text.StartsWith("//", StringComparison.Ordinal))
            Clipboard.SetText(Pipeline.Text);
    }
}
