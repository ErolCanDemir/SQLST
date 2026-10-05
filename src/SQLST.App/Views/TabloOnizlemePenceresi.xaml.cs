using System.Windows;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// 👁 Tablo önizleme penceresi (v22-S3, saha turu-3 m.1 — kullanıcı: "tablo önizleme özelliğimiz var,
/// bunu Ctrl ile tablonun üzerine gelince yapıyoruz ya, tabloya sağ tıklayınca da koyalım").
///
/// Editördeki Ctrl+hover ipucunun menü karşılığı: kolon özeti (🔑 PK · 🔗 FK · tip) + ilk 5 satır.
/// İçerik AYNI saf sınıftan (<see cref="TabloOnizleme"/>) üretilir — iki yüzey ayrı kod yazmaz.
/// Satırlar çağıranın verdiği delege ile gelir (önbellek/koruma orada); MongoDB'de satır getirilmez,
/// kolon listesi yine görünür (Ctrl+hover'daki davranışın aynısı).
/// </summary>
public partial class TabloOnizlemePenceresi : Window
{
    private readonly SemaNesnesi _nesne;
    private readonly Action<SemaNesnesi>? _sekmedeAc;

    public TabloOnizlemePenceresi(
        SemaNesnesi nesne,
        Func<CancellationToken, Task<(ResultSetData? Veri, string? Hata)>>? satirGetir,
        Action<SemaNesnesi>? sekmedeAc = null)
    {
        InitializeComponent();
        _nesne = nesne;
        _sekmedeAc = sekmedeAc;

        Basligi.Text = $"📄 {nesne.TamAd} — {nesne.Kolonlar.Count} kolon";
        Kolonlar.Text = TabloOnizleme.KolonOzeti(nesne, azami: 40);
        Veri.Text = satirGetir is null
            ? "(ilk satırlar yalnız SQL motorlarında)"
            : "ilk satırlar alınıyor…";

        if (satirGetir is not null)
            Loaded += async (_, _) => await YukleAsync(satirGetir);
    }

    private async Task YukleAsync(Func<CancellationToken, Task<(ResultSetData? Veri, string? Hata)>> satirGetir)
    {
        (ResultSetData? veri, string? hata) = await satirGetir(CancellationToken.None);
        Veri.Text = veri is null
            ? $"(ilk satırlar alınamadı: {hata})"
            : TabloOnizleme.MiniTablo(veri, azamiKolon: 12, azamiHucre: 24);
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
        => Clipboard.SetText($"{Basligi.Text}\n\n{Kolonlar.Text}\n\n{Veri.Text}");

    private void SekmedeAc_Click(object sender, RoutedEventArgs e)
    {
        _sekmedeAc?.Invoke(_nesne);
        Close();
    }
}
