using System.Data;
using ClosedXML.Excel;

namespace SQLST.Application;

/// <summary>
/// Tip koruyan Excel dışa aktarma (V2-S6, FG-4.4): sayı sayı, tarih tarih olarak
/// yazılır — CSV'nin "her şey metin" sınırının panzehiri. ClosedXML 0.105 (MIT,
/// sürüm sabit — 07-r2 §1); thread-safe değildir, her çağrı kendi workbook'unu kurar.
/// </summary>
public static class XlsxYazici
{
    public static void DosyayaYaz(DataTable tablo, string dosyaYolu)
    {
        using var kitap = new XLWorkbook();
        IXLWorksheet sayfa = kitap.Worksheets.Add("Sonuç");

        for (int k = 0; k < tablo.Columns.Count; k++)
        {
            IXLCell hucre = sayfa.Cell(1, k + 1);
            hucre.Value = tablo.Columns[k].ColumnName;
            hucre.Style.Font.Bold = true;
        }

        for (int s = 0; s < tablo.Rows.Count; s++)
        {
            object?[] satir = tablo.Rows[s].ItemArray;
            for (int k = 0; k < satir.Length; k++)
                sayfa.Cell(s + 2, k + 1).Value = HucreDegeri(satir[k]);
        }

        sayfa.SheetView.FreezeRows(1);
        sayfa.Columns().AdjustToContents(1, Math.Min(tablo.Rows.Count + 1, 100)); // genişlik: ilk 100 satıra göre (perf)
        kitap.SaveAs(dosyaYolu);
    }

    /// <summary>ClosedXML senkron çalışır; UI donmasın diye havuz iş parçacığına alınır.</summary>
    public static Task DosyayaYazAsync(DataTable tablo, string dosyaYolu, CancellationToken ct = default)
        => Task.Run(() => DosyayaYaz(tablo, dosyaYolu), ct);

    /// <summary>Tipi koru: sayı/tarih/bool ham gider; binary hex, diğerleri metin.</summary>
    private static XLCellValue HucreDegeri(object? deger) => deger switch
    {
        null or DBNull => Blank.Value,
        bool b => b,
        byte by => by, sbyte sb => sb, short s => s, ushort us => us,
        int i => i, uint ui => ui, long l => l, ulong ul => (double)ul,
        float f => f, double d => d, decimal m => m,
        DateTime dt => dt,
        TimeSpan ts => ts,
        string metin => metin,
        Guid g => g.ToString("D"),
        byte[] b => "0x" + Convert.ToHexString(b),
        _ => deger.ToString() ?? "",
    };
}
