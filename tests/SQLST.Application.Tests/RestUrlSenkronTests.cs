using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// 🌐 URL ↔ Params senkron çekirdeği (v23 REST yeniden tasarımı — kullanıcı isteği 1 Eki 2026:
/// "URL'ye ?ad=değer yazınca parametre tablosu dolmalı, Postman gibi"). Sözleşme: metinler ham
/// taşınır (encode yok — {{değişken}} bozulmaz), ✓ kaldırılan satır URL'den düşer ama kalır.
/// </summary>
public class RestUrlSenkronTests
{
    [Fact]
    public void Ayristir_soru_isaretinden_boler_ciftleri_cikarir()
    {
        (string taban, IReadOnlyList<RestSatir> p) =
            RestUrlSenkron.Ayristir("https://api.site.com/v2/talepler?durum=acik&sayfa=2&limit=50");

        Assert.Equal("https://api.site.com/v2/talepler", taban);
        Assert.Equal(3, p.Count);
        Assert.Equal(("durum", "acik"), (p[0].Anahtar, p[0].Deger));
        Assert.Equal(("sayfa", "2"), (p[1].Anahtar, p[1].Deger));
        Assert.Equal(("limit", "50"), (p[2].Anahtar, p[2].Deger));
        Assert.All(p, s => Assert.True(s.Etkin));
    }

    [Fact]
    public void Ayristir_soru_isareti_yoksa_param_bos()
    {
        (string taban, IReadOnlyList<RestSatir> p) = RestUrlSenkron.Ayristir("https://api.site.com/v2");
        Assert.Equal("https://api.site.com/v2", taban);
        Assert.Empty(p);
    }

    [Fact]
    public void Ayristir_ilk_esittire_gore_boler_degerdeki_esittir_korunur() // base64/JWT değerleri
    {
        (_, IReadOnlyList<RestSatir> p) = RestUrlSenkron.Ayristir("x?token=abc=defg==&bayrak");
        Assert.Equal(("token", "abc=defg=="), (p[0].Anahtar, p[0].Deger));
        Assert.Equal(("bayrak", ""), (p[1].Anahtar, p[1].Deger)); // '=' yok → değer boş
    }

    [Fact]
    public void Ayristir_bos_bolutleri_atlar_ve_degisken_yertutucusu_bozulmaz()
    {
        (_, IReadOnlyList<RestSatir> p) = RestUrlSenkron.Ayristir("x?a=1&&b={{Token}}&");
        Assert.Equal(2, p.Count);
        Assert.Equal("{{Token}}", p[1].Deger); // encode edilmedi — ham korunur
    }

    [Fact]
    public void Kur_yalniz_etkin_ve_anahtari_dolu_satirlari_yazar() // ✓ kaldırılan düşer ama silinmez
    {
        string url = RestUrlSenkron.Kur("https://x/api",
        [
            new RestSatir("durum", "acik", true),
            new RestSatir("sirala", "tarih", false),   // ✓ kaldırıldı → URL'de yok
            new RestSatir("", "sahipsiz", true),       // anahtarsız → yazılmaz
            new RestSatir("limit", "", true),          // değer boş → "limit=" yazılır
        ]);
        Assert.Equal("https://x/api?durum=acik&limit=", url);
    }

    [Fact]
    public void Kur_hic_etkin_yoksa_taban_doner()
        => Assert.Equal("https://x/api",
            RestUrlSenkron.Kur("https://x/api", [new RestSatir("a", "1", false)]));

    [Fact]
    public void Ayristir_kur_gidis_donusu_kayipsiz() // senkronun temel güvencesi
    {
        const string url = "https://api.site.com/v1/ara?q=sql%20server&tip=2&bos=";
        (string taban, IReadOnlyList<RestSatir> p) = RestUrlSenkron.Ayristir(url);
        Assert.Equal(url, RestUrlSenkron.Kur(taban, p)); // encode'a dokunulmadı — birebir
    }
}
