using System.Windows;
using System.Windows.Input;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// 🔗 Kolon bağları penceresi (v20-S10): gezginde kolona sağ tık → kolonun TÜM FK bağları —
/// ▲ gidenler (kolonun işaret ettiği) + ▼ gelenler (kolonu işaret edenler). Bilgi penceresi
/// olduğundan KÜÇÜK açılır (kullanıcı kuralı v20-S9); çift tık ilişkiyi JOIN sorgusuyla açar.
/// </summary>
public partial class KolonBaglariPenceresi : Window
{
    private readonly Action<KolonBaglari.Bag>? _joinAc;

    public KolonBaglariPenceresi(
        string kolonYolu, IReadOnlyList<KolonBaglari.Bag> baglar, Action<KolonBaglari.Bag>? joinAc = null)
    {
        InitializeComponent();
        _joinAc = joinAc;
        Baslik.Text = $"🔗 {kolonYolu}";
        Liste.ItemsSource = baglar;
        int giden = baglar.Count(b => b.Giden);
        Ozet.Text = baglar.Count == 0
            ? "Bu kolonun yabancı anahtar bağı yok."
            : $"▲ {giden} giden · ▼ {baglar.Count - giden} gelen";
    }

    private void Liste_CiftTik(object sender, MouseButtonEventArgs e)
    {
        if (Liste.SelectedItem is KolonBaglari.Bag bag)
            _joinAc?.Invoke(bag);
    }
}
