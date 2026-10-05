using System.Data;
using System.Text.Json;
using ClosedXML.Excel;
using SQLST.Application;

namespace SQLST.Application.Tests;

public class SecimIstatistikcisiTests
{
    [Fact]
    public void Sayisal_secim_sum_avg_min_max()
    {
        SecimIstatistigi s = SecimIstatistikcisi.Hesapla([10, 20.5m, 30, DBNull.Value, "metin"]);

        Assert.Equal(5, s.Adet);
        Assert.Equal(3, s.SayisalAdet);
        Assert.Equal(60.5m, s.Toplam);
        Assert.Equal(60.5m / 3, s.Ortalama);
        Assert.Equal(10m, s.EnKucuk);
        Assert.Equal(30m, s.EnBuyuk);
        Assert.Contains("SUM", s.Metin);
        Assert.Contains("5 hücre", s.Metin);
    }

    [Fact]
    public void Sayisal_olmayan_secim_yalniz_adet()
    {
        SecimIstatistigi s = SecimIstatistikcisi.Hesapla(["a", "b", DBNull.Value]);

        Assert.Equal(3, s.Adet);
        Assert.Equal(0, s.SayisalAdet);
        Assert.Null(s.Toplam);
        Assert.DoesNotContain("SUM", s.Metin);
    }

    [Fact]
    public void Tek_hucre_istatistik_gurultusu_yapmaz()
        => Assert.Equal("", SecimIstatistikcisi.Hesapla([42]).Metin);

    [Fact]
    public void Decimal_asan_double_atlanir_cokmez()
    {
        SecimIstatistigi s = SecimIstatistikcisi.Hesapla([1, double.MaxValue]);
        Assert.Equal(1, s.SayisalAdet);
        Assert.Equal(1m, s.Toplam);
    }
}

public class GridScriptleyiciTests
{
    private static DataTable Ornek()
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Ad", typeof(string));
        t.Columns.Add("Bakiye", typeof(decimal));
        t.Rows.Add(1, "O'Brien", 10.5m);
        t.Rows.Add(2, DBNull.Value, DBNull.Value);
        return t;
    }

    [Fact]
    public void Insert_olarak_tum_satirlar_tek_insert_coklu_values()
    {
        string script = GridScriptleyici.InsertOlarak(Ornek(), null);

        Assert.Contains("INSERT INTO [Tablo] ([Id], [Ad], [Bakiye]) VALUES", script);
        Assert.Contains("(1, N'O''Brien', 10.5)", script);
        Assert.Contains("(2, NULL, NULL)", script);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "INSERT INTO")); // ≤1000 satır tek INSERT
    }

    [Fact]
    public void Insert_1000_values_sinirinda_bolunur()
    {
        var t = new DataTable();
        t.Columns.Add("X", typeof(int));
        for (int i = 0; i < 1001; i++)
            t.Rows.Add(i);

        string script = GridScriptleyici.InsertOlarak(t, null);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script, "INSERT INTO").Count);
    }

    [Fact]
    public void In_listesi_tekillestirir_null_atlar()
    {
        string s = GridScriptleyici.InListesi([1, 2, 2, DBNull.Value, "a", "a"]);
        Assert.Equal("IN (1, 2, N'a')", s);
    }

    [Fact]
    public void In_listesi_bos_secimde_gecerli_sql()
        => Assert.Equal("IN (NULL)", GridScriptleyici.InListesi([DBNull.Value]));
}

public class XlsxYaziciTests : IDisposable
{
    private readonly string _dosya = Path.Combine(Path.GetTempPath(), $"sqlst-{Guid.NewGuid():N}.xlsx");

    [Fact]
    public void Tipler_korunur_sayi_sayi_tarih_tarih()
    {
        var t = new DataTable();
        t.Columns.Add("Ad", typeof(object));
        t.Columns.Add("Tutar", typeof(object));
        t.Columns.Add("Tarih", typeof(object));
        t.Columns.Add("Bos", typeof(object));
        t.Rows.Add("Ali şğü", 1234.56m, new DateTime(2026, 7, 17, 10, 30, 0), DBNull.Value);

        XlsxYazici.DosyayaYaz(t, _dosya);

        using var kitap = new XLWorkbook(_dosya);
        IXLWorksheet sayfa = kitap.Worksheet(1);
        Assert.Equal("Ad", sayfa.Cell(1, 1).GetString());
        Assert.Equal("Ali şğü", sayfa.Cell(2, 1).GetString());
        Assert.Equal(XLDataType.Number, sayfa.Cell(2, 2).DataType);   // sayı METİN değil
        Assert.Equal(1234.56, sayfa.Cell(2, 2).GetDouble(), 3);
        Assert.Equal(XLDataType.DateTime, sayfa.Cell(2, 3).DataType); // tarih METİN değil
        Assert.True(sayfa.Cell(2, 4).IsEmpty());
    }

    public void Dispose()
    {
        if (File.Exists(_dosya)) File.Delete(_dosya);
    }
}

public class JsonYaziciTests
{
    [Fact]
    public void Tipler_korunur_ve_turkce_kacirilmaz()
    {
        var t = new DataTable();
        t.Columns.Add("Ad", typeof(object));
        t.Columns.Add("Tutar", typeof(object));
        t.Columns.Add("Aktif", typeof(object));
        t.Columns.Add("Bos", typeof(object));
        t.Rows.Add("Gümüş", 12.5m, true, DBNull.Value);

        string json = JsonYazici.Yaz(t);

        using JsonDocument belge = JsonDocument.Parse(json);
        JsonElement ilk = belge.RootElement[0];
        Assert.Equal("Gümüş", ilk.GetProperty("Ad").GetString());
        Assert.Equal(JsonValueKind.Number, ilk.GetProperty("Tutar").ValueKind); // sayı string değil
        Assert.Equal(12.5m, ilk.GetProperty("Tutar").GetDecimal());
        Assert.Equal(JsonValueKind.True, ilk.GetProperty("Aktif").ValueKind);
        Assert.Equal(JsonValueKind.Null, ilk.GetProperty("Bos").ValueKind);
        Assert.Contains("Gümüş", json); // \u ile kaçırılmadı
    }

    [Fact]
    public void Tarih_iso_binary_base64()
    {
        var t = new DataTable();
        t.Columns.Add("Zaman", typeof(object));
        t.Columns.Add("Veri", typeof(object));
        t.Rows.Add(new DateTime(2026, 7, 17, 9, 0, 0), new byte[] { 1, 2 });

        string json = JsonYazici.Yaz(t);

        Assert.Contains("2026-07-17T09:00:00", json);
        Assert.Contains(Convert.ToBase64String(new byte[] { 1, 2 }), json);
    }
}

public class HucreGoruntuleyiciTests
{
    [Fact]
    public void Json_hucre_girintili_bicimlenir()
    {
        (string metin, bool jsonMu) = HucreGoruntuleyici.Bicimlendir("""{"ad":"Ali","yas":3}""");

        Assert.True(jsonMu);
        Assert.Contains("\"ad\": \"Ali\"", metin);
        Assert.Contains('\n', metin); // girintilendi
    }

    [Fact]
    public void Json_dizi_de_bicimlenir()
    {
        (_, bool jsonMu) = HucreGoruntuleyici.Bicimlendir("[1,2,3]");
        Assert.True(jsonMu);
    }

    [Theory]
    [InlineData("düz metin")]
    [InlineData("{bozuk json")]
    public void Json_olmayan_oldugu_gibi(string ham)
    {
        (string metin, bool jsonMu) = HucreGoruntuleyici.Bicimlendir(ham);
        Assert.False(jsonMu);
        Assert.Equal(ham, metin);
    }

    [Fact]
    public void Null_ve_binary_okunur_gosterim()
    {
        Assert.Equal(("NULL", false), HucreGoruntuleyici.Bicimlendir(DBNull.Value));
        Assert.Equal(("0x0102", false), HucreGoruntuleyici.Bicimlendir(new byte[] { 1, 2 }));
    }
}
