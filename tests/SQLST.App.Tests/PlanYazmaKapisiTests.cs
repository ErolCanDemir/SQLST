using System.Collections.ObjectModel;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// KULLANICI BULGUSU (2026-07-20): <c>DROP PROCEDURE … CREATE PROCEDURE …</c> için plan
/// istendiğinde araç "GERÇEK plan sorguyu ÇALIŞTIRIR … yine de çalıştırılsın mı?" diye
/// soruyor, üstelik kullanıcıyı <i>"Yalnız planı görmek istiyorsanız 'Tahmini plan'
/// kullanın"</i> diye <b>ARTIK VAR OLMAYAN</b> bir düğmeye yönlendiriyordu (2026-07-19'da
/// motor başına tek plan düğmesine geçilmişti).
///
/// Doğru davranış: <b>ölçümlü plan yalnız OKUMA sorgularında alınır.</b> Yazma/DDL'de
/// sorgu hiç çalıştırılmaz — plan yine de tam olarak gelir, çünkü SHOWPLAN_XML ifadeyi
/// yalnız DERLER. Kullanıcıya sorulacak bir şey yoktur: doğru cevabı araç zaten bilir.
/// </summary>
public class PlanYazmaKapisiTests
{
    private static SorguSekmesiViewModel Kur(ConnectionProfile profil, ISqlExecutor executor)
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        return new SorguSekmesiViewModel(
            new QueryService(),
            new OturumFabrikasi(saglayici), saglayici,
            () => profil,
            kirliOkumaGetir: () => false, guvenliYazmaGetir: () => false, rollbackSnGetir: () => 300,
            new ObservableCollection<string>(["db"]), "test.sql");
    }

    /// <summary>
    /// Kullanıcının ekran görüntüsündeki tam senaryo. Eski kodda bu onay kutusu AÇILIRDI;
    /// artık hiç sorulmaz. Onay geri gelirse bu test düşer.
    /// </summary>
    [Theory]
    [InlineData("DROP PROCEDURE IF EXISTS [satis].[spGetSiparisler];\nGO\n"
              + "CREATE PROCEDURE satis.spGetSiparisler @MusteriId INT AS "
              + "SELECT * FROM satis.Siparis WHERE MusteriId = @MusteriId")]
    [InlineData("DELETE FROM Musteri WHERE Id = 1;")]
    [InlineData("UPDATE Musteri SET Ad = 'x' WHERE Id = 1;")]
    [InlineData("ALTER TABLE Musteri ADD Notlar NVARCHAR(50);")]
    public async Task Yazma_planinda_kullaniciya_HIC_SORULMAZ(string sql)
    {
        var executor = new SahteExecutor();
        SorguSekmesiViewModel sekme = Kur(Profiller.Yap(MotorTuru.Mssql), executor);
        sekme.Belge.Text = sql;
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        var sorulanlar = new List<string>();
        sekme.OnayIste = soru => { sorulanlar.Add(soru); return true; };

        await sekme.PlanAlAsync(gercek: true);

        Assert.Empty(sorulanlar);
    }

    /// <summary>
    /// Kaldırılan uyarının METNİ de regresyona karşı korunur: "Tahmini plan" diye bir
    /// düğme yoktur, kullanıcıyı oraya yönlendiren hiçbir metin kalmamalıdır.
    /// </summary>
    [Fact]
    public async Task Olmayan_Tahmini_plan_dugmesine_yonlendirme_YAPILMAZ()
    {
        var executor = new SahteExecutor();
        SorguSekmesiViewModel sekme = Kur(Profiller.Yap(MotorTuru.Mssql), executor);
        sekme.Belge.Text = "DELETE FROM Musteri WHERE Id = 1;";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        var sorulanlar = new List<string>();
        sekme.OnayIste = soru => { sorulanlar.Add(soru); return false; };

        (SorguPlani? _, string? hata) = await sekme.PlanAlAsync(gercek: true);

        Assert.DoesNotContain(sorulanlar, s => s.Contains("Tahmini plan", StringComparison.Ordinal));
        Assert.False(hata?.Contains("Tahmini plan", StringComparison.Ordinal) == true);
        // "Vazgeçildi" da olmamalı: vazgeçilecek bir soru sorulmadı.
        Assert.False(hata?.Contains("Vazgeçildi", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Salt-okunur kapısı KORUNUR — bu dilim onu gevşetmemelidir. (Test kümesinin
    /// boş yere geçmediğinin de kanıtı: aynı yolda bir kapı hâlâ ateşleniyor.)
    /// </summary>
    [Fact]
    public async Task Salt_okunur_profilde_yazma_plani_hala_REDDEDILIR()
    {
        var executor = new SahteExecutor();
        SorguSekmesiViewModel sekme = Kur(
            Profiller.Yap(MotorTuru.Mssql, saltOkunur: true), executor);
        sekme.Belge.Text = "DELETE FROM Musteri WHERE Id = 1;";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        (SorguPlani? plan, string? hata) = await sekme.PlanAlAsync(gercek: true);

        Assert.Null(plan);
        Assert.NotNull(hata);
        Assert.Empty(executor.Cagrilar);
    }
}
