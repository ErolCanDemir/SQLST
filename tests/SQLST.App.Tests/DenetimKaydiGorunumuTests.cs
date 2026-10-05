using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>v10-S3 Aktivite/Denetim: geçmiş kaydının "kim ne iş yapmış" gösterimi.</summary>
public class DenetimKaydiGorunumuTests
{
    private static GecmisKaydi Kayit(string sql, string? kullanici = "DOMAIN\\ali", string? db = "satis") => new()
    {
        Sunucu = "srv",
        Veritabani = db,
        Kullanici = kullanici,
        Sql = sql,
        BaslangicUtc = new DateTime(2026, 7, 23, 9, 0, 0, DateTimeKind.Utc),
        Durum = GecmisDurumu.Basarili,
    };

    [Fact]
    public void Tur_ve_yazma_bayragi()
    {
        var okuma = new DenetimKaydiGorunumu(Kayit("SELECT * FROM Musteri"), IslemTuru.Select);
        var silme = new DenetimKaydiGorunumu(Kayit("DELETE FROM Musteri WHERE Id=1"), IslemTuru.Delete);

        Assert.Equal("Okuma", okuma.Tur);
        Assert.False(okuma.Yazma);
        Assert.Equal("Silme", silme.Tur);
        Assert.True(silme.Yazma); // yalnız-yazma süzgeci bunu gösterir
    }

    [Fact]
    public void Eslesir_kullanici_tur_db_sql_icinde_arar()
    {
        var g = new DenetimKaydiGorunumu(Kayit("UPDATE Siparis SET tutar=1"), IslemTuru.Update);

        Assert.True(g.Eslesir("ali"));       // kullanıcı
        Assert.True(g.Eslesir("güncelleme")); // tür etiketi (büyük/küçük duyarsız)
        Assert.True(g.Eslesir("satis"));     // db
        Assert.True(g.Eslesir("Siparis"));   // sql
        Assert.False(g.Eslesir("silme"));    // eşleşmez
    }

    [Fact]
    public void Kullanici_yoksa_soru_isareti()
        => Assert.Equal("?", new DenetimKaydiGorunumu(Kayit("SELECT 1", kullanici: null), IslemTuru.Select).Kullanici);

    [Fact]
    public void SqlKisa_tek_satira_iner_ve_kirpar()
    {
        var g = new DenetimKaydiGorunumu(Kayit("SELECT\n  a,\n  b\nFROM t"), IslemTuru.Select);
        Assert.DoesNotContain('\n', g.SqlKisa);
    }
}
