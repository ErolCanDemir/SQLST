using System.Net;
using System.Text.Json;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 🤖 Asistan S1 (v11, 2026-07-25): SAF istem kurucuları + sağlayıcı gövde/yanıt çözücüleri +
/// hazırlık kapısı. Gizlilik rayı burada kilitlenir: şema özetinde yalnız ad/tip vardır.
/// </summary>
public class AsistanTests
{
    private static SemaOnbellegi Onbellek(int tabloSayisi = 2)
    {
        var nesneler = new List<SemaNesnesi>();
        for (int i = 1; i <= tabloSayisi; i++)
            nesneler.Add(new SemaNesnesi("Db", "dbo", $"Tablo{i:00}", SemaNesneTuru.Tablo,
                [new("Id", "int", false, true), new("Ad", "nvarchar(50)", true, false)], []));
        return new SemaOnbellegi { Nesneler = nesneler, YuklenmeZamaniUtc = DateTime.UtcNow };
    }

    [Fact]
    public void Sema_ozeti_ad_ve_tip_icerir_pk_isaretler()
    {
        string ozet = AsistanIstemleri.SemaOzeti(Onbellek());
        Assert.Contains("dbo.Tablo01 (Id int PK, Ad nvarchar(50))", ozet);
        Assert.DoesNotContain("Sunucu", ozet); // bağlantı bilgisi asla girmez
    }

    [Fact]
    public void Sema_ozeti_tavani_asinca_ACIKCA_kirpar()
    {
        string ozet = AsistanIstemleri.SemaOzeti(Onbellek(AsistanIstemleri.EnFazlaNesne + 5));
        Assert.Contains("+5 nesne daha — özet kırpıldı", ozet);
    }

    [Fact]
    public void Serbest_soru_istemi_sema_motor_ve_editor_sorgusunu_tasir()
    {
        string istem = AsistanIstemleri.SerbestSoru(
            "en çok siparişi olan 10 müşteri", "dbo.Musteri (Id int PK)",
            AsistanIstemleri.MotorAdi(MotorTuru.Mssql), "SELECT * FROM dbo.Musteri");

        Assert.Contains("Microsoft SQL Server (T-SQL)", istem);
        Assert.Contains("dbo.Musteri (Id int PK)", istem);
        Assert.Contains("ÜZERİNDE ÇALIŞTIĞI SORGU", istem);
        Assert.Contains("SELECT * FROM dbo.Musteri", istem);
        Assert.Contains("en çok siparişi olan 10 müşteri", istem);
        Assert.Contains("uydurma", istem); // şema dışına çıkma yönergesi
    }

    // ───────── v22-S7: ŞEMA KAPISI (ölçüm: şema bloğu "merhaba"yı 0,7 sn → 90 sn yapıyordu) ─────────

    /// <summary>Selamlaşma/sohbet → şema İSTENMEZ. Bu satırların her biri sahada 90 sn'ye mal oluyordu.</summary>
    [Theory]
    [InlineData("merhaba")]
    [InlineData("Selam")]
    [InlineData("günaydın")]
    [InlineData("teşekkürler")]
    [InlineData("nasılsın?")]
    [InlineData("sen kimsin")]
    [InlineData("ne yapabilirsin")]
    [InlineData("")]
    [InlineData("   ")]
    public void Sema_kapisi_sohbette_kapali(string soru)
        => Assert.False(AsistanIstemleri.SemaIsteniyorMu(soru));

    /// <summary>
    /// Veri sorusu → şema İSTENİR. Türkçe EKLİ hâller de yakalanmalı ("sayısı" → "say",
    /// "tablolarda" → "tablo"); yakalanmazsa model şemayı göremez ve cevap bozulur (pahalı hata).
    /// </summary>
    [Theory]
    [InlineData("en çok siparişi olan müşterileri getir")]
    [InlineData("sipariş sayısı kaç")]
    [InlineData("hangi tablolarda tarih kolonu var")]
    [InlineData("bu sorguyu hızlandır")]
    [InlineData("SELECT yazar mısın")]
    [InlineData("müşterileri listele")]
    [InlineData("aylık toplam ciro raporu")]
    [InlineData("index önerir misin")]
    [InlineData("veritabanında neler var")]
    public void Sema_kapisi_veri_sorusunda_acik(string soru)
        => Assert.True(AsistanIstemleri.SemaIsteniyorMu(soru));

    /// <summary>Şema özeti BOŞ verilince istemde şema bölümü ve ona bağlı yönergeler HİÇ yer almaz.</summary>
    [Fact]
    public void Serbest_soru_semasiz_kurulunca_sema_bolumu_yazilmaz()
    {
        string istem = AsistanIstemleri.SerbestSoru(
            "merhaba", kompaktSema: "", AsistanIstemleri.MotorAdi(MotorTuru.Mssql));

        Assert.DoesNotContain("VERİTABANI ŞEMASI", istem);
        Assert.DoesNotContain("uydurma", istem); // şemaya atıf yapan yönerge de yazılmaz
        Assert.DoesNotContain("SÖZ DİZİMİ", istem);
        Assert.Contains("merhaba", istem);
        // Asıl kazanç: istem küçücük kalmalı (ölçüm: 59 token ≈ 0,7 sn ↔ 1.434 token ≈ 90 sn).
        Assert.True(istem.Length < 400, $"şemasız istem {istem.Length} karakter — fazla büyük");
    }

    [Fact]
    public void Sorgu_degerlendir_istemi_sorguyu_semayi_ve_kontrol_listesini_tasir()
    {
        // v11-S2 (kullanıcı isteği 1): tek tık değerlendirme.
        string istem = AsistanIstemleri.SorguDegerlendir(
            "SELECT * FROM dbo.Musteri m JOIN dbo.Siparis s", "dbo.Musteri (Id int PK)",
            AsistanIstemleri.MotorAdi(MotorTuru.Postgres));

        Assert.Contains("DEĞERLENDİRİLECEK SORGU:", istem);
        Assert.Contains("JOIN dbo.Siparis s", istem);
        Assert.Contains("PostgreSQL", istem);
        Assert.Contains("WHERE'siz UPDATE/DELETE", istem);      // risk kontrolü yönergede
        Assert.Contains("DÜZELTİLMİŞ SORGUNUN TAMAMINI", istem); // düzeltme talebi
        Assert.Contains("dbo.Musteri (Id int PK)", istem);
    }

    [Fact]
    public void Plan_metni_agaci_girintili_ve_uyarili_yazar()
    {
        // v11-S5b: metinleştirici sayı uydurmaz — ne varsa onu yazar.
        var plan = new SorguPlani(Gercek: true,
        [
            new IfadePlani("SELECT …", 10,
                new PlanDugumu("Hash Match", null, 60, 100, 1500, [],
                    [new PlanDugumu("Index Scan", "IX_Musteri", 40, 100, 1500, ["tempdb'ye taşma"], [])]),
                [new PlanEksikIndexi(85, "dbo.Musteri", ["Ad"], [], [])]),
        ]);
        string metin = AsistanIstemleri.PlanMetni(plan);
        Assert.Contains("(GERÇEK plan", metin);
        Assert.Contains("- Hash Match · pay %60", metin);
        Assert.Contains("  - Index Scan [IX_Musteri]", metin);
        Assert.Contains("gerçek 1500 satır", metin);
        Assert.Contains("UYARI: tempdb'ye taşma", metin);
        Assert.Contains("EKSİK INDEX önerisi (etki %85)", metin);

        string istem = AsistanIstemleri.PlanYorumla(metin, "PostgreSQL");
        Assert.Contains("EXECUTION PLAN:", istem);
        Assert.Contains("Sayı uydurma", istem);
    }

    [Fact]
    public void Log_ozetle_istemi_gruplari_ve_kok_neden_yonergesini_tasir()
    {
        string istem = AsistanIstemleri.LogOzetle("30× adimTamamlandi:50\n2× Zaman asimi");
        Assert.Contains("KÖK NEDEN", istem);
        Assert.Contains("30× adimTamamlandi:50", istem);
    }

    [Fact]
    public void Cevaptan_sorgu_ayiklama_blok_ve_yedek_yollar()
    {
        // v11-S3: ``` bloklarından EN UZUNU (düzeltilmiş tam sorgu genelde o)
        const string cevap = """
            Sorguda hata var.

            ```sql
            SELECT Id FROM dbo.Musteri
            ```
            Düzeltilmiş hali:
            ```sql
            SELECT m.Id, m.Ad FROM dbo.Musteri m WHERE m.Aktif = 1
            ```
            """;
        Assert.Equal("SELECT m.Id, m.Ad FROM dbo.Musteri m WHERE m.Aktif = 1",
            AsistanIstemleri.CevaptanSorguAyikla(cevap));

        // Blok yoksa: cevabın tamamı sorgu gibi başlıyorsa o (Mongo { dahil)
        Assert.Equal("SELECT 1", AsistanIstemleri.CevaptanSorguAyikla("  SELECT 1  "));
        Assert.StartsWith("{", AsistanIstemleri.CevaptanSorguAyikla("{ \"find\": \"x\" }")!);

        // Düz metin cevap → null (düğme çıkmaz)
        Assert.Null(AsistanIstemleri.CevaptanSorguAyikla("Bu sorgu doğru görünüyor."));
        Assert.Null(AsistanIstemleri.CevaptanSorguAyikla(null));
    }

    /// <summary>
    /// ⛔ Gemini KALDIRILDI (v22-S6). Regresyon kapısı: sağlayıcı listesine geri sızarsa bu test düşer.
    /// Enum'da yalnız 🏠 Yerel ve OpenAI-uyumlu kalmalı ve VARSAYILAN (0) Yerel olmalı — "kendi
    /// ajanımız" kararının kod karşılığı budur.
    /// </summary>
    [Fact]
    public void Saglayici_listesinde_gemini_yok_varsayilan_yerel()
    {
        string[] adlar = Enum.GetNames<AsistanSaglayici>();
        Assert.Equal(["Yerel", "OpenAiUyumlu"], adlar);
        Assert.DoesNotContain(adlar, a => a.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AsistanSaglayici.Yerel, default(AsistanSaglayici));
    }

    [Fact]
    public void OpenAi_govdesi_ve_cevap_cozumu()
    {
        string govde = AsistanYonlendirici.OpenAiGovde("soru", "llama3.2");
        using JsonDocument d = JsonDocument.Parse(govde);
        Assert.Equal("llama3.2", d.RootElement.GetProperty("model").GetString());
        Assert.Equal("soru",
            d.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        const string yanit = """
            { "choices": [ { "message": { "role": "assistant", "content": "cevap metni" } } ] }
            """;
        Assert.Equal("cevap metni", AsistanYonlendirici.OpenAiCevapCoz(yanit));
    }

    [Fact]
    public void Istekler_dogru_uca_ve_basliklara_gider()
    {
        using var y = AsistanYonlendirici.YerelIstek("i", null, null);
        Assert.Equal("http://127.0.0.1:11434/api/chat", y.RequestUri!.ToString()); // Ollama'nın KENDİ ucu

        using var o = AsistanYonlendirici.OpenAiIstek("i", "m", "http://localhost:11434/", "");
        Assert.Equal("http://localhost:11434/v1/chat/completions", o.RequestUri!.ToString());
        Assert.False(o.Headers.Contains("Authorization")); // Ollama anahtarsız — başlık hiç eklenmez
    }

    [Fact]
    public void Hazir_kapisi_uzak_uc_anahtar_ister_yerel_istemez()
    {
        Assert.True(new AsistanAyarlari(AsistanSaglayici.Yerel, "m", null, null).Hazir);
        Assert.True(new AsistanAyarlari(
            AsistanSaglayici.OpenAiUyumlu, "m", "http://localhost:11434", null).Hazir);
        Assert.False(new AsistanAyarlari(
            AsistanSaglayici.OpenAiUyumlu, "m", "https://api.groq.com/openai", null).Hazir);
    }

    // ── Yerel (Ollama) sağlayıcı — v21-S1 ────────────────────────────────────────────────────

    [Fact]
    public void Yerel_saglayici_anahtarsiz_hazir()
    {
        // Yerel her koşulda hazır: anahtar/internet gerekmez (kapalı ağ hedefi)
        Assert.True(new AsistanAyarlari(AsistanSaglayici.Yerel, "", null, null).Hazir);
        Assert.True(new AsistanAyarlari(AsistanSaglayici.Yerel, "qwen2.5-coder:7b",
            "http://127.0.0.1:11434", null).Hazir);
    }

    [Fact]
    public void Yerel_istek_localhost_ve_varsayilanlarla_kurulur()
    {
        // Model/taban boş → S1 varsayılanları (qwen2.5-coder:7b · 127.0.0.1:11434), anahtar başlığı YOK
        using HttpRequestMessage r = AsistanYonlendirici.YerelIstek("soru", null, null);
        // v22-S5: Ollama'nın KENDİ ucu. OpenAI-uyumlu uç keep_alive'ı sessizce yutuyordu (ölçüldü:
        // 2 dk gönderildi → düşme süresi değişmedi; aynı değer /api/chat'e gidince 1 dk'ya indi).
        Assert.Equal("http://127.0.0.1:11434/api/chat", r.RequestUri!.ToString());
        Assert.False(r.Headers.Contains("Authorization"));

        string govde = new StreamReader(r.Content!.ReadAsStream()).ReadToEnd();
        using JsonDocument d = JsonDocument.Parse(govde);
        Assert.Equal(AsistanYonlendirici.YerelVarsayilanModel,
            d.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// v22-S5 (KDS S30c iyileştirmesinin SQLST karşılığı): istek gövdesi model bellekte kalma
    /// süresini VE üretim tavanını taşımalı. İkisi de "sessizce kaybolabilen" ayarlar olduğu için
    /// gövdede birebir aranır — biri düşerse soğuk yükleme (108 sn) ya da kaçak üretim geri gelir.
    /// </summary>
    [Fact]
    public void Yerel_istek_bellekte_kalma_ve_uretim_tavani_tasir()
    {
        using HttpRequestMessage r = AsistanYonlendirici.YerelIstek("soru", null, null);
        string govde = new StreamReader(r.Content!.ReadAsStream()).ReadToEnd();
        using JsonDocument d = JsonDocument.Parse(govde);

        Assert.Equal(AsistanYonlendirici.YerelBellekteKalma,
            d.RootElement.GetProperty("keep_alive").GetString());
        Assert.Equal(AsistanYonlendirici.YerelAzamiUretim,
            d.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(d.RootElement.GetProperty("stream").GetBoolean()); // tek parça yanıt
    }

    /// <summary>
    /// v22-S5 (kullanıcı bulgusu: "sorulara neden cevap verememiş — verebilmesi gerekiyor"):
    /// ekran görüntüsünde AYNI oturumda ilk iki soru cevaplanmış, sonrakiler
    /// "Sağlayıcı 503 döndürdü: This model is currently experiencing high demand" almıştı.
    /// Yani yapılandırma değil, ücretsiz bulut modelinin o anki DOLULUĞU — geçici bir durum.
    /// Eskiden ilk hatada pes ediliyordu; artık geçici kodlarda yeniden denenir.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]   // 503 "high demand" — kullanıcının aldığı
    [InlineData(HttpStatusCode.BadGateway, true)]           // 502
    [InlineData(HttpStatusCode.GatewayTimeout, true)]       // 504
    [InlineData(HttpStatusCode.InternalServerError, true)]  // 500
    [InlineData(HttpStatusCode.RequestTimeout, true)]       // 408
    [InlineData(HttpStatusCode.TooManyRequests, false)]     // 429 = KOTA: tekrarlamak kotayı yakar
    [InlineData(HttpStatusCode.Unauthorized, false)]        // 401 anahtar — tekrarla düzelmez
    [InlineData(HttpStatusCode.NotFound, false)]            // 404 model adı — tekrarla düzelmez
    public void Gecici_hata_ayrimi_dogru(HttpStatusCode kod, bool beklenen)
        => Assert.Equal(beklenen, AsistanYonlendirici.GeciciMi(kod));

    [Fact]
    public void Yeniden_deneme_beklemesi_artar_ama_kullaniciyi_bekletmez()
    {
        // Kullanıcı ekranda bekliyor: toplam ek bekleme 7 sn'yi geçmemeli (2 + 5).
        Assert.Equal(TimeSpan.FromSeconds(2), AsistanYonlendirici.BeklemeSuresi(1));
        Assert.Equal(TimeSpan.FromSeconds(5), AsistanYonlendirici.BeklemeSuresi(2));
        Assert.Equal(3, AsistanYonlendirici.AzamiDeneme);
    }

    /// <summary>Ollama yerel yanıtı OpenAI'daki choices[0] sarmalını KULLANMAZ — message.content.</summary>
    [Fact]
    public void Yerel_cevap_message_content_ten_cozulur()
    {
        const string yanit =
            """{"model":"qwen2.5-coder:7b","message":{"role":"assistant","content":"cevap metni"},"done":true}""";
        Assert.Equal("cevap metni", AsistanYonlendirici.YerelCevapCoz(yanit));

        // OpenAI biçimi buraya düşerse null döner (sessizce yanlış metin üretmez)
        Assert.Null(AsistanYonlendirici.YerelCevapCoz("""{"choices":[{"message":{"content":"x"}}]}"""));
    }

    [Fact]
    public void Yerel_istek_ozel_model_ve_taban_korunur()
    {
        using HttpRequestMessage r = AsistanYonlendirici.YerelIstek(
            "s", "codellama:13b", "http://192.168.1.50:11434");
        Assert.StartsWith("http://192.168.1.50:11434/", r.RequestUri!.ToString());

        string govde = new StreamReader(r.Content!.ReadAsStream()).ReadToEnd();
        Assert.Contains("codellama:13b", govde);
    }

    [Fact]
    public void Yerel_varsayilan_model_apache_lisansli_7b()
    {
        // Lisans kaydı (v21-S1): 3B "Qwen Research" çıktı → 7B'ye geçtik. Regresyon kapısı:
        // varsayılan yanlışlıkla 3B'ye dönerse bu test düşer.
        Assert.Equal("qwen2.5-coder:7b", AsistanYonlendirici.YerelVarsayilanModel);
        Assert.DoesNotContain("3b", AsistanYonlendirici.YerelVarsayilanModel);
    }

    [Fact]
    public void Hata_ozeti_saglayici_mesajini_ayiklar_ham_duvari_basmaz()
    {
        Assert.Equal("API key not valid",
            AsistanYonlendirici.HataOzeti("""{ "error": { "code": 400, "message": "API key not valid" } }"""));
        Assert.StartsWith("<html", AsistanYonlendirici.HataOzeti("<html>uzun hata sayfası</html>"));
    }

    // ───────────────────────── v22-S6: ön yükleme ─────────────────────────

    /// <summary>
    /// Isıtma gövdesinin AYIRT EDİCİ özellikleri (v22-S8): önek GERÇEK bir <c>user</c> mesajı olarak
    /// gider (sarmalama asıl soruyla aynı olsun — farklı olursa token dizisi baştan ayrışır ve
    /// önbellek ıskalar) ama <c>num_predict=1</c> ile: model istemi İŞLER, üretim YAPMAZ.
    /// Bu ikisinden biri kaybolursa ısıtma ya işe yaramaz ya da gerçek bir üretim turuna döner.
    /// </summary>
    [Fact]
    public void Isitma_govdesi_onegi_user_mesaji_olarak_ve_tek_token_uretimle_gonderir()
    {
        string govde = AsistanYonlendirici.YerelIsitmaGovdesi("ONEK METNI", "qwen2.5-coder:7b");

        using JsonDocument d = JsonDocument.Parse(govde);
        Assert.Equal("qwen2.5-coder:7b", d.RootElement.GetProperty("model").GetString());
        JsonElement mesaj = d.RootElement.GetProperty("messages")[0];
        Assert.Equal("user", mesaj.GetProperty("role").GetString());
        Assert.Equal("ONEK METNI", mesaj.GetProperty("content").GetString());
        Assert.Equal(1, d.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(AsistanYonlendirici.YerelBellekteKalma,
            d.RootElement.GetProperty("keep_alive").GetString()); // asıl soruya kadar bellekte kalsın
    }

    /// <summary>
    /// Isıtma uzak sağlayıcıda no-op: model sağlayıcının sunucusunda, ısıtılacak bir şey yok.
    /// OpenAI-uyumlu ayarla çağrı ağa ÇIKMAMALI — bu yüzden ulaşılamayan bir taban adresi verilse
    /// bile hatasız ve anında dönmeli.
    /// </summary>
    [Fact]
    public async Task Isitma_uzak_saglayicida_aga_cikmaz()
    {
        var yonlendirici = new AsistanYonlendirici(new DpapiSecretProtector());
        var ayar = new AsistanAyarlari(
            AsistanSaglayici.OpenAiUyumlu, "bir-model", "http://127.0.0.1:1/olmayan", "k");

        // Ağa çıksaydı bağlantı reddine takılırdı; no-op olduğu için sessizce tamamlanır.
        await yonlendirici.IsitAsync("onek", ayar, CancellationToken.None);
    }

    /// <summary>Boş önek ısıtılmaz — şema kapısı kapalıysa (sohbet) ısıtacak bir istem yoktur.</summary>
    [Fact]
    public async Task Bos_onek_isitilmaz()
    {
        var yonlendirici = new AsistanYonlendirici(new DpapiSecretProtector());
        var ayar = new AsistanAyarlari(
            AsistanSaglayici.Yerel, "qwen2.5-coder:7b", "http://127.0.0.1:1", null);

        // Ağa çıksaydı bağlantı reddine takılıp (yutulsa da) zaman harcardı; boş önekte hiç denenmez.
        await yonlendirici.IsitAsync("   ", ayar, CancellationToken.None);
    }

    /// <summary>
    /// Yerel'de Ollama KAPALIYSA ısıtma sessizce yutar — kullanıcı hiçbir şey istemediği hâlde
    /// (sekmeyi açmak yeterliydi) hata görmemeli. Asıl soru sorulduğunda aynı durum zaten yol
    /// gösteren mesajla yüzeye çıkar.
    /// </summary>
    [Fact]
    public async Task Isitma_ollama_kapaliyken_sessizce_yutar()
    {
        var yonlendirici = new AsistanYonlendirici(new DpapiSecretProtector());
        var ayar = new AsistanAyarlari(
            AsistanSaglayici.Yerel, "qwen2.5-coder:7b", "http://127.0.0.1:1", null);

        await yonlendirici.IsitAsync("onek", ayar, CancellationToken.None); // fırlatmamalı
    }

    // ───────── v22-S11: PROXY (saha bulgusu 25 Ağu 2026 — "AI çalışmıyor", HTTP 407) ─────────

    /// <summary>
    /// 🔌 YEREL istek PROXY'DEN GEÇMEMELİ. Kurumsal ağda .NET sistem proxy'sini uygular; muafiyet
    /// listesinde 127.0.0.1 yoksa Ollama'ya giden istek bile proxy'ye gidip 407 alıyordu — sahada
    /// "AI çalışmıyor" görüntüsünün sebebi buydu. Loopback'e giden istemci proxy KULLANMAMALI.
    /// </summary>
    [Fact]
    public void Loopback_istegi_proxysiz_istemciyle_gider()
    {
        using HttpRequestMessage yerel = AsistanYonlendirici.YerelIstek("s", null, null);
        Assert.True(yerel.RequestUri!.IsLoopback, "yerel uç loopback olmalı");

        using HttpRequestMessage uzak = AsistanYonlendirici.OpenAiIstek(
            "s", "m", "https://api.groq.com/openai", "k");
        Assert.False(uzak.RequestUri!.IsLoopback, "uzak uç loopback OLMAMALI");
    }

    /// <summary>
    /// 407 mesajı ham proxy HTML'i BASMAMALI; ne olduğunu ve ne yapılacağını söylemeli. Sahada
    /// kullanıcı ekranında "&lt;!DOCTYPE html …" proxy oturum sayfası çıkıyordu ve hatanın ağ
    /// katmanından geldiği hiç anlaşılmıyordu. Yerel ve bulut için AYRI yönlendirme verilir.
    /// </summary>
    [Fact]
    public void Proxy_407_mesaji_yol_gosterir_ham_html_basmaz()
    {
        string yerel = AsistanYonlendirici.ProxyMesajiTest(
            new AsistanAyarlari(AsistanSaglayici.Yerel, "m", "http://127.0.0.1:11434", null));
        Assert.Contains("407", yerel);
        // ⚠ "muafiyet" yerine "muaf": mesajda MUAFİYET büyük harfle geçiyor ve OrdinalIgnoreCase
        // Türkçe 'İ' (U+0130) ile 'i'yi EŞLEMİYOR — bu iddia o yüzden düşmüştü.
        Assert.Contains("muaf", yerel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", yerel);
        Assert.DoesNotContain("<!DOCTYPE", yerel);
        Assert.DoesNotContain("<html", yerel);

        string bulut = AsistanYonlendirici.ProxyMesajiTest(
            new AsistanAyarlari(AsistanSaglayici.OpenAiUyumlu, "m", "https://uc.ornek", "k"));
        Assert.Contains("407", bulut);
        Assert.Contains("Yerel AI", bulut); // internetsiz kaçış yolu gösterilir
        Assert.DoesNotContain("<html", bulut);
    }
}
