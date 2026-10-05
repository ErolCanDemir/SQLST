using SQLST.App.ViewModels;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 🔎 Veri Arama × FTS entegrasyonu (v23 S3 — K3 kararı): MSSQL'de arama başında FULLTEXT kolon
/// haritası TEK sorguyla çekilir; FTS'li tablonun sorgusu CONTAINS'e geçer ve sonuç etiketi
/// ⚡FTS taşır; FTS'siz tablo ve MSSQL-dışı motor eski '=' yolunda birebir kalır.
/// </summary>
public class VeriAramaFtsVmTests
{
    private static QueryResult Satirli(params object?[][] satirlar) => new()
    {
        Basarili = true,
        ResultSetler =
        [
            new ResultSetData
            {
                Kolonlar = [new KolonBilgisi("k", "sql_variant", typeof(object))],
                Satirlar = [.. satirlar],
            },
        ],
    };

    private static SemaOnbellegi Onbellek() => new()
    {
        Nesneler =
        [
            new SemaNesnesi("db", "dbo", "Belgeler", SemaNesneTuru.Tablo,
                [new("Id", "int", false, true), new("Icerik", "nvarchar(max)", true, false)], []),
            new SemaNesnesi("db", "dbo", "Digeri", SemaNesneTuru.Tablo,
                [new("Ad", "nvarchar(70)", false, false)], []),
        ],
        YuklenmeZamaniUtc = DateTime.UtcNow,
    };

    private static VeriAramaSekmesiViewModel Vm(ILehce lehce, List<string> kosulanlar)
        => new(["KdsDemo"], "KdsDemo", lehce,
            _ => Task.FromResult<SemaOnbellegi?>(Onbellek()),
            (_, sql, _, _) =>
            {
                kosulanlar.Add(sql);
                return Task.FromResult(sql.Contains("fulltext_index_columns")
                    ? Satirli(["dbo", "Belgeler", "Icerik"])
                    : Satirli(["dbo.X", 1]));
            },
            (_, _, _) => { });

    [Fact]
    public async Task Mssqlde_harita_cekilir_ftsli_tablo_contains_ile_aranir_etiket_simsekli()
    {
        var kosulanlar = new List<string>();
        VeriAramaSekmesiViewModel vm = Vm(new MssqlLehcesi(new DpapiSecretProtector()), kosulanlar);
        vm.Deger = "motor arızası";

        await vm.AraCommand.ExecuteAsync(null);

        Assert.Contains("fulltext_index_columns", kosulanlar[0]); // harita İLK ve TEK sorgu
        Assert.Equal(1, kosulanlar.Count(s => s.Contains("fulltext_index_columns")));

        string belgelerSql = Assert.Single(kosulanlar, s => s.Contains("[Belgeler]"));
        Assert.Contains("CONTAINS([Icerik], N'\"motor arızası\"')", belgelerSql);
        string digeriSql = Assert.Single(kosulanlar, s => s.Contains("[Digeri]"));
        Assert.DoesNotContain("CONTAINS", digeriSql); // FTS'siz tablo '=' yolunda

        Assert.Contains(vm.Sonuclar, s => s.Etiket.Contains("Belgeler") && s.Etiket.Contains("⚡FTS"));
        Assert.Contains(vm.Sonuclar, s => s.Etiket.Contains("Digeri") && !s.Etiket.Contains("⚡FTS"));
    }

    [Fact]
    public async Task Mssql_disinda_harita_sorgusu_hic_kosulmaz()
    {
        var kosulanlar = new List<string>();
        VeriAramaSekmesiViewModel vm = Vm(new PostgresLehcesi(new DpapiSecretProtector()), kosulanlar);
        vm.Deger = "42";

        await vm.AraCommand.ExecuteAsync(null);

        Assert.DoesNotContain(kosulanlar, s => s.Contains("fulltext_index_columns"));
        Assert.DoesNotContain(kosulanlar, s => s.Contains("CONTAINS("));
    }
}
