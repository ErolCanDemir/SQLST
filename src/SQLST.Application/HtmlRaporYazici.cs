using System.Data;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace SQLST.Application;

/// <summary>
/// Tek sayfalık HTML sonuç raporu (V2-S9, Ö6): sorgu + sunucu/DB + zaman + süre +
/// sonuç tabloları tek dosyada, tema renkleriyle (antrasit + turkuaz — Tema B).
/// "Bak şu sonuca" e-postasının eki olacak kanıt niteliğinde çıktı.
/// </summary>
public static class HtmlRaporYazici
{
    /// <summary>Rapor başına satır tavanı — tarayıcıyı boğmamak için (aşımda dürüst not düşülür).</summary>
    public const int SatirTavani = 1000;

    public static string Yaz(
        string sunucu, string? veritabani, string sql, string sureMetni,
        IReadOnlyList<(string Baslik, DataTable Tablo)> setler, DateTime zaman)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"tr\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>SQLST raporu — {K(veritabani ?? sunucu)}</title>");
        sb.AppendLine("""
            <style>
            body { font-family: 'Segoe UI', sans-serif; margin: 24px; color: #263238; }
            .baslik { background: #263238; color: #fff; padding: 14px 18px; border-radius: 6px 6px 0 0; }
            .baslik h1 { margin: 0; font-size: 18px; }
            .meta { background: #EEF6F5; padding: 10px 18px; font-size: 13px; border-bottom: 2px solid #1B7A70; }
            .meta span { margin-right: 18px; }
            pre.sql { background: #FAFAFA; border: 1px solid #DDD; border-radius: 4px; padding: 12px;
                      font-family: 'Cascadia Mono', Consolas, monospace; font-size: 13px; overflow-x: auto; }
            table { border-collapse: collapse; margin: 10px 0 24px; font-size: 13px; }
            th { background: #1B7A70; color: #fff; padding: 6px 10px; text-align: left; }
            td { border-bottom: 1px solid #E0E0E0; padding: 5px 10px; }
            tr:nth-child(even) td { background: #F7FAFA; }
            .null { color: #999; font-style: italic; }
            .not { color: #8D6E00; background: #FFF8E1; padding: 6px 10px; border-radius: 4px; font-size: 12px; }
            h2 { font-size: 15px; color: #1B7A70; margin-bottom: 2px; }
            .altbilgi { color: #999; font-size: 11px; margin-top: 30px; }
            </style></head><body>
            """);
        sb.AppendLine($"<div class=\"baslik\"><h1>SQLST sorgu raporu</h1></div>");
        sb.AppendLine($"<div class=\"meta\"><span>🖥 {K(sunucu)}</span><span>🗄 {K(veritabani ?? "-")}</span>"
            + $"<span>🕘 {zaman:dd.MM.yyyy HH:mm:ss}</span><span>⏱ {K(sureMetni)}</span></div>");
        sb.AppendLine($"<h2>Sorgu</h2><pre class=\"sql\">{K(sql)}</pre>");

        foreach ((string baslik, DataTable tablo) in setler)
        {
            sb.AppendLine($"<h2>{K(baslik)}</h2>");
            int satirSayisi = Math.Min(tablo.Rows.Count, SatirTavani);
            if (tablo.Rows.Count > SatirTavani)
                sb.AppendLine($"<div class=\"not\">İlk {SatirTavani:N0} satır gösteriliyor (toplam {tablo.Rows.Count:N0}).</div>");

            sb.AppendLine("<table><tr>");
            foreach (DataColumn kolon in tablo.Columns)
                sb.Append($"<th>{K(kolon.ColumnName)}</th>");
            sb.AppendLine("</tr>");
            for (int i = 0; i < satirSayisi; i++)
            {
                sb.Append("<tr>");
                foreach (DataColumn kolon in tablo.Columns)
                {
                    object? deger = tablo.Rows[i][kolon];
                    sb.Append(deger is null or DBNull
                        ? "<td class=\"null\">NULL</td>"
                        : $"<td>{K(HamDeger.Metin(tablo.Rows[i], kolon))}</td>"); // v23-S13: ham
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
        }

        sb.AppendLine($"<div class=\"altbilgi\">SQLST ile üretildi — {DateTime.Now:dd.MM.yyyy HH:mm}</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static Task DosyayaYazAsync(string html, string yol, CancellationToken ct = default)
        => System.IO.File.WriteAllTextAsync(yol, html, new UTF8Encoding(true), ct);

    // WebUtility.HtmlEncode 0xA0-0xFF aralığını &#N; yapar (ü → &#252;) — Türkçe ham kalsın,
    // dosya zaten UTF-8; yalnız <>&"' kaçırılır.
    private static readonly HtmlEncoder Kacirici = HtmlEncoder.Create(UnicodeRanges.All);

    private static string K(string metin) => Kacirici.Encode(metin);
}
