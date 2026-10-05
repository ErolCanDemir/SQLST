using System.Collections.ObjectModel;
using System.Windows.Threading;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// ⏪ Geri Al paketi (V15-S3, BF-1) — GERÇEK LocalDB + Güvenli Yazma akışı: DML'den önce eski hal
/// yakalanır; paket YALNIZ kullanıcı COMMIT derse depoya düşer, ROLLBACK'te düşmez.
/// </summary>
public class GeriAlLocalDbTests
{
    private sealed class BellekDepo : IGeriAlDeposu
    {
        public List<GeriAlPaketi> Paketler { get; } = [];

        public Task<long> EkleAsync(GeriAlPaketi paket, CancellationToken ct = default)
        {
            Paketler.Add(paket with { Id = Paketler.Count + 1 });
            return Task.FromResult((long)Paketler.Count);
        }

        public Task<IReadOnlyList<GeriAlPaketi>> ListeleAsync(int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GeriAlPaketi>>(Paketler);

        public Task<GeriAlPaketi?> GetirAsync(long id, CancellationToken ct = default)
            => Task.FromResult(Paketler.FirstOrDefault(p => p.Id == id));

        public Task SilAsync(long id, CancellationToken ct = default)
        {
            Paketler.RemoveAll(p => p.Id == id);
            return Task.CompletedTask;
        }
    }

    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    [Fact]
    public void Commit_paketler_rollback_paketlemez()
    {
        Dispatcher d = StaOrtak.Sta();
        var executor = new SqlExecutor(new LehceSaglayici(new DpapiSecretProtector()));
        Task<QueryResult> Kos(string sql) => executor.ExecuteAsync(Profil(), sql,
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);

        Kos("""
            IF OBJECT_ID('tempdb.dbo.SqlstGeriAl') IS NOT NULL DROP TABLE tempdb.dbo.SqlstGeriAl;
            CREATE TABLE tempdb.dbo.SqlstGeriAl (Id int PRIMARY KEY, Ad nvarchar(20), Durum nvarchar(10));
            INSERT INTO tempdb.dbo.SqlstGeriAl VALUES (1, N'Ahmet', N'Aktif'), (2, N'Ayşe', N'Aktif');
            """).GetAwaiter().GetResult();
        try
        {
            var bellekDepo = new BellekDepo();
            SorguSekmesiViewModel vm = d.Invoke(() =>
            {
                var saglayici = new LehceSaglayici(new DpapiSecretProtector());
                ConnectionProfile profil = Profil();
                var sekme = new SorguSekmesiViewModel(
                    new QueryService(), new OturumFabrikasi(saglayici), saglayici,
                    () => profil, () => false, () => true /* Güvenli Yazma AÇIK */, () => 300,
                    new ObservableCollection<string>(), "SQLST1");
                sekme.SecilenVeritabani = "tempdb";
                sekme.GeriAlDeposu = bellekDepo;
                return sekme;
            });

            Task Kosum(string sql) => d.InvokeAsync(async () =>
            {
                vm.Belge.Text = sql;
                await vm.CalistirAsync();
            }).Task.Unwrap();

            // 1) UPDATE → Güvenli Yazma bandı açık; COMMIT → paket ESKİ değerlerle depoda.
            Kosum("UPDATE dbo.SqlstGeriAl SET Durum = N'Pasif' WHERE Durum = N'Aktif'").GetAwaiter().GetResult();
            Assert.True(d.Invoke(() => vm.IslemAcik));
            d.InvokeAsync(() => vm.CommitAsync()).Task.Unwrap().GetAwaiter().GetResult();

            GeriAlPaketi paket = Assert.Single(bellekDepo.Paketler);
            Assert.Equal("UPDATE", paket.Fiil);
            Assert.Equal("dbo.SqlstGeriAl", paket.Tablo);
            Assert.Equal(2, paket.SatirSayisi);
            Assert.Equal(["Id"], GeriAlSerilestirici.PkOku(paket));
            IReadOnlyList<string?[]> eski = GeriAlSerilestirici.SatirlariOku(paket);
            Assert.All(eski, satir => Assert.Equal("Aktif", satir[2])); // paket ESKİ hali taşır
            QueryResult commitSonrasi = Kos("SELECT COUNT(*) FROM tempdb.dbo.SqlstGeriAl WHERE Durum = N'Pasif'")
                .GetAwaiter().GetResult();
            Assert.Equal(2, Convert.ToInt32(commitSonrasi.ResultSetler[0].Satirlar[0][0])); // veri gerçekten değişti

            // 2) İkinci DML → ROLLBACK: paket EKLENMEZ, veri geri döner.
            Kosum("DELETE FROM dbo.SqlstGeriAl WHERE Id = 1").GetAwaiter().GetResult();
            Assert.True(d.Invoke(() => vm.IslemAcik));
            d.InvokeAsync(() => vm.RollbackAsync()).Task.Unwrap().GetAwaiter().GetResult();

            Assert.Single(bellekDepo.Paketler); // hâlâ yalnız COMMIT'in paketi
            QueryResult rollbackSonrasi = Kos("SELECT COUNT(*) FROM tempdb.dbo.SqlstGeriAl")
                .GetAwaiter().GetResult();
            Assert.Equal(2, Convert.ToInt32(rollbackSonrasi.ResultSetler[0].Satirlar[0][0])); // satır silinmedi
        }
        finally
        {
            Kos("IF OBJECT_ID('tempdb.dbo.SqlstGeriAl') IS NOT NULL DROP TABLE tempdb.dbo.SqlstGeriAl;")
                .GetAwaiter().GetResult();
        }
    }
}
