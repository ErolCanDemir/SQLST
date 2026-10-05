using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SQLST.App.Views;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 🌐 REST Postman düzeni (v23 — kullanıcı onaylı mockup): URL ↔ Params ÇİFT YÖNLÜ senkronun
/// pencere üstündeki sözleşmesi + gönderimde query'nin İKİ KEZ eklenmemesi + durum rozetleri
/// ve Çerezler sekmesi. Ağ yok — sahte istemci yanıtı üretir, gönderilen isteği yakalar.
/// </summary>
public class RestPostmanDuzeniStaTests
{
    private sealed class SahteIstemci : RestIstemcisi
    {
        public RestIstek? Son;

        public override Task<RestCevap> GonderAsync(RestIstek istek, CancellationToken ct)
        {
            Son = istek;
            return Task.FromResult(new RestCevap(200, "OK",
                [
                    new RestSatir("Set-Cookie", "oturum=abc123; Path=/; HttpOnly"),
                    new RestSatir("Content-Type", "application/json"),
                ],
                """{"ad":1}""", TimeSpan.FromMilliseconds(42), 9, "application/json", null));
        }
    }

    [Fact]
    public void Url_params_cift_yonlu_senkron_ve_gonderimde_ciftleme_yok()
    {
        StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");
            var istemci = new SahteIstemci();
            var w = new RestIstemciPenceresi(istemci)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 1280, Height = 800,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

            var url = (TextBox)w.FindName("UrlKutusu");
            var grid = (DataGrid)w.FindName("ParamGrid");
            var satirlar = (System.Collections.ObjectModel.ObservableCollection<RestSatirGorunum>)grid.ItemsSource;

            // 1) URL → grid: query yazınca tablo kendiliğinden dolar (kullanıcının asıl isteği).
            url.Text = "https://api.site.com/v2/talepler?durum=acik&sayfa=2";
            StaOrtak.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(2, satirlar.Count);
            Assert.Equal(("durum", "acik"), (satirlar[0].Anahtar, satirlar[0].Deger));
            Assert.Equal(("sayfa", "2"), (satirlar[1].Anahtar, satirlar[1].Deger));

            // Açıklama URL yeniden yazılınca KORUNUR (anahtar eşleşmesi).
            satirlar[0].Aciklama = "talep durumu süzgeci";
            url.Text = "https://api.site.com/v2/talepler?durum=kapali&sayfa=3";
            StaOrtak.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal("kapali", satirlar[0].Deger);
            Assert.Equal("talep durumu süzgeci", satirlar[0].Aciklama);

            // 2) Grid → URL: satır eklenince URL güncellenir.
            satirlar.Add(new RestSatirGorunum { Anahtar = "limit", Deger = "50" });
            StaOrtak.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal("https://api.site.com/v2/talepler?durum=kapali&sayfa=3&limit=50", url.Text);

            // 3) ✓ kaldırılan satır URL'den düşer ama grid'de kalır (Postman davranışı).
            satirlar[1].Etkin = false;
            typeof(RestIstemciPenceresi)
                .GetMethod("ParamlardanUrle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(w, null); // onay kutusu commit'inin karşılığı
            Assert.Equal("https://api.site.com/v2/talepler?durum=kapali&limit=50", url.Text);
            Assert.Equal(3, satirlar.Count); // sayfa satırı listede duruyor

            // 4) Gönderim: URL TABAN gider, query yalnız grid'den — çiftleme yok.
            ((Button)w.FindName("GonderDugmesi")).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            StaOrtak.PumpUntil(() => istemci.Son is not null, TimeSpan.FromSeconds(10));
            Assert.NotNull(istemci.Son);
            Assert.Equal("https://api.site.com/v2/talepler", istemci.Son!.Url);
            Assert.Equal(["durum", "sayfa", "limit"],
                istemci.Son.QueryParametreleri.Select(p => p.Anahtar).ToArray());
            Assert.False(istemci.Son.QueryParametreleri[1].Etkin); // ✓ kaldırılmış satır pasif gider

            // 5) Rozetler + Çerezler (Postman): 200 OK yeşil rozet, Set-Cookie satırı çözülmüş.
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
            var durumRozet = (TextBlock)w.FindName("DurumRozetMetin");
            Assert.Equal("200 OK", durumRozet.Text);
            var cerezGrid = (DataGrid)w.FindName("CerezGrid");
            var cerezler = (IReadOnlyList<RestSatir>)cerezGrid.ItemsSource;
            RestSatir cerez = Assert.Single(cerezler);
            Assert.Equal("oturum", cerez.Anahtar);
            Assert.Contains("abc123", cerez.Deger);
            Assert.Contains("HttpOnly", cerez.Deger);

            w.Close();
        });
    }
}
