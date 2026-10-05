using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.10 fikir 3 — hücreden hızlı filtre: koşul SQL'e EKLENİR, sorgu yeniden üretilmez.
/// Kritik davranışlar: koşul ORDER BY/GROUP BY'ın ÖNÜNE girer, mevcut WHERE'e AND'lenir,
/// NULL'da IS [NOT] NULL'a döner, alias/hint/biçim bozulmaz, desteklenmeyen sorguda net hata.
/// </summary>
public class HizliFiltreTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static string Uygula(string sql, string kolon = "Tutar",
        FiltreIliskisi iliski = FiltreIliskisi.Esit, object? deger = null)
    {
        (string? yeni, string? hata) = HizliFiltre.Uygula(sql, kolon, iliski, deger ?? 310.75m, Mssql);
        Assert.Null(hata);
        return yeni!;
    }

    [Fact]
    public void Where_yoksa_from_sonrasina_eklenir()
        => Assert.Equal("SELECT * FROM dbo.Talep WHERE [Tutar] = 310.75", Uygula("SELECT * FROM dbo.Talep"));

    [Fact]
    public void Mevcut_where_e_and_lenir()
        => Assert.Equal("SELECT * FROM dbo.Talep WHERE Yil = 2026 AND [Tutar] = 310.75",
            Uygula("SELECT * FROM dbo.Talep WHERE Yil = 2026"));

    [Fact]
    public void Order_by_nin_ONUNE_girer() // en kritik: sözdizimi bozulmamalı
    {
        Assert.Equal("SELECT * FROM dbo.Talep WHERE [Tutar] = 310.75 ORDER BY Id DESC",
            Uygula("SELECT * FROM dbo.Talep ORDER BY Id DESC"));
        Assert.Equal("SELECT * FROM dbo.Talep WHERE Yil = 2026 AND [Tutar] = 310.75 ORDER BY Id",
            Uygula("SELECT * FROM dbo.Talep WHERE Yil = 2026 ORDER BY Id"));
    }

    [Fact]
    public void Group_by_havingin_onune_girer()
        => Assert.Equal("SELECT Sehir, COUNT(*) FROM dbo.Talep WHERE [Tutar] = 310.75 GROUP BY Sehir HAVING COUNT(*) > 1",
            Uygula("SELECT Sehir, COUNT(*) FROM dbo.Talep GROUP BY Sehir HAVING COUNT(*) > 1"));

    [Fact]
    public void Alias_join_ve_hint_bozulmaz()
    {
        string sonuc = Uygula("SELECT t.* FROM dbo.Talep t WITH(NOLOCK) JOIN dbo.Durum d ON d.Id = t.DurumId");

        Assert.StartsWith("SELECT t.* FROM dbo.Talep t WITH(NOLOCK) JOIN dbo.Durum d ON d.Id = t.DurumId", sonuc);
        Assert.EndsWith("WHERE [Tutar] = 310.75", sonuc);
    }

    [Fact]
    public void Null_deger_is_null_a_doner()
    {
        Assert.EndsWith("WHERE [Tutar] IS NULL",
            Uygula("SELECT * FROM T", deger: DBNull.Value));
        Assert.EndsWith("WHERE [Tutar] IS NOT NULL",
            Uygula("SELECT * FROM T", iliski: FiltreIliskisi.EsitDegil, deger: DBNull.Value));
    }

    [Fact]
    public void Islecler_ve_metin_literali()
    {
        Assert.EndsWith("[Tutar] <> 5", Uygula("SELECT * FROM T", iliski: FiltreIliskisi.EsitDegil, deger: 5));
        Assert.EndsWith("[Tutar] >= 5", Uygula("SELECT * FROM T", iliski: FiltreIliskisi.BuyukEsit, deger: 5));
        Assert.EndsWith("[Tutar] <= 5", Uygula("SELECT * FROM T", iliski: FiltreIliskisi.KucukEsit, deger: 5));

        string metin = Uygula("SELECT * FROM T", kolon: "Sehir", deger: "İz'mir"); // tırnak KAÇIRILMALI
        Assert.Contains("[Sehir] = ", metin);
        Assert.Contains("İz''mir", metin);
    }

    [Theory]
    [InlineData("SELECT * FROM A UNION SELECT * FROM B", "UNION")]
    [InlineData("SELECT 1; SELECT 2", "TEK bir SELECT")]
    [InlineData("UPDATE T SET x = 1", "TEK bir SELECT")]
    [InlineData("SELECT 42", "FROM yok")]
    [InlineData("SELECT * FROM", "çözümlenemedi")]
    public void Desteklenmeyen_sorguda_net_hata(string sql, string mesajParcasi)
    {
        (string? yeni, string? hata) = HizliFiltre.Uygula(sql, "Tutar", FiltreIliskisi.Esit, 1, Mssql);
        Assert.Null(yeni);
        Assert.Contains(mesajParcasi, hata);
    }

    [Fact]
    public void Siralanabilir_tipler_kiyas_menusunu_acar()
    {
        Assert.True(HizliFiltre.SiralanabilirMi(5));
        Assert.True(HizliFiltre.SiralanabilirMi(3.5m));
        Assert.True(HizliFiltre.SiralanabilirMi(new DateTime(2026, 8, 14)));
        Assert.False(HizliFiltre.SiralanabilirMi("metin"));
        Assert.False(HizliFiltre.SiralanabilirMi(true));
        Assert.False(HizliFiltre.SiralanabilirMi(Guid.NewGuid()));
        Assert.False(HizliFiltre.SiralanabilirMi(null));
    }
}
