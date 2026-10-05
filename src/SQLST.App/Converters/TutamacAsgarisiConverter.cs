using System.Globalization;
using System.Windows.Data;

namespace SQLST.App.Converters;

/// <summary>
/// 🖱 Kaydırıcı tutamağının asgari boyu — RAYA GÖRE uyarlanır.
///
/// NEDEN (kullanıcı bulgusu 26 Ağu 2026: <i>"pencerenin altındaki gridi büyütüp küçülttükçe scroll
/// yok oluyor"</i>): tutamağa SABİT <c>MinHeight=72</c> vermiştim ve "kısa rayda WPF kırpar" diye
/// yazmıştım — <b>yanlıştı, ölçüldü</b>: ray 18 px'e inse bile tutamak 72 px kalıyor, yani raya
/// SIĞMIYOR. Ölçüm (40 kolonlu grid, dış ScrollViewer içinde):
/// <code>
/// gridBoy | rayBoy | tutamacBoy
///     400 |    358 |       72     ✓
///     100 |     58 |       72     ✗ taşıyor
///      60 |     18 |       72     ✗ taşıyor
/// </code>
/// Grid kısaldığında (çok kolonlu sonuçta yatay kaydırıcı da rayın 14 px'ini yer) tutamak rayı
/// aşıyor ve kullanıcı kaydırıcıyı yitiriyor.
///
/// KURAL: <c>min(Tavan, ray × Oran)</c>, en az <c>Taban</c>. Uzun rayda 72 px'e dayanır (asıl istek:
/// "çok verili tabloda tutulabilir olsun"), ray kısaldıkça birlikte küçülür ve HER ZAMAN sığar.
/// Oran 1.0 değil çünkü tutamak rayı tamamen doldurursa kaydıracak yer kalmaz.
/// </summary>
public sealed class TutamacAsgarisiConverter : IValueConverter
{
    /// <summary>
    /// Uzun rayda hedeflenen tutamaç boyu (px).
    ///
    /// ⚠ ÜÇÜNCÜ ARTIRIM (27 Ağu 2026). Kullanıcı üç kez "küçük" dedi: 24 → 36 → 72 → <b>160</b>.
    /// Üçünde de az artırmışım çünkü sayıya PİKSEL olarak bakıyordum; asıl ölçü ORAN: tam ekranda
    /// ray ~658 px olur ve 72 px tutamak rayın yalnız <b>%11</b>'idir — kullanıcının "ufacık bir
    /// nokta" demesinin sebebi bu. 160 px, o rayda ~%24 eder: fareyle rahat hedeflenir ve kaydırma
    /// payı da kalır. Kısa rayda zaten <see cref="Oran"/> devreye girip küçültür.
    /// </summary>
    public const double Tavan = 160;

    /// <summary>Tutamak rayın en fazla bu oranını kaplar; gerisi kaydırma payıdır.</summary>
    public const double Oran = 0.6;

    /// <summary>Bu boyun altına inilmez — altında tutamak fareyle hedeflenemez hâle gelir.</summary>
    public const double Taban = 8;

    /// <summary>Ray uzunluğundan tutamağın asgari boyunu hesaplar (saf; birim testli).</summary>
    public static double Hesapla(double rayUzunlugu)
    {
        if (double.IsNaN(rayUzunlugu) || double.IsInfinity(rayUzunlugu) || rayUzunlugu <= 0)
            return Taban; // ölçüm yapılmadan önce; ilk yerleşimde güncellenir
        return Math.Max(Taban, Math.Min(Tavan, rayUzunlugu * Oran));
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Hesapla(value is double d ? d : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
