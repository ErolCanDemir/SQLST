using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SQLST.App.Views;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// ⏪ Geri Al Paketleri penceresi UI testi (V15-S4). Depo bellekte; doğrulanan EKRAN akışı:
/// pencere açılınca paket listelenir → seçince doğuran SQL + eski hal grid'i dolar →
/// "Geri alma script'i üret" ters DML'i SEKME AÇMA köprüsüne DOĞRU içerikle gönderir.
/// </summary>
public class GeriAlPenceresiStaTests
{
    private sealed class BellekDepo : IGeriAlDeposu
    {
        private readonly List<GeriAlPaketi> _paketler;
        public BellekDepo(params GeriAlPaketi[] paketler) => _paketler = [.. paketler];

        public Task<long> EkleAsync(GeriAlPaketi paket, CancellationToken ct = default)
        { _paketler.Add(paket); return Task.FromResult(paket.Id); }

        public Task<IReadOnlyList<GeriAlPaketi>> ListeleAsync(int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GeriAlPaketi>>(
                [.. _paketler.OrderByDescending(p => p.Id).Select(p => p with { SatirlarJson = "" })]);

        public Task<GeriAlPaketi?> GetirAsync(long id, CancellationToken ct = default)
            => Task.FromResult(_paketler.FirstOrDefault(p => p.Id == id));

        public Task SilAsync(long id, CancellationToken ct = default)
        { _paketler.RemoveAll(p => p.Id == id); return Task.CompletedTask; }
    }

    [Fact]
    public void Paket_listelenir_secilir_ve_ters_dml_sekmeye_gonderilir()
    {
        (int liste, string doguran, int gridSatir, string acilanSql, string? acilanDb)
            = StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var paket = new GeriAlPaketi
            {
                Id = 1, TarihUtc = new DateTime(2026, 7, 27, 8, 0, 0, DateTimeKind.Utc),
                Sunucu = "s", Veritabani = "DemoDb", Tablo = "dbo.Musteri", Fiil = "UPDATE",
                SqlMetni = "UPDATE dbo.Musteri SET Durum = N'Pasif' WHERE Sehir = N'İzmir'",
                SatirSayisi = 2,
                KolonlarJson = """[{"Ad":"Id","Tip":"int"},{"Ad":"Durum","Tip":"nvarchar"}]""",
                PkJson = """["Id"]""",
                SatirlarJson = """[["1","Aktif"],["2","Aktif"]]""",
            };

            string? acilanSql = null, acilanDb = null;
            var w = new GeriAlPenceresi(new BellekDepo(paket), (_, sql, db) => { acilanSql = sql; acilanDb = db; })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

            var liste = (ListBox)w.FindName("PaketListesi");
            int listeSayi = liste.Items.Count;

            liste.SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            string doguran = ((TextBox)w.FindName("DoguranSql")).Text;
            var grid = (DataGrid)w.FindName("EskiHalGrid");
            int gridSatir = ((DataView)grid.ItemsSource).Count;

            ((Button)w.FindName("BtnGeriAlUret") ?? Dugme(w, "⏪ Geri alma script'i üret"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            return (listeSayi, doguran, gridSatir, acilanSql ?? "", acilanDb);
        });

        Assert.Equal(1, liste);
        Assert.Contains("WHERE Sehir = N'İzmir'", doguran);
        Assert.Equal(2, gridSatir);                                   // iki eski satır önizlemede
        Assert.Contains("UPDATE dbo.Musteri SET [Durum] = N'Aktif' WHERE [Id] = 1;", acilanSql); // ters DML
        Assert.Contains("[Id] = 2", acilanSql);
        Assert.Equal("DemoDb", acilanDb);                             // sekme paketin DB'sinde açılır
    }

    private static Button Dugme(DependencyObject kok, string icerik)
    {
        if (kok is Button b && (b.Content as string) == icerik)
            return b;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(kok);
        for (int i = 0; i < n; i++)
        {
            if (Dugme(System.Windows.Media.VisualTreeHelper.GetChild(kok, i), icerik) is { } bulunan)
                return bulunan;
        }
        return null!;
    }
}
