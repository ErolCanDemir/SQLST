using SQLST.App.ViewModels;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v7-S1 — Karşılaştırma sekmesinin WPF'siz davranışı: sol = aktif profil, sağ = ELLE girilen
/// (aynı motor) bağlantı, şema fark gridini doldurma. Şema diff mantığı SemaKarsilastiriciTests'te.
/// </summary>
public class KarsilastirmaSekmesiTests
{
    private static ConnectionProfile Profil(string sunucu = "sunucu")
        => new() { Ad = "aktif", Motor = MotorTuru.Mssql, Sunucu = sunucu, Kimlik = KimlikTuru.Windows };

    private static SemaNesnesi Tablo(string ad, params string[] kolonlar)
        => new("db", "dbo", ad, SemaNesneTuru.Tablo,
            [.. kolonlar.Select(k => new SemaKolonu(k, "int", false, false))], []);

    /// <summary>db adına göre şema döndüren yapılandırılabilir sahte (senkron → test deterministik).</summary>
    private sealed class FakeSema : ISchemaService
    {
        public List<string> Dbler { get; } = ["A", "B"];
        public Dictionary<string, IReadOnlyList<SemaNesnesi>> Semalar { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VeritabaniBilgisi>>([.. Dbler.Select(d => new VeritabaniBilgisi(d, false))]);

        public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
            => Task.FromResult(new SemaOnbellegi
            {
                Nesneler = veritabani is not null && Semalar.TryGetValue(veritabani, out var n) ? n : [],
                YuklenmeZamaniUtc = DateTime.UtcNow,
            });

        public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);

        /// <summary>v19-S4 gövde kıyası testleri: "sunucu|şema.ad" → tanım (yoksa null).</summary>
        public Dictionary<string, string> Tanimlar { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult(Tanimlar.TryGetValue($"{profil.Sunucu}|{nesne.TamAd}", out string? t) ? t : null);

        public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult(new DuzenlemeMetasi("db", "dbo", nesne.Ad, []));
    }

    private static SemaNesnesi TabloPk(string ad, string pkKolon)
        => new("db", "dbo", ad, SemaNesneTuru.Tablo, [new SemaKolonu(pkKolon, "int", false, true)], []);

    private static KarsilastirmaSekmesiViewModel Yeni(FakeSema sema, ConnectionProfile aktif, ISqlExecutor? exec = null)
        => new(sema, new DpapiSecretProtector(), exec ?? new SahteExecutor(),
            new LehceSaglayici(new DpapiSecretProtector()), () => aktif);

    /// <summary>db adına (VeritabaniOverride) göre (anahtar, hash) satırları döndüren sahte executor.
    /// <see cref="TamSatirlar"/> DOLUYSA "SELECT *" sorgularına (Id, Ad) tam satırları döner —
    /// eşitleme testleri kaynak satırı buradan çeker; boşsa eski tek-şekilli davranış korunur.</summary>
    private sealed class VeriExecutor : ISqlExecutor
    {
        public Dictionary<string, List<object?[]>> DbSatirlari { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<object?[]>> TamSatirlar { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<QueryResult> ExecuteAsync(ConnectionProfile profil, string sql, ExecuteOptions secenekler, CancellationToken ct)
        {
            string db = secenekler.VeritabaniOverride ?? "";
            if (TamSatirlar.Count > 0 && sql.StartsWith("SELECT *", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new QueryResult
                {
                    Basarili = true,
                    ResultSetler =
                    [
                        new ResultSetData
                        {
                            Kolonlar = [new KolonBilgisi("Id", "int"), new KolonBilgisi("Ad", "nvarchar")],
                            Satirlar = TamSatirlar.TryGetValue(db, out var tam) ? tam : [],
                        },
                    ],
                });
            }

            return Task.FromResult(new QueryResult
            {
                Basarili = true,
                ResultSetler =
                [
                    new ResultSetData
                    {
                        Kolonlar = [new KolonBilgisi("Id", "int"), new KolonBilgisi("__hash", "int")],
                        Satirlar = DbSatirlari.TryGetValue(db, out var r) ? r : [],
                    },
                ],
            });
        }

        public Task<(bool Basarili, string? HataMesaji)> TestConnectionAsync(ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult<(bool, string?)>((true, null));
    }

    [Fact]
    public async Task Yukle_sol_dbleri_doldurur()
    {
        var vm = Yeni(new FakeSema(), Profil());

        await vm.YukleAsync();

        Assert.Contains("A", vm.SolVeritabanlari);
        Assert.Equal("A", vm.SolDb);
    }

    [Fact]
    public async Task SagBaglan_dbleri_getirir()
    {
        var vm = Yeni(new FakeSema(), Profil());
        vm.SagSunucu = "sunucu2";

        await vm.SagBaglanAsync();

        Assert.Contains("A", vm.SagVeritabanlari);
        Assert.Equal("A", vm.SagDb);
    }

    [Fact]
    public async Task Sag_sunucu_bossa_UYARIR()
    {
        var vm = Yeni(new FakeSema(), Profil());
        vm.SagSunucu = "";

        await vm.SagBaglanAsync();

        Assert.Empty(vm.SagVeritabanlari);
        Assert.Contains("sunucu adı gerekli", vm.Bilgi, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ayni_sunucu_ayni_db_UYARIR()
    {
        var vm = Yeni(new FakeSema(), Profil("sunucu"));
        await vm.YukleAsync();
        vm.SagSunucu = "sunucu"; // aynı sunucu
        await vm.SagBaglanAsync();

        vm.SolDb = "A";
        vm.SagDb = "A"; // + aynı db
        await vm.KarsilastirAsync();

        Assert.Empty(vm.Farklar);
        Assert.Contains("aynı sunucu", vm.Bilgi, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sema_farki_gridini_doldurur()
    {
        var sema = new FakeSema();
        sema.Semalar["A"] = [Tablo("Musteri", "Id")];
        sema.Semalar["B"] = [Tablo("Musteri", "Id"), Tablo("Yeni", "Id")];
        var vm = Yeni(sema, Profil());
        await vm.YukleAsync();
        vm.SagSunucu = "sunucu";
        await vm.SagBaglanAsync();

        vm.SolDb = "A";
        vm.SagDb = "B"; // aynı sunucu, farklı DB (iki DB kıyası)
        await vm.KarsilastirAsync();

        SemaFarkGorunumu f = Assert.Single(vm.Farklar);
        Assert.Equal("dbo.Yeni", f.Nesne);
        Assert.Contains("yalnız hedefte", f.Yon, StringComparison.Ordinal); // "sağda"→"hedefte" (2026-07-31)
        Assert.Contains("1 fark", vm.Bilgi, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Es_semalarda_fark_yok_der()
    {
        var sema = new FakeSema();
        sema.Semalar["A"] = [Tablo("Musteri", "Id")];
        sema.Semalar["B"] = [Tablo("Musteri", "Id")];
        var vm = Yeni(sema, Profil());
        await vm.YukleAsync();
        vm.SagSunucu = "sunucu";
        await vm.SagBaglanAsync();

        vm.SolDb = "A";
        vm.SagDb = "B";
        await vm.KarsilastirAsync();

        Assert.Empty(vm.Farklar);
        Assert.Contains("Fark yok", vm.Bilgi, StringComparison.Ordinal);
    }

    [Fact]
    public void Sag_motor_metni_SOLA_sabit()
    {
        var vm = Yeni(new FakeSema(), Profil());
        Assert.Contains("SQL Server", vm.SagMotorMetni, StringComparison.Ordinal);
    }

    // ── S2: veri karşılaştırma ────────────────────────────────────────────────

    [Fact]
    public async Task Veri_tablo_secilince_PK_anahtari_gosterilir()
    {
        var sema = new FakeSema();
        sema.Semalar["A"] = [TabloPk("Musteri", "Id")];
        var vm = Yeni(sema, Profil());
        await vm.YukleAsync();               // SolDb="A" → sol şema yüklenir (senkron sahte)

        Assert.Contains("dbo.Musteri", vm.VeriTablolari);
        vm.VeriTablo = "dbo.Musteri";
        Assert.Contains("Id", vm.VeriAnahtar, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Veri_karsilastir_satir_farklarini_bulur()
    {
        var sema = new FakeSema();
        sema.Semalar["A"] = [TabloPk("Musteri", "Id")]; // sol şema (tablo listesi + PK)
        var exec = new VeriExecutor();
        exec.DbSatirlari["A"] = [[1, "h1"], [2, "h2"], [3, "h3"]]; // kaynak
        exec.DbSatirlari["B"] = [[1, "h1"], [2, "XX"], [4, "h4"]]; // hedef

        var vm = Yeni(sema, Profil(), exec);
        await vm.YukleAsync();
        vm.VeriTablo = "dbo.Musteri";
        vm.SagSunucu = "sunucu2";
        await vm.SagBaglanAsync();
        vm.SolDb = "A";
        vm.SagDb = "B";

        await vm.VeriKarsilastirAsync();

        Assert.Equal(3, vm.VeriFarklari.Count);
        Assert.Contains(vm.VeriFarklari, f => f.Anahtar == "2" && f.Yon.Contains("farklı", StringComparison.Ordinal)
            && f.Detay.Contains("→", StringComparison.Ordinal)); // ≠ satırda kolon detayı (kaynak → hedef)
        // "solda/sağda" → "kaynakta/hedefte" (kullanıcı isteği 2026-07-31)
        Assert.Contains(vm.VeriFarklari, f => f.Anahtar == "3" && f.Yon.Contains("kaynakta", StringComparison.Ordinal));
        Assert.Contains(vm.VeriFarklari, f => f.Anahtar == "4" && f.Yon.Contains("hedefte", StringComparison.Ordinal));
        Assert.Contains("3 fark", vm.VeriBilgi, StringComparison.Ordinal);
    }

    // ── v19-S4: gövde kıyası + gövdeli eşitleme ──────────────────────────────

    [Fact]
    public async Task Govde_farki_bulunur_ve_create_or_alter_ile_esitlenir()
    {
        var sema = new FakeSema();
        SemaNesnesi vw = new("db", "dbo", "VwOzet", SemaNesneTuru.View, [], []);
        sema.Semalar["A"] = [TabloPk("Musteri", "Id"), vw];
        sema.Semalar["B"] = [TabloPk("Musteri", "Id"), vw];
        sema.Tanimlar["sunucu|dbo.VwOzet"] = "CREATE VIEW dbo.VwOzet AS SELECT 1;";   // kaynak
        sema.Tanimlar["sunucu2|dbo.VwOzet"] = "CREATE VIEW dbo.VwOzet AS SELECT 2;";  // hedef FARKLI

        var vm = Yeni(sema, Profil());
        await vm.YukleAsync();
        vm.SagSunucu = "sunucu2";
        await vm.SagBaglanAsync();
        vm.SolDb = "A";
        vm.SagDb = "B";
        await vm.KarsilastirAsync();

        SemaFarkGorunumu fark = Assert.Single(vm.Farklar); // tablolar eş — tek fark gövde
        Assert.Equal("gövde farklı", fark.Detay);
        Assert.Equal("dbo.VwOzet", fark.Nesne);

        string? yakalanan = null;
        vm.ScriptGoster = (_, script) => yakalanan = script;
        await vm.SemaEsitleAsync([fark]);

        string s = Assert.IsType<string>(yakalanan);
        Assert.Contains("CREATE OR ALTER VIEW dbo.VwOzet AS SELECT 1;", s); // kaynak gövde hedefe
    }

    [Fact]
    public async Task Ayni_govde_fark_uretmez_bosluk_farki_esittir()
    {
        var sema = new FakeSema();
        SemaNesnesi vw = new("db", "dbo", "VwOzet", SemaNesneTuru.View, [], []);
        sema.Semalar["A"] = [vw];
        sema.Semalar["B"] = [vw];
        sema.Tanimlar["sunucu|dbo.VwOzet"] = "CREATE VIEW dbo.VwOzet AS\r\nSELECT 1;   ";
        sema.Tanimlar["sunucu2|dbo.VwOzet"] = "CREATE VIEW dbo.VwOzet AS\nSELECT 1;"; // yalnız satır sonu/boşluk

        var vm = Yeni(sema, Profil());
        await vm.YukleAsync();
        vm.SagSunucu = "sunucu2";
        await vm.SagBaglanAsync();
        vm.SolDb = "A";
        vm.SagDb = "B";
        await vm.KarsilastirAsync();

        Assert.Empty(vm.Farklar); // normalize kıyas — kozmetik fark gürültü üretmez
    }

    // ── 🔀 Şema Eşitleme (2026-08-03: veri eşitlemeyle aynı yöntem) ──────────

    [Fact]
    public async Task Sema_esitle_tumu_drop_ve_create_uretir()
    {
        var sema = new FakeSema();
        sema.Semalar["A"] = [TabloPk("Musteri", "Id"), TabloPk("YeniTablo", "Id")]; // kaynakta fazladan tablo
        sema.Semalar["B"] = [TabloPk("Musteri", "Id"), TabloPk("EskiTablo", "Id")]; // hedefte fazladan tablo
        var vm = Yeni(sema, Profil());
        await vm.YukleAsync();
        vm.SagSunucu = "sunucu2";
        await vm.SagBaglanAsync();
        vm.SolDb = "A";
        vm.SagDb = "B";
        await vm.KarsilastirAsync();
        Assert.Equal(2, vm.Farklar.Count);

        string? yakalanan = null;
        vm.ScriptGoster = (_, script) => yakalanan = script;

        await vm.TumSemayiEsitleAsync();

        string s = Assert.IsType<string>(yakalanan);
        Assert.Contains("CREATE TABLE [dbo].[YeniTablo]", s);
        Assert.Contains("DROP TABLE [dbo].[EskiTablo];", s);
        Assert.Contains("YIKICI", s);
        Assert.Contains("üretildi", vm.Bilgi, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sema_esitle_secim_yalniz_secileni_isler()
    {
        var sema = new FakeSema();
        sema.Semalar["A"] = [TabloPk("Musteri", "Id"), TabloPk("YeniTablo", "Id")];
        sema.Semalar["B"] = [TabloPk("Musteri", "Id"), TabloPk("EskiTablo", "Id")];
        var vm = Yeni(sema, Profil());
        await vm.YukleAsync();
        vm.SagSunucu = "sunucu2";
        await vm.SagBaglanAsync();
        vm.SolDb = "A";
        vm.SagDb = "B";
        await vm.KarsilastirAsync();

        string? yakalanan = null;
        vm.ScriptGoster = (_, script) => yakalanan = script;
        SemaFarkGorunumu yalnizCreate = vm.Farklar.Single(f => f.Nesne == "dbo.YeniTablo");

        await vm.SemaEsitleAsync([yalnizCreate]);

        string s = Assert.IsType<string>(yakalanan);
        Assert.Contains("CREATE TABLE [dbo].[YeniTablo]", s);
        Assert.DoesNotContain("DROP TABLE", s); // seçilmeyen fark script'e girmez
    }

    // ── 🔀 Veri Eşitleme (madde 4, 2026-08-03: çoklu seçim / tümü) ───────────

    private async Task<(KarsilastirmaSekmesiViewModel Vm, Func<string?> Script)> EsitlemeSahnesiKurAsync()
    {
        var sema = new FakeSema();
        sema.Semalar["A"] = [TabloPk("Musteri", "Id")];
        var exec = new VeriExecutor();
        exec.DbSatirlari["A"] = [[1, "h1"], [2, "h2"], [3, "h3"]]; // kaynak
        exec.DbSatirlari["B"] = [[1, "h1"], [2, "XX"], [4, "h4"]]; // hedef: 2 farklı · 3 yok · 4 fazla
        exec.TamSatirlar["A"] = [[2, "KaynakAd2"], [3, "KaynakAd3"]];
        exec.TamSatirlar["B"] = [[4, "HedefAd4"]];

        var vm = Yeni(sema, Profil(), exec);
        await vm.YukleAsync();
        vm.VeriTablo = "dbo.Musteri";
        vm.SagSunucu = "sunucu2";
        await vm.SagBaglanAsync();
        vm.SolDb = "A";
        vm.SagDb = "B";
        await vm.VeriKarsilastirAsync();
        Assert.Equal(3, vm.VeriFarklari.Count);

        string? yakalanan = null;
        vm.ScriptGoster = (_, script) => yakalanan = script;
        return (vm, () => yakalanan);
    }

    [Fact]
    public async Task Tumunu_esitle_uc_dml_turunu_de_uretir()
    {
        (KarsilastirmaSekmesiViewModel vm, Func<string?> script) = await EsitlemeSahnesiKurAsync();

        await vm.TumunuEsitleAsync();

        string s = Assert.IsType<string>(script());
        Assert.Contains("DELETE FROM [dbo].[Musteri] WHERE [Id] = 4;", s);                       // yalnız hedefte
        Assert.Contains("UPDATE [dbo].[Musteri] SET [Ad] = N'KaynakAd2' WHERE [Id] = 2;", s);    // farklı
        Assert.Contains("INSERT INTO [dbo].[Musteri] ([Id], [Ad]) VALUES (3, N'KaynakAd3');", s); // yalnız kaynakta
        Assert.Contains("üretildi", vm.VeriBilgi, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secilenleri_esitle_yalniz_secilen_farki_isler()
    {
        (KarsilastirmaSekmesiViewModel vm, Func<string?> script) = await EsitlemeSahnesiKurAsync();
        VeriFarkGorunumu silinecek = vm.VeriFarklari.Single(f => f.Anahtar == "4");

        await vm.EsitleAsync([silinecek]); // kullanıcı tek satır seçti

        string s = Assert.IsType<string>(script());
        Assert.Contains("DELETE FROM [dbo].[Musteri] WHERE [Id] = 4;", s);
        Assert.DoesNotContain("INSERT INTO", s);
        Assert.DoesNotContain("UPDATE ", s.Replace("DELETE → UPDATE → INSERT", "")); // başlık yorumu hariç
    }

    [Fact]
    public async Task Karsilastirma_yokken_esitle_uyarir()
    {
        var vm = Yeni(new FakeSema(), Profil());
        string? yakalanan = null;
        vm.ScriptGoster = (_, script) => yakalanan = script;

        await vm.TumunuEsitleAsync();

        Assert.Null(yakalanan);
        Assert.Contains("Veri karşılaştır", vm.VeriBilgi, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Veri_PK_yoksa_uyarir()
    {
        var sema = new FakeSema();
        sema.Semalar["A"] = [Tablo("Pksiz", "Ad")]; // PK yok (Tablo helper PkMi=false)
        var vm = Yeni(sema, Profil());
        await vm.YukleAsync();
        vm.VeriTablo = "dbo.Pksiz";
        vm.SagSunucu = "sunucu2";
        await vm.SagBaglanAsync();
        vm.SolDb = "A";
        vm.SagDb = "B";

        await vm.VeriKarsilastirAsync();

        Assert.Empty(vm.VeriFarklari);
        Assert.Contains("PK yok", vm.VeriBilgi, StringComparison.Ordinal);
    }
}
