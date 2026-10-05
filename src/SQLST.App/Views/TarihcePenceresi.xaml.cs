using System.Windows;
using System.Windows.Controls;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// Nesne tarihçesi (V2-S8, Ö2): sürüm listesi + DiffPlex karşılaştırma + sekmeye aç.
/// 1 sürüm seçilirse bir öncekiyle, 2 sürüm seçilirse ikisi arasında diff gösterilir.
/// </summary>
public partial class TarihcePenceresi : Window
{
    /// <summary>Liste satırı: kayıt + Türkçe zaman + kısa hash.</summary>
    public sealed record SurumSatiri(TarihceKaydi Kayit)
    {
        public string ZamanMetni => Kayit.GorulmeUtc.ToLocalTime().ToString("g");
        public string HashKisa => Kayit.IcerikHash[..12].ToLowerInvariant();
    }

    private readonly IReadOnlyList<SurumSatiri> _surumler;
    private readonly Action<string, string> _sekmeyeAc;
    private readonly string _nesneAdi;

    public TarihcePenceresi(string nesneAdi, IReadOnlyList<TarihceKaydi> kayitlar, Action<string, string> sekmeyeAc)
    {
        InitializeComponent();
        _nesneAdi = nesneAdi;
        _sekmeyeAc = sekmeyeAc;
        _surumler = [.. kayitlar.Select(k => new SurumSatiri(k))];

        Title = $"SQLST — Tarihçe: {nesneAdi}";
        UstBilgi.Text = $"{nesneAdi} — {_surumler.Count} yerel sürüm. "
            + "Dürüst sınır: yalnız BU araçla görülen anlar kaydedilir; veritabanının gerçek denetim günlüğü değildir.";
        Surumler.ItemsSource = _surumler;
        if (_surumler.Count > 0)
            Surumler.SelectedIndex = 0; // son sürüm vs bir önceki
    }

    private void Secim_Degisti(object sender, SelectionChangedEventArgs e)
    {
        List<SurumSatiri> secili = [.. Surumler.SelectedItems.Cast<SurumSatiri>().OrderByDescending(s => s.Kayit.Id)];
        (TarihceKaydi? yeni, TarihceKaydi? eski) = secili switch
        {
            [{ } tek] => (tek.Kayit, BirOnceki(tek)),
            [{ } a, { } b, ..] => (a.Kayit, b.Kayit),
            _ => (null, null),
        };

        if (yeni is null)
            return;
        Diff.OldText = eski?.Tanim ?? "";
        Diff.NewText = yeni.Tanim;
        DiffBaslik.Text = eski is null
            ? $"{Yerel(yeni)} (ilk sürüm — karşılaştırılacak öncesi yok)"
            : $"{Yerel(eski)}  →  {Yerel(yeni)}";

        static string Yerel(TarihceKaydi k) => k.GorulmeUtc.ToLocalTime().ToString("g");
    }

    private TarihceKaydi? BirOnceki(SurumSatiri satir)
    {
        int i = _surumler.ToList().FindIndex(s => s.Kayit.Id == satir.Kayit.Id);
        return i >= 0 && i + 1 < _surumler.Count ? _surumler[i + 1].Kayit : null;
    }

    private void SekmeyeAc_Click(object sender, RoutedEventArgs e)
    {
        if (Surumler.SelectedItems.Cast<SurumSatiri>().OrderByDescending(s => s.Kayit.Id).FirstOrDefault() is not { } secili)
            return;
        _sekmeyeAc($"{_nesneAdi} @{secili.ZamanMetni}",
            NesneScriptleyici.AlterEDonustur(secili.Kayit.Tanim));
        Close();
    }
}
