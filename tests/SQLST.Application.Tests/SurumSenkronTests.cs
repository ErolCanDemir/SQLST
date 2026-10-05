using System.IO;
using System.Text.RegularExpressions;

namespace SQLST.Application.Tests;

/// <summary>
/// v22-S4 (çok ajanlı çökme denetiminin yan bulgusu): kurulum sürümü ile UYGULAMA sürümü kaymıştı.
/// <c>installer/sqlst.iss</c> 0.24.0 derken <c>SQLST.App.csproj</c> <b>0.11.4</b>'te kalmıştı —
/// yani kurulu exe kendini 0.11.4 diye tanıtıyordu (dosya özellikleri, "Hakkında").
///
/// Bu kozmetik değil: dört turdur süren çökme soruşturmasında ilk sorulan şey "hangi sürüm
/// çöktü?"dür ve makinedeki tek makine-okunur cevap yanlıştı. Bu test kaymayı kilitler.
/// </summary>
public class SurumSenkronTests
{
    private static string DepoKoku()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SQLST.slnx")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    [Fact]
    public void Installer_ve_csproj_surumleri_AYNI()
    {
        string kok = DepoKoku();

        string iss = File.ReadAllText(Path.Combine(kok, "installer", "sqlst.iss"));
        Match issSurum = Regex.Match(iss, "#define\\s+Surum\\s+\"([^\"]+)\"");
        Assert.True(issSurum.Success, "installer/sqlst.iss içinde #define Surum bulunamadı");

        string csproj = File.ReadAllText(Path.Combine(kok, "src", "SQLST.App", "SQLST.App.csproj"));
        Match csprojSurum = Regex.Match(csproj, "<Version>([^<]+)</Version>");
        Assert.True(csprojSurum.Success, "SQLST.App.csproj içinde <Version> bulunamadı");

        Assert.Equal(issSurum.Groups[1].Value, csprojSurum.Groups[1].Value);
    }
}
