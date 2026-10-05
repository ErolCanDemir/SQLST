using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>V5-S4 · Snippet gövde çözümü — imleç işareti mantığı saf ve motordan bağımsızdır.</summary>
public class SnippetCozmeTests
{
    private static Snippet Yap(string govde)
        => new(1, "k", "b", govde, null, false);

    [Fact]
    public void Imlec_isareti_silinir_ve_ofset_yerine_gecer()
    {
        (string metin, int ofset) = Yap("SELECT * FROM $0;").Coz();

        Assert.Equal("SELECT * FROM ;", metin);
        Assert.Equal("SELECT * FROM ".Length, ofset);
        Assert.DoesNotContain(Snippet.ImlecIsareti, metin);
    }

    [Fact]
    public void Isaret_yoksa_imlec_sonda_kalir()
    {
        (string metin, int ofset) = Yap("SELECT 1;").Coz();

        Assert.Equal("SELECT 1;", metin);
        Assert.Equal(metin.Length, ofset);
    }

    [Fact]
    public void Cok_satirli_govdede_ofset_dogru()
    {
        (string metin, int ofset) = Yap("SELECT TOP 100 *\nFROM $0;").Coz();

        Assert.Equal("SELECT TOP 100 *\nFROM ;", metin);
        // Ofset işaretin bulunduğu yer olmalı — imleç oraya taşınacak
        Assert.Equal(metin.IndexOf(';'), ofset);
    }

    [Fact]
    public void Sadece_ILK_isaret_islenir()
    {
        // İkinci $0 metinde kalır: çok imleçli genişletme desteklenmiyor, bunu
        // sessizce yarım yapmaktansa olduğu gibi bırakmak dürüst davranıştır.
        (string metin, _) = Yap("$0 ve $0").Coz();

        Assert.Equal(" ve $0", metin);
    }
}

/// <summary>
/// V5-S4 · Öneri üretiminde MOTOR KAPSAMI. Bu testler çoklu motor kuralını korur:
/// T-SQL anahtar sözcükleri MongoDB sekmesinde önerilmemelidir.
/// </summary>
public class OtoTamamlamaMotorKapsamiTests
{
    private static IReadOnlyList<TamamlamaOnerisi> Oner(MotorTuru motor, IReadOnlyList<Snippet>? s = null)
        => OtoTamamlama.Oner("SEL", 3, onbellek: null, out _, motor, s);

    [Fact]
    public void Mongoda_TSQL_anahtar_sozcukleri_ONERILMEZ()
    {
        IReadOnlyList<TamamlamaOnerisi> oneriler = Oner(MotorTuru.Mongo);

        Assert.DoesNotContain(oneriler, o => o.Metin == "TOP");
        Assert.DoesNotContain(oneriler, o => o.Metin == "GETDATE");
        Assert.DoesNotContain(oneriler, o => o.Metin == "SELECT");
    }

    [Theory]
    [InlineData(MotorTuru.Mssql)]
    [InlineData(MotorTuru.Postgres)]
    [InlineData(MotorTuru.MySql)]
    [InlineData(MotorTuru.Oracle)]
    public void SQL_ailesinde_anahtar_sozcukler_onerilir(MotorTuru motor)
        => Assert.Contains(Oner(motor), o => o.Metin == "SELECT");

    [Fact]
    public void Snippetler_en_ustte_gelir()
    {
        var snippet = new Snippet(1, "sel100", "İlk 100", "SELECT TOP 100 * FROM $0;", MotorTuru.Mssql, true);

        IReadOnlyList<TamamlamaOnerisi> oneriler = Oner(MotorTuru.Mssql, [snippet]);

        TamamlamaOnerisi? bulunan = oneriler.FirstOrDefault(o => o.Metin == "sel100");
        Assert.NotNull(bulunan);
        Assert.Same(snippet, bulunan!.Snippet);
        // Önceliği anahtar sözcüklerden ve nesnelerden yüksek olmalı
        Assert.True(bulunan.Oncelik > oneriler.Where(o => o.Snippet is null).Max(o => o.Oncelik));
    }

    [Fact]
    public void Snippet_verilmezse_oneri_uretimi_bozulmaz()
        => Assert.NotEmpty(Oner(MotorTuru.Mssql, null));
}

/// <summary>
/// V5-S4 · Snippet deposu — GERÇEK SQLite dosyasına karşı. Göç 8'in gerçekten uygulandığını
/// ve motor süzmesinin çalıştığını kanıtlar; şema hatası ancak burada görünür.
/// </summary>
public class SnippetDeposuTests : IDisposable
{
    private readonly string _klasor = Path.Combine(
        Path.GetTempPath(), "sqlst-snippet-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly YerelDepo _depo;
    private readonly SqliteSnippetDeposu _snippetler;

    public SnippetDeposuTests()
    {
        Directory.CreateDirectory(_klasor);
        // Yapıcı DOSYA yolu alır ve göçleri kendisi uygular.
        _depo = new YerelDepo(Path.Combine(_klasor, "test.db"));
        _snippetler = new SqliteSnippetDeposu(_depo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_klasor, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Yerlesik_snippetler_gocle_gelir()
        => Assert.NotEmpty(await _snippetler.TumunuListeleAsync());

    [Fact]
    public async Task Motor_suzmesi_YALNIZ_o_motorun_snippetlerini_getirir()
    {
        IReadOnlyList<Snippet> mongo = await _snippetler.ListeleAsync(MotorTuru.Mongo);

        Assert.NotEmpty(mongo);
        // Mongo listesinde başka bir motora ait snippet OLMAMALI
        Assert.All(mongo, s => Assert.True(s.Motor is null or MotorTuru.Mongo,
            $"'{s.Kisayol}' Mongo listesinde ama motoru {s.Motor}"));
    }

    [Fact]
    public async Task Yerlesik_kalıplar_yanlis_motora_sizmaz()
    {
        // T-SQL kalıbı (TOP) Mongo'da ÇIKMAMALI — çoklu motor kuralının somut sınavı.
        IReadOnlyList<Snippet> mongo = await _snippetler.ListeleAsync(MotorTuru.Mongo);
        Assert.DoesNotContain(mongo, s => s.Govde.Contains("TOP", StringComparison.Ordinal));

        // Mongo kalıbı da SQL motorunda çıkmamalı
        IReadOnlyList<Snippet> mssql = await _snippetler.ListeleAsync(MotorTuru.Mssql);
        Assert.DoesNotContain(mssql, s => s.Govde.Contains("db.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Her_motorun_sel100_karsiligi_kendi_soz_dizimindedir()
    {
        async Task<string> Govde(MotorTuru motor, string kisayol)
            => (await _snippetler.ListeleAsync(motor)).Single(s => s.Kisayol == kisayol).Govde;

        Assert.Contains("TOP 100", await Govde(MotorTuru.Mssql, "sel100"));
        Assert.Contains("LIMIT 100", await Govde(MotorTuru.Postgres, "sel100"));
        Assert.Contains("LIMIT 100", await Govde(MotorTuru.MySql, "sel100"));
        Assert.Contains("FETCH FIRST 100", await Govde(MotorTuru.Oracle, "sel100"));

        // Oracle'da sondaki ';' olmaz (düz SQL kabul etmez) — evin kuralı
        Assert.DoesNotContain(";", await Govde(MotorTuru.Oracle, "sel100"));
    }

    [Fact]
    public async Task Ekle_guncelle_sil_dongusu()
    {
        long id = await _snippetler.EkleAsync(
            new Snippet(0, "kendi", "Kendi kalıbım", "SELECT $0", MotorTuru.Postgres, false));

        Snippet eklenen = (await _snippetler.ListeleAsync(MotorTuru.Postgres)).Single(s => s.Id == id);
        Assert.Equal("kendi", eklenen.Kisayol);
        Assert.False(eklenen.Yerlesik);

        await _snippetler.GuncelleAsync(eklenen with { Baslik = "Yeni ad" });
        Assert.Equal("Yeni ad",
            (await _snippetler.ListeleAsync(MotorTuru.Postgres)).Single(s => s.Id == id).Baslik);

        await _snippetler.SilAsync(id);
        Assert.DoesNotContain(await _snippetler.TumunuListeleAsync(), s => s.Id == id);
    }

    [Fact]
    public async Task Motorsuz_snippet_HER_motorda_gorunur()
    {
        await _snippetler.EkleAsync(new Snippet(0, "hepsi", "Motorsuz", "-- not", Motor: null, false));

        foreach (MotorTuru motor in Enum.GetValues<MotorTuru>())
            Assert.Contains(await _snippetler.ListeleAsync(motor), s => s.Kisayol == "hepsi");
    }
}
