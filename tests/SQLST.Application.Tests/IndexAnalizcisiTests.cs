using SQLST.Application;

namespace SQLST.Application.Tests;

public class IndexAnalizcisiTests
{
    private static EksikIndexOnerisi Oneri(
        string tablo = "[dbo].[Musteri]", string? esitlik = "[A], [B]",
        string? esitsizlik = null, string? include = null, double skor = 1_000_000)
        => new("db", tablo, esitlik, esitsizlik, include, skor, 100,
            new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc), null);

    [Theory]
    [InlineData(100, OneriGucu.Dusuk)]
    [InlineData(499_999, OneriGucu.Dusuk)]
    [InlineData(500_000, OneriGucu.Orta)]
    [InlineData(5_000_000, OneriGucu.Yuksek)]
    public void Skor_kovalari_blitzindex_esigi(double skor, OneriGucu beklenen)
        => Assert.Equal(beklenen, IndexAnalizcisi.SkorKovasi(skor));

    [Fact]
    public void Eski_kullanim_soluklasir()
    {
        var simdi = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(IndexAnalizcisi.SolukMu(simdi.AddDays(-31), simdi));
        Assert.False(IndexAnalizcisi.SolukMu(simdi.AddDays(-1), simdi));
        Assert.True(IndexAnalizcisi.SolukMu(null, simdi));
    }

    [Fact]
    public void Kolon_ayirici_dmv_bicimini_cozer()
    {
        EksikIndexOnerisi o = Oneri(esitlik: "[Ad], [Soyad]", esitsizlik: "[Tarih]", include: "[Tutar]");
        Assert.Equal(["Ad", "Soyad", "Tarih"], o.AnahtarKolonlar);
        Assert.Equal(["Tutar"], o.IncludeKolonlar);
    }

    // ---- Örtüşme analizi (07-r2 §6) ----

    private static MevcutIndex Mevcut(string tablo, string ad, string[] anahtar, string[]? include = null,
        bool unique = false, bool pk = false)
        => new(tablo, ad, unique, pk, anahtar, include ?? []);

    [Fact]
    public void Ayni_onek_ve_kapsam_zaten_var_der()
    {
        OrtusmeSonucu s = IndexAnalizcisi.OrtusmeBul(
            Oneri(esitlik: "[A], [B]"),
            [Mevcut("[dbo].[Musteri]", "IX_Var", ["A", "B", "C"])]);

        Assert.NotNull(s.Not);
        Assert.Contains("zaten kapsanıyor", s.Not);
        Assert.Contains("IX_Var", s.Not);
    }

    [Fact]
    public void Anahtar_uyuyor_include_eksikse_genisletme_onerir()
    {
        OrtusmeSonucu s = IndexAnalizcisi.OrtusmeBul(
            Oneri(esitlik: "[A]", include: "[X], [Y]"),
            [Mevcut("[dbo].[Musteri]", "IX_Dar", ["A"], ["X"])]);

        Assert.Contains("INCLUDE ekleyin", s.Not);
        Assert.Contains("Y", s.Not);
        Assert.DoesNotContain("X,", s.Not); // X zaten kapsanıyor — yalnız eksik listelenir
    }

    [Fact]
    public void Oncu_kolon_farkliysa_ortusme_yok()
    {
        // (A,B) ≠ (B,A): öncü kolon seek yeteneğini belirler (07-r2 §6)
        OrtusmeSonucu s = IndexAnalizcisi.OrtusmeBul(
            Oneri(esitlik: "[A], [B]"),
            [Mevcut("[dbo].[Musteri]", "IX_Ters", ["B", "A"])]);

        Assert.Null(s.Not);
    }

    [Fact]
    public void Baska_tablonun_indexi_karismaz()
    {
        OrtusmeSonucu s = IndexAnalizcisi.OrtusmeBul(
            Oneri(tablo: "[dbo].[Musteri]", esitlik: "[A]"),
            [Mevcut("[dbo].[Siparis]", "IX_Ayni", ["A"])]);

        Assert.Null(s.Not);
    }

    [Fact]
    public void Kismi_onek_uyusmazligi_otomatik_birlestirilmez()
    {
        // öneri (A,B,C) — mevcut (A,B): mevcut daha KISA, kapsayamaz → not yok (insana bırak)
        OrtusmeSonucu s = IndexAnalizcisi.OrtusmeBul(
            Oneri(esitlik: "[A], [B], [C]"),
            [Mevcut("[dbo].[Musteri]", "IX_Kisa", ["A", "B"])]);

        Assert.Null(s.Not);
    }

    // ---- Script üretimi (R1.1 kural 3) ----

    [Fact]
    public void Create_scripti_zorunlu_uyarilarla_gelir()
    {
        string script = IndexAnalizcisi.CreateIndexScripti(
            Oneri(esitlik: "[Ad]", esitsizlik: "[Tarih]", include: "[Tutar]"),
            new DateTime(2026, 7, 10, 8, 0, 0));

        Assert.Contains("İNCELEMEDEN ÇALIŞTIRMAYIN", script);
        Assert.Contains("Kolon SIRASI DMV'den gelmez", script);
        Assert.Contains("obez index", script);
        Assert.Contains("10.07.2026", script); // uptime başlangıcı
        Assert.Contains("CREATE NONCLUSTERED INDEX", script);
        Assert.Contains("ON [dbo].[Musteri] ([Ad], [Tarih])", script);
        Assert.Contains("INCLUDE ([Tutar])", script);
        Assert.Contains("USE [db];", script);
    }

    [Fact]
    public void Disable_scripti_sil_degil_disable()
    {
        string script = IndexAnalizcisi.DisableScripti("[dbo].[Musteri]", "IX_Olu");

        Assert.Contains("ALTER INDEX [IX_Olu] ON [dbo].[Musteri] DISABLE;", script);
        Assert.Contains("SİLMEYİN", script);
        Assert.Contains("REBUILD", script);
        Assert.DoesNotContain("DROP INDEX", script);
    }
}
