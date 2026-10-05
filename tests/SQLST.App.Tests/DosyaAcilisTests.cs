using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLST.App.ViewModels;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 📂 Çift tık / "Birlikte aç" köprüsü (v23-S7 — kullanıcı bulgusu 28 Eyl 2026: "var olan bir
/// sql dosyasını bizde açamıyoruz"): komut satırı argümanı süzgeci + bağlantı kurulunca
/// bekleyen dosyaların sekmede BİR kez açılması. Kurulumun [Registry] "Birlikte aç" kaydı
/// bu hattı çağırır; buradaki testler uygulama tarafını kilitler.
/// </summary>
public class DosyaAcilisTests
{
    [Fact]
    public void Acilacak_dosyalar_yalniz_var_olan_sql_txt() // arg süzgeci — işleyicinin kapısı
    {
        string klasor = Path.Combine(Path.GetTempPath(), $"sqlst-arg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(klasor);
        try
        {
            string sql = Path.Combine(klasor, "rapor.sql");
            string txt = Path.Combine(klasor, "not.txt");
            string docx = Path.Combine(klasor, "belge.docx");
            File.WriteAllText(sql, "SELECT 1");
            File.WriteAllText(txt, "x");
            File.WriteAllText(docx, "y");

            string[] sonuc = App.AcilacakDosyalar(
                [sql, txt, docx, Path.Combine(klasor, "yok.sql"), "\"bozuk<yol>.sql\""]);

            Assert.Equal([sql, txt], sonuc); // .docx tür dışı · yok.sql diskte yok · bozuk yol elenir
        }
        finally
        {
            Directory.Delete(klasor, true);
        }
    }

    [Fact]
    public void Bekleyen_dosya_baglanti_kurulunca_sekmede_BIR_kez_acilir()
    {
        string klasor = Path.Combine(Path.GetTempPath(), $"sqlst-ac-{Guid.NewGuid():N}");
        Directory.CreateDirectory(klasor);
        Dispatcher d = StaOrtak.Sta();
        try
        {
            string dosya = Path.Combine(klasor, "aylik-rapor.sql");
            File.WriteAllText(dosya, "SELECT N'çağrı' AS Tanım", new UTF8Encoding(true)); // BOM'lu + Türkçe

            d.Invoke(() =>
            {
                StaOrtak.Birlestir("PaletAcik.xaml");
                StaOrtak.Birlestir("Tema.xaml");
                var services = new ServiceCollection();
                SQLST.App.App.HizmetleriKur(services);
                services.AddSingleton(new YerelDepo(Path.Combine(klasor, "sqlst.db")));
                ServiceProvider prov = services.BuildServiceProvider();
                MainWindow w = prov.GetRequiredService<MainWindow>();
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000;
                w.Show();
                StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
                var vm = (MainViewModel)w.DataContext;
                var profil = new ConnectionProfile
                {
                    Ad = "Diag", Sunucu = @"(localdb)\MSSQLLocalDB",
                    Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
                };

                SQLST.App.App.BekleyenDosyalar = [dosya];
                w.ProfilUygula(profil);
                StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

                SorguSekmesiViewModel sekme = Assert.Single(
                    vm.Sekmeler.OfType<SorguSekmesiViewModel>().Where(s => s.Baslik == "aylik-rapor.sql"));
                Assert.Contains("çağrı", sekme.Belge.Text);            // kodlama zinciri: mojibake yok
                Assert.Null(SQLST.App.App.BekleyenDosyalar);           // liste bir kez tüketildi

                w.ProfilUygula(profil); // bağlantı değişimi/yenilenmesi — dosya İKİNCİ kez açılmaz
                StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
                Assert.Single(vm.Sekmeler.OfType<SorguSekmesiViewModel>()
                    .Where(s => s.Baslik == "aylik-rapor.sql"));

                w.Close();
            });
        }
        finally
        {
            try { Directory.Delete(klasor, true); } catch { }
        }
    }
}
