using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Teşhis Merkezi (V2-S7) gerçek LocalDB doğrulaması — DMV sorguları çalışır ve modele oturur.
///
/// <b>Neden seri koşuyor?</b> Buradaki sorgular SUNUCU GENELİ DMV'lere bakar
/// (<c>dm_exec_query_stats</c> plan önbelleği, oturum listeleri), dolayısıyla paralel koşan
/// diğer LocalDB testlerinin ürettiği gürültüden etkilenir: tam süitte ara sıra düşüp izole
/// koşuda hep geçiyorlardı. Bakım paneli testlerinde aynı sorun 2026-07-18'de teşhis edilmiş
/// ve koleksiyon paralellikten ayrılarak çözülmüştü; bu sınıf o zaman gözden kaçmıştı —
/// aynı DMV'leri okuduğu için aynı ilaca ihtiyacı var. Doğru çözüm iddiaları gevşetmek
/// DEĞİLDİR: gevşetilmiş bir iddia testi susturur ama hiçbir şey kanıtlamaz.
/// </summary>
[Collection(TekBasinaKosanBakim.Ad)]
public class TeshisServisiLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());
    private static TeshisServisi Servis() => new(Executor);

    [Fact]
    public async Task Sunucu_baslangici_okunur_ve_gecmiste()
    {
        DateTime baslangic = await Servis().SunucuBaslangiciAsync(Profil(), CancellationToken.None);
        Assert.True(baslangic < DateTime.Now.AddMinutes(1), $"başlangıç gelecekte olamaz: {baslangic}");
        Assert.True(baslangic > DateTime.Now.AddYears(-1));
    }

    [Fact]
    public async Task Eksik_index_sorgusu_calisir_ve_oneri_uretilebilir()
    {
        // Öneriyi tetikle: index'siz tabloda seçici WHERE'li sorgu (optimizer kaydeder)
        string tablo = $"TeshisEksik_{Guid.NewGuid():N}";
        var tempdb = new ExecuteOptions { VeritabaniOverride = "tempdb" };
        await Executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{tablo}] (Id INT IDENTITY PRIMARY KEY, Grup INT NOT NULL, Ad NVARCHAR(50));
            INSERT INTO dbo.[{tablo}] (Grup, Ad)
            SELECT TOP 2000 o1.object_id % 100, N'x' FROM sys.objects o1 CROSS JOIN sys.objects o2;
            SELECT Ad FROM dbo.[{tablo}] WHERE Grup = 7;
            """, tempdb, CancellationToken.None);
        try
        {
            IReadOnlyList<EksikIndexOnerisi> oneriler =
                await Servis().EksikIndexlerAsync(Profil(), CancellationToken.None);

            // DMV sunucu geneli — sorgu çalıştı ve model oturdu; tempdb system DB (id 2)
            // süzüldüğü için bizim tablo görünmeyebilir; skor sıralaması korunmalı.
            for (int i = 1; i < oneriler.Count; i++)
                Assert.True(oneriler[i - 1].Skor >= oneriler[i].Skor, "skor azalan sıralı olmalı");
        }
        finally
        {
            await Executor.ExecuteAsync(Profil(),
                $"DROP TABLE IF EXISTS dbo.[{tablo}];", tempdb, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Mevcut_indexler_anahtar_sirali_okunur()
    {
        string tablo = $"TeshisMevcut_{Guid.NewGuid():N}";
        var tempdb = new ExecuteOptions { VeritabaniOverride = "tempdb" };
        await Executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{tablo}] (A INT, B INT, C INT, CONSTRAINT [PK_{tablo}] PRIMARY KEY (A));
            CREATE NONCLUSTERED INDEX [IX_{tablo}_BA] ON dbo.[{tablo}] (B, A) INCLUDE (C);
            """, tempdb, CancellationToken.None);
        try
        {
            IReadOnlyList<MevcutIndex> indexler =
                await Servis().MevcutIndexlerAsync(Profil(), "tempdb", CancellationToken.None);

            MevcutIndex ix = indexler.Single(i => i.Ad == $"IX_{tablo}_BA");
            Assert.Equal(["B", "A"], ix.AnahtarKolonlar); // sıra korunur (07-r2 §6)
            Assert.Equal(["C"], ix.IncludeKolonlar);
            Assert.False(ix.PkMi);
            Assert.True(indexler.Single(i => i.Ad == $"PK_{tablo}").PkMi);
        }
        finally
        {
            await Executor.ExecuteAsync(Profil(),
                $"DROP TABLE IF EXISTS dbo.[{tablo}];", tempdb, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Kullanilmayan_index_sorgusu_calisir_unique_pk_haric()
    {
        IReadOnlyList<KullanilmayanIndex> liste =
            await Servis().KullanilmayanIndexlerAsync(Profil(), "tempdb", CancellationToken.None);

        // tempdb'de aday olmayabilir — sorgu çalıştı ve script kuralı doğru olmalı
        Assert.All(liste, k =>
        {
            Assert.Equal(0, k.Okuma);
            Assert.True(k.Guncelleme > 0);
            Assert.Contains("DISABLE", k.DisableScript);
        });
    }

    [Fact]
    public async Task Rcsi_ve_havuz_verileri_tum_setleriyle_gelir()
    {
        // Set sayıları 2026-07-19 sadeleştirmesiyle azaldı (kullanıcı kararı): RCSI'den
        // "version store" ve "açık işlemler", Havuz'dan "bloklanan oturumlar" kaldırıldı —
        // üçü de Bakım sekmesindeki daha iyi karşılıklarıyla mükerrerdi.
        QueryResult rcsi = await Servis().RcsiVerileriAsync(Profil(), CancellationToken.None);
        Assert.True(rcsi.Basarili, rcsi.Hata?.Mesaj);
        Assert.Single(rcsi.ResultSetler);                 // yalnız DB durumları

        QueryResult havuz = await Servis().HavuzVerileriAsync(Profil(), CancellationToken.None);
        Assert.True(havuz.Basarili, havuz.Hata?.Mesaj);
        Assert.Equal(3, havuz.ResultSetler.Count);        // pahalı + bağlantı + yedek
        // Satır sayısı iddia edilmez: LocalDB boşta kapanıp yeniden başlar — soğuk
        // başlangıçta plan önbelleği (dm_exec_query_stats) meşru olarak boş olabilir.
        Assert.Equal(6, havuz.ResultSetler[0].Kolonlar.Count); // pahalı sorgu seti şeması

        QueryResult boyutlar = await Servis().TabloBoyutlariAsync(Profil(), "tempdb", CancellationToken.None);
        Assert.True(boyutlar.Basarili, boyutlar.Hata?.Mesaj);
    }

    [Fact]
    public void Rcsi_gecis_scripti_asamali_ve_uyarili()
    {
        string script = TeshisServisi.RcsiGecisScripti("LstQmsDb");

        Assert.Contains("TEK TUŞLA AÇILMAZ", script);
        Assert.Contains("ALLOW_SNAPSHOT_ISOLATION ON;", script);
        Assert.Contains("-- ALTER DATABASE [LstQmsDb] SET READ_COMMITTED_SNAPSHOT", script); // 2. aşama YORUMDA
        Assert.Contains("LOST UPDATE", script);
        Assert.Contains("NOLOCK", script);
    }
}
