using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SQLST.App.Views;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// SP parametre penceresi UI testi (v16). Doğrulanan akış: parametreler listelenir → değer girilir
/// → Çalıştır'a basılınca tip-duyarlı EXEC üretilir (Sonuc) ve CalistirIstendi=true olur.
/// </summary>
public class SpParametrePenceresiStaTests
{
    [Fact]
    public void Degerler_girilir_calistir_tip_duyarli_exec_uretir()
    {
        (string? sonuc, bool calistir, int satirSayisi) = StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var sp = new SemaNesnesi("DemoDb", "dbo", "MusteriGetir", SemaNesneTuru.StoredProcedure, [],
            [
                new SemaParametresi("@id", "int", false),
                new SemaParametresi("@ad", "nvarchar(50)", false),
                new SemaParametresi("@sonuc", "int", CikisMi: true),
            ]);

            var w = new SpParametrePenceresi(sp)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            var grid = (DataGrid)w.FindName("Grid")!;
            var satirlar = grid.ItemsSource.Cast<SpParametrePenceresi.Satir>().ToList();
            satirlar.First(s => s.Ad == "@id").Deger = "42";
            satirlar.First(s => s.Ad == "@ad").Deger = "Ali'nin";

            ((Button)w.FindName("BtnSpCalistir")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(100));

            string? sonucMetni = w.Sonuc;
            bool cal = w.CalistirIstendi;
            w.Close();
            return (sonucMetni, cal, satirlar.Count);
        });

        Assert.Equal(3, satirSayisi);              // giriş 2 + çıkış 1
        Assert.True(calistir);
        Assert.NotNull(sonuc);
        // Hizalama için parametre adları padlenir → boşluğa duyarsız kontrol (tek boşluğa indir).
        string tek = System.Text.RegularExpressions.Regex.Replace(sonuc, " +", " ");
        Assert.Contains("@id = 42", tek);                 // sayısal tırnaksız
        Assert.Contains("@ad = N'Ali''nin'", tek);        // metin kaçışlı
        Assert.Contains("@sonuc = @out_sonuc OUTPUT", tek); // çıkış değişkenle
        Assert.Contains("SELECT @out_sonuc", tek);          // sonda geri okuma
    }
}
