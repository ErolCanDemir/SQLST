using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// BF-3 (2026-07-27): Excel/tablo farkından üretilen INSERT/UPDATE/DELETE — Güvenli Yazma bandına
/// gidecek motor-doğru metin. Sıra DELETE→UPDATE→INSERT; NULL anahtar IS NULL; silme yalnız tam
/// eşitlemede; literaller motorun kurallarında (MSSQL: N'…', 1/0).
/// </summary>
public class FarkDmlUreticiTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static Dictionary<string, object?> S(params (string, object?)[] alanlar)
        => alanlar.ToDictionary(a => a.Item1, a => a.Item2);

    /// <summary>ExcelFarkKarsilastirici ile gerçek farkı kurar (uçtan uca gerçekçi senaryo).</summary>
    private static TabloFarki Fark()
    {
        var kaynak = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Id", 1), ("Ad", "Ahmet"), ("Sehir", "Izmir")),   // değişmedi
            S(("Id", 2), ("Ad", "Ayse"), ("Sehir", "Bursa")),    // Sehir değişti
            S(("Id", 4), ("Ad", "Veli"), ("Sehir", "Ankara")),   // yeni
        };
        var hedef = new IReadOnlyDictionary<string, object?>[]
        {
            S(("Id", 1), ("Ad", "Ahmet"), ("Sehir", "Izmir")),
            S(("Id", 2), ("Ad", "Ayse"), ("Sehir", "Istanbul")), // eski Sehir
            S(("Id", 3), ("Ad", "Can"), ("Sehir", "Adana")),     // Excel'de yok → silinecek
        };
        return ExcelFarkKarsilastirici.Karsilastir(kaynak, hedef, ["Id"], ["Ad", "Sehir"]);
    }

    [Fact]
    public void Insert_maplı_tüm_kolonları_yazar()
    {
        IReadOnlyList<string> k = FarkDmlUretici.Uret(Mssql, "dbo", "Musteri", ["Id"], Fark(), silmeDahil: false);

        Assert.Contains("INSERT INTO [dbo].[Musteri] ([Id], [Ad], [Sehir]) VALUES (4, N'Veli', N'Ankara');", k);
    }

    [Fact]
    public void Update_yalnız_değişen_kolonu_SET_eder_anahtarı_WHERE_e_koyar()
    {
        IReadOnlyList<string> k = FarkDmlUretici.Uret(Mssql, "dbo", "Musteri", ["Id"], Fark(), silmeDahil: false);

        Assert.Contains("UPDATE [dbo].[Musteri] SET [Sehir] = N'Bursa' WHERE [Id] = 2;", k);
        // Değişmeyen Ad, SET'e girmez
        Assert.DoesNotContain(k, x => x.Contains("[Ad] = N'Ayse'"));
    }

    [Fact]
    public void Silme_yalnız_tam_eşitlemede_üretilir()
    {
        IReadOnlyList<string> kapali = FarkDmlUretici.Uret(Mssql, "dbo", "Musteri", ["Id"], Fark(), silmeDahil: false);
        Assert.DoesNotContain(kapali, x => x.StartsWith("DELETE", StringComparison.Ordinal));

        IReadOnlyList<string> acik = FarkDmlUretici.Uret(Mssql, "dbo", "Musteri", ["Id"], Fark(), silmeDahil: true);
        Assert.Contains("DELETE FROM [dbo].[Musteri] WHERE [Id] = 3;", acik);
    }

    [Fact]
    public void Sıra_DELETE_UPDATE_INSERT()
    {
        IReadOnlyList<string> k = FarkDmlUretici.Uret(Mssql, "dbo", "Musteri", ["Id"], Fark(), silmeDahil: true);

        int d = IndexOfPrefix(k, "DELETE");
        int u = IndexOfPrefix(k, "UPDATE");
        int i = IndexOfPrefix(k, "INSERT");
        Assert.True(d >= 0 && u >= 0 && i >= 0);
        Assert.True(d < u && u < i, $"Beklenen sıra DELETE<UPDATE<INSERT; gelen: {string.Join(" | ", k)}");
    }

    [Fact]
    public void Bileşik_anahtar_AND_ile_bağlanır()
    {
        var degisen = new DegisenSatir(
            S(("Ulke", "TR"), ("Kod", 34)),
            new Dictionary<string, (object?, object?)> { ["Ad"] = ("Eski", "Yeni") },
            S(("Ulke", "TR"), ("Kod", 34), ("Ad", "Yeni")));
        var fark = new TabloFarki([], [degisen], []);

        IReadOnlyList<string> k = FarkDmlUretici.Uret(Mssql, "dbo", "Sehir", ["Ulke", "Kod"], fark, silmeDahil: false);

        Assert.Equal("UPDATE [dbo].[Sehir] SET [Ad] = N'Yeni' WHERE [Ulke] = N'TR' AND [Kod] = 34;", k[0]);
    }

    [Fact]
    public void Null_anahtar_IS_NULL_olur()
    {
        var silinen = new FarkSatiri(S(("Kod", null), ("Ad", "X")));
        var fark = new TabloFarki([], [], [silinen]);

        IReadOnlyList<string> k = FarkDmlUretici.Uret(Mssql, "dbo", "T", ["Kod"], fark, silmeDahil: true);

        Assert.Equal("DELETE FROM [dbo].[T] WHERE [Kod] IS NULL;", k[0]);
    }

    [Fact]
    public void Boş_fark_boş_liste()
    {
        var fark = new TabloFarki([], [], []);
        Assert.Empty(FarkDmlUretici.Uret(Mssql, "dbo", "T", ["Id"], fark, silmeDahil: true));
    }

    [Fact]
    public void Anahtarsız_çağrı_hata()
    {
        var fark = new TabloFarki([], [], []);
        Assert.Throws<ArgumentException>(() => FarkDmlUretici.Uret(Mssql, "dbo", "T", [], fark, silmeDahil: false));
    }

    private static int IndexOfPrefix(IReadOnlyList<string> komutlar, string onek)
    {
        for (int i = 0; i < komutlar.Count; i++)
            if (komutlar[i].StartsWith(onek, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
