using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// asistan.config (kullanıcı kararı 2026-07-26: ayarlar UI'dan çıktı, yalnız dosyadan okunur).
/// Kritik davranış: dosyaya DÜZ METİN "anahtar" yazılırsa ilk okumada DPAPI ile şifrelenip
/// dosya MÜHÜRLENİR — düz metin diskte kalmaz.
/// </summary>
public class AsistanConfigTests : IDisposable
{
    private readonly string _dosya = Path.Combine(Path.GetTempPath(), $"sqlst-asistan-{Guid.NewGuid():N}.config");
    private readonly DpapiSecretProtector _protector = new();

    private AsistanConfigDosyasi Config() => new(_dosya, _protector);

    public void Dispose()
    {
        if (File.Exists(_dosya))
            File.Delete(_dosya);
    }

    [Fact]
    public void Dosya_yoksa_null_doner()
        => Assert.Null(Config().Oku());

    [Fact]
    public void Bozuk_json_null_doner_ve_firlatmaz()
    {
        File.WriteAllText(_dosya, "{ bu json degil");
        Assert.Null(Config().Oku());
    }

    [Fact]
    public void Duz_metin_anahtar_ilk_okumada_sifrelenir_ve_dosya_muhurlenir()
    {
        File.WriteAllText(_dosya, """
            { "saglayici": "OpenAiUyumlu", "model": "bir-model", "anahtar": "cok-gizli-anahtar" }
            """);

        AsistanAyarlari? ayarlar = Config().Oku();

        Assert.NotNull(ayarlar);
        Assert.True(ayarlar.Hazir);
        Assert.Equal("cok-gizli-anahtar", _protector.Coz(ayarlar.AnahtarSifreli!)); // şifreli değer çözülünce aynı

        // Dosya mühürlendi: düz metin İZİ YOK, şifreli alan VAR.
        string muhurlu = File.ReadAllText(_dosya);
        Assert.DoesNotContain("cok-gizli-anahtar", muhurlu);
        Assert.Contains("anahtarSifreli", muhurlu);

        // İkinci okuma da aynı ayarları verir (mühürlü dosyadan).
        AsistanAyarlari? tekrar = Config().Oku();
        Assert.Equal("cok-gizli-anahtar", _protector.Coz(tekrar!.AnahtarSifreli!));
    }

    [Fact]
    public void Yaz_oku_gidis_donusu_alanlari_korur()
    {
        var ayarlar = new AsistanAyarlari(
            AsistanSaglayici.OpenAiUyumlu, "llama-3.3-70b-versatile",
            "http://localhost:11434", _protector.Sifrele("k"));

        Config().Yaz(ayarlar);
        AsistanAyarlari? okunan = Config().Oku();

        Assert.Equal(ayarlar, okunan); // record eşitliği: sağlayıcı + model + taban + şifreli anahtar
    }

    [Fact]
    public void Eksik_alanlar_varsayilana_duser()
    {
        File.WriteAllText(_dosya, """{ "anahtar": "k" }""");
        AsistanAyarlari? ayarlar = Config().Oku();

        Assert.Equal(AsistanSaglayici.Yerel, ayarlar!.Saglayici); // v22-S6: varsayılan artık Yerel
        Assert.Equal(AsistanYonlendirici.YerelVarsayilanModel, ayarlar.Model);
        Assert.Equal(AsistanYonlendirici.YerelVarsayilanTaban, ayarlar.TabanAdres);
        Assert.True(ayarlar.Hazir);
    }

    /// <summary>
    /// ⛔→🏠 GEMİNİ GÖÇÜ (v22-S6) — sahadaki config dosyalarının çoğu böyle. Kritik nokta MODEL:
    /// sağlayıcıyı Yerel'e çevirip "gemini-flash-latest" adını bırakmak, Ollama'ya var olmayan bir
    /// model göndermek olurdu (404). Model ve taban da yerel varsayılana çekilmeli.
    /// </summary>
    [Fact]
    public void Eski_gemini_configi_yerele_gocurulur_model_de_tasinmaz()
    {
        File.WriteAllText(_dosya, """
            { "saglayici": "Gemini", "model": "gemini-flash-latest", "anahtarSifreli": "eski-sifreli" }
            """);

        AsistanAyarlari? ayarlar = Config().Oku();

        Assert.Equal(AsistanSaglayici.Yerel, ayarlar!.Saglayici);
        Assert.Equal(AsistanYonlendirici.YerelVarsayilanModel, ayarlar.Model); // gemini-* TAŞINMADI
        Assert.Equal(AsistanYonlendirici.YerelVarsayilanTaban, ayarlar.TabanAdres);
        Assert.Equal("eski-sifreli", ayarlar.AnahtarSifreli); // kullanıcının sırrı SİLİNMEZ
        Assert.True(ayarlar.Hazir);

        // Göç diske de yansır: dosya kendini onarır, her açılışta tekrar göçürülmez.
        string yeni = File.ReadAllText(_dosya);
        Assert.DoesNotContain("Gemini", yeni);
        Assert.Contains("Yerel", yeni);
    }
}
