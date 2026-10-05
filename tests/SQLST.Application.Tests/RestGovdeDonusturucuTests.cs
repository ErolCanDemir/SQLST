using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 saha m.21 — REST Body anahtar/değer ⇄ JSON dönüştürücüsü: tipleme kuralları
/// (sayı/bool/null otomatik, {{değişken}} metin kalır, hücredeki JSON parçası gömülür),
/// iki yönlü tutarlılık ve "düz nesne değilse tabloya karışma" sözleşmesi.
/// </summary>
public class RestGovdeDonusturucuTests
{
    [Fact]
    public void Kv_json_uretir_ve_tipler()
    {
        string json = RestGovdeDonusturucu.KvdenJson(
        [
            ("ad", "Ali Veli"),
            ("yas", "42"),
            ("puan", "3.14"),
            ("aktif", "true"),
            ("silinmis", "null"),
            ("token", "{{Token}}"),          // değişken yer tutucusu METİN kalır
            ("adres", """{"il":"İzmir"}"""), // hücredeki JSON parçası gömülür
            ("", "atlanir"),                 // boş anahtar atlanır
        ]);

        Assert.Contains("\"ad\": \"Ali Veli\"", json);
        Assert.Contains("\"yas\": 42", json);
        Assert.Contains("\"puan\": 3.14", json);
        Assert.Contains("\"aktif\": true", json);
        Assert.Contains("\"silinmis\": null", json);
        Assert.Contains("\"token\": \"{{Token}}\"", json);
        Assert.Contains("\"il\": \"İzmir\"", json); // Türkçe \u kaçışına çevrilmez
        Assert.DoesNotContain("atlanir", json);
    }

    [Fact]
    public void Json_duz_nesne_tabloya_cozulur()
    {
        var kv = RestGovdeDonusturucu.JsondanKv("""{"ad":"Ali","yas":42,"aktif":true,"x":null,"ic":{"a":1}}""");

        Assert.NotNull(kv);
        Assert.Equal(("ad", "Ali"), kv![0]);
        Assert.Equal(("yas", "42"), kv[1]);
        Assert.Equal(("aktif", "true"), kv[2]);
        Assert.Equal(("x", "null"), kv[3]);
        Assert.Equal("ic", kv[4].Anahtar);
        Assert.Contains("\"a\":1", kv[4].Deger); // iç içe değer hücrede JSON metni olarak taşınır
    }

    [Fact]
    public void Gidis_donus_ayni_govdeyi_uretir()
    {
        string json = RestGovdeDonusturucu.KvdenJson([("ad", "Ali"), ("yas", "42"), ("t", "{{Token}}")]);
        var kv = RestGovdeDonusturucu.JsondanKv(json);
        Assert.NotNull(kv);
        Assert.Equal(json, RestGovdeDonusturucu.KvdenJson(kv!.Select(s => (s.Anahtar, s.Deger))));
    }

    [Theory]
    [InlineData("[1,2,3]")]                    // kök dizi
    [InlineData("\"metin\"")]                  // kök skaler
    [InlineData("{ \"yarim\": ")]              // bozuk JSON (yazım sürüyor)
    [InlineData("{ \"a\": {{Token}} }")]       // değişkenli gövde geçerli JSON değil
    public void Duz_nesne_olmayan_govde_tabloya_karismaz(string json)
        => Assert.Null(RestGovdeDonusturucu.JsondanKv(json));

    [Fact]
    public void Bos_govde_bos_tablo()
        => Assert.Empty(RestGovdeDonusturucu.JsondanKv("")!);
}
