using System.Collections.ObjectModel;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// Kullanıcı bulgusunun (2026-07-20) CANLI kanıtı — LocalDB.
///
/// <b>Neden sahte executor yetmez?</b> "Onay sorulmadı" testi niyeti kanıtlar, sonucu
/// kanıtlamaz. Asıl soru şudur: yazma/DDL ifadesinin planı istendiğinde <b>ifade gerçekten
/// çalışmıyor mu?</b> Bunu ancak gerçek bir sunucuda, "prosedür oluşmuş mu?" diye sorarak
/// kanıtlayabiliriz. Eski davranışta kullanıcı "Evet" derse prosedür GERÇEKTEN oluşuyordu.
/// </summary>
public class PlanYazmaCanliTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Motor = MotorTuru.Mssql, Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());

    private static SorguSekmesiViewModel Kur()
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        return new SorguSekmesiViewModel(
            new QueryService(),
            new OturumFabrikasi(saglayici), saglayici,
            Profil,
            kirliOkumaGetir: () => false, guvenliYazmaGetir: () => false, rollbackSnGetir: () => 300,
            new ObservableCollection<string>(["tempdb"]), "test.sql")
        {
            SecilenVeritabani = "tempdb",
        };
    }

    /// <summary>
    /// Kullanıcının ekran görüntüsündeki senaryonun tıpkısı: CREATE PROCEDURE'ün planı
    /// istenir. Plan GELMELİ ama prosedür OLUŞMAMALIDIR.
    /// </summary>
    [Fact]
    public async Task CREATE_PROCEDURE_plani_alinir_ama_prosedur_OLUSMAZ()
    {
        string ad = $"spPlanTest_{Guid.NewGuid():N}";
        SorguSekmesiViewModel sekme = Kur();
        sekme.Belge.Text = $"CREATE PROCEDURE dbo.{ad} @Id INT AS SELECT @Id;";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        var sorulanlar = new List<string>();
        sekme.OnayIste = soru => { sorulanlar.Add(soru); return true; };

        try
        {
            (SorguPlani? plan, string? hata) = await sekme.PlanAlAsync(gercek: true);

            Assert.True(plan is not null, $"plan alınamadı: {hata}");
            Assert.Empty(sorulanlar);                                  // hiç sorulmadı
            Assert.False(plan!.Gercek);                                // ölçümlü değil
            Assert.True(plan.YazmaOlduguIcinCalistirilmadi);           // sebebi: yazma/DDL

            // ASIL İDDİA: ifade çalışmadıysa prosedür var olmamalı.
            Assert.False(await VarMiAsync(ad),
                $"{ad} OLUŞMUŞ — plan alınırken DDL gerçekten çalıştırılmış demektir");
        }
        finally
        {
            await Executor.ExecuteAsync(Profil(),
                $"DROP PROCEDURE IF EXISTS dbo.{ad};",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Karşı örnek — kapının fazla geniş olmadığını kanıtlar: OKUMA sorgusunda ölçümlü
    /// plan alınmaya devam eder. Aksi hâlde "her şeyi tahmine düşür" de bu testleri
    /// geçerdi ve özelliğin yarısını sessizce kaybederdik.
    /// </summary>
    [Fact]
    public async Task SELECT_sorgusunda_OLCUMLU_plan_alinmaya_devam_eder()
    {
        SorguSekmesiViewModel sekme = Kur();
        sekme.Belge.Text = "SELECT TOP 5 name FROM sys.objects;";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        (SorguPlani? plan, string? hata) = await sekme.PlanAlAsync(gercek: true);

        Assert.True(plan is not null, $"plan alınamadı: {hata}");
        Assert.True(plan!.Gercek, "okuma sorgusunda ölçümlü plan alınmalıydı");
        Assert.False(plan.YazmaOlduguIcinCalistirilmadi);
    }

    private static async Task<bool> VarMiAsync(string ad)
    {
        QueryResult r = await Executor.ExecuteAsync(Profil(),
            $"SELECT COUNT(*) FROM tempdb.sys.objects WHERE name = '{ad}' AND type = 'P';",
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);

        Assert.True(r.Basarili, r.Hata?.Mesaj);
        return Convert.ToInt32(r.ResultSetler[0].Satirlar[0][0]) > 0;
    }
}
