using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 saha m.23 — ORDER BY'sız tek-tablolu SELECT'te görüntünün PK'ya dizilmesi:
/// tespit kuralı (alt sorgu ORDER BY'ı bile "var" sayılır — güvenli taraf), PK indeks
/// eşleme sözleşmesi (eksik anahtar → hiç karışma) ve sıralama kuralları (null en sonda,
/// bileşik anahtar, tip karışıklığında string karşılaştırma).
/// </summary>
public class VarsayilanSiralamaTests
{
    [Theory]
    [InlineData("SELECT * FROM T ORDER BY Id", true)]
    [InlineData("select x from t order   by x desc", true)]
    [InlineData("SELECT * FROM (SELECT TOP 5 * FROM T ORDER BY Id) i", true)] // alt sorgu → yine dokunma
    [InlineData("SELECT * FROM T WHERE KullaniciId = 5", false)]
    [InlineData("SELECT OrderById FROM T", false)] // kolon adı 'order by' değildir
    public void OrderBy_tespiti(string sql, bool beklenen)
        => Assert.Equal(beklenen, VarsayilanSiralama.OrderByIceriyorMu(sql));

    private static KolonBilgisi[] Kolonlar(params string[] adlar)
        => [.. adlar.Select(a => new KolonBilgisi(a, "int"))];

    [Fact]
    public void Pk_indeksleri_ad_esler_buyuk_kucuk_duyarsiz()
        => Assert.Equal([2, 0], VarsayilanSiralama.PkIndeksleri(
            Kolonlar("YIL", "Ad", "id"), ["Id", "Yil"]));

    [Fact]
    public void Pk_kolonu_sonucta_yoksa_null()
        => Assert.Null(VarsayilanSiralama.PkIndeksleri(Kolonlar("Ad", "Soyad"), ["Id"]));

    [Fact]
    public void Siralama_artan_null_en_sonda_bilesik()
    {
        List<object?[]> satirlar =
        [
            [3, "c"], [1, "b"], [DBNull.Value, "z"], [1, "a"], [2, null],
        ];

        VarsayilanSiralama.Sirala(satirlar, [0, 1]);

        Assert.Equal([1, "a"], satirlar[0]);
        Assert.Equal([1, "b"], satirlar[1]);
        Assert.Equal(2, satirlar[2][0]);
        Assert.Equal(3, satirlar[3][0]);
        Assert.Equal(DBNull.Value, satirlar[4][0]); // null anahtar en sonda
    }

    [Fact]
    public void Tip_karisikliginda_string_kiyasa_duser_patlamaz()
    {
        List<object?[]> satirlar = [[10], ["2"], [1]];
        VarsayilanSiralama.Sirala(satirlar, [0]); // farklı tipler CompareTo fırlatmamalı
        Assert.Equal(3, satirlar.Count);
    }
}
