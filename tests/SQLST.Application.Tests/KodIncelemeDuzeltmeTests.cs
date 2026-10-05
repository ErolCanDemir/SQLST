using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Kod inceleme (2026-07-27) düzeltmelerinin regresyon testleri: BF-3 bileşik-anahtar çakışması,
/// char(n) dolgusu, yinelenen anahtar, computed/identity DML; BF-7 motorlar-arası tarama sınıflaması.
/// </summary>
public class KodIncelemeDuzeltmeTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static Dictionary<string, object?> S(params (string, object?)[] a)
        => a.ToDictionary(x => x.Item1, x => x.Item2);

    // ── BF-3 Bug 1: bileşik anahtar ayırıcısız çakışıyordu ──
    [Fact]
    public void Bilesik_anahtar_ayirici_carpismayi_onler()
    {
        var kaynak = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Yil", 2026), ("No", 5), ("Ad", "A")),
            S(("Yil", 202), ("No", 65), ("Ad", "B")),
        };
        var hedef = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Yil", 2026), ("No", 5), ("Ad", "A")),
            S(("Yil", 202), ("No", 65), ("Ad", "B")),
        };

        // Ayırıcısız (2026,5) ve (202,65) ikisi de "20265" olup ÇAKIŞIR, sahte fark üretirdi.
        TabloFarki f = ExcelFarkKarsilastirici.Karsilastir(kaynak, hedef, ["Yil", "No"], ["Ad"]);
        Assert.Empty(f.Yeniler);
        Assert.Empty(f.Degisenler);
        Assert.Empty(f.Silinenler);
    }

    // ── BF-3 Bug 4: char(n) sondan boşluk dolgusu sahte "değişti" üretiyordu ──
    [Fact]
    public void Char_padding_sahte_degisiklik_uretmez()
    {
        var kaynak = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Kod", "TR")) };
        var hedef = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1), ("Kod", "TR        ")) };

        TabloFarki f = ExcelFarkKarsilastirici.Karsilastir(kaynak, hedef, ["Id"], ["Kod"]);
        Assert.Empty(f.Degisenler);
    }

    // ── BF-3 Bug 5: Excel'de yinelenen anahtar tespiti ──
    [Fact]
    public void Yinelenen_anahtar_sayisi()
    {
        var uc = new IReadOnlyDictionary<string, object?>[] { S(("Id", 1)), S(("Id", 2)), S(("Id", 1)), S(("Id", 1)) };
        Assert.Equal(2, ExcelFarkKarsilastirici.YinelenenAnahtarSayisi(uc, ["Id"])); // Id=1 üç kez → 2 fazlalık
        Assert.Equal(0, ExcelFarkKarsilastirici.YinelenenAnahtarSayisi([S(("Id", 1)), S(("Id", 2))], ["Id"]));
    }

    // ── BF-3 Bug 2: computed/rowversion INSERT/UPDATE dışında ──
    [Fact]
    public void Yazilamaz_kolon_INSERT_ve_UPDATE_disinda()
    {
        var fark = new TabloFarki(
            Yeniler: [new FarkSatiri(S(("Id", 5), ("Ad", "Yeni"), ("Toplam", 99)))],
            Degisenler: [new DegisenSatir(S(("Id", 2)),
                new Dictionary<string, (object?, object?)> { ["Ad"] = ("A", "B"), ["Toplam"] = (1, 2) },
                S(("Id", 2)))],
            Silinenler: []);
        var yazilamaz = new HashSet<string>(["Toplam"], StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> k = FarkDmlUretici.Uret(Mssql, "dbo", "T", ["Id"], fark, false, yazilamaz);

        Assert.DoesNotContain(k, x => x.Contains("Toplam"));
        Assert.Contains(k, x => x.StartsWith("UPDATE", StringComparison.Ordinal) && x.Contains("[Ad] = N'B'"));
        Assert.Contains(k, x => x.StartsWith("INSERT", StringComparison.Ordinal) && x.Contains("[Id], [Ad])"));
    }

    // ── BF-3 Bug 2: identity anahtar → INSERT bloğu IDENTITY_INSERT ile sarılır ──
    [Fact]
    public void Identity_anahtar_IDENTITY_INSERT_ile_sarilir()
    {
        var fark = new TabloFarki([new FarkSatiri(S(("Id", 5), ("Ad", "Yeni")))], [], []);

        IReadOnlyList<string> k = FarkDmlUretici.Uret(Mssql, "dbo", "T", ["Id"], fark, false, null, identityInsert: true);

        Assert.Equal("SET IDENTITY_INSERT [dbo].[T] ON;", k[0]);
        Assert.StartsWith("INSERT", k[1]);
        Assert.Equal("SET IDENTITY_INSERT [dbo].[T] OFF;", k[^1]);
    }

    // ── BF-7 Finding 2: motorlar-arası tarama sınıflaması ──
    [Theory]
    [InlineData("Table Scan", true)]                 // MSSQL heap
    [InlineData("Clustered Index Scan", true)]        // MSSQL full
    [InlineData("Seq Scan", true)]                    // PostgreSQL full
    [InlineData("TABLE ACCESS (FULL)", true)]         // Oracle full
    [InlineData("Tam tablo taraması [ALL]", true)]    // MySQL full
    [InlineData("Index Seek", false)]                 // MSSQL index
    [InlineData("Index Only Scan", false)]            // PostgreSQL index
    [InlineData("Bitmap Index Scan", false)]          // PostgreSQL index
    [InlineData("INDEX (UNIQUE SCAN)", false)]        // Oracle index
    [InlineData("Constant Scan", false)]              // veri okumaz
    public void Tarama_motorlar_arasi_dogru_siniflanir(string islem, bool taramaBekleniyor)
    {
        var plan = new SorguPlani(true,
            [new IfadePlani("s", 1, new PlanDugumu(islem, "x", 50, null, null, [], []), [])]);

        YavaslikRaporu r = YavaslikCozumleyici.Coz(plan);
        Assert.Equal(taramaBekleniyor, r.Bulgular.Any(b => b.Baslik == "Tam tarama"));
    }
}
