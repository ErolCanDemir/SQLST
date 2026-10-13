using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// 📈 Query Store saf çekirdeği (v23-S28 S1): durum türetmeleri (readonly nedeni, destek), sıralama kolonu, pencere koşulu,
/// gerileyen eşiği, işlem script'leri, ayrıştırma, kısa SQL / ms metni, hata çevirisi. Gerçek LocalDB turu ayrı dosyada.
/// </summary>
public class QueryStoreSorgulariTests
{
    private static ResultSetData Rs(string[] kolonlar, params object?[][] satirlar)
        => new() { Kolonlar = [.. kolonlar.Select(k => new KolonBilgisi(k, "x"))], Satirlar = satirlar };

    [Fact]
    public void Durum_turetmeleri()
    {
        var acik = new QsDurum(16, "READ_WRITE", 0, "AUTO", 1000, 42, 60, 1284, 1412, true, true);
        Assert.True(acik.Destekli); Assert.True(acik.Acik); Assert.Equal("", acik.ReadonlyNedeni);
        var dolu = acik with { Durum = "READ_ONLY", ReadonlyReason = 65536 };
        Assert.True(dolu.SaltOkunur); Assert.Contains("depo dolu", dolu.ReadonlyNedeni);
        Assert.Contains("ikincil", (acik with { ReadonlyReason = 8 }).ReadonlyNedeni);
        Assert.False((acik with { SurumMajor = 12 }).Destekli);
        Assert.True((acik with { Durum = "OFF" }).Kapali);
    }

    [Fact]
    public void Durum_okuma_surum_ve_secenekler()
    {
        var surum = new QueryResult { ResultSetler = [Rs(["surum", "okuma_izni", "alter_izni"], [17, 1, 0])] };
        QsDurum eski = QueryStoreSorgulari.DurumOku(new QueryResult { ResultSetler = [Rs(["surum", "okuma_izni", "alter_izni"], [12, 1, 1])] }, null)!;
        Assert.False(eski.Destekli);
        var durum = new QueryResult { ResultSetler = [Rs(["durum", "readonly_reason", "yakalama", "max_mb", "cur_mb", "aralik_dk", "sorgu_sayisi", "plan_sayisi"],
            ["READ_WRITE", 0, "AUTO", 1000, 42, 60, 1284, 1412])] };
        QsDurum d = QueryStoreSorgulari.DurumOku(surum, durum)!;
        Assert.Equal(17, d.SurumMajor); Assert.True(d.Acik); Assert.Equal(42, d.CurMb); Assert.Equal(1284, d.SorguSayisi);
        Assert.True(d.OkumaIzni); Assert.False(d.AlterIzni);
        Assert.Equal("?", QueryStoreSorgulari.DurumOku(surum, null)!.Durum);
    }

    [Theory]
    [InlineData(QsMetrik.Sure, QsIstatistik.Toplam, "toplam_ms")]
    [InlineData(QsMetrik.Sure, QsIstatistik.Ortalama, "ort_ms")]
    [InlineData(QsMetrik.Sure, QsIstatistik.EnYuksek, "enyuksek_ms")]
    [InlineData(QsMetrik.Cpu, QsIstatistik.Toplam, "cpu_toplam_ms")]
    [InlineData(QsMetrik.Okuma, QsIstatistik.EnYuksek, "okuma_enyuksek")]
    [InlineData(QsMetrik.Yurutme, QsIstatistik.Ortalama, "yurutme")]
    public void Siralama_kolonu(QsMetrik m, QsIstatistik i, string beklenen) => Assert.Equal(beklenen, QueryStoreSorgulari.SiralamaKolonu(m, i));

    [Fact]
    public void Sorgu_metinleri_pencere_ve_siralama_tasir()
    {
        string s = QueryStoreSorgulari.EnPahaliSql(QsMetrik.Cpu, QsIstatistik.Ortalama, QsPencere.Gun7);
        Assert.Contains("DATEADD(hour, -168, SYSDATETIMEOFFSET())", s);
        Assert.EndsWith("ORDER BY cpu_ort_ms DESC;", s.TrimEnd());
        Assert.Contains("TOP (50)", s);
        Assert.DoesNotContain("WHERE i.end_time", QueryStoreSorgulari.EnPahaliSql(QsMetrik.Sure, QsIstatistik.Toplam, QsPencere.Tumu));
        string g = QueryStoreSorgulari.GerilenSql(QsPencere.Saat1);
        Assert.Contains("DATEADD(hour, -1, SYSDATETIMEOFFSET())", g);
        Assert.Contains("son.ort_ms >= once.ort_ms * 1.5 AND son.ort_ms - once.ort_ms >= 10", g);
        Assert.Contains("plan_degisti", g);
        Assert.Contains("is_forced_plan = 1", QueryStoreSorgulari.ZorlanmisSql());
        Assert.Contains("WHERE p.query_id = 412", QueryStoreSorgulari.PlanlarSql(412, QsPencere.Tumu));
        Assert.Equal("EXEC sys.sp_query_store_force_plan @query_id = 412, @plan_id = 1402;", QueryStoreSorgulari.ZorlaSql(412, 1402));
        Assert.Equal("EXEC sys.sp_query_store_unforce_plan @query_id = 412, @plan_id = 1402;", QueryStoreSorgulari.ZorlamaKaldirSql(412, 1402));
        Assert.Equal("ALTER DATABASE [Sa]]tis] SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = AUTO, MAX_STORAGE_SIZE_MB = 1000, INTERVAL_LENGTH_MINUTES = 60);",
            QueryStoreSorgulari.AcSql("Sa]tis"));
        Assert.Contains("plan_id = 7", QueryStoreSorgulari.PlanXmlSql(7));
        Assert.Null(QueryStoreSorgulari.PencereSaat(QsPencere.Tumu));
        Assert.Equal(720, QueryStoreSorgulari.PencereSaat(QsPencere.Gun30));
    }

    [Fact]
    public void Sorgu_ve_plan_ayristirma()
    {
        var rs = Rs(["query_id", "sql_metni", "nesne", "yurutme", "toplam_ms", "ort_ms", "enyuksek_ms", "cpu_toplam_ms", "okuma_toplam", "plan_sayisi", "zorlanmis", "zorlama_hatasi", "son_calisma", "onceki_ort_ms", "plan_degisti"],
            [412L, "SELECT  1\n  FROM x", "dbo.usp_X", 18420L, 1846210.5m, 100.2m, 842m, 90000m, 12345m, 3, 0, 0, new DateTime(2026, 10, 13, 9, 41, 0), 31.4m, 1],
            [87L, "UPDATE y", DBNull.Value, 5L, 10m, 2m, 3m, 1m, 2m, 1, 1, 1, DBNull.Value, DBNull.Value, 0]);
        IReadOnlyList<QsSorgu> s = QueryStoreSorgulari.SorgulariOku(new QueryResult { ResultSetler = [rs] });
        Assert.Equal(2, s.Count);
        Assert.Equal(100.2, s[0].OrtMs, 1); Assert.True(s[0].PlanDegisti); Assert.Equal(31.4, s[0].OncekiOrtMs!.Value, 1);
        Assert.Null(s[1].Nesne); Assert.True(s[1].Zorlanmis); Assert.True(s[1].ZorlamaHatasi); Assert.Null(s[1].OncekiOrtMs); Assert.Null(s[1].SonCalisma);

        var prs = Rs(["plan_id", "yurutme", "ort_ms", "enyuksek_ms", "son_calisma", "zorlanmis", "zorlama_hatasi"],
            [1588L, 6210L, 100.2m, 842m, new DateTime(2026, 10, 13, 9, 41, 0), false, DBNull.Value],
            [1402L, 0L, DBNull.Value, DBNull.Value, DBNull.Value, true, "NO_PLAN"]);
        IReadOnlyList<QsPlan> p = QueryStoreSorgulari.PlanlariOku(new QueryResult { ResultSetler = [prs] });
        Assert.Equal(1588, p[0].PlanId); Assert.False(p[0].Zorlanmis); Assert.Null(p[0].ZorlamaHatasi);
        Assert.True(p[1].Zorlanmis); Assert.Equal("NO_PLAN", p[1].ZorlamaHatasi); Assert.Equal(0, p[1].Yurutme);

        Assert.Equal("<ShowPlanXML/>", QueryStoreSorgulari.PlanXmlOku(new QueryResult { ResultSetler = [Rs(["plan_xml"], ["<ShowPlanXML/>"])] }));
        Assert.Null(QueryStoreSorgulari.PlanXmlOku(new QueryResult { ResultSetler = [Rs(["plan_xml"])] }));
    }

    [Fact]
    public void Metin_yardimcilari_ve_hata()
    {
        Assert.Equal("SELECT 1 FROM x", QueryStoreSorgulari.KisaSql("SELECT  1\n  FROM x"));
        Assert.Equal(140, QueryStoreSorgulari.KisaSql(new string('a', 300)).Length);
        Assert.EndsWith("…", QueryStoreSorgulari.KisaSql(new string('a', 300)));
        Assert.Equal("18,5 sn", QueryStoreSorgulari.MsMetni(18462).Replace('.', ','));
        Assert.Equal("1846 ms", QueryStoreSorgulari.MsMetni(1846.2).Replace(".", "").Replace(",", "")); // binlik ayracı kültüre göre
        Assert.Equal("100 ms", QueryStoreSorgulari.MsMetni(100.2));
        Assert.Equal("1,5 ms", QueryStoreSorgulari.MsMetni(1.49).Replace('.', ','));
        Assert.Equal("0,07 ms", QueryStoreSorgulari.MsMetni(0.07).Replace('.', ','));
        Assert.Contains("VIEW DATABASE STATE", QueryStoreSorgulari.HataMetni(new SqlHata("x", 297, 1, 14)));
        Assert.Contains("desteklemiyor", QueryStoreSorgulari.HataMetni(new SqlHata("x", 208, 1, 16)));
        Assert.Contains("bu sorguya ait değil", QueryStoreSorgulari.HataMetni(new SqlHata("x", 12406, 1, 16)));
        Assert.Contains("son 7 gün", QueryStoreSorgulari.PencereAdi(QsPencere.Gun7));
    }
}
