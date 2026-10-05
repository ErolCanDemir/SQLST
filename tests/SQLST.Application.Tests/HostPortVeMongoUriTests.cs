using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>İnceleme 2026-07-30 bekleyenleri: IPv6 host ayrımı + Mongo SRV/URI kimlik akışı.</summary>
public class HostPortVeMongoUriTests
{
    [Theory]
    [InlineData("localhost", "localhost", null)]
    [InlineData("localhost:5432", "localhost", 5432)]
    [InlineData("sunucu.ic.lst:3306", "sunucu.ic.lst", 3306)]
    [InlineData("::1", "::1", null)]                          // çıplak IPv6 — son ':1' port DEĞİL
    [InlineData("fe80::9656:d028:8652:66b6", "fe80::9656:d028:8652:66b6", null)]
    [InlineData("[::1]", "::1", null)]
    [InlineData("[::1]:5432", "::1", 5432)]
    [InlineData("[fe80::1]:1433", "fe80::1", 1433)]
    [InlineData("host:abc", "host:abc", null)]                // sayı değil → port ayrılmaz
    [InlineData("host:99999", "host:99999", null)]            // aralık dışı → port ayrılmaz
    [InlineData(" localhost:5432 ", "localhost", 5432)]
    public void Ayir_hostu_ve_portu_dogru_boler(string girdi, string host, int? port)
        => Assert.Equal((host, port), HostPortAyristirici.Ayir(girdi));

    private sealed class DuzProtector : ISecretProtector
    {
        public string Sifrele(string duzMetin) => duzMetin;
        public string Coz(string sifreliBase64) => sifreliBase64;
    }

    private static ConnectionProfile Profil(string sunucu, string? kullanici = null, string? parola = null) => new()
    {
        Motor = MotorTuru.Mongo, Sunucu = sunucu, BaglantiTimeoutSn = 5,
        Kimlik = kullanici is null ? KimlikTuru.Windows : KimlikTuru.Sql,
        KullaniciAdi = kullanici, ParolaSifreli = parola,
    };

    [Fact]
    public void Mongo_ciplak_ipv6_koseli_paranteze_alinir()
        => Assert.StartsWith("mongodb://[::1]/", MongoVeriKaynagi.BaglantiDizesi(Profil("::1"), new DuzProtector()));

    [Fact]
    public void Mongo_parantezli_ipv6_port_korunur()
        => Assert.StartsWith("mongodb://[::1]:27018/",
            MongoVeriKaynagi.BaglantiDizesi(Profil("[::1]:27018"), new DuzProtector()));

    [Fact]
    public void Mongo_srv_uride_profil_kimligi_uriye_islenir()
        => Assert.Equal("mongodb+srv://ali:gizli@kume.ornek.net/?retryWrites=true",
            MongoVeriKaynagi.BaglantiDizesi(
                Profil("mongodb+srv://kume.ornek.net/?retryWrites=true", "ali", "gizli"), new DuzProtector()));

    [Fact]
    public void Mongo_uri_kendi_kimligini_tasiyorsa_dokunulmaz()
        => Assert.Equal("mongodb+srv://veli:sifre@kume.ornek.net/",
            MongoVeriKaynagi.BaglantiDizesi(
                Profil("mongodb+srv://veli:sifre@kume.ornek.net/", "ali", "gizli"), new DuzProtector()));

    [Fact]
    public void Mongo_kimlik_ozel_karakterler_uri_kacisiyla_yazilir()
        => Assert.StartsWith("mongodb://ali%40lst:p%40rol%3Aa@localhost:27017/",
            MongoVeriKaynagi.BaglantiDizesi(Profil("localhost:27017", "ali@lst", "p@rol:a"), new DuzProtector()));
}
