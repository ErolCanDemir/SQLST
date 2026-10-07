using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 🔎 FTS S4 yönetimi (v23-S17): envanterin yeni kolonları, doldurma/izleme/etkinlik işlemlerinin
/// ONAY ve salt-okunur kapısı, artımlı doldurmanın timestamp şartı, katalog işlemleri (birleştir
/// doğrudan · rebuild/silme script'i sekmeye), stoplist kelimeleri ve doldurma sürerken CANLI izleme
/// (bitince kendiliğinden durur). Sahte köprü — FtsSekmesiVmTests deseni; canlı tur kullanıcıda (K4).
/// </summary>
public class FtsYonetimVmTests
{
    private static QueryResult Deger(params object?[][] satirlar) => new()
    {
        Basarili = true,
        ResultSetler = [new ResultSetData { Kolonlar = [new KolonBilgisi("k", "sql_variant")], Satirlar = [.. satirlar] }],
    };

    /// <summary>16 kolonlu envanter satırı (FtsSorgulari.EnvanterSorgusu sırası).</summary>
    private static object?[] Satir(string tablo, string katalog = "Kat", int doldurmaKodu = 0, bool damga = false,
        string izleme = "AUTO", int etkin = 1, long bekleyen = 0)
        => ["dbo", tablo, katalog, "Icerik (Turkish)", izleme, doldurmaKodu == 0 ? "Boşta (dolu)" : "Tam doldurma sürüyor",
            12500L, bekleyen, 0L, "FULL", new DateTime(2026, 10, 6, 9, 0, 0),
            doldurmaKodu == 0 ? new DateTime(2026, 10, 6, 9, 4, 30) : null,
            damga ? 1 : 0, "SYSTEM", etkin, doldurmaKodu];

    private sealed class Kopru
    {
        public List<string> Gonderilen { get; } = [];
        public List<(string Baslik, string Sql)> Acilan { get; } = [];
        public List<string> Sorulan { get; } = [];
        public bool Onay { get; set; } = true;
        public Func<object?[][]> Envanter { get; set; } = () => [Satir("Belgeler"), Satir("Talepler", damga: true)];

        public Task<QueryResult> Calistir(string sql, string? _, CancellationToken __)
        {
            Gonderilen.Add(sql);
            return Task.FromResult(
                sql.Contains("IsFullTextInstalled") ? Deger([1])
                : sql.Contains("fulltext_indexes") ? Deger(Envanter())
                : sql.Contains("FULLTEXTCATALOGPROPERTY") ? Deger(["Kat", 1, 25000L, 12, "Boşta"], ["Bos", 0, 0L, 0, "Boşta"])
                : sql.Contains("fulltext_catalogs") ? Deger(["Kat"], ["Bos"])
                : sql.Contains("N'SYSTEM'") ? Deger([0, "SYSTEM", null], [5, "Ozel", 2])
                : sql.Contains("fulltext_system_stopwords") ? Deger(["bir"], ["ve"], ["ile"])
                : sql.Contains("fulltext_stopwords") ? Deger(["lst"], ["sqlst"])
                : new QueryResult { Basarili = true });
        }

        public FtsSekmesiViewModel Vm(bool saltOkunur = false) => new(
            ["KdsDemo"], "KdsDemo", _ => Task.FromResult<SemaOnbellegi?>(new SemaOnbellegi { Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow }),
            Calistir, (b, s, _) => Acilan.Add((b, s)), (_, _, _) => { },
            m => { Sorulan.Add($"{m.Baslik} {m.Mesaj}"); return Onay; }, saltOkunur)
        { CanliIzlemeSn = 1 };

        public bool AlterGitti => Gonderilen.Any(s => s.StartsWith("ALTER FULLTEXT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Envanter_yeni_kolonlari_ve_katalog_index_sayisi()
    {
        var k = new Kopru();
        FtsSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();

        FtsEnvanterSatiri b = vm.Envanter[0];
        Assert.Equal("12.500", b.OgeMetni);                              // tr-TR binlik
        Assert.Equal("FULL · 2026-10-06 09:04:30", b.SonDoldurmaMetni);  // ham tarih
        Assert.Equal("SYSTEM", b.Stoplist);
        Assert.Equal("✔", b.EtkinMetni);
        Assert.False(b.DoldurmaSuruyor);

        FtsKatalogSatiri kat = vm.KatalogDetaylari.Single(x => x.Ad == "Kat");
        Assert.Equal(2, kat.IndexSayisi);                                // envanterden sayıldı
        Assert.Equal("★ varsayılan", kat.VarsayilanMetni);
        Assert.Equal("12 MB", kat.BoyutMetni);
        Assert.Equal(0, vm.KatalogDetaylari.Single(x => x.Ad == "Bos").IndexSayisi);
    }

    [Fact]
    public async Task Tam_doldurma_onayla_gider_onaysiz_gitmez()
    {
        var k = new Kopru { Onay = false };
        FtsSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();
        vm.SeciliEnvanter = vm.Envanter[0];

        await vm.TamDoldurAsync();
        Assert.False(k.AlterGitti);
        Assert.Contains("TAM doldurma", Assert.Single(k.Sorulan));

        k.Onay = true;
        await vm.TamDoldurAsync();
        Assert.Contains("ALTER FULLTEXT INDEX ON [dbo].[Belgeler] START FULL POPULATION;", k.Gonderilen);
        Assert.Contains("Tam doldurma başladı", vm.YonetimNotu);
    }

    [Fact]
    public async Task Artimli_timestampsiz_tabloda_sunucuya_gitmez()
    {
        var k = new Kopru();
        FtsSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();

        vm.SeciliEnvanter = vm.Envanter[0];            // Belgeler: timestamp yok
        Assert.False(vm.ArtimliMumkun);
        await vm.ArtimliDoldurAsync();
        Assert.False(k.AlterGitti);
        Assert.Contains("timestamp/rowversion", vm.YonetimNotu);

        vm.SeciliEnvanter = vm.Envanter[1];            // Talepler: var
        Assert.True(vm.ArtimliMumkun);
        await vm.ArtimliDoldurAsync();
        Assert.Contains(k.Gonderilen, s => s.Contains("[Talepler] START INCREMENTAL POPULATION"));
    }

    [Fact]
    public async Task Izleme_secimi_satirdan_gelir_ve_uygulanir()
    {
        var k = new Kopru { Envanter = () => [Satir("Belgeler", izleme: "MANUAL")] };
        FtsSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();
        vm.SeciliEnvanter = vm.Envanter[0];
        Assert.Equal("MANUAL", vm.SeciliIzleme);

        vm.SeciliIzleme = "AUTO";
        await vm.IzlemeUygulaAsync();
        Assert.Contains("ALTER FULLTEXT INDEX ON [dbo].[Belgeler] SET CHANGE_TRACKING = AUTO;", k.Gonderilen);
    }

    [Fact]
    public async Task Salt_okunurda_islem_gitmez_ama_script_acilir()
    {
        var k = new Kopru();
        FtsSekmesiViewModel vm = k.Vm(saltOkunur: true);
        await vm.YenileAsync();
        vm.SeciliEnvanter = vm.Envanter[0];
        vm.SeciliKatalogDetay = vm.KatalogDetaylari[0];

        await vm.TamDoldurAsync();
        await vm.EtkinlikDegistirAsync();
        await vm.KatalogDuzenleAsync();
        Assert.False(k.AlterGitti);
        Assert.Empty(k.Sorulan);
        Assert.Contains("Salt-okunur", vm.YonetimNotu);

        vm.KatalogYenidenKurScriptiUret();             // script sekmeye — çalıştırmaz
        Assert.Contains("REBUILD", Assert.Single(k.Acilan).Sql);
    }

    [Fact]
    public async Task Katalog_birlestir_dogrudan_silme_scripti_indexleri_yorumda_listeler()
    {
        var k = new Kopru();
        FtsSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();
        vm.SeciliKatalogDetay = vm.KatalogDetaylari.Single(x => x.Ad == "Kat");

        await vm.KatalogDuzenleAsync();
        Assert.Contains("ALTER FULLTEXT CATALOG [Kat] REORGANIZE;", k.Gonderilen);

        await vm.KatalogVarsayilanYapAsync();          // zaten varsayılan — gitmez
        Assert.DoesNotContain(k.Gonderilen, s => s.Contains("AS DEFAULT"));

        vm.KatalogSilmeScriptiUret();
        (string baslik, string sql) = Assert.Single(k.Acilan);
        Assert.Equal("fts-katalog-sil-Kat", baslik);
        Assert.Contains("-- DROP FULLTEXT INDEX ON [dbo].[Belgeler];", sql);
        Assert.Contains("-- DROP FULLTEXT INDEX ON [dbo].[Talepler];", sql);
    }

    [Fact]
    public async Task Stoplist_kelimeleri_dile_ve_listeye_gore()
    {
        var k = new Kopru();
        FtsSekmesiViewModel vm = k.Vm();
        await vm.YenileAsync();

        Assert.Equal(["SYSTEM (yerleşik, dile göre)", "Ozel (2 kelime)"], vm.Stoplistler.Select(s => s.Gosterim));
        Assert.Equal(["bir", "ve", "ile"], vm.StopKelimeleri);       // sistem listesi, Türkçe
        Assert.Contains(k.Gonderilen, s => s.Contains("language_id = 1055"));

        vm.SeciliStoplist = vm.Stoplistler[1];
        Assert.Equal(["lst", "sqlst"], vm.StopKelimeleri);
        Assert.Contains("2 kelime · Turkish", vm.StopKelimeOzeti);

        vm.StopDilIndex = 1;                                          // English
        Assert.Contains(k.Gonderilen, s => s.Contains("stoplist_id = 5 AND language_id = 1033"));
    }

    [Fact]
    public async Task Doldurma_surerken_canli_izlenir_bitince_durur_secim_korunur()
    {
        int okuma = 0;
        var k = new Kopru();
        k.Envanter = () => ++okuma <= 2
            ? [Satir("Belgeler", doldurmaKodu: 1), Satir("Talepler")]
            : [Satir("Belgeler"), Satir("Talepler")];
        FtsSekmesiViewModel vm = k.Vm();

        await vm.YenileAsync();                                       // 1. okuma: sürüyor → izleme başlar
        vm.SeciliEnvanter = vm.Envanter[1];
        Assert.True(vm.CanliIzleniyor);
        Assert.True(vm.DoldurmaSuruyorMu);

        for (int i = 0; i < 60 && vm.CanliIzleniyor; i++)
            await Task.Delay(100);

        Assert.False(vm.CanliIzleniyor);
        Assert.False(vm.DoldurmaSuruyorMu);
        Assert.Contains("Doldurma bitti", vm.YonetimNotu);
        Assert.Equal("dbo.Talepler", vm.SeciliEnvanter?.TamAd);      // tazelemede seçim korundu
    }

    [Fact]
    public async Task Sunucu_hatasi_notta_gorunur()
    {
        var k = new Kopru();
        FtsSekmesiViewModel vm = new(
            ["KdsDemo"], "KdsDemo", _ => Task.FromResult<SemaOnbellegi?>(null),
            (sql, db, ct) => sql.StartsWith("ALTER", StringComparison.Ordinal)
                ? Task.FromResult(new QueryResult { Hata = new SqlHata("Full-text crawl manager has not been initialized.", 7609, 0, 16) })
                : k.Calistir(sql, db, ct),
            (_, _, _) => { }, (_, _, _) => { }, _ => true);
        await vm.YenileAsync();
        vm.SeciliEnvanter = vm.Envanter[0];
        await vm.TamDoldurAsync();
        Assert.Contains("[7609]", vm.YonetimNotu);
    }
}
