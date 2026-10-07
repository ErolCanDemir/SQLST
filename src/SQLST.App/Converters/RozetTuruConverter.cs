using System.Globalization;
using System.Windows.Data;

namespace SQLST.App.Converters;

/// <summary>
/// Durum metni → rozet türü (v23-S21 — mockup'taki sonuç rozetleri tüm ekranlara): "ok" · "hata" · "calis" ·
/// "uyari" · "gri". Tema.xaml'daki <c>SonucRozeti</c> stili Tag'e bakar. Metinler ekranların ZATEN ürettiği
/// Türkçe durumlardır (✔/✖ hata, Boşta/sürüyor, ✓ eşit/≠ farklı, Batch/RPC/Hata/Deadlock…) — yeni sözlük yok,
/// eşleşmeyen nötr gri kalır.
/// </summary>
public sealed class RozetTuruConverter : IValueConverter
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Tur(string? metin)
    {
        string m = (metin ?? "").Trim().ToLower(Tr);
        if (m.Length == 0 || m is "—" or "-" or "?")
            return "gri";
        if (m.Contains("hata") || m.StartsWith('✖') || m.Contains("deadlock") || m.Contains("başarısız"))
            return "hata";
        if (m.Contains("sürüyor") || m.Contains("çalışıyor") || m.Contains("kuruluyor") || m.Contains("işleniyor")
            || m.Contains("güncelleme") || m.Contains("yayılıyor") || m == "batch")
            return "calis";
        if (m.Contains("durdur") || m.Contains("devre dışı") || m.Contains("duraklat") || m.Contains("yavaşlat")
            || m.Contains("dolu —") || m.Contains("disk dolu") || m.Contains("farklı") || m.Contains("yalnız")
            || m.Contains("iptal") || m.Contains("kurtarılıyor") || m.Contains("kapalı"))
            return "uyari";
        if (m.StartsWith('✔') || m.StartsWith('✓') || m.Contains("başarılı") || m.StartsWith("boşta") || m.Contains("eşit"))
            return "ok";
        return "gri";
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Tur(value?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
