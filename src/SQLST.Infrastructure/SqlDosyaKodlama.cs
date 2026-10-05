using System.Text;

namespace SQLST.Infrastructure;

/// <summary>Çözülmüş dosya metni: içerik + algılanan kodlama (+ varsa kullanıcıya uyarı).</summary>
public sealed record KodlanmisMetin(string Metin, string KodlamaAdi, string? Uyari);

/// <summary>
/// .sql dosyası kodlama çözümü (v20-S14 "Dosya Aç"): BOM varsa o kazanır (UTF-8/UTF-16);
/// yoksa SIKI UTF-8 denenir, bozuk bayt görülürse Windows-1254'e (Türkçe ANSI — eski SSMS/SSMS
/// script'lerinin varsayılanı) düşülür ve UYARI verilir. Sessiz mojibake yasak: "tanımı" hiçbir
/// yolda "tanÄ±mÄ±" olmaz. Salt bayt→metin — IO çağırandadır, testlenebilir.
/// </summary>
public static class SqlDosyaKodlama
{
    static SqlDosyaKodlama()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // 1254 için

    public static KodlanmisMetin Coz(byte[] bayt)
    {
        // BOM'lar: UTF-8 EF BB BF · UTF-16 LE FF FE · UTF-16 BE FE FF
        if (bayt.Length >= 3 && bayt[0] == 0xEF && bayt[1] == 0xBB && bayt[2] == 0xBF)
            return new(Encoding.UTF8.GetString(bayt, 3, bayt.Length - 3), "UTF-8 (BOM)", null);
        if (bayt.Length >= 2 && bayt[0] == 0xFF && bayt[1] == 0xFE)
            return new(Encoding.Unicode.GetString(bayt, 2, bayt.Length - 2), "UTF-16 LE", null);
        if (bayt.Length >= 2 && bayt[0] == 0xFE && bayt[1] == 0xFF)
            return new(Encoding.BigEndianUnicode.GetString(bayt, 2, bayt.Length - 2), "UTF-16 BE", null);

        try
        {
            var siki = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return new(siki.GetString(bayt), "UTF-8", null);
        }
        catch (DecoderFallbackException)
        {
            // Geçerli UTF-8 değil → Türkçe ANSI en olası aday; yine de kullanıcıya söylenir.
            return new(Encoding.GetEncoding(1254).GetString(bayt), "Windows-1254",
                "Dosya UTF-8 değil — Windows-1254 (Türkçe ANSI) olarak açıldı; karakterler bozuk görünüyorsa kodlamayı dönüştürün.");
        }
    }
}
