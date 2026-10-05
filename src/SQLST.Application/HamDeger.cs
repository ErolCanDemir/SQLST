using System.Data;
using System.Globalization;

namespace SQLST.Application;

/// <summary>
/// 🧾 Veritabanı değerinin HAM metni — SSMS'in gösterdiği biçim, kültürden BAĞIMSIZ
/// (v23-S13 — kullanıcı kararı 5 Eki 2026: <i>"hiçbir alanı formatlama, db nasıl ise öyle
/// kalsın — MSSQL'de tarih '-'li, bizde '.'lı; bunun için rezil oluyorum"</i>).
///
/// FOG-9 (Türkçe biçim) arayüzün KENDİ metinleri için geçerli kalır (süre, sayaç, durum);
/// VERİTABANINDAN GELEN HİÇBİR DEĞER kültürle biçimlenmez. Grid gösterimi, Ctrl+C, ⧉ kopyala,
/// CSV, HTML rapor ve hücre görüntüleyici hepsi bu TEK noktadan geçer — yüzeyler arası fark
/// kalmaz. SQL tipi biliniyorsa (<paramref name="sqlTip"/>) SSMS'le BİREBİR (datetime → .fff,
/// datetime2(3) → .fff, smalldatetime → saniyesiz kesir yok, money → 4 hane, bit → 1/0,
/// uniqueidentifier → BÜYÜK harf); bilinmiyorsa (akışlı dışa aktarma, Mongo) yine ISO ve
/// invariant — asla "05.10.2026" ya da "1250,75" üretilmez.
/// </summary>
public static class HamDeger
{
    /// <summary>DataColumn.ExtendedProperties anahtarı — SQL tipi tabloyla birlikte taşınır;
    /// böylece DataTable alan HER yüzey (CSV/pano/rapor) tipe göre ham metin üretir.</summary>
    public const string TipAnahtari = "sqlst.sqlTip";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Değerin ham metni; NULL → "NULL" (SSMS sözleşmesi).</summary>
    public static string Metin(object? deger, string? sqlTip = null)
    {
        (string taban, int? olcek) = TipCoz(sqlTip);
        return deger switch
        {
            null or DBNull => "NULL",
            string s => s,
            bool b => taban is "bool" or "boolean" ? (b ? "true" : "false") : (b ? "1" : "0"),
            byte[] b => "0x" + Convert.ToHexString(b),
            DateTime t => TarihMetni(t, taban, olcek),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss" + Kesir(olcek ?? 7) + " zzz", Inv),
            TimeSpan ts => SaatMetni(ts, taban, olcek),
            Guid g => taban == "uniqueidentifier" ? g.ToString().ToUpperInvariant() : g.ToString(),
            decimal m when taban is "money" or "smallmoney" => m.ToString("0.0000", Inv),
            double d => d.ToString("R", Inv),
            float f => f.ToString("R", Inv),
            IFormattable f => f.ToString(null, Inv),
            _ => Convert.ToString(deger, Inv) ?? "",
        };
    }

    /// <summary>Satırdaki hücrenin ham metni — kolonun taşıdığı SQL tipiyle.</summary>
    public static string Metin(DataRow satir, DataColumn kolon)
        => Metin(satir[kolon], kolon.ExtendedProperties[TipAnahtari] as string);

    /// <summary>
    /// Gösterim tipi: tarih/saat ailesinde kesir ölçeği ada eklenir — "datetime2(3)". Ölçek,
    /// SSMS'in kaç kesir hanesi gösterdiğini belirler (datetime2(0) kesirsiz, varsayılan 7).
    /// </summary>
    public static string GorunumTipi(string tipAdi, int? olcek)
        => olcek is { } o and >= 0 and <= 7
           && tipAdi.ToLowerInvariant() is "datetime2" or "time" or "datetimeoffset"
            ? $"{tipAdi}({o})"
            : tipAdi;

    private static (string Taban, int? Olcek) TipCoz(string? sqlTip)
    {
        if (string.IsNullOrWhiteSpace(sqlTip))
            return ("", null);
        string t = sqlTip.Trim().ToLowerInvariant();
        int ac = t.IndexOf('(');
        if (ac < 0)
            return (t, null);
        int kapa = t.IndexOf(')', ac);
        string ic = kapa > ac ? t[(ac + 1)..kapa] : "";
        return (t[..ac].Trim(), int.TryParse(ic, NumberStyles.None, Inv, out int o) ? o : null);
    }

    private static string Kesir(int hane) => hane <= 0 ? "" : "." + new string('f', Math.Min(hane, 7));

    private static string TarihMetni(DateTime t, string taban, int? olcek) => taban switch
    {
        // MSSQL date daima saatsizdir; Oracle DATE saat taşır → saat varsa gösterilir.
        "date" when t.TimeOfDay == TimeSpan.Zero => t.ToString("yyyy-MM-dd", Inv),
        "datetime" => t.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv),
        "smalldatetime" => t.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        "datetime2" => t.ToString("yyyy-MM-dd HH:mm:ss" + Kesir(olcek ?? 7), Inv),
        // Tip bilinmiyor / diğer motorlar: ISO + kesir YALNIZ varsa (sıfırlar atılır) — PG/MySQL
        // araçlarının gösterdiği biçim; kültürsüz ve kayıpsız.
        _ => t.ToString("yyyy-MM-dd HH:mm:ss", Inv) + KesirVarsa(t.Ticks % TimeSpan.TicksPerSecond),
    };

    private static string SaatMetni(TimeSpan ts, string taban, int? olcek)
        => taban == "time" && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1)
            ? new DateTime(ts.Ticks).ToString("HH:mm:ss" + Kesir(olcek ?? 7), Inv)
            : ts.ToString("c", Inv); // MySQL TIME 24 saati aşabilir/negatif olabilir — sabit biçim

    private static string KesirVarsa(long tick)
        => tick == 0 ? "" : "." + tick.ToString("0000000", Inv).TrimEnd('0');
}
