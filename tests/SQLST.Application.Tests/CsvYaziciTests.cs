using System.Data;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

public class CsvYaziciTests
{
    private static DataTable OrnekTablo() => SonucBicimleyici.TabloyaCevir(new ResultSetData
    {
        Kolonlar = [new("Ad", "nvarchar"), new("Not", "nvarchar")],
        Satirlar =
        [
            ["Ali", "düz değer"],
            ["Nokta;Virgül", "çift \"tırnak\" var"],
            ["çok\nsatır", DBNull.Value],
        ],
    }).Tablo;

    [Fact]
    public void Ayirici_tirnak_ve_satir_sonu_rfc4180_ile_kacirilir()
    {
        string csv = CsvYazici.Metin(OrnekTablo());

        Assert.StartsWith("Ad;Not", csv);
        Assert.Contains("\"Nokta;Virgül\"", csv);              // ayırıcı içeren → tırnaklı
        Assert.Contains("\"çift \"\"tırnak\"\" var\"", csv);   // içteki tırnak ikilenir
        Assert.Contains("\"çok\nsatır\"", csv);                // satır sonu içeren → tırnaklı
    }

    [Fact]
    public void Null_hucre_bos_yazilir()
    {
        string csv = CsvYazici.Metin(OrnekTablo());
        // son satır: "çok\nsatır";<boş>
        Assert.EndsWith(";" + Environment.NewLine, csv.Replace("\r\n", Environment.NewLine).TrimEnd('\n', '\r') + Environment.NewLine);
    }

    // --- Formül enjeksiyonu koruması (inceleme 2026-07-30 bekleyeni) ---

    [Theory]
    [InlineData("=1+2", "'=1+2")]                       // formül → ' öneki
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("=cmd|'/c calc'!A0", "'=cmd|'/c calc'!A0")]     // DDE saldırısı etkisizleşir (tek ' RFC tırnağı gerektirmez)
    [InlineData("+kayit|zarar", "'+kayit|zarar")]        // + ile başlayan sayı-olmayan → önek
    [InlineData("-5", "-5")]                             // negatif SAYI dokunulmaz
    [InlineData("+3.14", "+3.14")]                       // işaretli ondalık dokunulmaz (invariant)
    [InlineData("-2,5", "-2,5")]                         // tr ondalık da sayı sayılır
    [InlineData("düz metin", "düz metin")]
    public void Formul_baslangicli_metin_tirnak_onekiyle_korunur(string girdi, string beklenen)
    {
        DataTable t = SonucBicimleyici.TabloyaCevir(new ResultSetData
        {
            Kolonlar = [new("Deger", "nvarchar")],
            Satirlar = [[girdi]],
        }).Tablo;

        string satir = CsvYazici.Metin(t).Replace("\r\n", "\n").Split('\n')[1];
        Assert.Equal(beklenen, satir);
    }

    [Fact]
    public async Task Dosyaya_utf8_bom_ile_yazar()
    {
        string yol = Path.Combine(Path.GetTempPath(), $"sqlst-csv-{Guid.NewGuid():N}.csv");
        try
        {
            await CsvYazici.DosyayaYazAsync(OrnekTablo(), yol);
            byte[] bytes = await File.ReadAllBytesAsync(yol);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // BOM — Excel Türkçe karakter için
            Assert.Contains("düz değer", File.ReadAllText(yol));
        }
        finally
        {
            if (File.Exists(yol)) File.Delete(yol);
        }
    }
}
