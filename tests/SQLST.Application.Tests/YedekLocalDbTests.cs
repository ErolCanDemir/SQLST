using System.IO;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 💾 Yedek al — gerçek LocalDB (v23-S26 S1). Pencerenin kullandığı TÜM sorgular gerçek sunucuda: sunucu bilgisi
/// (LocalDB: Express motoru, sıkıştırma yok, yedek klasörü boş), xp_dirtree, tarihçe; üretilen script gerçekten
/// BACKUP alır → dosya diskte, msdb'de kayıt; SIMPLE'da BACKUP LOG 4208 ile düşer (Türkçe çeviri doğrulanır).
/// Her test kendi geçici veritabanını kurar ve siler.
/// </summary>
public class YedekLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB", Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());

    private static Task<QueryResult> Kos(string sql, string? db = null, int timeout = 60)
        => Executor.ExecuteAsync(Profil(), sql, new ExecuteOptions { VeritabaniOverride = db, KomutTimeoutSnOverride = timeout }, CancellationToken.None);

    [Fact]
    public async Task Sunucu_bilgisi_klasor_ve_gercek_yedek()
    {
        string db = "SqlstYedek_" + Guid.NewGuid().ToString("N")[..8];
        string klasor = Path.Combine(Path.GetTempPath(), "sqlst-yedek-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(klasor);
        Assert.Null((await Kos($"CREATE DATABASE [{db}];")).Hata);
        try
        {
            Assert.Null((await Kos("CREATE TABLE dbo.T (Id int PRIMARY KEY, Ad nvarchar(50)); INSERT dbo.T VALUES (1, N'Çınar');", db)).Hata);

            // sunucu bilgisi — LocalDB gerçeği
            QueryResult b = await Kos(YedekSorgulari.SunucuBilgisiSql(db));
            Assert.Null(b.Hata);
            YedekSunucuBilgisi bilgi = YedekSorgulari.SunucuBilgisiOku(b)!;
            Assert.Equal(4, bilgi.EngineEdition);
            Assert.False(bilgi.SikistirmaVar);
            Assert.Equal("SIMPLE", bilgi.KurtarmaModeli);
            Assert.True(bilgi.YedekIzni);
            Assert.True(string.IsNullOrEmpty(bilgi.YedekKlasoru));
            Assert.False(string.IsNullOrEmpty(bilgi.VarsayilanKlasor)); // veri klasörüne düşer

            // sunucu klasörü (xp_dirtree): bizim geçici klasör görünür mü
            File.WriteAllText(Path.Combine(klasor, "eski.bak"), "x");
            Directory.CreateDirectory(Path.Combine(klasor, "Arsiv"));
            QueryResult k = await Kos(YedekSorgulari.KlasorSql(klasor));
            Assert.Null(k.Hata);
            IReadOnlyList<KlasorGirisi> girisler = YedekSorgulari.KlasorOku(k);
            Assert.Equal(["Arsiv", "eski.bak"], girisler.Select(g => g.Ad));
            Assert.True(girisler[0].KlasorMu);

            // gerçek TAM yedek: pencerenin ürettiği script
            string dosya = YedekSorgulari.YolBirlestir(klasor, YedekSorgulari.DosyaAdiOner(db, YedekTuru.Tam, DateTime.Now));
            var ayar = new YedekAyari(db, YedekTuru.Tam, dosya, false, true, false, true, "test tam", "SQLST testi");
            Assert.Empty(YedekSorgulari.Dogrula(ayar, bilgi));
            QueryResult y = await Kos(YedekSorgulari.Script(ayar, DateTime.Now, "localdb"), db, 0);
            Assert.Null(y.Hata);
            Assert.True(File.Exists(dosya), "yedek dosyası yok: " + dosya);
            Assert.Contains(y.Mesajlar, m => m.Contains("100 percent processed", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(y.Mesajlar, m => m.Contains("BACKUP DATABASE successfully", StringComparison.OrdinalIgnoreCase));

            // tarihçe: msdb kaydı
            QueryResult t = await Kos(YedekSorgulari.TarihceSql(db), "msdb");
            Assert.Null(t.Hata);
            YedekKaydi kayit = Assert.Single(YedekSorgulari.TarihceOku(t));
            Assert.Equal(YedekTuru.Tam, kayit.Tur);
            Assert.Equal(dosya, kayit.Dosya);
            Assert.True(kayit.BoyutBayt > 0);
            Assert.False(kayit.KopyaMi);

            // fark yedeği de alınır (tam var); günlük SIMPLE'da düşer → Türkçe
            var fark = ayar with { Tur = YedekTuru.Fark, Dosya = YedekSorgulari.YolBirlestir(klasor, "fark.bak") };
            Assert.Null((await Kos(YedekSorgulari.Script(fark, DateTime.Now, "localdb"), db, 0)).Hata);
            var gunluk = ayar with { Tur = YedekTuru.Gunluk, Dosya = YedekSorgulari.YolBirlestir(klasor, "g.trn") };
            Assert.Contains("SIMPLE", YedekSorgulari.Dogrula(gunluk, bilgi)[0]);
            QueryResult g = await Kos(YedekSorgulari.Script(gunluk, DateTime.Now, "localdb"), db, 0);
            Assert.NotNull(g.Hata);
            Assert.Equal(4208, g.Hata!.Numara);
            Assert.Contains("günlük yedeği alınamaz", YedekSorgulari.HataMetni(g.Hata));

            // ilerleme sorgusu boşta boş döner (koşan yedek yok)
            Assert.Null(YedekSorgulari.IlerlemeOku(await Kos(YedekSorgulari.IlerlemeSql(db))));
        }
        finally
        {
            await Kos($"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];");
            await Kos($"EXEC msdb.dbo.sp_delete_database_backuphistory @database_name = N'{db}';", "msdb");
            try { Directory.Delete(klasor, true); } catch (IOException) { }
        }
    }
}
