using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 🔁 LINQ → SQL motoru (v20-S11): metod zinciri + query syntax → lehçeli SQL. Kendi motorumuz —
/// Roslyn yalnız ayrıştırıcı. Çevrilemeyen yapı NET hatayla düşer (sessiz yanlış yasak).
/// </summary>
public class LinqCeviriciTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());
    private static readonly ILehce Postgres = new PostgresLehcesi(new DpapiSecretProtector());
    private static readonly ILehce MySql = new MySqlLehcesi(new DpapiSecretProtector());

    private static SemaKolonu K(string ad, string tip = "nvarchar(50)") => new(ad, tip, true, false);

    private static readonly IReadOnlyList<SemaNesnesi> Nesneler =
    [
        new("Dukkan", "dbo", "Musteri", SemaNesneTuru.Tablo,
            [K("Id", "int"), K("Ad"), K("Sehir"), K("Aktif", "bit"), K("KayitTarihi", "datetime")], []),
        new("Dukkan", "dbo", "Siparis", SemaNesneTuru.Tablo,
            [K("Id", "int"), K("MusteriId", "int"), K("Tutar", "decimal"), K("Tarih", "datetime")], []),
        new("Dukkan", "dbo", "Fatura", SemaNesneTuru.Tablo, [K("Id", "int"), K("SiparisId", "int")], []),
    ];

    private static readonly IReadOnlyList<YabanciAnahtar> Fkler =
    [
        new("dbo", "Siparis", ["MusteriId"], "dbo", "Musteri", ["Id"], "FK_Siparis_Musteri"),
        new("dbo", "Fatura", ["SiparisId"], "dbo", "Siparis", ["Id"], "FK_Fatura_Siparis"),
    ];

    private static string Sql(string linq, ILehce? lehce = null) =>
        LinqCevirici.Cevir(linq, Nesneler, Fkler, lehce ?? Mssql).Sql;

    // ── temel zincir ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Where_select_tek_kolon()
    {
        string sql = Sql("db.Musteriler.Where(m => m.Sehir == \"Ankara\").Select(m => m.Ad)");

        Assert.Equal("SELECT m.[Ad]\nFROM [dbo].[Musteri] m\nWHERE m.[Sehir] = N'Ankara';", sql);
    }

    [Fact]
    public void Cogul_tablo_adi_tekile_eslenir()
    {
        // "Musteriler" şemada yok; çoğul eki soyulup Musteri bulunur
        Assert.Contains("[dbo].[Musteri]", Sql("Musteriler.Select(m => m.Id)"));
    }

    [Fact]
    public void Var_q_kabugu_ve_noktali_virgul_soyulur()
    {
        string sql = Sql("var sonuc = db.Musteri.Where(m => m.Aktif == true);");

        Assert.Equal("SELECT m.*\nFROM [dbo].[Musteri] m\nWHERE m.[Aktif] = 1;", sql);
    }

    [Fact]
    public void Anonim_projeksiyon_ve_takma_ad()
    {
        string sql = Sql("db.Musteri.Select(m => new { m.Ad, Yer = m.Sehir })");

        Assert.Equal("SELECT m.[Ad], m.[Sehir] AS [Yer]\nFROM [dbo].[Musteri] m;", sql);
    }

    [Fact]
    public void Coklu_where_and_ile_birlesir()
    {
        string sql = Sql("db.Musteri.Where(m => m.Aktif == true).Where(m => m.Sehir != null)");

        Assert.Contains("WHERE m.[Aktif] = 1\n  AND m.[Sehir] IS NOT NULL", sql);
    }

    [Fact]
    public void Orderby_thenby_descending()
    {
        string sql = Sql("db.Musteri.OrderBy(m => m.Sehir).ThenByDescending(m => m.Ad)");

        Assert.Contains("ORDER BY m.[Sehir], m.[Ad] DESC", sql);
    }

    [Fact]
    public void Take_lehceye_gore_top_ve_limit()
    {
        Assert.StartsWith("SELECT TOP (5) ", Sql("db.Musteri.Take(5)"));
        Assert.Contains("LIMIT 5", Sql("db.Musteri.Take(5)", Postgres));
    }

    [Fact]
    public void Skip_take_sayfalama_orderby_sart()
    {
        string sql = Sql("db.Musteri.OrderBy(m => m.Id).Skip(20).Take(10)");
        Assert.Contains("OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY", sql);

        string pg = Sql("db.Musteri.OrderBy(m => m.Id).Skip(20).Take(10)", Postgres);
        Assert.Contains("LIMIT 10 OFFSET 20", pg);

        LinqCeviriHatasi hata = Assert.Throws<LinqCeviriHatasi>(() => Sql("db.Musteri.Skip(5)"));
        Assert.Contains("OrderBy", hata.Message);
    }

    [Fact]
    public void First_top1_distinct_count()
    {
        Assert.StartsWith("SELECT TOP (1) ", Sql("db.Musteri.First(m => m.Id == 7)"));
        Assert.StartsWith("SELECT DISTINCT ", Sql("db.Musteri.Select(m => m.Sehir).Distinct()"));
        Assert.StartsWith("SELECT COUNT(*)", Sql("db.Musteri.Count(m => m.Aktif == true)"));
    }

    [Fact]
    public void Any_exists_sarmali_motor_farki()
    {
        string mssql = Sql("db.Musteri.Any(m => m.Sehir == \"Bolu\")");
        Assert.StartsWith("SELECT CASE WHEN EXISTS (", mssql);

        string pg = Sql("db.Musteri.Any(m => m.Sehir == \"Bolu\")", Postgres);
        Assert.StartsWith("SELECT EXISTS (", pg);
    }

    [Fact]
    public void Toplamlar_sum_avg()
    {
        Assert.StartsWith("SELECT SUM(s.[Tutar])", Sql("db.Siparis.Sum(s => s.Tutar)"));
        Assert.StartsWith("SELECT AVG(s.[Tutar])", Sql("db.Siparis.Average(s => s.Tutar)"));
    }

    // ── ifadeler ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void String_metotlari_like_upper_isnull()
    {
        Assert.Contains("m.[Ad] LIKE N'Ah%'", Sql("db.Musteri.Where(m => m.Ad.StartsWith(\"Ah\"))"));
        Assert.Contains("m.[Ad] LIKE N'%oğlu'", Sql("db.Musteri.Where(m => m.Ad.EndsWith(\"oğlu\"))"));
        Assert.Contains("m.[Ad] LIKE N'%kan%'", Sql("db.Musteri.Where(m => m.Ad.Contains(\"kan\"))"));
        Assert.Contains("UPPER(m.[Ad]) = N'ALİ'", Sql("db.Musteri.Where(m => m.Ad.ToUpper() == \"ALİ\")"));
        Assert.Contains("(m.[Ad] IS NULL OR m.[Ad] = N'')", Sql("db.Musteri.Where(m => string.IsNullOrEmpty(m.Ad))"));
    }

    [Fact]
    public void In_listesi_ternary_ve_tarih()
    {
        Assert.Contains("m.[Sehir] IN (N'Ankara', N'Bolu')",
            Sql("db.Musteri.Where(m => new[] { \"Ankara\", \"Bolu\" }.Contains(m.Sehir))"));
        Assert.Contains("CASE WHEN m.[Aktif] = 1 THEN N'evet' ELSE N'hayır' END",
            Sql("db.Musteri.Select(m => new { Durum = m.Aktif == true ? \"evet\" : \"hayır\" })"));
        Assert.Contains("YEAR(m.[KayitTarihi]) = 2026",
            Sql("db.Musteri.Where(m => m.KayitTarihi.Year == 2026)"));
        Assert.Contains("EXTRACT(YEAR FROM m.\"KayitTarihi\") = 2026",
            Sql("db.Musteri.Where(m => m.KayitTarihi.Year == 2026)", Postgres));
    }

    [Fact]
    public void Metin_birlestirme_motor_farki()
    {
        Assert.Contains("m.[Ad] + N'!'", Sql("db.Musteri.Select(m => new { X = m.Ad + \"!\" })"));
        Assert.Contains("CONCAT(m.`Ad`, '!')", Sql("db.Musteri.Select(m => new { X = m.Ad + \"!\" })", MySql));
    }

    [Fact]
    public void Uzunluk_lehceye_gore_len_length()
    {
        Assert.Contains("LEN(m.[Ad]) > 10", Sql("db.Musteri.Where(m => m.Ad.Length > 10)"));
        Assert.Contains("LENGTH(m.\"Ad\") > 10", Sql("db.Musteri.Where(m => m.Ad.Length > 10)", Postgres));
    }

    // ── gruplamalar ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Groupby_key_count_having()
    {
        string sql = Sql("""
            db.Musteri.GroupBy(m => m.Sehir)
              .Where(g => g.Count() > 5)
              .Select(g => new { Sehir = g.Key, Adet = g.Count() })
            """);

        Assert.Contains("SELECT m.[Sehir], COUNT(*) AS [Adet]", sql);
        Assert.Contains("GROUP BY m.[Sehir]", sql);
        Assert.Contains("HAVING COUNT(*) > 5", sql);
    }

    [Fact]
    public void Groupby_sum_toplama()
    {
        string sql = Sql("db.Siparis.GroupBy(s => s.MusteriId).Select(g => new { g.Key, Toplam = g.Sum(x => x.Tutar) })");

        Assert.Contains("SELECT s.[MusteriId] AS [Key], SUM(s.[Tutar]) AS [Toplam]", sql);
        Assert.Contains("GROUP BY s.[MusteriId]", sql);
    }

    // ── join + navigation ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Metod_join()
    {
        string sql = Sql("""
            db.Siparis.Join(db.Musteri, s => s.MusteriId, m => m.Id,
                (s, m) => new { m.Ad, s.Tutar })
            """);

        Assert.Contains("FROM [dbo].[Siparis] s", sql);
        Assert.Contains("JOIN [dbo].[Musteri] m ON s.[MusteriId] = m.[Id]", sql);
        Assert.Contains("SELECT m.[Ad], s.[Tutar]", sql);
    }

    [Fact]
    public void Navigation_uyesi_fk_join_uretir()
    {
        // s.Musteri şemada kolon değil — FK grafından JOIN'e çevrilir
        string sql = Sql("db.Siparis.Where(s => s.Musteri.Sehir == \"Ankara\").Select(s => s.Tutar)");

        Assert.Contains("JOIN [dbo].[Musteri] musteri ON s.[MusteriId] = musteri.[Id]", sql);
        Assert.Contains("WHERE musteri.[Sehir] = N'Ankara'", sql);
    }

    [Fact]
    public void Ters_navigation_any_exists_alt_sorgu()
    {
        // m.Siparisler koleksiyonu: Siparis→Musteri FK'sının tersi → EXISTS
        string sql = Sql("db.Musteri.Where(m => m.Siparisler.Any(s => s.Tutar > 1000))");

        Assert.Contains("EXISTS (SELECT 1 FROM [dbo].[Siparis] alt WHERE alt.[MusteriId] = m.[Id] AND (alt.[Tutar] > 1000))", sql);
    }

    [Fact]
    public void Ters_navigation_count_alt_sorgu()
    {
        string sql = Sql("db.Musteri.Select(m => new { m.Ad, Adet = m.Siparisler.Count() })");

        Assert.Contains("(SELECT COUNT(*) FROM [dbo].[Siparis] alt WHERE alt.[MusteriId] = m.[Id]) AS [Adet]", sql);
    }

    // ── query syntax ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Query_syntax_from_where_orderby_select()
    {
        string sql = Sql("""
            from m in db.Musteriler
            where m.Sehir == "Ankara" && m.Aktif == true
            orderby m.Ad descending
            select new { m.Id, m.Ad }
            """);

        // v22-S1: üst seviye && ayrı satırlara bölünür (uzun WHERE okunur kalsın)
        Assert.Equal(
            "SELECT m.[Id], m.[Ad]\nFROM [dbo].[Musteri] m\nWHERE m.[Sehir] = N'Ankara'\n  AND m.[Aktif] = 1\nORDER BY m.[Ad] DESC;",
            sql);
    }

    [Fact]
    public void Query_syntax_join()
    {
        string sql = Sql("""
            from s in db.Siparisler
            join m in db.Musteriler on s.MusteriId equals m.Id
            where m.Sehir == "Bolu"
            select new { m.Ad, s.Tutar }
            """);

        Assert.Contains("JOIN [dbo].[Musteri] m ON s.[MusteriId] = m.[Id]", sql);
        Assert.Contains("WHERE m.[Sehir] = N'Bolu'", sql);
    }

    [Fact]
    public void Query_syntax_group_into()
    {
        string sql = Sql("""
            from s in db.Siparisler
            group s by s.MusteriId into g
            select new { g.Key, Toplam = g.Sum(x => x.Tutar) }
            """);

        Assert.Contains("GROUP BY s.[MusteriId]", sql);
        Assert.Contains("SUM(s.[Tutar]) AS [Toplam]", sql);
    }

    [Fact]
    public void Query_syntax_sonrasi_metod_zinciri()
    {
        string sql = Sql("(from m in db.Musteriler where m.Aktif == true select m).Take(3)");

        Assert.StartsWith("SELECT TOP (3) m.*", sql);
    }

    // ── v20-S19 simetri: SQL→LINQ'in ürettiği kalıplar geri döner ───────────────────────────

    [Fact]
    public void Left_join_kalibi_defaultifempty()
    {
        string sql = Sql("""
            from m in db.Musteriler
            join s in db.Siparisler on m.Id equals s.MusteriId into sGrup
            from s in sGrup.DefaultIfEmpty()
            select new { m.Ad, s.Tutar }
            """);

        Assert.Contains("LEFT JOIN [dbo].[Siparis] s ON m.[Id] = s.[MusteriId]", sql);
    }

    [Fact]
    public void Coklu_from_cross_join_olur()
    {
        string sql = Sql("""
            from m in db.Musteriler
            from s in db.Siparisler
            where m.Id == s.MusteriId
            select new { m.Ad, s.Tutar }
            """);

        Assert.Contains("CROSS JOIN [dbo].[Siparis] s", sql);
        Assert.Contains("WHERE m.[Id] = s.[MusteriId]", sql);
    }

    [Fact]
    public void Tablo_kaynagi_any_exists_olur()
    {
        string sql = Sql("db.Musteri.Where(m => db.Siparis.Any(s => s.MusteriId == m.Id && s.Tutar > 100))");

        Assert.Contains("EXISTS (SELECT 1 FROM [dbo].[Siparis] s WHERE s.[MusteriId] = m.[Id] AND s.[Tutar] > 100)", sql);
    }

    [Fact]
    public void Select_contains_in_alt_sorgusu_olur()
    {
        string sql = Sql("db.Musteri.Where(m => db.Siparis.Where(s => s.Tutar > 5000).Select(s => s.MusteriId).Contains(m.Id))");

        Assert.Contains("m.[Id] IN (SELECT s.[MusteriId] FROM [dbo].[Siparis] s WHERE s.[Tutar] > 5000)", sql);
    }

    [Fact]
    public void Null_birlestirme_coalesce_olur()
    {
        string sql = Sql("db.Musteri.Select(m => new { Yer = m.Sehir ?? \"bilinmiyor\" })");

        Assert.Contains("COALESCE(m.[Sehir], N'bilinmiyor') AS [Yer]", sql);
    }

    [Fact]
    public void Ef_functions_like_geri_doner()
    {
        string sql = Sql("db.Musteri.Where(m => EF.Functions.Like(m.Ad, \"A%B%C\"))");

        Assert.Contains("m.[Ad] LIKE N'A%B%C'", sql);
    }

    [Fact]
    public void Kume_islecleri_union_concat()
    {
        string sql = Sql("""
            (from m in db.Musteriler select m.Ad).Union(
             from s in db.Siparisler select s.Tutar)
            """);
        Assert.Contains("\nUNION\n", sql);

        string sqlAll = Sql("(from m in db.Musteriler select m.Id).Concat(from s in db.Siparisler select s.Id)");
        Assert.Contains("\nUNION ALL\n", sqlAll);
    }

    [Fact]
    public void Varli_cok_ifadeli_metin_with_cte_olur()
    {
        // v20-S20 simetri: SQL→LINQ'in CTE için ürettiği "var x = (…); ana" kalıbı geri döner
        string sql = Sql("""
            var Buyukler = (from s in db.Siparisler where s.Tutar > 1000 select new { s.MusteriId, s.Tutar });

            from b in Buyukler
            where b.Tutar > 5000
            select b.MusteriId
            """);

        Assert.StartsWith("WITH [Buyukler] AS (", sql);
        Assert.Contains("FROM [Buyukler] b", sql);   // CTE kaynağı şemasız tırnaklı ad
        Assert.Contains("WHERE b.[Tutar] > 5000", sql);
    }

    [Fact]
    public void Gidis_donus_cte_zinciri()
    {
        // SQL(CTE) → LINQ(var) → SQL(WITH): kalıp yolda kaybolmaz
        string ilkSql = """
            WITH Ozet AS (SELECT s.MusteriId FROM Siparis s WHERE s.Tutar > 100)
            SELECT o.MusteriId FROM Ozet o
            """;
        string linq = SqlLinqCevirici.Cevir(ilkSql).Linq;
        string sonSql = LinqCevirici.Cevir(linq, Nesneler, Fkler, Mssql).Sql;

        Assert.StartsWith("WITH [Ozet] AS (", sonSql);
        Assert.Contains("WHERE s.[Tutar] > 100", sonSql);
        Assert.Contains("FROM [Ozet] o", sonSql);
    }

    [Fact]
    public void Gidis_donus_sql_linq_sql_ayni_anlam()
    {
        // Zincir kanıtı (v20-S19): SQL→LINQ→SQL — LEFT JOIN + EXISTS + ?? kalıpları yolda kaybolmaz.
        string ilkSql = """
            SELECT m.Ad, ISNULL(m.Sehir, N'-') AS Yer
            FROM Musteri m
            LEFT JOIN Siparis s ON s.MusteriId = m.Id
            WHERE EXISTS (SELECT 1 FROM Fatura f WHERE f.SiparisId = s.Id)
            """;
        string linq = SqlLinqCevirici.Cevir(ilkSql).Linq;
        string sonSql = LinqCevirici.Cevir(linq, Nesneler, Fkler, Mssql).Sql;

        Assert.Contains("LEFT JOIN [dbo].[Siparis] s ON m.[Id] = s.[MusteriId]", sonSql);
        Assert.Contains("EXISTS (SELECT 1 FROM [dbo].[Fatura] f WHERE f.[SiparisId] = s.[Id])", sonSql);
        Assert.Contains("COALESCE(m.[Sehir], N'-')", sonSql); // ISNULL → ?? → COALESCE zinciri
        Assert.Contains("AS [Yer]", sonSql);
    }

    // ── hatalar (sessiz yanlış yasak) ───────────────────────────────────────────────────────

    [Fact]
    public void Bilinmeyen_tablo_net_hata()
    {
        LinqCeviriHatasi hata = Assert.Throws<LinqCeviriHatasi>(() => Sql("db.Personel.Take(1)"));
        Assert.Contains("Personel", hata.Message);
        Assert.Contains("bulunamadı", hata.Message);
    }

    [Fact]
    public void Desteklenmeyen_metot_net_hata()
    {
        LinqCeviriHatasi hata = Assert.Throws<LinqCeviriHatasi>(
            () => Sql("db.Musteri.Reverse()"));
        Assert.Contains("Reverse", hata.Message);
    }

    [Fact]
    public void Dis_degisken_parametreye_cevrilir() // v20-S21 saha m.1: hata değil, @param + uyarı
    {
        LinqCeviriSonucu sonuc = LinqCevirici.Cevir(
            "db.Musteri.Where(m => m.Sehir == aranan)", Nesneler, Fkler, Mssql);

        Assert.Contains("m.[Sehir] = @aranan", sonuc.Sql);
        Assert.Contains(sonuc.Uyarilar, u => u.Contains("@aranan"));
    }

    [Fact]
    public void Dis_liste_degiskeni_in_parametresi() // ids.Contains(x.Kol) → x.Kol IN (@ids)
    {
        LinqCeviriSonucu sonuc = LinqCevirici.Cevir(
            "db.Musteri.Where(m => sehirler.Contains(m.Sehir))", Nesneler, Fkler, Mssql);

        Assert.Contains("m.[Sehir] IN (@sehirler)", sonuc.Sql);
        Assert.Contains(sonuc.Uyarilar, u => u.Contains("@sehirler"));
    }

    // ── v20-S21 saha m.1: üst seviye skaler kıyas + Where'li join kaynağı + LEFT JOIN null ──

    [Fact]
    public void Sorgu_sayimi_kiyasi_case_when_sarmali()
    {
        string sql = Sql("(db.Musteri.Where(m => m.Aktif == true)).Count() == 0");

        Assert.StartsWith("SELECT CASE WHEN (", sql);
        Assert.Contains("SELECT COUNT(*)", sql);
        Assert.Contains(") = 0 THEN 1 ELSE 0 END;", sql);
    }

    [Fact]
    public void Sabit_soldaysa_islec_cevrilir() // 0 < q.Count() → COUNT > 0
    {
        string sql = Sql("0 < db.Musteri.Count()");

        Assert.Contains(") > 0 THEN 1 ELSE 0 END;", sql);
    }

    [Fact]
    public void Join_kaynagi_where_kosullari_on_a_eklenir() // önceden SESSİZCE düşüyordu
    {
        string sql = Sql("""
            from m in db.Musteriler
            join s in db.Siparisler.Where(s => s.Tutar > 100) on m.Id equals s.MusteriId
            select new { m.Ad, s.Tutar }
            """);

        Assert.Contains("JOIN [dbo].[Siparis] s ON m.[Id] = s.[MusteriId] AND s.[Tutar] > 100", sql);
    }

    [Fact]
    public void Saha_m1_birebir_kullanici_sorgusu() // (from … into … DefaultIfEmpty … select).Count() == 0
    {
        IReadOnlyList<SemaNesnesi> nesneler =
        [
            new("Db", "dbo", "BirlesmeDetay", SemaNesneTuru.Tablo,
                [K("Id", "int"), K("BirlesmeId", "int"), K("DevirOlanFirmaId", "int")], []),
            new("Db", "dbo", "Talep", SemaNesneTuru.Tablo,
                [K("Id", "int"), K("TalepDurumuId", "int"), K("TalepTuruId", "int"), K("FirmaId", "int")], []),
        ];
        LinqCeviriSonucu sonuc = LinqCevirici.Cevir("""
            (from bd in context.BirlesmeDetay
            join bda in context.BirlesmeDetay on bd.BirlesmeId equals bda.BirlesmeId
            join t in context.Talep.Where(t => talepDurumuIds.Contains(t.TalepDurumuId) && t.TalepTuruId == talepTuruId) on bda.DevirOlanFirmaId equals t.FirmaId into talep
            from tb in talep.DefaultIfEmpty()
            where bd.DevirOlanFirmaId == devirOlanFirmaId && tb == null
            select bd.Id).Count() == 0;
            """, nesneler, [], Mssql);

        Assert.StartsWith("SELECT CASE WHEN (", sonuc.Sql);
        Assert.Contains("LEFT JOIN [dbo].[Talep] t ON bda.[DevirOlanFirmaId] = t.[FirmaId]"
            + " AND t.[TalepDurumuId] IN (@talepDurumuIds) AND t.[TalepTuruId] = @talepTuruId", sonuc.Sql);
        Assert.Contains("t.[FirmaId] IS NULL", sonuc.Sql);            // tb == null → iç anahtar
        Assert.Contains("= @devirOlanFirmaId", sonuc.Sql);
        Assert.Contains(") = 0 THEN 1 ELSE 0 END;", sonuc.Sql);
    }

    // ── v22-S1 saha: alt sorgulu join kaynağı · ilişkili alt sorgu · dış nesne üyesi · bit kolon ──

    /// <summary>Kullanıcının saha sorgusundaki tablolar (MERSIS birleşme akışı).</summary>
    private static readonly IReadOnlyList<SemaNesnesi> Mersis =
    [
        new("Mersis", "dbo", "Birlesme", SemaNesneTuru.Tablo,
            [K("Id", "int"), K("DevirAlanFirmaId", "int"), K("TescilDurumuId", "int")], []),
        new("Mersis", "dbo", "BirlesmeDetay", SemaNesneTuru.Tablo,
            [K("Id", "int"), K("BirlesmeId", "int"), K("DevirOlanFirmaId", "int")], []),
        new("Mersis", "dbo", "Talep", SemaNesneTuru.Tablo,
            [K("Id", "int"), K("FirmaId", "int"), K("TsmId", "int"), K("TalepDurumuId", "int"),
             K("TalepTuruId", "int")], []),
        new("Mersis", "dbo", "Firma", SemaNesneTuru.Tablo, [K("Id", "int"), K("SicilNo"), K("MersisNo")], []),
        new("Mersis", "dbo", "Unvan", SemaNesneTuru.Tablo,
            [K("Id", "int"), K("FirmaId", "int"), K("TamUnvan"), K("Aktif", "bit"), K("BitisTalepId", "int"),
             K("TescilDurumuId", "int")], []),
    ];

    [Fact]
    public void V22_alt_sorgulu_join_kaynagi_turetilmis_tablo_olur()
    {
        LinqCeviriSonucu sonuc = LinqCevirici.Cevir("""
            from b in context.Birlesme
            join bdVar in (from bd in context.BirlesmeDetay group bd by bd.BirlesmeId into gbd select gbd.Key)
                on b.Id equals bdVar
            select b.Id
            """, Mersis, [], Mssql);

        Assert.Contains("""
            JOIN (
              SELECT bd.[BirlesmeId]
              FROM [dbo].[BirlesmeDetay] bd
              GROUP BY bd.[BirlesmeId]
            ) bdVar ON b.[Id] = bdVar.[BirlesmeId]
            """, sonuc.Sql);
    }

    [Fact]
    public void V22_iliskili_alt_sorgu_sayimi_ve_varligi()
    {
        string sql = Sql("""
            from m in db.Musteri
            where (from s in db.Siparis where s.MusteriId == m.Id select s.Id).Count() > 2
               && (from f in db.Fatura where f.SiparisId == m.Id select f.Id).Any()
            select m.Id
            """);

        Assert.Contains("(\n  SELECT COUNT(*)\n  FROM [dbo].[Siparis] s\n  WHERE s.[MusteriId] = m.[Id]\n) > 2", sql);
        Assert.Contains("EXISTS (\n  SELECT 1\n  FROM [dbo].[Fatura] f\n  WHERE f.[SiparisId] = m.[Id]\n)", sql);
    }

    [Fact]
    public void V22_dis_nesne_ve_enum_uyesi_parametre_olur()
    {
        LinqCeviriSonucu sonuc = LinqCevirici.Cevir(
            "db.Musteri.Where(m => m.Sehir == aramaModel.Sehir && m.Id == (int)EnumDurum.Aktif)",
            Nesneler, Fkler, Mssql);

        Assert.Contains("m.[Sehir] = @aramaModel_Sehir", sonuc.Sql);
        Assert.Contains("m.[Id] = @EnumDurum_Aktif", sonuc.Sql);
        Assert.Contains(sonuc.Uyarilar, u => u.Contains("@aramaModel_Sehir"));
    }

    [Fact]
    public void V22_sabit_olmayan_like_deseni_birlestirmeyle_yazilir()
    {
        Assert.Contains("m.[Ad] LIKE N'%' + @aramaModel_Ad + N'%'",
            Sql("db.Musteri.Where(m => m.Ad.Contains(aramaModel.Ad))"));
        Assert.Contains("""m."Ad" LIKE :aramaModel_Ad || '%'""",
            Sql("db.Musteri.Where(m => m.Ad.StartsWith(aramaModel.Ad))", Postgres));
        Assert.Contains("LIKE CONCAT('%', @aramaModel_Ad)",
            Sql("db.Musteri.Where(m => m.Ad.EndsWith(aramaModel.Ad))", MySql));
    }

    [Fact]
    public void V22_bit_kolon_kosul_baglaminda_esitlik_alir()
    {
        // MSSQL: "WHERE m.[Aktif] AND …" derlenmez → "= 1" eklenir
        Assert.Contains("WHERE m.[Aktif] = 1\n  AND m.[Sehir] IS NOT NULL",
            Sql("db.Musteri.Where(m => m.Aktif && m.Sehir != null)"));
        Assert.Contains("WHERE NOT (m.[Aktif] = 1)", Sql("db.Musteri.Where(m => !m.Aktif)"));
        // PostgreSQL'de kolon gerçek boolean — dokunulmaz
        Assert.Contains("WHERE m.\"Aktif\"\n  AND", Sql("db.Musteri.Where(m => m.Aktif && m.Sehir != null)", Postgres));
    }

    [Fact]
    public void V22_saha_kullanici_sorgusu_birebir_cevrilir()
    {
        // Kullanıcının 2026-08-17 saha turunda çeviremediği sorgu — BİREBİR (LINQ⇄SQL ekranı)
        LinqCeviriSonucu sonuc = LinqCevirici.Cevir("""
            from b in context.Birlesme
            join bdVar in (from bd in context.BirlesmeDetay group bd by bd.BirlesmeId into gbd select gbd.Key) on b.Id equals bdVar
            join t in context.Talep on b.DevirAlanFirmaId equals t.FirmaId
            join f in context.Firma on b.DevirAlanFirmaId equals f.Id
            join u in context.Unvan on b.DevirAlanFirmaId equals u.FirmaId
            where t.TsmId == tsmId &&
            ((string.IsNullOrEmpty(aramaModel.DevirAlanFirmaUnvan) || (u.TamUnvan.Contains(aramaModel.DevirAlanFirmaUnvan)))) &&
            ((string.IsNullOrEmpty(aramaModel.SicilNo) || (f.SicilNo == aramaModel.SicilNo))) &&
            ((string.IsNullOrEmpty(aramaModel.MersisNo) || (f.MersisNo == aramaModel.MersisNo))) &&
            b.TescilDurumuId == (int)EnumTescilDurumu.EklendiOnayBekliyor &&
            t.TalepDurumuId != (int)EnumTalepDurumu.Tescilli && talepTuruBirlesmeIds.Contains(t.TalepTuruId) &&
            u.Aktif && u.BitisTalepId == null && tescilDurumuIds.Contains(u.TescilDurumuId)
            && (
                from bd in context.BirlesmeDetay
                join t in context.Talep on bd.DevirOlanFirmaId equals t.FirmaId
                where bd.BirlesmeId == b.Id && t.TalepDurumuId == (int)EnumTalepDurumu.Tescilli && t.TalepTuruId == (int)EnumTalepTuru.DegisiklikBirlesmeDevirOlan
                select bd.BirlesmeId
            ).Count() == (from bd in context.BirlesmeDetay
                        where bd.BirlesmeId == b.Id
                        select bd.BirlesmeId).Count()
            select b.Id
            """, Mersis, [], Mssql);

        Assert.StartsWith("SELECT b.[Id]\nFROM [dbo].[Birlesme] b\nJOIN (", sonuc.Sql);
        Assert.Contains(") bdVar ON b.[Id] = bdVar.[BirlesmeId]", sonuc.Sql);       // türetilmiş tablo
        Assert.Contains("JOIN [dbo].[Talep] t ON b.[DevirAlanFirmaId] = t.[FirmaId]", sonuc.Sql);
        Assert.Contains("u.[TamUnvan] LIKE N'%' + @aramaModel_DevirAlanFirmaUnvan + N'%'", sonuc.Sql);
        Assert.Contains("(@aramaModel_SicilNo IS NULL OR @aramaModel_SicilNo = N'')", sonuc.Sql);
        Assert.Contains("b.[TescilDurumuId] = @EnumTescilDurumu_EklendiOnayBekliyor", sonuc.Sql);
        Assert.Contains("t.[TalepTuruId] IN (@talepTuruBirlesmeIds)", sonuc.Sql);
        Assert.Contains("u.[Aktif] = 1", sonuc.Sql);                                 // bit kolon koşulda
        Assert.Contains("u.[BitisTalepId] IS NULL", sonuc.Sql);
        Assert.Contains("SELECT COUNT(*)", sonuc.Sql);                               // iki ilişkili alt sorgu
        Assert.Contains("WHERE bd.[BirlesmeId] = b.[Id]", sonuc.Sql);                // dış satıra başvuru korundu
        Assert.DoesNotContain("@bd", sonuc.Sql);                                     // iç adlar parametreye düşmedi
    }

    [Fact]
    public void Bozuk_csharp_net_hata()
    {
        Assert.Throws<LinqCeviriHatasi>(() => Sql("db.Musteri.Where(m => m.Ad =="));
    }

    [Fact]
    public void Bilinmeyen_kolon_uyari_verir_ama_cevirir()
    {
        LinqCeviriSonucu sonuc = LinqCevirici.Cevir(
            "db.Musteri.Where(m => m.Eposta == \"x\")", Nesneler, Fkler, Mssql);

        Assert.Contains("m.[Eposta] = N'x'", sonuc.Sql);
        Assert.Contains(sonuc.Uyarilar, u => u.Contains("Eposta"));
    }

    [Fact]
    public void Like_deseninde_joker_uyarisi()
    {
        LinqCeviriSonucu sonuc = LinqCevirici.Cevir(
            "db.Musteri.Where(m => m.Ad.Contains(\"%50\"))", Nesneler, Fkler, Mssql);

        Assert.Contains(sonuc.Uyarilar, u => u.Contains("joker"));
    }
}
