using SQLST.App.ViewModels;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 🔎 FTS sekmesi VM'i (v23 S1+S2 — kararlar K1-K4): keşif (kurulu değil yönlendirmesi + envanter),
/// arama yardımcısının ürettiği SQL ve köprü sözleşmesi (ara → ÇALIŞTIRARAK, sihirbaz →
/// ÇALIŞTIRMADAN sekme), sihirbazın anahtar-index ön şartı. WPF gerekmez — saf delege deseni
/// (ProfilerSekmesiVmTests gibi). Canlı FTS turu kullanıcının sunucusunda (K4 — LocalDB desteklemez).
/// </summary>
public class FtsSekmesiVmTests
{
    private static QueryResult Deger(params object?[][] satirlar) => new()
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
                [
                    new("Id", "int", false, true),
                    new("Icerik", "nvarchar(max)", true, false),
                    new("Tutar", "decimal(18,2)", true, false),
                ], []),
            new SemaNesnesi("db", "dbo", "Talepler", SemaNesneTuru.Tablo,
                [new("Id", "int", false, true), new("Not", "nvarchar(400)", true, false)], []),
        ],
        YuklenmeZamaniUtc = DateTime.UtcNow,
    };

    private static FtsSekmesiViewModel Vm(
        Func<string, QueryResult> cevapla,
        List<(string Baslik, string Sql, bool Calistir)>? acilanlar = null)
        => new(["KdsDemo"], "KdsDemo",
            _ => Task.FromResult<SemaOnbellegi?>(Onbellek()),
            (sql, _, _) => Task.FromResult(cevapla(sql)),
            (b, s, _) => acilanlar?.Add((b, s, false)),
            (b, s, _) => acilanlar?.Add((b, s, true)));

    [Fact]
    public async Task Fts_kurulu_degilse_turkce_yonlendirme_ve_bolumler_kapali()
    {
        FtsSekmesiViewModel vm = Vm(sql => sql.Contains("IsFullTextInstalled") ? Deger([0]) : Deger());

        await vm.YenileAsync();

        Assert.True(vm.FtsYok);
        Assert.Contains("KURULU DEĞİL", vm.Ozet);
        Assert.Contains("LocalDB", vm.Ozet);      // en yaygın neden adıyla söylenir
        Assert.Empty(vm.Envanter);
    }

    [Fact]
    public async Task Envanter_yuklenir_tablolar_ve_kolon_secenekleri_dolar()
    {
        FtsSekmesiViewModel vm = Vm(sql =>
            sql.Contains("IsFullTextInstalled") ? Deger([1])
            : sql.Contains("fulltext_indexes") ? Deger(
                ["dbo", "Belgeler", "Katalog1", "Icerik (Turkish), Baslik (English)", "AUTO", "Boşta (dolu)"])
            : sql.Contains("fulltext_catalogs") ? Deger(["Katalog1"])
            : Deger());

        await vm.YenileAsync();

        FtsEnvanterSatiri satir = Assert.Single(vm.Envanter);
        Assert.Equal("dbo.Belgeler", satir.TamAd);
        Assert.Contains("1 tabloda", vm.Ozet);

        vm.SeciliAramaTablo = "dbo.Belgeler";
        Assert.Equal(["(tüm kolonlar)", "Icerik", "Baslik"], vm.KolonSecenekleri);

        // Sihirbaz adayları: FTS'li Belgeler ELENİR, Talepler kalır.
        Assert.Equal(["dbo.Talepler"], vm.SihirbazTablolari);
    }

    [Fact]
    public async Task Ara_calistirarak_acar_contains_sql_ile_terim_bos_uyarir()
    {
        var acilanlar = new List<(string Baslik, string Sql, bool Calistir)>();
        FtsSekmesiViewModel vm = Vm(sql =>
            sql.Contains("IsFullTextInstalled") ? Deger([1])
            : sql.Contains("fulltext_indexes") ? Deger(["dbo", "Belgeler", "K", "Icerik (Turkish)", "AUTO", "x"])
            : Deger(), acilanlar);
        await vm.YenileAsync();
        vm.SeciliAramaTablo = "dbo.Belgeler";

        await vm.AraAsync(); // terim boş
        Assert.Contains("terimi yazın", vm.Ozet);
        Assert.Empty(acilanlar);

        vm.Terim = "motor arızası";
        vm.SecilenKipIndex = 0; // kelime/deyim
        await vm.AraAsync();

        (string baslik, string sql, bool calistir) = Assert.Single(acilanlar);
        Assert.Equal("fts-Belgeler", baslik);
        Assert.True(calistir); // arama KOŞARAK açılır
        Assert.Contains("CONTAINS(*, N'\"motor arızası\"')", sql); // (tüm kolonlar) → *
    }

    [Fact]
    public async Task Rankli_arama_anahtar_bulursa_containstable_bulamazsa_duz_ve_uyarir()
    {
        var acilanlar = new List<(string Baslik, string Sql, bool Calistir)>();
        bool anahtarVar = true;
        FtsSekmesiViewModel vm = Vm(sql =>
            sql.Contains("IsFullTextInstalled") ? Deger([1])
            : sql.Contains("fulltext_indexes") ? Deger(["dbo", "Belgeler", "K", "Icerik (Turkish)", "AUTO", "x"])
            : sql.Contains("is_unique") ? (anahtarVar ? Deger(["PK_Belgeler", "Id"]) : Deger())
            : Deger(), acilanlar);
        await vm.YenileAsync();
        vm.SeciliAramaTablo = "dbo.Belgeler";
        vm.Terim = "fatu";
        vm.SecilenKipIndex = 1; // önek
        vm.Rankli = true;

        await vm.AraAsync();
        Assert.Contains("CONTAINSTABLE([dbo].[Belgeler]", acilanlar[^1].Sql);
        Assert.Contains("ft.[KEY] = t.[Id]", acilanlar[^1].Sql);

        anahtarVar = false;
        vm.Rankli = true;
        await vm.AraAsync();
        Assert.Contains("UNIQUE anahtar bulunamadı", vm.Ozet);
        Assert.False(vm.Rankli);                       // dürüstçe düz aramaya düşer
        Assert.Contains("CONTAINS([", acilanlar[^1].Sql.Replace("CONTAINS(*", "CONTAINS([x")); // düz CONTAINS
        Assert.DoesNotContain("CONTAINSTABLE", acilanlar[^1].Sql);
    }

    [Fact]
    public async Task Sihirbaz_anahtar_yoksa_uyarir_varsa_calistirmadan_script_acar()
    {
        var acilanlar = new List<(string Baslik, string Sql, bool Calistir)>();
        bool anahtarVar = false;
        FtsSekmesiViewModel vm = Vm(sql =>
            sql.Contains("IsFullTextInstalled") ? Deger([1])
            : sql.Contains("fulltext_indexes") ? Deger(["dbo", "Belgeler", "K", "Icerik (Turkish)", "AUTO", "x"])
            : sql.Contains("is_unique") ? (anahtarVar ? Deger(["PK_Talepler", "Id"]) : Deger())
            : Deger(), acilanlar);
        await vm.YenileAsync();

        vm.SeciliSihirbazTablo = "dbo.Talepler";
        await Task.Delay(50); // kolon yükleme fire-and-forget
        FtsKolonOgesi kolon = Assert.Single(vm.SihirbazKolonlari); // yalnız METİN kolonu (Not)
        Assert.Equal("Not", kolon.Ad);

        await vm.ScriptUretAsync(); // kolon seçilmedi
        Assert.Contains("kolonu işaretleyin", vm.SihirbazNotu);

        kolon.Secili = true;
        await vm.ScriptUretAsync(); // anahtar index yok
        Assert.Contains("UNIQUE", vm.SihirbazNotu);
        Assert.Empty(acilanlar);

        anahtarVar = true;
        await vm.ScriptUretAsync();
        (string baslik, string sql, bool calistir) = Assert.Single(acilanlar);
        Assert.Equal("fts-kurulum-Talepler", baslik);
        Assert.False(calistir); // DDL ÇALIŞTIRILMADAN açılır — Güvenli Yazma sözleşmesi
        Assert.Contains("CREATE FULLTEXT INDEX ON [dbo].[Talepler]", sql);
        Assert.Contains("[Not] LANGUAGE 1055", sql); // varsayılan dil Türkçe
        Assert.Contains("KEY INDEX [PK_Talepler]", sql);
    }
}
