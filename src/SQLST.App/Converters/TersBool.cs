using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SQLST.App.Converters;

/// <summary>Mesgul=true iken düğmeleri kapatmak için bool tersleyici.</summary>
public sealed class TersBool : IValueConverter
{
    public static readonly TersBool Ornek = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>Asistan mesaj metnini zengin görünüme çevirir (Markdown → WPF; v11-S7 rötuş 2).</summary>
public sealed class MarkdownCevirici : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? MarkdownGorunum.Olustur(s) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible (BoolGorunurluk'un tersi — çip bağlacı ilk çipte gizli, #2).</summary>
public sealed class TersBoolGorunurluk : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
