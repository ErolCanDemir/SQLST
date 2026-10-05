using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S4 Edit m.1 — kullanıcı: "Edit modda satır sınırı var, 178 gösteriyor — en az 1000 olmalı."
///
/// İki katmanlı sorun vardı: (a) sınır 200'dü; (b) kullanıcının gördüğü 178 aslında sınır bile
/// değildi — 96 MB'lık bayt bütçesi geniş satırlı tabloda 200'e varmadan kesiyordu ve kesildiği
/// HİÇBİR yerde yazılmıyordu. Düzeltme: sınır 1000 + Edit'e özel 256 MB bütçe + kesilme nedeni
/// bilgi satırında. GERÇEK LocalDB'ye karşı doğrulanır.
/// </summary>
public class DuzenlemeSinirCanliTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    [Fact]
    public async Task Bin_satirdan_buyuk_tabloda_1000_gosterilir_ve_sinir_soylenir()
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        var executor = new SqlExecutor(new DpapiSecretProtector());
        var semaServisi = new SchemaService(executor, saglayici);

        // 1.100 satır — eski sınır 200'dü, yenisi 1.000: ikisinin de üstünde.
        const string kur = """
            IF OBJECT_ID('tempdb.dbo.SqlstEditSinir') IS NOT NULL DROP TABLE tempdb.dbo.SqlstEditSinir;
            CREATE TABLE tempdb.dbo.SqlstEditSinir (Id int PRIMARY KEY, Ad nvarchar(50));
            ;WITH n AS (SELECT TOP (1100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                        FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO tempdb.dbo.SqlstEditSinir SELECT i, CONCAT(N'ad', i) FROM n;
            """;
        QueryResult hazir = await executor.ExecuteAsync(Profil(), kur,
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        Assert.True(hazir.Basarili, hazir.Hata?.Mesaj);

        var tablo = new SemaNesnesi("tempdb", "dbo", "SqlstEditSinir", SemaNesneTuru.Tablo, [], []);
        try
        {
            var vm = new DuzenlemeSekmesiViewModel(
                semaServisi, new QueryService(), new OturumFabrikasi(saglayici),
                saglayici.Getir(MotorTuru.Mssql), Profil, tablo);
            await vm.YukleAsync();

            // 1) Boş filtre: TOP(1000) üretilir — 1.000 satır ve "ilk 1.000" bilgisi.
            Assert.Equal(1_000, vm.Gorunum!.Count);       // ESKİDEN: 200
            Assert.False(vm.Salt);
            Assert.Contains("1.000", vm.Bilgi);

            // 2) Kullanıcının TOP'suz kendi sorgusu: okuyucu 1.000'de keser ve kesildiği artık
            //    SÖYLENİR (eskiden 178'de sessizce kesiliyordu — bu turun asıl şikâyeti).
            vm.FiltreSql = "SELECT Id, Ad FROM tempdb.dbo.SqlstEditSinir ORDER BY Id";
            await vm.FiltreCalistir();
            Assert.Equal(1_000, vm.Gorunum!.Count);
            Assert.Contains("sınır 1.000", vm.Bilgi);     // kesilme nedeni bilgi satırında
            Assert.Contains("WHERE", vm.Bilgi);           // ve daraltma yolu tarif ediliyor
        }
        finally
        {
            await executor.ExecuteAsync(Profil(),
                "IF OBJECT_ID('tempdb.dbo.SqlstEditSinir') IS NOT NULL DROP TABLE tempdb.dbo.SqlstEditSinir;",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Edit m.2 köprüsü: FiltreSql ↔ AvalonEdit belgesi iki yönlü senkron (tek thread'de —
    /// TextDocument thread-affine olduğundan UI dışı erişim modeli budur; bkz. VM belgeleri).
    /// </summary>
    [Fact]
    public void FiltreSql_ve_belge_ayni_metni_paylasir()
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        var vm = new DuzenlemeSekmesiViewModel(
            new SchemaService(new SqlExecutor(new DpapiSecretProtector()), saglayici),
            new QueryService(), new OturumFabrikasi(saglayici),
            saglayici.Getir(MotorTuru.Mssql), Profil,
            new SemaNesnesi("db", "dbo", "T", SemaNesneTuru.Tablo, [], []));

        vm.FiltreSql = "SELECT 1";                       // eski API (testler/çağıranlar)
        Assert.Equal("SELECT 1", vm.FiltreBelgesi.Text); // editör belgesine yansır

        vm.FiltreBelgesi.Text = "SELECT 2";              // editörden yazım (TextChanged köprüsü)
        Assert.Equal("SELECT 2", vm.FiltreSql);          // sorgu koşucusunun gördüğü metin
    }
}
