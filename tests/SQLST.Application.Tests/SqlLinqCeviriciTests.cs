using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// 🔁 SQL → LINQ ters motoru (v20-S12): ScriptDom AST → query-syntax LINQ metni. Tam karşılığı
/// üretilemeyen yapı (LEFT JOIN/UNION/EXISTS/CTE…) NET hatayla reddedilir — yaklaşık çeviri yok.
/// </summary>
public class SqlLinqCeviriciTests
{
    private static string Linq(string sql) => SqlLinqCevirici.Cevir(sql).Linq;

    // ── temel ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Where_select_kolonlar()
    {
        string linq = Linq("SELECT m.Ad, m.Sehir FROM dbo.Musteri m WHERE m.Aktif = 1");

        Assert.Equal("from m in db.Musteri\nwhere m.Aktif == 1\nselect new { m.Ad, m.Sehir }", linq);
    }

    [Fact]
    public void Select_yildiz_tek_tablo()
    {
        Assert.Equal("from m in db.Musteri\nselect m", Linq("SELECT * FROM Musteri m"));
    }

    [Fact]
    public void Takma_adsiz_tablo_bas_harfi_degisken_olur()
    {
        Assert.Equal("from m in db.Musteri\nselect m", Linq("SELECT * FROM Musteri"));
    }

    [Fact]
    public void Tek_kolon_duz_select()
    {
        Assert.Equal("from m in db.Musteri\nselect m.Ad", Linq("SELECT m.Ad FROM Musteri m"));
    }

    [Fact]
    public void Kolon_takma_adi_atama_olur()
    {
        Assert.Contains("select new { Yer = m.Sehir }", Linq("SELECT m.Sehir AS Yer FROM Musteri m"));
    }

    [Fact]
    public void Niteliksiz_kolon_tek_tabloda_baglanir()
    {
        Assert.Equal("from m in db.Musteri\nwhere m.Aktif == 1\nselect m.Ad",
            Linq("SELECT Ad FROM Musteri WHERE Aktif = 1"));
    }

    // ── koşullar ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Like_desenleri_startswith_endswith_contains()
    {
        Assert.Contains("m.Ad.StartsWith(\"Ah\")", Linq("SELECT * FROM Musteri m WHERE m.Ad LIKE N'Ah%'"));
        Assert.Contains("m.Ad.EndsWith(\"oğlu\")", Linq("SELECT * FROM Musteri m WHERE m.Ad LIKE N'%oğlu'"));
        Assert.Contains("m.Ad.Contains(\"kan\")", Linq("SELECT * FROM Musteri m WHERE m.Ad LIKE N'%kan%'"));
    }

    [Fact]
    public void Is_null_ve_in_listesi()
    {
        Assert.Contains("m.Sehir == null", Linq("SELECT * FROM Musteri m WHERE m.Sehir IS NULL"));
        Assert.Contains("m.Sehir != null", Linq("SELECT * FROM Musteri m WHERE m.Sehir IS NOT NULL"));
        Assert.Contains("new[] { \"Ankara\", \"Bolu\" }.Contains(m.Sehir)",
            Linq("SELECT * FROM Musteri m WHERE m.Sehir IN (N'Ankara', N'Bolu')"));
    }

    [Fact]
    public void And_or_not_parantez()
    {
        string linq = Linq("SELECT * FROM Musteri m WHERE (m.Aktif = 1 OR m.Sehir = N'Bolu') AND NOT (m.Id = 5)");

        Assert.Contains("(m.Aktif == 1 || m.Sehir == \"Bolu\") && !(m.Id == 5)", linq);
    }

    [Fact]
    public void Fonksiyonlar_upper_len_year_case()
    {
        Assert.Contains("m.Ad.ToUpper() == \"ALİ\"", Linq("SELECT * FROM Musteri m WHERE UPPER(m.Ad) = N'ALİ'"));
        Assert.Contains("m.Ad.Length > 10", Linq("SELECT * FROM Musteri m WHERE LEN(m.Ad) > 10"));
        Assert.Contains("Yil = m.KayitTarihi.Year",
            Linq("SELECT YEAR(m.KayitTarihi) AS Yil FROM Musteri m"));
        Assert.Contains("m.Aktif == 1 ? \"evet\" : \"hayır\"",
            Linq("SELECT CASE WHEN m.Aktif = 1 THEN N'evet' ELSE N'hayır' END AS Durum FROM Musteri m"));
    }

    // ── join ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Inner_join_query_syntax()
    {
        string linq = Linq("""
            SELECT m.Ad, s.Tutar
            FROM dbo.Siparis s
            JOIN dbo.Musteri m ON s.MusteriId = m.Id
            WHERE m.Sehir = N'Ankara'
            """);

        Assert.Equal("""
            from s in db.Siparis
            join m in db.Musteri on s.MusteriId equals m.Id
            where m.Sehir == "Ankara"
            select new { m.Ad, s.Tutar }
            """.Replace("\r\n", "\n"), linq);
    }

    [Fact]
    public void Bilesik_on_anahtari_anonim_nesne()
    {
        string linq = Linq("SELECT a.X FROM A a JOIN B b ON a.K1 = b.K1 AND a.K2 = b.K2");

        Assert.Contains("join b in db.B on new { a.K1, a.K2 } equals new { b.K1, b.K2 }", linq);
    }

    [Fact]
    public void On_esitliginde_taraflar_ters_yazilmissa_duzeltilir()
    {
        // equals'ın solu DIŞ kaynak olmalı — SQL'de m.Id = s.MusteriId yazılsa bile
        string linq = Linq("SELECT s.Id FROM Siparis s JOIN Musteri m ON m.Id = s.MusteriId");

        Assert.Contains("join m in db.Musteri on s.MusteriId equals m.Id", linq);
    }

    // ── gruplamalar ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Group_by_count_having()
    {
        string linq = Linq("""
            SELECT m.Sehir, COUNT(*) AS Adet
            FROM Musteri m
            GROUP BY m.Sehir
            HAVING COUNT(*) > 5
            """);

        Assert.Equal("""
            from m in db.Musteri
            group m by m.Sehir into g
            where g.Count() > 5
            select new { Sehir = g.Key, Adet = g.Count() }
            """.Replace("\r\n", "\n"), linq);
    }

    [Fact]
    public void Group_by_sum_kolonu()
    {
        string linq = Linq("SELECT s.MusteriId, SUM(s.Tutar) AS Toplam FROM Siparis s GROUP BY s.MusteriId");

        Assert.Contains("group s by s.MusteriId into g", linq);
        Assert.Contains("Toplam = g.Sum(x => x.Tutar)", linq);
        Assert.Contains("MusteriId = g.Key", linq);
    }

    [Fact]
    public void Bilesik_grup_anahtari()
    {
        string linq = Linq("SELECT m.Sehir, m.Aktif, COUNT(*) AS N FROM Musteri m GROUP BY m.Sehir, m.Aktif");

        Assert.Contains("group m by new { m.Sehir, m.Aktif } into g", linq);
        Assert.Contains("select new { g.Key.Sehir, g.Key.Aktif, N = g.Count() }", linq);
    }

    // ── ekler ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Top_distinct_ve_offset_fetch()
    {
        Assert.EndsWith(").Take(10)", Linq("SELECT TOP (10) * FROM Musteri m"));
        Assert.EndsWith(").Distinct()", Linq("SELECT DISTINCT m.Sehir FROM Musteri m"));
        Assert.EndsWith(").Skip(20).Take(10)",
            Linq("SELECT * FROM Musteri m ORDER BY m.Id OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY"));
    }

    [Fact]
    public void Orderby_desc()
    {
        Assert.Contains("orderby m.Sehir, m.Ad descending",
            Linq("SELECT * FROM Musteri m ORDER BY m.Sehir, m.Ad DESC"));
    }

    // ── v20-S18 kapsam genişlemesi ──────────────────────────────────────────────────────────

    [Fact]
    public void Left_join_groupjoin_defaultifempty()
    {
        string linq = Linq("SELECT m.Ad, s.Tutar FROM Musteri m LEFT JOIN Siparis s ON s.MusteriId = m.Id");

        Assert.Contains("join s in db.Siparis on m.Id equals s.MusteriId into sGrup", linq);
        Assert.Contains("from s in sGrup.DefaultIfEmpty()", linq);
    }

    [Fact]
    public void Right_join_yol_gosteren_hata()
    {
        LinqCeviriHatasi hata = Assert.Throws<LinqCeviriHatasi>(
            () => Linq("SELECT * FROM A a RIGHT JOIN B b ON a.Id = b.AId"));
        Assert.Contains("LEFT JOIN yazın", hata.Message);
    }

    [Fact]
    public void Exists_ve_not_exists_any_olur()
    {
        string linq = Linq("""
            SELECT m.Ad FROM Musteri m
            WHERE EXISTS (SELECT 1 FROM Siparis s WHERE s.MusteriId = m.Id AND s.Tutar > 100)
            """);
        Assert.Contains("where db.Siparis.Any(s => s.MusteriId == m.Id && s.Tutar > 100)", linq);

        string linq2 = Linq("SELECT m.Ad FROM Musteri m WHERE NOT EXISTS (SELECT 1 FROM Siparis s WHERE s.MusteriId = m.Id)");
        Assert.Contains("!(db.Siparis.Any(s => s.MusteriId == m.Id))", linq2);
    }

    [Fact]
    public void In_alt_sorgusu_select_contains_olur()
    {
        string linq = Linq("""
            SELECT m.Ad FROM Musteri m
            WHERE m.Id IN (SELECT s.MusteriId FROM Siparis s WHERE s.Tutar > 5000)
            """);

        Assert.Contains("db.Siparis.Where(s => s.Tutar > 5000).Select(s => s.MusteriId).Contains(m.Id)", linq);
    }

    [Fact]
    public void Union_ailesi_kume_islecleri()
    {
        string union = Linq("SELECT a.Id FROM A a UNION SELECT b.Id FROM B b");
        Assert.Contains(").Union(", union);

        string unionAll = Linq("SELECT a.Id FROM A a UNION ALL SELECT b.Id FROM B b");
        Assert.Contains(").Concat(", unionAll);

        string except = Linq("SELECT a.Id FROM A a EXCEPT SELECT b.Id FROM B b");
        Assert.Contains(").Except(", except);
    }

    [Fact]
    public void Virgullu_from_coklu_from_olur()
    {
        string linq = Linq("SELECT a.X, b.Y FROM A a, B b WHERE a.Id = b.AId");

        Assert.Contains("from a in db.A", linq);
        Assert.Contains("from b in db.B", linq);
        Assert.Contains("where a.Id == b.AId", linq);
    }

    [Fact]
    public void Isnull_coalesce_nullif()
    {
        Assert.Contains("(m.Sehir ?? \"bilinmiyor\")",
            Linq("SELECT ISNULL(m.Sehir, N'bilinmiyor') AS Yer FROM Musteri m"));
        Assert.Contains("(m.Sehir ?? m.Ad ?? \"-\")",
            Linq("SELECT COALESCE(m.Sehir, m.Ad, N'-') AS Yer FROM Musteri m"));
        Assert.Contains("(m.Sehir == m.Ad ? null : m.Sehir)",
            Linq("SELECT NULLIF(m.Sehir, m.Ad) AS Yer FROM Musteri m"));
    }

    [Fact]
    public void Cast_convert_donusumleri()
    {
        Assert.Contains("(int)s.Tutar", Linq("SELECT CAST(s.Tutar AS int) AS T FROM Siparis s"));
        Assert.Contains("s.Tutar.ToString()", Linq("SELECT CONVERT(nvarchar(20), s.Tutar) AS T FROM Siparis s"));
        Assert.Contains("s.Tarih.Date", Linq("SELECT CAST(s.Tarih AS date) AS G FROM Siparis s"));
    }

    [Fact]
    public void Substring_dateadd_datediff()
    {
        Assert.Contains("m.Ad.Substring(0, 3)", Linq("SELECT SUBSTRING(m.Ad, 1, 3) AS K FROM Musteri m"));
        Assert.Contains("s.Tarih.AddDays(7)", Linq("SELECT DATEADD(day, 7, s.Tarih) AS Y FROM Siparis s"));

        SqlLinqSonucu fark = SqlLinqCevirici.Cevir("SELECT DATEDIFF(day, s.Tarih, GETDATE()) AS Gun FROM Siparis s");
        Assert.Contains("(DateTime.Now - s.Tarih).Days", fark.Linq);
        Assert.Contains(fark.Uyarilar, u => u.Contains("sınır sayar"));
    }

    [Fact]
    public void Skaler_alt_sorgu_top1_kolon_firstordefault()
    {
        string linq = Linq("""
            SELECT m.Ad,
                   (SELECT TOP (1) s.Tutar FROM Siparis s WHERE s.MusteriId = m.Id) AS SonTutar
            FROM Musteri m
            """);

        Assert.Contains("SonTutar = db.Siparis.Where(s => s.MusteriId == m.Id).Select(s => s.Tutar).FirstOrDefault()", linq);
    }

    [Fact]
    public void Karisik_like_ef_functions_uyariyla()
    {
        SqlLinqSonucu sonuc = SqlLinqCevirici.Cevir("SELECT * FROM Musteri m WHERE m.Ad LIKE N'A%B%C'");

        Assert.Contains("EF.Functions.Like(m.Ad, \"A%B%C\")", sonuc.Linq);
        Assert.Contains(sonuc.Uyarilar, u => u.Contains("EF Core"));
    }

    [Fact]
    public void Ic_takma_ad_dis_takma_adi_golgelemez()
    {
        // iç alt sorguda da "m" kullanılırsa dıştaki m bağını bozmamalı (AltKayit geri yükleme)
        string linq = Linq("""
            SELECT m.Ad FROM Musteri m
            WHERE EXISTS (SELECT 1 FROM Siparis m2 WHERE m2.MusteriId = m.Id)
              AND m.Aktif = 1
            """);

        Assert.Contains("m.Aktif == 1", linq); // EXISTS'ten SONRA dış m hâlâ çözülüyor
    }

    // ── v20-S20: CTE + türetilmiş tablo ─────────────────────────────────────────────────────

    [Fact]
    public void Cte_var_tanimina_cevrilir()
    {
        string linq = Linq("""
            WITH BuyukSiparisler AS (
                SELECT s.MusteriId, s.Tutar FROM Siparis s WHERE s.Tutar > 1000
            )
            SELECT b.MusteriId FROM BuyukSiparisler b
            """);

        Assert.Contains("var BuyukSiparisler = (", linq);
        Assert.Contains("where s.Tutar > 1000", linq);
        Assert.Contains("from b in BuyukSiparisler", linq); // db. öneki YOK — değişken kaynak
        Assert.DoesNotContain("db.BuyukSiparisler", linq);
    }

    [Fact]
    public void Zincirli_coklu_cte()
    {
        string linq = Linq("""
            WITH A1 AS (SELECT s.MusteriId FROM Siparis s),
                 B1 AS (SELECT a.MusteriId FROM A1 a)
            SELECT b.MusteriId FROM B1 b
            """);

        Assert.Contains("var A1 = (", linq);
        Assert.Contains("var B1 = (", linq);
        Assert.Contains("from a in A1", linq); // ikinci CTE birinciye başvuruyor
        Assert.Contains("from b in B1", linq);
    }

    [Fact]
    public void Turetilmis_tablo_hoist_edilir()
    {
        string linq = Linq("""
            SELECT d.MusteriId, m.Ad
            FROM (SELECT s.MusteriId FROM Siparis s WHERE s.Tutar > 500) d
            JOIN Musteri m ON m.Id = d.MusteriId
            """);

        Assert.Contains("var dSorgu = (", linq);
        Assert.Contains("where s.Tutar > 500", linq);
        Assert.Contains("from d in dSorgu", linq); // hoist edilen değişken ana sorgunun kaynağı
        Assert.Contains("join m in db.Musteri", linq);
    }

    [Fact]
    public void Ozyinelemeli_cte_ve_kolon_listesi_net_hata()
    {
        LinqCeviriHatasi oz = Assert.Throws<LinqCeviriHatasi>(() => Linq("""
            WITH X AS (SELECT s.Id FROM X s)
            SELECT x.Id FROM X x
            """));
        Assert.Contains("Özyinelemeli", oz.Message);

        LinqCeviriHatasi kolon = Assert.Throws<LinqCeviriHatasi>(() => Linq("""
            WITH X (A) AS (SELECT s.Id FROM Siparis s)
            SELECT x.A FROM X x
            """));
        Assert.Contains("kolon listesi", kolon.Message);
    }

    // ── hatalar (yaklaşık çeviri yasak) ─────────────────────────────────────────────────────

    [Fact]
    public void Cte_pencere_fonksiyonu_ve_update_net_hata()
    {
        // Karşılığı üretilemeyenler v20-S18 sonrası da NET hatayla kalır (yaklaşık çeviri yasak)
        Assert.Throws<LinqCeviriHatasi>(() => Linq("WITH x AS (SELECT 1 AS A) SELECT * FROM x"));
        Assert.Throws<LinqCeviriHatasi>(
            () => Linq("SELECT ROW_NUMBER() OVER (ORDER BY m.Id) AS N FROM Musteri m"));
        Assert.Throws<LinqCeviriHatasi>(() => Linq("UPDATE Musteri SET Ad = N'x'"));
    }

    [Fact]
    public void Bozuk_sql_net_hata()
    {
        Assert.Throws<LinqCeviriHatasi>(() => Linq("SELECT FROM WHERE"));
        // karışık LIKE artık hata değil: EF.Functions.Like + uyarı (v20-S18) — ayrı testte
    }

    [Fact]
    public void Kullanici_sorgusu_kds_servis_ozeti_birebir()
    {
        // Kullanıcı bulgusu 2026-08-07: canlı testte çevrilemeyen sorgu — artık uçtan uca çevrilmeli.
        // İçinde: TOP + çok tablolu GROUP BY (çakışan Ad adları) + COUNT(DISTINCT) + bağıntılı
        // skaler alt sorgu + ORDER BY'da SELECT takma adı.
        string linq = Linq("""
            SELECT TOP (20)
              sv.Ad                          AS Servis,
              st.Ad                          AS ServisTipi,
              COUNT(DISTINCT t.Id)           AS TalepSayisi,
              SUM(t.IscilikTutari)           AS ToplamIscilik,
              SUM(t.ParcaTutari)             AS ToplamParca,
              SUM(t.ToplamTutar)             AS GenelCiro,
              (SELECT COUNT(*) FROM servis.ServisCalisanlari c WHERE c.ServisId = sv.Id AND c.AktifMi = 1) AS AktifCalisan
            FROM servis.Servisler sv
            JOIN ortak.ServisTipleri st ON st.Id = sv.ServisTipiId
            JOIN talep.Talepler t       ON t.ServisId = sv.Id
            GROUP BY sv.Id, sv.Ad, st.Ad
            ORDER BY GenelCiro DESC;
            """);

        Assert.Contains("from sv in db.Servisler", linq);
        Assert.Contains("join st in db.ServisTipleri on sv.ServisTipiId equals st.Id", linq);
        Assert.Contains("join t in db.Talepler on sv.Id equals t.ServisId", linq);
        // çok tablolu grup: eleman JOIN'li satır; çakışan Ad → StAd
        Assert.Contains("group new { sv, st, t } by new { sv.Id, sv.Ad, StAd = st.Ad } into g", linq);
        Assert.Contains("orderby g.Sum(x => x.t.ToplamTutar) descending", linq);
        Assert.Contains("Servis = g.Key.Ad", linq);
        Assert.Contains("ServisTipi = g.Key.StAd", linq);
        Assert.Contains("TalepSayisi = g.Select(x => x.t.Id).Distinct().Count()", linq);
        Assert.Contains("ToplamIscilik = g.Sum(x => x.t.IscilikTutari)", linq);
        Assert.Contains("AktifCalisan = db.ServisCalisanlari.Count(c => c.ServisId == g.Key.Id && c.AktifMi == 1)", linq);
        Assert.EndsWith(").Take(20)", linq);
    }

    [Fact]
    public void Orderby_select_takma_adi_acilir()
    {
        string linq = Linq("SELECT s.Tutar * 2 AS Katli FROM Siparis s ORDER BY Katli DESC");

        Assert.Contains("orderby s.Tutar * 2 descending", linq);
    }

    [Fact]
    public void Getdate_uyari_ile_cevrilir()
    {
        SqlLinqSonucu sonuc = SqlLinqCevirici.Cevir("SELECT * FROM Siparis s WHERE s.Tarih < GETDATE()");

        Assert.Contains("s.Tarih < DateTime.Now", sonuc.Linq);
        Assert.Contains(sonuc.Uyarilar, u => u.Contains("istemci saati"));
    }

    // ── v20-S21 saha m.8: ON'daki anahtar-dışı koşullar + @parametre ────────────────────────

    [Fact]
    public void Join_on_ic_tablo_kosullari_kaynak_where_ine_tasinir()
    {
        string linq = Linq("SELECT t.Id FROM Talep t INNER JOIN Unvan u ON t.FirmaId = u.FirmaId"
            + " AND u.Aktif = 1 AND u.BitisTalepId IS NULL AND u.TescilDurumuId IN (2,3,5)");

        Assert.Contains("join u in db.Unvan.Where(u => u.Aktif == 1 && u.BitisTalepId == null"
            + " && new[] { 2, 3, 5 }.Contains(u.TescilDurumuId)) on t.FirmaId equals u.FirmaId", linq);
    }

    [Fact]
    public void Join_on_dis_tabloya_degen_kosul_where_satirina()
    {
        string linq = Linq("SELECT t.Id FROM Talep t INNER JOIN Unvan u ON t.FirmaId = u.FirmaId AND t.Tip = 3");

        Assert.Contains("join u in db.Unvan on t.FirmaId equals u.FirmaId\nwhere t.Tip == 3", linq);
    }

    [Fact]
    public void Left_join_on_dis_tabloya_degen_kosul_net_hata()
    {
        LinqCeviriHatasi hata = Assert.Throws<LinqCeviriHatasi>(() => Linq(
            "SELECT t.Id FROM Talep t LEFT JOIN Unvan u ON t.FirmaId = u.FirmaId AND t.Tip = 3"));
        Assert.Contains("LEFT JOIN", hata.Message);
    }

    [Fact]
    public void Left_join_on_ic_tablo_kosulu_kaynak_where_ine() // LEFT'te de doğru semantik
    {
        string linq = Linq("SELECT t.Id FROM Talep t LEFT JOIN Unvan u ON t.FirmaId = u.FirmaId AND u.Aktif = 1");

        Assert.Contains("join u in db.Unvan.Where(u => u.Aktif == 1) on t.FirmaId equals u.FirmaId into uGrup", linq);
    }

    [Fact]
    public void Sql_parametresi_degiskene_cevrilir()
    {
        SqlLinqSonucu sonuc = SqlLinqCevirici.Cevir("SELECT * FROM Talep t WHERE t.TalepNo = @TalepNo");

        Assert.Contains("t.TalepNo == talepNo", sonuc.Linq);
        Assert.Contains(sonuc.Uyarilar, u => u.Contains("@TalepNo"));
    }

    [Fact]
    public void Saha_m8_hisse_devri_union_sorgusu() // kullanıcının birebir sorgusu (3'lü UNION)
    {
        SqlLinqSonucu sonuc = SqlLinqCevirici.Cevir("""
            select
            DevredenKisi.Adi + ' ' + DevralanKisi.Soyadi as DevredenAdSoyad,
            DevralanKisi.Adi + ' ' + DevralanKisi.Soyadi as DevralanAdSoyad,
            hd.DevredilenHamilineHisseAdedi,
            hd.DevredilenNamaHisseAdedi,
            hd.DevredilenImtiyazliHisseAdedi,
            DevredenOrtaklikTuru.Adi as DevredenOrtakTuru,
            DevralanOrtaklikTuru.Adi as DevralanOrtakTuru
            from mersis.Talep t with(nolock)
            inner join mersis.HisseDevri hd with(nolock) on t.Id = hd.BaslangicTalepId and hd.TescilDurumuId in (2,3,5)
            inner join mersis.OrtakKisi DevredenOrtakKisi with(nolock) on DevredenOrtakKisi.OrtakId = hd.DevredenOrtakId
            inner join mersis.OrtakKisi DevralanOrtakKisi with(nolock) on DevralanOrtakKisi.OrtakId = hd.DevralanOrtakId
            inner join mersis.Ortak DevredenOrtak with(nolock) on DevredenOrtak.Id = DevredenOrtakKisi.OrtakId
            inner join mersis.Ortak DevralanOrtak with(nolock) on DevralanOrtak.Id = DevralanOrtakKisi.OrtakId
            inner join mersis.kisi DevredenKisi with(nolock) on DevredenKisi.Id = DevredenOrtakKisi.KisiId
            inner join mersis.kisi DevralanKisi with(nolock) on DevralanKisi.Id = DevralanOrtakKisi.KisiId
            inner join ortak.OrtaklikTuru DevredenOrtaklikTuru with(nolock) on DevredenOrtak.OrtaklikTuruId = DevredenOrtaklikTuru.Id
            inner join ortak.OrtaklikTuru DevralanOrtaklikTuru with(nolock) on DevralanOrtak.OrtaklikTuruId = DevralanOrtaklikTuru.Id
            where t.TalepDurumuId = 13 and t.TalepNo = @TalepNo
            UNION
            select
            DevredenUnvan.TamUnvan as DevredenAdSoyad,
            DevralanUnvan.TamUnvan as DevralanAdSoyad,
            hd.DevredilenHamilineHisseAdedi,
            hd.DevredilenNamaHisseAdedi,
            hd.DevredilenImtiyazliHisseAdedi,
            DevredenOrtaklikTuru.Adi as DevredenOrtakTuru,
            DevralanOrtaklikTuru.Adi as DevralanOrtakTuru
            from mersis.Talep t with(nolock)
            inner join mersis.HisseDevri hd with(nolock) on t.Id = hd.BaslangicTalepId and hd.TescilDurumuId in (2,3,5)
            inner join mersis.OrtakFirma DevredenOrtakFirma with(nolock) on DevredenOrtakFirma.OrtakFirmaId = hd.DevredenOrtakId
            inner join mersis.OrtakFirma DevralanOrtakFirma with(nolock) on DevralanOrtakFirma.OrtakFirmaId = hd.DevralanOrtakId
            inner join mersis.Ortak DevredenOrtak with(nolock) on DevredenOrtak.Id = DevredenOrtakFirma.OrtakId
            inner join mersis.Ortak DevralanOrtak with(nolock) on DevralanOrtak.Id = DevralanOrtakFirma.OrtakId
            inner join mersis.firma DevredenFirma with(nolock) on DevredenOrtakFirma.FirmaId = DevredenFirma.Id and DevredenFirma.TescilDurumuId = 3
            inner join mersis.firma DevralanFirma with(nolock) on DevralanOrtakFirma.FirmaId = DevralanFirma.Id and DevralanFirma.TescilDurumuId = 3
            inner join mersis.unvan DevredenUnvan with(nolock) on DevredenFirma.Id = DevredenUnvan.FirmaId and DevredenUnvan.Aktif = 1 and DevredenUnvan.BitisTalepId is null and DevredenUnvan.TescilDurumuId in (2,3,5)
            inner join mersis.unvan DevralanUnvan with(nolock) on DevralanFirma.Id = DevralanUnvan.FirmaId and DevralanUnvan.Aktif = 1 and DevralanUnvan.BitisTalepId is null and DevralanUnvan.TescilDurumuId in (2,3,5)
            inner join ortak.OrtaklikTuru DevredenOrtaklikTuru with(nolock) on DevredenOrtak.OrtaklikTuruId = DevredenOrtaklikTuru.Id
            inner join ortak.OrtaklikTuru DevralanOrtaklikTuru with(nolock) on DevralanOrtak.OrtaklikTuruId = DevralanOrtaklikTuru.Id
            where t.TalepDurumuId = 13 and t.TalepNo = @TalepNo
            UNION
            select
            DevredenUnvan.TamUnvan as DevredenAdSoyad,
            DevralanUnvan.TamUnvan as DevralanAdSoyad,
            hd.DevredilenHamilineHisseAdedi,
            hd.DevredilenNamaHisseAdedi,
            hd.DevredilenImtiyazliHisseAdedi,
            DevredenOrtaklikTuru.Adi as DevredenOrtakTuru,
            DevralanOrtaklikTuru.Adi as DevralanOrtakTuru
            from mersis.Talep t with(nolock)
            inner join mersis.HisseDevri hd with(nolock) on t.Id = hd.BaslangicTalepId and hd.TescilDurumuId in (2,3,5)
            inner join mersis.OrtakDisTuzelKisilik DevredenOrtakDtk with(nolock) on DevredenOrtakDtk.OrtakId = hd.DevredenOrtakId
            inner join mersis.OrtakDisTuzelKisilik DevralanOrtakDtk with(nolock) on DevralanOrtakDtk.OrtakId = hd.DevralanOrtakId
            inner join mersis.Ortak DevredenOrtak with(nolock) on DevredenOrtak.Id = DevredenOrtakDtk.OrtakId
            inner join mersis.Ortak DevralanOrtak with(nolock) on DevralanOrtak.Id = DevralanOrtakDtk.OrtakId
            inner join mersis.DisTuzelKisilik DevredenDtk with(nolock) on DevredenDtk.Id = DevredenOrtakDtk.DisTuzelKisilikId
            inner join mersis.DisTuzelKisilik DevralanDtk with(nolock) on DevralanDtk.Id = DevralanOrtakDtk.DisTuzelKisilikId
            inner join mersis.unvan DevredenUnvan with(nolock) on DevredenDtk.Id = DevredenUnvan.FirmaId and DevredenUnvan.Aktif = 1 and DevredenUnvan.BitisTalepId is null and DevredenUnvan.TescilDurumuId in (2,3,5)
            inner join mersis.unvan DevralanUnvan with(nolock) on DevralanDtk.Id = DevralanUnvan.FirmaId and DevralanUnvan.Aktif = 1 and DevralanUnvan.BitisTalepId is null and DevralanUnvan.TescilDurumuId in (2,3,5)
            inner join ortak.OrtaklikTuru DevredenOrtaklikTuru with(nolock) on DevredenOrtak.OrtaklikTuruId = DevredenOrtaklikTuru.Id
            inner join ortak.OrtaklikTuru DevralanOrtaklikTuru with(nolock) on DevralanOrtak.OrtaklikTuruId = DevralanOrtaklikTuru.Id
            where t.TalepDurumuId = 13 and t.TalepNo = @TalepNo
            """);

        Assert.Contains(".Union(", sonuc.Linq);                                       // 3 dal bağlandı
        Assert.Contains("db.HisseDevri.Where(hd => new[] { 2, 3, 5 }.Contains(hd.TescilDurumuId))", sonuc.Linq);
        Assert.Contains("devredenkisi.Adi + \" \" + devralankisi.Soyadi", sonuc.Linq); // metin birleştirme
        Assert.Contains("db.unvan.Where(devredenunvan => devredenunvan.Aktif == 1" // SQL'de tablo küçük: mersis.unvan
            + " && devredenunvan.BitisTalepId == null"
            + " && new[] { 2, 3, 5 }.Contains(devredenunvan.TescilDurumuId))", sonuc.Linq);
        Assert.Contains("t.TalepNo == talepNo", sonuc.Linq);                           // @TalepNo → değişken
        Assert.Single(sonuc.Uyarilar, u => u.Contains("@TalepNo"));                    // 3 dalda TEK uyarı
    }

    [Fact]
    public void Saha_m8_terkin_sorgusu() // kullanıcının ikinci birebir sorgusu
    {
        SqlLinqSonucu sonuc = SqlLinqCevirici.Cevir("""
            select
            t.Id as TalepId,
            u.TamUnvan as TamUnvan,
            f.MersisNo as MersisNo,
            tt.Adi as TalepTuru,
            '' as KararListesi,
            dh.Adi as TerkinHususu
            from Mersis.Talep t with(nolock)
            inner join Mersis.Unvan u with(nolock) on t.FirmaId = u.FirmaId and u.TescilDurumuId in (2,3,5) and u.BitisTalepId is null
            inner join Mersis.Firma f with(nolock) on f.Id = t.FirmaId
            inner join Ortak.TalepTuru tt with(nolock) on tt.Id = t.TalepTuruId
            inner join Mersis.Tescil tes with(nolock) on t.Id = tes.TalepId
            inner join Mersis.FirmaDegisiklikHususlari fdh WITH(NOLOCK) on t.Id = fdh.TalepId
            inner join Ortak.DegisiklikHususlari dh WITH(NOLOCK) on dh.Id = fdh.DegisiklikHususId
            where t.TalepTuruId IN (1056, 47, 6) and t.TalepNo = @TalepNo and f.TescilDurumuId = 3
            """);

        Assert.Contains("join u in db.Unvan.Where(u => new[] { 2, 3, 5 }.Contains(u.TescilDurumuId)"
            + " && u.BitisTalepId == null) on t.FirmaId equals u.FirmaId", sonuc.Linq);
        Assert.Contains("new[] { 1056, 47, 6 }.Contains(t.TalepTuruId)", sonuc.Linq);
        Assert.Contains("KararListesi = \"\"", sonuc.Linq);
        Assert.Contains("t.TalepNo == talepNo", sonuc.Linq);
    }
}
