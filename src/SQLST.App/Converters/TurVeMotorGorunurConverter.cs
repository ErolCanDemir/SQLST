using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SQLST.Contracts;

namespace SQLST.App.Converters;

/// <summary>
/// Menü öğesi görünürlüğü: NESNE TÜRÜ **ve** MOTOR birlikte süzer (V3 denetimi 2026-07-18).
///
/// Neden gerekli: T-SQL script üreten öğeler (SELECT script'i, EXEC iskeleti, ALTER,
/// DROP+CREATE, CREATE TABLE, Düzenle) yalnız SQL Server'da doğru çalışır. Diğer motorlarda
/// ya anında söz dizimi hatası verirdi ya da — daha kötüsü — ALTER dönüşümü sessizce
/// hiçbir şey yapmayan bir script üretirdi (kullanıcı güncellediğini sanırdı).
///
/// Bağlama sırası: [0] SemaNesneTuru, [1] MotorMssqlMu (bool).
/// ConverterParameter: görünür olunacak tür listesi, ör. "Tablo,View".
/// </summary>
public sealed class TurVeMotorGorunurConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not SemaNesneTuru tur || parameter is not string liste)
            return Visibility.Collapsed;

        // Motor bayrağı henüz bağlanmadıysa (bağlantı öncesi) gizli kal — yanlış menü gösterme.
        if (values[1] is not bool mssqlMu || !mssqlMu)
            return Visibility.Collapsed;

        foreach (string parca in liste.Split(','))
        {
            if (parca.Trim().Equals(tur.ToString(), StringComparison.OrdinalIgnoreCase))
                return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
