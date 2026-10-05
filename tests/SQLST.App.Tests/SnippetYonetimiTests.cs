using System.IO;
using SQLST.App.ViewModels;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// Kod parçası YÖNETİM ekranı (kullanıcı isteği 2026-07-19: <i>"kod parçaları da giriş
/// yapılan motora göre listelensin"</i>).
///
/// Editördeki öneri listesi zaten motora göre süzülüyordu; yönetim ekranı ise HEPSİNİ
/// gösteriyordu. İkisi artık AYNI kümeyi gösteriyor — yönetimde gördüğün kalıp editörde
/// de çıkar, çıkmayan da orada görünmez.
/// </summary>
public class SnippetYonetimiTests : IDisposable
{
    private readonly string _klasor = Path.Combine(
        Path.GetTempPath(), "sqlst-snip-vm-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly SqliteSnippetDeposu _depo;

    public SnippetYonetimiTests()
    {
        Directory.CreateDirectory(_klasor);
        _depo = new SqliteSnippetDeposu(new YerelDepo(Path.Combine(_klasor, "test.db")));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_klasor, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Liste_YALNIZ_aktif_motorun_kaliplarini_gosterir()
    {
        var vm = new SnippetlerViewModel(_depo, MotorTuru.Mongo);

        await vm.YukleAsync();

        Assert.NotEmpty(vm.Snippetler);
        Assert.All(vm.Snippetler, s => Assert.True(
            s.Motor is null or MotorTuru.Mongo,
            $"'{s.Kisayol}' Mongo listesinde ama motoru {s.Motor}"));

        // T-SQL kalıbı (TOP) Mongo yönetim listesinde ÇIKMAMALI
        Assert.DoesNotContain(vm.Snippetler, s => s.Govde.Contains("TOP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Farkli_motorda_farkli_liste_gelir()
    {
        var mongo = new SnippetlerViewModel(_depo, MotorTuru.Mongo);
        var mssql = new SnippetlerViewModel(_depo, MotorTuru.Mssql);

        await mongo.YukleAsync();
        await mssql.YukleAsync();

        Assert.Contains(mssql.Snippetler, s => s.Govde.Contains("TOP", StringComparison.Ordinal));
        Assert.Contains(mongo.Snippetler, s => s.Govde.Contains("db.", StringComparison.Ordinal));
        Assert.DoesNotContain(mssql.Snippetler, s => s.Govde.Contains("db.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Baglanti_yoksa_TUMU_listelenir()
    {
        // Yönetim yine yapılabilmeli — bağlı olmadan da kalıplara erişilsin.
        var vm = new SnippetlerViewModel(_depo, aktifMotor: null);

        await vm.YukleAsync();

        Assert.Contains(vm.Snippetler, s => s.Motor == MotorTuru.Mssql);
        Assert.Contains(vm.Snippetler, s => s.Motor == MotorTuru.Mongo);
        Assert.Contains("TÜM", vm.Kapsam, StringComparison.Ordinal);
    }

    [Fact]
    public void Kapsam_metni_neyin_gizlendigini_SOYLER()
    {
        // Kullanıcı "kaydettiğim kalıp nerede?" diye aramasın: gizlenmiş, silinmemiş.
        var vm = new SnippetlerViewModel(_depo, MotorTuru.Postgres);

        Assert.Contains("Postgres", vm.Kapsam, StringComparison.Ordinal);
        Assert.Contains("gizli", vm.Kapsam, StringComparison.Ordinal);
    }

    [Fact]
    public void Yeni_kalip_AKTIF_motora_varsayilanlanir()
    {
        var vm = new SnippetlerViewModel(_depo, MotorTuru.Oracle);

        vm.YeniCommand.Execute(null);

        Assert.Equal(MotorTuru.Oracle, vm.Motor);
    }

    // ── Motor seçimi bağlı motorla SINIRLI (kullanıcı isteği 2026-07-19) ──

    [Fact]
    public void Motor_secenekleri_YALNIZ_aktif_motor_ve_her_motorda()
    {
        // "SQL ile bağlandıysam ya her motor ya da SQL'i seçebileyim; Mongo ve diğer
        // motorlara kod parçası KAYDEDEMEYEYİM."
        var vm = new SnippetlerViewModel(_depo, MotorTuru.Mssql);

        Assert.Equal(2, vm.Motorlar.Count);
        Assert.Contains(vm.Motorlar, m => m.Deger is null);            // "(her motorda)"
        Assert.Contains(vm.Motorlar, m => m.Deger == MotorTuru.Mssql);
        Assert.DoesNotContain(vm.Motorlar, m => m.Deger == MotorTuru.Mongo);
        Assert.DoesNotContain(vm.Motorlar, m => m.Deger == MotorTuru.Postgres);
    }

    [Fact]
    public void Motor_secenekleri_baglantiya_gore_DEGISIR()
    {
        Assert.Contains(new SnippetlerViewModel(_depo, MotorTuru.Mongo).Motorlar,
            m => m.Deger == MotorTuru.Mongo);
        Assert.DoesNotContain(new SnippetlerViewModel(_depo, MotorTuru.Oracle).Motorlar,
            m => m.Deger == MotorTuru.Mongo);
    }

    [Fact]
    public void Baglanti_yoksa_TUM_motorlar_secilebilir()
    {
        // Yönetim bağlantısız da yapılabilmeli; orada kısıtlamanın dayanağı yok.
        var vm = new SnippetlerViewModel(_depo, aktifMotor: null);

        Assert.Contains(vm.Motorlar, m => m.Deger == MotorTuru.Mssql);
        Assert.Contains(vm.Motorlar, m => m.Deger == MotorTuru.Mongo);
    }

    // ── "(her motorda)" SEÇİLEBİLMELİ (kullanıcı bulgusu 2026-07-19) ──

    [Fact]
    public void Her_motorda_secenegi_SECILEBILIR()
    {
        // ComboBox'ın bağlandığı yüzey null olamaz; null öğe WPF'te seçili tutulamıyordu
        // ve kullanıcı "(her motorda)"yı seçemiyordu. Sarmalayıcı bunu çözer.
        var vm = new SnippetlerViewModel(_depo, MotorTuru.Mssql);

        SnippetlerViewModel.MotorSecimi herMotor =
            vm.Motorlar.Single(m => m.Deger is null);

        vm.SeciliMotorSecimi = herMotor;

        Assert.Null(vm.Motor);                            // kayda null (= her motorda) gider
        Assert.Same(herMotor, vm.SeciliMotorSecimi);      // seçim EKRANDA da kalır
    }

    [Fact]
    public void Secim_yuzeyi_ASLA_null_olmaz()
    {
        // Aksi hâlde ComboBox boş görünür ve kullanıcı ne seçili olduğunu bilemez.
        var vm = new SnippetlerViewModel(_depo, MotorTuru.Mssql);

        vm.Motor = null;
        Assert.NotNull(vm.SeciliMotorSecimi);

        vm.Motor = MotorTuru.Mssql;
        Assert.Equal(MotorTuru.Mssql, vm.SeciliMotorSecimi.Deger);
    }

    [Fact]
    public void Her_motorda_secenegi_ADIYLA_gorunur()
        => Assert.Equal("(her motorda)",
            new SnippetlerViewModel(_depo, MotorTuru.Mssql).Motorlar.Single(m => m.Deger is null).Ad);

    [Fact]
    public async Task Her_motorda_secenegi_HALA_kaydedilebilir()
    {
        // Kısıtlama "(her motorda)"yı kapatmamalı — kullanıcı kararıyla duruyor.
        var vm = new SnippetlerViewModel(_depo, MotorTuru.Mssql);
        await vm.YukleAsync();

        vm.YeniCommand.Execute(null);
        vm.Kisayol = "hepsi";
        vm.Baslik = "Motorsuz not";
        vm.Govde = "-- not $0";
        vm.Motor = null;

        await vm.KaydetCommand.ExecuteAsync(null);

        Assert.Contains(vm.Snippetler, s => s.Kisayol == "hepsi");
        Assert.Contains(await _depo.ListeleAsync(MotorTuru.Mongo), s => s.Kisayol == "hepsi");
    }

    [Fact]
    public async Task Ayni_motora_kaydedilen_kalip_listede_GORUNUR()
    {
        var vm = new SnippetlerViewModel(_depo, MotorTuru.Mssql);
        await vm.YukleAsync();

        vm.YeniCommand.Execute(null);
        vm.Kisayol = "kendi";
        vm.Baslik = "Kendi kalıbım";
        vm.Govde = "SELECT $0";

        await vm.KaydetCommand.ExecuteAsync(null);

        Assert.Contains(vm.Snippetler, s => s.Kisayol == "kendi");
        Assert.DoesNotContain("görünmeyecek", vm.Bilgi, StringComparison.Ordinal);
    }
}
