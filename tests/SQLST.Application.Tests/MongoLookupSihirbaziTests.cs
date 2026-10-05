using System.Text.Json;
using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.26 fikir 7 — $lookup sihirbazı: üretilen pipeline SQLST'nin çalıştırdığı biçimde
/// ({"aggregate": …, "pipeline": […]}) ve GEÇERLİ JSON olmalı; düzleştirme/LEFT JOIN seçenekleri
/// doğru adımları üretmeli (preserveNullAndEmptyArrays yalnız istendiğinde).
/// </summary>
public class MongoLookupSihirbaziTests
{
    // v22-S1 m.13: üretici artık sona $limit ekliyor; ADIM SAYISI iddiası olan eski testler sınırı
    // KAPATARAK (onizleme: null) eski davranışı ölçmeye devam eder.
    private static JsonElement Pipeline(bool duzlestir, bool eslesmeyenler, int? onizleme = null)
    {
        string json = MongoLookupSihirbazi.PipelineYaz(
            "oturumlar", "kullanicilar", "kullaniciId", "_id", "kullanici", duzlestir, eslesmeyenler, onizleme);
        return JsonDocument.Parse(json).RootElement;   // ⚠ bozuk JSON üretmediğimizin kanıtı
    }

    [Fact]
    public void Lookup_adimi_dogru_alanlarla_uretilir()
    {
        JsonElement kok = Pipeline(duzlestir: false, eslesmeyenler: false);

        Assert.Equal("oturumlar", kok.GetProperty("aggregate").GetString());
        JsonElement adimlar = kok.GetProperty("pipeline");
        Assert.Equal(1, adimlar.GetArrayLength());     // düzleştirme yok → tek adım

        JsonElement lookup = adimlar[0].GetProperty("$lookup");
        Assert.Equal("kullanicilar", lookup.GetProperty("from").GetString());
        Assert.Equal("kullaniciId", lookup.GetProperty("localField").GetString());
        Assert.Equal("_id", lookup.GetProperty("foreignField").GetString());
        Assert.Equal("kullanici", lookup.GetProperty("as").GetString());
    }

    [Fact]
    public void Duzlestirme_unwind_ekler_left_join_kapaliyken_sade()
    {
        JsonElement adimlar = Pipeline(duzlestir: true, eslesmeyenler: false).GetProperty("pipeline");

        Assert.Equal(2, adimlar.GetArrayLength());
        Assert.Equal("$kullanici", adimlar[1].GetProperty("$unwind").GetString());  // sade biçim
    }

    [Fact]
    public void Left_join_preserve_bayragiyla_gelir()
    {
        JsonElement unwind = Pipeline(duzlestir: true, eslesmeyenler: true)
            .GetProperty("pipeline")[1].GetProperty("$unwind");

        Assert.Equal("$kullanici", unwind.GetProperty("path").GetString());
        Assert.True(unwind.GetProperty("preserveNullAndEmptyArrays").GetBoolean());
    }

    [Fact]
    public void Ozel_karakterli_adlar_kacisli_yazilir()
    {
        string json = MongoLookupSihirbazi.PipelineYaz(
            "koleksiyon\"riskli", "b", "a.b", "_id", "so\\nuc", false, false);

        JsonElement kok = JsonDocument.Parse(json).RootElement;   // parse edilebiliyorsa kaçış doğru
        Assert.Equal("koleksiyon\"riskli", kok.GetProperty("aggregate").GetString());
        Assert.Equal("a.b", kok.GetProperty("pipeline")[0].GetProperty("$lookup").GetProperty("localField").GetString());
    }

    [Theory]
    [InlineData("kullanicilar", "kullanici")]
    [InlineData("belgeler", "belge")]
    [InlineData("users", "user")]
    [InlineData("log", "log")]
    [InlineData("", "eslesen")]
    public void Varsayilan_sonuc_alani_makul(string koleksiyon, string beklenen)
        => Assert.Equal(beklenen, MongoLookupSihirbazi.VarsayilanSonucAlani(koleksiyon));

    // ── v22-S1 saha turu-2 m.13: ÖNİZLEME SINIRI ───────────────────────────────────────────
    // Kullanıcı sihirbazın ürettiği sorguyu gönderdi: "çok yavaş çalışıyor". ÖLÇÜM (yerel MongoDB,
    // ana 50.000 · hedef 10.000 belge — gerçek koleksiyonların çok altında): limitsiz sorgu 60 sn
    // komut tavanına dayanıp İPTAL oldu (0 satır); $limit 200 ile 16,9 sn; hedef alan index'liyken
    // 143 ms. Sınır SONA konur: $lookup'tan ÖNCE konsaydı yalnız ilk N ANA belge birleşir, eşleşmeler
    // sessizce kaybolurdu (aynı ölçümde 200 yerine 40 satır).

    [Fact]
    public void Varsayilan_pipeline_sona_limit_ekler()
    {
        string json = MongoLookupSihirbazi.PipelineYaz(
            "oturumlar", "kullanicilar", "kullaniciId", "_id", "kullanici",
            duzlestir: true, eslesmeyenlerDeGelsin: false); // onizleme parametresi VERİLMEDİ

        JsonElement adimlar = JsonDocument.Parse(json).RootElement.GetProperty("pipeline");
        Assert.Equal(3, adimlar.GetArrayLength());                       // lookup + unwind + limit
        Assert.True(adimlar[0].TryGetProperty("$lookup", out _));
        Assert.True(adimlar[1].TryGetProperty("$unwind", out _));
        Assert.Equal(MongoLookupSihirbazi.VarsayilanOnizleme, adimlar[2].GetProperty("$limit").GetInt32());
    }

    [Fact]
    public void Limit_unwind_SONRASINA_konur_eslesmeler_kaybolmasin()
    {
        JsonElement adimlar = Pipeline(duzlestir: true, eslesmeyenler: true, onizleme: 50)
            .GetProperty("pipeline");

        Assert.Equal(3, adimlar.GetArrayLength());
        Assert.False(adimlar[0].TryGetProperty("$limit", out _)); // $lookup'tan ÖNCE DEĞİL
        Assert.Equal(50, adimlar[adimlar.GetArrayLength() - 1].GetProperty("$limit").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void Sinirsiz_istenirse_limit_adimi_hic_eklenmez(int? onizleme)
    {
        JsonElement adimlar = Pipeline(duzlestir: false, eslesmeyenler: false, onizleme)
            .GetProperty("pipeline");

        Assert.Equal(1, adimlar.GetArrayLength()); // yalnız $lookup
    }
}
