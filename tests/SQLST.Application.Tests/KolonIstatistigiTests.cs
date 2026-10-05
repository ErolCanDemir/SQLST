using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.10 fikir 5 — kolon istatistiği: sorgu sıfırdan yazılmaz, çalışan sorgunun FROM+WHERE'i
/// ORİJİNAL metinden kesilir (süzgeç/alias/hint korunur), yalnız SELECT listesi değişir.
/// Sayısal kolonda AVG/SUM eklenir (CAST'li — int AVG'nin kesme ve SUM'ın taşma tuzağı),
/// gruplu/UNION'lu sorguda özellik kapanır, TOP'lu sorguda not düşülür.
/// </summary>
public class KolonIstatistigiTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static (string? Sql, string? Not, string? Hata) Yaz(string sql, bool sayisal = false, string kolon = "Tutar")
        => KolonIstatistigi.SorguYaz(sql, kolon, sayisal, Mssql);

    [Fact]
    public void Süzgec_ve_kaynak_orijinalinden_korunur()
    {
        (string? sql, _, string? hata) = Yaz("SELECT t.* FROM dbo.Talep t WITH(NOLOCK) WHERE t.Yil = 2026 ORDER BY t.Id");

        Assert.Null(hata);
        Assert.Contains("FROM dbo.Talep t WITH(NOLOCK) WHERE t.Yil = 2026", sql);
        Assert.DoesNotContain("ORDER BY", sql);            // sıralama istatistikte anlamsız — alınmaz
        Assert.Contains("COUNT_BIG(*)", sql);
        Assert.Contains("COUNT_BIG(DISTINCT [Tutar])", sql);
        Assert.Contains("MIN([Tutar])", sql);
        Assert.Contains("MAX([Tutar])", sql);
    }

    [Fact]
    public void Sayisal_kolonda_ortalama_ve_toplam_castli_gelir()
    {
        (string? sayisal, _, _) = Yaz("SELECT * FROM T", sayisal: true);
        Assert.Contains("AVG(CAST([Tutar] AS decimal(38,6)))", sayisal);
        Assert.Contains("SUM(CAST([Tutar] AS decimal(38,6)))", sayisal);

        (string? metin, _, _) = Yaz("SELECT * FROM T");   // metin/tarih kolonunda anlamsız → yok
        Assert.DoesNotContain("AVG(", metin);
        Assert.DoesNotContain("SUM(", metin);
    }

    [Fact]
    public void Top_varsa_not_dusulur() // sayı TOP'suz hesaplanır — kullanıcı yanılmasın
    {
        (string? sql, string? not, _) = Yaz("SELECT TOP (200) * FROM dbo.Talep");
        Assert.NotNull(sql);
        Assert.Contains("TÜM satırlar", not);
    }

    [Theory]
    [InlineData("SELECT Sehir, COUNT(*) FROM T GROUP BY Sehir", "GROUP BY")]
    [InlineData("SELECT * FROM A UNION SELECT * FROM B", "UNION")]
    [InlineData("SELECT 1; SELECT 2", "TEK bir SELECT")]
    [InlineData("SELECT 42", "FROM yok")]
    public void Desteklenmeyen_sorguda_net_hata(string sql, string mesajParcasi)
    {
        (string? uretilen, _, string? hata) = Yaz(sql);
        Assert.Null(uretilen);
        Assert.Contains(mesajParcasi, hata);
    }

    [Fact]
    public void Bicimle_null_sayisini_turetir_ve_dolu_kolonunu_gizler()
    {
        var veri = new ResultSetData
        {
            Kolonlar =
            [
                new KolonBilgisi("satır", "bigint"), new KolonBilgisi("dolu", "bigint"),
                new KolonBilgisi("farklı", "bigint"), new KolonBilgisi("en küçük", "decimal"),
                new KolonBilgisi("en büyük", "decimal"),
            ],
            Satirlar = [[100L, 93L, 12L, 99.0m, 8400.5m]],
        };

        IReadOnlyList<(string Etiket, string Deger)> satirlar = KolonIstatistigi.Bicimle(veri);

        Assert.DoesNotContain(satirlar, s => s.Etiket == "dolu");   // ham "dolu" gösterilmez
        Assert.Contains(satirlar, s => s.Etiket == "NULL" && s.Deger.StartsWith("7"));  // 100-93
        Assert.Contains(satirlar, s => s.Etiket == "satır" && s.Deger == "100");
        Assert.Contains(satirlar, s => s.Etiket == "en büyük" && s.Deger.Contains("8.400"));
    }

    [Fact]
    public void Bicimle_null_degerleri_ve_bos_sonucu_kaldirir()
    {
        var bos = new ResultSetData { Kolonlar = [new KolonBilgisi("satır", "bigint")], Satirlar = [] };
        Assert.Empty(KolonIstatistigi.Bicimle(bos));

        var nulllu = new ResultSetData
        {
            Kolonlar = [new KolonBilgisi("satır", "bigint"), new KolonBilgisi("dolu", "bigint"),
                        new KolonBilgisi("en küçük", "decimal")],
            Satirlar = [[0L, 0L, DBNull.Value]],
        };
        Assert.Contains(KolonIstatistigi.Bicimle(nulllu), s => s.Etiket == "en küçük" && s.Deger == "NULL");
    }
}
