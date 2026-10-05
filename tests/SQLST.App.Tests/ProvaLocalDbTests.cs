using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 🔍 Sorgu provası (V15-S2, BF-2) — GERÇEK LocalDB'ye karşı VM testi: prova doğru SAYAR,
/// örnek satırları GÖSTERİR ve tabloya DOKUNMAZ; "Şimdi çalıştır" ise gerçekten çalıştırır.
/// Dispatcher thread'inde koşar (CanliDenetimTests dersi: VM'in timer/band üyeleri UI thread'ine ait).
/// </summary>
public class ProvaLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    [Fact]
    public void Prova_sayar_gosterir_degistirmez_onay_gercekten_calistirir()
    {
        Dispatcher d = StaOrtak.Sta();
        var executor = new SqlExecutor(new LehceSaglayici(new DpapiSecretProtector()));
        Task<QueryResult> Kos(string sql) => executor.ExecuteAsync(Profil(), sql,
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);

        Kos("""
            IF OBJECT_ID('tempdb.dbo.SqlstProva') IS NOT NULL DROP TABLE tempdb.dbo.SqlstProva;
            CREATE TABLE tempdb.dbo.SqlstProva (Id int PRIMARY KEY, Sehir nvarchar(20), Durum nvarchar(10));
            INSERT INTO tempdb.dbo.SqlstProva VALUES (1, N'İzmir', N'Aktif'), (2, N'İzmir', N'Aktif'), (3, N'Ankara', N'Aktif');
            """).GetAwaiter().GetResult();
        try
        {
            SorguSekmesiViewModel vm = d.Invoke(() =>
            {
                var saglayici = new LehceSaglayici(new DpapiSecretProtector());
                ConnectionProfile profil = Profil();
                var sekme = new SorguSekmesiViewModel(
                    new QueryService(), new OturumFabrikasi(saglayici), saglayici,
                    () => profil, () => false, () => false, () => 5, new ObservableCollection<string>(), "SQLST1");
                sekme.SecilenVeritabani = "tempdb";
                return sekme;
            });

            // 1) Prova: 2 satır sayılır, örnek grid dolu, band SET kolonunu söyler — tablo DEĞİŞMEZ.
            string? band = d.InvokeAsync(async () =>
            {
                vm.Belge.Text = "UPDATE dbo.SqlstProva SET Durum = N'Pasif' WHERE Sehir = N'İzmir'";
                await vm.ProvaAsync();
                return vm.ProvaBandi;
            }).Task.Unwrap().GetAwaiter().GetResult();

            Assert.NotNull(band);
            Assert.Contains("2 satır etkilenecek", band);
            Assert.Contains("Durum", band); // SET edilecek kolon
            d.Invoke(() =>
            {
                Assert.Single(vm.SonucSetleri);
                Assert.Equal(2, vm.SonucSetleri[0].Set.SatirSayisi); // etkilenecek ilk satırlar
            });

            QueryResult once = Kos("SELECT COUNT(*) FROM tempdb.dbo.SqlstProva WHERE Durum = N'Pasif'")
                .GetAwaiter().GetResult();
            Assert.Equal(0, Convert.ToInt32(once.ResultSetler[0].Satirlar[0][0])); // prova HİÇBİR ŞEY değiştirmedi

            // 2) "Şimdi çalıştır": prova edilen metin olağan yoldan koşar, band söner.
            d.InvokeAsync(async () =>
            {
                await ((IAsyncRelayCommand)vm.ProvaOnaylaCommand).ExecuteAsync(null);
            }).Task.Unwrap().GetAwaiter().GetResult();

            QueryResult sonra = Kos("SELECT COUNT(*) FROM tempdb.dbo.SqlstProva WHERE Durum = N'Pasif'")
                .GetAwaiter().GetResult();
            Assert.Equal(2, Convert.ToInt32(sonra.ResultSetler[0].Satirlar[0][0])); // UPDATE gerçekten koştu
            d.Invoke(() => Assert.Null(vm.ProvaBandi));
        }
        finally
        {
            Kos("IF OBJECT_ID('tempdb.dbo.SqlstProva') IS NOT NULL DROP TABLE tempdb.dbo.SqlstProva;")
                .GetAwaiter().GetResult();
        }
    }
}
