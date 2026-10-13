using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SQLST.App.Views;

namespace SQLST.App.Converters;

/// <summary>
/// Enum ↔ bool (v23-S24 grafik tür çipleri): IsChecked = (değer == parametre). Geri yönde yalnız "true"
/// enum'a çevrilir; "false" (çipin kendisi tıklanıp bırakılması) kaynağa DOKUNMAZ — RadioButton'da zaten olmaz.
/// </summary>
public sealed class EnumEsit : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

/// <summary>Seri sırası (0..) → paletin ikon rengi (9 renk döner). Açıklama kutusu ve Y çipleri tuvalle AYNI rengi alır.</summary>
public sealed class SeriRengi : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? GrafikTuvali.SeriFircasi(i) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Gizli açıklama öğesi soluk (S2: seri/dilim gizlendi).</summary>
public sealed class GizliOpaklik : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.35 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null/boş metin → Collapsed (uyarı bandı yalnız uyarı varken).</summary>
public sealed class MetinVarsaGorunur : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
