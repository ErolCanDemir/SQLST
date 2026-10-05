using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V3-S1 / Faz 1 CANLI KANITI: değişmemiş veri erişim gövdesinin (SqlExecutor + SonucOkuyucu)
/// yalnız <see cref="PostgresLehcesi"/> takılarak gerçek bir PostgreSQL'i sürebildiğini gösterir.
///
/// Ortam-kapılı: 127.0.0.1:5433'te erişilebilir bir Postgres + sqlst_demo (satis.musteri
/// 3 satır, satis.aktif_musteri view) yoksa test ATLANIR — CI/başka makine kırılmaz.
/// Kurulum betiği: scratchpad/seed.sql (taşınabilir binary ile ayağa kaldırılır).
/// </summary>
public class PostgresCanliTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 5433;
    private const string Db = "sqlst_demo";

    private static ConnectionProfile Profil() => new()
    {
        Ad = "pg-demo",
        Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql,   // trust auth — parola yok sayılır
        KullaniciAdi = "postgres",
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
        ILehce lehce = new PostgresLehcesi(new DpapiSecretProtector());
        return (new SqlExecutor(lehce), lehce);
    }

    [Fact]
    public async Task Degismemis_executor_postgres_sorgusunu_calistirir_ve_satirlari_okur()
    {
        if (!Erisilebilir()) return; // ortam-kapılı: Postgres yoksa atla (CI kırılmaz)
        (SqlExecutor executor, _) = Kur();

        QueryResult sonuc = await executor.ExecuteAsync(
            Profil(),
            "SELECT id, ad, bakiye, aktif FROM satis.musteri ORDER BY id;",
            new ExecuteOptions { VeritabaniOverride = Db }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        ResultSetData set = Assert.Single(sonuc.ResultSetler);
        Assert.Equal(["id", "ad", "bakiye", "aktif"], set.Kolonlar.Select(k => k.Ad));
        // Demo veritabanı elle zenginleştirilebildiğinden satır sayısına DEĞİL, çekirdek
        // kayıtların doğru okunmasına bakılır (id 1 ve 2 sabit seed'dir).
        Assert.True(set.Satirlar.Count >= 3, $"en az 3 satır beklenir, gelen: {set.Satirlar.Count}");
        Assert.Equal(1, Convert.ToInt32(set.Satirlar[0][0]));            // id
        Assert.Contains("Ahmet", (string)set.Satirlar[0][1]!);           // ad (Türkçe: "Ahmet Çelik")
        Assert.Equal(DBNull.Value, set.Satirlar[1][2]);                  // 2. müşterinin bakiyesi NULL
        Assert.False((bool)set.Satirlar[1][3]!);                         // aktif = false
    }

    [Fact]
    public async Task Satir_siniri_postgres_akisinda_da_uygulanir()
    {
        if (!Erisilebilir()) return; // ortam-kapılı: Postgres yoksa atla (CI kırılmaz)
        (SqlExecutor executor, _) = Kur();

        // Reader-side cap (motor-nötr): 2 satırda durur, sınır aşıldı bandı işaretlenir.
        QueryResult sonuc = await executor.ExecuteAsync(
            Profil(), "SELECT id FROM satis.musteri ORDER BY id;",
            new ExecuteOptions { SatirSiniri = 2, VeritabaniOverride = Db }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.Equal(2, sonuc.ResultSetler[0].Satirlar.Count);
        Assert.True(sonuc.SatirSiniriAsildi);
    }

    [Fact]
    public async Task Sema_agaci_postgres_katalogundan_yuklenir_pk_ve_tiplerle()
    {
        if (!Erisilebilir()) return; // ortam-kapılı: Postgres yoksa atla (CI kırılmaz)
        (SqlExecutor executor, ILehce lehce) = Kur();
        var schema = new SchemaService(executor, lehce);

        // Veritabanı listesi
        IReadOnlyList<VeritabaniBilgisi> dbler = await schema.VeritabanlariAsync(Profil(), CancellationToken.None);
        Assert.Contains(dbler, d => d.Ad == Db && !d.SistemMi);
        Assert.Contains(dbler, d => d.Ad == "postgres" && d.SistemMi);

        // Şema yükleme (nesneler + kolonlar + PK)
        SemaOnbellegi sema = await schema.YukleAsync(Profil(), Db, CancellationToken.None);

        SemaNesnesi musteri = Assert.Single(sema.Nesneler, n => n.Ad == "musteri");
        Assert.Equal(SemaNesneTuru.Tablo, musteri.Tur);
        Assert.Equal("satis", musteri.Sema);
        Assert.Equal(Db, musteri.Veritabani);
        Assert.Equal(5, musteri.Kolonlar.Count);
        Assert.True(musteri.Kolonlar.Single(k => k.Ad == "id").PkMi);
        Assert.Equal("varchar(50)", musteri.Kolonlar.Single(k => k.Ad == "ad").Tip);
        Assert.True(musteri.Kolonlar.Single(k => k.Ad == "bakiye").NullOlabilir);

        SemaNesnesi view = Assert.Single(sema.Nesneler, n => n.Ad == "aktif_musteri");
        Assert.Equal(SemaNesneTuru.View, view.Tur);
    }

    [Fact]
    public async Task View_tanimi_pg_get_viewdef_ile_okunur()
    {
        if (!Erisilebilir()) return; // ortam-kapılı: Postgres yoksa atla (CI kırılmaz)
        (SqlExecutor executor, ILehce lehce) = Kur();
        var schema = new SchemaService(executor, lehce);

        var view = new SemaNesnesi(Db, "satis", "aktif_musteri", SemaNesneTuru.View, [], []);
        string? tanim = await schema.TanimGetirAsync(Profil(), view, CancellationToken.None);

        Assert.NotNull(tanim);
        Assert.Contains("aktif", tanim!); // WHERE aktif ... görünmeli
    }
}
