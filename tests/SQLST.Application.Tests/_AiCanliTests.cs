using System.Diagnostics;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;
using Xunit.Abstractions;

namespace SQLST.Application.Tests;

/// <summary>
/// 🤖 Yerel AI CANLI uçtan-uca testi (v21-S3 doğrulama, kullanıcı isteği 2026-08-08:
/// "uygulama üzerinden her şeyi test et, cevap veriyor mu, doğru cevap veriyor mu").
/// Uygulamanın GERÇEK hattını kullanır: AsistanIstemleri (ViewModel'in çağırdığı istem kurucuları)
/// → AsistanYonlendirici.SorAsync (gerçek HTTP → Ollama) → CevaptanSorguAyikla → SqlDogrulayici.
/// Yalnız SQLST_AI=1 ile ve Ollama ayaktayken koşar; normal süitte ATLANIR (ağ/model ister).
/// </summary>
public class _AiCanliTests(ITestOutputHelper cikti)
{
    private static bool Etkin => Environment.GetEnvironmentVariable("SQLST_AI") == "1";

    private static readonly AsistanAyarlari Yerel =
        new(AsistanSaglayici.Yerel, "qwen2.5-coder:7b", "http://127.0.0.1:11434", null);

    private static AsistanYonlendirici Servis() => new(new DpapiSecretProtector());

    // Gerçekçi mini şema (AsistanIstemleri.SemaOzeti biçimiyle)
    private const string Sema = """
        dbo.Musteri (Id int PK, Ad nvarchar(100), Sehir nvarchar(50), Aktif bit)
        dbo.Siparis (Id int PK, MusteriId int, Tutar decimal(18,2), Tarih datetime)
        dbo.Urun (Id int PK, Ad nvarchar(100), Fiyat decimal(18,2))
        """;

    private async Task<(bool Yanit, string Metin, long Ms)> SorAsync(string istem)
    {
        var sw = Stopwatch.StartNew();
        AsistanCevabi c = await Servis().SorAsync(istem, Yerel, CancellationToken.None);
        sw.Stop();
        cikti.WriteLine($"[{sw.ElapsedMilliseconds} ms] başarılı={c.Basarili}");
        cikti.WriteLine(c.Metin);
        cikti.WriteLine(new string('-', 60));
        return (c.Basarili && c.Metin.Length > 0, c.Metin, sw.ElapsedMilliseconds);
    }

    /// <summary>SQL üreten özelliklerde: cevap geldi mi + çıkan SQL GEÇERLİ T-SQL mi (uygulama hattı).</summary>
    private void SqlDogrula(string cevap)
    {
        string? sql = AsistanIstemleri.CevaptanSorguAyikla(cevap);
        cikti.WriteLine($"ÇIKARILAN SQL: {sql ?? "(yok)"}");
        Assert.NotNull(sql);
        SqlDogrulamaSonucu d = SqlDogrulayici.Dogrula(sql, MotorTuru.Mssql);
        cikti.WriteLine($"DOĞRULAMA: doğrulandı={d.Dogrulandi} geçerli={d.Gecerli} hata={d.Hata}");
        Assert.True(d.Gecerli, $"Model geçersiz T-SQL üretti: {d.Hata}\nSQL: {sql}");
    }

    [Fact]
    public async Task A1_Serbest_soru_sql_uretir_ve_gecerli()
    {
        if (!Etkin) return;
        cikti.WriteLine("=== A1: Serbest soru → SQL üretimi ===");
        string istem = AsistanIstemleri.SerbestSoru(
            "En çok sipariş tutarına sahip 5 müşteriyi ad ve toplam tutarla getir",
            Sema, AsistanIstemleri.MotorAdi(MotorTuru.Mssql), null, MotorTuru.Mssql);
        var (yanit, metin, _) = await SorAsync(istem);
        Assert.True(yanit, "Model cevap vermedi");
        SqlDogrula(metin);
    }

    [Fact]
    public async Task A2_Hata_cozdur_aciklama_ve_gecerli_duzeltme()
    {
        if (!Etkin) return;
        cikti.WriteLine("=== A2: Hata çözdür ===");
        string istem = AsistanIstemleri.HataCozdur(
            "SELECT Ad, SUM(s.Tutar) FROM dbo.Musteri m JOIN dbo.Siparis s ON s.MusteriId = m.Id",
            "Msg 8120: Column 'dbo.Musteri.Ad' is invalid in the select list because it is not contained in either an aggregate function or the GROUP BY clause.",
            Sema, AsistanIstemleri.MotorAdi(MotorTuru.Mssql), MotorTuru.Mssql);
        var (yanit, metin, _) = await SorAsync(istem);
        Assert.True(yanit, "Model cevap vermedi");
        SqlDogrula(metin); // düzeltilmiş sorgu geçerli T-SQL olmalı
    }

    [Fact]
    public async Task A3_Sorgu_degerlendir_yanit_verir()
    {
        if (!Etkin) return;
        cikti.WriteLine("=== A3: Sorgu değerlendir ===");
        string istem = AsistanIstemleri.SorguDegerlendir(
            "SELECT * FROM dbo.Musteri m JOIN dbo.Siparis s",  // eksik ON — değerlendirmeli
            Sema, AsistanIstemleri.MotorAdi(MotorTuru.Mssql), MotorTuru.Mssql);
        var (yanit, metin, _) = await SorAsync(istem);
        Assert.True(yanit, "Model cevap vermedi");
        // İçerik kontrolü: JOIN/ON kavramına değinmeli (eksik koşulu görmeli)
        Assert.True(metin.Contains("ON", StringComparison.OrdinalIgnoreCase)
            || metin.Contains("JOIN", StringComparison.OrdinalIgnoreCase),
            "Değerlendirme eksik JOIN koşuluna değinmedi");
    }

    [Fact]
    public async Task A4_Sorgu_acikla_yanit_verir()
    {
        if (!Etkin) return;
        cikti.WriteLine("=== A4: Sorgu açıkla ===");
        string istem = AsistanIstemleri.SorguAcikla(
            "SELECT m.Sehir, COUNT(*) FROM dbo.Musteri m WHERE m.Aktif = 1 GROUP BY m.Sehir",
            Sema, AsistanIstemleri.MotorAdi(MotorTuru.Mssql));
        var (yanit, metin, _) = await SorAsync(istem);
        Assert.True(yanit, "Model cevap vermedi");
        Assert.True(metin.Length > 40, "Açıklama çok kısa / boş");
    }

    [Fact]
    public async Task A5_Log_ozetle_yanit_verir()
    {
        if (!Etkin) return;
        cikti.WriteLine("=== A5: Log özetle ===");
        string istem = AsistanIstemleri.LogOzetle(
            "312× Timeout expired waiting for connection\n88× Deadlock victim: process\n5× Login failed for user");
        var (yanit, metin, _) = await SorAsync(istem);
        Assert.True(yanit, "Model cevap vermedi");
        Assert.True(metin.Length > 40, "Özet çok kısa / boş");
    }

    [Fact]
    public async Task A6_Duzeltme_turu_bozuk_sqli_gecerliye_cevirir()
    {
        if (!Etkin) return;
        cikti.WriteLine("=== A6: Düzeltme turu (S2 kapısı) — bilerek bozuk T-SQL ===");
        // POC'de 7B'nin ürettiği geçersiz kalıp: TOP sonda. Düzeltme turu düzeltmeli.
        const string bozuk = "SELECT Ad FROM dbo.Musteri ORDER BY Ad TOP 5";
        Assert.False(SqlDogrulayici.Dogrula(bozuk, MotorTuru.Mssql).Gecerli); // girdi gerçekten bozuk

        string istem = AsistanIstemleri.SqlDuzeltmeTuru(
            bozuk, "Satır 1: 'TOP' yakınında yanlış söz dizimi", MotorTuru.Mssql);
        var (yanit, metin, _) = await SorAsync(istem);
        Assert.True(yanit, "Model cevap vermedi");
        SqlDogrula(metin); // düzeltilmiş SQL geçerli T-SQL olmalı
    }
}
