using System.Collections.ObjectModel;
using System.Windows.Threading;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// Canlı sözdizimi denetimi (kullanıcı isteği 2026-07-26: "kırmızı yazarken yansın") —
/// GERÇEK LocalDB'ye karşı VM testi: bozuk SQL PARSEONLY ile yakalanır ve HataSatiri
/// BELGE satırına eşlenir; geçerli SQL çizgiyi söndürür; denetim HİÇBİR ŞEY ÇALIŞTIRMAZ
/// (DML parse edilir ama tablo değişmez). Denetim uygulamadaki gibi DISPATCHER thread'inde
/// koşar: await sonrası Belge.Text okuması TextDocument sahiplik kuralına tabidir —
/// thread-pool'dan okumak fırlatır ve sessiz catch'e düşerdi (ilk test koşusunda görüldü).
/// </summary>
public class CanliDenetimTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    /// <summary>Belgeyi yazar, canlı denetimi bekler, HataSatiri'ni döndürür — hepsi dispatcher'da.</summary>
    private static int? Denetle(Dispatcher d, SorguSekmesiViewModel sekme, string sql)
        => d.InvokeAsync(async () =>
        {
            sekme.Belge.Text = sql;
            await sekme.CanliDenetleAsync();
            return sekme.HataSatiri;
        }).Task.Unwrap().GetAwaiter().GetResult();

    [Fact]
    public void Bozuk_sql_yazarken_yakalanir_duzeltilince_soner_ve_hicbir_sey_calismaz()
    {
        Dispatcher d = StaOrtak.Sta();
        SorguSekmesiViewModel sekme = d.Invoke(() =>
        {
            var saglayici = new LehceSaglayici(new DpapiSecretProtector());
            ConnectionProfile profil = Profil();
            var vm = new SorguSekmesiViewModel(
                new QueryService(), new OturumFabrikasi(saglayici), saglayici,
                () => profil, () => false, () => false, () => 5, new ObservableCollection<string>(), "SQLST1");
            vm.SecilenVeritabani = "tempdb";
            return vm;
        });

        // 1) Bozuk SQL: 2. BELGE satırında hata — PARSEONLY sarmalının 1 satırlık kayması geri eşlenmeli.
        Assert.Equal(2, Denetle(d, sekme, "SELECT 1\nSELEC * FROM sys.objects"));

        // 2) Geçerli SQL: çizgi söner.
        Assert.Null(Denetle(d, sekme, "SELECT name FROM sys.objects"));

        // 3) DML bile PARSE edilir ama ÇALIŞMAZ: tempdb'de tablo kur, DELETE yaz, denetle —
        //    satırlar YERİNDE durmalı (canlı denetim asla yürütmez).
        var executor = new SqlExecutor(new LehceSaglayici(new DpapiSecretProtector()));
        executor.ExecuteAsync(Profil(), """
            IF OBJECT_ID('tempdb.dbo.SqlstCanliDenetim') IS NOT NULL DROP TABLE tempdb.dbo.SqlstCanliDenetim;
            CREATE TABLE tempdb.dbo.SqlstCanliDenetim (Id int);
            INSERT INTO tempdb.dbo.SqlstCanliDenetim VALUES (1), (2);
            """, new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None)
            .GetAwaiter().GetResult();
        try
        {
            Assert.Null(Denetle(d, sekme, "DELETE FROM tempdb.dbo.SqlstCanliDenetim;")); // geçerli — çizgi yok

            QueryResult kontrol = executor.ExecuteAsync(Profil(),
                "SELECT COUNT(*) FROM tempdb.dbo.SqlstCanliDenetim",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.Equal(2, Convert.ToInt32(kontrol.ResultSetler[0].Satirlar[0][0])); // DELETE KOŞMADI
        }
        finally
        {
            executor.ExecuteAsync(Profil(),
                "IF OBJECT_ID('tempdb.dbo.SqlstCanliDenetim') IS NOT NULL DROP TABLE tempdb.dbo.SqlstCanliDenetim;",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }
}
