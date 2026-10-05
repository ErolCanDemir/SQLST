using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// Sekme içi Edit sorgusu (kullanıcı düzeltmesi 2026-07-26: "sorguyu aynı pencerede yazıp
/// SONUCUNU düzenlemek istiyorum") — GERÇEK LocalDB'ye karşı VM düzeyi test:
///  • FiltreSql boş → tablonun ilk 200 satırı, düzenlenebilir (eski davranış korunur).
///  • FiltreSql WHERE'li ve PK'yı SELECT ediyor → daralan sonuç, hâlâ DÜZENLENEBİLİR.
///  • FiltreSql PK'sız → grid SALT-OKUNUR + bilgi satırı nedeni (PK adlarıyla) söyler.
/// </summary>
public class DuzenlemeFiltreCanliTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    [Fact]
    public async Task Filtre_sorgusu_sonucu_duzenlenir_pk_yoksa_salt_okunur()
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        var executor = new SqlExecutor(new DpapiSecretProtector());
        var semaServisi = new SchemaService(executor, saglayici);

        const string kur = """
            IF OBJECT_ID('tempdb.dbo.SqlstEditFiltre') IS NOT NULL DROP TABLE tempdb.dbo.SqlstEditFiltre;
            CREATE TABLE tempdb.dbo.SqlstEditFiltre (Id int PRIMARY KEY, Ad nvarchar(50), Sehir nvarchar(50));
            INSERT INTO tempdb.dbo.SqlstEditFiltre VALUES
              (1, N'Ali', N'İzmir'), (2, N'Ayşe', N'Ankara'), (3, N'Can', N'İzmir');
            """;
        QueryResult hazir = await executor.ExecuteAsync(Profil(), kur,
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        Assert.True(hazir.Basarili, hazir.Hata?.Mesaj);

        var tablo = new SemaNesnesi("tempdb", "dbo", "SqlstEditFiltre", SemaNesneTuru.Tablo, [], []);
        try
        {
            var vm = new DuzenlemeSekmesiViewModel(
                semaServisi, new QueryService(), new OturumFabrikasi(saglayici),
                saglayici.Getir(MotorTuru.Mssql), Profil, tablo);

            // 1) Boş sorgu: tüm satırlar, düzenlenebilir.
            await vm.YukleAsync();
            Assert.False(vm.Salt);
            Assert.Equal(3, vm.Gorunum!.Count);

            // 2) WHERE'li + PK'lı sorgu: daralan sonuç, hâlâ düzenlenebilir.
            vm.FiltreSql = "SELECT Id, Ad FROM tempdb.dbo.SqlstEditFiltre WHERE Sehir = N'İzmir' ORDER BY Id";
            await vm.FiltreCalistir();
            Assert.False(vm.Salt);
            Assert.Equal(2, vm.Gorunum!.Count);
            Assert.Equal("Ali", vm.Gorunum[0]["Ad"]);

            // 3) PK'sız sorgu: salt-okunur + neden PK adıyla söylenir.
            vm.FiltreSql = "SELECT Ad FROM tempdb.dbo.SqlstEditFiltre";
            await vm.FiltreCalistir();
            Assert.True(vm.Salt);
            Assert.Contains("Id", vm.Bilgi); // "sorguya PK kolonlarını ekleyin (Id)"
        }
        finally
        {
            await executor.ExecuteAsync(Profil(),
                "IF OBJECT_ID('tempdb.dbo.SqlstEditFiltre') IS NOT NULL DROP TABLE tempdb.dbo.SqlstEditFiltre;",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        }
    }
}
