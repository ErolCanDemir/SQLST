using System.IO;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v6 "Tümünü dışa aktar" CANLI doğrulaması (LocalDB). Kritik iddia: akışlı CSV yazımı
/// 100.000'lik GRID sınırına TAKILMAZ — grid görüntüleme sınırıdır, dosyaya aktarma tümünü
/// alır. Bu yüzden test bilerek sınırın ÜSTÜNDE (120.000) satır kurar ve dosyada tümünün
/// olduğunu sayar. "Çalıştı" demek yetmez; SATIR SAYISI sınırı aştığını kanıtlar.
/// </summary>
public class TopluDisaAktarLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());
    private static readonly ExecuteOptions Tempdb = new() { VeritabaniOverride = "tempdb" };

    [Fact]
    public async Task Akis_100k_grid_sinirini_ASAR()
    {
        const int adet = 120_000; // grid sınırı 100.000 → akış bunu aşmalı
        string tablo = $"Aktar_{Guid.NewGuid():N}";

        // Değerler önemsiz (sayım testi); ROW_NUMBER/ORDER BY YOK → gereksiz sort olmasın (hız).
        await Executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{tablo}] (n INT, s NVARCHAR(20));
            INSERT INTO dbo.[{tablo}] (n, s)
            SELECT TOP {adet} 1, N'satır'
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """, Tempdb, CancellationToken.None);

        string dosya = Path.Combine(Path.GetTempPath(), $"sqlst-aktar-test-{Guid.NewGuid():N}.csv");
        var fabrika = new OturumFabrikasi(new MssqlLehcesi(new DpapiSecretProtector()));
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        try
        {
            long yazilan = await CsvYazici.AkislaDosyayaYazAsync(
                oturum, $"SELECT n, s FROM dbo.[{tablo}]",
                Tempdb, dosya, ct: CancellationToken.None);

            Assert.Equal(adet, yazilan); // grid'in 100k'sını AŞTI

            // Dosyada gerçekten adet+1 satır var mı (başlık + veri)?
            int satirSayisi = File.ReadLines(dosya).Count();
            Assert.Equal(adet + 1, satirSayisi);
        }
        finally
        {
            if (File.Exists(dosya))
                File.Delete(dosya);
            await Executor.ExecuteAsync(Profil(),
                $"DROP TABLE IF EXISTS dbo.[{tablo}];", Tempdb, CancellationToken.None);
        }
    }
}
