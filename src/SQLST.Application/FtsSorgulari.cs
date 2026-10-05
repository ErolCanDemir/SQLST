using System.Text;

namespace SQLST.Application;

/// <summary>Sorgu yardımcısının arama kipleri (10-fts-arastirma §C/S1).</summary>
public enum FtsAramaKipi
{
    /// <summary>Tek kelime ya da AND/OR'suz deyim — <c>CONTAINS(kolon, '"terim"')</c>.</summary>
    Kelime,

    /// <summary>Önek: <c>'"fatu*"'</c> — "fatura, faturalama…" hepsi.</summary>
    Onek,

    /// <summary>İki terimin yakınlığı: <c>NEAR((a, b), mesafe)</c>.</summary>
    Yakinlik,

    /// <summary>Çekimler: <c>FORMSOF(INFLECTIONAL, terim)</c> — koştu/koşacak/koşuyor…</summary>
    Cekimler,

    /// <summary>Serbest metin — <c>FREETEXT</c>: çekim+eşanlamlı gevşek eşleşme.</summary>
    SerbestMetin,
}

/// <summary>
/// 🔎 Full Text Search'ün SAF çekirdeği (v23 — araştırma: docs/10-fts-arastirma.md; kararlar
/// K1-K4: yalnız MSSQL · S1+S2 · canlı doğrulama kullanıcıda): keşif/envanter sorguları,
/// CONTAINS/FREETEXT sorgu üretimi ve FULLTEXT CATALOG/INDEX kurulum script'leri. UI/IO yok.
///
/// ⚠ LocalDB FTS DESTEKLEMEZ — çağıran, <see cref="KuruluMuSorgusu"/> 0 dönerse Türkçe
/// yönlendirme gösterir (yarım özellik yok: düğme gizlenmez, yol gösterilir — profil sunucusu
/// değişebilir). Üretilen DDL ÇALIŞTIRILMAZ, sekmede açılır (Güvenli Yazma deseni).
/// </summary>
public static class FtsSorgulari
{
    /// <summary>Sunucuda FTS bileşeni kurulu mu — 1/0 (LocalDB'de 0).</summary>
    public static string KuruluMuSorgusu()
        => "SELECT CAST(ISNULL(SERVERPROPERTY('IsFullTextInstalled'), 0) AS int) AS kurulu;";

    /// <summary>
    /// FULLTEXT INDEX envanteri: şema · tablo · katalog · kolonlar(+dil) · değişiklik izleme ·
    /// doldurma durumu. Kolon birleştirme STRING_AGG değil STUFF+FOR XML — SQL 2017 öncesi
    /// sunucular da desteklensin (FTS 2008'den beri var; sürüm şartı koymuyoruz).
    /// </summary>
    public static string EnvanterSorgusu() => """
        SELECT s.name AS sema, t.name AS tablo, c.name AS katalog,
               STUFF((SELECT ', ' + col.name + ' (' + ISNULL(fl.name, '?') + ')'
                      FROM sys.fulltext_index_columns fic
                      JOIN sys.columns col ON col.object_id = fic.object_id AND col.column_id = fic.column_id
                      LEFT JOIN sys.fulltext_languages fl ON fl.lcid = fic.language_id
                      WHERE fic.object_id = fi.object_id
                      ORDER BY col.name
                      FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS kolonlar,
               fi.change_tracking_state_desc AS izleme,
               CASE OBJECTPROPERTYEX(fi.object_id, 'TableFulltextPopulateStatus')
                    WHEN 0 THEN N'Boşta (dolu)' WHEN 1 THEN N'Tam doldurma sürüyor'
                    WHEN 2 THEN N'Artımlı doldurma sürüyor' WHEN 3 THEN N'İzleme yayılıyor'
                    WHEN 4 THEN N'Arka plan güncelleme' WHEN 5 THEN N'Durdurulmuş/azaltılmış'
                    ELSE N'?' END AS doldurma
        FROM sys.fulltext_indexes fi
        JOIN sys.tables t ON t.object_id = fi.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.fulltext_catalogs c ON c.fulltext_catalog_id = fi.fulltext_catalog_id
        ORDER BY s.name, t.name;
        """;

    /// <summary>Kataloglar (sihirbazda "mevcut katalog" seçimi için).</summary>
    public static string KatalogSorgusu()
        => "SELECT name FROM sys.fulltext_catalogs ORDER BY name;";

    /// <summary>
    /// S3 (Veri Arama entegrasyonu): FULLTEXT kolonlarının DÜZ haritası — satır başına
    /// (şema · tablo · kolon). Envanterdeki birleşik metni ayrıştırmak yerine ayrı uç:
    /// Veri Arama tablo başına "hangi kolonlar FTS'li?" diye bakar. FTS kurulu değilse
    /// sys görünümleri yine vardır — sorgu 0 satırla döner, çağıran LIKE yoluna düşer.
    /// </summary>
    public static string KolonHaritasiSorgusu() => """
        SELECT s.name AS sema, t.name AS tablo, col.name AS kolon
        FROM sys.fulltext_index_columns fic
        JOIN sys.tables t ON t.object_id = fic.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.columns col ON col.object_id = fic.object_id AND col.column_id = fic.column_id
        ORDER BY s.name, t.name, col.name;
        """;

    /// <summary>
    /// FULLTEXT INDEX'in şartı olan anahtar index adayları: TEK kolonlu, UNIQUE, kolonu NOT NULL.
    /// İlk satır (PK öncelikli) sihirbazın varsayılanıdır; hiç satır yoksa tabloya FTS kurulamaz —
    /// sihirbaz bunu dürüstçe söyler.
    /// </summary>
    public static string AnahtarIndexSorgusu(string sema, string tablo) => $"""
        SELECT i.name AS indeks, col.name AS kolon
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'{TamAd(sema, tablo).Replace("'", "''")}')
          AND i.is_unique = 1 AND i.has_filter = 0 AND col.is_nullable = 0
          AND (SELECT COUNT(*) FROM sys.index_columns ic2
               WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id
                 AND ic2.is_included_column = 0) = 1
        ORDER BY i.is_primary_key DESC, i.name;
        """;

    /// <summary>Türkçe öncelikli dil seçenekleri (ad · LCID). FTS her kolon için dil ister —
    /// kök bulma/çekim o dile göre çalışır.</summary>
    public static IReadOnlyList<(string Ad, int Lcid)> DilSecenekleri { get; } =
        [("Turkish", 1055), ("English", 1033), ("Neutral (dil işlemesiz)", 0)];

    // ── S1: CONTAINS/FREETEXT sorgu üretimi ───────────────────────────────────

    /// <summary>
    /// Arama sorgusunu kurar. <paramref name="kolon"/> null → tüm index'li kolonlar (<c>*</c>).
    /// <paramref name="rankli"/> ise CONTAINSTABLE/FREETEXTTABLE ile RANK kolonu gelir, en alakalı
    /// üstte (<paramref name="anahtarKolon"/> şart — KEY eşlemesi). Terim içindeki çift tırnak
    /// ikizlenir, tek tırnak SQL kaçışıyla — enjeksiyon değil sözdizimi hatası bile çıkmaz.
    /// </summary>
    public static string AramaSorgusu(
        string sema, string tablo, string? kolon, FtsAramaKipi kip, string terim,
        string? ikinciTerim = null, int yakinlikMesafesi = 5, int tavan = 100,
        bool rankli = false, string? anahtarKolon = null)
    {
        string hedef = kolon is { Length: > 0 } k ? Kose(k) : "*";
        string kosulIfadesi = KosulIfadesi(kip, terim, ikinciTerim, yakinlikMesafesi);
        bool freetext = kip == FtsAramaKipi.SerbestMetin;

        if (!rankli || anahtarKolon is not { Length: > 0 })
            return $"SELECT TOP ({tavan}) * FROM {TamAd(sema, tablo)} "
                 + $"WHERE {(freetext ? "FREETEXT" : "CONTAINS")}({hedef}, {Tirnak(kosulIfadesi)});";

        string fonksiyon = freetext ? "FREETEXTTABLE" : "CONTAINSTABLE";
        return $"SELECT TOP ({tavan}) ft.RANK AS [Skor], t.* FROM {TamAd(sema, tablo)} t\n"
             + $"JOIN {fonksiyon}({TamAd(sema, tablo)}, {hedef}, {Tirnak(kosulIfadesi)}) ft\n"
             + $"  ON ft.[KEY] = t.{Kose(anahtarKolon)}\nORDER BY ft.RANK DESC;";
    }

    /// <summary>CONTAINS/FREETEXT'in İÇ ifadesi (tek tırnaklar hariç) — test edilebilir ayrı uç.</summary>
    public static string KosulIfadesi(FtsAramaKipi kip, string terim, string? ikinciTerim, int mesafe)
    {
        string t = (terim ?? "").Trim().Replace("\"", "\"\"");
        string t2 = (ikinciTerim ?? "").Trim().Replace("\"", "\"\"");
        return kip switch
        {
            FtsAramaKipi.Kelime => $"\"{t}\"",
            FtsAramaKipi.Onek => $"\"{t.TrimEnd('*')}*\"",
            FtsAramaKipi.Yakinlik => $"NEAR((\"{t}\", \"{t2}\"), {mesafe})",
            FtsAramaKipi.Cekimler => $"FORMSOF(INFLECTIONAL, \"{t}\")",
            _ => t, // SerbestMetin: FREETEXT ham metin alır — tırnak süslemesi istemez
        };
    }

    // ── S2: kurulum sihirbazı script'leri ─────────────────────────────────────

    /// <summary>
    /// Kurulum script'i: (gerekirse) katalog + FULLTEXT INDEX — ÇALIŞTIRILMAZ, sekmede açılır;
    /// kullanıcı Güvenli Yazma açıkken koşar. Katalog adı yeni ise IF NOT EXISTS bekçisiyle
    /// üretilir (ikinci koşuda patlamaz). Doldurma CHANGE_TRACKING AUTO ile otomatik başlar.
    /// </summary>
    public static string KurulumScripti(
        string sema, string tablo, IReadOnlyList<(string Kolon, int Lcid)> kolonlar,
        string katalog, string anahtarIndex, bool katalogYeni)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- 🔎 Full-Text kurulum · {TamAd(sema, tablo)} · SQLST sihirbazı üretti");
        sb.AppendLine("-- Script ÇALIŞTIRILMADI — gözden geçirip Güvenli Yazma açıkken koşun.");
        sb.AppendLine("-- İlk doldurma tablonun boyutuna göre sürebilir; durum FTS ekranında izlenir.");
        sb.AppendLine();
        if (katalogYeni)
        {
            sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'{katalog.Replace("'", "''")}')");
            sb.AppendLine($"    CREATE FULLTEXT CATALOG {Kose(katalog)};");
            sb.AppendLine();
        }
        sb.AppendLine($"CREATE FULLTEXT INDEX ON {TamAd(sema, tablo)}");
        sb.AppendLine("(");
        sb.AppendLine(string.Join(",\n", kolonlar.Select(k => $"    {Kose(k.Kolon)} LANGUAGE {k.Lcid}")));
        sb.AppendLine(")");
        sb.AppendLine($"KEY INDEX {Kose(anahtarIndex)} ON {Kose(katalog)}");
        sb.Append("WITH (CHANGE_TRACKING = AUTO);");
        return sb.ToString();
    }

    /// <summary>Index silme script'i (envanterden sağ tık — o da çalıştırmadan sekmeye).</summary>
    public static string SilmeScripti(string sema, string tablo)
        => $"-- 🔎 Full-Text index silme · SQLST\nDROP FULLTEXT INDEX ON {TamAd(sema, tablo)};";

    private static string TamAd(string sema, string tablo)
        => string.IsNullOrEmpty(sema) ? Kose(tablo) : $"{Kose(sema)}.{Kose(tablo)}";

    private static string Kose(string ad) => "[" + ad.Replace("]", "]]") + "]";

    private static string Tirnak(string ifade) => "N'" + ifade.Replace("'", "''") + "'";
}
