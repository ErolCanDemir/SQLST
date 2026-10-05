using System.Security.Cryptography;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// DPAPI (CurrentUser) tabanlı parola koruması: profiles.json başka makineye
/// veya başka kullanıcıya taşınsa bile parola çözülemez (00-vizyon §4 ders 1).
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// <b>BU DEĞER ASLA DEĞİŞTİRİLEMEZ — bir isim değil, KRİPTOGRAFİK SABİTTİR.</b>
    ///
    /// DPAPI, şifrelerken kullanılan entropy ile çözerken kullanılanın AYNI olmasını şart
    /// koşar. Bu dize değişirse kullanıcının kayıtlı bütün bağlantı parolaları
    /// <b>kalıcı olarak çözülemez</b> hâle gelir — geri dönüşü yoktur, kullanıcı hepsini
    /// elle yeniden girmek zorunda kalır.
    ///
    /// Ürün adı 2026-07-20'de MiniSSMS → SQLST oldu; bu satır <b>bilerek</b> eski adıyla
    /// bırakıldı. Toplu ad değişiminde en kolay gözden kaçacak ve en pahalıya patlayacak
    /// satır burasıydı. Yeni bir entropy'ye geçmek isteyen, önce eskisiyle çözüp yenisiyle
    /// yeniden şifreleyen bir GÖÇ yazmalıdır — tek başına bu sabiti değiştirmek veri kaybıdır.
    /// (<c>DpapiEntropyTests</c> bu satırı testle de kilitler.)
    /// </summary>
    private static readonly byte[] Entropy = "MiniSSMS.v1"u8.ToArray();

    public string Sifrele(string duzMetin)
    {
        byte[] blob = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(duzMetin), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    public string Coz(string sifreliBase64)
    {
        byte[] duz = ProtectedData.Unprotect(
            Convert.FromBase64String(sifreliBase64), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(duz);
    }
}
