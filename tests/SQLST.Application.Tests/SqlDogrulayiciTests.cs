using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// 🛡 AI çıktısı SQL doğrulayıcı (v21-S2): POC'de yerel model geçersiz T-SQL üretti
/// (3B LIMIT, 7B yanlış konumlu TOP) — bu kapı onları yakalar. "Sessiz yanlış yasak" AI'a da.
/// </summary>
public class SqlDogrulayiciTests
{
    [Fact]
    public void Gecerli_tsql_ok()
    {
        SqlDogrulamaSonucu s = SqlDogrulayici.Dogrula("SELECT TOP (5) Ad FROM dbo.Musteri", MotorTuru.Mssql);
        Assert.True(s.Dogrulandi);
        Assert.True(s.Gecerli);
        Assert.Null(s.Hata);
    }

    [Fact]
    public void Poc_hatalari_yakalanir()
    {
        // 7B'nin ürettiği: TOP sonda → geçersiz
        SqlDogrulamaSonucu top = SqlDogrulayici.Dogrula(
            "SELECT Ad FROM dbo.Musteri ORDER BY Ad TOP 5", MotorTuru.Mssql);
        Assert.True(top.Dogrulandi);
        Assert.False(top.Gecerli);
        Assert.NotNull(top.Hata);

        // 3B'nin ürettiği: LIMIT (T-SQL'de yok) → geçersiz
        SqlDogrulamaSonucu limit = SqlDogrulayici.Dogrula(
            "SELECT Ad FROM dbo.Musteri LIMIT 5", MotorTuru.Mssql);
        Assert.False(limit.Gecerli);
    }

    [Fact]
    public void Diger_motorlar_dogrulanamadi_der_sahte_gecerli_demez()
    {
        // PG/MySQL/Oracle parser'ımız yok — "geçerli" diye uydurmayız
        foreach (MotorTuru m in (MotorTuru[])[MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Oracle])
        {
            SqlDogrulamaSonucu s = SqlDogrulayici.Dogrula("SELECT * FROM x LIMIT 5", m);
            Assert.False(s.Dogrulandi);
            Assert.False(s.Gecerli);
        }
    }

    [Fact]
    public void Mongo_json_gecerliligi()
    {
        Assert.True(SqlDogrulayici.Dogrula("""{ "find": "log", "limit": 5 }""", MotorTuru.Mongo).Gecerli);
        SqlDogrulamaSonucu bozuk = SqlDogrulayici.Dogrula("""{ "find": "log", }""", MotorTuru.Mongo);
        Assert.True(bozuk.Dogrulandi);
        Assert.False(bozuk.Gecerli);
    }

    [Fact]
    public void Bos_sql_yapilamadi()
    {
        Assert.False(SqlDogrulayici.Dogrula("", MotorTuru.Mssql).Dogrulandi);
        Assert.False(SqlDogrulayici.Dogrula(null, MotorTuru.Mssql).Dogrulandi);
    }

    // ── istem sıkılaştırma (v21-S2) ──────────────────────────────────────────────────────────

    [Fact]
    public void Motor_soz_dizim_notu_lehceye_gore()
    {
        Assert.Contains("TOP (n)", AsistanIstemleri.MotorSozDizimNotu(MotorTuru.Mssql));
        Assert.Contains("LIMIT", AsistanIstemleri.MotorSozDizimNotu(MotorTuru.Postgres));
        Assert.Contains("FETCH FIRST", AsistanIstemleri.MotorSozDizimNotu(MotorTuru.Oracle));
        Assert.Equal("", AsistanIstemleri.MotorSozDizimNotu(MotorTuru.Mongo)); // SQL değil
    }

    [Fact]
    public void Uretim_istemleri_soz_dizim_notunu_tasir()
    {
        string istem = AsistanIstemleri.SerbestSoru(
            "ilk 5 müşteri", "dbo.Musteri (Id int PK)", "Microsoft SQL Server (T-SQL)",
            editordekiSorgu: null, motor: MotorTuru.Mssql);
        Assert.Contains("TOP (n)", istem); // POC hatasını önleyen kural istemde
    }

    [Fact]
    public void Duzeltme_turu_istemi_hatayi_ve_yalniz_sql_talebini_tasir()
    {
        string istem = AsistanIstemleri.SqlDuzeltmeTuru(
            "SELECT Ad FROM dbo.Musteri LIMIT 5", "Satır 1: 'LIMIT' yakınında yanlış söz dizimi",
            MotorTuru.Mssql);

        Assert.Contains("LIMIT", istem);                 // bozuk sorgu içeride
        Assert.Contains("yanlış söz dizimi", istem);      // parse hatası içeride
        Assert.Contains("TOP (n)", istem);               // düzeltme yönü verilmiş
        Assert.Contains("Açıklama YAZMA", istem);         // yalnız SQL iste
    }
}
