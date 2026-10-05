using System.Globalization;
using System.Windows.Data;

namespace SQLST.App.Converters;

/// <summary>
/// Sonuç grid'inin yüksekliğini mevcut alana ORANTILI hesaplar (kullanıcı bulgusu
/// 2026-07-20): eskiden her grid <c>MaxHeight="400"</c> ile sabitti, panel büyütülünce
/// grid 400'de takılıp altta çirkin bir boşluk bırakıyordu.
///
/// Artık her grid, sonuç bölgesinin yüksekliğini result set SAYISINA bölerek pay alır:
/// tek set → tüm alanı doldurur, iki set → yarısını, üçü → üçte birini… "Orantılı büyüme".
/// Yükseklik verilir (MaxHeight değil) ki grid her zaman payını KAPLASIN — az satır varsa
/// grid kendi boş alanıyla dolar, dışarıda karanlık boşluk kalmaz. Grid'e SABİT yükseklik
/// vermek satır sanallaştırmasını da korur (sınırsız yükseklikte DataGrid tüm satırları
/// render edip donardı).
///
/// Bağlama sırası: [0] mevcut yükseklik (ScrollViewer.ActualHeight), [1] set sayısı (int).
/// </summary>
public sealed class OranliYukseklikConverter : IMultiValueConverter
{
    /// <summary>Set başına başlık/düğme satırı + kenar payı (px) — grid alanından düşülür.</summary>
    private const double BasyukPayi = 44;

    /// <summary>Grid asla bundan kısa olmasın (çok sette bile okunur kalsın).</summary>
    private const double AsgariYukseklik = 140;

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double alan || values[1] is not int sayi)
            return AsgariYukseklik;

        // Layout henüz ölçülmediyse (ActualHeight 0) asgariyle başla; ilk ölçümde güncellenir.
        if (alan <= 0 || sayi <= 0)
            return AsgariYukseklik;

        double pay = alan / sayi - BasyukPayi;
        return Math.Max(AsgariYukseklik, pay);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
