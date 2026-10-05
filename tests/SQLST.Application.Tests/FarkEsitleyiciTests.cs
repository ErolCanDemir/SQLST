using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>BF-3 (2026-07-27): tam eşitleme birleştirmesi — Excel + tablo satırları → sayımlar + Güvenli Yazma script'i.</summary>
public class FarkEsitleyiciTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static Dictionary<string, object?> S(params (string, object?)[] a)
        => a.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Uçtan_uca_sayımlar_ve_script_üretir()
    {
        var excel = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Id", 1), ("Ad", "Ahmet")),  // değişmedi
            S(("Id", 2), ("Ad", "Ayse2")),  // değişti
            S(("Id", 3), ("Ad", "Yeni")),   // yeni
        };
        var tablo = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Id", 1), ("Ad", "Ahmet")),
            S(("Id", 2), ("Ad", "Ayse")),
            S(("Id", 9), ("Ad", "Silinecek")), // Excel'de yok
        };

        FarkEsitleyici.EsitlemeSonucu sonuc = FarkEsitleyici.Hesapla(
            Mssql, "dbo", "Musteri", excel, tablo, ["Id"], ["Ad"], silmeDahil: true);

        Assert.Equal(1, sonuc.YeniSayisi);
        Assert.Equal(1, sonuc.DegisenSayisi);
        Assert.Equal(1, sonuc.SilinenSayisi);
        Assert.Equal(3, sonuc.Komutlar.Count);
        Assert.Contains("DELETE FROM [dbo].[Musteri] WHERE [Id] = 9;", sonuc.Komutlar);
        Assert.Contains("UPDATE [dbo].[Musteri] SET [Ad] = N'Ayse2' WHERE [Id] = 2;", sonuc.Komutlar);
        Assert.Contains("INSERT INTO [dbo].[Musteri] ([Id], [Ad]) VALUES (3, N'Yeni');", sonuc.Komutlar);
        Assert.Equal(string.Join(Environment.NewLine, sonuc.Komutlar), sonuc.Onizleme);
    }

    [Fact]
    public void Silme_kapalıyken_silinen_sayılır_ama_script_üretmez()
    {
        var excel = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Ad", "A")) };
        var tablo = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Id", 1), ("Ad", "A")),
            S(("Id", 2), ("Ad", "B")), // Excel'de yok
        };

        FarkEsitleyici.EsitlemeSonucu sonuc = FarkEsitleyici.Hesapla(
            Mssql, "dbo", "T", excel, tablo, ["Id"], ["Ad"], silmeDahil: false);

        Assert.Equal(1, sonuc.SilinenSayisi); // kullanıcıya "1 satır Excel'de yok" gösterilir
        Assert.True(sonuc.BosMu);             // ama silme kapalı + başka fark yok → komut yok
    }
}
