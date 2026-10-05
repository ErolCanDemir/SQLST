using System.Globalization;
using System.Windows.Data;
using SQLST.Contracts;

namespace SQLST.App.Converters;

/// <summary>Nesne türü → tek aileden simge (03 §5: tutarlı simge seti).</summary>
public sealed class TurSimgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SemaNesneTuru tur
            ? tur switch
            {
                SemaNesneTuru.Tablo => "▦",
                SemaNesneTuru.View => "◫",
                SemaNesneTuru.StoredProcedure => "⚙",
                SemaNesneTuru.Fonksiyon => "ƒ",
                SemaNesneTuru.Koleksiyon => "⛁",
                SemaNesneTuru.Index => "🔑",
                _ => "•",
            }
            : "•";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Not: TurRenkConverter kaldırıldı (3. tur, 2026-07-18) — fırçayı bir kez çözüp dondurduğu
// için canlı tema takasında eski palette kalıyordu; renkler artık MainWindow'daki
// DataTrigger'lı stille DynamicResource üzerinden verilir (03 §5 eşlemesi orada).
