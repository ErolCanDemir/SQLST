using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V3-S1 / Faz 2 CANLI KANITI (MySQL ailesi): değişmemiş veri erişim gövdesi yalnız
/// <see cref="MySqlLehcesi"/> takılarak gerçek bir MariaDB'yi sürer (MySqlConnector — MIT;
/// MariaDB'nin resmî önerdiği sürücü, MySQL ile ortak protokol).
///
/// Ortam-kapılı: 127.0.0.1:3307'de erişilebilir bir MariaDB/MySQL + sqlst_demo
/// (musteri 3 satır + aktif_musteri view + musteri_getir SP) yoksa test ATLANIR.
/// Kurulum betiği: scratchpad/maria-seed.sql (taşınabilir MariaDB ile ayağa kaldırılır).
/// </summary>
public class MySqlCanliTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 3307;
    private const string Db = "sqlst_demo";

    private static ConnectionProfile Profil() => new()
    {
        Ad = "maria-demo",
        Motor = MotorTuru.MySql,
        Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql,   // skip-grant-tables — parola yok sayılır
        KullaniciAdi = "root",
        BaglantiTimeoutSn = 10,
    };

    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    private static (SqlExecutor Executor, ILehce Lehce) Kur()
    {
        ILehce lehce = new MySqlLehcesi(new DpapiSecretProtector());
        return (new SqlExecutor(lehce), lehce);
    }

    [Fact]
    public async Task Degismemis_executor_mariadb_sorgusunu_calistirir_ve_satirlari_okur()
    {
        if (!Erisilebilir()) return; // ortam-kapılı: MariaDB yoksa atla (CI kırılmaz)
        (SqlExecutor executor, _) = Kur();

        QueryResult sonuc = await executor.ExecuteAsync(
            Profil(),
            "SELECT id, ad, bakiye FROM musteri ORDER BY id;",
            new ExecuteOptions { VeritabaniOverride = Db }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        ResultSetData set = Assert.Single(sonuc.ResultSetler);
        Assert.Equal(3, set.Satirlar.Count);
        Assert.Equal("Ahmet Celik", (string)set.Satirlar[0][1]!);
        Assert.Equal(DBNull.Value, set.Satirlar[1][2]); // NULL bakiye
    }

    [Fact]
    public async Task Sema_agaci_mariadb_katalogundan_yuklenir_pk_view_ve_sp_ile()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        (SqlExecutor executor, ILehce lehce) = Kur();
        var schema = new SchemaService(executor, lehce);

        IReadOnlyList<VeritabaniBilgisi> dbler = await schema.VeritabanlariAsync(Profil(), CancellationToken.None);
        Assert.Contains(dbler, d => d.Ad == Db && !d.SistemMi);
        Assert.Contains(dbler, d => d.Ad == "mysql" && d.SistemMi);

        SemaOnbellegi sema = await schema.YukleAsync(Profil(), Db, CancellationToken.None);

        SemaNesnesi musteri = Assert.Single(sema.Nesneler, n => n.Ad == "musteri");
        Assert.Equal(SemaNesneTuru.Tablo, musteri.Tur);
        Assert.Equal(5, musteri.Kolonlar.Count);
        Assert.True(musteri.Kolonlar.Single(k => k.Ad == "id").PkMi);          // truthy 1/0 yolu
        Assert.Equal("varchar(50)", musteri.Kolonlar.Single(k => k.Ad == "ad").Tip);
        Assert.True(musteri.Kolonlar.Single(k => k.Ad == "bakiye").NullOlabilir);

        Assert.Single(sema.Nesneler, n => n.Ad == "aktif_musteri" && n.Tur == SemaNesneTuru.View);
        SemaNesnesi sp = Assert.Single(sema.Nesneler, n => n.Ad == "musteri_getir");
        Assert.Equal(SemaNesneTuru.StoredProcedure, sp.Tur);
        Assert.Single(sp.Parametreler); // p_id
    }

    [Fact]
    public async Task Use_ile_veritabani_gecisi_kalici_oturumda_calisir()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        ILehce lehce = new MySqlLehcesi(new DpapiSecretProtector());
        await using IDbOturum oturum = new OturumFabrikasi(lehce).Olustur(Profil());

        // USE `sqlst_demo` yolu (AcikBaglantidaVeritabaniDegisir=true — MSSQL ile aynı dal)
        QueryResult sonuc = await oturum.CalistirAsync(
            "SELECT COUNT(*) FROM musteri;",
            new ExecuteOptions { VeritabaniOverride = Db }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.Equal(3, Convert.ToInt32(sonuc.ResultSetler[0].Satirlar[0][0]));
    }

    [Fact]
    public async Task View_ve_sp_tanimi_information_schemadan_okunur()
    {
        if (!Erisilebilir()) return; // ortam-kapılı
        (SqlExecutor executor, ILehce lehce) = Kur();
        var schema = new SchemaService(executor, lehce);

        var view = new SemaNesnesi(Db, Db, "aktif_musteri", SemaNesneTuru.View, [], []);
        string? viewTanim = await schema.TanimGetirAsync(Profil(), view, CancellationToken.None);
        Assert.NotNull(viewTanim);
        Assert.Contains("aktif", viewTanim!);

        var sp = new SemaNesnesi(Db, Db, "musteri_getir", SemaNesneTuru.StoredProcedure, [], []);
        string? spTanim = await schema.TanimGetirAsync(Profil(), sp, CancellationToken.None);
        Assert.NotNull(spTanim);
        Assert.Contains("SELECT", spTanim!, StringComparison.OrdinalIgnoreCase);
    }
}
