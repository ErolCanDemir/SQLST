using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SQLST.Contracts;

namespace SQLST.App.Converters;

/// <summary>
/// Sağ tık menüsünde öğeyi yalnız uygun nesne türlerinde gösterir.
/// ConverterParameter virgüllü tür listesidir, ör. "Tablo,View".
/// </summary>
public sealed class TurGorunurConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SemaNesneTuru tur || parameter is not string liste)
            return Visibility.Collapsed;

        foreach (string parca in liste.Split(','))
        {
            if (parca.Trim().Equals(tur.ToString(), StringComparison.OrdinalIgnoreCase))
                return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
