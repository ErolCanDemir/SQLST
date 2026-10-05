using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v7-S2 — satır bazlı veri farkı çekirdeği (SAF) + MSSQL satır-hash sorgusu. Gerçek karşılaştırma
/// sunucuda hash + burada anahtar eşleşmesi/hash kıyası; canlı çalıştırma VM/entegrasyon işidir.
/// </summary>
public class VeriFarkHesabiTests
{
    [Fact]
    public void Es_satirlar_fark_vermez()
    {
        var sol = new Dictionary<string, string> { ["1"] = "aa", ["2"] = "bb" };
        var sag = new Dictionary<string, string> { ["1"] = "aa", ["2"] = "bb" };

        Assert.Empty(VeriFarkHesabi.Hesapla(sol, sag));
    }

    [Fact]
    public void Yalniz_sol_sag_ve_farkli_ayirt_edilir()
    {
        var sol = new Dictionary<string, string> { ["1"] = "aa", ["2"] = "bb", ["3"] = "cc" };
        var sag = new Dictionary<string, string> { ["1"] = "aa", ["2"] = "XX", ["4"] = "dd" };

        IReadOnlyList<VeriFarkKaydi> f = VeriFarkHesabi.Hesapla(sol, sag);

        Assert.Equal(VeriFarkTuru.Farkli, f.Single(x => x.Anahtar == "2").Tur);     // hash değişti
        Assert.Equal(VeriFarkTuru.YalnizSol, f.Single(x => x.Anahtar == "3").Tur);  // sağda yok
        Assert.Equal(VeriFarkTuru.YalnizSag, f.Single(x => x.Anahtar == "4").Tur);  // solda yok
        Assert.DoesNotContain(f, x => x.Anahtar == "1");                            // eş → fark yok
        Assert.Equal(1, VeriFarkHesabi.Say(f, VeriFarkTuru.Farkli));
    }

    private static readonly ISecretProtector Koruyucu = new DpapiSecretProtector();

    [Fact]
    public void Mssql_hash_sorgusu_checksum_uretir()
    {
        string? sql = new MssqlLehcesi(Koruyucu)
            .SatirHashSorgusu("satis", "Musteri", ["Id"], ["Id", "Ad"], null);

        Assert.Equal("SELECT [Id], BINARY_CHECKSUM(*) AS __hash FROM [satis].[Musteri]", sql);
    }

    [Fact]
    public void Mssql_hash_sorgusu_WHERE_ve_bilesik_anahtar()
    {
        string? sql = new MssqlLehcesi(Koruyucu)
            .SatirHashSorgusu("dbo", "Kalem", ["SiparisId", "SatirNo"], ["SiparisId", "SatirNo", "Tutar"], "aktif = 1");

        Assert.Equal(
            "SELECT [SiparisId], [SatirNo], BINARY_CHECKSUM(*) AS __hash FROM [dbo].[Kalem] WHERE aktif = 1",
            sql);
    }

    [Fact]
    public void Postgres_hash_sorgusu_md5_satir_metni()
    {
        string? sql = new PostgresLehcesi(Koruyucu)
            .SatirHashSorgusu("satis", "Musteri", ["Id"], ["Id", "Ad"], null);

        Assert.Equal("SELECT \"Id\", md5(\"Musteri\"::text) AS __hash FROM \"satis\".\"Musteri\"", sql);
    }

    [Fact]
    public void MySql_hash_sorgusu_concat_ws_md5()
    {
        string? sql = new MySqlLehcesi(Koruyucu)
            .SatirHashSorgusu(null, "Musteri", ["Id"], ["Id", "Ad"], null);

        Assert.Equal(
            "SELECT `Id`, MD5(CONCAT_WS(CHAR(31), COALESCE(CAST(`Id` AS CHAR), CHAR(0)), "
            + "COALESCE(CAST(`Ad` AS CHAR), CHAR(0)))) AS __hash FROM `Musteri`",
            sql);
    }

    [Fact]
    public void Oracle_hash_sorgusu_standard_hash_rawtohex()
    {
        string? sql = new OracleLehcesi(Koruyucu)
            .SatirHashSorgusu("SATIS", "MUSTERI", ["ID"], ["ID", "AD"], null);

        Assert.Equal(
            "SELECT \"ID\", RAWTOHEX(STANDARD_HASH(NVL(TO_CHAR(\"ID\"), CHR(0)) || CHR(31) || "
            + "NVL(TO_CHAR(\"AD\"), CHR(0)), 'MD5')) AS __hash FROM \"SATIS\".\"MUSTERI\"",
            sql);
    }

    [Fact]
    public void Anahtar_yoksa_hash_sorgusu_null_tum_motorlarda()
    {
        Assert.Null(new MssqlLehcesi(Koruyucu).SatirHashSorgusu("dbo", "Kalem", [], ["a"], null));
        Assert.Null(new PostgresLehcesi(Koruyucu).SatirHashSorgusu("dbo", "Kalem", [], ["a"], null));
        Assert.Null(new MySqlLehcesi(Koruyucu).SatirHashSorgusu(null, "Kalem", [], ["a"], null));
        Assert.Null(new OracleLehcesi(Koruyucu).SatirHashSorgusu("DBO", "KALEM", [], ["A"], null));
    }
}
