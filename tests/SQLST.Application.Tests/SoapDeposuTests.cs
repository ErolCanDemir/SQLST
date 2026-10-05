using System.IO;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>v14-S3 — SOAP kalıcı deposu: geçmiş ekleme/budama/sıralama + ortam kaydet/üzerine yaz.</summary>
public class SoapDeposuTests
{
    [Fact]
    public async Task Gecmis_ekler_budar_ve_ortam_ayni_adla_gunceller()
    {
        string dosya = Path.Combine(Path.GetTempPath(), $"sqlst-soap-{Guid.NewGuid():N}.db");
        try
        {
            var depo = new SqliteSoapDeposu(new YerelDepo(dosya));

            for (int i = 1; i <= 5; i++)
            {
                await depo.GecmisEkleAsync(new SoapGecmisKaydi(
                    0, new DateTime(2026, 7, 26, 10, i, 0, DateTimeKind.Utc),
                    "http://uc/Servis.svc", $"http://tempuri.org/I/Op{i}",
                    $"<zarf>{i}</zarf>", i == 3 ? 500 : 200, i * 10, i == 3), enCok: 3);
            }

            IReadOnlyList<SoapGecmisKaydi> gecmis = await depo.GecmisAsync();
            Assert.Equal(3, gecmis.Count); // budama: yalnız son 3
            Assert.Equal("http://tempuri.org/I/Op5", gecmis[0].Aksiyon); // en yeni üstte
            Assert.True(gecmis.Single(g => g.Aksiyon.EndsWith("Op3", StringComparison.Ordinal)).FaultMu);
            Assert.Equal("<zarf>4</zarf>", gecmis[1].Zarf);

            // v16: Basic auth alanları (kullanıcı adı + şifreli parola) da saklanır/geri okunur.
            await depo.OrtamKaydetAsync(new SoapOrtamKaydi("Test", "http://t/x?wsdl", "http://t/x", "gtbuser", "SIFRELI1"));
            await depo.OrtamKaydetAsync(new SoapOrtamKaydi("Canlı", "http://c/x?wsdl", "http://c/x"));
            await depo.OrtamKaydetAsync(new SoapOrtamKaydi("Test", "http://t2/x?wsdl", "http://t2/x", "gtbuser2", "SIFRELI2")); // üzerine

            IReadOnlyList<SoapOrtamKaydi> ortamlar = await depo.OrtamlarAsync();
            Assert.Equal(2, ortamlar.Count);
            SoapOrtamKaydi test = ortamlar.Single(o => o.Ad == "Test");
            Assert.Equal("http://t2/x", test.Adres);          // güncellendi
            Assert.Equal("gtbuser2", test.KullaniciAdi);      // auth kullanıcı adı güncellendi
            Assert.Equal("SIFRELI2", test.ParolaSifreli);     // şifreli parola güncellendi
            Assert.Null(ortamlar.Single(o => o.Ad == "Canlı").KullaniciAdi); // kimliksiz ortam null kalır
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // havuz dosyayı bırakmadan silinemez
            File.Delete(dosya);
        }
    }
}
