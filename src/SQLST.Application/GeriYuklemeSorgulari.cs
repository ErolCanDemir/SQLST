using System.Globalization;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>RESTORE HEADERONLY satırı — dosyadaki bir yedek seti.</summary>
public sealed record YedekSeti(
    int Konum, YedekTuru Tur, string Veritabani, DateTime Bitis, long Boyut, int SurumMajor, int SurumMinor,
    string? Ad, decimal FirstLsn, decimal LastLsn, decimal CheckpointLsn, decimal DatabaseBackupLsn, bool KopyaMi, string? KurtarmaModeli);

/// <summary>RESTORE FILELISTONLY satırı — yedekteki mantıksal dosya. Tur: D veri · L günlük · F/S diğer.</summary>
public sealed record YedekDosyasi(string Mantiksal, string Fiziksel, string Tur, long Boyut);

/// <summary>MOVE satırı: mantıksal ad → sunucudaki hedef yol.</summary>
public sealed record DosyaTasima(string Mantiksal, string Hedef);

/// <summary>Geri yükleme penceresinin sunucudan öğrendiği: sürüm, klasörler, var olan DB'ler, izinler.</summary>
public sealed record GeriYuklemeBilgisi(
    int SurumMajor, string Surum, string? VeriKlasoru, string? GunlukKlasoru,
    IReadOnlyList<string> Veritabanlari, bool OlusturmaIzni, bool Sysadmin)
{
    public bool VeritabaniVar(string ad) => Veritabanlari.Contains(ad, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Pencere seçimleri → script. <paramref name="Konumlar"/> zincir sırasıyla (tam → fark → günlükler → hedef).</summary>
public sealed record GeriYuklemeAyari(
    string Dosya, IReadOnlyList<int> Konumlar, string HedefDb, IReadOnlyList<DosyaTasima> Tasimalar,
    bool Replace, bool BaglantilariKes, bool NoRecovery, bool HedefMevcut);

/// <summary>
/// ⏪ Geri yükleme SAF çekirdeği (v23-S26 S2 — docs/13-yedek-arastirma.md): HEADERONLY/FILELISTONLY/VERIFYONLY sorguları
/// ve ayrıştırma, yedek zinciri (tam → fark → günlük, LSN eşlemesiyle), MOVE hedef önerisi (yeni ada kopya), script
/// (SINGLE_USER sarmalı, FILE = n, MOVE, REPLACE, RECOVERY/NORECOVERY), doğrulama ve hata çevirisi. WPF yalnız gösterir.
/// </summary>
public static class GeriYuklemeSorgulari
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] SistemDbler = ["master", "model", "msdb", "tempdb"];

    public static bool SistemDbMi(string ad) => SistemDbler.Contains(ad.Trim(), StringComparer.OrdinalIgnoreCase);

    // ---- sorgular ----

    public static string BilgiSql() => """
        SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS surum_major,
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) AS surum,
               CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512)) AS veri_klasoru,
               CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(512)) AS gunluk_klasoru,
               STUFF((SELECT N'|' + name FROM sys.databases FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, N'') AS veritabanlari,
               ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE'), 0) AS olusturma_izni,
               ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) AS sysadmin;
        """;

    public static string HeaderSql(string dosya) => $"RESTORE HEADERONLY FROM DISK = {YedekSorgulari.Literal(dosya)};";

    public static string FileListSql(string dosya, int konum) => $"RESTORE FILELISTONLY FROM DISK = {YedekSorgulari.Literal(dosya)} WITH FILE = {konum};";

    public static string VerifySql(string dosya, int konum) => $"RESTORE VERIFYONLY FROM DISK = {YedekSorgulari.Literal(dosya)} WITH FILE = {konum};";

    // ---- okuma ----

    public static GeriYuklemeBilgisi? BilgiOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { Satirlar.Count: > 0 } rs)
            return null;
        YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, rs.Satirlar[0]);
        return new GeriYuklemeBilgisi(s.Int("surum_major"), s.Metin("surum"), s.MetinYaDaNull("veri_klasoru"), s.MetinYaDaNull("gunluk_klasoru"),
            [.. s.Metin("veritabanlari").Split('|', StringSplitOptions.RemoveEmptyEntries)], s.Bool("olusturma_izni"), s.Bool("sysadmin"));
    }

    /// <summary>HEADERONLY: BackupType 1 tam · 5 fark · 2 günlük (diğerleri dosya/kısmi — tam gibi listelenir, zincire girmez).</summary>
    public static IReadOnlyList<YedekSeti> SetleriOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        List<YedekSeti> liste = [];
        foreach (object?[] satir in rs.Satirlar)
        {
            YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, satir);
            liste.Add(new YedekSeti(
                Math.Max(1, s.Int("Position")),
                s.Int("BackupType") switch { 5 => YedekTuru.Fark, 2 => YedekTuru.Gunluk, _ => YedekTuru.Tam },
                s.Metin("DatabaseName"), s.Tarih("BackupFinishDate") ?? DateTime.MinValue, s.Long("BackupSize"),
                s.Int("SoftwareVersionMajor"), s.Int("SoftwareVersionMinor"), s.MetinYaDaNull("BackupName"),
                s.Decimal("FirstLSN"), s.Decimal("LastLSN"), s.Decimal("CheckpointLSN"), s.Decimal("DatabaseBackupLSN"),
                s.Bool("IsCopyOnly"), s.MetinYaDaNull("RecoveryModel")));
        }
        return liste;
    }

    public static IReadOnlyList<YedekDosyasi> DosyalariOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        List<YedekDosyasi> liste = [];
        foreach (object?[] satir in rs.Satirlar)
        {
            YedekSorgulari.Okuyucu s = YedekSorgulari.Satir(rs, satir);
            liste.Add(new YedekDosyasi(s.Metin("LogicalName"), s.Metin("PhysicalName"), s.Metin("Type"), s.Long("Size")));
        }
        return liste;
    }

    // ---- zincir ----

    /// <summary>
    /// Seçilen setin geri yükleme zinciri (aynı dosyadaki setlerden): Tam → kendisi · Fark → tabanı olan tam (CheckpointLSN =
    /// farkın DatabaseBackupLSN'i) + fark · Günlük → tam + (varsa son uyan fark) + aradaki günlükler + kendisi. Taban yoksa
    /// yalnız hedef döner ve uyarı söyler (yarım zincir sessizce koşmaz).
    /// </summary>
    public static (IReadOnlyList<YedekSeti> Zincir, string? Uyari) ZincirKur(IReadOnlyList<YedekSeti> setler, int konum)
    {
        YedekSeti? hedef = setler.FirstOrDefault(s => s.Konum == konum);
        if (hedef is null)
            return ([], "Seçili yedek seti dosyada yok.");
        if (hedef.Tur == YedekTuru.Tam)
            return ([hedef], null);

        YedekSeti? tam = setler.LastOrDefault(s => s.Tur == YedekTuru.Tam && !s.KopyaMi && s.CheckpointLsn == hedef.DatabaseBackupLsn)
                      ?? setler.LastOrDefault(s => s.Tur == YedekTuru.Tam && !s.KopyaMi && s.Konum < hedef.Konum);
        if (tam is null)
            return ([hedef], hedef.Tur == YedekTuru.Fark
                ? "Bu farkın tabanı olan TAM yedek bu dosyada yok — önce o tam yedeği NORECOVERY ile geri yükleyin, sonra bu farkı."
                : "Bu günlüğün tabanı olan TAM yedek bu dosyada yok — önce tam (ve varsa fark) yedeği NORECOVERY ile geri yükleyin.");

        List<YedekSeti> zincir = [tam];
        if (hedef.Tur == YedekTuru.Fark)
        {
            zincir.Add(hedef);
            return (zincir, null);
        }

        YedekSeti? fark = setler.LastOrDefault(s => s.Tur == YedekTuru.Fark && s.DatabaseBackupLsn == tam.CheckpointLsn
                                                    && s.Konum < hedef.Konum && s.LastLsn <= hedef.LastLsn);
        if (fark is not null)
            zincir.Add(fark);
        decimal son = fark?.LastLsn ?? tam.LastLsn;
        foreach (YedekSeti g in setler.Where(s => s.Tur == YedekTuru.Gunluk && s.Konum < hedef.Konum && s.LastLsn > son && s.FirstLsn <= hedef.FirstLsn)
                                       .OrderBy(s => s.Konum))
            zincir.Add(g);
        zincir.Add(hedef);
        return (zincir, null);
    }

    // ---- hedef yollar ----

    /// <summary>
    /// MOVE önerisi: hedef ad kaynakla aynıysa dosyalar yerinde kalır (üzerine yazma); farklıysa (kopya DB) her dosya
    /// sunucunun veri/günlük klasörüne hedef adla yeniden adlandırılır — aynı fiziksel dosya iki DB'de olamaz.
    /// </summary>
    public static IReadOnlyList<DosyaTasima> HedefYollariOner(IReadOnlyList<YedekDosyasi> dosyalar, string kaynakDb, string hedefDb, GeriYuklemeBilgisi? bilgi)
    {
        bool ayni = string.Equals(kaynakDb.Trim(), hedefDb.Trim(), StringComparison.OrdinalIgnoreCase);
        List<DosyaTasima> liste = [];
        int veriSira = 0, gunlukSira = 0;
        foreach (YedekDosyasi d in dosyalar)
        {
            if (ayni)
            {
                liste.Add(new DosyaTasima(d.Mantiksal, d.Fiziksel));
                continue;
            }
            bool gunluk = string.Equals(d.Tur, "L", StringComparison.OrdinalIgnoreCase);
            string klasor = (gunluk ? bilgi?.GunlukKlasoru : null) ?? bilgi?.VeriKlasoru ?? KlasorKismi(d.Fiziksel);
            int nokta = d.Fiziksel.LastIndexOf('.');
            string uzanti = nokta > Math.Max(d.Fiziksel.LastIndexOf('\\'), d.Fiziksel.LastIndexOf('/')) ? d.Fiziksel[nokta..] : (gunluk ? ".ldf" : ".mdf");
            int sira = gunluk ? gunlukSira++ : veriSira++;
            string ad = GuvenliAd(hedefDb.Trim()) + (gunluk ? "_log" : "") + (sira > 0 ? "_" + sira.ToString(Inv) : "") + uzanti;
            liste.Add(new DosyaTasima(d.Mantiksal, YedekSorgulari.YolBirlestir(klasor, ad)));
        }
        return liste;
    }

    private static string KlasorKismi(string yol)
    {
        int i = Math.Max(yol.LastIndexOf('\\'), yol.LastIndexOf('/'));
        return i > 0 ? yol[..i] : "";
    }

    private static string GuvenliAd(string ad)
    {
        var sb = new StringBuilder();
        foreach (char c in ad)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.ToString();
    }

    // ---- script ----

    /// <summary>Tam script: USE master → (bağlantıları kes) → zincir RESTORE'ları (ilkinde MOVE/REPLACE, sonuncuda RECOVERY) → MULTI_USER.</summary>
    public static string Script(GeriYuklemeAyari a, IReadOnlyList<YedekSeti> zincir, DateTime zaman, string sunucu)
    {
        var sb = new StringBuilder();
        string hedef = YedekSorgulari.Koseli(a.HedefDb.Trim());
        sb.Append("USE [master];\n");
        if (a.BaglantilariKes && a.HedefMevcut)
            sb.Append("ALTER DATABASE ").Append(hedef).Append(" SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\n");
        for (int i = 0; i < zincir.Count; i++)
        {
            YedekSeti s = zincir[i];
            bool ilk = i == 0, son = i == zincir.Count - 1;
            sb.Append(s.Tur == YedekTuru.Gunluk ? "RESTORE LOG " : "RESTORE DATABASE ").Append(hedef).Append('\n');
            sb.Append("FROM DISK = ").Append(YedekSorgulari.Literal(a.Dosya)).Append('\n');
            List<string> sec = ["FILE = " + s.Konum.ToString(Inv)];
            if (ilk)
            {
                foreach (DosyaTasima t in a.Tasimalar)
                    sec.Add("MOVE " + YedekSorgulari.Literal(t.Mantiksal) + " TO " + YedekSorgulari.Literal(t.Hedef));
                if (a.Replace) sec.Add("REPLACE");
            }
            sec.Add(son && !a.NoRecovery ? "RECOVERY" : "NORECOVERY");
            sec.Add("STATS = 10");
            sb.Append("WITH ").Append(string.Join(",\n     ", sec)).Append(";\n");
        }
        if (a.BaglantilariKes && a.HedefMevcut && !a.NoRecovery)
            sb.Append("ALTER DATABASE ").Append(hedef).Append(" SET MULTI_USER;\n");
        sb.Append("-- SQLST ").Append(zaman.ToString("dd.MM.yyyy HH:mm", Inv)).Append(" · ").Append(sunucu);
        return sb.ToString();
    }

    /// <summary>Koşmadan önceki engeller (K5/K7): dosya/set/hedef, sistem DB, mevcut üstüne REPLACE'siz, sürüm, izin, boş/çakışan MOVE.</summary>
    public static IReadOnlyList<string> Dogrula(GeriYuklemeAyari a, IReadOnlyList<YedekSeti> zincir, GeriYuklemeBilgisi? bilgi)
    {
        List<string> h = [];
        if (string.IsNullOrWhiteSpace(a.Dosya))
            h.Add("Yedek dosyası seçin (sunucunun diskinde).");
        else if (zincir.Count == 0)
            h.Add("Yedek seti seçin — önce 🔎 Oku ile dosyanın içindekileri listeleyin.");
        string hedef = a.HedefDb.Trim();
        if (hedef.Length == 0)
            h.Add("Hedef veritabanı adı boş.");
        else if (SistemDbMi(hedef))
            h.Add($"'{hedef}' sistem veritabanı — buradan geri yüklenemez (SSMS'te de özel yordam ister).");
        if (a.HedefMevcut && !a.Replace)
            h.Add($"'{hedef}' zaten var — üzerine yazmak için \"Mevcut veritabanının üzerine yaz\" seçin ya da yeni bir ad verin.");
        if (bilgi is not null && zincir.Count > 0)
        {
            int yedekSurum = zincir.Max(s => s.SurumMajor);
            if (yedekSurum > bilgi.SurumMajor)
                h.Add($"Yedek daha yeni bir SQL Server sürümünden (iç sürüm {yedekSurum}) — bu sunucu ({bilgi.SurumMajor}) geri yükleyemez.");
            if (!a.HedefMevcut && !bilgi.OlusturmaIzni && !bilgi.Sysadmin)
                h.Add("Yeni veritabanı oluşturma izniniz yok (dbcreator rolü ya da CREATE ANY DATABASE gerekir).");
        }
        foreach (DosyaTasima t in a.Tasimalar)
            if (string.IsNullOrWhiteSpace(t.Hedef))
                h.Add($"'{t.Mantiksal}' dosyasının hedef yolu boş.");
        var tekrar = a.Tasimalar.GroupBy(t => t.Hedef.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1 && g.Key.Length > 0);
        if (tekrar is not null)
            h.Add($"İki dosya aynı yola gidiyor: {tekrar.Key}");
        return h;
    }

    /// <summary>Sık RESTORE hatalarının Türkçe açıklaması.</summary>
    public static string HataMetni(SqlHata hata) => hata.Numara switch
    {
        3101 => "Veritabanı kullanımda (açık bağlantılar var) — \"Açık bağlantıları kes\" seçeneğini açın.",
        3154 => "Yedek başka bir veritabanına ait — mevcut veritabanının üzerine yazmak için \"üzerine yaz\" (REPLACE) seçin.",
        1834 => "Dosya üzerine yazılamıyor — başka bir veritabanı kullanıyor. Hedef dosya yollarını değiştirin: " + hata.Mesaj,
        5133 or 5170 => "Dosya yolu açılamadı — klasör sunucuda yok ya da SQL Server hizmetinin yazma izni yok: " + hata.Mesaj,
        3201 => "Yedek dosyası açılamadı — yol sunucuda yok ya da okuma izni yok: " + hata.Mesaj,
        3241 => "Dosya geçerli bir SQL Server yedeği değil ya da bozuk: " + hata.Mesaj,
        3169 => "Yedek daha yeni bir SQL Server sürümünden — bu sunucu geri yükleyemez.",
        4305 or 4326 => "Günlük zinciri kopuk — bu günlük yedeği, geri yüklenen son sete bağlanmıyor: " + hata.Mesaj,
        262 or 10 => "İzin yok: " + hata.Mesaj,
        3013 => "Geri yükleme tamamlanamadı: " + hata.Mesaj,
        _ => hata.Mesaj,
    };
}
