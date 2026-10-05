using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>V16 (kullanıcı isteği 2026-07-27): editör kod katlama aralıkları — BEGIN/END, CASE, yorum.</summary>
public class KatlamaTests
{
    [Fact]
    public void Ic_ice_begin_end_bloklari_eslesir()
    {
        const string sql = "IF (1=1)\nBEGIN\n  SELECT 1\n  BEGIN\n    SELECT 2\n  END\nEND";
        IReadOnlyList<SqlCozumleyici.KatlamaAraligi> k = SqlCozumleyici.KatlamaAraliklari(sql);

        Assert.Equal(2, k.Count);
        // Artan başlangıç: dış BEGIN önce
        Assert.True(k[0].Bas < k[1].Bas);
        // Dış blok iç bloğu kapsar
        Assert.True(k[0].Bas < k[1].Bas && k[0].Son > k[1].Son);
        // Katlama BEGIN anahtarından SONRA başlar (anahtar görünür kalır)
        Assert.Equal("BEGIN", sql.Substring(k[0].Bas - 5, 5));
    }

    [Fact]
    public void Tek_satirlik_begin_end_katlanmaz()
        => Assert.Empty(SqlCozumleyici.KatlamaAraliklari("BEGIN SELECT 1 END"));

    [Fact]
    public void Try_catch_bloklari_katlanir()
    {
        const string sql = "BEGIN TRY\n  SELECT 1\nEND TRY\nBEGIN CATCH\n  SELECT 2\nEND CATCH";
        IReadOnlyList<SqlCozumleyici.KatlamaAraligi> k = SqlCozumleyici.KatlamaAraliklari(sql);
        Assert.Equal(2, k.Count); // TRY bloğu + CATCH bloğu
    }

    [Fact]
    public void Case_end_dengeyi_bozmaz()
    {
        // CASE'in END'i BEGIN'i yanlışlıkla kapatmamalı: dış BEGIN…END bütün olarak katlanır.
        const string sql = "BEGIN\n  SELECT CASE WHEN a=1\n    THEN 'x'\n    ELSE 'y'\n  END\n  SELECT 2\nEND";
        IReadOnlyList<SqlCozumleyici.KatlamaAraligi> k = SqlCozumleyici.KatlamaAraliklari(sql);

        Assert.Equal(2, k.Count); // dış BEGIN bloğu + çok satırlı CASE
        SqlCozumleyici.KatlamaAraligi disBlok = k.OrderByDescending(x => x.Son - x.Bas).First();
        Assert.EndsWith("END", sql[..disBlok.Son]); // dış blok son END'e kadar uzanır
    }

    [Fact]
    public void String_ve_yorum_icindeki_begin_end_sayilmaz()
    {
        const string sql = "SELECT 'BEGIN falan END' AS x -- BEGIN yorumda\nSELECT 1";
        Assert.Empty(SqlCozumleyici.KatlamaAraliklari(sql));
    }

    [Fact]
    public void Cok_satirli_yorum_katlanir()
    {
        const string sql = "/* satir1\n satir2\n satir3 */\nSELECT 1";
        IReadOnlyList<SqlCozumleyici.KatlamaAraligi> k = SqlCozumleyici.KatlamaAraliklari(sql);
        Assert.Single(k);
        Assert.Equal(0, k[0].Bas);
    }

    [Fact]
    public void Bos_ve_dengesiz_metin_patlamaz()
    {
        Assert.Empty(SqlCozumleyici.KatlamaAraliklari(""));
        Assert.Empty(SqlCozumleyici.KatlamaAraliklari("END END END")); // fazladan END'ler yutulur
    }
}
