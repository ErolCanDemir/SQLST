using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// ⏪ Geri yükleme saf çekirdeği (v23-S26 S2): zincir kurma (tam/fark/günlük, LSN eşlemesi, taban yoksa uyarı), MOVE
/// hedef önerisi (aynı ad → yerinde; yeni ad → yeniden adlandırma), script (SINGLE_USER sarmalı, FILE/MOVE/REPLACE/
/// RECOVERY), doğrulama engelleri, hata çevirisi, HEADERONLY/FILELISTONLY ayrıştırma. LocalDB turu ayrı dosyada.
/// </summary>
public class GeriYuklemeSorgulariTests
{
    private static readonly DateTime Z = new(2026, 10, 11, 2, 0, 0);

    private static YedekSeti Set(int konum, YedekTuru tur, decimal first, decimal last, decimal checkpoint, decimal dbBackup, bool kopya = false)
        => new(konum, tur, "Satis", Z.AddHours(konum), 1_000_000, 16, 0, null, first, last, checkpoint, dbBackup, kopya, "FULL");

    // Tam#1 (checkpoint 100) → Fark#2 (taban 100) → Günlük#3 → Günlük#4 ; Tam#5 (checkpoint 500) → Fark#6
    private static readonly YedekSeti[] Setler =
    [
        Set(1, YedekTuru.Tam, 90, 110, 100, 0),
        Set(2, YedekTuru.Fark, 150, 160, 155, 100),
        Set(3, YedekTuru.Gunluk, 110, 170, 0, 100),
        Set(4, YedekTuru.Gunluk, 170, 200, 0, 100),
        Set(5, YedekTuru.Tam, 480, 520, 500, 0),
        Set(6, YedekTuru.Fark, 600, 610, 605, 500),
    ];

    private static GeriYuklemeBilgisi Bilgi(int major = 16, bool olusturma = true, bool sysadmin = false, params string[] dbler)
        => new(major, "16.0.1000", @"C:\SQLData\", @"C:\SQLLog\", dbler.Length == 0 ? ["master", "Satis"] : dbler, olusturma, sysadmin);

    [Fact]
    public void Zincir_tam_tek_fark_tabanli_gunluk_tam_fark_araligi()
    {
        Assert.Equal([1], GeriYuklemeSorgulari.ZincirKur(Setler, 1).Zincir.Select(s => s.Konum));
        Assert.Equal([1, 2], GeriYuklemeSorgulari.ZincirKur(Setler, 2).Zincir.Select(s => s.Konum));
        Assert.Equal([5, 6], GeriYuklemeSorgulari.ZincirKur(Setler, 6).Zincir.Select(s => s.Konum)); // fark #6'nın tabanı #5
        // günlük #4: tam #1 + fark #2 (LastLSN 160 ≤ 170) + günlük #3 (170 > 160) + kendisi
        (IReadOnlyList<YedekSeti> z, string? u) = GeriYuklemeSorgulari.ZincirKur(Setler, 4);
        Assert.Null(u);
        Assert.Equal([1, 2, 3, 4], z.Select(s => s.Konum));
        // günlük #3: tam #1 + fark #2? fark LastLSN 160 ≤ 170 → evet; aradaki günlük yok
        Assert.Equal([1, 2, 3], GeriYuklemeSorgulari.ZincirKur(Setler, 3).Zincir.Select(s => s.Konum));
    }

    [Fact]
    public void Zincir_taban_yoksa_uyari_ve_yalniz_hedef()
    {
        YedekSeti[] yalnizFark = [Set(1, YedekTuru.Fark, 150, 160, 155, 100)];
        (IReadOnlyList<YedekSeti> z, string? u) = GeriYuklemeSorgulari.ZincirKur(yalnizFark, 1);
        Assert.Single(z);
        Assert.Contains("TAM yedek bu dosyada yok", u);
        Assert.Contains("dosyada yok", GeriYuklemeSorgulari.ZincirKur(Setler, 99).Uyari);
        // yalnız-kopya tam, taban olamaz
        YedekSeti[] kopyaTam = [Set(1, YedekTuru.Tam, 90, 110, 100, 0, kopya: true), Set(2, YedekTuru.Fark, 150, 160, 155, 100)];
        Assert.NotNull(GeriYuklemeSorgulari.ZincirKur(kopyaTam, 2).Uyari);
    }

    [Fact]
    public void Hedef_yollar_ayni_ad_yerinde_yeni_ad_klasor_ve_adlandirma()
    {
        YedekDosyasi[] d =
        [
            new("Satis", @"D:\Eski\Satis.mdf", "D", 100), new("Satis_2", @"D:\Eski\Satis_2.ndf", "D", 50), new("Satis_log", @"D:\Eski\Satis_log.ldf", "L", 20),
        ];
        IReadOnlyList<DosyaTasima> ayni = GeriYuklemeSorgulari.HedefYollariOner(d, "Satis", "satis", Bilgi());
        Assert.Equal(@"D:\Eski\Satis.mdf", ayni[0].Hedef);
        Assert.Equal(@"D:\Eski\Satis_log.ldf", ayni[2].Hedef);

        IReadOnlyList<DosyaTasima> yeni = GeriYuklemeSorgulari.HedefYollariOner(d, "Satis", "Satis Test", Bilgi());
        Assert.Equal(@"C:\SQLData\Satis_Test.mdf", yeni[0].Hedef);
        Assert.Equal(@"C:\SQLData\Satis_Test_1.ndf", yeni[1].Hedef);
        Assert.Equal(@"C:\SQLLog\Satis_Test_log.ldf", yeni[2].Hedef);

        // sunucu klasörü bilinmiyorsa orijinal klasör
        Assert.Equal(@"D:\Eski\Kopya.mdf", GeriYuklemeSorgulari.HedefYollariOner(d, "Satis", "Kopya", null)[0].Hedef);
    }

    [Fact]
    public void Script_yeni_ad_tek_set_ve_ustune_yazma_zincir()
    {
        var zincir = new[] { Setler[0] };
        var yeni = new GeriYuklemeAyari(@"D:\Y\s.bak", [1], "Satis_Test",
            [new("Satis", @"C:\SQLData\Satis_Test.mdf"), new("Satis_log", @"C:\SQLLog\Satis_Test_log.ldf")], false, false, false, false);
        string s1 = GeriYuklemeSorgulari.Script(yeni, zincir, Z, "Local");
        Assert.StartsWith("USE [master];\nRESTORE DATABASE [Satis_Test]\nFROM DISK = N'D:\\Y\\s.bak'\nWITH FILE = 1,\n     MOVE N'Satis' TO N'C:\\SQLData\\Satis_Test.mdf',\n     MOVE N'Satis_log' TO N'C:\\SQLLog\\Satis_Test_log.ldf',\n     RECOVERY,\n     STATS = 10;\n", s1);
        Assert.DoesNotContain("SINGLE_USER", s1);
        Assert.DoesNotContain("REPLACE", s1);

        var ustune = new GeriYuklemeAyari(@"D:\Y\s.bak", [1, 2], "Satis",
            [new("Satis", @"D:\Eski\Satis.mdf")], true, true, false, true);
        string s2 = GeriYuklemeSorgulari.Script(ustune, [Setler[0], Setler[1]], Z, "Local");
        Assert.Contains("ALTER DATABASE [Satis] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\n", s2);
        Assert.Contains("WITH FILE = 1,\n     MOVE N'Satis' TO N'D:\\Eski\\Satis.mdf',\n     REPLACE,\n     NORECOVERY,", s2); // ilk set: MOVE+REPLACE, NORECOVERY
        Assert.Contains("WITH FILE = 2,\n     RECOVERY,", s2);                                                          // son set: RECOVERY, MOVE yok
        Assert.Contains("ALTER DATABASE [Satis] SET MULTI_USER;", s2);

        string s3 = GeriYuklemeSorgulari.Script(ustune with { NoRecovery = true }, [Setler[0], Setler[2]], Z, "Local");
        Assert.Contains("RESTORE LOG [Satis]", s3);
        Assert.DoesNotContain("RECOVERY,\n", s3.Replace("NORECOVERY", ""));
        Assert.DoesNotContain("MULTI_USER", s3); // NORECOVERY'de MULTI_USER dönülmez (DB RESTORING'de)
    }

    [Fact]
    public void Dogrula_engeller()
    {
        var zincir = new[] { Setler[0] };
        GeriYuklemeAyari a = new(@"D:\Y\s.bak", [1], "Satis_Test", [new("Satis", @"C:\x.mdf")], false, false, false, false);
        Assert.Empty(GeriYuklemeSorgulari.Dogrula(a, zincir, Bilgi()));
        Assert.Contains("dosyası seçin", GeriYuklemeSorgulari.Dogrula(a with { Dosya = "" }, zincir, Bilgi())[0]);
        Assert.Contains("Oku", GeriYuklemeSorgulari.Dogrula(a, [], Bilgi())[0]);
        Assert.Contains("boş", GeriYuklemeSorgulari.Dogrula(a with { HedefDb = " " }, zincir, Bilgi())[0]);
        Assert.Contains("sistem veritabanı", GeriYuklemeSorgulari.Dogrula(a with { HedefDb = "msdb" }, zincir, Bilgi())[0]);
        Assert.Contains("üzerine yaz", GeriYuklemeSorgulari.Dogrula(a with { HedefDb = "Satis", HedefMevcut = true }, zincir, Bilgi())[0]);
        Assert.Empty(GeriYuklemeSorgulari.Dogrula(a with { HedefDb = "Satis", HedefMevcut = true, Replace = true }, zincir, Bilgi()));
        Assert.Contains("daha yeni", GeriYuklemeSorgulari.Dogrula(a, zincir, Bilgi(major: 15))[0]);
        Assert.Contains("izniniz yok", GeriYuklemeSorgulari.Dogrula(a, zincir, Bilgi(olusturma: false))[0]);
        Assert.Empty(GeriYuklemeSorgulari.Dogrula(a, zincir, Bilgi(olusturma: false, sysadmin: true)));
        Assert.Contains("hedef yolu boş", GeriYuklemeSorgulari.Dogrula(a with { Tasimalar = [new("Satis", "")] }, zincir, Bilgi())[0]);
        Assert.Contains("aynı yola", GeriYuklemeSorgulari.Dogrula(a with { Tasimalar = [new("a", @"C:\x.mdf"), new("b", @"c:\X.MDF")] }, zincir, Bilgi())[0]);
    }

    [Fact]
    public void Headeronly_ve_filelistonly_ayristirma()
    {
        var h = new ResultSetData
        {
            Kolonlar = [new("BackupName", "x"), new("BackupType", "x"), new("Position", "x"), new("DatabaseName", "x"), new("BackupFinishDate", "x"),
                        new("BackupSize", "x"), new("FirstLSN", "x"), new("LastLSN", "x"), new("CheckpointLSN", "x"), new("DatabaseBackupLSN", "x"),
                        new("SoftwareVersionMajor", "x"), new("SoftwareVersionMinor", "x"), new("IsCopyOnly", "x"), new("RecoveryModel", "x")],
            Satirlar = [["tam", (byte)1, (short)1, "Satis", Z, 1_900_000_000m, 90m, 110m, 100m, 0m, 16, 0, false, "FULL"],
                        ["fark", (byte)5, (short)2, "Satis", Z.AddHours(12), 120_000_000m, 150m, 160m, 155m, 100m, 16, 0, false, "FULL"],
                        [DBNull.Value, (byte)2, (short)3, "Satis", Z.AddHours(13), 9_000_000m, 160m, 170m, 0m, 100m, 16, 0, false, "FULL"]],
        };
        IReadOnlyList<YedekSeti> s = GeriYuklemeSorgulari.SetleriOku(new QueryResult { ResultSetler = [h] });
        Assert.Equal([YedekTuru.Tam, YedekTuru.Fark, YedekTuru.Gunluk], s.Select(x => x.Tur));
        Assert.Equal(100m, s[1].DatabaseBackupLsn);
        Assert.Null(s[2].Ad);
        Assert.Equal(16, s[0].SurumMajor);

        var f = new ResultSetData
        {
            Kolonlar = [new("LogicalName", "x"), new("PhysicalName", "x"), new("Type", "x"), new("Size", "x")],
            Satirlar = [["Satis", @"D:\Eski\Satis.mdf", "D", 2_400_000_000m], ["Satis_log", @"D:\Eski\Satis_log.ldf", "L", 512_000_000m]],
        };
        IReadOnlyList<YedekDosyasi> d = GeriYuklemeSorgulari.DosyalariOku(new QueryResult { ResultSetler = [f] });
        Assert.Equal("L", d[1].Tur);
        Assert.Equal(2_400_000_000, d[0].Boyut);

        var b = new ResultSetData
        {
            Kolonlar = [new("surum_major", "x"), new("surum", "x"), new("veri_klasoru", "x"), new("gunluk_klasoru", "x"), new("veritabanlari", "x"), new("olusturma_izni", "x"), new("sysadmin", "x")],
            Satirlar = [[16, "16.0.1", @"C:\D\", DBNull.Value, "master|tempdb|Satis", 1, 0]],
        };
        GeriYuklemeBilgisi bilgi = GeriYuklemeSorgulari.BilgiOku(new QueryResult { ResultSetler = [b] })!;
        Assert.True(bilgi.VeritabaniVar("satis"));
        Assert.False(bilgi.VeritabaniVar("Satis_Test"));
        Assert.Null(bilgi.GunlukKlasoru);
    }

    [Fact]
    public void Sorgular_ve_hata_metinleri()
    {
        Assert.Equal("RESTORE HEADERONLY FROM DISK = N'D:\\Y\\s''.bak';", GeriYuklemeSorgulari.HeaderSql(@"D:\Y\s'.bak"));
        Assert.Equal("RESTORE FILELISTONLY FROM DISK = N'D:\\Y\\s.bak' WITH FILE = 2;", GeriYuklemeSorgulari.FileListSql(@"D:\Y\s.bak", 2));
        Assert.Equal("RESTORE VERIFYONLY FROM DISK = N'D:\\Y\\s.bak' WITH FILE = 1;", GeriYuklemeSorgulari.VerifySql(@"D:\Y\s.bak", 1));
        Assert.True(GeriYuklemeSorgulari.SistemDbMi("MSDB"));
        Assert.False(GeriYuklemeSorgulari.SistemDbMi("Satis"));
        Assert.Contains("bağlantıları kes", GeriYuklemeSorgulari.HataMetni(new SqlHata("x", 3101, 1, 16)));
        Assert.Contains("REPLACE", GeriYuklemeSorgulari.HataMetni(new SqlHata("x", 3154, 1, 16)));
        Assert.Contains("yollarını değiştirin", GeriYuklemeSorgulari.HataMetni(new SqlHata("x", 1834, 1, 16)));
        Assert.Contains("daha yeni", GeriYuklemeSorgulari.HataMetni(new SqlHata("x", 3169, 1, 16)));
        Assert.Contains("zinciri kopuk", GeriYuklemeSorgulari.HataMetni(new SqlHata("x", 4305, 1, 16)));
    }
}
