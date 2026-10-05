using SQLST.Contracts;
using Xunit;

namespace SQLST.Application.Tests;

public class AgYardimcisiTests
{
    private static bool Yerel(string url) => AgYardimcisi.YerelMi(new Uri(url));

    [Theory]
    [InlineData("http://localhost:8080/servis")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://sunucu/servis")]          // noktasız intranet makine adı
    [InlineData("http://192.168.1.50:8080/wsdl")] // LAN — kullanıcı bulgusu
    [InlineData("http://10.0.0.5/x")]
    [InlineData("http://172.16.4.9/x")]
    [InlineData("http://172.31.255.1/x")]
    [InlineData("http://169.254.10.10/x")]        // link-local
    [InlineData("http://[fe80::1]/x")]            // IPv6 link-local
    public void Yerel_adresler_proxy_muaf(string url) => Assert.True(Yerel(url));

    [Theory]
    [InlineData("http://8.8.8.8/x")]              // public IP
    [InlineData("https://example.com/servis")]    // FQDN (güvenle yerel sayılamaz)
    [InlineData("http://172.32.0.1/x")]           // 172.32 = 12-blok DIŞI
    [InlineData("http://172.15.0.1/x")]           // 172.15 = 12-blok DIŞI
    [InlineData("https://api.kurum.com/soap")]
    public void Uzak_adresler_proxy_gerektirir(string url) => Assert.False(Yerel(url));
}
