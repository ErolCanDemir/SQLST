using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// 💾 Yedek al saf çekirdeği (v23-S26 S1 — docs/13-yedek-arastirma.md): script üretimi (3 tür + seçenekler),
/// dosya adı, sunucu yolu, doğrulama engelleri (K5/K7/K8), sonuç ayrıştırma, hata çevirisi. Sunucusuz; LocalDB turu
/// YedekLocalDbTests'te.
/// </summary>
public class YedekSorgulariTests
{
    private static readonly DateTime Zaman = new(2026, 10, 12, 17, 15, 0);

    private static YedekAyari Ayar(YedekTuru tur = YedekTuru.Tam, string dosya = @"D:\Yedek\Satis.bak",
        bool kopya = false, bool checksum = true, bool sikistir = false, bool init = true, string? ad = "Satis tam", string? aciklama = null)
        => new("Satis", tur, dosya, kopya, checksum, sikistir, init, ad, aciklama);

    private static YedekSunucuBilgisi Bilgi(string kurtarma = "FULL", bool sikistirmaVar = true, bool izin = true, bool gunlukIzni = true,
        int engine = 3, string? yedekKlasoru = @"D:\Yedek", string? veriKlasoru = @"C:\Data")
        => new("16.0.1000", "Developer Edition", engine, kurtarma, yedekKlasoru, veriKlasoru, sikistirmaVar, false, izin, gunlukIzni);

    [Fact]
    public void Tam_yedek_scripti_secenekler_sirali()
    {
        string s = YedekSorgulari.Script(Ayar(sikistir: true, kopya: true, aciklama: "Sürüm 0.30 öncesi"), Zaman, "Local");

        Assert.StartsWith("BACKUP DATABASE [Satis]\nTO DISK = N'D:\\Yedek\\Satis.bak'\nWITH NAME = N'Satis tam',", s);
        Assert.Contains("DESCRIPTION = N'Sürüm 0.30 öncesi'", s);
        Assert.Contains("INIT,", s);
        Assert.Contains("CHECKSUM,", s);
        Assert.Contains("COMPRESSION,", s);
        Assert.Contains("COPY_ONLY,", s);
        Assert.EndsWith("STATS = 10;\n-- SQLST 12.10.2026 17:15 · Local", s);
        Assert.DoesNotContain("DIFFERENTIAL", s);
    }

    [Fact]
    public void Fark_DIFFERENTIAL_gunluk_BACKUP_LOG_noinit_ekler()
    {
        string fark = YedekSorgulari.Script(Ayar(YedekTuru.Fark, init: false, checksum: false), Zaman, "Local");
        string gunluk = YedekSorgulari.Script(Ayar(YedekTuru.Gunluk, dosya: @"D:\Yedek\Satis.trn", ad: null), Zaman, "Local");

        Assert.StartsWith("BACKUP DATABASE [Satis]", fark);
        Assert.Contains("WITH DIFFERENTIAL,", fark);
        Assert.Contains("NOINIT", fark);
        Assert.DoesNotContain("CHECKSUM", fark);
        Assert.StartsWith("BACKUP LOG [Satis]\nTO DISK = N'D:\\Yedek\\Satis.trn'\nWITH INIT,", gunluk);
        Assert.DoesNotContain("NAME =", gunluk);
    }

    [Fact]
    public void Tirnak_ve_koseli_kacislari()
    {
        string s = YedekSorgulari.Script(new YedekAyari("Sa]tis", YedekTuru.Tam, @"D:\O'Neil\x.bak", false, false, false, true, "Ad'ı", null), Zaman, "s");

        Assert.Contains("BACKUP DATABASE [Sa]]tis]", s);
        Assert.Contains("N'D:\\O''Neil\\x.bak'", s);
        Assert.Contains("NAME = N'Ad''ı'", s);
    }

    [Fact]
    public void Dosya_adi_tur_zaman_uzanti_guvenli_karakter()
    {
        Assert.Equal("Satis_tam_20261012-1715.bak", YedekSorgulari.DosyaAdiOner("Satis", YedekTuru.Tam, Zaman));
        Assert.Equal("Satis_fark_20261012-1715.bak", YedekSorgulari.DosyaAdiOner("Satis", YedekTuru.Fark, Zaman));
        Assert.Equal("Satis_gunluk_20261012-1715.trn", YedekSorgulari.DosyaAdiOner("Satis", YedekTuru.Gunluk, Zaman));
        Assert.Equal("Sat_s_DB_tam_20261012-1715.bak", YedekSorgulari.DosyaAdiOner("Sat/s DB", YedekTuru.Tam, Zaman));
    }

    [Theory]
    [InlineData(@"D:\Yedek", "a.bak", @"D:\Yedek\a.bak")]
    [InlineData(@"D:\Yedek\", "a.bak", @"D:\Yedek\a.bak")]
    [InlineData("/var/opt/mssql/backup", "a.bak", "/var/opt/mssql/backup/a.bak")]
    [InlineData("", "a.bak", "a.bak")]
    public void Yol_birlestirme_sunucu_ayraci(string klasor, string dosya, string beklenen)
        => Assert.Equal(beklenen, YedekSorgulari.YolBirlestir(klasor, dosya));

    [Fact]
    public void Dogrula_engeller()
    {
        Assert.Contains("boş", YedekSorgulari.Dogrula(Ayar(dosya: ""), Bilgi())[0]);
        Assert.Contains("tam yol", YedekSorgulari.Dogrula(Ayar(dosya: "satis.bak"), Bilgi())[0]);
        Assert.Contains("SIMPLE", YedekSorgulari.Dogrula(Ayar(YedekTuru.Gunluk), Bilgi(kurtarma: "SIMPLE"))[0]);
        Assert.Contains("izniniz yok", YedekSorgulari.Dogrula(Ayar(), Bilgi(izin: false))[0]);
        Assert.Contains("BACKUP LOG", YedekSorgulari.Dogrula(Ayar(YedekTuru.Gunluk), Bilgi(gunlukIzni: false))[0]);
        Assert.Contains("sıkıştırma yok", YedekSorgulari.Dogrula(Ayar(sikistir: true), Bilgi(sikistirmaVar: false))[0]);
        Assert.Contains("Azure", YedekSorgulari.Dogrula(Ayar(), Bilgi(engine: 5))[0]);
        Assert.Empty(YedekSorgulari.Dogrula(Ayar(), Bilgi()));
        Assert.Empty(YedekSorgulari.Dogrula(Ayar(), null)); // bilgi gelmeden yalnız dosya denetimi
    }

    [Fact]
    public void Sunucu_bilgisi_turetilen_bayraklar()
    {
        YedekSunucuBilgisi localdb = Bilgi(kurtarma: "SIMPLE", sikistirmaVar: false, engine: 4, yedekKlasoru: "", veriKlasoru: @"C:\Users\x\");
        Assert.False(localdb.GunlukAlinabilir);
        Assert.True(localdb.DosyayaYedekVar);
        Assert.Equal(@"C:\Users\x\", localdb.VarsayilanKlasor); // yedek klasörü boş → veri klasörü (K4)
        Assert.Equal(@"D:\Yedek", Bilgi().VarsayilanKlasor);
        Assert.False(Bilgi(engine: 5).DosyayaYedekVar);
    }

    [Fact]
    public void Sunucu_bilgisi_ve_tarihce_okuma()
    {
        var bilgiRs = new ResultSetData
        {
            Kolonlar = [new("surum", "nvarchar"), new("edition", "nvarchar"), new("engine_edition", "int"), new("kurtarma", "nvarchar"),
                        new("yedek_klasoru", "nvarchar"), new("veri_klasoru", "nvarchar"), new("sikistirma_var", "int"),
                        new("sikistirma_varsayilan", "int"), new("yedek_izni", "int"), new("gunluk_izni", "int")],
            Satirlar = [["17.0.4025.3", "Express Edition (64-bit)", 4, "SIMPLE", DBNull.Value, @"C:\Users\x\", 0, 0, 1, 1]],
        };
        YedekSunucuBilgisi b = YedekSorgulari.SunucuBilgisiOku(new QueryResult { ResultSetler = [bilgiRs] })!;
        Assert.Equal(4, b.EngineEdition);
        Assert.Null(b.YedekKlasoru);
        Assert.False(b.SikistirmaVar);
        Assert.True(b.YedekIzni);

        var tarihceRs = new ResultSetData
        {
            Kolonlar = [new("backup_start_date", "datetime"), new("backup_finish_date", "datetime"), new("type", "char"),
                        new("backup_size", "numeric"), new("compressed_backup_size", "numeric"), new("dosya", "nvarchar"),
                        new("kullanici", "nvarchar"), new("is_copy_only", "bit")],
            Satirlar = [[Zaman, Zaman.AddSeconds(30), "D", 1_900_000_000m, 600_000_000m, @"D:\Y\a.bak", "sa", false],
                        [Zaman, Zaman.AddSeconds(2), "L", 96_000_000m, DBNull.Value, @"D:\Y\a.trn", "SQLAgent", true]],
        };
        IReadOnlyList<YedekKaydi> k = YedekSorgulari.TarihceOku(new QueryResult { ResultSetler = [tarihceRs] });
        Assert.Equal(2, k.Count);
        Assert.Equal(YedekTuru.Tam, k[0].Tur);
        Assert.Equal(1_900_000_000, k[0].BoyutBayt);
        Assert.Equal(600_000_000, k[0].SikistirilmisBayt);
        Assert.Equal(YedekTuru.Gunluk, k[1].Tur);
        Assert.Null(k[1].SikistirilmisBayt);
        Assert.True(k[1].KopyaMi);
    }

    [Fact]
    public void Klasor_okuma_klasorler_once_ad_sirali_ilerleme_okuma()
    {
        var rs = new ResultSetData
        {
            Kolonlar = [new("subdirectory", "nvarchar"), new("depth", "int"), new("file", "int")],
            Satirlar = [["zeta.bak", 1, 1], ["Yedek", 1, 0], ["alpha.trn", 1, 1], ["Arsiv", 1, 0]],
        };
        IReadOnlyList<KlasorGirisi> g = YedekSorgulari.KlasorOku(new QueryResult { ResultSetler = [rs] });
        Assert.Equal(["Arsiv", "Yedek", "alpha.trn", "zeta.bak"], g.Select(x => x.Ad));
        Assert.True(g[0].KlasorMu); Assert.False(g[3].KlasorMu);

        var ilerlemeRs = new ResultSetData
        {
            Kolonlar = [new("percent_complete", "real"), new("estimated_completion_time", "bigint")],
            Satirlar = [[62.5f, 41_000L]],
        };
        YedekIlerleme i = YedekSorgulari.IlerlemeOku(new QueryResult { ResultSetler = [ilerlemeRs] })!;
        Assert.Equal(62.5, i.Yuzde, 1);
        Assert.Equal(TimeSpan.FromSeconds(41), i.Kalan);
        Assert.Null(YedekSorgulari.IlerlemeOku(new QueryResult { ResultSetler = [new ResultSetData { Kolonlar = ilerlemeRs.Kolonlar, Satirlar = [] }] }));
    }

    [Fact]
    public void Boyut_ve_hata_metinleri()
    {
        Assert.Equal("1,8 GB", YedekSorgulari.BoyutMetni(1_900_000_000).Replace('.', ','));
        Assert.Equal("95 MB", YedekSorgulari.BoyutMetni(100_000_000)); // 100e6 / 2^20 = 95,4
        Assert.Contains("SIMPLE", YedekSorgulari.HataMetni(new SqlHata("x", 4208, 1, 16)));
        Assert.Contains("açılamadı", YedekSorgulari.HataMetni(new SqlHata("Cannot open backup device", 3201, 1, 16)));
        Assert.Contains("sıkıştırma yok", YedekSorgulari.HataMetni(new SqlHata("x", 1844, 1, 16)));
        Assert.Contains("TAM yedek", YedekSorgulari.HataMetni(new SqlHata("x", 3035, 1, 16)));
        Assert.Equal("bilinmeyen", YedekSorgulari.HataMetni(new SqlHata("bilinmeyen", 99999, 1, 16)));
    }

    [Fact]
    public void Sorgu_metinleri_db_adini_literal_tasir()
    {
        Assert.Contains("HAS_PERMS_BY_NAME(N'Sa''tis', N'DATABASE', N'BACKUP DATABASE')", YedekSorgulari.SunucuBilgisiSql("Sa'tis"));
        Assert.Contains("b.database_name = N'Satis'", YedekSorgulari.TarihceSql("Satis"));
        Assert.Equal("EXEC master.sys.xp_dirtree N'D:\\Yedek', 1, 1;", YedekSorgulari.KlasorSql(@"D:\Yedek"));
        Assert.Contains("DB_ID(N'Satis')", YedekSorgulari.IlerlemeSql("Satis"));
    }
}
