using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>V15-S1 (BF-2): DML → salt-SELECT prova çifti çevirisi. Çevirici HİÇBİR ŞEY çalıştırmaz.</summary>
public class ProvaCeviriciTests
{
    [Fact]
    public void Basit_update_sayim_ve_ornek_uretir()
    {
        (ProvaPlani? plan, string? hata) = ProvaCevirici.Cevir(
            "UPDATE dbo.Musteri SET Durum = 'Pasif', GuncellemeTarihi = GETDATE()\n"
            + "WHERE Sehir = 'İzmir' AND SonSiparis < '2025-01-01'");

        Assert.Null(hata);
        Assert.Equal("UPDATE", plan!.Fiil);
        Assert.Equal("dbo.Musteri", plan.Hedef);
        Assert.Equal(["Durum", "GuncellemeTarihi"], plan.SetKolonlari);
        Assert.False(plan.WhereYok);
        Assert.Equal(
            "SELECT COUNT_BIG(*) AS [etkilenecek satır] FROM dbo.Musteri "
            + "WHERE Sehir = 'İzmir' AND SonSiparis < '2025-01-01';", plan.SayimSql);
        // m.28: örnek SELECT mevcut kolonların yanına SET ifadelerini "→ olacak" kolonu koyar
        Assert.Equal(
            "SELECT TOP (10) *, 'Pasif' AS [Durum → olacak], GETDATE() AS [GuncellemeTarihi → olacak] "
            + "FROM dbo.Musteri WHERE Sehir = 'İzmir' AND SonSiparis < '2025-01-01';", plan.OrnekSql);
    }

    [Fact]
    public void Delete_ve_wheresiz_uyarisi()
    {
        (ProvaPlani? plan, string? hata) = ProvaCevirici.Cevir("DELETE FROM Siparis");

        Assert.Null(hata);
        Assert.Equal("DELETE", plan!.Fiil);
        Assert.True(plan.WhereYok); // UI kırmızı "TÜM tablo" uyarısı buradan
        Assert.Empty(plan.SetKolonlari);
        Assert.Equal("SELECT COUNT_BIG(*) AS [etkilenecek satır] FROM Siparis;", plan.SayimSql);
    }

    [Fact]
    public void Update_from_joinli_alias_korunur_ornek_hedefe_niteli()
    {
        (ProvaPlani? plan, string? hata) = ProvaCevirici.Cevir(
            "UPDATE m SET m.Durum = 'Vip'\n"
            + "FROM dbo.Musteri m INNER JOIN dbo.Siparis s ON s.MusteriId = m.Id\n"
            + "WHERE s.Tutar > 10000");

        Assert.Null(hata);
        Assert.Equal("m", plan!.Hedef);
        // Kaynak FROM aynen (alias + JOIN bozulmadan), örnek: hedef kolonları + "→ olacak" (m.28)
        Assert.StartsWith(
            "SELECT TOP (10) m.*, 'Vip' AS [Durum → olacak] FROM dbo.Musteri m INNER JOIN", plan.OrnekSql);
        Assert.Contains("WHERE s.Tutar > 10000", plan.SayimSql);
    }

    [Fact]
    public void Toplu_dml_nota_dusulur()
    {
        (ProvaPlani? plan, _) = ProvaCevirici.Cevir("DELETE TOP (100) FROM Log WHERE Tarih < '2024-01-01'");
        Assert.NotNull(plan!.TopNotu);
        Assert.Contains("TOP (100)", plan.TopNotu);
        Assert.Contains("WHERE Tarih < '2024-01-01'", plan.SayimSql); // sayım TOP'suz — tüm eşleşenler
    }

    [Theory]
    [InlineData("SELECT 1", "yalnız UPDATE ve DELETE")]
    [InlineData("INSERT INTO T VALUES (1)", "INSERT'in provası yok")]
    [InlineData("UPDATE A SET x=1; DELETE FROM B", "TEK ifadeyle")]
    [InlineData("WITH c AS (SELECT 1 AS a) UPDATE T SET x=1 FROM T JOIN c ON c.a=T.x", "CTE")]
    [InlineData("", "sorgu yok")]
    [InlineData("UPDATE SET nerede", "Söz dizimi hatası")]
    public void Desteklenmeyenler_anlasilir_mesaj_verir(string sql, string beklenen)
    {
        (ProvaPlani? plan, string? hata) = ProvaCevirici.Cevir(sql);
        Assert.Null(plan);
        Assert.Contains(beklenen, hata, StringComparison.OrdinalIgnoreCase);
    }
}
