using System.Collections.ObjectModel;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// B2/A3 — arama sekmesi ViewModel'i. <b>Bu sınıfın var olma sebebi somut:</b> V5-S2'de
/// sevk edilen sürümde sekme, AÇILDIĞI ANDAKİ veritabanını yakalayıp saklıyordu ve sekme
/// yeniden kullanıldığı için ağaçtan başka bir veritabanı seçmek arama kapsamını hiç
/// değiştirmiyordu. Lehçe ve çözümleyici katmanları iyi test edildiğinden hata yalnız
/// elle kullanımda görüldü — kullanıcı buldu. Buradaki testler o sınıfı kapatır.
/// </summary>
public class AramaSekmesiTests
{
    private static AramaSekmesiViewModel Kur(
        SahteExecutor executor, ConnectionProfile? profil, string? secilenVeritabani = "ilk_db",
        ObservableCollection<string>? veritabanlari = null,
        AramaSekmesiViewModel.MongoAramaDelegesi? mongoArayici = null)
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        return new AramaSekmesiViewModel(
            executor,
            () => profil,
            saglayici.Getir,
            veritabanlari ?? ["ilk_db", "ikinci_db"],
            secilenVeritabani,
            mongoArayici);
    }

    [Fact]
    public async Task Arama_SECILI_veritabaninda_kosar()
    {
        var executor = new SahteExecutor();
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.Equal("ilk_db", executor.SonVeritabani);
    }

    [Fact]
    public async Task Veritabani_DEGISINCE_arama_yeni_veritabaninda_kosar()
    {
        // ŞİPPED HATA: sekme açılıştaki veritabanını saklıyordu; bu test o sürümde düşerdi.
        var executor = new SahteExecutor();
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        sekme.SecilenVeritabani = "ikinci_db";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.Equal("ikinci_db", executor.SonVeritabani);
        Assert.Equal(2, executor.Cagrilar.Count);
    }

    [Fact]
    public async Task Veritabani_degisince_ONCEKI_SONUCLAR_TEMIZLENIR()
    {
        // Bayat sonuç bırakmak yanlış cevaptan beterdir: kullanıcı doğru veritabanına
        // baktığını sanır.
        var executor = new SahteExecutor
        {
            Sonuc = SahteExecutor.AramaSonucu(("dbo", "vw_test", "V", "SELECT kdv FROM t")),
        };
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);
        Assert.NotEmpty(sekme.Sonuclar);

        sekme.SecilenVeritabani = "ikinci_db";

        Assert.Empty(sekme.Sonuclar);
        Assert.Null(sekme.SeciliSonuc);
    }

    [Fact]
    public async Task Profil_DEGISINCE_yeni_baglantida_kosar()
    {
        // Profil de saklanmıyor, her aramada taze okunuyor: aynı bayatlama sınıfı.
        var executor = new SahteExecutor();
        ConnectionProfile? aktif = Profiller.Yap(ad: "birinci");

        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        var sekme = new AramaSekmesiViewModel(
            executor, () => aktif, saglayici.Getir, ["ilk_db"], "ilk_db") { Aranan = "kdv" };

        await sekme.AraCommand.ExecuteAsync(null);
        Assert.Equal("birinci", executor.SonProfil?.Ad);

        aktif = Profiller.Yap(ad: "ikinci");
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.Equal("ikinci", executor.SonProfil?.Ad);
    }

    [Fact]
    public async Task Motor_degisince_O_MOTORUN_sorgusu_gonderilir()
    {
        // Lehçe de saklanmıyor: PG'ye geçilince MSSQL katalog sorgusu gitmemeli.
        var executor = new SahteExecutor();
        ConnectionProfile? aktif = Profiller.Yap(MotorTuru.Mssql);

        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        var sekme = new AramaSekmesiViewModel(
            executor, () => aktif, saglayici.Getir, ["db"], "db") { Aranan = "kdv" };

        await sekme.AraCommand.ExecuteAsync(null);
        Assert.Contains("sys.sql_modules", executor.SonSql, StringComparison.Ordinal);

        aktif = Profiller.Yap(MotorTuru.Postgres);
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.Contains("pg_get_functiondef", executor.SonSql, StringComparison.Ordinal);
    }

    // ── B5/A6: MongoDB kendi arama yolundan geçer ──────────────────────────

    [Fact]
    public async Task Mongoda_SQL_sorgusu_GONDERILMEZ()
    {
        // Mongo ayrı ailedir: katalog SQL'i gönderilmemeli, kendi delegesi çağrılmalı.
        var executor = new SahteExecutor();
        bool cagrildi = false;

        AramaSekmesiViewModel sekme = Kur(
            executor, Profiller.Yap(MotorTuru.Mongo),
            mongoArayici: (_, _, _, _, _) =>
            {
                cagrildi = true;
                return Task.FromResult<IReadOnlyList<MongoMetinArayici.Bulgu>>([]);
            });

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.True(cagrildi);
        Assert.Empty(executor.Cagrilar);          // SQL executor'a HİÇ gidilmedi
    }

    [Fact]
    public async Task Mongo_bulgulari_sonuc_listesine_donusur()
    {
        var executor = new SahteExecutor();
        AramaSekmesiViewModel sekme = Kur(
            executor, Profiller.Yap(MotorTuru.Mongo),
            mongoArayici: (_, _, _, _, _) => Task.FromResult<IReadOnlyList<MongoMetinArayici.Bulgu>>(
            [
                new("demo", "siparis.kdv_idx", SemaNesneTuru.Index, "{\n  \"key\": { \"kdv\": 1 }\n}"),
                // NOT: gövdede aranan metin GERÇEKTEN geçmeli — ViewModel eşleşmeyen
                // bulguyu eler (ilk yazımda pipeline boştu ve test haklı olarak düştü).
                new("demo", "vw_kdv", SemaNesneTuru.View,
                    "{\n  \"viewOn\": \"siparis\",\n  \"pipeline\": [ { \"$match\": { \"kdv\": 18 } } ]\n}"),
            ]));

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.Equal(2, sekme.Sonuclar.Count);
        Assert.Contains(sekme.Sonuclar, s => s.Tur == SemaNesneTuru.Index);
        Assert.Contains(sekme.Sonuclar, s => s.Tur == SemaNesneTuru.View);
        Assert.All(sekme.Sonuclar, s => Assert.NotEmpty(s.Eslesmeler));
    }

    [Fact]
    public async Task Mongo_kapsam_metni_FARKLI_seyleri_anlatir()
    {
        // Kullanıcı "aradım, yok" derken neyin aranmadığını bilmeli: Mongo'da belge
        // verisi ve koleksiyon adları taranmıyor.
        AramaSekmesiViewModel mongo = Kur(new SahteExecutor(), Profiller.Yap(MotorTuru.Mongo));
        AramaSekmesiViewModel mssql = Kur(new SahteExecutor(), Profiller.Yap());

        Assert.Contains("index", mongo.Kapsam, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Belge verisi", mongo.Kapsam, StringComparison.Ordinal);
        Assert.Contains("stored procedure", mssql.Kapsam, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Mongo_koprusu_yoksa_kullaniciya_SOYLENIR()
    {
        var executor = new SahteExecutor();
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap(MotorTuru.Mongo));

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.NotNull(sekme.Hata);
        Assert.Empty(executor.Cagrilar);
    }

    [Fact]
    public async Task Baglanti_yoksa_arama_YAPILMAZ()
    {
        var executor = new SahteExecutor();
        AramaSekmesiViewModel sekme = Kur(executor, profil: null);

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.Empty(executor.Cagrilar);
        Assert.NotNull(sekme.Hata);
    }

    [Fact]
    public async Task Bos_arama_metni_sunucuya_GITMEZ()
    {
        var executor = new SahteExecutor();
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());

        sekme.Aranan = "   ";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.Empty(executor.Cagrilar);
    }

    [Fact]
    public async Task Kapsam_metni_secili_veritabanini_YANSITIR()
    {
        // Kullanıcı "bulunamadı"yı yanlış yorumlamasın diye kapsam ekranda yazar —
        // ve DB değişince güncellenmeli.
        var executor = new SahteExecutor();
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());

        Assert.Contains("ilk_db", sekme.Kapsam, StringComparison.Ordinal);
        Assert.Contains("ilk_db", sekme.Baslik, StringComparison.Ordinal);

        sekme.SecilenVeritabani = "ikinci_db";

        Assert.Contains("ikinci_db", sekme.Kapsam, StringComparison.Ordinal);
        Assert.Contains("ikinci_db", sekme.Baslik, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Tavana_takilinca_kullaniciya_SOYLENIR()
    {
        // Sessizce kırpmak yanlış cevaptan beterdir: kullanıcı eksik listeyi TAM sanar.
        var executor = new SahteExecutor
        {
            Sonuc = SahteExecutor.AramaSonucu(
                [.. Enumerable.Range(0, ILehce.AramaTavani)
                    .Select(i => ("dbo", $"vw_{i}", "V", "SELECT kdv FROM t"))]),
        };
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.True(sekme.TavanaTakildi);
        Assert.Contains("ilk", sekme.Ozet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tavanin_altinda_UYARI_CIKMAZ()
    {
        var executor = new SahteExecutor
        {
            Sonuc = SahteExecutor.AramaSonucu(("dbo", "vw_test", "V", "SELECT kdv FROM t")),
        };
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.False(sekme.TavanaTakildi);
        Assert.DoesNotContain("⚠", sekme.Ozet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sunucu_hatasi_kullaniciya_YANSITILIR()
    {
        var executor = new SahteExecutor
        {
            Sonuc = new QueryResult { Hata = new SqlHata("izin yok", 229, 1, 14) },
        };
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        Assert.NotNull(sekme.Hata);
        Assert.Contains("izin yok", sekme.Hata!, StringComparison.Ordinal);
        Assert.Empty(sekme.Sonuclar);
    }

    [Fact]
    public async Task Tanim_sekmede_acilirken_ARAMANIN_veritabani_kullanilir()
    {
        // Sonuca çift tıklayınca açılan sekme, varsayılana değil aramanın yapıldığı
        // veritabanına bağlanmalı.
        var executor = new SahteExecutor
        {
            Sonuc = SahteExecutor.AramaSonucu(("dbo", "vw_test", "V", "satir1\nkdv burada\nsatir3")),
        };
        AramaSekmesiViewModel sekme = Kur(executor, Profiller.Yap());
        sekme.SecilenVeritabani = "ikinci_db";

        (string Ad, string Tanim, int Satir)? acilan = null;
        sekme.TanimiSekmedeAc = (ad, tanim, satir) => acilan = (ad, tanim, satir);

        sekme.Aranan = "kdv";
        await sekme.AraCommand.ExecuteAsync(null);

        AramaSonucu sonuc = Assert.Single(sekme.Sonuclar);
        sekme.EslesmeyeGitCommand.Execute(sonuc.Eslesmeler[0]);

        Assert.NotNull(acilan);
        Assert.Equal("dbo.vw_test", acilan!.Value.Ad);
        Assert.Equal(2, acilan.Value.Satir);          // "kdv burada" ikinci satır
        Assert.Contains("kdv", acilan.Value.Tanim, StringComparison.Ordinal);
    }
}
