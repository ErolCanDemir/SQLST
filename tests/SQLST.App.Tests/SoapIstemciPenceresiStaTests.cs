using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SQLST.App.Views;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 🔌 SOAP İstemcisi penceresi UI testi (v14-S2). Ağ SAHTE (SoapIstemcisi sanal üyeleri
/// override) — doğrulanan EKRAN akışı: WSDL yükle → operasyonlar listelendi → seçimde
/// SoapAction + örnek zarf doldu → zarfı düzenleyip Gönder → istek sahte istemciye DOĞRU
/// parametrelerle gitti, yanıt GİRİNTİLİ gösterildi, süre durumda; Fault ayrıca işaretlendi.
/// </summary>
public class SoapIstemciPenceresiStaTests
{
    private sealed class SahteSoap : SoapIstemcisi
    {
        public string? IstenenUrl, CagrilanAdres, CagrilanAksiyon, GidenZarf;
        public bool FaultDondur;

        public SoapKimlik? SonKimlik;

        public override Task<(string, IReadOnlyList<string>)> WsdlIndirAsync(
            string url, SoapKimlik? kimlik, CancellationToken ct)
        {
            IstenenUrl = url;
            SonKimlik = kimlik;
            const string wsdl = """
                <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                                  xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                                  xmlns:xs="http://www.w3.org/2001/XMLSchema"
                                  xmlns:tns="http://tempuri.org/"
                                  targetNamespace="http://tempuri.org/" name="MiniServis">
                  <wsdl:types>
                    <xs:schema targetNamespace="http://tempuri.org/" elementFormDefault="qualified"
                               xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      <xs:element name="Topla">
                        <xs:complexType><xs:sequence>
                          <xs:element name="a" type="xs:int" />
                          <xs:element name="b" type="xs:int" />
                        </xs:sequence></xs:complexType>
                      </xs:element>
                    </xs:schema>
                  </wsdl:types>
                  <wsdl:message name="ToplaIn"><wsdl:part name="parameters" element="tns:Topla" /></wsdl:message>
                  <wsdl:portType name="IMini">
                    <wsdl:operation name="Topla"><wsdl:input message="tns:ToplaIn" /></wsdl:operation>
                  </wsdl:portType>
                  <wsdl:binding name="B" type="tns:IMini">
                    <soap:binding transport="http://schemas.xmlsoap.org/soap/http" />
                    <wsdl:operation name="Topla">
                      <soap:operation soapAction="http://tempuri.org/IMini/Topla" style="document" />
                    </wsdl:operation>
                  </wsdl:binding>
                  <wsdl:service name="MiniServis">
                    <wsdl:port name="B" binding="tns:B">
                      <soap:address location="http://localhost:1/Mini.svc" />
                    </wsdl:port>
                  </wsdl:service>
                </wsdl:definitions>
                """;
            return Task.FromResult<(string, IReadOnlyList<string>)>((wsdl, []));
        }

        public override Task<SoapCevap> CagirAsync(
            string adres, string soapAction, string zarf, SoapKimlik? kimlik, CancellationToken ct)
        {
            CagrilanAdres = adres;
            CagrilanAksiyon = soapAction;
            GidenZarf = zarf;
            SonKimlik = kimlik;
            return Task.FromResult(FaultDondur
                ? new SoapCevap(500,
                    """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultstring>Bölme sıfır</faultstring></s:Fault></s:Body></s:Envelope>""",
                    TimeSpan.FromMilliseconds(12), true, null)
                : new SoapCevap(200,
                    """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><ToplaResponse xmlns="http://tempuri.org/"><Sonuc>42</Sonuc></ToplaResponse></s:Body></s:Envelope>""",
                    TimeSpan.FromMilliseconds(34), false, null));
        }
    }

    /// <summary>Çok operasyonlu WSDL veren sahte — v19-S17 arama süzmesi testi için.</summary>
    private sealed class CokOperasyonluSoap : SoapIstemcisi
    {
        public override Task<(string, IReadOnlyList<string>)> WsdlIndirAsync(
            string url, SoapKimlik? kimlik, CancellationToken ct)
        {
            string Op(string ad) =>
                $"""<wsdl:operation name="{ad}"><wsdl:input message="tns:{ad}In" /></wsdl:operation>""";
            string Mesaj(string ad) =>
                $"""<wsdl:message name="{ad}In"><wsdl:part name="parameters" element="tns:{ad}" /></wsdl:message>""";
            string Eleman(string ad) =>
                $"""<xs:element name="{ad}"><xs:complexType><xs:sequence /></xs:complexType></xs:element>""";
            string BindOp(string ad) =>
                $"""<wsdl:operation name="{ad}"><soap:operation soapAction="http://tempuri.org/IMini/{ad}" style="document" /></wsdl:operation>""";

            string[] adlar = ["Topla", "Cikar", "Carp", "Bol", "MusteriGetir", "SiparisGetir"];
            string wsdl = $"""
                <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                                  xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                                  xmlns:xs="http://www.w3.org/2001/XMLSchema"
                                  xmlns:tns="http://tempuri.org/"
                                  targetNamespace="http://tempuri.org/" name="MiniServis">
                  <wsdl:types>
                    <xs:schema targetNamespace="http://tempuri.org/" elementFormDefault="qualified"
                               xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      {string.Concat(adlar.Select(Eleman))}
                    </xs:schema>
                  </wsdl:types>
                  {string.Concat(adlar.Select(Mesaj))}
                  <wsdl:portType name="IMini">{string.Concat(adlar.Select(Op))}</wsdl:portType>
                  <wsdl:binding name="B" type="tns:IMini">
                    <soap:binding transport="http://schemas.xmlsoap.org/soap/http" />
                    {string.Concat(adlar.Select(BindOp))}
                  </wsdl:binding>
                  <wsdl:service name="MiniServis">
                    <wsdl:port name="B" binding="tns:B">
                      <soap:address location="http://localhost:1/Mini.svc" />
                    </wsdl:port>
                  </wsdl:service>
                </wsdl:definitions>
                """;
            return Task.FromResult<(string, IReadOnlyList<string>)>((wsdl, []));
        }

        public override Task<SoapCevap> CagirAsync(
            string adres, string soapAction, string zarf, SoapKimlik? kimlik, CancellationToken ct)
            => Task.FromResult(new SoapCevap(200, "<ok/>", TimeSpan.Zero, false, null));
    }

    [Fact]
    public void Operasyon_aramasi_listeyi_suzer_ve_secimi_korur() // v19-S17 (kullanıcı isteği 2026-08-04)
    {
        (bool aramaGorunur, int hepsi, int cSuzulen, string[] cAdlar, int getirSuzulen,
            int temizlenince, bool secimKorundu)
            = StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            string depoDosya = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"sqlst-soap-ara-{Guid.NewGuid():N}.db");
            var w = new SoapIstemciPenceresi(new CokOperasyonluSoap(), new SqliteSoapDeposu(new YerelDepo(depoDosya)))
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                WindowState = System.Windows.WindowState.Normal, // XAML Maximized'ı test için geç
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

            ((TextBox)w.FindName("WsdlUrl")).Text = "http://localhost:1/Mini.svc?wsdl";
            ((Button)w.FindName("YukleDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            var operasyonlar = (ListBox)w.FindName("Operasyonlar");
            var aramaSatiri = (System.Windows.FrameworkElement)w.FindName("AramaSatiri");
            var arama = (TextBox)w.FindName("OperasyonArama");

            bool gorunur = aramaSatiri.Visibility == System.Windows.Visibility.Visible;
            int hepsiSayi = operasyonlar.Items.Count;

            arama.Text = "c"; // Cikar, Carp (harf duyarsız içeren)
            StaOrtak.Pump(TimeSpan.FromMilliseconds(50));
            int cSayi = operasyonlar.Items.Count;
            string[] cList = [.. operasyonlar.Items.Cast<string>()];

            arama.Text = ""; // temizle → hepsi geri
            StaOrtak.Pump(TimeSpan.FromMilliseconds(50));

            // Seçili operasyonu KAPSAYAN aramayla süz → seçim korunmalı (öğe süzgeçte kalıyor).
            operasyonlar.SelectedItem = "MusteriGetir";
            arama.Text = "getir"; // MusteriGetir, SiparisGetir — seçili olan içinde
            StaOrtak.Pump(TimeSpan.FromMilliseconds(50));
            int getirSayi = operasyonlar.Items.Count;
            bool korundu = (operasyonlar.SelectedItem as string) == "MusteriGetir";

            arama.Text = ""; // temizle → hepsi geri
            StaOrtak.Pump(TimeSpan.FromMilliseconds(50));
            int temizSayi = operasyonlar.Items.Count;

            w.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.File.Delete(depoDosya);
            return (gorunur, hepsiSayi, cSayi, cList, getirSayi, temizSayi, korundu);
        });

        Assert.True(aramaGorunur);              // ≥1 operasyon → arama kutusu görünür
        Assert.Equal(6, hepsi);
        Assert.Equal(2, cSuzulen);              // Cikar, Carp
        Assert.Contains("Cikar", cAdlar);
        Assert.Contains("Carp", cAdlar);
        Assert.Equal(2, getirSuzulen);          // MusteriGetir, SiparisGetir
        Assert.True(secimKorundu);              // seçili operasyon süzgeçte kalınca korundu
        Assert.Equal(6, temizlenince);          // temizleyince tüm operasyonlar geri
    }

    [Fact]
    public void Wsdl_yukle_operasyon_sec_gonder_ve_fault_akisi()
    {
        (string servisAdi, int operasyonSayisi, string aksiyon, string zarfIlk, string gidenZarf,
            string yanit, string durum, string faultDurum,
            int gecmisSayisi, string gecmisIlk, string geriYuklenenZarf, SahteSoap sahte)
            = StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var s = new SahteSoap();
            // Gerçek SQLite depo (geçici dosya): gönderilen istek geçmişe düşer, çift tıkla döner.
            string depoDosya = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"sqlst-soap-sta-{Guid.NewGuid():N}.db");
            var w = new SoapIstemciPenceresi(s, new SqliteSoapDeposu(new YerelDepo(depoDosya)))
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                WindowState = System.Windows.WindowState.Normal, // XAML Maximized'ı test için geç
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

            ((TextBox)w.FindName("WsdlUrl")).Text = "http://localhost:1/Mini.svc?wsdl";
            ((Button)w.FindName("YukleDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            var operasyonlar = (ListBox)w.FindName("Operasyonlar");
            string ad = ((TextBlock)w.FindName("ServisAdi")).Text;
            string aksiyonMetni = ((TextBox)w.FindName("Aksiyon")).Text;
            var zarf = (ICSharpCode.AvalonEdit.TextEditor)w.FindName("Zarf");
            string ilkZarf = zarf.Text;

            zarf.Text = zarf.Text.Replace("<a>0</a>", "<a>40</a>").Replace("<b>0</b>", "<b>2</b>");
            ((Button)w.FindName("GonderDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            string yanitMetni = ((ICSharpCode.AvalonEdit.TextEditor)w.FindName("Yanit")).Text;
            string durumMetni = ((TextBlock)w.FindName("Durum")).Text;

            s.FaultDondur = true;
            ((Button)w.FindName("GonderDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            string faultDurumMetni = ((TextBlock)w.FindName("Durum")).Text;

            // v14-S3: iki gönderim geçmişe düştü (en yeni üstte ⚠ Fault); çift tık geri yükler.
            var gecmis = (ListBox)w.FindName("Gecmis");
            int gecmisSayisi = gecmis.Items.Count;
            string gecmisIlk = gecmis.Items[0]?.ToString() ?? "";
            var zarfKutu = (ICSharpCode.AvalonEdit.TextEditor)w.FindName("Zarf");
            zarfKutu.Text = "";
            gecmis.SelectedIndex = 1; // önceki başarılı istek
            gecmis.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent,
            });
            string geriYuklenenZarf = zarfKutu.Text;

            int sayi = operasyonlar.Items.Count;
            w.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.File.Delete(depoDosya);
            return (ad, sayi, aksiyonMetni, ilkZarf, s.GidenZarf ?? "", yanitMetni, durumMetni,
                faultDurumMetni, gecmisSayisi, gecmisIlk, geriYuklenenZarf, s);
        });

        Assert.Equal("MiniServis", servisAdi);
        Assert.Equal(1, operasyonSayisi);
        Assert.Equal("http://tempuri.org/IMini/Topla", aksiyon);
        Assert.Contains("<a>0</a>", zarfIlk, StringComparison.Ordinal);       // örnek zarf doldu
        Assert.Contains("<a>40</a>", gidenZarf, StringComparison.Ordinal);    // düzenleme aynen gitti
        Assert.Equal("http://localhost:1/Mini.svc", sahte.CagrilanAdres);     // adres WSDL'den
        Assert.Contains("<Sonuc>42</Sonuc>", yanit, StringComparison.Ordinal);
        Assert.Contains("\n", yanit, StringComparison.Ordinal);               // yanıt girintilendi
        Assert.Contains("HTTP 200", durum, StringComparison.Ordinal);
        Assert.Contains("34 ms", durum, StringComparison.Ordinal);
        Assert.Contains("FAULT", faultDurum, StringComparison.Ordinal);       // fault açıkça işaretli
        // Geçmiş (v14-S3): iki kayıt, en yeni (Fault) üstte ⚠ ile; çift tık zarfı geri getirdi.
        Assert.Equal(2, gecmisSayisi);
        Assert.Contains("⚠", gecmisIlk, StringComparison.Ordinal);
        Assert.Contains("Topla", gecmisIlk, StringComparison.Ordinal);
        Assert.Contains("<a>40</a>", geriYuklenenZarf, StringComparison.Ordinal);
    }
}
