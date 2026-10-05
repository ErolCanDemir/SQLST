using System.Globalization;
using System.Windows.Data;

namespace SQLST.App.Converters;

/// <summary>
/// 🖱 Kaydırıcı tutamağına ASGARİ BOY dayatmanın ÇALIŞAN yolu — <c>Track.ViewportSize</c> şişirme.
///
/// TARİHÇE (27 Ağu 2026, piksel kanıtı): tutamağa <c>MinHeight</c> vermek İŞE YARAMIYOR.
/// <c>Track</c>, tutamağı kendi hesapladığı doğal orana göre YERLEŞTİRİR ve MinHeight'ı umursamaz —
/// eleman şişer (<c>ActualHeight=59</c> ölçülür!) ama yerleştirme dikdörtgenine KIRPILIR ve ekrana
/// yalnız doğal boyu çizilir. 10.000 satırda doğal oran %0,03 → ~4 px → kullanıcının dört kez
/// bildirdiği "ufacık nokta". 24→36→72→160 artırımlarının HİÇBİRİ bu yüzden görünmedi; benim tüm
/// ölçümlerim <c>Thumb.ActualHeight</c> okuduğu için (şişmiş ama kırpılmış eleman) hep "doğru"
/// gördü — ekrana bakan kullanıcı haklıydı. Kanıt: render PNG'sinde tutamak renginin (#3A3640)
/// yalnız y=694–699 arasında olması, 702'den itibaren ray zemini (#15161A).
///
/// MEKANİZMA: Track tutamak boyunu <c>L×V/(V+R)</c> hesaplar (L=ray, V=viewport, R=Max−Min).
/// Boy T'den küçük kalacaksa V şişirilir: <c>V' = R×T/(L−T)</c> → Track'in kendi hesabı tam T verir.
/// Yani Track'e karşı savaşmak yerine ONUN girdisi değiştirilir — kırpma olmaz, gerçekten çizilir.
///
/// Bedeli (bilinçli): ViewportSize aynı zamanda ray-tıklamasında sayfa adımıdır; şişirme sayfa
/// adımını da büyütür. Küçük tutamaklı (çok satırlı) durumda bu fark kullanıcı lehinedir.
/// </summary>
public sealed class TutamacViewportConverter : IMultiValueConverter
{
    /// <summary>
    /// [0]=ViewportSize · [1]=Maximum · [2]=Minimum · [3]=ray uzunluğu (ScrollBar ActualHeight/Width).
    /// Hedef boy <see cref="TutamacAsgarisiConverter.Hesapla"/>'dan gelir — tek matematik, iki mekanizma değil.
    /// </summary>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [double v, double max, double min, double l])
            return Binding.DoNothing; // ölçüm henüz yok — Track kendi değeriyle kalsın

        double r = max - min;
        if (r <= 0 || v <= 0 || l <= 0)
            return v; // kaydırılacak şey yok / henüz yerleşmedi — dokunma

        double t = TutamacAsgarisiConverter.Hesapla(l);
        if (t >= l)
            return v; // ray hedeften kısa — dayatılamaz (Hesapla oranla küçülttüğünden pratikte olmaz)

        double dogal = l * v / (v + r);
        return dogal >= t ? v : r * t / (l - t);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
