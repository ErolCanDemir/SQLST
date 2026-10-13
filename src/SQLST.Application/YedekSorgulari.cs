using System.Globalization;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Yedek türü (docs/13-yedek-arastirma.md §A): Tam · Fark (son tama göre) · İşlem günlüğü (yalnız FULL/BULK_LOGGED).</summary>
public enum YedekTuru { Tam, Fark, Gunluk }

/// <summary>Yedek penceresinin sunucudan tek sorguyla öğrendiği her şey — düğme/seçenek görünürlüğü bundan türer (K7/K8).</summary>
public sealed record YedekSunucuBilgisi(
    string Surum, string Edition, int EngineEdition, string KurtarmaModeli,
    string? YedekKlasoru, string? VeriKlasoru,
    bool SikistirmaVar, bool SikistirmaVarsayilan, bool YedekIzni, bool GunlukIzni)
{
    /// <summary>Azure SQL Database (EngineEdition 5) dosyaya yedek desteklemez.</summary>
    public bool DosyayaYedekVar => EngineEdition != 5;

    /// <summary>SIMPLE kurtarma modelinde günlük yedeği alınamaz (hata 4208).</summary>
    public bool GunlukAlinabilir => !string.Equals(KurtarmaModeli, "SIMPLE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Varsayılan klasör: sunucunun yedek klasörü; LocalDB'de boştur → veri klasörü (K4).</summary>
    public string VarsayilanKlasor => !string.IsNullOrWhiteSpace(YedekKlasoru) ? YedekKlasoru! : VeriKlasoru ?? "";
}

/// <summary>msdb.dbo.backupset satırı — "bu veritabanının son yedekleri".</summary>
public sealed record YedekKaydi(
    DateTime Baslangic, DateTime Bitis, YedekTuru Tur, long BoyutBayt, long? SikistirilmisBayt,
    string Dosya, string Kullanici, bool KopyaMi, string Veritabani = "", int Konum = 1);

/// <summary>xp_dirtree girişi: klasör ya da dosya.</summary>
public sealed record KlasorGirisi(string Ad, bool KlasorMu);

/// <summary>Pencerenin seçimleri → script.</summary>
public sealed record YedekAyari(
    string Veritabani, YedekTuru Tur, string Dosya,
    bool KopyaOnly, bool Checksum, bool Sikistir, bool UzerineYaz,
    string? Ad, string? Aciklama);

/// <summary>sys.dm_exec_requests'ten canlı ilerleme (K3).</summary>
public sealed record YedekIlerleme(double Yuzde, TimeSpan? Kalan);

/// <summary>
/// 💾 Yedek al / geri yükle SAF çekirdeği (v23-S26 S1 — docs/13-yedek-arastirma.md). Sorgu metinleri, script üretimi,
/// doğrulama, sonuç ayrıştırma ve hata çevirisi burada; WPF yalnız gösterir ve köprüden koşturur.
/// </summary>
public static class YedekSorgulari
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- sorgular ----

    /// <summary>Sürüm, edition, kurtarma modeli, klasörler, sıkıştırma, izinler — tek satır (master bağlamında).</summary>
    public static string SunucuBilgisiSql(string veritabani) => $"""
        SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) AS surum,
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition,
               CAST(SERVERPROPERTY('EngineEdition') AS int) AS engine_edition,
               ISNULL((SELECT recovery_model_desc FROM sys.databases WHERE name = {Literal(veritabani)}), N'') AS kurtarma,
               CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(512)) AS yedek_klasoru,
               CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512)) AS veri_klasoru,
               CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) = 4
                      OR CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) LIKE N'%Express%' THEN 0 ELSE 1 END AS sikistirma_var,
               ISNULL((SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'backup compression default'), 0) AS sikistirma_varsayilan,
               ISNULL(HAS_PERMS_BY_NAME({Literal(veritabani)}, N'DATABASE', N'BACKUP DATABASE'), 0) AS yedek_izni,
               ISNULL(HAS_PERMS_BY_NAME({Literal(veritabani)}, N'DATABASE', N'BACKUP LOG'), 0) AS gunluk_izni;
        """;

    /// <summary>Son yedekler (msdb bağlamında): tür, zaman, boyut, dosya, kullanıcı, copy_only, DB adı, dosyadaki konum.
    /// <paramref name="veritabani"/> null → tüm veritabanları (geri yükleme "tarihçeden seç").</summary>
    public static string TarihceSql(string? veritabani, int adet = 10) => $"""
        SELECT TOP ({adet}) b.backup_start_date, b.backup_finish_date, b.type, b.backup_size, b.compressed_backup_size,
               ISNULL(m.physical_device_name, N'') AS dosya, ISNULL(b.user_name, N'') AS kullanici, b.is_copy_only,
               b.database_name AS veritabani, b.position AS konum
        FROM msdb.dbo.backupset b
        LEFT JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = b.media_set_id
        {(veritabani is null ? "" : "WHERE b.database_name = " + Literal(veritabani))}
        ORDER BY b.backup_finish_date DESC;
        """;

    /// <summary>Sunucu diski (K4): bir düzey klasör + dosya listesi.</summary>
    public static string KlasorSql(string yol) => $"EXEC master.sys.xp_dirtree {Literal(yol)}, 1, 1;";

    /// <summary>Canlı ilerleme: yedek komutu bu veritabanı bağlamında koşar → database_id ile eşleşir. Geri yüklemede
    /// bağlam master'dır (hedef henüz yok olabilir) → <paramref name="veritabani"/> null: en son BACKUP/RESTORE isteği.</summary>
    public static string IlerlemeSql(string? veritabani) => $"""
        SELECT TOP (1) percent_complete, estimated_completion_time
        FROM sys.dm_exec_requests
        WHERE command IN (N'BACKUP DATABASE', N'BACKUP LOG', N'RESTORE DATABASE', N'RESTORE LOG')
          {(veritabani is null ? "" : "AND database_id = DB_ID(" + Literal(veritabani) + ")")}
        ORDER BY start_time DESC;
        """;

    // ---- okuma / yardımcı ----

    /// <summary>Okuyucu dış dosyalardan (geri yükleme) da kullanılsın.</summary>
    internal static Okuyucu Satir(ResultSetData rs, object?[] satir) => new(rs, satir);

    // ---- script ----

    public static string Uzanti(YedekTuru tur) => tur == YedekTuru.Gunluk ? "trn" : "bak";

    /// <summary>Otomatik dosya adı: Satis_tam_20261012-1715.bak — ad güvenli karakterlere indirgenir.</summary>
    public static string DosyaAdiOner(string veritabani, YedekTuru tur, DateTime zaman)
    {
        var guvenli = new StringBuilder();
        foreach (char c in veritabani)
            guvenli.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        string turAdi = tur switch { YedekTuru.Fark => "fark", YedekTuru.Gunluk => "gunluk", _ => "tam" };
        return $"{guvenli}_{turAdi}_{zaman.ToString("yyyyMMdd-HHmm", Inv)}.{Uzanti(tur)}";
    }

    /// <summary>Sunucu yolu birleştirme: Linux SQL Server'da '/' (klasör öyle geliyorsa), yoksa '\'.</summary>
    public static string YolBirlestir(string klasor, string dosya)
    {
        if (string.IsNullOrWhiteSpace(klasor)) return dosya;
        char ayrac = klasor.Contains('/') && !klasor.Contains('\\') ? '/' : '\\';
        return klasor.TrimEnd('\\', '/') + ayrac + dosya;
    }

    public static string TurAdi(YedekTuru tur) => tur switch
    {
        YedekTuru.Fark => "Fark", YedekTuru.Gunluk => "Günlük", _ => "Tam",
    };

    /// <summary>BACKUP script'i — pencerede canlı önizleme ve "Script olarak al" aynı metni kullanır (K2).</summary>
    public static string Script(YedekAyari a, DateTime zaman, string sunucu)
    {
        var sb = new StringBuilder(); // satır sonu '\n': editör/sekme LF'e alışık, test platformdan bağımsız
        sb.Append(a.Tur == YedekTuru.Gunluk ? "BACKUP LOG " : "BACKUP DATABASE ").Append(Koseli(a.Veritabani)).Append('\n');
        sb.Append("TO DISK = ").Append(Literal(a.Dosya)).Append('\n');
        List<string> secenekler = [];
        if (a.Tur == YedekTuru.Fark) secenekler.Add("DIFFERENTIAL");
        if (!string.IsNullOrWhiteSpace(a.Ad)) secenekler.Add("NAME = " + Literal(a.Ad!.Trim()));
        if (!string.IsNullOrWhiteSpace(a.Aciklama)) secenekler.Add("DESCRIPTION = " + Literal(a.Aciklama!.Trim()));
        secenekler.Add(a.UzerineYaz ? "INIT" : "NOINIT");
        if (a.Checksum) secenekler.Add("CHECKSUM");
        if (a.Sikistir) secenekler.Add("COMPRESSION");
        if (a.KopyaOnly) secenekler.Add("COPY_ONLY");
        secenekler.Add("STATS = 10");
        sb.Append("WITH ").Append(string.Join(",\n     ", secenekler)).Append(";\n");
        sb.Append("-- SQLST ").Append(zaman.ToString("dd.MM.yyyy HH:mm", Inv)).Append(" · ").Append(sunucu);
        return sb.ToString();
    }

    /// <summary>Koşmadan önce dürüst engeller (K5/K7/K8): boş dosya, SIMPLE'da günlük, izin, Azure, sıkıştırma yok.</summary>
    public static IReadOnlyList<string> Dogrula(YedekAyari a, YedekSunucuBilgisi? bilgi)
    {
        List<string> hatalar = [];
        if (string.IsNullOrWhiteSpace(a.Dosya))
            hatalar.Add("Hedef dosya yolu boş.");
        else if (!a.Dosya.Contains('\\') && !a.Dosya.Contains('/'))
            hatalar.Add("Hedef dosya tam yol olmalı (sunucunun diskinde, ör. D:\\Yedek\\" + a.Dosya + ").");
        if (bilgi is null)
            return hatalar;
        if (!bilgi.DosyayaYedekVar)
            hatalar.Add("Bu sunucuda (Azure SQL Database) dosyaya yedek desteklenmiyor — yedekler otomatik alınır.");
        if (a.Tur == YedekTuru.Gunluk && !bilgi.GunlukAlinabilir)
            hatalar.Add($"Kurtarma modeli {bilgi.KurtarmaModeli} — günlük yedeği alınamaz (Tam ya da Fark seçin).");
        if (!bilgi.YedekIzni)
            hatalar.Add("BACKUP DATABASE izniniz yok (db_backupoperator / db_owner rolü gerekir).");
        else if (a.Tur == YedekTuru.Gunluk && !bilgi.GunlukIzni)
            hatalar.Add("BACKUP LOG izniniz yok.");
        if (a.Sikistir && !bilgi.SikistirmaVar)
            hatalar.Add("Bu sürümde (Express/LocalDB) yedek sıkıştırma yok.");
        return hatalar;
    }

    // ---- okuma ----

    public static YedekSunucuBilgisi? SunucuBilgisiOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { Satirlar.Count: > 0 } rs)
            return null;
        var s = new Okuyucu(rs, rs.Satirlar[0]);
        return new YedekSunucuBilgisi(
            s.Metin("surum"), s.Metin("edition"), s.Int("engine_edition"), s.Metin("kurtarma"),
            s.MetinYaDaNull("yedek_klasoru"), s.MetinYaDaNull("veri_klasoru"),
            s.Bool("sikistirma_var"), s.Bool("sikistirma_varsayilan"), s.Bool("yedek_izni"), s.Bool("gunluk_izni"));
    }

    public static IReadOnlyList<YedekKaydi> TarihceOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        List<YedekKaydi> liste = [];
        foreach (object?[] satir in rs.Satirlar)
        {
            var s = new Okuyucu(rs, satir);
            liste.Add(new YedekKaydi(
                s.Tarih("backup_start_date") ?? DateTime.MinValue, s.Tarih("backup_finish_date") ?? DateTime.MinValue,
                s.Metin("type") switch { "I" => YedekTuru.Fark, "L" => YedekTuru.Gunluk, _ => YedekTuru.Tam },
                s.Long("backup_size"), s.LongYaDaNull("compressed_backup_size"),
                s.Metin("dosya"), s.Metin("kullanici"), s.Bool("is_copy_only"),
                s.Metin("veritabani"), Math.Max(1, s.Int("konum"))));
        }
        return liste;
    }

    /// <summary>xp_dirtree: subdirectory · depth · file (1 = dosya). Klasörler önce, ad sırasıyla.</summary>
    public static IReadOnlyList<KlasorGirisi> KlasorOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { } rs)
            return [];
        List<KlasorGirisi> liste = [];
        foreach (object?[] satir in rs.Satirlar)
        {
            var s = new Okuyucu(rs, satir);
            liste.Add(new KlasorGirisi(s.Metin("subdirectory"), !s.Bool("file")));
        }
        return [.. liste.OrderByDescending(g => g.KlasorMu).ThenBy(g => g.Ad, StringComparer.OrdinalIgnoreCase)];
    }

    public static YedekIlerleme? IlerlemeOku(QueryResult sonuc)
    {
        if (sonuc.ResultSetler.FirstOrDefault() is not { Satirlar.Count: > 0 } rs)
            return null;
        var s = new Okuyucu(rs, rs.Satirlar[0]);
        long kalanMs = s.Long("estimated_completion_time");
        return new YedekIlerleme(s.Double("percent_complete"), kalanMs > 0 ? TimeSpan.FromMilliseconds(kalanMs) : null);
    }

    public static string BoyutMetni(long bayt) => bayt switch
    {
        >= 1L << 30 => (bayt / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bayt / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bayt / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bayt.ToString(CultureInfo.CurrentCulture) + " B",
    };

    /// <summary>Sık BACKUP/RESTORE hatalarının Türkçe açıklaması; bilinmeyende sunucu mesajı aynen.</summary>
    public static string HataMetni(SqlHata hata) => hata.Numara switch
    {
        4208 => "Kurtarma modeli SIMPLE — günlük yedeği alınamaz. Tam ya da Fark alın (ya da modeli FULL yapın).",
        3201 => "Yedek dosyası açılamadı — klasör sunucuda yok ya da SQL Server hizmetinin yazma izni yok: " + hata.Mesaj,
        1844 => "Bu sürümde yedek sıkıştırma yok (Express/LocalDB) — 'Sıkıştır' seçeneğini kaldırın.",
        262 or 10 => "İzin yok: " + hata.Mesaj,
        3035 => "Fark yedeği için önce bir TAM yedek gerekir (bu veritabanının tam yedeği yok).",
        3013 => "Yedekleme tamamlanamadı: " + hata.Mesaj,
        _ => hata.Mesaj,
    };

    // ---- yardımcılar ----

    public static string Literal(string s) => "N'" + s.Replace("'", "''") + "'";

    public static string Koseli(string ad) => "[" + ad.Replace("]", "]]") + "]";

    internal readonly struct Okuyucu(ResultSetData rs, object?[] satir)
    {
        private object? Deger(string kolon)
        {
            for (int i = 0; i < rs.Kolonlar.Count; i++)
                if (string.Equals(rs.Kolonlar[i].Ad, kolon, StringComparison.OrdinalIgnoreCase))
                    return i < satir.Length && satir[i] is not DBNull ? satir[i] : null;
            return null;
        }

        public string Metin(string kolon) => MetinYaDaNull(kolon) ?? "";
        public string? MetinYaDaNull(string kolon) => Deger(kolon) is { } o ? (o as string ?? Convert.ToString(o, Inv)) : null;
        public int Int(string kolon) => Deger(kolon) is IConvertible c ? c.ToInt32(Inv) : 0;
        public long Long(string kolon) => LongYaDaNull(kolon) ?? 0;
        public long? LongYaDaNull(string kolon) => Deger(kolon) is IConvertible c ? c.ToInt64(Inv) : null;
        public double Double(string kolon) => Deger(kolon) is IConvertible c ? c.ToDouble(Inv) : 0;
        public decimal Decimal(string kolon) => Deger(kolon) is IConvertible c ? c.ToDecimal(Inv) : 0;
        public bool Bool(string kolon) => Deger(kolon) switch { bool b => b, IConvertible c => c.ToInt32(Inv) != 0, _ => false };
        public DateTime? Tarih(string kolon) => Deger(kolon) is DateTime t ? t : null;
    }
}
