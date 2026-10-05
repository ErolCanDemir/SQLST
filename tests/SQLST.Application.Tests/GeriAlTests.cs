using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>V15-S3 (BF-1): yakalayıcı sınırları + serileştirici sadakati + depo CRUD/budama.</summary>
public class GeriAlTests
{
    // ---- GeriAlYakalayici ----

    [Fact]
    public void Update_icin_yakalama_ve_pk_sorgusu_uretir()
    {
        GeriAlAdayi? aday = GeriAlYakalayici.Uret(
            "UPDATE dbo.Musteri SET Durum = 'Pasif' WHERE Sehir = N'İzmir'");

        Assert.NotNull(aday);
        Assert.Equal("UPDATE", aday.Fiil);
        Assert.Equal("dbo.Musteri", aday.Tablo);
        Assert.Equal("SELECT TOP (50001) * FROM dbo.Musteri WHERE Sehir = N'İzmir';", aday.YakalamaSql);
        Assert.Contains("OBJECT_ID(N'dbo.Musteri')", aday.PkSql);
    }

    [Fact]
    public void Wheresiz_delete_tum_tabloyu_yakalar()
    {
        GeriAlAdayi? aday = GeriAlYakalayici.Uret("DELETE FROM Siparis");
        Assert.Equal("DELETE", aday!.Fiil);
        Assert.Equal("SELECT TOP (50001) * FROM Siparis;", aday.YakalamaSql);
    }

    [Theory]
    [InlineData("UPDATE m SET Durum=1 FROM Musteri m JOIN X ON X.Id=m.Id")] // FROM'lu — MVP dışı
    [InlineData("UPDATE A SET x=1; DELETE FROM B")]                          // çok ifade
    [InlineData("WITH c AS (SELECT 1 a) UPDATE T SET x=1 FROM T")]           // CTE
    [InlineData("INSERT INTO T VALUES (1)")]                                 // INSERT — eski hal yok
    [InlineData("SELECT 1")]
    [InlineData("bozuk sql (")]
    public void Kapsam_disi_dml_null_doner(string sql)
        => Assert.Null(GeriAlYakalayici.Uret(sql));

    // ---- GeriAlSerilestirici ----

    [Fact]
    public void Paket_lob_kolonlari_dislar_ve_degerler_gidis_donuste_korunur()
    {
        var aday = new GeriAlAdayi("UPDATE", "dbo.T", "-", "-");
        var set = new ResultSetData
        {
            Kolonlar =
            [
                new KolonBilgisi("Id", "int"),
                new KolonBilgisi("Ad", "nvarchar"),
                new KolonBilgisi("Resim", "varbinary"),   // dışlanır (LOB)
                new KolonBilgisi("Surum", "timestamp"),   // dışlanır (rowversion)
                new KolonBilgisi("Tutar", "decimal"),
                new KolonBilgisi("Tarih", "datetime2"),
                new KolonBilgisi("AktifMi", "bit"),
            ],
            Satirlar =
            [
                [1, "Ahmet Çelik", new byte[] { 1, 2 }, new byte[] { 9 }, 1250.75m,
                 new DateTime(2026, 7, 27, 10, 30, 0, DateTimeKind.Utc), true],
                [2, null, null, null, null, null, false],
            ],
        };

        GeriAlPaketi paket = GeriAlSerilestirici.PaketKur(
            "sunucu", "DemoDb", aday, "UPDATE ...", set, ["Id"], new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, paket.SatirSayisi);
        IReadOnlyList<GeriAlSerilestirici.KolonTanimi> kolonlar = GeriAlSerilestirici.KolonlariOku(paket);
        Assert.Equal(["Id", "Ad", "Tutar", "Tarih", "AktifMi"], kolonlar.Select(k => k.Ad)); // LOB'lar yok
        Assert.Equal(["Id"], GeriAlSerilestirici.PkOku(paket));

        IReadOnlyList<string?[]> satirlar = GeriAlSerilestirici.SatirlariOku(paket);
        Assert.Equal("1", satirlar[0][0]);
        Assert.Equal("Ahmet Çelik", satirlar[0][1]);          // Türkçe karakter bozulmadan
        Assert.Equal("1250.75", satirlar[0][2]);              // invariant — TR virgülü değil
        Assert.StartsWith("2026-07-27T10:30:00", satirlar[0][3]); // ISO 8601
        Assert.Equal("1", satirlar[0][4]);
        Assert.Null(satirlar[1][1]);                          // NULL null kalır ("null" metni değil)
    }

    // ---- SqliteGeriAlDeposu ----

    [Fact]
    public async Task Depo_ekler_listeler_getirir_siler_ve_eskiyi_budar()
    {
        string dosya = Path.Combine(Path.GetTempPath(), $"sqlst-gerial-{Guid.NewGuid():N}.db");
        try
        {
            var depo = new SqliteGeriAlDeposu(new YerelDepo(dosya));

            GeriAlPaketi Paket(DateTime tarih, string tablo) => new()
            {
                TarihUtc = tarih, Sunucu = "s", Veritabani = "db", Tablo = tablo,
                Fiil = "UPDATE", SqlMetni = "UPDATE ...", SatirSayisi = 2,
                KolonlarJson = """[{"Ad":"Id","Tip":"int"}]""", PkJson = """["Id"]""",
                SatirlarJson = """[["1"],["2"]]""",
            };

            long eskiId = await depo.EkleAsync(Paket(DateTime.UtcNow.AddDays(-40), "dbo.Eski")); // yaş sınırı dışı
            long yeniId = await depo.EkleAsync(Paket(DateTime.UtcNow, "dbo.Yeni"));
            Assert.True(yeniId > eskiId);

            IReadOnlyList<GeriAlPaketi> liste = await depo.ListeleAsync();
            Assert.Single(liste);                       // 40 günlük paket budandı
            Assert.Equal("dbo.Yeni", liste[0].Tablo);
            Assert.Equal("", liste[0].SatirlarJson);    // liste hafif — satır verisi yok

            GeriAlPaketi? tam = await depo.GetirAsync(liste[0].Id);
            Assert.Equal("""[["1"],["2"]]""", tam!.SatirlarJson); // tam paket dolu

            await depo.SilAsync(liste[0].Id);
            Assert.Empty(await depo.ListeleAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string ek in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(dosya + ek))
                    File.Delete(dosya + ek);
            }
        }
    }
}
