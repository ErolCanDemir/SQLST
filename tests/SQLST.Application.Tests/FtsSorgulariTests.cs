using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// 🔎 FTS çekirdeği (v23 S1+S2 — kararlar K1-K4, docs/10-fts-arastirma.md): keşif sorguları,
/// CONTAINS/FREETEXT üretimi (kaçışlar dahil) ve kurulum/silme script'leri. LocalDB FTS
/// desteklemediğinden bu katman SUNUCUSUZ test edilir; canlı tur kullanıcının sunucusunda (K4).
/// </summary>
public class FtsSorgulariTests
{
    [Fact]
    public void Kesif_sorgulari_beklenen_kaynaklara_gider()
    {
        Assert.Contains("IsFullTextInstalled", FtsSorgulari.KuruluMuSorgusu());
        Assert.Contains("sys.fulltext_indexes", FtsSorgulari.EnvanterSorgusu());
        Assert.Contains("TableFulltextPopulateStatus", FtsSorgulari.EnvanterSorgusu());
        // STRING_AGG BİLEREK yok — SQL 2017 öncesi sunucular da desteklenir (STUFF+FOR XML).
        Assert.DoesNotContain("STRING_AGG", FtsSorgulari.EnvanterSorgusu());
        Assert.Contains("sys.fulltext_catalogs", FtsSorgulari.KatalogSorgusu());
    }

    [Fact]
    public void Anahtar_index_sorgusu_tek_kolonlu_unique_notnull_arar()
    {
        string sql = FtsSorgulari.AnahtarIndexSorgusu("talep", "Talepler");
        Assert.Contains("is_unique = 1", sql);
        Assert.Contains("is_nullable = 0", sql);
        Assert.Contains("= 1", sql); // tek kolon şartı
        Assert.Contains("[talep].[Talepler]", sql);
        Assert.Contains("is_primary_key DESC", sql); // PK varsayılan aday
    }

    [Theory]
    [InlineData(FtsAramaKipi.Kelime, "fatura", null, "\"fatura\"")]
    [InlineData(FtsAramaKipi.Onek, "fatu", null, "\"fatu*\"")]
    [InlineData(FtsAramaKipi.Onek, "fatu*", null, "\"fatu*\"")] // kullanıcı * yazdıysa ikilenmez
    [InlineData(FtsAramaKipi.Cekimler, "koş", null, "FORMSOF(INFLECTIONAL, \"koş\")")]
    [InlineData(FtsAramaKipi.Yakinlik, "motor", "arıza", "NEAR((\"motor\", \"arıza\"), 5)")]
    [InlineData(FtsAramaKipi.SerbestMetin, "motor arızası şikayeti", null, "motor arızası şikayeti")]
    public void Kosul_ifadesi_kipe_gore_kurulur(FtsAramaKipi kip, string terim, string? ikinci, string beklenen)
        => Assert.Equal(beklenen, FtsSorgulari.KosulIfadesi(kip, terim, ikinci, 5));

    [Fact]
    public void Arama_sorgusu_contains_ile_kurulur_tirnaklar_kacislanir()
    {
        string sql = FtsSorgulari.AramaSorgusu("dbo", "Belgeler", "Icerik",
            FtsAramaKipi.Kelime, "o'reilly \"deyim\"");

        Assert.Contains("SELECT TOP (100) * FROM [dbo].[Belgeler]", sql);
        // tek tırnak SQL kaçışı, çift tırnak FTS kaçışı — ikisi birden
        Assert.Contains("CONTAINS([Icerik], N'\"o''reilly \"\"deyim\"\"\"')", sql);
    }

    [Fact]
    public void Serbest_metin_freetext_uretir_kolon_yoksa_yildiz()
    {
        string sql = FtsSorgulari.AramaSorgusu("dbo", "Belgeler", null,
            FtsAramaKipi.SerbestMetin, "motor arızası", tavan: 50);
        Assert.Contains("FREETEXT(*, N'motor arızası')", sql);
        Assert.Contains("TOP (50)", sql);
    }

    [Fact]
    public void Rankli_arama_containstable_ile_skorlu_ve_key_eslemeli()
    {
        string sql = FtsSorgulari.AramaSorgusu("dbo", "Belgeler", "Icerik",
            FtsAramaKipi.Onek, "fatu", rankli: true, anahtarKolon: "Id");

        Assert.Contains("CONTAINSTABLE([dbo].[Belgeler], [Icerik], N'\"fatu*\"')", sql);
        Assert.Contains("ft.RANK AS [Skor]", sql);
        Assert.Contains("ft.[KEY] = t.[Id]", sql);
        Assert.Contains("ORDER BY ft.RANK DESC", sql);
    }

    [Fact]
    public void Kurulum_scripti_katalog_bekcisi_dil_ve_key_index_ile()
    {
        string script = FtsSorgulari.KurulumScripti("talep", "Talepler",
            [("SikayetNotu", 1055), ("TalepNo", 1033)],
            katalog: "SqlstKatalog", anahtarIndex: "PK_Talepler", katalogYeni: true);

        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs", script);
        Assert.Contains("CREATE FULLTEXT CATALOG [SqlstKatalog];", script);
        Assert.Contains("CREATE FULLTEXT INDEX ON [talep].[Talepler]", script);
        Assert.Contains("[SikayetNotu] LANGUAGE 1055", script);
        Assert.Contains("[TalepNo] LANGUAGE 1033", script);
        Assert.Contains("KEY INDEX [PK_Talepler] ON [SqlstKatalog]", script);
        Assert.Contains("CHANGE_TRACKING = AUTO", script);
        Assert.Contains("ÇALIŞTIRILMADI", script); // Güvenli Yazma sözleşmesi başlıkta

        // Mevcut katalog seçildiyse CREATE CATALOG hiç yazılmaz.
        string mevcutla = FtsSorgulari.KurulumScripti("talep", "Talepler",
            [("SikayetNotu", 1055)], "Var", "PK_Talepler", katalogYeni: false);
        Assert.DoesNotContain("CREATE FULLTEXT CATALOG", mevcutla);
    }

    [Fact]
    public void Silme_scripti_ve_dil_listesi()
    {
        Assert.Contains("DROP FULLTEXT INDEX ON [dbo].[Belgeler];", FtsSorgulari.SilmeScripti("dbo", "Belgeler"));
        Assert.Equal(1055, FtsSorgulari.DilSecenekleri[0].Lcid); // Türkçe varsayılan aday
    }
}
