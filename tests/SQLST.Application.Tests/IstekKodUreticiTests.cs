using SQLST.Application;
using SQLST.Contracts;
using Xunit;

namespace SQLST.Application.Tests;

public class IstekKodUreticiTests
{
    private static RestIstek Istek(
        HttpMetodu metod = HttpMetodu.GET, string url = "https://api/x",
        string? govde = null, SoapKimlik? kimlik = null,
        RestSatir[]? basliklar = null, RestSatir[]? query = null) =>
        new(metod, url, query ?? [], basliklar ?? [], govde, kimlik);

    [Fact]
    public void CSharp_metod_url_baslik_govde_uretir()
    {
        RestIstek i = Istek(HttpMetodu.POST, "https://api/kisi",
            govde: "{\"ad\":\"Ahmet\"}",
            basliklar: [new RestSatir("X-Api", "k1")]);

        string kod = IstekKodUretici.Uret(i, KodDili.CSharp);

        Assert.Contains("HttpMethod.Post", kod);
        Assert.Contains("https://api/kisi", kod);
        Assert.Contains("X-Api", kod);
        Assert.Contains("StringContent", kod);
        Assert.Contains("\\\"ad\\\"", kod); // gövdedeki tırnak kaçışlı
    }

    [Fact]
    public void CSharp_bearer_authorization_header()
    {
        string kod = IstekKodUretici.Uret(Istek(kimlik: new SoapKimlik(null, "tok123")), KodDili.CSharp);
        Assert.Contains("AuthenticationHeaderValue(\"Bearer\", \"tok123\")", kod);
    }

    [Fact]
    public void Python_requests_headers_params_uretir()
    {
        RestIstek i = Istek(HttpMetodu.GET, "https://api/liste",
            basliklar: [new RestSatir("Accept", "application/json")],
            query: [new RestSatir("sayfa", "2")]);

        string kod = IstekKodUretici.Uret(i, KodDili.Python);

        Assert.Contains("import requests", kod);
        Assert.Contains("\"get\"", kod);
        Assert.Contains("\"Accept\": \"application/json\"", kod);
        Assert.Contains("\"sayfa\": \"2\"", kod);
        Assert.Contains("resp.status_code", kod);
    }

    [Fact]
    public void Python_govde_data_ve_basic_auth()
    {
        RestIstek i = Istek(HttpMetodu.POST, "https://api/x",
            govde: "veri", kimlik: new SoapKimlik("kul", "sifre"));

        string kod = IstekKodUretici.Uret(i, KodDili.Python);

        Assert.Contains("data=\"veri\"", kod);
        Assert.Contains("auth=(\"kul\", \"sifre\")", kod);
    }

    [Fact]
    public void Curl_mevcut_uretice_devreder()
    {
        // cURL üretimi CurlCozumleyici.Uret ile — round-trip'te curl + URL bulunmalı
        string kod = IstekKodUretici.Uret(Istek(url: "https://api/z"), KodDili.Curl);
        Assert.Contains("curl", kod);
        Assert.Contains("https://api/z", kod);
    }

    [Fact]
    public void Etkin_olmayan_baslik_atlanir()
    {
        RestIstek i = Istek(basliklar: [new RestSatir("Gizli", "x", Etkin: false)]);
        string kod = IstekKodUretici.Uret(i, KodDili.CSharp);
        Assert.DoesNotContain("Gizli", kod);
    }
}
