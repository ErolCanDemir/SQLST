using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 📤 Şema Kopyalama (2026-07-31) uçtan uca: LocalDB'de İZOLE kaynak DB kur (2 tablo + FK + view + SP)
/// → servis hedef DB'yi kendisi OLUŞTURUP şemayı kurar → hedefte nesneler/FK doğrulanır → iki DB de
/// düşürülür. Kaynak izole DB'dir (tempdb değil — paralel testlerin geçici nesneleri karışmasın).
/// </summary>
public class SemaKopyalamaLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "kopya-test", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, Motor = MotorTuru.Mssql, BaglantiTimeoutSn = 60,
    };

    [Fact]
    public async Task Sema_kopyalama_uctan_uca_tablolar_fk_view_sp()
    {
        var lehceler = new LehceSaglayici(new DpapiSecretProtector());
        var executor = new SqlExecutor(lehceler);
        var semaServisi = new SchemaService(executor, lehceler);
        var servis = new SemaKopyalamaServisi(semaServisi, new TeshisServisi(executor), executor, lehceler);
        ConnectionProfile p = Profil();

        string kaynakDb = $"SqlstKpyKaynak_{Guid.NewGuid():N}"[..24];
        string hedefDb = $"SqlstKpyHedef_{Guid.NewGuid():N}"[..24];

        async Task<QueryResult> KosAsync(string sql, string? db = null)
            => await executor.ExecuteAsync(p, sql, new ExecuteOptions { VeritabaniOverride = db }, CancellationToken.None);

        // Kaynak DB + mini şema
        Assert.Null((await KosAsync($"CREATE DATABASE [{kaynakDb}];")).Hata);
        try
        {
            QueryResult kur = await KosAsync($"""
                CREATE TABLE dbo.Musteri (Id INT NOT NULL PRIMARY KEY, Ad NVARCHAR(50) NOT NULL);
                CREATE TABLE dbo.Siparis (Id INT NOT NULL PRIMARY KEY, MusteriId INT NOT NULL, Tutar DECIMAL(18,2) NULL);
                ALTER TABLE dbo.Siparis ADD CONSTRAINT FK_Sip_Mus FOREIGN KEY (MusteriId) REFERENCES dbo.Musteri (Id);
                CREATE INDEX IX_Siparis_MusteriId ON dbo.Siparis (MusteriId);
                """, kaynakDb);
            Assert.Null(kur.Hata);
            // view + SP ayrı batch'ler (CREATE VIEW/PROC batch'in ilki olmalı)
            Assert.Null((await KosAsync(
                "CREATE VIEW dbo.vwSiparisOzet AS SELECT s.Id, m.Ad, s.Tutar FROM dbo.Siparis s JOIN dbo.Musteri m ON m.Id = s.MusteriId;",
                kaynakDb)).Hata);
            Assert.Null((await KosAsync(
                "CREATE PROCEDURE dbo.spMusteriSay AS SELECT COUNT(*) FROM dbo.Musteri;", kaynakDb)).Hata);

            // Kopyala (hedef DB'yi servis oluşturur)
            SemaKopyaSonucu sonuc = await servis.KopyalaAsync(
                p, kaynakDb, p, hedefDb, new SemaKopyaKapsami(), null, CancellationToken.None);

            Assert.True(sonuc.Basarili, string.Join(" | ", sonuc.Hatalar) + (sonuc.GenelHata ?? ""));
            Assert.Equal(sonuc.ToplamAdim, sonuc.BasariliAdim);
            Assert.True(sonuc.ToplamAdim >= 5); // 2 tablo + 1 FK + 1 view + 1 SP

            // Hedefte doğrula
            SemaOnbellegi hedefSema = await semaServisi.YukleAsync(p, hedefDb, CancellationToken.None);
            Assert.Contains(hedefSema.Nesneler, n => n.Tur == SemaNesneTuru.Tablo && n.Ad == "Musteri");
            Assert.Contains(hedefSema.Nesneler, n => n.Tur == SemaNesneTuru.Tablo && n.Ad == "Siparis");
            Assert.Contains(hedefSema.Nesneler, n => n.Tur == SemaNesneTuru.View && n.Ad == "vwSiparisOzet");
            Assert.Contains(hedefSema.Nesneler, n => n.Tur == SemaNesneTuru.StoredProcedure && n.Ad == "spMusteriSay");
            IReadOnlyList<YabanciAnahtar> hedefFk = await semaServisi.YabanciAnahtarlarAsync(p, hedefDb, CancellationToken.None);
            Assert.Single(hedefFk);
            Assert.Equal("Siparis", hedefFk[0].KaynakTablo);

            // Bonus: tek script üretimi de tutarlı (aynı plan, GO'lu)
            var uyarilar = new List<string>();
            IReadOnlyList<SemaKopyaAdimi> plan = await servis.PlanUretAsync(
                p, kaynakDb, new SemaKopyaKapsami(), uyarilar, null, CancellationToken.None);
            string script = SemaKopyalamaServisi.TekScript(plan, kaynakDb);
            Assert.Contains("CREATE TABLE", script);
            Assert.Contains("FOREIGN KEY", script);
            Assert.Contains("GO", script);
            Assert.Empty(uyarilar);
        }
        finally
        {
            foreach (string db in new[] { kaynakDb, hedefDb })
                await KosAsync($"""
                    IF DB_ID('{db}') IS NOT NULL BEGIN
                        ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{db}];
                    END
                    """);
        }
    }
}
