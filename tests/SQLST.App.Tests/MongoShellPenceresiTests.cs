using System.Windows;
using System.Windows.Threading;
using SQLST.App.Views;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S3 saha turu-3 m.4 — kullanıcı: "Mongoda shell yapıştır butonuna basınca ekran açılmıyor
/// muydu? Eğer açılıyor ise şu anda açılmıyor, bir sorun var."
///
/// KÖK NEDEN: ekran hiç YOKTU. Düğme panoyu sessizce okuyup çevirir, başarılıysa yeni sekme açar,
/// çeviremezse yalnız ana penceredeki DURUM ÇUBUĞUNA yazardı. Pano beklenen biçimde değilse
/// (en sık hâl — ör. Compass'ın <c>db.getCollection(...)</c> çıktısı, ki o da reddediliyordu)
/// ekranda hiçbir şey olmuyor, özellik bozuk sanılıyordu.
///
/// Bu sınıf ekranın sözleşmesini sabitler: her durumda AÇILIR, sebebi İÇERDE yazar.
/// </summary>
public class MongoShellPenceresiTests
{
    private static readonly bool Etkin = OperatingSystem.IsWindows();

    private static void Pencerede(string baslangic, Action<MongoShellPenceresi, List<string>> denetle)
    {
        var acilanlar = new List<string>();
        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoShellPenceresi(baslangic, acilanlar.Add)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 820, Height = 600,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
            denetle(w, acilanlar);
            w.Close();
        });
    }

    [Fact]
    public void Pano_bosken_bile_ekran_acilir_ve_ne_yapilacagini_soyler()
    {
        if (!Etkin)
            return;

        Pencerede("", (w, _) =>
        {
            Assert.True(w.IsVisible);                        // ESKİDEN: hiçbir şey açılmazdı
            Assert.Contains("yapıştırın", w.Durum.Text);
            Assert.False(w.SekmedeAcDugmesi.IsEnabled);      // çevrilecek bir şey yok
        });
    }

    [Fact]
    public void Cevrilemeyen_metinde_hata_ekranda_girdinin_yaninda_yazar()
    {
        if (!Etkin)
            return;

        // ESKİDEN bu mesaj yalnız ana penceredeki durum çubuğuna düşerdi — kolayca kaçırılıyordu.
        Pencerede("SELECT * FROM oturumlar", (w, _) =>
        {
            Assert.Contains("db.' ile başlamıyor", w.Durum.Text);
            Assert.False(w.SekmedeAcDugmesi.IsEnabled);
            Assert.Equal("", w.Cikti.Text);
        });
    }

    [Fact]
    public void Gecerli_komut_cevrilir_ve_sekmede_ac_json_gonderir()
    {
        if (!Etkin)
            return;

        Pencerede("db.getCollection('oturumlar').find({ kullaniciId: 5 }).limit(10)", (w, acilanlar) =>
        {
            Assert.Contains("Çevrildi", w.Durum.Text);
            Assert.Contains("\"find\": \"oturumlar\"", w.Cikti.Text);
            Assert.True(w.SekmedeAcDugmesi.IsEnabled);

            w.SekmedeAcDugmesi.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Single(acilanlar);
            Assert.Contains("\"limit\": 10", acilanlar[0]);
        });
    }

    [Fact]
    public void Metin_duzenlenince_ceviri_yenilenir()
    {
        if (!Etkin)
            return;

        Pencerede("bozuk", (w, _) =>
        {
            Assert.False(w.SekmedeAcDugmesi.IsEnabled);

            w.Girdi.Text = "db.log.find({})"; // kullanıcı düzeltiyor
            StaOrtak.Pump(TimeSpan.FromMilliseconds(150));

            Assert.True(w.SekmedeAcDugmesi.IsEnabled);
            Assert.Contains("\"find\": \"log\"", w.Cikti.Text);
        });
    }
}
