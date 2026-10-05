using System.Data;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SQLST.Application;

/// <summary>
/// Tip koruyan JSON dışa aktarma (V2-S6, FG-4.4): nesne dizisi; sayı sayı, bool bool,
/// tarih ISO 8601, binary base64, NULL null. Türkçe karakterler kaçırılmadan yazılır.
/// </summary>
public static class JsonYazici
{
    private static readonly JsonWriterOptions YaziciAyari = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "ş" ş olmasın — dosya UTF-8
    };

    public static string Yaz(DataTable tablo)
    {
        using var bellek = new MemoryStream();
        using (var yazici = new Utf8JsonWriter(bellek, YaziciAyari))
        {
            yazici.WriteStartArray();
            foreach (DataRow satir in tablo.Rows)
            {
                yazici.WriteStartObject();
                for (int k = 0; k < tablo.Columns.Count; k++)
                {
                    yazici.WritePropertyName(tablo.Columns[k].ColumnName);
                    DegerYaz(yazici, satir[k]);
                }
                yazici.WriteEndObject();
            }
            yazici.WriteEndArray();
        }
        return Encoding.UTF8.GetString(bellek.ToArray());
    }

    public static async Task DosyayaYazAsync(DataTable tablo, string dosyaYolu, CancellationToken ct = default)
        => await File.WriteAllTextAsync(dosyaYolu, Yaz(tablo), new UTF8Encoding(false), ct); // JSON'da BOM olmaz (RFC 8259)

    private static void DegerYaz(Utf8JsonWriter yazici, object? deger)
    {
        switch (deger)
        {
            case null or DBNull: yazici.WriteNullValue(); break;
            case bool b: yazici.WriteBooleanValue(b); break;
            case byte or sbyte or short or ushort or int or uint or long:
                yazici.WriteNumberValue(Convert.ToInt64(deger)); break;
            case ulong ul: yazici.WriteNumberValue(ul); break;
            // NaN/Infinity JSON'da sayı olamaz — WriteNumberValue fırlatıp TÜM dışa aktarmayı
            // düşürüyordu (inceleme 2026-07-30; Mongo double alanlarında meşru değer). Metin yazılır.
            case float f when !float.IsFinite(f): yazici.WriteStringValue(f.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
            case double d when !double.IsFinite(d): yazici.WriteStringValue(d.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
            case float f: yazici.WriteNumberValue(f); break;
            case double d: yazici.WriteNumberValue(d); break;
            case decimal m: yazici.WriteNumberValue(m); break;
            case DateTime dt: yazici.WriteStringValue(dt.ToString("O")); break;
            case DateTimeOffset dto: yazici.WriteStringValue(dto.ToString("O")); break;
            case TimeSpan ts: yazici.WriteStringValue(ts.ToString("c")); break;
            case Guid g: yazici.WriteStringValue(g); break;
            case byte[] b: yazici.WriteBase64StringValue(b); break;
            default: yazici.WriteStringValue(deger.ToString()); break;
        }
    }
}
