using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>🔀 Veri Eşitleme üreticisi (madde 4, 2026-08-03): fark → DELETE/UPDATE/INSERT, sıra sabit.</summary>
public class VeriEsitleyiciTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static readonly SemaNesnesi Tablo = new("db", "dbo", "Musteri", SemaNesneTuru.Tablo,
        [new SemaKolonu("Id", "int", false, true), new SemaKolonu("Ad", "nvarchar(50)", false, false)], []);

    private static Dictionary<string, object?> Anahtar(int id) => new(StringComparer.OrdinalIgnoreCase) { ["Id"] = id };

    [Fact]
    public void Uc_fark_turu_dogru_dml_ve_sabit_sira()
    {
        string script = VeriEsitleyici.ScriptUret(Mssql, Tablo, "hedefSrv [HedefDb]", ["Id"],
        [
            new VeriEsitleyici.Kayit(VeriFarkTuru.YalnizSol, Anahtar(1), ["Id", "Ad"], [1, "Ali"]),
            new VeriEsitleyici.Kayit(VeriFarkTuru.YalnizSag, Anahtar(2)),
            new VeriEsitleyici.Kayit(VeriFarkTuru.Farkli, Anahtar(3), ["Id", "Ad"], [3, "Veli'oğlu"]),
        ]);

        Assert.Contains("DELETE FROM [dbo].[Musteri] WHERE [Id] = 2;", script);
        Assert.Contains("UPDATE [dbo].[Musteri] SET [Ad] = N'Veli''oğlu' WHERE [Id] = 3;", script);
        Assert.Contains("INSERT INTO [dbo].[Musteri] ([Id], [Ad]) VALUES (1, N'Ali');", script);

        // Sıra: DELETE → UPDATE → INSERT (başlık yorumuyla karışmasın diye tablolu desen aranır)
        int d = script.IndexOf("DELETE FROM [dbo]", StringComparison.Ordinal);
        int u = script.IndexOf("UPDATE [dbo]", StringComparison.Ordinal);
        int i = script.IndexOf("INSERT INTO [dbo]", StringComparison.Ordinal);
        Assert.True(d < u && u < i);

        Assert.Contains("KAYNAK → HEDEF", script);
        Assert.Contains("hedefSrv [HedefDb]", script);        // hedef script başında açıkça yazar
        Assert.Contains("1 silme · 1 güncelleme · 1 ekleme", script);
        Assert.Contains("ÇALIŞTIRILMADI", script);
    }

    [Fact]
    public void Kaynak_satiri_olmayan_insert_atlanir_ve_acikca_soylenir()
    {
        string script = VeriEsitleyici.ScriptUret(Mssql, Tablo, "h [db]", ["Id"],
            [new VeriEsitleyici.Kayit(VeriFarkTuru.YalnizSol, Anahtar(9))]); // kaynak satır yok

        Assert.DoesNotContain("INSERT INTO", script);
        Assert.Contains("⚠ atlandı", script);
        Assert.Contains("[Id] = 9", script);
    }

    [Fact]
    public void Guncellemede_anahtar_kolon_sete_girmez_ve_null_anahtar_is_null_olur()
    {
        string script = VeriEsitleyici.ScriptUret(Mssql, Tablo, "h [db]", ["Id"],
        [
            new VeriEsitleyici.Kayit(VeriFarkTuru.Farkli, Anahtar(5), ["Id", "Ad"], [5, null]),
            new VeriEsitleyici.Kayit(VeriFarkTuru.YalnizSag,
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Id"] = null }),
        ]);

        Assert.Contains("SET [Ad] = NULL WHERE [Id] = 5;", script);
        Assert.DoesNotContain("SET [Id]", script);            // anahtar SET'e girmez
        Assert.Contains("WHERE [Id] IS NULL;", script);        // NULL anahtar '=' değil IS NULL
    }

    [Fact]
    public void Postgres_lehcesiyle_cift_tirnak_ve_n_oneksiz()
    {
        var pg = new PostgresLehcesi(new DpapiSecretProtector());
        var tablo = new SemaNesnesi("db", "public", "musteri", SemaNesneTuru.Tablo,
            [new SemaKolonu("id", "integer", false, true), new SemaKolonu("ad", "text", false, false)], []);

        string script = VeriEsitleyici.ScriptUret(pg, tablo, "h [db]", ["id"],
            [new VeriEsitleyici.Kayit(VeriFarkTuru.Farkli, new Dictionary<string, object?> { ["id"] = 7 }, ["id", "ad"], [7, "Ayşe"])]);

        Assert.Contains("UPDATE \"public\".\"musteri\" SET \"ad\" = 'Ayşe' WHERE \"id\" = 7;", script);
        Assert.DoesNotContain("N'", script);
    }
}
