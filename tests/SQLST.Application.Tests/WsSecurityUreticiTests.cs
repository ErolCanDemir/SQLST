using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>🔐 v19-S2 — WS-Security UsernameToken zarf enjeksiyonu (saf üretici).</summary>
public class WsSecurityUreticiTests
{
    private const string Zarf =
        "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">"
        + "<soapenv:Body><Islem xmlns=\"http://tempuri.org/\"><no>1</no></Islem></soapenv:Body>"
        + "</soapenv:Envelope>";

    private static readonly DateTime Zaman = new(2026, 8, 3, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Headersiz_zarfa_header_ve_usernametoken_eklenir()
    {
        (string? yeni, string? hata) = WsSecurityUretici.UsernameTokenEkle(
            Zarf, "ali", "gizli", "bm9uY2U=", Zaman);

        Assert.Null(hata);
        Assert.Contains("wsse:Security", yeni);
        Assert.Contains("<wsse:Username>ali</wsse:Username>", yeni);
        Assert.Contains("#PasswordText", yeni);                       // parola tipi açıkça yazar
        Assert.Contains(">gizli</wsse:Password>", yeni);
        Assert.Contains("<wsse:Nonce", yeni);
        Assert.Contains(">bm9uY2U=</wsse:Nonce>", yeni);
        Assert.Contains("<wsu:Created>2026-08-03T10:30:00.000Z</wsu:Created>", yeni);
        // Header, Body'den ÖNCE ve aynı SOAP ad alanında oluşturulmalı
        Assert.True(yeni!.IndexOf("Header", StringComparison.Ordinal)
            < yeni.IndexOf("Body", StringComparison.Ordinal));
        Assert.Contains("<Islem xmlns=\"http://tempuri.org/\">", yeni); // gövde bozulmadı
    }

    [Fact]
    public void Var_olan_header_korunur_icine_eklenir()
    {
        string headerli = Zarf.Replace("<soapenv:Body>",
            "<soapenv:Header><Ozel>x</Ozel></soapenv:Header><soapenv:Body>");

        (string? yeni, string? hata) = WsSecurityUretici.UsernameTokenEkle(
            headerli, "ali", "p", "bm9uY2U=", Zaman);

        Assert.Null(hata);
        Assert.Contains("<Ozel>x</Ozel>", yeni);       // kullanıcının başlığı ezilmedi
        Assert.Contains("wsse:UsernameToken", yeni);
    }

    [Fact]
    public void Zaten_security_varsa_dokunulmaz()
    {
        string guvenlikli = Zarf.Replace("<soapenv:Body>",
            "<soapenv:Header><wsse:Security xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" /></soapenv:Header><soapenv:Body>");

        (string? yeni, string? hata) = WsSecurityUretici.UsernameTokenEkle(
            guvenlikli, "ali", "p", "bm9uY2U=", Zaman);

        Assert.Null(yeni);
        Assert.Contains("zaten", hata, StringComparison.OrdinalIgnoreCase); // elle yazılan ezilmez
    }

    [Fact]
    public void Ozel_karakterler_xml_kacisiyla_guvenli_yazilir()
    {
        (string? yeni, string? hata) = WsSecurityUretici.UsernameTokenEkle(
            Zarf, "a<li>", "p&q\"r", "bm9uY2U=", Zaman);

        Assert.Null(hata);
        Assert.Contains("a&lt;li&gt;", yeni);          // enjeksiyon değil veri
        Assert.Contains("p&amp;q\"r", yeni);
        Assert.DoesNotContain("<li>", yeni);
    }

    [Theory]
    [InlineData("bozuk xml", "çözümlenemedi")]
    [InlineData("<kok></kok>", "Envelope değil")]
    public void Bozuk_girdi_kibar_hatayla_reddedilir(string zarf, string beklenen)
    {
        (string? yeni, string? hata) = WsSecurityUretici.UsernameTokenEkle(zarf, "a", "p", "n", Zaman);
        Assert.Null(yeni);
        Assert.Contains(beklenen, hata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kullanici_bos_ise_reddedilir()
    {
        (string? yeni, string? hata) = WsSecurityUretici.UsernameTokenEkle(Zarf, " ", "p", "n", Zaman);
        Assert.Null(yeni);
        Assert.Contains("kullanıcı adı", hata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Soap12_zarfinda_da_calisir() // Envelope ns'i ne olursa olsun Header o ns'te kurulur
    {
        string s12 = Zarf.Replace("http://schemas.xmlsoap.org/soap/envelope/",
            "http://www.w3.org/2003/05/soap-envelope");

        (string? yeni, string? hata) = WsSecurityUretici.UsernameTokenEkle(s12, "ali", "p", "n", Zaman);

        Assert.Null(hata);
        Assert.Contains("www.w3.org/2003/05/soap-envelope", yeni);
        Assert.Contains("wsse:UsernameToken", yeni);
    }
}
