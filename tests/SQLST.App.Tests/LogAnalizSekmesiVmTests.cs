using SQLST.App.ViewModels;
using SQLST.Contracts;
using LogGrupGorunum = SQLST.App.Views.LogGrupGorunum;

namespace SQLST.App.Tests;

/// <summary>
/// Log Analizi SEKMESİ VM'i (v20-S21 saha m.2): "⏹ Durdur" iptali dinlemeyen bir sorguda bile
/// (ör. Mongo find ct'yi umursamaz) UI'yı ANINDA boşaltmalı; geciken eski koşunun sonucu duruma
/// yazamamalı (nesil sayacı). Pencere/WPF gerekmez — VM saf delegelerle test edilir.
/// </summary>
public class LogAnalizSekmesiVmTests
{
    private static QueryResult TekSatirSonuc(bool kesik = false) => new()
    {
        Basarili = true,
        ToplamSatir = 1,
        SatirSiniriAsildi = kesik, // true → örneklem tabloyu kapsamadı (gerçek sayım gerekir)
        ResultSetler =
        [
            new ResultSetData
            {
                Kolonlar = [new KolonBilgisi("Message", "nvarchar", typeof(string))],
                Satirlar = [new object?[] { "Timeout on OrderService" }],
            },
        ],
    };

    /// <summary>Gerçek-sayım sorgusunun (SUM(CASE WHEN … LIKE …)) sahte yanıtı: c0 = gerçek toplam.</summary>
    private static QueryResult SayimSonuc(int sayi) => new()
    {
        Basarili = true,
        ToplamSatir = 1,
        ResultSetler =
        [
            new ResultSetData
            {
                Kolonlar = [new KolonBilgisi("c0", "int", typeof(int))],
                Satirlar = [new object?[] { sayi }],
            },
        ],
    };

    /// <summary>
    /// Mongo alan keşfi yanıtı: Message(string) + Tarih(date) + Level(string).
    /// v22-S2: keşif artık 50 TAM BELGE değil, tek kolonluk ($objectToArray + $type) ad/tip listesi
    /// döndürüyor — sahte yanıt da o biçimde (yoksa test gerçeği taklit etmez).
    /// </summary>
    private static QueryResult MongoAlanlar() => new()
    {
        Basarili = true,
        ToplamSatir = 1,
        ResultSetler =
        [
            new ResultSetData
            {
                Kolonlar = [new KolonBilgisi("alanlar", "array", typeof(string))],
                Satirlar =
                [
                    new object?[]
                    {
                        """[{"k":"Message","t":"string"},{"k":"Tarih","t":"date"},{"k":"Level","t":"string"}]""",
                    },
                ],
            },
        ],
    };

    private static QueryResult MongoKume(IReadOnlyList<object?[]> satirlar) => new()
    {
        Basarili = true,
        ToplamSatir = satirlar.Count,
        ResultSetler =
        [
            new ResultSetData
            {
                Kolonlar =
                [
                    new KolonBilgisi("Message", "string", typeof(string)),
                    new KolonBilgisi("Tarih", "date", typeof(DateTime)), // BSON DateTime → "date" (m.3)
                    new KolonBilgisi("Level", "string", typeof(string)),
                ],
                Satirlar = satirlar,
            },
        ],
    };

    private static LogAnalizSekmesiViewModel VmKur(Func<string, string, CancellationToken, Task<QueryResult>> calistir)
    {
        var vm = new LogAnalizSekmesiViewModel(
            MotorTuru.Mssql, ["Db"], "Db",
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>([]),
            calistir);
        vm.SecilenTablo = "dbo.Logs";   // _tablolar boş → kademe erken döner, seçim elle verilir
        vm.SecilenKolon = "Message";
        return vm;
    }

    [Fact]
    public async Task Durdur_iptali_dinlemeyen_sorguda_ui_yi_aninda_bosaltir()
    {
        var asili = new TaskCompletionSource<QueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        LogAnalizSekmesiViewModel vm = VmKur((_, _, _) => asili.Task); // ct'yi DİNLEMEYEN sorgu

        Task analiz = vm.AnalizCommand.ExecuteAsync(null); // await'te asılı kalır
        Assert.True(vm.DurdurGorunur);
        Assert.False(vm.AnalizBosta);
        Assert.False(vm.AnalizCommand.CanExecute(null)); // buton gerçekte pasif

        vm.DurdurCommand.Execute(null); // m.2: sorgu dönmese bile UI ANINDA boşalmalı
        Assert.False(vm.DurdurGorunur);
        Assert.True(vm.AnalizBosta);
        // Kullanıcı canlı bulgusu: buton GERÇEK CanExecute'la sınanmalı — AsyncRelayCommand kendi
        // task'i uçuştayken varsayılan olarak pasif kalıyordu (AllowConcurrentExecutions düzeltmesi).
        Assert.True(vm.AnalizCommand.CanExecute(null));
        Assert.True(vm.AralikAnalizKomutCommand.CanExecute(null));
        Assert.Equal("Durduruldu.", vm.Ozet);

        // Asılı sorgu SONRADAN dönerse eski nesildendir — duruma/listeye yazamaz.
        asili.SetResult(TekSatirSonuc());
        await analiz;
        Assert.True(vm.AnalizBosta);
        Assert.False(vm.DurdurGorunur);
        Assert.Empty(vm.Gruplar);
        Assert.Equal("Durduruldu.", vm.Ozet);
    }

    [Fact]
    public async Task Normal_kosu_durduru_kapatir_ve_gruplari_doldurur() // nesil bekçisi akışı bozmasın
    {
        LogAnalizSekmesiViewModel vm = VmKur((_, _, _) => Task.FromResult(TekSatirSonuc()));

        await vm.AnalizCommand.ExecuteAsync(null);

        Assert.False(vm.DurdurGorunur);
        Assert.True(vm.AnalizBosta);
        Assert.True(vm.AnalizCommand.CanExecute(null)); // koşu bitince buton yeniden aktif
        Assert.Single(vm.Gruplar);
        Assert.False(vm.GercekSayimGorunur); // örneklem tabloyu kapsadı → sayım düğmesine gerek yok
    }

    [Fact]
    public async Task Kesik_orneklemde_gercek_sayim_otomatik_kosmaz_dugmeyle_kosar() // ara madde: hız
    {
        int cagri = 0;
        LogAnalizSekmesiViewModel vm = VmKur((_, sql, _) =>
        {
            cagri++;
            return Task.FromResult(sql.Contains("SUM(CASE WHEN", StringComparison.Ordinal)
                ? SayimSonuc(1840)
                : TekSatirSonuc(kesik: true));
        });

        await vm.AnalizCommand.ExecuteAsync(null);

        Assert.Equal(1, cagri);              // %-başlı LIKE'lı TAM TARAMA artık OTOMATİK koşmuyor
        Assert.True(vm.GercekSayimGorunur);  // bunun yerine düğme çıkıyor
        Assert.True(vm.AnalizBosta);
        Assert.False(vm.DurdurGorunur);
        Assert.Equal(1, Assert.Single(vm.Gruplar).Sayi); // örneklem sayısı

        await vm.GercekSayimGetirCommand.ExecuteAsync(null); // kullanıcı isterse tam sayım

        Assert.Equal(2, cagri);
        Assert.Equal(1840, Assert.Single(vm.Gruplar).Sayi); // gerçek toplam yazıldı
        Assert.False(vm.GercekSayimGorunur); // başarılı sayım düğmeyi gizler
        Assert.True(vm.AnalizBosta);
        Assert.False(vm.DurdurGorunur);
    }

    [Fact]
    public async Task Mongo_lazy_alanlarla_ilk_son_gorulme_dolar() // saha m.3: önceden hep boştu
    {
        var eski = new DateTime(2026, 8, 10, 9, 0, 0);
        var yeni = new DateTime(2026, 8, 13, 14, 30, 0);
        var vm = new LogAnalizSekmesiViewModel(
            MotorTuru.Mongo, ["Db"], "Db",
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(
                [new("Db", "Db", "ExceptionLog", SemaNesneTuru.Koleksiyon, [], [])]), // faz-1: kolonlar BOŞ
            // v22-S2: alan keşfi de artık aggregate — ayırt edici işaret $objectToArray (örneklem
            // sorgusunda yoktur). Sıra ÖNEMLİ: keşif önce sınanmalı.
            (_, sorgu, _) => Task.FromResult(
                sorgu.Contains("$objectToArray", StringComparison.Ordinal)
                    ? MongoAlanlar() // alan keşfi (Tarih alanı "date" tipiyle)
                    : MongoKume(     // örneklem: aynı imzadan iki kayıt, farklı zamanlar
                        [new object?[] { "Timeout on OrderService id 1", eski, "Error" },
                         new object?[] { "Timeout on OrderService id 2", yeni, "Error" }])));

        await vm.IlkYukleAsync();
        vm.SecilenTablo = "Db.ExceptionLog"; // lazy alan yüklemesi koşar

        Assert.Equal("Message", vm.SecilenKolon); // mesaj tahmini lazy alanlardan
        await vm.AnalizCommand.ExecuteAsync(null);

        var grup = Assert.Single(vm.Gruplar);
        Assert.Equal(eski, grup.IlkGorulme); // m.3 düzeltmesi: zaman alanı artık bulunuyor
        Assert.Equal(yeni, grup.SonGorulme);
        Assert.Null(grup.Seviye); // v22-S12: seviye kavramı sekmeden kaldırıldı — artık hiç üretilmez
    }

    // 🐢→⚡ v22-S1 (saha turu-2 m.2 "Mongo'da log analizi çok geç cevap veriyor"): strateji, zaman
    // alanının INDEX'li olup olmamasına göre seçilir. ÖLÇÜM (yerel MongoDB 8.3, 600k belge, son 24
    // saatte ~2.000 kayıt, uygulamanın GERÇEK sorgu metinleriyle): 24 saat süzgeci 1.718 ms ↔
    // $natural ile son N kayıt 114 ms. Index'liyken 24 saat süzgeci 11 ms — o zaman o yol doğrudur.

    /// <summary>Mongo VM'i verilen index alanlarıyla kurar; koşan sorgu metinlerini yakalar.</summary>
    private static (LogAnalizSekmesiViewModel Vm, List<string> Sorgular) MongoVmKur(
        IReadOnlyList<string> indexAlanlari)
    {
        var sorgular = new List<string>();
        var vm = new LogAnalizSekmesiViewModel(
            MotorTuru.Mongo, ["Db"], "Db",
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(
                [new("Db", "Db", "ExceptionLog", SemaNesneTuru.Koleksiyon, [], [])]),
            (_, sorgu, _) =>
            {
                sorgular.Add(sorgu);
                return Task.FromResult(sorgu.Contains("$objectToArray", StringComparison.Ordinal)
                    ? MongoAlanlar() // alan keşfi (find limit 50)
                    : MongoKume([new object?[] { "Timeout on OrderService id 1", new DateTime(2026, 8, 16), "Error" }]));
            })
        {
            MongoIndexAlanlari = (_, _) => Task.FromResult(indexAlanlari),
        };
        return (vm, sorgular);
    }

    [Fact]
    public async Task Mongo_zaman_alani_indexsizse_son_n_kayit_sorgusu_kosar()
    {
        (LogAnalizSekmesiViewModel vm, List<string> sorgular) = MongoVmKur(["_id"]); // Tarih index'i YOK
        await vm.IlkYukleAsync();
        vm.SecilenTablo = "Db.ExceptionLog";

        await vm.AnalizCommand.ExecuteAsync(null);

        string analiz = sorgular[^1];
        Assert.Contains("\"$sort\":{\"_id\":-1}", analiz);  // v22-S2: en yeniler _id index'inden
        // 24 saat süzgeci KOŞMADI (asıl ayrım bu) — $match'te zaman alanı hiç yok.
        Assert.DoesNotContain("\"Tarih\":{\"$gte\"", analiz);
        Assert.Contains("$substrCP", analiz);          // mesaj SUNUCUDA kırpıldı (ağ maliyeti)
        Assert.Contains("createIndex", vm.Ozet);       // kalıcı çözüm kullanıcıya yazıldı
        Assert.Contains("index yok", vm.Ozet);
    }

    [Fact]
    public async Task Mongo_zaman_alani_indexliyse_24_saat_sorgusu_korunur()
    {
        (LogAnalizSekmesiViewModel vm, List<string> sorgular) = MongoVmKur(["Tarih", "_id"]);
        await vm.IlkYukleAsync();
        vm.SecilenTablo = "Db.ExceptionLog";

        await vm.AnalizCommand.ExecuteAsync(null);

        string analiz = sorgular[^1];
        Assert.Contains("aggregate", analiz);          // index varken doğru yol: sunucuda süz
        // v22-S4 m.4: 24 saat süzgeci artık $expr DEĞİL, düz tarih literali — $expr'in index
        // kullanımı sunucu sürümüne bağlıydı (ExceptionLog'da 120 sn sunucu tavanı aşıldı).
        Assert.DoesNotContain("$expr", analiz);
        Assert.Contains("\"Tarih\":{\"$gte\":{\"$date\"", analiz);
        Assert.DoesNotContain("$sort", analiz);
        Assert.DoesNotContain("createIndex", vm.Ozet); // hız notu yok
    }

    /// <summary>
    /// v22-S4 saha turu-4 m.6 — kullanıcı: "analiz ÇALIŞAMIYOR, çalışsa ekran görüntüsünü alacağım".
    ///
    /// Index kontrolü analiz sorgusundan ÖNCE, kullanıcıya durum yazılmadan ve ⏹ Durdur ortada
    /// yokken koşuyor; üstelik köprü iptal jetonu almadığı için kesilemiyordu. Yanıt gelmezse analiz
    /// hiç BAŞLAMIYOR ve ekranda hiçbir şey olmuyor — kullanıcının tarif ettiği durum tam olarak bu.
    /// Bu bir HIZ İPUCUDUR, zorunlu bilgi değil: süresi dolunca "index yok" varsayılıp güvenli ve
    /// zaten hızlı olan yola düşülmeli. Test bunu ASILI KALAN bir köprüyle sabitler.
    /// </summary>
    [Fact]
    public async Task Index_koprusu_yanit_vermezse_analiz_ASILMAZ_hizli_yola_duser()
    {
        var asili = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        (LogAnalizSekmesiViewModel vm, List<string> sorgular) = MongoVmKur(["Tarih", "_id"]);
        vm.MongoIndexAlanlari = (_, _) => asili.Task; // HİÇ dönmeyen index sorgusu
        await vm.IlkYukleAsync();
        vm.SecilenTablo = "Db.ExceptionLog";

        var kronometre = System.Diagnostics.Stopwatch.StartNew();
        await vm.AnalizCommand.ExecuteAsync(null);
        kronometre.Stop();

        // Analiz TAMAMLANDI (asılı kalmadı) ve index'siz varsayımıyla hızlı yola düştü.
        Assert.False(vm.AnalizCalisiyor);
        Assert.NotEmpty(sorgular);
        Assert.Contains("\"$sort\":{\"_id\":-1}", sorgular[^1]);
        Assert.True(kronometre.Elapsed < TimeSpan.FromSeconds(15),
            $"index beklemesi sınırlanmadı: {kronometre.Elapsed.TotalSeconds:N1} sn");

        asili.SetResult(["Tarih"]); // geciken yanıt sonradan gelse de bir şey bozulmaz
    }

    /// <summary>
    /// v22-S4 saha turu-4 m.6 (çok ajanlı denetim bulgusu, iki lensle doğrulandı): örneklem TAVANA
    /// dayandığında sayılar ÖRNEKLEMDİR — "gerçek toplam" denemez.
    ///
    /// Eski ölçüt <c>sonuc.SatirSiniriAsildi</c> idi; o bayrak OKUYUCUNUN sınırında (20.000) doğar,
    /// oysa Mongo sorgusu bizim örneklem tavanımızla (5.000) zaten kesiliyor → bayrak HİÇ true
    /// olmuyordu. Sonuç: milyonlarca kayıtlık koleksiyonun 5.000'i okunup ekrana "gerçek toplam"
    /// yazılıyor ve "🔢 Gerçek toplamları getir" düğmesi hiç çıkmıyordu — kullanıcının bir kez
    /// şikâyet ettiği "yanıltıcı sayı" sessizce geri gelmişti.
    /// </summary>
    [Fact]
    public async Task Orneklem_tavana_dayandiysa_GERCEK_TOPLAM_denmez_ve_dugme_cikar()
    {
        // Tavan kadar (5.000) satır dönen sahte yanıt: daha fazlası olabilir demektir.
        object?[][] satirlar = [.. Enumerable.Range(0, 5_000)
            .Select(i => new object?[] { $"Timeout on OrderService id {i}", new DateTime(2026, 8, 16), "Error" })];

        var sorgular = new List<string>();
        var vm = new LogAnalizSekmesiViewModel(
            MotorTuru.Mongo, ["Db"], "Db",
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(
                [new("Db", "Db", "ExceptionLog", SemaNesneTuru.Koleksiyon, [], [])]),
            (_, sorgu, _) =>
            {
                sorgular.Add(sorgu);
                return Task.FromResult(sorgu.Contains("$objectToArray", StringComparison.Ordinal)
                    ? MongoAlanlar()
                    : MongoKume(satirlar));
            })
        {
            MongoIndexAlanlari = (_, _) => Task.FromResult<IReadOnlyList<string>>(["Tarih", "_id"]),
        };
        await vm.IlkYukleAsync();
        vm.SecilenTablo = "Db.ExceptionLog";

        await vm.AnalizCommand.ExecuteAsync(null);

        Assert.DoesNotContain("gerçek toplam", vm.Ozet);  // ESKİDEN tam da bunu yazıyordu
        Assert.Contains("örneklem", vm.Ozet);
        Assert.True(vm.GercekSayimGorunur, "tavana dayanmış örneklemde tam sayım düğmesi ÇIKMALI");
    }

    // ── v22-S12: EK KOLONLAR ("birden fazla kolonu yan yana") + seviyenin kaldırılması ─────────
    // Kullanıcı 2026-09-17: (1) seviye alanı belirlenemiyor ve gereksiz — combodan da grid
    // kolonundan da kalktı; (2) gruplanan imzanın örnek satırından SEÇİLEN ek kolonlar (ör.
    // StackTrace'e göre grupla, yanında ExceptionMessage) grid'de yan yana gösterilir.

    /// <summary>Message + ExceptionMessage kolonlu sahte yanıt (ek kolon senaryosu).</summary>
    private static QueryResult EkKolonluSonuc() => new()
    {
        Basarili = true,
        ToplamSatir = 2,
        ResultSetler =
        [
            new ResultSetData
            {
                Kolonlar =
                [
                    new KolonBilgisi("Message", "nvarchar", typeof(string)),
                    new KolonBilgisi("ExceptionMessage", "nvarchar", typeof(string)),
                ],
                Satirlar =
                [
                    new object?[] { "Timeout on OrderService id 1", "Sunucu yanıt vermedi" },
                    new object?[] { "Timeout on OrderService id 2", "Bağlantı koptu" },
                ],
            },
        ],
    };

    [Fact]
    public async Task Ek_kolon_secilince_sorguya_grid_e_ve_disa_aktarima_girer()
    {
        var sorgular = new List<string>();
        LogAnalizSekmesiViewModel vm = VmKur((_, sql, _) =>
        {
            sorgular.Add(sql);
            return Task.FromResult(EkKolonluSonuc());
        });
        // Mesaj kolonunun kendisi de işaretlenirse ELENMELİ (örnek mesaj zaten kendi kolonunda).
        vm.EkKolonOgeleri.Add(new LogEkKolonOgesi("Message", () => { }) { Secili = true });
        vm.EkKolonOgeleri.Add(new LogEkKolonOgesi("ExceptionMessage", () => { }) { Secili = true });

        await vm.AnalizCommand.ExecuteAsync(null);

        // 1) Sorgu: ek kolon SELECT'e girdi; mesaj kolonu ikinci kez eklenmedi.
        Assert.Contains("Message, ExceptionMessage", sorgular[^1]);
        // 2) Grid köprüsü: başlık listesi yalnız gerçek ek kolonu taşıyor.
        Assert.Equal("ExceptionMessage", Assert.Single(vm.EkKolonBasliklari));
        // 3) Grup değeri: ÖRNEK MESAJLA AYNI satırdan (ilk satır) gelir.
        LogGrupGorunum grup = Assert.Single(vm.Gruplar);
        Assert.Equal("Sunucu yanıt vermedi", Assert.Single(grup.EkDegerler));
        // 4) Kopyala/CSV: başlık + değer dışarı çıkar; "Seviye" başlığı artık YOK.
        string metin = vm.SatirMetni('\t');
        Assert.Contains("ExceptionMessage", metin);
        Assert.Contains("Sunucu yanıt vermedi", metin);
        Assert.DoesNotContain("Seviye", metin);
    }

    [Fact]
    public async Task Ek_kolon_secilmemisse_davranis_eskisiyle_ayni()
    {
        var sorgular = new List<string>();
        LogAnalizSekmesiViewModel vm = VmKur((_, sql, _) =>
        {
            sorgular.Add(sql);
            return Task.FromResult(TekSatirSonuc());
        });

        await vm.AnalizCommand.ExecuteAsync(null);

        Assert.Equal("SELECT TOP (20000) Message FROM dbo.Logs;", sorgular[^1]); // yalnız mesaj kolonu
        Assert.Empty(vm.EkKolonBasliklari);
        Assert.Empty(Assert.Single(vm.Gruplar).EkDegerler);
    }

    [Fact]
    public async Task Mongo_ek_alan_projeksiyona_kirpilarak_girer_ve_deger_grid_e_gelir()
    {
        (LogAnalizSekmesiViewModel vm, List<string> sorgular) = MongoVmKur(["Tarih", "_id"]);
        await vm.IlkYukleAsync();
        vm.SecilenTablo = "Db.ExceptionLog"; // alan keşfi EkKolonOgeleri'ni doldurur (Message/Tarih/Level)

        LogEkKolonOgesi ek = vm.EkKolonOgeleri.Single(o => o.Ad == "Level");
        ek.Secili = true;

        await vm.AnalizCommand.ExecuteAsync(null);

        // Projeksiyonda ek alan var ve mesaj gibi SUNUCUDA kırpılıyor ($substrCP'li $cond).
        string analiz = sorgular[^1];
        Assert.Contains("\"Level\":{\"$cond\"", analiz);
        Assert.Equal("Level", Assert.Single(vm.EkKolonBasliklari));
        LogGrupGorunum grup = Assert.Single(vm.Gruplar);
        Assert.Equal("Error", Assert.Single(grup.EkDegerler)); // MongoKume 3. kolonu ada göre bulundu
    }
}
