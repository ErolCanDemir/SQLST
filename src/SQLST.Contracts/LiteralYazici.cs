using System.Globalization;

namespace SQLST.Contracts;

/// <summary>İkili (binary) verinin motor literali biçimi.</summary>
public enum LiteralIkilik
{
    /// <summary>T-SQL: <c>0x0AFF</c></summary>
    OnEkli0x,
    /// <summary>PostgreSQL: <c>'\x0AFF'::bytea</c></summary>
    PgBytea,
    /// <summary>MySQL/MariaDB: <c>X'0AFF'</c></summary>
    XTirnak,
    /// <summary>Oracle: <c>HEXTORAW('0AFF')</c></summary>
    HexToRaw,
}

/// <summary>Tarih/saat değerlerinin motor literali biçimi.</summary>
public enum LiteralZaman
{
    /// <summary>T-SQL / PostgreSQL: <c>'2026-07-18T14:30:05.1234567'</c> (ISO, 'T' ayraçlı).</summary>
    IsoT,
    /// <summary>MySQL/MariaDB: <c>'2026-07-18 14:30:05.123456'</c> (boşluk ayraçlı, en çok 6 hane).</summary>
    IsoBosluk,
    /// <summary>Oracle: örtük dönüşüm yok — <c>TO_DATE</c>/<c>TO_TIMESTAMP</c> ile açık yazılır.</summary>
    OracleDonusum,
}

/// <summary>
/// Bir motorun literal yazım kuralları. <see cref="LiteralYazici"/> tek bir algoritmayı
/// bu kurallara göre uygular — motor başına dört ayrı yazıcı çoğaltmak yerine farkı VERİ
/// olarak tutarız (V4-S1).
/// </summary>
/// <param name="UnicodeOneki">Metin <c>N'…'</c> ile mi yazılır (yalnız T-SQL).</param>
/// <param name="TersBoleniKacir">
/// <c>\</c> karakteri kaçış sayılıyor mu — MySQL/MariaDB'de VARSAYILAN olarak evettir
/// (<c>NO_BACKSLASH_ESCAPES</c> kapalı), bu yüzden ikilenmelidir. Atlanırsa
/// <c>'C:\yol'</c> gibi bir değer sessizce bozulur.
/// </param>
public sealed record LiteralKurallari(
    bool UnicodeOneki,
    bool TersBoleniKacir,
    string Dogru,
    string Yanlis,
    LiteralIkilik Ikilik,
    LiteralZaman Zaman)
{
    public static readonly LiteralKurallari TSql =
        new(UnicodeOneki: true, TersBoleniKacir: false, "1", "0", LiteralIkilik.OnEkli0x, LiteralZaman.IsoT);

    /// <summary>PostgreSQL: gerçek <c>boolean</c> tipi vardır → TRUE/FALSE.</summary>
    public static readonly LiteralKurallari Postgres =
        new(UnicodeOneki: false, TersBoleniKacir: false, "TRUE", "FALSE", LiteralIkilik.PgBytea, LiteralZaman.IsoT);

    /// <summary>MySQL/MariaDB: BOOLEAN = TINYINT(1) → 1/0; ters bölen kaçışı açıktır.</summary>
    public static readonly LiteralKurallari MySql =
        new(UnicodeOneki: false, TersBoleniKacir: true, "1", "0", LiteralIkilik.XTirnak, LiteralZaman.IsoBosluk);

    /// <summary>Oracle: BOOLEAN sütun tipi yoktur (NUMBER(1) yaygın) → 1/0.</summary>
    public static readonly LiteralKurallari Oracle =
        new(UnicodeOneki: false, TersBoleniKacir: false, "1", "0", LiteralIkilik.HexToRaw, LiteralZaman.OracleDonusum);
}

/// <summary>
/// .NET değerini güvenli SQL literaline çevirir (V2-S5; 07-r2 §4 KRİTİK kuralları):
/// metin tırnak ikilemeli; sayı/tarih INVARIANT (kültüre bağlı ToString YASAK — "3,14"
/// felaketi); binary ve tarih motorun biçiminde; NULL tırnaksız. Bilinmeyen tip sessizce
/// ToString'lenmez — açık hata (yanlış veri yazmaktansa hiç yazma).
///
/// V4-S1'de motor farkındalığı kazandı: <see cref="LiteralKurallari"/> ile parametriktir.
/// Contracts'ta durur çünkü hem Application (DML üretimi) hem Infrastructure (lehçeler)
/// kullanır — bağımlılık yönü <c>Application → Contracts ← Infrastructure</c>.
/// </summary>
public static class LiteralYazici
{
    /// <summary>Geriye uyumlu T-SQL literali (MSSQL'e özgü grid script'leri bunu kullanır).</summary>
    public static string Yaz(object? deger) => Yaz(deger, LiteralKurallari.TSql);

    public static string Yaz(object? deger, LiteralKurallari k) => deger switch
    {
        null or DBNull => "NULL",
        string s => Metin(s, k),
        char c => Metin(c.ToString(), k),
        bool b => b ? k.Dogru : k.Yanlis,
        byte[] b => Ikili(b, k),
        Guid g => $"'{g:D}'",
        DateTime dt => Zaman(dt, k),
        DateTimeOffset dto => ZamanDilimli(dto, k),
        TimeSpan ts => Sure(ts, k),
        DateOnly d => Tarih(d, k),
        TimeOnly t => Sure(t.ToTimeSpan(), k),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        byte or sbyte or short or ushort or int or uint or long or ulong
            => ((IFormattable)deger).ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(
            $"'{deger.GetType().Name}' tipi için güvenli SQL literali üretilemiyor — bu kolon elle sorguyla güncellenmeli."),
    };

    private static string Metin(string s, LiteralKurallari k)
    {
        string govde = s.Replace("'", "''");
        if (k.TersBoleniKacir)
            govde = govde.Replace("\\", "\\\\");
        return (k.UnicodeOneki ? "N'" : "'") + govde + "'";
    }

    private static string Ikili(byte[] b, LiteralKurallari k)
    {
        string hex = Convert.ToHexString(b);
        return k.Ikilik switch
        {
            LiteralIkilik.OnEkli0x => b.Length == 0 ? "0x00" : "0x" + hex,
            LiteralIkilik.PgBytea => $@"'\x{hex}'::bytea",          // boş dizi de geçerli: '\x'::bytea
            LiteralIkilik.XTirnak => b.Length == 0 ? "X''" : $"X'{hex}'",
            // HEXTORAW('') Oracle'da NULL'a düşüp "geçersiz hex" hatası verir — sessiz yanlış
            // yerine açık ret (07-r2 §4: yanlış veri yazmaktansa hiç yazma).
            LiteralIkilik.HexToRaw when b.Length == 0 => throw new NotSupportedException(
                "Oracle'da boş RAW/BLOB literali yoktur — bu kolonu NULL bırakın ya da elle sorguyla güncelleyin."),
            LiteralIkilik.HexToRaw => $"HEXTORAW('{hex}')",
            _ => throw new NotSupportedException($"Bilinmeyen ikili biçimi: {k.Ikilik}"),
        };
    }

    private static string Zaman(DateTime dt, LiteralKurallari k) => k.Zaman switch
    {
        // date kolonları sade kalsın; saatli değerler tam çözünürlükte
        LiteralZaman.IsoT => dt.TimeOfDay == TimeSpan.Zero
            ? $"'{dt:yyyy-MM-dd}'"
            : $"'{dt:yyyy-MM-ddTHH:mm:ss.fffffff}'",
        // MySQL kesir hanesi en çok 6'dır (DATETIME(6)); 7 hane yazmak hata verir
        LiteralZaman.IsoBosluk => dt.TimeOfDay == TimeSpan.Zero
            ? $"'{dt:yyyy-MM-dd}'"
            : $"'{dt:yyyy-MM-dd HH:mm:ss.ffffff}'",
        LiteralZaman.OracleDonusum => dt.TimeOfDay == TimeSpan.Zero
            ? $"TO_DATE('{dt:yyyy-MM-dd}', 'YYYY-MM-DD')"
            : $"TO_TIMESTAMP('{dt:yyyy-MM-dd HH:mm:ss.fffffff}', 'YYYY-MM-DD HH24:MI:SS.FF7')",
        _ => throw new NotSupportedException($"Bilinmeyen zaman biçimi: {k.Zaman}"),
    };

    private static string Tarih(DateOnly d, LiteralKurallari k) => k.Zaman == LiteralZaman.OracleDonusum
        ? $"TO_DATE('{d:yyyy-MM-dd}', 'YYYY-MM-DD')"
        : $"'{d:yyyy-MM-dd}'";

    private static string ZamanDilimli(DateTimeOffset dto, LiteralKurallari k) => k.Zaman switch
    {
        LiteralZaman.OracleDonusum =>
            $"TO_TIMESTAMP_TZ('{dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)}', "
          + "'YYYY-MM-DD HH24:MI:SS.FF7 TZH:TZM')",
        LiteralZaman.IsoBosluk =>
            $"'{dto.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'", // MySQL'de offset saklanmaz
        _ => $"'{dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)}'",
    };

    private static string Sure(TimeSpan ts, LiteralKurallari k) => k.Zaman switch
    {
        // Oracle'da TIME tipi yoktur; INTERVAL DAY TO SECOND karşılığı açıkça yazılır
        LiteralZaman.OracleDonusum =>
            $"TO_DSINTERVAL('{(int)ts.TotalDays} {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}')",
        _ => $"'{ts.ToString("c", CultureInfo.InvariantCulture)}'",
    };
}
