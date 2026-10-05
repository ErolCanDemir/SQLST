using Microsoft.Data.SqlClient;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>Profil + seçeneklerden SqlClient bağlantı dizesi kurar (tek yer).</summary>
internal sealed class BaglantiDizesiKurucu
{
    private readonly ISecretProtector _protector;

    public BaglantiDizesiKurucu(ISecretProtector protector) => _protector = protector;

    /// <param name="havuz">
    /// Kalıcı oturumda (07-r2 §2) açık transaction'lı bağlantı pool'a DÖNDÜRÜLMEMELİDİR →
    /// oturum bağlantısı için false; stateless tek-atış için true.
    /// </param>
    public string Kur(ConnectionProfile profil, string? veritabaniOverride, bool havuz)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = profil.Sunucu,
            ConnectTimeout = profil.BaglantiTimeoutSn,
            ApplicationName = "SQLST",
            TrustServerCertificate = true, // iç ağ sunucuları çoğunlukla self-signed sertifikalı
            MultipleActiveResultSets = false,
            Pooling = havuz,
        };

        // Bağlantı sunucuyadır (FG-1.1); veritabanı yalnız sorgu bazında override ile gelir.
        if (!string.IsNullOrWhiteSpace(veritabaniOverride))
            b.InitialCatalog = veritabaniOverride;

        if (profil.Kimlik == KimlikTuru.Windows)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = profil.KullaniciAdi ?? "";
            b.Password = profil.ParolaSifreli is null ? "" : _protector.Coz(profil.ParolaSifreli);
        }

        if (profil.SaltOkunur)
            b.ApplicationIntent = ApplicationIntent.ReadOnly; // 2. katman (FG-6.5)

        return b.ConnectionString;
    }
}
