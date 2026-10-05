using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>V5-S2: satır/bağlam çıkarımı motor-nötrdür — bir kez yazılır, beş motorda kullanılır.</summary>
public class MetinArayiciTests
{
    private static QueryResult Kume(params object?[][] satirlar)
        => new()
        {
            Basarili = true,
            ResultSetler =
            [
                new ResultSetData
                {
                    Kolonlar =
                    [
                        new KolonBilgisi("sema", "nvarchar", typeof(string)),
                        new KolonBilgisi("ad", "nvarchar", typeof(string)),
                        new KolonBilgisi("tur", "char", typeof(string)),
                        new KolonBilgisi("tanim", "nvarchar", typeof(string)),
                    ],
                    Satirlar = [.. satirlar],
                },
            ],
        };

    private static ILehce Mssql => new MssqlLehcesi(new DpapiSecretProtector());

    [Fact]
    public void Satir_numaralari_1den_baslar_ve_dogru_bulunur()
    {
        const string tanim = "CREATE PROCEDURE p AS\nBEGIN\n  SELECT bakiye FROM musteri;\nEND";

        IReadOnlyList<AramaEslesmesi> e = MetinArayici.Eslesmeler(tanim, "bakiye");

        AramaEslesmesi tek = Assert.Single(e);
        Assert.Equal(3, tek.Satir);                       // editörde bu satıra gidilecek
        Assert.Equal("SELECT bakiye FROM musteri;", tek.SatirMetni);
    }

    [Fact]
    public void Ayni_satirda_cok_gecis_BIR_KEZ_raporlanir()
    {
        // Kullanıcı satıra gidiyor, karaktere değil — aynı satırı üç kez listelemek gürültü olurdu
        IReadOnlyList<AramaEslesmesi> e = MetinArayici.Eslesmeler("x = x + x", "x");
        Assert.Single(e);
    }

    [Fact]
    public void Varsayilan_buyuk_kucuk_harf_duyarsizdir()
    {
        Assert.Single(MetinArayici.Eslesmeler("SELECT BAKIYE FROM t", "bakiye"));
        Assert.Empty(MetinArayici.Eslesmeler("SELECT BAKIYE FROM t", "bakiye", buyukKucukDuyarli: true));
    }

    [Theory]
    [InlineData("a\nb\nARANAN")]      // Unix
    [InlineData("a\r\nb\r\nARANAN")]  // Windows
    [InlineData("a\rb\rARANAN")]      // eski Mac
    public void Farkli_satir_sonlari_ayni_satir_numarasini_verir(string tanim)
        => Assert.Equal(3, MetinArayici.Eslesmeler(tanim, "ARANAN")[0].Satir);

    [Fact]
    public void Uzun_satir_kirpilir_liste_bozulmaz()
    {
        string uzun = "SELECT " + new string('x', 400) + " bakiye";
        string metin = MetinArayici.Eslesmeler(uzun, "bakiye")[0].SatirMetni;

        Assert.True(metin.Length <= 161, $"satır {metin.Length} karakter — kırpılmalıydı");
        Assert.EndsWith("…", metin);
    }

    [Fact]
    public void Cok_eslesmeli_nesneler_uste_gelir()
    {
        IReadOnlyList<AramaSonucu> s = MetinArayici.Cozumle(Kume(
            ["dbo", "az", "P", "bakiye"],
            ["dbo", "cok", "P", "bakiye\nbakiye\nbakiye"],
            ["dbo", "orta", "P", "bakiye\nbakiye"]), Mssql, "bakiye");

        Assert.Equal(["cok", "orta", "az"], s.Select(x => x.Ad));
        Assert.Equal(3, s[0].EslesmeSayisi);
    }

    [Fact]
    public void Sifreli_nesne_atlanir_cokme_olmaz()
    {
        // sys.sql_modules şifreli nesnede definition = NULL döner
        IReadOnlyList<AramaSonucu> s = MetinArayici.Cozumle(Kume(
            ["dbo", "sifreli", "P", null],
            ["dbo", "acik", "P", "bakiye"]), Mssql, "bakiye");

        Assert.Equal("acik", Assert.Single(s).Ad);
    }

    [Fact]
    public void Bilinmeyen_tur_kodu_aramayi_KIRMAZ()
    {
        // Tür yalnız simge içindir; tanımadığı kod yüzünden arama çökmemeli
        IReadOnlyList<AramaSonucu> s = MetinArayici.Cozumle(Kume(
            ["dbo", "x", "ZZZ", "bakiye"]), Mssql, "bakiye");

        Assert.Single(s);
    }

    [Fact]
    public void Tanim_saklanir_ki_sekmede_yeniden_sorgulanmasin()
    {
        const string tanim = "CREATE VIEW v AS SELECT bakiye FROM t";
        AramaSonucu s = Assert.Single(MetinArayici.Cozumle(Kume(["dbo", "v", "V", tanim]), Mssql, "bakiye"));

        Assert.Equal(tanim, s.Tanim);
        Assert.Equal("dbo.v", s.TamAd);
    }

    [Fact]
    public void Bos_arama_ve_bos_sonuc_guvenli()
    {
        Assert.Empty(MetinArayici.Cozumle(Kume(["dbo", "x", "P", "abc"]), Mssql, ""));
        Assert.Empty(MetinArayici.Cozumle(new QueryResult { Basarili = true }, Mssql, "abc"));
        Assert.Empty(MetinArayici.Eslesmeler("abc", ""));
    }
}

/// <summary>
/// V5-S2: her lehçenin arama sorgusu. <b>En kritik davranış LIKE joker kaçışı:</b>
/// kaçırılmazsa "kdv_orani" araması "kdvXorani"yi de bulur (sessiz yanlış sonuç).
/// </summary>
public class MetinAramaSorgusuTests
{
    private static readonly LehceSaglayici Lehceler = new(new DpapiSecretProtector());

    [Theory]
    [InlineData(MotorTuru.Mssql)]
    [InlineData(MotorTuru.Postgres)]
    [InlineData(MotorTuru.MySql)]
    [InlineData(MotorTuru.Oracle)]
    public void Alt_cizgi_ve_yuzde_JOKER_olarak_gecmez(MotorTuru motor)
    {
        string sql = Lehceler.Getir(motor).MetinAramaSorgusu("kdv_orani%");

        // Ham hâliyle geçerse joker olur; kaçırılmış hâli bulunmalı
        Assert.DoesNotContain("kdv_orani%'", sql);
        Assert.Contains("ESCAPE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kdv", sql);
    }

    [Theory]
    [InlineData(MotorTuru.Mssql)]
    [InlineData(MotorTuru.Postgres)]
    [InlineData(MotorTuru.MySql)]
    [InlineData(MotorTuru.Oracle)]
    public void Tek_tirnak_kacirilir_sorgu_bozulmaz(MotorTuru motor)
    {
        string sql = Lehceler.Getir(motor).MetinAramaSorgusu("O'Brien");
        Assert.Contains("O''Brien", sql);
    }

    [Fact]
    public void Oracle_sorgusunda_noktali_virgul_olmaz()
        => Assert.DoesNotContain(";", Lehceler.Getir(MotorTuru.Oracle).MetinAramaSorgusu("x"));

    // ── B4/A5 (2026-07-19): sonuç tavanı — dört motorda da ──

    [Fact]
    public void Her_motorda_sonuc_TAVANI_vardir()
    {
        // Tavan yokken çok yaygın bir metin (ör. "SELECT") arandığında veritabanındaki
        // TÜM nesne tanımları ağdan geçip belleğe alınıyordu.
        Assert.Contains($"TOP {ILehce.AramaTavani}",
            Lehceler.Getir(MotorTuru.Mssql).MetinAramaSorgusu("x"), StringComparison.Ordinal);

        Assert.Contains($"LIMIT {ILehce.AramaTavani}",
            Lehceler.Getir(MotorTuru.Postgres).MetinAramaSorgusu("x"), StringComparison.Ordinal);

        Assert.Contains($"LIMIT {ILehce.AramaTavani}",
            Lehceler.Getir(MotorTuru.MySql).MetinAramaSorgusu("x"), StringComparison.Ordinal);

        Assert.Contains($"FETCH FIRST {ILehce.AramaTavani} ROWS ONLY",
            Lehceler.Getir(MotorTuru.Oracle).MetinAramaSorgusu("x"), StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_tavani_da_noktali_virgulsuz_kalir()
        => Assert.DoesNotContain(";", Lehceler.Getir(MotorTuru.Oracle).MetinAramaSorgusu("x"));

    // ── B3/A4 (2026-07-19): Oracle arama sorgusundaki dört kusurun regresyonu ──

    [Fact]
    public void Oracle_VIEW_tanimlarini_da_arar()
    {
        // ALL_SOURCE view içermez (yalnız PROCEDURE/FUNCTION/PACKAGE/TRIGGER/TYPE);
        // view'lar sessizce atlanıyordu — diğer üç motorda aranırken.
        string sql = Lehceler.Getir(MotorTuru.Oracle).MetinAramaSorgusu("kdv");

        Assert.Contains("all_views", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION ALL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_view_metnini_LONG_kolondan_okumaz()
    {
        // ALL_VIEWS.TEXT bir LONG kolondur; LONG'a LIKE/UPPER UYGULANAMAZ ve sorgu
        // Oracle'da hiç çalışmazdı. VARCHAR2 karşılığı TEXT_VC kullanılmalı.
        string sql = Lehceler.Getir(MotorTuru.Oracle).MetinAramaSorgusu("kdv");

        Assert.Contains("text_vc", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPPER(v.text)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_LISTAGG_KULLANMAZ_taşma_riski_yok()
    {
        // LISTAGG VARCHAR2 (4000 bayt) döndürür; büyük bir package body sınırı aşınca
        // ORA-01489 ile TÜM arama düşerdi. XMLAGG + getclobval() sınırsızdır ve KESMEZ.
        string sql = Lehceler.Getir(MotorTuru.Oracle).MetinAramaSorgusu("kdv");

        Assert.DoesNotContain("LISTAGG", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("getclobval", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oracle_package_spec_ve_body_AYRI_gruplanir()
    {
        // GROUP BY tür içermiyordu; aynı adlı PACKAGE ve PACKAGE BODY tek satırda
        // toplanıyor, ikisi de 1. satırdan başladığı için metinler iç içe geçip satır
        // numaralarını anlamsızlaştırıyordu.
        string sql = Lehceler.Getir(MotorTuru.Oracle).MetinAramaSorgusu("kdv");

        Assert.Contains("GROUP BY s.owner, s.name, s.type", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_tur_kodunu_MSSQL_koduna_cevirir()
    {
        // Ham Oracle türü ('PROCEDURE'…) dönüyordu; TurCevir tanımadığından her şey
        // StoredProcedure görünüyordu. Diğer üç lehçe MSSQL kodlarına normalize ediyor.
        string sql = Lehceler.Getir(MotorTuru.Oracle).MetinAramaSorgusu("kdv");

        Assert.Contains("'FN'", sql, StringComparison.Ordinal);
        Assert.Contains("'V'", sql, StringComparison.Ordinal);

        // Üretilen kodlar lehçenin kendi TurCevir'i tarafından TANINMALI
        ILehce oracle = Lehceler.Getir(MotorTuru.Oracle);
        Assert.Equal(SemaNesneTuru.View, oracle.TurCevir("V"));
        Assert.Equal(SemaNesneTuru.Fonksiyon, oracle.TurCevir("FN"));
        Assert.Equal(SemaNesneTuru.StoredProcedure, oracle.TurCevir("P"));
    }

    [Fact]
    public void Her_motor_KANONIK_dort_kolonu_uretir()
    {
        // sema, ad, tur, tanim — Application katmanı bu sıraya güvenir
        foreach (MotorTuru m in new[] { MotorTuru.Mssql, MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Oracle })
        {
            string sql = Lehceler.Getir(m).MetinAramaSorgusu("x").ToLowerInvariant();
            Assert.Contains("sema", sql);
            Assert.Contains("ad", sql);
            Assert.Contains("tanim", sql);
        }
    }

    [Fact]
    public void Postgres_ve_MySql_sistem_nesnelerini_DISLAR()
    {
        Assert.Contains("pg_catalog", Lehceler.Getir(MotorTuru.Postgres).MetinAramaSorgusu("x"));
        // MySQL'de kapsam DATABASE() ile seçili veritabanına sınırlı
        Assert.Contains("DATABASE()", Lehceler.Getir(MotorTuru.MySql).MetinAramaSorgusu("x"));
    }
}

/// <summary>
/// V5-S2 CANLI KANITI (LocalDB). <b>Ürünün gerçekte çağırdığı yoldan geçer</b> —
/// V5-S1'de canlı testler farklı bir katmandan geçtiği için bozuk tahmini planı kaçırmıştı.
/// </summary>
public class MetinAramaLocalDbTests : IAsyncLifetime
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly ExecuteOptions Tempdb = new() { VeritabaniOverride = "tempdb" };

    private readonly string _ek = Guid.NewGuid().ToString("N")[..12];
    private readonly MssqlLehcesi _lehce = new(new DpapiSecretProtector());
    private readonly SqlExecutor _executor;

    public MetinAramaLocalDbTests() => _executor = new SqlExecutor(_lehce);

    private string Sp => $"sp_arama_{_ek}";
    private string Vw => $"vw_arama_{_ek}";

    public async Task InitializeAsync()
    {
        // NOT: gövdede "SELECT kdv_orani FROM sys.objects" YAZILAMAZ — ertelenmiş ad çözümlemesi
        // yalnız OLMAYAN nesneler içindir; sys.objects var olduğundan kolon adı CREATE anında
        // doğrulanır ve SP hiç oluşmaz. Kurulum sonucu ayrıca doğrulanır: sessiz başarısızlık
        // arama hatası gibi görünüyordu.
        QueryResult r = await _executor.ExecuteAsync(Profil(), $"""
            EXEC('CREATE PROCEDURE dbo.{Sp} AS
            BEGIN
              SELECT 1 AS bir;
              SELECT 0 AS kdv_orani WHERE 1 = 0;
            END');
            EXEC('CREATE VIEW dbo.{Vw} AS SELECT name AS kdv_orani FROM sys.objects');
            """, Tempdb, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);
    }

    public async Task DisposeAsync()
        => await _executor.ExecuteAsync(Profil(), $"""
            DROP PROCEDURE IF EXISTS dbo.{Sp};
            DROP VIEW IF EXISTS dbo.{Vw};
            """, Tempdb, CancellationToken.None);

    private async Task<IReadOnlyList<AramaSonucu>> AraAsync(string aranan)
    {
        QueryResult r = await _executor.ExecuteAsync(
            Profil(), _lehce.MetinAramaSorgusu(aranan), Tempdb, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);
        return MetinArayici.Cozumle(r, _lehce, aranan);
    }

    [Fact]
    public async Task Tanimda_gecen_metin_bulunur_ve_satir_numarasi_dogrudur()
    {
        IReadOnlyList<AramaSonucu> s = await AraAsync("kdv_orani");

        AramaSonucu sp = Assert.Single(s, x => x.Ad == Sp);
        Assert.Equal(SemaNesneTuru.StoredProcedure, sp.Tur);
        AramaEslesmesi e = Assert.Single(sp.Eslesmeler);
        Assert.Contains("kdv_orani", e.SatirMetni);
        Assert.True(e.Satir > 1, "eşleşme SP gövdesinin ilk satırında değil");

        Assert.Contains(s, x => x.Ad == Vw && x.Tur == SemaNesneTuru.View);
    }

    [Fact]
    public async Task LIKE_JOKERI_yanlis_eslesme_URETMEZ()
    {
        // '_' kaçırılmazsa "kdvXorani" da eşleşirdi. Gerçek sunucuda kanıtlıyoruz:
        // aradığımız desende '_' var ama tanımlarda "kdvXorani" YOK → kaçış doğruysa
        // yalnız gerçek eşleşmeler döner; kaçış bozuksa sorgu ya hata verir ya fazla döner.
        IReadOnlyList<AramaSonucu> tam = await AraAsync("kdv_orani");
        Assert.NotEmpty(tam);

        // Var olmayan bir desen hiç sonuç vermemeli (joker sızıntısı olsaydı dönerdi)
        Assert.Empty(await AraAsync("kdv%orani_yok"));
    }

    [Fact]
    public async Task Eslesmeyen_metin_bos_sonuc_verir()
        => Assert.Empty(await AraAsync($"asla_bulunmayacak_{_ek}"));

    [Fact]
    public async Task Tek_tirnakli_arama_sorguyu_bozmaz()
    {
        IReadOnlyList<AramaSonucu> s = await AraAsync("O'Brien");
        Assert.Empty(s);   // önemli olan HATA VERMEMESİ
    }
}

/// <summary>V5-S2 CANLI KANITI (PostgreSQL). Ortam-kapılı.</summary>
public class MetinAramaPostgresCanliTests : IAsyncLifetime
{
    private const string Host = "127.0.0.1";
    private const int Port = 5433;

    private static ConnectionProfile Profil() => new()
    {
        Ad = "pg-arama", Motor = MotorTuru.Postgres, Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql, KullaniciAdi = "postgres", BaglantiTimeoutSn = 10,
    };

    private static readonly ExecuteOptions Demo = new() { VeritabaniOverride = "sqlst_demo" };

    private readonly string _ek = Guid.NewGuid().ToString("N")[..12];
    private readonly PostgresLehcesi _lehce = new(new DpapiSecretProtector());
    private readonly SqlExecutor _executor;

    public MetinAramaPostgresCanliTests() => _executor = new SqlExecutor(_lehce);

    private string Vw => $"vw_arama_{_ek}";
    private string Fn => $"fn_arama_{_ek}";

    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    public async Task InitializeAsync()
    {
        if (!Erisilebilir()) return;
        QueryResult r = await _executor.ExecuteAsync(Profil(), $"""
            CREATE VIEW public.{Vw} AS SELECT 1 AS kdv_orani;
            CREATE FUNCTION public.{Fn}() RETURNS int LANGUAGE sql AS $$
              SELECT kdv_orani FROM public.{Vw}
            $$;
            """, Demo, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);
    }

    public async Task DisposeAsync()
    {
        if (!Erisilebilir()) return;
        await _executor.ExecuteAsync(Profil(), $"""
            DROP FUNCTION IF EXISTS public.{Fn}();
            DROP VIEW IF EXISTS public.{Vw};
            """, Demo, CancellationToken.None);
    }

    [Fact]
    public async Task View_ve_fonksiyon_tanimlarinda_bulunur()
    {
        if (!Erisilebilir()) return;

        QueryResult r = await _executor.ExecuteAsync(
            Profil(), _lehce.MetinAramaSorgusu("kdv_orani"), Demo, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);

        IReadOnlyList<AramaSonucu> s = MetinArayici.Cozumle(r, _lehce, "kdv_orani");

        Assert.Contains(s, x => x.Ad == Vw);
        Assert.Contains(s, x => x.Ad == Fn);
        Assert.All(s, x => Assert.NotEmpty(x.Eslesmeler));
        // Sistem katalogları dışlanmalı — sonuçlar kendi şemamızdan
        Assert.DoesNotContain(s, x => x.Sema is "pg_catalog" or "information_schema");
    }

    [Fact]
    public async Task Eslesmeyen_metin_bos_doner()
    {
        if (!Erisilebilir()) return;

        QueryResult r = await _executor.ExecuteAsync(
            Profil(), _lehce.MetinAramaSorgusu($"asla_yok_{_ek}"), Demo, CancellationToken.None);

        Assert.True(r.Basarili, r.Hata?.Mesaj);
        Assert.Empty(MetinArayici.Cozumle(r, _lehce, $"asla_yok_{_ek}"));
    }
}
