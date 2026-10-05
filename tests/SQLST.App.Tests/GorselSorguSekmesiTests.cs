using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v6-S1 — Görsel Sorgu sekmesinin WPF'siz davranışı: tablo ekleme/dedupe, düğme kapısı,
/// "Script'e aç" köprüsü. Tuval/sürükleme render'ı gözle doğrulanır (proje kuralı); burada
/// ViewModel MANTIĞI sabitlenir.
/// </summary>
public class GorselSorguSekmesiTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static SemaNesnesi Tablo(string ad, params string[] kolonlar) =>
        new("db", "satis", ad, SemaNesneTuru.Tablo,
            [.. kolonlar.Select(k => new SemaKolonu(k, "int", false, false))], []);

    private static GorselSorguSekmesiViewModel Yeni(ILehce? lehce = null) =>
        new(() => lehce ?? Mssql);

    [Fact]
    public void Bos_tuvalde_script_URETILEMEZ()
    {
        var sekme = Yeni();
        Assert.False(sekme.ScriptUretilebilir);   // düğme pasif
    }

    [Fact]
    public void Tablo_eklenince_script_uretilebilir_olur()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri"), 10, 20);

        Assert.True(sekme.ScriptUretilebilir);
        Assert.Single(sekme.Kutular);
        Assert.Equal(10, sekme.Kutular[0].X);
        Assert.Equal(20, sekme.Kutular[0].Y);
    }

    [Fact]
    public void Ayni_tablo_IKI_KEZ_eklenmez()
    {
        // S1'de takma ad yok → FROM a CROSS JOIN a anlamsız olurdu. Tekrar sessizce yok sayılır.
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri"), 0, 0);
        sekme.TabloEkle(Tablo("Musteri"), 100, 100);

        Assert.Single(sekme.Kutular);
        Assert.Contains("zaten tuvalde", sekme.Bilgi, StringComparison.Ordinal);
    }

    [Fact]
    public void Kutu_silinince_kapi_kapanir()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri"), 0, 0);
        sekme.KutuSil(sekme.Kutular[0]);

        Assert.Empty(sekme.Kutular);
        Assert.False(sekme.ScriptUretilebilir);
    }

    [Fact]
    public void ScripteAc_uretilen_SQLi_koprüden_gonderir()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri"), 0, 0);
        sekme.TabloEkle(Tablo("Siparis"), 250, 0);

        string? gelenBaslik = null, gelenSql = null;
        sekme.SekmeyeAc = (b, s) => { gelenBaslik = b; gelenSql = s; };

        sekme.ScripteAcCommand.Execute(null);

        Assert.Equal("görsel-sorgu.sql", gelenBaslik);
        // İki tablo → CROSS JOIN'li SELECT (S1 davranışı), MSSQL köşeli tırnak.
        Assert.Equal(
            "SELECT *\nFROM [satis].[Musteri]\n    CROSS JOIN [satis].[Siparis];",
            gelenSql);
    }

    [Fact]
    public void Calistir_uretilen_SQLi_calistirma_koprusunden_gonderir()
    {
        // YEDEK yol (2026-07-23): SorguCalistirici bağlı DEĞİLSE eski "sekmede çalıştır" köprüsü işler.
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri"), 0, 0);

        string? gelenSql = null;
        sekme.SekmeyeAcVeCalistir = (_, s) => gelenSql = s;
        sekme.CalistirCommand.Execute(null);

        Assert.Equal("SELECT *\nFROM [satis].[Musteri];", gelenSql);
    }

    [Fact]
    public async Task Calistir_yerinde_sonucu_grid_verisine_cevirir()
    {
        // Yerinde sonuç (kullanıcı isteği 2026-07-23): script sekmesi açılmaz, sonuç bu sekmede.
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri", "Id"), 0, 0);

        bool eskiYolCagrildi = false;
        sekme.SekmeyeAcVeCalistir = (_, _) => eskiYolCagrildi = true;
        sekme.SorguCalistirici = (_, _) => Task.FromResult(new QueryResult
        {
            Basarili = true,
            ToplamSatir = 2,
            ResultSetler =
            [
                new ResultSetData
                {
                    Kolonlar = [new KolonBilgisi("Id", "int")],
                    Satirlar = [[1], [2]],
                },
            ],
        });

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)sekme.CalistirCommand).ExecuteAsync(null);

        Assert.False(eskiYolCagrildi);                      // script sekmesi AÇILMADI
        Assert.NotNull(sekme.SonucGorunum);
        Assert.Equal(2, sekme.SonucGorunum!.Count);         // DataView.Count = satır sayısı (CS8602'siz)
        Assert.StartsWith("2", sekme.SonucBilgi);           // "2 satır · ..."
    }

    [Fact]
    public async Task Calistir_hatada_grid_yerine_mesaj_gosterir()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri"), 0, 0);
        sekme.SorguCalistirici = (_, _) => Task.FromResult(new QueryResult
        {
            Hata = new SqlHata("Tablo yok", 208, 1, 16),
        });

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)sekme.CalistirCommand).ExecuteAsync(null);

        Assert.Null(sekme.SonucGorunum);
        Assert.Contains("Tablo yok", sekme.SonucBilgi);
    }

    [Fact]
    public void Bos_tuvalde_Calistir_koprüyü_CAGIRMAZ()
    {
        var sekme = Yeni();
        bool cagrildi = false;
        sekme.SekmeyeAcVeCalistir = (_, _) => cagrildi = true;

        sekme.CalistirCommand.Execute(null);

        Assert.False(cagrildi);
    }

    [Fact]
    public void Lehce_yoksa_script_uretilmez_ve_kullaniciya_soylenir()
    {
        // Profil kapanmış / MongoDB'ye geçilmiş: lehçe null döner.
        var sekme = new GorselSorguSekmesiViewModel(() => null);
        sekme.TabloEkle(Tablo("Musteri"), 0, 0);

        bool koprüCagrildi = false;
        sekme.SekmeyeAc = (_, _) => koprüCagrildi = true;

        sekme.ScripteAcCommand.Execute(null);

        Assert.False(koprüCagrildi);
        Assert.Contains("SQL profili yok", sekme.Bilgi, StringComparison.Ordinal);
    }

    // ── S2: bağlantılar (JOIN) ──────────────────────────────────────────────

    /// <summary>Siparis.MusteriId → Musteri.Id FK'si döndüren sahte getirici.</summary>
    private static GorselSorguSekmesiViewModel YeniFkli() => new(
        () => Mssql,
        (_, _) => Task.FromResult<IReadOnlyList<YabanciAnahtar>>(
            [new YabanciAnahtar("satis", "Siparis", ["MusteriId"], "satis", "Musteri", ["Id"])]));

    [Fact]
    public async Task Bagla_FK_varsa_ON_kolonlarini_ONERIR()
    {
        var sekme = YeniFkli();
        sekme.TabloEkle(Tablo("Musteri", "Id"), 0, 0);
        sekme.TabloEkle(Tablo("Siparis", "MusteriId"), 300, 0);

        GorselBaglanti? bag = await sekme.BaglaAsync(sekme.Kutular[0], sekme.Kutular[1]);

        Assert.NotNull(bag);
        Assert.Single(sekme.Baglantilar);
        KolonEsiGorunumu es = bag!.Kolonlar.Single(k => k.Dolu);
        Assert.Equal("Id", es.SolKolon);          // sol = Musteri (FK hedefi)
        Assert.Equal("MusteriId", es.SagKolon);   // sag = Siparis (FK kaynağı)
    }

    [Fact]
    public async Task Ayni_cift_IKI_KEZ_baglanmaz_ve_kendine_baglanmaz()
    {
        var sekme = YeniFkli();
        sekme.TabloEkle(Tablo("Musteri", "Id"), 0, 0);
        sekme.TabloEkle(Tablo("Siparis", "MusteriId"), 300, 0);

        await sekme.BaglaAsync(sekme.Kutular[0], sekme.Kutular[1]);
        Assert.Null(await sekme.BaglaAsync(sekme.Kutular[1], sekme.Kutular[0])); // ters yön de mükerrer
        Assert.Null(await sekme.BaglaAsync(sekme.Kutular[0], sekme.Kutular[0])); // kendine
        Assert.Single(sekme.Baglantilar);
    }

    [Fact]
    public async Task Kutu_silinince_bagli_baglantilar_da_kalkar()
    {
        var sekme = YeniFkli();
        sekme.TabloEkle(Tablo("Musteri", "Id"), 0, 0);
        sekme.TabloEkle(Tablo("Siparis", "MusteriId"), 300, 0);
        await sekme.BaglaAsync(sekme.Kutular[0], sekme.Kutular[1]);

        sekme.KutuSil(sekme.Kutular[0]);

        Assert.Empty(sekme.Baglantilar); // bağ da gitti (dangling çizgi kalmaz)
        Assert.Single(sekme.Kutular);
    }

    [Fact]
    public async Task ScripteAc_baglantidan_JOIN_uretir()
    {
        var sekme = YeniFkli();
        sekme.TabloEkle(Tablo("Musteri", "Id"), 0, 0);
        sekme.TabloEkle(Tablo("Siparis", "MusteriId"), 300, 0);
        await sekme.BaglaAsync(sekme.Kutular[0], sekme.Kutular[1]);

        string? sql = null;
        sekme.SekmeyeAc = (_, s) => sql = s;
        sekme.ScripteAcCommand.Execute(null);

        Assert.Equal(
            "SELECT *\nFROM [satis].[Musteri]\n    INNER JOIN [satis].[Siparis] "
          + "ON [satis].[Musteri].[Id] = [satis].[Siparis].[MusteriId];",
            sql);
    }

    [Fact]
    public async Task FK_yoksa_bag_yine_kurulur_bos_satirla()
    {
        // FK getirici boş → öneri yok ama bağ kurulur; düzenleyici için boş bir satır hazır.
        var sekme = new GorselSorguSekmesiViewModel(() => Mssql,
            (_, _) => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]));
        sekme.TabloEkle(Tablo("A", "x"), 0, 0);
        sekme.TabloEkle(Tablo("B", "y"), 300, 0);

        GorselBaglanti? bag = await sekme.BaglaAsync(sekme.Kutular[0], sekme.Kutular[1]);

        Assert.NotNull(bag);
        Assert.Single(bag!.Kolonlar);          // boş satır
        Assert.False(bag.Kolonlar[0].Dolu);
    }

    // ── S3: WHERE koşulları ─────────────────────────────────────────────────

    [Fact]
    public void Kutu_kosulu_ScripteAc_WHERE_uretir()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri", "sehir"), 0, 0);
        sekme.Kutular[0].Kosullar.Add(new GorselKosulGorunumu
        {
            Kolon = "sehir", Operator = KosulOperatoru.Esit, Deger = "Ankara",
        });

        string? sql = null;
        sekme.SekmeyeAc = (_, s) => sql = s;
        sekme.ScripteAcCommand.Execute(null);

        Assert.Contains("WHERE [satis].[Musteri].[sehir] = N'Ankara';", sql!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bos_kosul_uretime_GIRMEZ()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri", "sehir"), 0, 0);
        sekme.Kutular[0].Kosullar.Add(new GorselKosulGorunumu()); // kolon boş → dolu değil

        string? sql = null;
        sekme.SekmeyeAc = (_, s) => sql = s;
        sekme.ScripteAcCommand.Execute(null);

        Assert.DoesNotContain("WHERE", sql!, StringComparison.Ordinal);
    }

    [Fact]
    public void Kosul_IS_NULL_deger_gerektirmez_ama_DOLUDUR()
    {
        var k = new GorselKosulGorunumu { Kolon = "x", Operator = KosulOperatoru.Bos };
        Assert.False(k.DegerGerekli);
        Assert.True(k.Dolu);   // değer olmadan da üretime girer

        var d = new GorselKosulGorunumu { Kolon = "x", Operator = KosulOperatoru.Esit };
        Assert.True(d.DegerGerekli);
        Assert.False(d.Dolu);  // değer gerekiyor ama boş → girmez
    }

    // ── S4: kolon seçimi ────────────────────────────────────────────────────

    [Fact]
    public void Secili_kolon_ScripteAc_SELECT_listesi_uretir()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri", "Id", "Ad", "sehir"), 0, 0);
        sekme.Kutular[0].KolonSecimleri[0].Secili = true; // Id
        sekme.Kutular[0].KolonSecimleri[2].Secili = true; // sehir

        string? sql = null;
        sekme.SekmeyeAc = (_, s) => sql = s;
        sekme.ScripteAcCommand.Execute(null);

        Assert.StartsWith("SELECT [satis].[Musteri].[Id], [satis].[Musteri].[sehir]", sql!, StringComparison.Ordinal);
    }

    [Fact]
    public void Hicbir_kolon_secili_degilse_SELECT_yildiz()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Musteri", "Id", "Ad"), 0, 0);

        string? sql = null;
        sekme.SekmeyeAc = (_, s) => sql = s;
        sekme.ScripteAcCommand.Execute(null);

        Assert.StartsWith("SELECT *", sql!, StringComparison.Ordinal);
    }

    // ── S5: Script → Görsel (TuvaliKur, tersine) ─────────────────────────────

    /// <summary>Ayrıştırılmış tabloyu ada göre çözen basit katalog.</summary>
    private static Func<CozumlenmisTablo, SemaNesnesi?> Katalog(params SemaNesnesi[] nesneler)
        => t => nesneler.FirstOrDefault(n => string.Equals(n.Ad, t.Ad, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TuvaliKur_tam_donugu_kutu_bag_kosul_secim_kurar()
    {
        // TAM DÖNGÜ: görsel → SQL (üretici) → CozumlenmisSorgu (çözümleyici) → tuval (TuvaliKur).
        SemaNesnesi m = Tablo("Musteri", "Id", "Ad", "sehir"), s = Tablo("Siparis", "MusteriId", "tutar");
        string sql = GorselSorguUretici.Uret(Mssql, [m, s],
            [new GorselJoin(m, s, JoinTuru.Left, [new GorselKolonEsi("Id", "MusteriId")])],
            [new GorselKosul(m, "sehir", KosulOperatoru.Esit, "Ankara", false)],
            [new GorselKolonAlani(m, "Ad")]);
        CozumlenmisSorgu cozum = GorselSorguCozumleyici.Coz(sql, out _)!;

        var sekme = Yeni();
        sekme.TuvaliKur(cozum, Katalog(m, s));

        Assert.Equal(2, sekme.Kutular.Count);
        GorselBaglanti bag = Assert.Single(sekme.Baglantilar);
        Assert.Equal(JoinTuru.Left, bag.Tur);
        Assert.Equal("Id", bag.Kolonlar[0].SolKolon);
        Assert.Equal("MusteriId", bag.Kolonlar[0].SagKolon);

        GorselSorguKutusu musteri = sekme.Kutular.Single(k => k.Nesne.Ad == "Musteri");
        GorselKosulGorunumu kosul = Assert.Single(musteri.Kosullar);
        Assert.Equal("sehir", kosul.Kolon);
        Assert.Equal("Ankara", kosul.Deger);
        Assert.True(musteri.KolonSecimleri.Single(c => c.Ad == "Ad").Secili);
    }

    [Fact]
    public void TuvaliKur_var_olan_tuvali_temizler()
    {
        var sekme = Yeni();
        sekme.TabloEkle(Tablo("Eski", "x"), 0, 0); // önceden bir şey var
        CozumlenmisSorgu cozum = GorselSorguCozumleyici.Coz("SELECT * FROM satis.Musteri;", out _)!;

        sekme.TuvaliKur(cozum, Katalog(Tablo("Musteri", "Id")));

        GorselSorguKutusu kutu = Assert.Single(sekme.Kutular);
        Assert.Equal("Musteri", kutu.Nesne.Ad); // Eski gitti
    }

    [Fact]
    public void TuvaliKur_semada_olmayan_tablo_atlanir_ve_bildirilir()
    {
        var sekme = Yeni();
        CozumlenmisSorgu cozum = GorselSorguCozumleyici.Coz(
            "SELECT * FROM satis.Musteri m INNER JOIN satis.Yok y ON m.id = y.mid;", out _)!;

        sekme.TuvaliKur(cozum, Katalog(Tablo("Musteri", "Id"))); // Yok katalogda değil

        Assert.Single(sekme.Kutular);            // yalnız Musteri
        Assert.Empty(sekme.Baglantilar);         // bağ da düştü (karşı uç yok)
        Assert.Contains("atlandı", sekme.Bilgi, StringComparison.Ordinal);
    }
}
