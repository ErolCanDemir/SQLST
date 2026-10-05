using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v6-S2 — FK okuma CANLI doğrulaması (LocalDB/MSSQL; kullanıcının motoru). Bu, Görsel Sorgu
/// Tasarımcısı'nın "iki tabloyu bağlayınca ON kolonlarını otomatik öner" özelliğinin dayandığı
/// altyapıdır. Birim testi "sorgu çalıştı" demez; burada GERÇEK tablolar + gerçek FK kurulur,
/// okunur ve <b>kolon eşleşmesinin/sırasının doğru</b> geldiği kanıtlanır — bileşik anahtar dahil.
/// </summary>
public class YabanciAnahtarLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());
    private static readonly ExecuteOptions Tempdb = new() { VeritabaniOverride = "tempdb" };
    private static SchemaService Servis() => new(Executor, new MssqlLehcesi(new DpapiSecretProtector()));

    [Fact]
    public async Task Tekil_ve_bilesik_FK_dogru_okunur()
    {
        string ek = Guid.NewGuid().ToString("N")[..8];
        string musteri = $"Musteri_{ek}", siparis = $"Siparis_{ek}", kalem = $"Kalem_{ek}";

        // Musteri(Id PK) ← Siparis(MusteriId) tekil FK
        // Siparis(No, Yil bileşik PK) ← Kalem(SiparisNo, SiparisYil) BİLEŞİK FK
        await Executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{musteri}] (Id INT PRIMARY KEY);
            CREATE TABLE dbo.[{siparis}] (
                No INT, Yil INT, MusteriId INT NULL,
                CONSTRAINT [PK_{siparis}] PRIMARY KEY (No, Yil),
                CONSTRAINT [FK_{siparis}_M] FOREIGN KEY (MusteriId) REFERENCES dbo.[{musteri}](Id));
            CREATE TABLE dbo.[{kalem}] (
                Id INT PRIMARY KEY, SiparisNo INT, SiparisYil INT,
                CONSTRAINT [FK_{kalem}_S] FOREIGN KEY (SiparisNo, SiparisYil)
                    REFERENCES dbo.[{siparis}](No, Yil));
            """, Tempdb, CancellationToken.None);

        try
        {
            IReadOnlyList<YabanciAnahtar> fkler =
                await Servis().YabanciAnahtarlarAsync(Profil(), "tempdb", CancellationToken.None);

            // Tekil FK: Siparis.MusteriId → Musteri.Id
            YabanciAnahtar tekil = fkler.Single(f => f.KaynakTablo == siparis && f.HedefTablo == musteri);
            Assert.Equal(["MusteriId"], tekil.KaynakKolonlar);
            Assert.Equal(["Id"], tekil.HedefKolonlar);

            // Bileşik FK: Kalem(SiparisNo, SiparisYil) → Siparis(No, Yil) — SIRA korunmalı
            YabanciAnahtar bilesik = fkler.Single(f => f.KaynakTablo == kalem && f.HedefTablo == siparis);
            Assert.Equal(["SiparisNo", "SiparisYil"], bilesik.KaynakKolonlar);
            Assert.Equal(["No", "Yil"], bilesik.HedefKolonlar);
            Assert.Equal("dbo", bilesik.KaynakSema);
        }
        finally
        {
            // FK'ler önce (bağımlılık sırası), sonra tablolar.
            await Executor.ExecuteAsync(Profil(), $"""
                DROP TABLE IF EXISTS dbo.[{kalem}];
                DROP TABLE IF EXISTS dbo.[{siparis}];
                DROP TABLE IF EXISTS dbo.[{musteri}];
                """, Tempdb, CancellationToken.None);
        }
    }
}
