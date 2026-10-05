using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Salt-okunur profil korumasının GERÇEKTEN yazmayı engellediğini canlı PostgreSQL'e karşı
/// doğrular (kullanıcı 2026-07-18 "çalışmıyor" dedi — kaldırmadan önce kesin kanıt).
/// Ortam-kapılı: 127.0.0.1:5433 yoksa atlanır.
/// </summary>
public class SaltOkunurCanliTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 5433;
    private const string Db = "sqlst_demo";

    private static ConnectionProfile Profil(bool saltOkunur) => new()
    {
        Ad = "pg-salt-okunur",
        Motor = MotorTuru.Postgres,
        Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql,
        KullaniciAdi = "postgres",
        SaltOkunur = saltOkunur,
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

    [Fact]
    public async Task Salt_okunur_profilde_yazma_gonderilmez_okuma_calisir()
    {
        if (!Erisilebilir()) return; // ortam-kapılı

        ILehce lehce = new PostgresLehcesi(new DpapiSecretProtector());
        var fabrika = new OturumFabrikasi(lehce);
        var servis = new QueryService();
        var secenek = new ExecuteOptions { VeritabaniOverride = Db };

        await using IDbOturum korumali = fabrika.Olustur(Profil(saltOkunur: true));

        // 1) Yazma ENGELLENMELİ — sunucuya hiç gitmemeli
        QueryResult yazma = await servis.RunAsync(
            korumali, "DELETE FROM satis.siparis WHERE 1=0;", secenek, CancellationToken.None);
        Assert.False(yazma.Basarili);
        Assert.Contains("salt-okunur", yazma.Hata!.Mesaj);

        // 2) Okuma AYNI profilde çalışmalı (koruma okumayı engellemez)
        QueryResult okuma = await servis.RunAsync(
            korumali, "SELECT count(*) FROM satis.musteri;", secenek, CancellationToken.None);
        Assert.True(okuma.Basarili, okuma.Hata?.Mesaj);
        Assert.Equal(5, Convert.ToInt32(okuma.ResultSetler[0].Satirlar[0][0]));
    }

    [Fact]
    public async Task Salt_okunur_kapaliyken_yazma_gecer()
    {
        if (!Erisilebilir()) return; // ortam-kapılı

        ILehce lehce = new PostgresLehcesi(new DpapiSecretProtector());
        await using IDbOturum serbest = new OturumFabrikasi(lehce).Olustur(Profil(saltOkunur: false));

        // WHERE 1=0 → hiçbir satıra dokunmaz ama yazma yolundan geçer
        QueryResult sonuc = await new QueryService().RunAsync(
            serbest, "DELETE FROM satis.siparis WHERE 1=0;",
            new ExecuteOptions { VeritabaniOverride = Db }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
    }
}
