using System.Data;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>Edit modu (V2-S5) gerçek LocalDB doğrulaması: meta bayrakları + uçtan uca uygulama + çakışma.</summary>
public class DuzenlemeLocalDbTests : IAsyncLifetime
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb",
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows,
        BaglantiTimeoutSn = 60,
    };

    private readonly string _tablo = $"DuzenlemeE2e_{Guid.NewGuid():N}";
    private readonly MssqlLehcesi _lehce = new(new DpapiSecretProtector());
    private readonly SqlExecutor _executor;
    private readonly SchemaService _schema;
    private static readonly ExecuteOptions Tempdb = new() { VeritabaniOverride = "tempdb" };

    public DuzenlemeLocalDbTests()
    {
        _executor = new SqlExecutor(_lehce);
        _schema = new SchemaService(_executor, _lehce);
    }

    private SemaNesnesi Nesne() => new("tempdb", "dbo", _tablo, SemaNesneTuru.Tablo, [], []);

    public async Task InitializeAsync()
        => await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{_tablo}] (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Ad NVARCHAR(50) NOT NULL,
                Bakiye DECIMAL(18,2) NULL,
                Rv ROWVERSION,
                Katlanmis AS (Id * 2)
            );
            INSERT INTO dbo.[{_tablo}] (Ad, Bakiye) VALUES (N'Ali', 10.50), (N'Ayşe', NULL);
            """, Tempdb, CancellationToken.None);

    public async Task DisposeAsync()
        => await _executor.ExecuteAsync(Profil(),
            $"DROP TABLE IF EXISTS dbo.[{_tablo}];", Tempdb, CancellationToken.None);

    [Fact]
    public async Task Meta_bayraklari_dogru_okunur()
    {
        DuzenlemeMetasi meta = await _schema.DuzenlemeMetaAsync(Profil(), Nesne(), CancellationToken.None);

        Assert.True(meta.DuzenlenebilirMi);
        DuzenlemeKolonu id = meta.Kolonlar.Single(k => k.Ad == "Id");
        Assert.True(id.PkMi);
        Assert.True(id.IdentityMi);
        Assert.False(id.Yazilabilir);

        DuzenlemeKolonu rv = meta.Kolonlar.Single(k => k.Ad == "Rv");
        Assert.True(rv.RowversionMi);
        Assert.Same(rv, meta.Rowversion);

        Assert.True(meta.Kolonlar.Single(k => k.Ad == "Katlanmis").ComputedMi);
        Assert.True(meta.Kolonlar.Single(k => k.Ad == "Bakiye").KiyasGuvenliMi);
        Assert.True(meta.Kolonlar.Single(k => k.Ad == "Ad").Yazilabilir);
    }

    [Fact]
    public async Task Declared_PK_yoksa_tek_IDENTITY_kolon_anahtar_sayilir_ve_duzenlenebilir()
    {
        // Kullanıcı bulgusu 2026-07-27: PK CONSTRAINT'i olmayan ama IDENTITY Id'li tablo
        // (Yonetim.KullaniciRolleri gibi) edit modda salt-okunur kalıyordu. Artık tek identity
        // anahtar sayılır → düzenlenebilir.
        string t = $"NoPkIdentity_{Guid.NewGuid():N}";
        await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{t}] (Id INT IDENTITY(1,1) NOT NULL, Aktif BIT NOT NULL);
            INSERT INTO dbo.[{t}] (Aktif) VALUES (1), (0);
            """, Tempdb, CancellationToken.None);
        try
        {
            var nesne = new SemaNesnesi("tempdb", "dbo", t, SemaNesneTuru.Tablo, [], []);
            DuzenlemeMetasi meta = await _schema.DuzenlemeMetaAsync(Profil(), nesne, CancellationToken.None);

            Assert.True(meta.DuzenlenebilirMi);                         // artık düzenlenebilir
            DuzenlemeKolonu anahtar = Assert.Single(meta.PkKolonlari);
            Assert.Equal("Id", anahtar.Ad);                            // identity anahtar oldu
            Assert.True(anahtar.IdentityMi);
        }
        finally
        {
            await _executor.ExecuteAsync(Profil(), $"DROP TABLE IF EXISTS dbo.[{t}];", Tempdb, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Declared_PK_yoksa_UNIQUE_index_anahtar_sayilir_ve_duzenlenebilir()
    {
        // Kullanıcı bulgusu 2026-07-27: PK CONSTRAINT'i olmayan ama benzersiz index'li tablo
        // (identity de değil) edit modda salt-okunur kalıyordu. Artık UNIQUE index anahtar sayılır.
        string t = $"NoPkUnique_{Guid.NewGuid():N}";
        await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE dbo.[{t}] (Kod INT NOT NULL, Ad NVARCHAR(50) NULL);
            CREATE UNIQUE INDEX UX_{t} ON dbo.[{t}] (Kod);
            INSERT INTO dbo.[{t}] (Kod, Ad) VALUES (10, N'A'), (20, N'B');
            """, Tempdb, CancellationToken.None);
        try
        {
            var nesne = new SemaNesnesi("tempdb", "dbo", t, SemaNesneTuru.Tablo, [], []);
            DuzenlemeMetasi meta = await _schema.DuzenlemeMetaAsync(Profil(), nesne, CancellationToken.None);

            Assert.True(meta.DuzenlenebilirMi);                      // benzersiz index → düzenlenebilir
            DuzenlemeKolonu anahtar = Assert.Single(meta.PkKolonlari);
            Assert.Equal("Kod", anahtar.Ad);                        // unique index kolonu anahtar oldu
        }
        finally
        {
            await _executor.ExecuteAsync(Profil(), $"DROP TABLE IF EXISTS dbo.[{t}];", Tempdb, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Guncelle_sil_ekle_tek_islemde_uygulanir()
    {
        var servis = new QueryService();
        var fabrika = new OturumFabrikasi(new DpapiSecretProtector());
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        DuzenlemeMetasi meta = await _schema.DuzenlemeMetaAsync(Profil(), Nesne(), CancellationToken.None);
        DataTable veri = await YukleAsync(oturum, meta);

        veri.Rows[0]["Bakiye"] = 99.99m;         // Ali → UPDATE
        veri.Rows[1].Delete();                    // Ayşe → DELETE
        DataRow yeni = veri.NewRow();
        yeni["Ad"] = "Veli'nin";                  // tek tırnaklı ad → INSERT (kaçış kanıtı)
        veri.Rows.Add(yeni);

        IReadOnlyList<string> komutlar = DmlUretici.Uret(_lehce, meta, veri);
        Assert.Equal(3, komutlar.Count);
        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            servis, _lehce, oturum, komutlar, Tempdb, CancellationToken.None);
        Assert.True(basarili, mesaj);

        QueryResult son = await oturum.CalistirAsync(
            $"SELECT Ad, Bakiye FROM dbo.[{_tablo}] ORDER BY Id;", Tempdb, CancellationToken.None);
        Assert.Equal(2, son.ResultSetler[0].Satirlar.Count);
        Assert.Equal("Ali", son.ResultSetler[0].Satirlar[0][0]);
        Assert.Equal(99.99m, son.ResultSetler[0].Satirlar[0][1]);
        Assert.Equal("Veli'nin", son.ResultSetler[0].Satirlar[1][0]);
    }

    [Fact]
    public async Task Baskasi_degistirdiyse_rowversion_cakismasi_hepsi_geri_alinir()
    {
        var servis = new QueryService();
        var fabrika = new OturumFabrikasi(new DpapiSecretProtector());
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        DuzenlemeMetasi meta = await _schema.DuzenlemeMetaAsync(Profil(), Nesne(), CancellationToken.None);
        DataTable veri = await YukleAsync(oturum, meta);
        veri.Rows[0]["Ad"] = "Bizim Değişiklik";

        // Araya "başkası" girer: aynı satırı değiştirir → rowversion artık farklı
        await _executor.ExecuteAsync(Profil(),
            $"UPDATE dbo.[{_tablo}] SET Ad = N'Rakip' WHERE Id = 1;", Tempdb, CancellationToken.None);

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            servis, _lehce, oturum, DmlUretici.Uret(_lehce, meta, veri), Tempdb, CancellationToken.None);

        Assert.False(basarili);
        Assert.Contains("Çakışma", mesaj);
        QueryResult son = await oturum.CalistirAsync(
            $"SELECT Ad FROM dbo.[{_tablo}] WHERE Id = 1;", Tempdb, CancellationToken.None);
        Assert.Equal("Rakip", son.ResultSetler[0].Satirlar[0][0]); // bizimki yazılmadı, rakibinki durdu
    }

    /// <summary>VM'in tipli tablo kurulumunun testteki karşılığı (ClrTip → kolon tipi).</summary>
    private async Task<DataTable> YukleAsync(IDbOturum oturum, DuzenlemeMetasi meta)
    {
        QueryResult sonuc = await oturum.CalistirAsync(
            $"SELECT TOP (200) * FROM {meta.TamAdKoseli};", Tempdb, CancellationToken.None);
        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);

        var tablo = new DataTable();
        foreach (KolonBilgisi k in sonuc.ResultSetler[0].Kolonlar)
            tablo.Columns.Add(k.Ad, k.ClrTip ?? typeof(object));
        foreach (object?[] satir in sonuc.ResultSetler[0].Satirlar)
            tablo.Rows.Add(satir);
        tablo.AcceptChanges();
        return tablo;
    }
}
