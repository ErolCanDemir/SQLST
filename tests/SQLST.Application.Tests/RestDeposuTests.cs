using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>REST deposu (v20-S8): geçmiş budama + ortam ({{değişken}}) şifreli round-trip + kayıtlı istek upsert.</summary>
public sealed class RestDeposuTests : IDisposable
{
    private readonly string _dosya = Path.Combine(Path.GetTempPath(), $"sqlst-rest-{Guid.NewGuid():N}.db");
    private IRestDeposu Depo() => new SqliteRestDeposu(new YerelDepo(_dosya), new SahteProtector());

    private sealed class SahteProtector : ISecretProtector
    {
        // Testte gerçek DPAPI yerine kimlik dönüşümü — depo mantığını (JSON round-trip) doğrular.
        public string Sifrele(string duzMetin) => duzMetin;
        public string Coz(string sifreliBase64) => sifreliBase64;
    }

    [Fact]
    public async Task Gecmis_eklenir_en_yeni_ustte_ve_budanir()
    {
        IRestDeposu d = Depo();
        for (int i = 1; i <= 5; i++)
            await d.GecmisEkleAsync(new RestGecmisKaydi(0, DateTime.UtcNow.AddSeconds(i), "GET", $"https://x/{i}", 200, i));

        IReadOnlyList<RestGecmisKaydi> hepsi = await d.GecmisAsync(100);
        Assert.Equal(5, hepsi.Count);
        Assert.Equal("https://x/5", hepsi[0].Url); // en yeni üstte

        await d.GecmisEkleAsync(new RestGecmisKaydi(0, DateTime.UtcNow, "POST", "https://x/6", 201, 9), enCok: 3);
        Assert.Equal(3, (await d.GecmisAsync(100)).Count); // budama
    }

    [Fact]
    public async Task Ortam_degiskenleri_sifreli_round_trip_ve_upsert()
    {
        IRestDeposu d = Depo();
        await d.OrtamKaydetAsync(new RestOrtam("Prod",
            [new RestSatir("baseUrl", "https://api.x"), new RestSatir("token", "TOK", Etkin: false)]));

        RestOrtam ortam = Assert.Single(await d.OrtamlarAsync());
        Assert.Equal("Prod", ortam.Ad);
        Assert.Equal(2, ortam.Degiskenler.Count);
        Assert.Equal("https://api.x", ortam.Degiskenler[0].Deger);
        Assert.False(ortam.Degiskenler[1].Etkin); // etkin bayrağı korunur

        await d.OrtamKaydetAsync(new RestOrtam("Prod", [new RestSatir("baseUrl", "https://api.yeni")]));
        RestOrtam guncel = Assert.Single(await d.OrtamlarAsync()); // upsert — aynı ad tek kayıt
        Assert.Equal("https://api.yeni", Assert.Single(guncel.Degiskenler).Deger);
    }

    [Fact]
    public async Task Kayitli_istek_upsert_ve_listelenir()
    {
        IRestDeposu d = Depo();
        await d.IstekKaydetAsync(new RestKayitliIstek("Firmalar", "GET", "https://api.x/firmalar", null));
        await d.IstekKaydetAsync(new RestKayitliIstek("Oluştur", "POST", "https://api.x/firmalar", """{"ad":"LST"}"""));
        Assert.Equal(2, (await d.KayitliIsteklerAsync()).Count);

        await d.IstekKaydetAsync(new RestKayitliIstek("Firmalar", "GET", "https://api.x/firmalar?aktif=1", null));
        IReadOnlyList<RestKayitliIstek> liste = await d.KayitliIsteklerAsync();
        Assert.Equal(2, liste.Count); // upsert
        Assert.Contains(liste, k => k.Ad == "Firmalar" && k.Url.Contains("aktif=1"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string ek in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dosya + ek)) File.Delete(_dosya + ek);
    }
}
