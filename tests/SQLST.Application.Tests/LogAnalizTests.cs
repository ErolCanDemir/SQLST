using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>Log analizi SAF çekirdeği (kullanıcı isteği 2026-07-23): ayrıştırma + imza + en çok tekrarlayan.</summary>
public class LogAnalizTests
{
    private static readonly string[] Ornek =
    [
        "2026-07-23 10:00:01.123 +03:00 [ERR] Tablo [Musteri] bulunamadi, id 42",
        "2026-07-23 10:00:02.456 +03:00 [ERR] Tablo [Siparis] bulunamadi, id 7",
        "   System.Exception: yigin izi burada (devam satiri)",   // başlık değil → atlanır
        "2026-07-23 10:00:03.789 +03:00 [INF] Sorgu calisti 15 ms",
        "2026-07-23 10:05:00.000 +03:00 [ERR] Tablo [Urun] bulunamadi, id 99",
        "2026-07-23 10:06:00.000 +03:00 [WRN] Yavas sorgu 1200 ms",
    ];

    [Fact]
    public void Ayristir_baslik_satirlarini_okur_devami_atlar()
    {
        IReadOnlyList<LogKaydi> k = LogAnaliz.Ayristir(Ornek);

        Assert.Equal(5, k.Count); // 6 satır, 1'i devam → 5 kayıt
        Assert.Equal("ERR", k[0].Seviye);
        Assert.Equal("Tablo [Musteri] bulunamadi, id 42", k[0].Mesaj);
        Assert.Equal("2026-07-23 10:00:01", k[0].Zaman);
    }

    [Fact]
    public void Imza_degisken_kisimlari_eler()
    {
        Assert.Equal(
            LogAnaliz.Imza("Tablo [Musteri] bulunamadi, id 42"),
            LogAnaliz.Imza("Tablo [Siparis] bulunamadi, id 7")); // farklı tablo/id → AYNI imza

        Assert.Contains("#", LogAnaliz.Imza("id 42"), StringComparison.Ordinal);
        Assert.Equal("<id>", LogAnaliz.Imza("6f9619ff-8b86-d011-b42d-00cf4fc964ff"));
    }

    [Fact]
    public void EnCokTekrarlayan_hatalari_gruplar_ve_siralar()
    {
        IReadOnlyList<LogKaydi> k = LogAnaliz.Ayristir(Ornek);
        IReadOnlyList<LogGrup> g = LogAnaliz.EnCokTekrarlayan(k, yalnizHata: true);

        // 3 "Tablo <x> bulunamadi, id #" ERR aynı gruba düşer; INF/WRN hariç
        LogGrup enUst = g[0];
        Assert.Equal(3, enUst.Sayi);
        Assert.Equal("ERR", enUst.Seviye);
        Assert.Equal("2026-07-23 10:05:00", enUst.SonZaman); // en yeni görülme
        Assert.DoesNotContain(g, x => x.Seviye is "INF" or "WRN");
    }

    [Fact]
    public void EnCokTekrarlayan_yalnizHata_false_hepsini_alir()
    {
        IReadOnlyList<LogKaydi> k = LogAnaliz.Ayristir(Ornek);
        IReadOnlyList<LogGrup> g = LogAnaliz.EnCokTekrarlayan(k, yalnizHata: false);

        Assert.Contains(g, x => x.Seviye == "INF");
        Assert.Contains(g, x => x.Seviye == "WRN");
    }

    // ── v20-S3: LIKE/regex deseni (gerçek-sayım/detay/trend anahtarı) ──────────────────────────

    [Fact]
    public void LikeDeseni_ayni_imzadan_ayni_kalibi_uretir()
    {
        // İmza ile aynı mantık: değişken span'lar tek '%' olur; iki farklı örnek AYNI kalıba iner.
        Assert.Equal(
            LogAnaliz.LikeDeseni("Tablo [Musteri] bulunamadi, id 42"),
            LogAnaliz.LikeDeseni("Tablo [Siparis] bulunamadi, id 7"));
        Assert.Equal("Tablo % bulunamadi, id %",
            LogAnaliz.LikeDeseni("Tablo [Musteri] bulunamadi, id 42"));
    }

    [Fact]
    public void LikeDeseni_meta_karakterleri_kacis_ile_korur()
    {
        // Gerçek '%' ve '_' JOKER DEĞİL, veridir → '!' ile kaçırılır (ESCAPE '!'); yer tutucu '%' olur.
        Assert.Equal("tam!% oran!_i", LogAnaliz.LikeDeseni("tam% oran_i"));
        // Kaçış karakterinin kendisi de ikizlenir.
        Assert.Contains("!!", LogAnaliz.LikeDeseni("dikkat! bitti"), StringComparison.Ordinal);
    }

    [Fact]
    public void RegexDeseni_sabiti_kacirir_degiskeni_yildizlar()
    {
        Assert.Equal("Tablo .* bulunamadi, id .*",
            LogAnaliz.RegexDeseni("Tablo [Musteri] bulunamadi, id 42"));
        // Regex meta-karakterleri sabit metinde kaçırılır (yer tutucu harf olduğundan etkilenmez).
        string r = LogAnaliz.RegexDeseni("Deger (x) + [y]");
        Assert.Contains(@"\(x\)", r, StringComparison.Ordinal);
        Assert.Contains(@"\+", r, StringComparison.Ordinal);
    }
}
