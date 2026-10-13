using System.IO;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// ⏪ Geri yükleme — gerçek LocalDB (v23-S26 S2). FULL modele alınan geçici DB'den aynı dosyaya tam + fark + günlük
/// yedeği (3 set); HEADERONLY/FILELISTONLY gerçek ayrıştırma; zincir (günlük → tam+fark+günlük); üretilen script
/// YENİ ada MOVE ile geri yükler → veri doğrulanır; sonra REPLACE + SINGLE_USER ile ÜZERİNE yazar; VERIFYONLY geçer.
/// Her şey finally'de temizlenir (iki DB, msdb tarihçesi, dosyalar).
/// </summary>
public class GeriYuklemeLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB", Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());

    private static Task<QueryResult> Kos(string sql, string? db = null, int timeout = 120)
        => Executor.ExecuteAsync(Profil(), sql, new ExecuteOptions { VeritabaniOverride = db, KomutTimeoutSnOverride = timeout }, CancellationToken.None);

    private static async Task<int> Sayi(string db)
    {
        QueryResult r = await Kos("SELECT COUNT(*) FROM dbo.T;", db);
        Assert.Null(r.Hata);
        return Convert.ToInt32(r.ResultSetler[0].Satirlar[0][0]);
    }

    [Fact]
    public async Task Zincirli_geri_yukleme_yeni_ada_ve_ustune()
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string db = "SqlstGeri_" + id, kopya = "SqlstGeriKopya_" + id;
        string klasor = Path.Combine(Path.GetTempPath(), "sqlst-geri-" + id);
        Directory.CreateDirectory(klasor);
        string dosya = Path.Combine(klasor, "zincir.bak");
        Assert.Null((await Kos($"CREATE DATABASE [{db}]; ALTER DATABASE [{db}] SET RECOVERY FULL;")).Hata);
        try
        {
            Assert.Null((await Kos("CREATE TABLE dbo.T (Id int PRIMARY KEY); INSERT dbo.T VALUES (1);", db)).Hata);
            // tam (set 1) → ekle → fark (set 2, aynı dosyaya NOINIT) → ekle → günlük (set 3)
            Assert.Null((await Kos(YedekSorgulari.Script(new YedekAyari(db, YedekTuru.Tam, dosya, false, true, false, true, "tam", null), DateTime.Now, "l"), db, 0)).Hata);
            Assert.Null((await Kos("INSERT dbo.T VALUES (2);", db)).Hata);
            Assert.Null((await Kos(YedekSorgulari.Script(new YedekAyari(db, YedekTuru.Fark, dosya, false, true, false, false, "fark", null), DateTime.Now, "l"), db, 0)).Hata);
            Assert.Null((await Kos("INSERT dbo.T VALUES (3);", db)).Hata);
            Assert.Null((await Kos(YedekSorgulari.Script(new YedekAyari(db, YedekTuru.Gunluk, dosya, false, true, false, false, "gunluk", null), DateTime.Now, "l"), db, 0)).Hata);

            // sunucu bilgisi
            GeriYuklemeBilgisi bilgi = GeriYuklemeSorgulari.BilgiOku(await Kos(GeriYuklemeSorgulari.BilgiSql()))!;
            Assert.True(bilgi.VeritabaniVar(db));
            Assert.False(bilgi.VeritabaniVar(kopya));
            Assert.True(bilgi.Sysadmin || bilgi.OlusturmaIzni);

            // HEADERONLY: 3 set, türler ve LSN zinciri gerçek
            QueryResult h = await Kos(GeriYuklemeSorgulari.HeaderSql(dosya));
            Assert.Null(h.Hata);
            IReadOnlyList<YedekSeti> setler = GeriYuklemeSorgulari.SetleriOku(h);
            Assert.Equal([YedekTuru.Tam, YedekTuru.Fark, YedekTuru.Gunluk], setler.Select(s => s.Tur));
            Assert.Equal([1, 2, 3], setler.Select(s => s.Konum));
            Assert.All(setler, s => Assert.Equal(db, s.Veritabani));
            Assert.Equal(setler[0].CheckpointLsn, setler[1].DatabaseBackupLsn); // farkın tabanı bu tam
            (IReadOnlyList<YedekSeti> zincir, string? uyari) = GeriYuklemeSorgulari.ZincirKur(setler, 3);
            Assert.Null(uyari);
            Assert.Equal([1, 2, 3], zincir.Select(s => s.Konum));

            // FILELISTONLY → MOVE yeni ada
            QueryResult f = await Kos(GeriYuklemeSorgulari.FileListSql(dosya, 1));
            Assert.Null(f.Hata);
            IReadOnlyList<YedekDosyasi> dosyalar = GeriYuklemeSorgulari.DosyalariOku(f);
            Assert.Equal(2, dosyalar.Count);
            Assert.Contains(dosyalar, d => d.Tur == "L");
            IReadOnlyList<DosyaTasima> tasimalar = GeriYuklemeSorgulari.HedefYollariOner(dosyalar, db, kopya,
                bilgi with { VeriKlasoru = klasor, GunlukKlasoru = klasor });
            Assert.All(tasimalar, t => Assert.StartsWith(klasor, t.Hedef));

            // VERIFYONLY
            Assert.Null((await Kos(GeriYuklemeSorgulari.VerifySql(dosya, 3), null, 0)).Hata);

            // yeni ada zincirli geri yükleme → 3 satır (tam 1 + fark 2 + günlük 3)
            var yeni = new GeriYuklemeAyari(dosya, [1, 2, 3], kopya, tasimalar, false, false, false, false);
            Assert.Empty(GeriYuklemeSorgulari.Dogrula(yeni, zincir, bilgi));
            QueryResult g1 = await Kos(GeriYuklemeSorgulari.Script(yeni, zincir, DateTime.Now, "l"), "master", 0);
            Assert.Null(g1.Hata);
            Assert.Equal(3, await Sayi(kopya));
            Assert.Contains(g1.Mesajlar, m => m.Contains("RESTORE LOG successfully", StringComparison.OrdinalIgnoreCase));

            // kaynağı boz → ÜZERİNE yalnız tam set (REPLACE + bağlantıları kes) → 1 satıra döner
            Assert.Null((await Kos("INSERT dbo.T VALUES (99);", db)).Hata);
            Assert.Equal(4, await Sayi(db));
            (IReadOnlyList<YedekSeti> tamZincir, _) = GeriYuklemeSorgulari.ZincirKur(setler, 1);
            IReadOnlyList<DosyaTasima> yerinde = GeriYuklemeSorgulari.HedefYollariOner(dosyalar, db, db, bilgi);
            var ustune = new GeriYuklemeAyari(dosya, [1], db, yerinde, true, true, false, true);
            Assert.Contains("üzerine yaz", GeriYuklemeSorgulari.Dogrula(ustune with { Replace = false }, tamZincir, bilgi)[0]);
            QueryResult g2 = await Kos(GeriYuklemeSorgulari.Script(ustune, tamZincir, DateTime.Now, "l"), "master", 0);
            Assert.Null(g2.Hata);
            Assert.Equal(1, await Sayi(db));
            QueryResult kip = await Kos($"SELECT user_access_desc FROM sys.databases WHERE name = N'{db}';");
            Assert.Equal("MULTI_USER", kip.ResultSetler[0].Satirlar[0][0]);
        }
        finally
        {
            foreach (string ad in new[] { db, kopya })
                await Kos($"IF DB_ID(N'{ad}') IS NOT NULL BEGIN ALTER DATABASE [{ad}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{ad}]; END");
            await Kos($"EXEC msdb.dbo.sp_delete_database_backuphistory @database_name = N'{db}'; EXEC msdb.dbo.sp_delete_database_backuphistory @database_name = N'{kopya}';", "msdb");
            try { Directory.Delete(klasor, true); } catch (IOException) { }
        }
    }
}
