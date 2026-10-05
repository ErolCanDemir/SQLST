using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

public class YerelDepoTests : IDisposable
{
    private readonly string _dosya = Path.Combine(Path.GetTempPath(), $"sqlst-{Guid.NewGuid():N}.db");

    private YerelDepo YeniDepo() => new(_dosya);

    [Fact]
    public void Bootstrap_semayi_ve_ayar_tablosunu_kurar()
    {
        var depo = YeniDepo();
        using var baglanti = depo.BaglantiAc();
        using var komut = baglanti.CreateCommand();
        komut.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        var tablolar = new List<string>();
        using var oku = komut.ExecuteReader();
        while (oku.Read()) tablolar.Add(oku.GetString(0));

        Assert.Contains("Ayar", tablolar);
        Assert.Contains("SemaSurum", tablolar);
        Assert.Contains("SorguGecmisi", tablolar);
        Assert.Contains("OturumSekmesi", tablolar);
        Assert.Contains("NesneTarihcesi", tablolar);
    }

    [Fact]
    public void Ikinci_bootstrap_gocleri_tekrar_uygulamaz()
    {
        _ = YeniDepo();
        _ = YeniDepo(); // aynı dosya, ikinci kez — hata vermemeli, sürüm artmamalı

        var depo = YeniDepo();
        using var baglanti = depo.BaglantiAc();
        using var komut = baglanti.CreateCommand();
        komut.CommandText = "SELECT MAX(Surum), COUNT(*) FROM SemaSurum;";
        using var oku = komut.ExecuteReader();
        oku.Read();
        // Göç sayısı: 3 (v2) + ProfilId göçleri: geçmiş, oturum, ayar, nesne tarihçesi = 7
        // + 8: Snippet tablosu ve yerleşik kalıplar (V5-S4) + 9: denetim Kullanici sütunu (v10-S2)
        // + 10: geçmişte SekmeAdi (2026-07-23) + 11: SOAP geçmişi ve ortamları (v14-S3)
        // + 12: Geri Al paketi (V15-S3, BF-1) + 13: SOAP ortam Basic auth kolonları (v16)
        // + 14: REST İstemcisi geçmiş/ortam/kayıtlı istek tabloları (v20-S8)
        Assert.Equal(14, oku.GetInt32(0));
        Assert.Equal(14, oku.GetInt32(1)); // her göç tek kayıt (tekrar eklenmedi)
    }

    [Fact]
    public async Task Ayar_yaz_oku_ve_ustune_yaz()
    {
        var depo = YeniDepo();
        IAyarDeposu ayar = new SqliteAyarDeposu(depo);

        Assert.Null(await ayar.OkuAsync("yok"));

        await ayar.YazAsync(AyarAnahtari.KomutTimeoutSn, "30");
        Assert.Equal("30", await ayar.OkuAsync(AyarAnahtari.KomutTimeoutSn));

        await ayar.YazAsync(AyarAnahtari.KomutTimeoutSn, "0"); // upsert
        Assert.Equal("0", await ayar.OkuAsync(AyarAnahtari.KomutTimeoutSn));
    }

    [Fact]
    public async Task Tipli_okuma_varsayilana_duser_ve_invariant_ayristirir()
    {
        var depo = YeniDepo();
        IAyarDeposu ayar = new SqliteAyarDeposu(depo);

        Assert.Equal(120, await ayar.IntOkuAsync("yok", 120));
        Assert.True(await ayar.BoolOkuAsync("yok", varsayilan: true));

        await ayar.YazAsync("sayi", "45");
        Assert.Equal(45, await ayar.IntOkuAsync("sayi", 0));

        await ayar.YazAsync("bayrak", "1");
        Assert.True(await ayar.BoolOkuAsync("bayrak", false));

        await ayar.YazAsync("bozuk", "abc");
        Assert.Equal(7, await ayar.IntOkuAsync("bozuk", 7)); // parse edilemez → varsayılan
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string ek in new[] { "", "-wal", "-shm" })
        {
            string y = _dosya + ek;
            if (File.Exists(y)) File.Delete(y);
        }
    }
}
