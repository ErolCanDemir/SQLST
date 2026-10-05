using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

public class DpapiSecretProtectorTests
{
    [Fact]
    public void Sifrele_Coz_gidis_donusu_ayni_metni_verir()
    {
        var protector = new DpapiSecretProtector();
        const string parola = "Gizli+Parola#123 ĞÜŞİÖÇ ğüşiöç";

        string sifreli = protector.Sifrele(parola);

        Assert.NotEqual(parola, sifreli);
        Assert.Equal(parola, protector.Coz(sifreli));
    }
}

public class JsonProfileStoreTests
{
    [Fact]
    public async Task Kaydet_getir_sil_dongusu_calisir()
    {
        string dosya = Path.Combine(Path.GetTempPath(), $"sqlst-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonProfileStore(dosya);
            var profil = new ConnectionProfile { Ad = "Test", Sunucu = "localhost\\SQLEXPRESS" };

            await store.SaveAsync(profil);
            IReadOnlyList<ConnectionProfile> hepsi = await store.GetAllAsync();
            Assert.Single(hepsi);
            Assert.Equal("Test", hepsi[0].Ad);

            profil.Ad = "Test-güncel";
            await store.SaveAsync(profil);
            hepsi = await store.GetAllAsync();
            Assert.Single(hepsi); // aynı Id güncellenir, çoğalmaz
            Assert.Equal("Test-güncel", hepsi[0].Ad);

            await store.DeleteAsync(profil.Id);
            Assert.Empty(await store.GetAllAsync());
        }
        finally
        {
            if (File.Exists(dosya)) File.Delete(dosya);
        }
    }
}
