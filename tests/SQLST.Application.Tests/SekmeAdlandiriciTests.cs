using SQLST.Application;

namespace SQLST.Application.Tests;

public class SekmeAdlandiriciTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Musteri WHERE Id = 1", "SELECT Musteri")]
    [InlineData("select top 10 Ad, Soyad\nfrom [dbo].[Musteri] m join Siparis s on s.MId = m.Id", "SELECT Musteri")]
    [InlineData("INSERT INTO dbo.Siparis (X) VALUES (1)", "INSERT Siparis")]
    [InlineData("INSERT dbo.Siparis (X) VALUES (1)", "INSERT Siparis")]
    [InlineData("UPDATE dbo.Siparis SET X = 1", "UPDATE Siparis")]
    [InlineData("DELETE FROM dbo.Siparis WHERE Id = 1", "DELETE Siparis")]
    [InlineData("EXEC dbo.spGetOrders @Id = 5", "EXEC spGetOrders")]
    [InlineData("EXECUTE [dbo].[spGetOrders]", "EXEC spGetOrders")]
    [InlineData("TRUNCATE TABLE dbo.GeciciVeri", "TRUNCATE GeciciVeri")]
    [InlineData("ALTER PROCEDURE dbo.spRapor AS BEGIN SELECT 1 END", "ALTER spRapor")]
    [InlineData("CREATE OR ALTER VIEW dbo.vwOzet AS SELECT 1 X", "CREATE vwOzet")]
    [InlineData("SELECT x FROM #kalici", "SELECT #kalici")]
    public void Fiil_ve_nesneden_baslik_turetir(string sql, string beklenen)
    {
        Assert.Equal(beklenen, SekmeAdlandirici.AdTuret(sql));
    }

    [Theory]
    [InlineData("-- FROM YorumdakiTablo gerçek değil\nSELECT * FROM dbo.Gercek", "SELECT Gercek")]
    [InlineData("/* FROM Blok */ SELECT * FROM dbo.Gercek", "SELECT Gercek")]
    public void Yorumlardaki_anahtar_kelimeler_yaniltmaz(string sql, string beklenen)
    {
        Assert.Equal(beklenen, SekmeAdlandirici.AdTuret(sql));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- sadece yorum")]
    [InlineData("SELECT 1")] // FROM'suz — türetilecek nesne yok
    [InlineData("PRINT 'merhaba'")]
    public void Turetilemeyince_null_doner_mevcut_ad_korunur(string sql)
    {
        Assert.Null(SekmeAdlandirici.AdTuret(sql));
    }

    [Fact]
    public void Cok_uzun_ad_kisaltilir()
    {
        string? ad = SekmeAdlandirici.AdTuret(
            "SELECT * FROM dbo.CokUzunAdliBirTabloAdiBuGercektenCokUzun_2026_Yedek");

        Assert.NotNull(ad);
        Assert.True(ad!.Length <= 30, $"'{ad}' ({ad.Length}) 30'u aşmamalı");
        Assert.EndsWith("…", ad);
    }
}
