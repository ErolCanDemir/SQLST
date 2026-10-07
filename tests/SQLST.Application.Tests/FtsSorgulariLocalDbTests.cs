using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 🔎 FTS okuma sorguları gerçek LocalDB'de (v23-S17). LocalDB'de FTS BİLEŞENİ yoktur ama katalog
/// görünümleri (sys.fulltext_indexes/catalogs/stoplists/system_stopwords) ve OBJECTPROPERTYEX /
/// FULLTEXTCATALOGPROPERTY vardır — sorguların sözdizimi ve kolon adları burada GERÇEK sunucuya
/// karşı doğrulanır (0 satırla döner). ALTER FULLTEXT işlemleri ve dolu sonuç canlı turda (K4).
/// </summary>
public class FtsSorgulariLocalDbTests
{
    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());

    private static Task<QueryResult> Calistir(string sql) => Executor.ExecuteAsync(new ConnectionProfile
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB", Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    }, sql, new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);

    [Fact]
    public async Task Envanter_katalog_ve_stoplist_sorgulari_gercek_sunucuda_hatasiz()
    {
        foreach (string sql in new[]
                 {
                     FtsSorgulari.EnvanterSorgusu(), FtsSorgulari.KatalogDetaySorgusu(),
                     FtsSorgulari.KolonHaritasiSorgusu(), FtsSorgulari.KatalogSorgusu(),
                 })
        {
            QueryResult r = await Calistir(sql);
            Assert.True(r.Hata is null, $"{r.Hata?.Mesaj}\n{sql}");
        }

        QueryResult envanter = await Calistir(FtsSorgulari.EnvanterSorgusu());
        Assert.Equal(16, envanter.ResultSetler[0].Kolonlar.Count); // VM bu sırayı okur (0..15)

        QueryResult stoplist = await Calistir(FtsSorgulari.StoplistSorgusu());
        Assert.Null(stoplist.Hata);
        Assert.Equal("SYSTEM", stoplist.ResultSetler[0].Satirlar[0][1]); // sistem listesi her zaman ilk
    }

    [Fact]
    public async Task Stop_kelime_sorgulari_gercek_sunucuda_hatasiz()
    {
        Assert.Null((await Calistir(FtsSorgulari.StopKelimeSorgusu(0, 1055))).Hata);
        Assert.Null((await Calistir(FtsSorgulari.StopKelimeSorgusu(5, 1055))).Hata);
    }
}
