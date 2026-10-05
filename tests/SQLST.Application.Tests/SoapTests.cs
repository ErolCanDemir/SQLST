using System.Net;
using System.Text;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v14-S1 — SOAP/WCF çekirdeği: WSDL çözümleme + örnek zarf üretimi (saf) ve indirme/çağrı
/// istemcisinin HttpListener'lı KAPALI DEVRE entegrasyonu (gerçek WCF sunucusu gerekmez; WCF'nin
/// çok parçalı WSDL yayını ve SOAP POST sözleşmesi burada birebir taklit edilir).
/// </summary>
public class SoapTests
{
    private const string Tns = "http://tempuri.org/";
    private const string DcNs = "http://schemas.datacontract.org/2004/07/LstQms";

    /// <summary>WCF tarzı doc/literal-wrapped WSDL; dcSemaAyri=true → Musteri şeması ana
    /// belgeden ÇIKARILIR (çok parçalı yayın taklidi; xsd:import schemaLocation'lı).</summary>
    private static (string Wsdl, string DcSema) FixtureUret(bool dcSemaAyri, string adres)
    {
        string dcSema = $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                       targetNamespace="{DcNs}" elementFormDefault="qualified">
              <xs:complexType name="Musteri">
                <xs:sequence>
                  <xs:element name="Id" type="xs:int" />
                  <xs:element name="Ad" type="xs:string" />
                  <xs:element name="Bakiye" type="xs:decimal" minOccurs="0" />
                  <xs:element name="KayitTarihi" type="xs:dateTime" />
                  <xs:element name="Etiketler" type="xs:string" maxOccurs="unbounded" minOccurs="0" />
                </xs:sequence>
              </xs:complexType>
            </xs:schema>
            """;

        string icSema = dcSemaAyri
            ? $"""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="{DcNs}" schemaLocation="{adres}?xsd=xsd0" /></xs:schema>"""
            : dcSema;

        string wsdl = $"""
            <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                              xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                              xmlns:xs="http://www.w3.org/2001/XMLSchema"
                              xmlns:tns="{Tns}" xmlns:q1="{DcNs}"
                              targetNamespace="{Tns}" name="QmsServis">
              <wsdl:types>
                <xs:schema targetNamespace="{Tns}" elementFormDefault="qualified"
                           xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:q1="{DcNs}">
                  <xs:import namespace="{DcNs}" />
                  <xs:element name="MusteriGetir">
                    <xs:complexType><xs:sequence>
                      <xs:element name="musteriNo" type="xs:int" />
                      <xs:element name="sehir" type="xs:string" minOccurs="0" />
                    </xs:sequence></xs:complexType>
                  </xs:element>
                  <xs:element name="MusteriKaydet">
                    <xs:complexType><xs:sequence>
                      <xs:element name="musteri" type="q1:Musteri" />
                    </xs:sequence></xs:complexType>
                  </xs:element>
                  <xs:element name="Listele">
                    <xs:complexType><xs:sequence /></xs:complexType>
                  </xs:element>
                </xs:schema>
                {icSema}
              </wsdl:types>
              <wsdl:message name="MusteriGetirIn"><wsdl:part name="parameters" element="tns:MusteriGetir" /></wsdl:message>
              <wsdl:message name="MusteriKaydetIn"><wsdl:part name="parameters" element="tns:MusteriKaydet" /></wsdl:message>
              <wsdl:message name="ListeleIn"><wsdl:part name="parameters" element="tns:Listele" /></wsdl:message>
              <wsdl:portType name="IQmsServis">
                <wsdl:operation name="MusteriGetir"><wsdl:input message="tns:MusteriGetirIn" /></wsdl:operation>
                <wsdl:operation name="MusteriKaydet"><wsdl:input message="tns:MusteriKaydetIn" /></wsdl:operation>
                <wsdl:operation name="Listele"><wsdl:input message="tns:ListeleIn" /></wsdl:operation>
              </wsdl:portType>
              <wsdl:binding name="BasicHttpBinding_IQmsServis" type="tns:IQmsServis">
                <soap:binding transport="http://schemas.xmlsoap.org/soap/http" />
                <wsdl:operation name="MusteriGetir">
                  <soap:operation soapAction="{Tns}IQmsServis/MusteriGetir" style="document" />
                </wsdl:operation>
                <wsdl:operation name="MusteriKaydet">
                  <soap:operation soapAction="{Tns}IQmsServis/MusteriKaydet" style="document" />
                </wsdl:operation>
                <wsdl:operation name="Listele">
                  <soap:operation soapAction="{Tns}IQmsServis/Listele" style="document" />
                </wsdl:operation>
              </wsdl:binding>
              <wsdl:service name="QmsServis">
                <wsdl:port name="BasicHttpBinding_IQmsServis" binding="tns:BasicHttpBinding_IQmsServis">
                  <soap:address location="{adres}" />
                </wsdl:port>
              </wsdl:service>
            </wsdl:definitions>
            """;
        return (wsdl, dcSema);
    }

    [Fact]
    public void Cozumleyici_operasyonlari_parametreleri_ve_ornek_zarfi_cikarir()
    {
        (string wsdl, _) = FixtureUret(dcSemaAyri: false, "http://localhost:9099/Servis.svc");

        SoapServis servis = WsdlCozumleyici.Cozumle(wsdl);

        Assert.Equal("QmsServis", servis.Ad);
        Assert.Equal("http://localhost:9099/Servis.svc", servis.Adres);
        Assert.Empty(servis.Uyarilar);
        Assert.Equal(["Listele", "MusteriGetir", "MusteriKaydet"],
            servis.Operasyonlar.Select(o => o.Ad));

        SoapOperasyon getir = servis.Operasyonlar.Single(o => o.Ad == "MusteriGetir");
        Assert.Equal($"{Tns}IQmsServis/MusteriGetir", getir.SoapAction);
        Assert.Equal(["musteriNo", "sehir"], getir.Parametreler.Select(p => p.Ad));
        Assert.True(getir.Parametreler[1].SecimlikMi);
        Assert.Contains($"<MusteriGetir xmlns=\"{Tns}\">", getir.OrnekZarf, StringComparison.Ordinal);
        Assert.Contains("<musteriNo>0</musteriNo>", getir.OrnekZarf, StringComparison.Ordinal);
        Assert.Contains("<sehir>?</sehir>", getir.OrnekZarf, StringComparison.Ordinal);

        // Karmaşık tip: kendi DataContract namespace'iyle nitelenir; tarih/ondalık örnekli;
        // dizi alanında "tekrarlayın" yorumu bırakılır.
        SoapOperasyon kaydet = servis.Operasyonlar.Single(o => o.Ad == "MusteriKaydet");
        Assert.Contains($"<musteri xmlns=\"{DcNs}\">", kaydet.OrnekZarf, StringComparison.Ordinal);
        Assert.Contains("<KayitTarihi>2026-01-01T00:00:00</KayitTarihi>", kaydet.OrnekZarf, StringComparison.Ordinal);
        Assert.Contains("<Bakiye>0.0</Bakiye>", kaydet.OrnekZarf, StringComparison.Ordinal);
        Assert.Contains("tekrarlayın", kaydet.OrnekZarf, StringComparison.Ordinal);
    }

    [Fact]
    public void Cok_parcali_wsdl_ek_belgeyle_ayni_sonucu_verir()
    {
        (string wsdl, string dcSema) = FixtureUret(dcSemaAyri: true, "http://localhost:9099/Servis.svc");

        SoapServis servis = WsdlCozumleyici.Cozumle(wsdl, [dcSema]);

        SoapOperasyon kaydet = servis.Operasyonlar.Single(o => o.Ad == "MusteriKaydet");
        Assert.Contains($"<musteri xmlns=\"{DcNs}\">", kaydet.OrnekZarf, StringComparison.Ordinal);
        Assert.Contains("<Id>0</Id>", kaydet.OrnekZarf, StringComparison.Ordinal);
        Assert.Empty(servis.Uyarilar);
    }

    [Fact]
    public void Sp_servis_iskeleti_imza_parametre_ve_output_uretir()
    {
        // v14-S4: SP imzası → WCF sarmalayıcı (kullanıcının "WCF servisim SP kullanıyor" deseni).
        var sp = new SemaNesnesi("LstQmsDb", "dbo", "MusteriGetir", SemaNesneTuru.StoredProcedure,
            [],
            [
                new SemaParametresi("@MusteriId", "int", false),
                new SemaParametresi("@Sehir", "nvarchar(50)", false),
                new SemaParametresi("@ToplamBakiye", "decimal(18,2)", true),
            ]);

        string kod = ServisIskeletiUretici.Uret(sp);

        Assert.Contains("public interface IMusteriGetirServisi", kod, StringComparison.Ordinal);
        Assert.Contains("DataTable MusteriGetir(int musteriId, string sehir, out decimal toplamBakiye)",
            kod, StringComparison.Ordinal);
        Assert.Contains("new SqlCommand(\"[dbo].[MusteriGetir]\"", kod, StringComparison.Ordinal);
        Assert.Contains("komut.Parameters.AddWithValue(\"@MusteriId\"", kod, StringComparison.Ordinal);
        Assert.Contains("SqlDbType.Decimal", kod, StringComparison.Ordinal);          // OUTPUT tanımı
        Assert.Contains("Direction = ParameterDirection.Output", kod, StringComparison.Ordinal);
        Assert.Contains("toplamBakiye = ", kod, StringComparison.Ordinal);            // çağrı sonrası atama
        Assert.Contains("[ServiceContract", kod, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kapali_devre_indir_cozumle_cagir_ve_fault()
    {
        int port = Random.Shared.Next(20000, 45000);
        string kokAdres = $"http://localhost:{port}/Servis.svc";
        (string wsdl, string dcSema) = FixtureUret(dcSemaAyri: true, kokAdres);

        using var sunucu = new HttpListener();
        sunucu.Prefixes.Add($"http://localhost:{port}/");
        sunucu.Start();
        string? gelenAction = null, gelenGovde = null;
        var sunucuGorevi = Task.Run(async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                HttpListenerContext ctx = await sunucu.GetContextAsync();
                string yol = ctx.Request.Url!.PathAndQuery;
                string yanit;
                if (yol.EndsWith("?wsdl", StringComparison.OrdinalIgnoreCase)
                    || yol.EndsWith("?singleWsdl", StringComparison.OrdinalIgnoreCase))
                {
                    yanit = wsdl;
                }
                else if (yol.Contains("?xsd=", StringComparison.OrdinalIgnoreCase))
                {
                    yanit = dcSema;
                }
                else
                {
                    gelenAction = ctx.Request.Headers["SOAPAction"];
                    using var okuyucu = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    gelenGovde = await okuyucu.ReadToEndAsync();
                    bool fault = gelenGovde.Contains("<musteriNo>-1</musteriNo>", StringComparison.Ordinal);
                    ctx.Response.StatusCode = fault ? 500 : 200;
                    yanit = fault
                        ? """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultstring>Müşteri bulunamadı</faultstring></s:Fault></s:Body></s:Envelope>"""
                        : """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><MusteriGetirResponse xmlns="http://tempuri.org/"><Ad>Ali Yılmaz</Ad></MusteriGetirResponse></s:Body></s:Envelope>""";
                }

                byte[] veri = Encoding.UTF8.GetBytes(yanit);
                ctx.Response.ContentType = "text/xml; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(veri);
                ctx.Response.Close();
            }
        });

        var istemci = new SoapIstemcisi();
        (string ana, IReadOnlyList<string> ekler) =
            await istemci.WsdlIndirAsync($"{kokAdres}?wsdl", null, CancellationToken.None);
        Assert.Single(ekler); // xsd:import izlendi — DataContract şeması indi

        SoapServis servis = WsdlCozumleyici.Cozumle(ana, ekler);
        SoapOperasyon getir = servis.Operasyonlar.Single(o => o.Ad == "MusteriGetir");

        // Örnek zarfı gerçek değerle düzenleyip çağır (ekrandaki akışın birebir aynısı).
        string zarf = getir.OrnekZarf.Replace("<musteriNo>0</musteriNo>", "<musteriNo>42</musteriNo>");
        SoapCevap cevap = await istemci.CagirAsync(servis.Adres, getir.SoapAction, zarf, null, CancellationToken.None);

        Assert.Equal(200, cevap.HttpDurum);
        Assert.False(cevap.FaultMu);
        Assert.Contains("Ali Yılmaz", cevap.Govde, StringComparison.Ordinal);
        Assert.Equal($"\"{Tns}IQmsServis/MusteriGetir\"", gelenAction);
        Assert.Contains("<musteriNo>42</musteriNo>", gelenGovde!, StringComparison.Ordinal);

        // Fault: HTTP 500 + s:Fault gövdesi — istemci gövdeyi KORUR ve Fault'u işaretler.
        string kotuZarf = getir.OrnekZarf.Replace("<musteriNo>0</musteriNo>", "<musteriNo>-1</musteriNo>");
        SoapCevap faultCevap = await istemci.CagirAsync(servis.Adres, getir.SoapAction, kotuZarf, null, CancellationToken.None);
        Assert.Equal(500, faultCevap.HttpDurum);
        Assert.True(faultCevap.FaultMu);
        Assert.Contains("Müşteri bulunamadı", faultCevap.Govde, StringComparison.Ordinal);

        sunucu.Stop();
        await Task.WhenAny(sunucuGorevi, Task.Delay(1000));
    }
}
