using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SQLST.App.Converters;

/// <summary>
/// Grid hücresi GÖRÜNTÜLEME metni: DBNull → "NULL" (FG-4.8); byte[] → kısaltılmış hex (dev blob
/// grid'i boğmasın); çok satırlı metin → TEK SATIR (kullanıcı bulgusu 2026-07-20).
///
/// <b>Çok satır → tek satır, NEDEN:</b> exception/JSON gibi <c>\n</c> içeren hücreler satırları
/// yükseltip kolonları kaydırıyor, sonuçlar "karışık" görünüyordu ("yan yana yazılmalı"). Satır
/// sonları görünür bir <c>⏎</c> işaretine çevrilir → her hücre tek satır, kolonlar hizalı.
/// TAM içerik korunur: çift tık → hücre görüntüleyici (V2-S6) çok satırlı gösterir; kopyalama/CSV
/// <see cref="Application.SonucBicimleyici"/> üzerinden HAM (satır sonları korunmuş) gider —
/// bu converter yalnız GÖSTERİMİ etkiler.
/// </summary>
public sealed class HucreMetniConverter : IValueConverter
{
    public static readonly HucreMetniConverter Ornek = new();
    private const int AzamiHexBayt = 32;

    /// <summary>Hücrede gösterilecek azami KARAKTER (v20-S21 saha m.9/14/25 ÇÖKME düzeltmesi):
    /// dev metin (MB'lık JSON/log değeri) TextBlock'a olduğu gibi verilince ÖLÇÜM tüm metni işler
    /// (TextTrimming yalnız görüntüyü kırpar) → donma + render OOM = süreç ölümü (AppDomain
    /// işleyicisi OOM'u kurtaramaz). Görüntü için 2000 karakter fazlasıyla yeter; TAM değer çift
    /// tıkta, kopyalamada ve CSV'de aynen korunur (bu converter yalnız GÖSTERİMİ etkiler).</summary>
    private const int AzamiKarakter = 2000;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DBNull => "NULL",
            byte[] b when b.Length > AzamiHexBayt =>
                $"0x{System.Convert.ToHexString(b, 0, AzamiHexBayt)}… ({b.Length:N0} bayt)",
            byte[] b => "0x" + System.Convert.ToHexString(b),
            string s when s.Length > AzamiKarakter =>
                TekSatir(s[..AzamiKarakter]) + $" … ({s.Length:N0} karakter — tamamı için hücreye çift tık)",
            string s => TekSatir(s),
            // v23-S13 (kullanıcı kararı 5 Eki: "hiçbir alan formatlanmasın, db nasıl ise öyle"):
            // tarih/sayı/bit/guid artık WPF bağlamasının tr-TR biçimine BIRAKILMAZ — SSMS'in ham
            // metni (parametre = kolonun SQL tipi; datetime → "2026-10-05 14:23:11.123").
            null => null,
            _ => SQLST.Application.HamDeger.Metin(value, parameter as string),
        };

    /// <summary>Satır sonlarını görünür ⏎ işaretine çevirir (yoksa dokunmaz — tahsis olmasın).</summary>
    private static string TekSatir(string s)
        => s.Contains('\n') || s.Contains('\r')
            ? s.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ").Replace("\r", " ⏎ ")
            : s;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Hücrenin PANO (Ctrl+C) metni — HAM değer (v23-S8, kullanıcı bulgusu 1 Eki 2026: "hücreye
/// tıklayıp Ctrl+C yapınca kopyalayamıyorum, illa açmam gerekiyor"). ClipboardContentBinding
/// verilmeyince WPF pano için GÖRÜNTÜ binding'ini kullanıyordu: uzun hücre panoya
/// "… (N karakter — çift tık)" kırpığıyla, çok satırlı hücre ⏎'li tek satır olarak gidiyordu.
/// Biçim <see cref="SonucBicimleyici.HucreMetni"/> — ⧉ kopyala/CSV ile birebir aynı sözleşme.
/// </summary>
public sealed class PanoHamConverter : IValueConverter
{
    public static readonly PanoHamConverter Ornek = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SQLST.Application.HamDeger.Metin(value, parameter as string); // parametre = SQL tipi

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// ✏ Düzenleme grid'i hücresi — İKİ YÖNLÜ ham dönüşüm (v23-S13, kullanıcı kararı 5 Eki 2026:
/// "hiçbir alan formatlanmasın"). Gösterim <see cref="SQLST.Application.HamDeger"/> (SSMS biçimi);
/// geri yazarken metin kolonun CLR tipine ÖNCE invariant, olmazsa güncel kültürle ayrıştırılır
/// (sayılarda binlik ayraç KAPALI). Neden şart: dönüşüm DataRow'a bırakılsa tr-TR'de "1250.75"
/// SESSİZCE 125075 olurdu ('.' binlik sayılır) — GridTopluIslem yapıştırma dersiyle aynı.
/// DBNull dokunulmadan geçer (NULL görünümü ve Ctrl+0 akışı değişmez).
/// </summary>
public sealed class DuzenlemeHamConverter(Type clrTip, string? sqlTip) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or DBNull or string ? value : SQLST.Application.HamDeger.Metin(value, sqlTip);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0 || clrTip == typeof(string))
            return value; // boş metin eski yoldan (DataRow kuralları) — davranış değişmez
        foreach (CultureInfo k in new[] { CultureInfo.InvariantCulture, CultureInfo.CurrentCulture })
        {
            if (Ayristir(s.Trim(), k) is { } deger)
                return deger;
        }
        // "1.250" invariant'ta 1,25 OKUNUR (nokta = ondalık, SQL'in kendi okuması) — binlik
        // ayracı desteklenmez; kılavuz bunu açıkça söyler.
        return value; // çözülemedi → DataRow doğrulaması eskisi gibi hata gösterir
    }

    private object? Ayristir(string s, CultureInfo k)
    {
        const NumberStyles sayi = NumberStyles.Float; // binlik ayraç bilinçli KAPALI
        if (clrTip == typeof(decimal))
            return decimal.TryParse(s, sayi, k, out decimal m) ? m : null;
        if (clrTip == typeof(double))
            return double.TryParse(s, sayi, k, out double d) ? d : null;
        if (clrTip == typeof(float))
            return float.TryParse(s, sayi, k, out float f) ? f : null;
        if (clrTip == typeof(DateTime))
            return DateTime.TryParse(s, k, DateTimeStyles.None, out DateTime t) ? t : null;
        if (clrTip == typeof(DateTimeOffset))
            return DateTimeOffset.TryParse(s, k, DateTimeStyles.None, out DateTimeOffset o) ? o : null;
        if (clrTip == typeof(TimeSpan))
            return TimeSpan.TryParse(s, k, out TimeSpan ts) ? ts : null;
        if (clrTip == typeof(Guid))
            return Guid.TryParse(s, out Guid g) ? g : null;
        if (clrTip == typeof(byte[]))
        {
            string hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
            try { return System.Convert.FromHexString(hex); }
            catch (FormatException) { return null; }
        }
        try
        {
            return System.Convert.ChangeType(s, clrTip, k); // tamsayılar vb. (binlik ayraç yok)
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>Hücre DBNull mü — soluk/italik stili tetikler (FG-4.8).</summary>
public sealed class DbNullMuConverter : IValueConverter
{
    public static readonly DbNullMuConverter Ornek = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DBNull;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visibility ↔ bool (kolon göster/gizle onay kutusu — 2026-07-28): Visible ⇄ true, Collapsed ⇄ false.
/// Huni menüsündeki CheckBox.IsChecked'i doğrudan <c>DataGridColumn.Visibility</c>'ye TwoWay bağlar;
/// işaret kalkınca kolon gizlenir. Hücre binding'ine dokunmaz (güvenli).
/// </summary>
public sealed class GorunurlukBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>null/boş METİN → Collapsed; doluysa Visible (bilgi bandı gibi isteğe bağlı öğeler için).</summary>
public sealed class NullGizleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Değer null ise Collapsed, doluysa Visible — TİPTEN BAĞIMSIZ (V5-S1).
/// <see cref="NullGizleConverter"/>'dan ayrıdır ve öyle olmak zorundadır: o, değeri
/// <c>as string</c> ile çevirir, dolayısıyla <c>double?</c> gibi metin OLMAYAN bir alan
/// için DAİMA null görür ve öğeyi hiç göstermez (plan ağacındaki "gerçek satır" bu yüzden
/// görünmüyordu — derleme bu hatayı yakalamaz).
/// </summary>
public sealed class DegerDoluysaGorunurConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Log seviye metni → rozet fırçası (v20-S3): Hata/Fatal kırmızı, Uyarı amber, Bilgi/Debug mavi-gri;
/// bilinmeyen/boş nötr gri. Serilog tam ad ve 3-harf kısaltmalarını (ERR/WRN/INF…) kapsar.
/// </summary>
public sealed class SeviyeFircasiConverter : IValueConverter
{
    private static readonly SolidColorBrush Kirmizi = Donuk(0xE0, 0x5A, 0x4F);
    private static readonly SolidColorBrush Amber = Donuk(0xE0, 0xA2, 0x2E);
    private static readonly SolidColorBrush Mavi = Donuk(0x4C, 0x8B, 0xF5);
    private static readonly SolidColorBrush Notr = Donuk(0x8A, 0x8A, 0x8A);

    private static SolidColorBrush Donuk(byte r, byte g, byte b)
    {
        var f = new SolidColorBrush(Color.FromRgb(r, g, b));
        f.Freeze();
        return f;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string s = (value as string)?.Trim() ?? "";
        if (s.Length == 0)
            return Notr;
        if (Basi(s, "err") || Basi(s, "fat") || Basi(s, "crit"))
            return Kirmizi;
        if (Basi(s, "warn") || Basi(s, "wrn") || Basi(s, "uyar"))
            return Amber;
        if (Basi(s, "inf") || Basi(s, "deb") || Basi(s, "dbg") || Basi(s, "verb") || Basi(s, "vrb") || Basi(s, "trace") || Basi(s, "trc") || Basi(s, "bilg"))
            return Mavi;
        return Notr;
    }

    private static bool Basi(string s, string on) => s.StartsWith(on, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Gezginde bir KOLON adı o anki aramayla eşleşiyorsa satırı KALIN yapar (v20-S4 "kolon arama"):
/// values = [kolonAdı, aramaMetni]. Arama boş ya da eşleşmiyorsa normal; eşleşiyorsa Bold — kullanıcı
/// bir tabloyu neden gördüğünü (hangi kolon eşleşti) tek bakışta anlar.
/// </summary>
public sealed class KolonEslesmeKalinlikConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        string ad = values.Length > 0 && values[0] is string a ? a : "";
        string arama = values.Length > 1 && values[1] is string q ? q.Trim() : "";
        return arama.Length > 0 && ad.Contains(arama, StringComparison.OrdinalIgnoreCase)
            ? FontWeights.Bold : FontWeights.Normal;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
