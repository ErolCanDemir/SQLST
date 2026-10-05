using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>v8 — Mongo "find yardımcısı": alanlardan find JSON üretimi + doğrulama.</summary>
public class MongoBulYaziciTests
{
    [Fact]
    public void Sadece_koleksiyon_temel_find()
    {
        (string? json, string? hata) = MongoBulYazici.Uret(new MongoBulIstegi("firmalar"));

        Assert.Null(hata);
        Assert.Contains("\"find\": \"firmalar\"", json!, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_sort_limit_skip_birlesir()
    {
        (string? json, string? hata) = MongoBulYazici.Uret(new MongoBulIstegi(
            "firmalar", Filtre: "{ \"aktif\": true }", Sirala: "{ \"ad\": 1 }", Limit: 50, Atla: 10));

        Assert.Null(hata);
        Assert.Contains("\"filter\"", json!, StringComparison.Ordinal);
        Assert.Contains("\"aktif\": true", json!, StringComparison.Ordinal);
        Assert.Contains("\"sort\"", json!, StringComparison.Ordinal);
        Assert.Contains("\"skip\": 10", json!, StringComparison.Ordinal);
        Assert.Contains("\"limit\": 50", json!, StringComparison.Ordinal);
    }

    [Fact]
    public void Kutu_guzellestirme_sarar_bicimler_ve_bozugu_yakalar()
    {
        // v12-S6 (kullanıcı isteği: "sorttaki json işini tüm alanlara"): çıplak çift sarılır +
        // girintilenir; geçerli nesne yalnız biçimlenir; bozuk metin Üret beklenmeden hata verir.
        (string? sarili, string? hata1) = MongoBulYazici.KutuGuzellestir("\"KullaniciId\": true");
        Assert.Null(hata1);
        Assert.StartsWith("{", sarili!, StringComparison.Ordinal);
        Assert.Contains("\"KullaniciId\": true", sarili!, StringComparison.Ordinal);

        (string? bicimli, string? hata2) = MongoBulYazici.KutuGuzellestir("{\"_id\":-1}");
        Assert.Null(hata2);
        Assert.Contains("\n", bicimli!, StringComparison.Ordinal); // girintili çok satır

        (string? bos, string? hata3) = MongoBulYazici.KutuGuzellestir("   ");
        Assert.Null(bos);
        Assert.Null(hata3); // boş kutu geçerli — isteğe bağlı alan

        (string? yok, string? hata4) = MongoBulYazici.KutuGuzellestir("\"KullaniciId\": : true");
        Assert.Null(yok);
        Assert.NotNull(hata4); // çift iki nokta — kullanıcının canlı yakaladığı hata
    }

    [Fact]
    public void Koleksiyon_bossa_hata()
    {
        (string? json, string? hata) = MongoBulYazici.Uret(new MongoBulIstegi("  "));
        Assert.Null(json);
        Assert.Contains("Koleksiyon", hata!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bozuk_filter_JSONu_hangi_alan_soylenir()
    {
        (string? json, string? hata) = MongoBulYazici.Uret(new MongoBulIstegi("k", Filtre: "{ aktif: }"));
        Assert.Null(json);
        Assert.Contains("filter", hata!, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_nesne_degilse_hata()
    {
        (string? json, string? hata) = MongoBulYazici.Uret(new MongoBulIstegi("k", Filtre: "[1,2,3]"));
        Assert.Null(json);
        Assert.Contains("filter", hata!, StringComparison.Ordinal);
    }

    [Fact]
    public void Suslu_parantezsiz_giris_otomatik_sarilir()
    {
        // Kullanıcı bulgusu 2026-07-25: otomatik doldurma "_id": -1 üretir (parantezsiz);
        // Üret bunu { "_id": -1 } olarak sarar — süslü parantez kullanıcıya yazdırılmaz.
        (string? json, string? hata) = MongoBulYazici.Uret(new MongoBulIstegi(
            "ExceptionLog", Sirala: "\"_id\": -1", Yansit: "\"Mesaj\": 1, \"_id\": 0", Limit: 200));

        Assert.Null(hata);
        Assert.Contains("\"sort\"", json!, StringComparison.Ordinal);
        (MongoBulIstegi? geri, _) = MongoBulYazici.Ayristir(json);
        Assert.Contains("\"_id\": -1", geri!.Sirala!, StringComparison.Ordinal);
        Assert.Contains("\"Mesaj\": 1", geri.Yansit!, StringComparison.Ordinal);
    }

    [Fact]
    public void Negatif_limit_hata()
    {
        (_, string? hata) = MongoBulYazici.Uret(new MongoBulIstegi("k", Limit: -1));
        Assert.Contains("Limit", hata!, StringComparison.Ordinal);
    }

    // ── Geri-ayrıştırma (JSON → alanlar) — borç kapanışı ─────────────────────

    [Fact]
    public void Ayristir_find_belgesini_alanlara_coz()
    {
        const string json = """
            { "find": "firmalar", "filter": { "aktif": true }, "sort": { "ad": 1 },
              "projection": { "ad": 1, "_id": 0 }, "skip": 10, "limit": 50 }
            """;

        (MongoBulIstegi? istek, string? hata) = MongoBulYazici.Ayristir(json);

        Assert.Null(hata);
        Assert.Equal("firmalar", istek!.Koleksiyon);
        Assert.Contains("\"aktif\": true", istek.Filtre!, StringComparison.Ordinal);
        Assert.Contains("\"ad\": 1", istek.Sirala!, StringComparison.Ordinal);
        Assert.Contains("\"_id\": 0", istek.Yansit!, StringComparison.Ordinal);
        Assert.Equal(10, istek.Atla);
        Assert.Equal(50, istek.Limit);
    }

    [Fact]
    public void Ayristir_uret_ile_tur_atar_gider_gelir() // round-trip: Uret → Ayristir aynı alanlar
    {
        var ozgun = new MongoBulIstegi("k", Filtre: "{ \"x\": 1 }", Limit: 5);
        (string? json, _) = MongoBulYazici.Uret(ozgun);

        (MongoBulIstegi? geri, string? hata) = MongoBulYazici.Ayristir(json);

        Assert.Null(hata);
        Assert.Equal("k", geri!.Koleksiyon);
        Assert.Equal(5, geri.Limit);
        Assert.Contains("\"x\": 1", geri.Filtre!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "Boş")]
    [InlineData("{ bozuk }", "JSON")]
    [InlineData("[1,2,3]", "nesne")]
    [InlineData("{ \"filter\": {} }", "find")] // find yok
    public void Ayristir_gecersizde_neden_doner(string json, string nedenParcasi)
    {
        (MongoBulIstegi? istek, string? hata) = MongoBulYazici.Ayristir(json);
        Assert.Null(istek);
        Assert.Contains(nedenParcasi, hata!, StringComparison.OrdinalIgnoreCase);
    }

    // ── v19-S14 (canlı test 2026-08-04): onarım — kullanıcı yalnız DEĞERLERİ girsin ──

    [Fact]
    public void KutuGuzellestir_kapanmamis_susluleri_dengeler()
    {
        // Tamamlamanın bıraktığı hâl: "KullaniciId": { "$ne": -666  (kapanışlar YOK)
        (string? guzel, string? hata) = MongoBulYazici.KutuGuzellestir("\"KullaniciId\": { \"$ne\": -666");
        Assert.Null(hata);
        Assert.Contains("\"$ne\": -666", guzel!, StringComparison.Ordinal);
        Assert.Equal(guzel!.Count(c => c == '{'), guzel.Count(c => c == '}')); // dengelendi
    }

    [Fact]
    public void KutuGuzellestir_tirnaksiz_anahtarlari_tirnaklar()
    {
        // Compass alışkanlığı: KullaniciId: { $ne: -666 } — anahtarlar çıplak.
        (string? guzel, string? hata) = MongoBulYazici.KutuGuzellestir("KullaniciId: { $ne: -666 }");
        Assert.Null(hata);
        Assert.Contains("\"KullaniciId\"", guzel!, StringComparison.Ordinal);
        Assert.Contains("\"$ne\"", guzel!, StringComparison.Ordinal);
    }

    [Fact]
    public void KutuGuzellestir_deger_sabitlerine_dokunmaz()
    {
        // true/false/null DEĞERDİR (':' izlemez) — tırnaklanmaz; dize içi ':' anahtar sanılmaz.
        (string? guzel, string? hata) = MongoBulYazici.KutuGuzellestir("aktif: true, not: \"x: y\"");
        Assert.Null(hata);
        Assert.Contains("\"aktif\": true", guzel!, StringComparison.Ordinal);
        Assert.Contains("\"x: y\"", guzel!, StringComparison.Ordinal);
    }

    [Fact]
    public void Uret_yarim_filtreyle_gecerli_json_uretir()
    {
        // Uret de aynı onarımdan geçer: yarım filtre → geçerli find belgesi.
        (string? json, string? hata) = MongoBulYazici.Uret(
            new MongoBulIstegi("ExceptionLog", Filtre: "KullaniciId: { $ne: -666"));
        Assert.Null(hata);
        (MongoBulIstegi? geri, string? ayristirmaHatasi) = MongoBulYazici.Ayristir(json);
        Assert.Null(ayristirmaHatasi);
        Assert.Contains("\"$ne\": -666", geri!.Filtre!, StringComparison.Ordinal);
    }

    [Fact]
    public void Onarim_gecersiz_metni_kurtaramazsa_neden_korunur()
    {
        (string? guzel, string? hata) = MongoBulYazici.KutuGuzellestir("{ : : }");
        Assert.Null(guzel);
        Assert.NotNull(hata);
    }
}
