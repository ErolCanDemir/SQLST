using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>REST İstemcisi saf çekirdeği (v20-S8): {{değişken}} çözümü + cURL ↔ istek köprüsü.</summary>
public class RestCekirdekTests
{
    // ── {{değişken}} ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Degisken_bilinenleri_cozer_bilinmeyeni_oldugu_gibi_birakir()
    {
        var d = new Dictionary<string, string> { ["baseUrl"] = "https://api.x", ["token"] = "T" };
        Assert.Equal("https://api.x/firmalar", DegiskenCozucu.Coz("{{baseUrl}}/firmalar", d));
        Assert.Equal("Bearer T", DegiskenCozucu.Coz("Bearer {{token}}", d));
        Assert.Equal("{{yok}}/x", DegiskenCozucu.Coz("{{yok}}/x", d)); // tanımsız korunur
        Assert.Equal("x", DegiskenCozucu.Coz("{{ baseUrl2 }}", new Dictionary<string, string> { ["baseUrl2"] = "x" })); // boşluk toleransı
    }

    [Fact]
    public void Degisken_adlari_ve_tanimsizlar_cikarilir()
    {
        Assert.Equal(["a", "b"], DegiskenCozucu.AdlariCikar("{{a}}/{{b}}/{{a}}")); // tekrarsız, sırayla
        Assert.Equal(["b"], DegiskenCozucu.TanimsizOlanlar("{{a}}{{b}}",
            new Dictionary<string, string> { ["a"] = "1" }));
    }

    // ── cURL → istek ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Curl_get_urlyi_cozer()
    {
        RestIstek? i = CurlCozumleyici.Coz("curl https://api.x/firmalar");
        Assert.NotNull(i);
        Assert.Equal(HttpMetodu.GET, i!.Metod);
        Assert.Equal("https://api.x/firmalar", i.Url);
    }

    [Fact]
    public void Curl_post_baslik_ve_govdeyi_cozer_metodu_cikarir()
    {
        RestIstek? i = CurlCozumleyici.Coz(
            """curl -X POST https://api.x/firmalar -H "Content-Type: application/json" -d '{"ad":"LST"}'""");
        Assert.NotNull(i);
        Assert.Equal(HttpMetodu.POST, i!.Metod);
        Assert.Equal("""{"ad":"LST"}""", i.Govde);
        Assert.Contains(i.Basliklar, b => b.Anahtar == "Content-Type" && b.Deger == "application/json");
    }

    [Fact]
    public void Curl_data_varsa_metod_post_olur()
    {
        RestIstek? i = CurlCozumleyici.Coz("curl https://api.x -d name=abc");
        Assert.Equal(HttpMetodu.POST, i!.Metod); // -X yok ama -d var → POST
        Assert.Equal("name=abc", i.Govde);
    }

    [Fact]
    public void Curl_u_ile_basic_kimlik_cozer()
    {
        RestIstek? i = CurlCozumleyici.Coz("curl -u ali:parola123 https://api.x");
        Assert.NotNull(i!.Kimlik);
        Assert.Equal("ali", i.Kimlik!.KullaniciAdi);
        Assert.Equal("parola123", i.Kimlik.Parola);
        Assert.False(i.Kimlik.BearerMi);
    }

    [Fact]
    public void Curl_G_ile_data_query_olur_govde_bosalir()
    {
        RestIstek? i = CurlCozumleyici.Coz("curl -G https://api.x/ara -d il=Ankara -d sayfa=1");
        Assert.Equal(HttpMetodu.GET, i!.Metod);
        Assert.Null(i.Govde);
        Assert.Contains(i.QueryParametreleri, q => q.Anahtar == "il" && q.Deger == "Ankara");
        Assert.Contains(i.QueryParametreleri, q => q.Anahtar == "sayfa" && q.Deger == "1");
    }

    [Fact]
    public void Curl_cok_satirli_devam_karakterini_coz()
    {
        RestIstek? i = CurlCozumleyici.Coz("curl 'https://api.x/f' \\\n  -H 'Accept: application/json' \\\n  -d 'x=1'");
        Assert.Equal("https://api.x/f", i!.Url);
        Assert.Contains(i.Basliklar, b => b.Anahtar == "Accept");
        Assert.Equal("x=1", i.Govde);
    }

    [Fact]
    public void Curl_bilinmeyen_bayraklar_atlanir()
    {
        RestIstek? i = CurlCozumleyici.Coz("curl -sSL -k https://api.x/f");
        Assert.Equal("https://api.x/f", i!.Url); // -s -S -L -k atlanır, URL yine bulunur
    }

    // ── istek → cURL ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Uret_query_baslik_ve_govdeyi_curl_e_doker()
    {
        var istek = new RestIstek(
            HttpMetodu.POST, "https://api.x/firmalar",
            [new RestSatir("il", "Ankara")],
            [new RestSatir("Content-Type", "application/json")],
            """{"ad":"LST"}""",
            Kimlik: null);
        string curl = CurlCozumleyici.Uret(istek);
        Assert.Contains("-X POST", curl, StringComparison.Ordinal);
        Assert.Contains("https://api.x/firmalar?il=Ankara", curl, StringComparison.Ordinal);
        Assert.Contains("-H 'Content-Type: application/json'", curl, StringComparison.Ordinal);
        Assert.Contains("""-d '{"ad":"LST"}'""", curl, StringComparison.Ordinal);
    }

    [Fact]
    public void Uret_bearer_kimligi_authorization_basligina_yazar()
    {
        var istek = new RestIstek(HttpMetodu.GET, "https://api.x", [], [], null, new SoapKimlik(null, "TOK"));
        Assert.Contains("-H 'Authorization: Bearer TOK'", CurlCozumleyici.Uret(istek), StringComparison.Ordinal);
    }

    // ── JSON → tablo (yanıt→grid) ───────────────────────────────────────────────────────────

    [Fact]
    public void JsonTablo_nesne_dizisini_kolon_birlesimiyle_cikarir()
    {
        JsonTabloCikar.Tablo t = JsonTabloCikar.Coz(
            """[ {"id":1,"ad":"A"}, {"id":2,"ad":"B","il":"Ankara"} ]""");
        Assert.Equal(["id", "ad", "il"], t.Kolonlar);        // birleşim, ilk görülme sırası
        Assert.Equal(2, t.Satirlar.Count);
        Assert.Equal(["1", "A", null], t.Satirlar[0]);       // eksik alan null
        Assert.Equal(["2", "B", "Ankara"], t.Satirlar[1]);
    }

    [Fact]
    public void JsonTablo_kok_nesnede_ilk_diziyi_kullanir()
    {
        JsonTabloCikar.Tablo t = JsonTabloCikar.Coz(
            """{ "toplam":128, "firmalar":[ {"id":42,"unvan":"LST"} ] }""");
        Assert.Equal(["id", "unvan"], t.Kolonlar);           // "firmalar" dizisi satır kaynağı
        Assert.Equal(["42", "LST"], t.Satirlar[0]);
    }

    [Fact]
    public void JsonTablo_dizisiz_nesne_tek_satir_olur()
    {
        JsonTabloCikar.Tablo t = JsonTabloCikar.Coz("""{ "id":7, "aktif":true }""");
        Assert.Equal(["id", "aktif"], t.Kolonlar);
        Assert.Equal(["7", "true"], t.Satirlar[0]);
    }

    [Fact]
    public void Curl_git_gel_temel_bilgiyi_korur()
    {
        const string kaynak = """curl -X PUT 'https://api.x/firma/42' -H 'Authorization: Bearer T' -d '{"aktif":false}'""";
        RestIstek? i = CurlCozumleyici.Coz(kaynak);
        string tekrar = CurlCozumleyici.Uret(i!);
        RestIstek? j = CurlCozumleyici.Coz(tekrar);
        Assert.Equal(HttpMetodu.PUT, j!.Metod);
        Assert.Equal("https://api.x/firma/42", j.Url);
        Assert.Equal("""{"aktif":false}""", j.Govde);
        Assert.Contains(j.Basliklar, b => b.Anahtar == "Authorization" && b.Deger == "Bearer T");
    }
}
