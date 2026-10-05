using SQLST.App.ViewModels;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 🤖 Asistan sekmesi VM akışı (v11-S1/S2) — headless: sahte servisle Sor ve SorguyuDegerlendir
/// komutlarının Show Prompt + cevap + bilgi sözleşmesi. HTTP yok.
/// </summary>
public class AsistanSekmesiTests
{
    private sealed class SahteServis : IAsistanServisi
    {
        public string? SonIstem;
        public int IsitmaSayisi;
        public string? IsitilanOnek;
        public AsistanAyarlari? IsitmaAyari;
        public AsistanCevabi Donecek = AsistanCevabi.Basari("model cevabı");
        public Task<AsistanCevabi> SorAsync(string istem, AsistanAyarlari a, CancellationToken ct)
        {
            SonIstem = istem;
            return Task.FromResult(Donecek);
        }

        public Task IsitAsync(string onek, AsistanAyarlari ayarlar, CancellationToken ct)
        {
            IsitmaSayisi++;
            IsitilanOnek = onek;
            IsitmaAyari = ayarlar;
            return Task.CompletedTask;
        }
    }

    /// <summary>Cevabı DIŞARIDAN tetiklenene kadar bekletir — "düşünüyor" balonunu uçuşta gözlemlemek için.</summary>
    private sealed class GecikenServis : IAsistanServisi
    {
        public readonly TaskCompletionSource<AsistanCevabi> Kapi =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AsistanCevabi> SorAsync(string istem, AsistanAyarlari a, CancellationToken ct) => Kapi.Task;
        public Task IsitAsync(string onek, AsistanAyarlari ayarlar, CancellationToken ct) => Task.CompletedTask;
    }

    private static (AsistanSekmesiViewModel Vm, SahteServis Servis) Kur(
        string? editorMetni, string? sonHata = null)
    {
        var servis = new SahteServis();
        var onbellek = new SemaOnbellegi
        {
            Nesneler = [new("Db", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new("Id", "int", false, true)], [])],
            YuklenmeZamaniUtc = DateTime.UtcNow,
        };
        var vm = new AsistanSekmesiViewModel(
            servis,
            () => new AsistanAyarlari(AsistanSaglayici.OpenAiUyumlu, "bir-model", "https://uc.ornek", "k"),
            () => Task.FromResult<SemaOnbellegi?>(onbellek),
            () => MotorTuru.Mssql,
            () => editorMetni,
            () => sonHata);
        return (vm, servis);
    }

    [Fact]
    public async Task Hata_cozdur_hata_ve_sorguyu_tasir_hata_yoksa_reddeder()
    {
        // v11-S4: tek tık — hata + sorgu istemde; hata yoksa modele gidilmez.
        (AsistanSekmesiViewModel vm, SahteServis servis) =
            Kur("SELECT * FROM dbo.Yok", sonHata: "Invalid object name 'dbo.Yok'.");
        await vm.HataCozdurCommand.ExecuteAsync(null);
        Assert.Contains("HATA MESAJI:", servis.SonIstem!);
        Assert.Contains("Invalid object name", servis.SonIstem!);
        Assert.Contains("HATA VEREN SORGU:", servis.SonIstem!);

        (AsistanSekmesiViewModel temiz, SahteServis servis2) = Kur("SELECT 1", sonHata: null);
        await temiz.HataCozdurCommand.ExecuteAsync(null);
        Assert.Null(servis2.SonIstem);
        Assert.Contains("Çözülecek hata yok", temiz.Bilgi);
    }

    [Fact]
    public async Task Sorgu_acikla_aciklama_istemi_kurar()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT a FROM b");
        await vm.SorguAciklaCommand.ExecuteAsync(null);
        Assert.Contains("AÇIKLANACAK SORGU:", servis.SonIstem!);
        Assert.Contains("SELECT a FROM b", servis.SonIstem!);
    }

    /// <summary>
    /// Show Prompt rayı: kullanıcının gördüğü istem, modele GİDEN metnin ta kendisidir.
    /// v22-S7: eski hâli "editördeki sorgu eklendi (varsayılan AÇIK)" ve "şema hep gider"
    /// varsayımlarını doğruluyordu; ikisi de bilinçli olarak değişti (başka sekme sızmasın +
    /// şema kapısı). Testin ASIL sözleşmesi — gölge kopya yok — burada korunuyor.
    /// </summary>
    [Fact]
    public async Task Sor_show_prompt_giden_metnin_kendisidir()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT 1");
        vm.EditordekiSorguyuEkle = true;         // sorgu bağlamı bilinçli olarak açık
        vm.Soru = "bu sorgudaki kolonlar ne?";   // veri sorusu → şema da gider

        await vm.SorCommand.ExecuteAsync(null);

        Assert.Equal(servis.SonIstem, vm.IstemOnizleme); // gölge kopya yok — birebir aynı
        Assert.Contains("dbo.Musteri", vm.IstemOnizleme);
        Assert.Contains("SELECT 1", vm.IstemOnizleme);
        Assert.Equal("model cevabı", vm.Cevap);
    }

    [Fact]
    public async Task Sorguyu_degerlendir_tek_tik_soru_istemez()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT * FROM dbo.Musteri");

        await vm.SorguyuDegerlendirCommand.ExecuteAsync(null);

        Assert.Contains("DEĞERLENDİRİLECEK SORGU:", servis.SonIstem!);
        Assert.Contains("SELECT * FROM dbo.Musteri", servis.SonIstem!);
        Assert.Equal("model cevabı", vm.Cevap);
    }

    [Fact]
    public async Task Sorgu_yoksa_degerlendirme_kibarca_reddedilir()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur(editorMetni: null);

        await vm.SorguyuDegerlendirCommand.ExecuteAsync(null);

        Assert.Null(servis.SonIstem); // modele hiç gidilmedi
        Assert.Contains("Değerlendirilecek sorgu yok", vm.Bilgi);
    }

    [Fact]
    public async Task Cevaptaki_sorgu_sekmede_ac_koprusune_gider()
    {
        // v11-S3: cevapta ``` bloklu sorgu → düğme görünür → köprü (başlık, sorgu) alır; çalıştırma YOK.
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT 1");
        servis.Donecek = AsistanCevabi.Basari("Al:\n```sql\nSELECT Id FROM dbo.Musteri\n```");
        (string Baslik, string Sorgu)? acilan = null;
        vm.SekmeyeAc = (b, s) => acilan = (b, s);
        vm.Soru = "müşteri idlerini getir";

        await vm.SorCommand.ExecuteAsync(null);
        Assert.True(vm.SekmedeAcGorunur);

        vm.SekmedeAcCommand.Execute(null);
        Assert.Equal(("asistan.sql", "SELECT Id FROM dbo.Musteri"), acilan);
    }

    [Fact]
    public async Task Limit_cevabi_bekleyin_bilgisine_cevrilir()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT 1");
        servis.Donecek = AsistanCevabi.Limit();
        vm.Soru = "x";

        await vm.SorCommand.ExecuteAsync(null);

        Assert.Contains("Ücretsiz katman limiti", vm.Bilgi);
    }

    [Fact]
    public async Task Istek_sirasinda_dusunuyor_balonu_gorunur_cevap_gelince_kalkar() // 2026-07-28
    {
        var servis = new GecikenServis();
        var onbellek = new SemaOnbellegi { Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow };
        var vm = new AsistanSekmesiViewModel(
            servis,
            () => new AsistanAyarlari(AsistanSaglayici.OpenAiUyumlu, "m", "https://uc.ornek", "k"),
            () => Task.FromResult<SemaOnbellegi?>(onbellek),
            () => MotorTuru.Mssql,
            () => "SELECT 1");
        vm.Soru = "bu ne?";

        Task calisan = vm.SorCommand.ExecuteAsync(null); // henüz beklemiyoruz — istek uçuşta

        // Uçuştayken: cevabın çıkacağı yerde AI logolu "düşünüyor" balonu (asistan tarafı, metin yok)
        Assert.Contains(vm.Mesajlar, m => m.Dusunuyor && !m.KullaniciMi && m.Metin.Length == 0);

        servis.Kapi.SetResult(AsistanCevabi.Basari("cevap")); // model döndü
        await calisan;

        Assert.DoesNotContain(vm.Mesajlar, m => m.Dusunuyor);                    // bekleme balonu kalktı
        Assert.Contains(vm.Mesajlar, m => !m.KullaniciMi && m.Metin == "cevap"); // gerçek cevap balonu geldi
    }

    // ───────────────────────── v22-S6: bekleme sayacı + ön yükleme ─────────────────────────

    /// <summary>
    /// Sayaç metni (saf): 60 sn'ye kadar salt saniye, sonrası dk+sn. Sınır 60'ta DEĞİŞMELİ —
    /// "83 sn" okunmuyor, uzun beklemeyi de gizliyordu (kullanıcı bulgusu: "cevap vermiyor").
    /// </summary>
    [Theory]
    [InlineData(0, "0 sn")]
    [InlineData(7, "7 sn")]
    [InlineData(59, "59 sn")]
    [InlineData(60, "1 dk 00 sn")]
    [InlineData(65, "1 dk 05 sn")]
    [InlineData(125, "2 dk 05 sn")]
    public void Sure_metni_dakikaya_gecince_bicim_degistirir(int saniye, string beklenen)
        => Assert.Equal(beklenen, AsistanSekmesiViewModel.SureMetni(TimeSpan.FromSeconds(saniye)));

    /// <summary>
    /// Bekleme SÜRERKEN sayaç dolu, iş bitince BOŞ. Boş olması arayüzün sayacı gizlemesi demek —
    /// cevap geldikten sonra ekranda "hayalet süre" kalmamalı.
    /// </summary>
    [Fact]
    public async Task Bekleme_sirasinda_sayac_dolar_bitince_temizlenir()
    {
        var servis = new GecikenServis();
        var onbellek = new SemaOnbellegi { Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow };
        var vm = new AsistanSekmesiViewModel(
            servis,
            () => new AsistanAyarlari(AsistanSaglayici.OpenAiUyumlu, "m", "https://uc.ornek", "k"),
            () => Task.FromResult<SemaOnbellegi?>(onbellek),
            () => MotorTuru.Mssql,
            () => "SELECT 1");
        vm.Soru = "bu ne?";

        Task calisan = vm.SorCommand.ExecuteAsync(null); // istek uçuşta

        // Sayaç ilk değerini TİK BEKLEMEDEN basar (aksi hâlde ilk saniye boyunca boş görünürdü).
        // "0 sn" diye SABİTLENMEZ: yavaş koşucuda assert'e gelene kadar bir tik geçip "1 sn" olabilir —
        // sözleşme "sayaç dolu ve saniye biçiminde", tam değer değil.
        Assert.Matches(@"^\d+ sn$", vm.BekleyenSure);

        servis.Kapi.SetResult(AsistanCevabi.Basari("cevap"));
        await calisan;

        Assert.Equal("", vm.BekleyenSure); // iş bitti → sayaç gizlenir
    }

    // ───────────── v22-S7: gönderdiğin metne sadık kal + şema kapısı ─────────────

    /// <summary>
    /// 🔒 BAŞKA SEKME SIZMASIN (kullanıcı kararı 24 Ağu 2026). Editördeki sorgu "en son uğranan"
    /// sorgu sekmesinden gelir — kullanıcı Asistan sekmesindeyken baktığı sekme bile değildir.
    /// Anahtar KAPALIYKEN (yeni varsayılan) o metin isteme HİÇ girmemeli. Eski kodda ikinci ve
    /// daha sinsi bir sızıntı vardı: anahtar kapalı olsa bile editör metni tablo süzmesinde
    /// kullanılıyordu, yani hangi tablonun TAM KOLON DÖKÜMÜNÜN gideceğine o sekme karar veriyordu.
    /// </summary>
    [Fact]
    public async Task Anahtar_kapaliyken_editordeki_sorgu_isteme_hic_girmez()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT * FROM dbo.Musteri");
        Assert.False(vm.EditordekiSorguyuEkle); // v22-S7: varsayılan KAPALI

        vm.Soru = "en çok siparişi olan müşteriler"; // veri sorusu → şema gitmeli
        await vm.SorCommand.ExecuteAsync(null);

        Assert.DoesNotContain("SELECT * FROM dbo.Musteri", servis.SonIstem!); // sorgu metni yok
        Assert.DoesNotContain("ÜZERİNDE ÇALIŞTIĞI SORGU", servis.SonIstem!);
        // Sinsi sızıntı: editördeki tablo adı yüzünden dbo.Musteri'nin KOLONLARI gitmemeli.
        Assert.DoesNotContain("Id int PK", servis.SonIstem!);
    }

    /// <summary>Anahtar AÇIKSA sorgu bilinçli olarak eklenir — özellik kaldırılmadı, varsayılanı değişti.</summary>
    [Fact]
    public async Task Anahtar_acikken_editordeki_sorgu_bilincli_olarak_eklenir()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT * FROM dbo.Musteri");
        vm.EditordekiSorguyuEkle = true;

        vm.Soru = "bu sorgu ne yapar?";
        await vm.SorCommand.ExecuteAsync(null);

        Assert.Contains("SELECT * FROM dbo.Musteri", servis.SonIstem!);
        Assert.Contains("ÜZERİNDE ÇALIŞTIĞI SORGU", servis.SonIstem!);
    }

    /// <summary>
    /// 🚪 "merhaba" istemi ŞEMASIZ ve KÜÇÜK olmalı — kullanıcının 90 saniye beklemesinin sebebi
    /// tam olarak buraya giren 1.375 token'lık tablo listesiydi.
    /// </summary>
    [Fact]
    public async Task Merhaba_istemi_semasiz_ve_kucuk_gider()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT * FROM dbo.Musteri");
        vm.Soru = "merhaba";

        await vm.SorCommand.ExecuteAsync(null);

        Assert.DoesNotContain("VERİTABANI ŞEMASI", servis.SonIstem!);
        Assert.DoesNotContain("dbo.Musteri", servis.SonIstem!);
        Assert.True(servis.SonIstem!.Length < 400, $"istem {servis.SonIstem.Length} karakter");
        Assert.Equal(servis.SonIstem, vm.IstemOnizleme); // Show Prompt rayı bozulmadı
    }

    /// <summary>Soruda TABLO ADI geçiyorsa kapı devre dışı — kullanıcı zaten o tabloyu işaret etmiştir.</summary>
    [Fact]
    public async Task Soruda_tablo_adi_gecerse_sema_kapisi_devre_disi()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur(editorMetni: null);
        vm.Soru = "Musteri hakkında ne söyleyebilirsin"; // veri işareti YOK ama tablo adı VAR

        await vm.SorCommand.ExecuteAsync(null);

        Assert.Contains("VERİTABANI ŞEMASI", servis.SonIstem!);
        Assert.Contains("dbo.Musteri", servis.SonIstem!);
    }

    /// <summary>Ön yükleme çağrısı servise GİDER ve o anki ayarı taşır (Yerel süzmesi servis tarafında).</summary>
    [Fact]
    public async Task Isitma_servise_gecerli_ayarla_gider()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur(editorMetni: null);

        await vm.IsitAsync();

        Assert.Equal(1, servis.IsitmaSayisi);
        Assert.Equal(AsistanSaglayici.OpenAiUyumlu, servis.IsitmaAyari!.Saglayici);
    }

    /// <summary>
    /// ⚠ BAYT BAYT AYNILIK — v22-S8'in TAŞIYICI TESTİ (KDS S34'ün aynı adlı testinin karşılığı).
    /// Isıtılan önek, gerçek istemin BAŞLANGICI olmazsa Ollama önbelleği ıskalar; ürün yine DOĞRU
    /// çalışır, yalnız ~70 sn yavaşlar — yani hatayı kimse fark etmez. Sessiz kaybı ancak test tutar.
    /// Değişken parçaların (kolon dökümü, editör sorgusu, soru) hepsi eklenmişken bile önek başta
    /// kalmalı: sıralamayı bozan bir düzenleme burada kırmızı verir.
    /// </summary>
    [Fact]
    public async Task Isitma_onegi_gercek_istemin_BASLANGICIYLA_birebir_ayni()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur("SELECT * FROM dbo.Musteri");
        await vm.IsitAsync();
        string onek = servis.IsitilanOnek!;
        Assert.NotEmpty(onek);

        // En ZENGİN istem: şema + ilgili tablo kolonları + editör sorgusu + soru
        vm.EditordekiSorguyuEkle = true;
        vm.Soru = "Musteri tablosundaki kolonları listele";
        await vm.SorCommand.ExecuteAsync(null);

        Assert.StartsWith(onek, servis.SonIstem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Isıtılan önek KOMPAKT şemayı taşımalı (tablo ADLARI) — kolon dökümünü DEĞİL. Kolon dökümü
    /// soruya göre değişir; önekte olursa önek her soruda başkalaşır ve ısıtma işlevsiz kalır.
    /// </summary>
    [Fact]
    public async Task Isitilan_onek_kompakt_semadir_kolon_dokumu_degil()
    {
        (AsistanSekmesiViewModel vm, SahteServis servis) = Kur(editorMetni: null);

        await vm.IsitAsync();

        Assert.Contains("dbo.Musteri", servis.IsitilanOnek!);   // tablo ADI önekte
        Assert.DoesNotContain("Id int PK", servis.IsitilanOnek!); // kolon dökümü önekte DEĞİL
    }
}
