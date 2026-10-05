using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

public class ProfilDogrulayiciTests
{
    private static ConnectionProfile GecerliProfil() => new()
    {
        Ad = "Dev",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
    };

    [Fact]
    public void Gecerli_profil_hatasiz_gecer()
    {
        Assert.Empty(ProfilDogrulayici.Dogrula(GecerliProfil()));
    }

    [Fact]
    public void Ad_ve_sunucu_zorunludur()
    {
        var p = GecerliProfil();
        p.Ad = "  ";
        p.Sunucu = "";
        IReadOnlyList<string> hatalar = ProfilDogrulayici.Dogrula(p);
        Assert.Equal(2, hatalar.Count);
    }

    [Fact]
    public void Sql_kimlikte_kullanici_adi_zorunludur()
    {
        var p = GecerliProfil();
        p.Kimlik = KimlikTuru.Sql;
        p.KullaniciAdi = null;
        Assert.Contains(ProfilDogrulayici.Dogrula(p), h => h.Contains("kullanıcı adı"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(301, false)]
    [InlineData(1, true)]
    [InlineData(300, true)]
    public void Baglanti_timeout_araligi_denetlenir(int sn, bool gecerli)
    {
        var p = GecerliProfil();
        p.BaglantiTimeoutSn = sn;
        Assert.Equal(gecerli, ProfilDogrulayici.Dogrula(p).Count == 0);
    }
}
