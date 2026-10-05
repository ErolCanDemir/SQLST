using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Tuvalde bir join tipinin tek satırı: sol tablonun bir kolonu ↔ sağ tablonun bir kolonu.
/// Kolonlar açılır listeden seçilir; boş seçim üretime dahil edilmez.
/// </summary>
public sealed partial class KolonEsiGorunumu : ObservableObject
{
    [ObservableProperty] private string? _solKolon;
    [ObservableProperty] private string? _sagKolon;

    public bool Dolu => !string.IsNullOrEmpty(SolKolon) && !string.IsNullOrEmpty(SagKolon);
}

/// <summary>
/// İki tablo kutusu arasındaki bağ (v6-S2) = bir JOIN. Sol korunan taraftır (LEFT).
/// Tuvalde bir çizgiyle gösterilir; çizginin uçları kutular taşındıkça güncellenir.
/// Çift tıkla açılan düzenleyicide join tipi ve ON kolonları seçilir.
/// </summary>
public sealed partial class GorselBaglanti : ObservableObject
{
    // Çizgi uçları kutunun BAŞLIK ORTASINA bağlanır (kutu genişliği XAML'de 210, başlık ~24).
    private const double Genislik = 210;
    private const double BaslikYariYuksekligi = 14;

    public GorselBaglanti(GorselSorguKutusu sol, GorselSorguKutusu sag)
    {
        Sol = sol;
        Sag = sag;
        // Kutular taşındıkça çizgi uçları takip etsin.
        Sol.PropertyChanged += (_, _) => UclariBildir();
        Sag.PropertyChanged += (_, _) => UclariBildir();
    }

    public GorselSorguKutusu Sol { get; }
    public GorselSorguKutusu Sag { get; }

    /// <summary>Join tipi (varsayılan INNER). MySQL'de FULL düzenleyicide gizlenir.</summary>
    [ObservableProperty] private JoinTuru _tur = JoinTuru.Inner;

    /// <summary>ON eşleşmesi — satır satır kolon çiftleri (bileşik anahtar → çok satır).</summary>
    public ObservableCollection<KolonEsiGorunumu> Kolonlar { get; } = [];

    /// <summary>Çizgi üstünde kısa özet: "INNER · Id = MusteriId".</summary>
    public string Ozet
    {
        get
        {
            string on = string.Join(", ", Kolonlar.Where(k => k.Dolu).Select(k => $"{k.SolKolon}={k.SagKolon}"));
            return Tur == JoinTuru.Cross || on.Length == 0
                ? Tur.ToString().ToUpperInvariant()
                : $"{Tur.ToString().ToUpperInvariant()} · {on}";
        }
    }

    public void OzetiTazele() => OnPropertyChanged(nameof(Ozet));

    // Çizgi uçları (Canvas koordinatı): iki kutunun başlık ortası.
    public double X1 => Sol.X + Genislik / 2;
    public double Y1 => Sol.Y + BaslikYariYuksekligi;
    public double X2 => Sag.X + Genislik / 2;
    public double Y2 => Sag.Y + BaslikYariYuksekligi;

    /// <summary>Özet etiketi çizginin ortasına konur.</summary>
    public double OrtaX => (X1 + X2) / 2;
    public double OrtaY => (Y1 + Y2) / 2;

    private void UclariBildir()
    {
        OnPropertyChanged(nameof(X1));
        OnPropertyChanged(nameof(Y1));
        OnPropertyChanged(nameof(X2));
        OnPropertyChanged(nameof(Y2));
        OnPropertyChanged(nameof(OrtaX));
        OnPropertyChanged(nameof(OrtaY));
    }

    /// <summary>Üretime hazır <see cref="GorselJoin"/> — yalnız DOLU kolon çiftleri girer.</summary>
    public GorselJoin JoinYap() => new(
        Sol.Nesne, Sag.Nesne, Tur,
        [.. Kolonlar.Where(k => k.Dolu).Select(k => new GorselKolonEsi(k.SolKolon!, k.SagKolon!))]);
}
