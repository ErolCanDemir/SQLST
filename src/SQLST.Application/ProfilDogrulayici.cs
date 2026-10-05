using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Profil formunun kaydet/bağlan öncesi doğrulaması (FG-1.x).</summary>
public static class ProfilDogrulayici
{
    public static IReadOnlyList<string> Dogrula(ConnectionProfile profil)
    {
        var hatalar = new List<string>();

        if (string.IsNullOrWhiteSpace(profil.Ad))
            hatalar.Add("Profil adı boş olamaz.");
        if (string.IsNullOrWhiteSpace(profil.Sunucu))
            hatalar.Add("Sunucu adı boş olamaz.");
        if (profil.Kimlik == KimlikTuru.Sql && string.IsNullOrWhiteSpace(profil.KullaniciAdi))
            hatalar.Add("SQL kimlik doğrulamasında kullanıcı adı zorunludur.");
        if (profil.BaglantiTimeoutSn is < 1 or > 300)
            hatalar.Add("Bağlantı zaman aşımı 1-300 sn aralığında olmalıdır.");

        return hatalar;
    }
}
