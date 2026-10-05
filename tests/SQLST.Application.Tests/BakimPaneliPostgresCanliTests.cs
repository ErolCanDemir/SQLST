using System.Net.Sockets;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V5-S3 · PostgreSQL bakım bölümlerinin CANLI doğrulaması. Ortam-kapılı.
///
/// Bu testler <b>şekli değil, koştuğunu</b> kanıtlamak içindir: panel bölümleri düz metin
/// SQL'dir, derleyici bunları görmez. V5-S2'de <c>pg_get_functiondef</c>'in aggregate'lerde
/// patlaması yalnız gerçek sunucuda ortaya çıkmıştı — aynı sınıf hatalar için tek korumamız bu.
/// </summary>
public class BakimPaneliPostgresCanliTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 5433;

    private static ConnectionProfile Profil() => new()
    {
        Ad = "pg-bakim", Motor = MotorTuru.Postgres, Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql, KullaniciAdi = "postgres", BaglantiTimeoutSn = 10,
    };

    private static readonly ExecuteOptions Demo = new() { VeritabaniOverride = "sqlst_demo" };

    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// TÜM bölümler gerçek sunucuda koşar. Tek tek değil topluca: bir bölüm bile sözdizimi
    /// ya da sürüm uyumsuzluğu yüzünden patlarsa panelde o bölüm hata bandıyla gelir.
    /// </summary>
    [Fact]
    public async Task Tum_teshis_bolumleri_gercek_sunucuda_calisir()
    {
        if (!Erisilebilir())
            return;

        var lehce = new PostgresLehcesi(new DpapiSecretProtector());
        var executor = new SqlExecutor(lehce);

        Assert.NotEmpty(lehce.TeshisBolumleri);

        foreach (TeshisBolumu bolum in lehce.TeshisBolumleri)
        {
            QueryResult r = await executor.ExecuteAsync(
                Profil(), bolum.Sorgu, Demo, CancellationToken.None);

            Assert.True(r.Basarili, $"'{bolum.Baslik}' bölümü düştü: {r.Hata?.Mesaj}");
            Assert.NotEmpty(r.ResultSetler);
        }
    }

    /// <summary>
    /// V5-S3 bölümlerinin gerçekten EKLENDİĞİNİ sabitler — bölüm listesi sessizce
    /// eksilirse (ör. birleştirme kazası) test düşer.
    /// </summary>
    [Fact]
    public void V5_S3_bolumleri_bildirilir()
    {
        var lehce = new PostgresLehcesi(new DpapiSecretProtector());
        IReadOnlyList<string> basliklar = [.. lehce.TeshisBolumleri.Select(b => b.Baslik)];

        Assert.Contains(basliklar, b => b.Contains("Bloklama zinciri", StringComparison.Ordinal));
        Assert.Contains(basliklar, b => b.Contains("İstatistik tazeliği", StringComparison.Ordinal));
    }

    /// <summary>
    /// 2026-07-19 sadeleştirmesi — PG panelinden "Kilit çakışmaları" kaldırıldı: bekleyenleri
    /// gösteriyor ama KÖK bloklayanı söylemiyordu, yani "Bloklama zinciri"nin eksik bir
    /// kopyasıydı. (MSSQL'de "Bloklanan oturumlar" aynı gerekçeyle silinmişti.) Bu test
    /// bölümün sessizce geri gelmesini engeller.
    /// </summary>
    [Fact]
    public void Sadelestirme_KORUNUR_kilit_cakismalari_geri_gelmez()
    {
        var lehce = new PostgresLehcesi(new DpapiSecretProtector());
        IReadOnlyList<string> basliklar = [.. lehce.TeshisBolumleri.Select(b => b.Baslik)];

        Assert.DoesNotContain(basliklar, b => b.Contains("Kilit çakışmaları", StringComparison.Ordinal));
        Assert.Equal(6, basliklar.Count);

        // ANALYZE tazeliği tek adreste kalmalı: "Tablo boyutları"nda last_analyze olmamalı,
        // VACUUM tarihleri "İstatistik tazeliği"ne sızmamalı.
        TeshisBolumu boyutlar = lehce.TeshisBolumleri.Single(b => b.Baslik.StartsWith("Tablo boyutları", StringComparison.Ordinal));
        TeshisBolumu istatistik = lehce.TeshisBolumleri.Single(b => b.Baslik == "İstatistik tazeliği");

        Assert.DoesNotContain("last_analyze", boyutlar.Sorgu, StringComparison.Ordinal);
        Assert.Contains("last_vacuum", boyutlar.Sorgu, StringComparison.Ordinal);
        Assert.Contains("last_analyze", istatistik.Sorgu, StringComparison.Ordinal);
        Assert.DoesNotContain("last_vacuum", istatistik.Sorgu, StringComparison.Ordinal);
    }
}
