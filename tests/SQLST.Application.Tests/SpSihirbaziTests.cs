using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>🪄 SP Sihirbazı çekirdeği (2026-07-31): eşleştirme + FK yol bulma + SP üretimi.</summary>
public class SpSihirbaziTests
{
    private static SemaNesnesi Tablo(string sema, string ad, params SemaKolonu[] kolonlar)
        => new("db", sema, ad, SemaNesneTuru.Tablo, kolonlar, []);

    private static SemaOnbellegi Onbellek(IReadOnlyList<YabanciAnahtar>? fkler = null) => new()
    {
        YuklenmeZamaniUtc = DateTime.UtcNow,
        Nesneler =
        [
            Tablo("Mersis", "Kisi",
                new SemaKolonu("Id", "int", false, true),
                new SemaKolonu("TcKimlikNo", "decimal(18,0)", true, false),
                new SemaKolonu("Ad", "nvarchar(70)", false, false)),
            Tablo("Mersis", "Firma",
                new SemaKolonu("Id", "int", false, true),
                new SemaKolonu("Unvan", "nvarchar(200)", false, false)),
            Tablo("Mersis", "FirmaKisi",
                new SemaKolonu("Id", "int", false, true),
                new SemaKolonu("KisiId", "int", false, false),
                new SemaKolonu("FirmaId", "int", false, false)),
        ],
        YabanciAnahtarlar = fkler ??
        [
            new YabanciAnahtar("Mersis", "FirmaKisi", ["KisiId"], "Mersis", "Kisi", ["Id"]),
            new YabanciAnahtar("Mersis", "FirmaKisi", ["FirmaId"], "Mersis", "Firma", ["Id"]),
        ],
    };

    [Fact]
    public void Kolon_arama_tam_eslesme_once_kismi_sonra()
    {
        Assert.Single(SpSihirbazi.KolonAra(Onbellek(), "TcKimlikNo"));   // tam
        Assert.Single(SpSihirbazi.KolonAra(Onbellek(), "@TcKimlikNo"));  // @ soyulur
        // "Kimlik" tam eşleşmez → kısmi: TcKimlikNo bulunur
        Assert.Contains(SpSihirbazi.KolonAra(Onbellek(), "Kimlik"), a => a.Kolon.Ad == "TcKimlikNo");
        // "Id" TAM eşleşmesi 3 tabloda da var → 3 aday (kullanıcı seçer)
        Assert.Equal(3, SpSihirbazi.KolonAra(Onbellek(), "Id").Count);
        Assert.Empty(SpSihirbazi.KolonAra(Onbellek(), "OlmayanKolon"));
    }

    [Fact]
    public void Yol_bulma_ara_tablodan_gecer() // Kisi ↔ FirmaKisi ↔ Firma (köprü tablo istenmese de)
    {
        SemaOnbellegi o = Onbellek();
        var baglanamayan = new List<string>();
        IReadOnlyList<SpJoinAdimi> adimlar = SpSihirbazi.YolBul(
            [o.Nesneler[0], o.Nesneler[1]], o.YabanciAnahtarlar, baglanamayan); // Kisi kök, Firma hedef

        Assert.Empty(baglanamayan);
        Assert.Equal(2, adimlar.Count); // FirmaKisi (ara) + Firma
        Assert.Equal("FirmaKisi", adimlar[0].YeniTablo.Ad);
        Assert.Equal("Firma", adimlar[1].YeniTablo.Ad);
    }

    [Fact]
    public void Fk_yoksa_baglanamayan_raporlanir()
    {
        SemaOnbellegi o = Onbellek(fkler: []);
        var baglanamayan = new List<string>();
        SpSihirbazi.YolBul([o.Nesneler[0], o.Nesneler[1]], o.YabanciAnahtarlar, baglanamayan);
        Assert.Single(baglanamayan);
    }

    [Fact]
    public void Sp_uretimi_param_join_where_dogru()
    {
        SemaOnbellegi o = Onbellek();
        var kisi = o.Nesneler[0];
        var firma = o.Nesneler[1];
        var giris = new SpKolonAdayi(kisi, kisi.Kolonlar[1]);   // TcKimlikNo
        var cikis1 = new SpKolonAdayi(kisi, kisi.Kolonlar[2]);  // Ad
        var cikis2 = new SpKolonAdayi(firma, firma.Kolonlar[1]); // Unvan
        var baglanamayan = new List<string>();
        IReadOnlyList<SpJoinAdimi> joinler = SpSihirbazi.YolBul([kisi, firma], o.YabanciAnahtarlar, baglanamayan);

        string sp = SpSihirbazi.SpUret("spKisiFirma",
            [new SpGirisi("TcKimlikNo", giris)],
            [new SpCikisi("Ad", cikis1), new SpCikisi("Unvan", cikis2)], joinler);

        Assert.Contains("CREATE PROCEDURE [dbo].[spKisiFirma]", sp);
        Assert.Contains("@TcKimlikNo decimal(18,0)", sp);              // tip eşleşen kolondan
        Assert.Contains("FROM [Mersis].[Kisi] t1", sp);
        Assert.Contains("INNER JOIN [Mersis].[FirmaKisi]", sp);        // ara tablo JOIN'de
        Assert.Contains("INNER JOIN [Mersis].[Firma]", sp);
        Assert.Contains("= @TcKimlikNo", sp);                          // WHERE eşitliği
        Assert.Contains("SET NOCOUNT ON;", sp);
    }

    // ── v19-S16 (kullanıcı isteği 2026-08-04): akıllı çıkarım + eşleşmeyen alanlar ──

    /// <summary>FK TANIMLI DEĞİLKEN bile XId→X ad kuralı JOIN yolu üretir (gerçek DB senaryosu).</summary>
    [Fact]
    public void Ad_kuralindan_iliskiler_XId_kolonu_X_tablosuna_baglar()
    {
        // FK tanımı YOK; ama FirmaKisi.KisiId → Kisi, FirmaKisi.FirmaId → Firma ad kuralından çıkar.
        SemaOnbellegi o = Onbellek(fkler: []);
        IReadOnlyList<YabanciAnahtar> cikan = SpSihirbazi.AdKuralindanIliskiler(o);

        Assert.Contains(cikan, f => f.KaynakTablo == "FirmaKisi" && f.KaynakKolonlar[0] == "KisiId"
                                    && f.HedefTablo == "Kisi" && f.HedefKolonlar[0] == "Id");
        Assert.Contains(cikan, f => f.KaynakTablo == "FirmaKisi" && f.KaynakKolonlar[0] == "FirmaId"
                                    && f.HedefTablo == "Firma");
        Assert.All(cikan, f => Assert.Equal(SpSihirbazi.AdKuraliEtiketi, f.Ad)); // hepsi ad-kuralı işaretli
    }

    /// <summary>Ad kuralı FK'sı zaten TANIMLI olan kolon için çıkarım ÜRETİLMEZ (çift JOIN olmaz).</summary>
    [Fact]
    public void Ad_kurali_tanimli_FK_varsa_tekrarlamaz()
    {
        SemaOnbellegi o = Onbellek(); // FK'lar tanımlı
        IReadOnlyList<YabanciAnahtar> cikan = SpSihirbazi.AdKuralindanIliskiler(o);
        Assert.DoesNotContain(cikan, f => f.KaynakTablo == "FirmaKisi" && f.KaynakKolonlar[0] == "KisiId");
    }

    /// <summary>Ad kuralından bulunan yol SP'de LEFT JOIN + uyarı olarak yazılır.</summary>
    [Fact]
    public void Ad_kurali_yolu_LEFT_JOIN_olarak_yazilir()
    {
        SemaOnbellegi o = Onbellek(fkler: []); // tanımlı FK yok → yol ad kuralından
        var kisi = o.Nesneler[0];
        var firma = o.Nesneler[1];
        List<YabanciAnahtar> tumFkler = [.. SpSihirbazi.AdKuralindanIliskiler(o)];
        var baglanamayan = new List<string>();
        IReadOnlyList<SpJoinAdimi> joinler = SpSihirbazi.YolBul([kisi, firma], tumFkler, baglanamayan);

        Assert.Empty(baglanamayan); // ad kuralı sayesinde bağlanabildi
        Assert.Contains(joinler, j => j.AdKuralindan);

        string sp = SpSihirbazi.SpUret("spTest",
            [], [new SpCikisi("Ad", new SpKolonAdayi(kisi, kisi.Kolonlar[2])),
                 new SpCikisi("Unvan", new SpKolonAdayi(firma, firma.Kolonlar[1]))], joinler);
        Assert.Contains("LEFT JOIN", sp);
        Assert.DoesNotContain("INNER JOIN", sp); // hepsi ad kuralından
    }

    /// <summary>Karşılığı olmayan ÇIKIŞ alanı NULL AS [ad] olarak yine konur (kullanıcı isteği).</summary>
    [Fact]
    public void Eslesmeyen_cikis_NULL_kolon_olarak_konur()
    {
        SemaOnbellegi o = Onbellek();
        var kisi = o.Nesneler[0];
        string sp = SpSihirbazi.SpUret("spTest",
            [], [new SpCikisi("Ad", new SpKolonAdayi(kisi, kisi.Kolonlar[2])),
                 new SpCikisi("HicbirTabloda", null)], // eşleşme yok
            []);

        Assert.Contains("NULL AS [HicbirTabloda]", sp);
        Assert.Contains("t1.[Ad] AS [Ad]", sp);
    }

    /// <summary>Karşılığı olmayan GİRİŞ parametre olarak konur; WHERE'ye TODO yorumu düşer.</summary>
    [Fact]
    public void Eslesmeyen_giris_parametre_olarak_konur_where_yorumlu()
    {
        SemaOnbellegi o = Onbellek();
        var kisi = o.Nesneler[0];
        string sp = SpSihirbazi.SpUret("spTest",
            [new SpGirisi("SerbestParam", null)],
            [new SpCikisi("Ad", new SpKolonAdayi(kisi, kisi.Kolonlar[2]))], []);

        Assert.Contains("@SerbestParam nvarchar(200)", sp); // parametre kondu (varsayılan tip)
        Assert.Contains("@SerbestParam", sp);
        Assert.Contains("-- AND", sp);                       // WHERE'de yorumlu yer tutucu
    }

    /// <summary>Hiçbir kolon eşleşmezse crash yerine iskelet taslak üretir (NULL kolonlar + FROM yorumu).</summary>
    [Fact]
    public void Hicbir_eslesme_yoksa_iskelet_taslak()
    {
        string sp = SpSihirbazi.SpUret("spBos",
            [new SpGirisi("X", null)], [new SpCikisi("Y", null)], []);
        Assert.Contains("CREATE PROCEDURE [dbo].[spBos]", sp);
        Assert.Contains("NULL AS [Y]", sp);
        Assert.Contains("-- FROM", sp); // elle doldurulacak yer tutucu
    }

    // ── v19-S18 (kullanıcı isteği 2026-08-04): WHERE ek koşulları + parametre kuralları ──

    private static (SemaNesnesi Kisi, SpKolonAdayi TcGiris, SpCikisi AdCikis) TekTablo()
    {
        var o = Onbellek();
        var kisi = o.Nesneler[0];
        return (kisi, new SpKolonAdayi(kisi, kisi.Kolonlar[1]),           // TcKimlikNo
            new SpCikisi("Ad", new SpKolonAdayi(kisi, kisi.Kolonlar[2]))); // Ad
    }

    [Fact]
    public void Parametre_kurali_IF_RAISERROR_RETURN_uretir()
    {
        (SemaNesnesi kisi, SpKolonAdayi tc, SpCikisi ad) = TekTablo();
        string sp = SpSihirbazi.SpUret("spT", [new SpGirisi("TcKimlikNo", tc)], [ad], [],
            kurallar: [new SpKural("TcKimlikNo", "=", "0", "TcKimlikNo 0 olamaz")]);

        Assert.Contains("IF @TcKimlikNo = 0", sp);
        Assert.Contains("RAISERROR(N'TcKimlikNo 0 olamaz', 16, 1);", sp);
        Assert.Contains("RETURN;", sp);
        // Kural kontrolü SELECT'ten ÖNCE gelmeli (geçersizse hiç sorgu çalışmasın).
        Assert.True(sp.IndexOf("IF @TcKimlikNo", StringComparison.Ordinal)
                  < sp.IndexOf("SELECT", StringComparison.Ordinal));
    }

    [Fact]
    public void Kural_IS_NULL_degersiz_ve_bos_mesaj_otomatik()
    {
        (_, SpKolonAdayi tc, SpCikisi ad) = TekTablo();
        string sp = SpSihirbazi.SpUret("spT", [new SpGirisi("FirmaId", null)], [ad], [],
            kurallar: [new SpKural("FirmaId", "IS NULL", null, null)]);

        Assert.Contains("IF @FirmaId IS NULL", sp);
        Assert.Contains("RAISERROR(N'FirmaId degeri gecersiz.'", sp); // otomatik mesaj
    }

    [Fact]
    public void Ek_where_metin_kolonda_tirnaklar_sayisalda_ham()
    {
        (SemaNesnesi kisi, SpKolonAdayi tc, SpCikisi ad) = TekTablo();
        // Ad = nvarchar → tırnaklanır; Id = int → ham.
        string sp = SpSihirbazi.SpUret("spT", [new SpGirisi("TcKimlikNo", tc)], [ad], [],
            ekKosullar:
            [
                new SpEkKosul(kisi, kisi.Kolonlar[2], "LIKE", "Ahmet%"),  // Ad (metin)
                new SpEkKosul(kisi, kisi.Kolonlar[0], ">", "100"),        // Id (sayı)
            ]);

        Assert.Contains("[Ad] LIKE N'Ahmet%'", sp);   // metin tırnaklandı
        Assert.Contains("[Id] > 100", sp);            // sayı ham
        Assert.Contains("= @TcKimlikNo", sp);         // giriş eşitliği de duruyor (AND'lenir)
    }

    [Fact]
    public void Ek_where_IS_NULL_degersiz_ve_IN_parantezlenir()
    {
        (SemaNesnesi kisi, SpKolonAdayi tc, SpCikisi ad) = TekTablo();
        string sp = SpSihirbazi.SpUret("spT", [], [ad], [],
            ekKosullar:
            [
                new SpEkKosul(kisi, kisi.Kolonlar[1], "IS NULL", null),   // TcKimlikNo IS NULL
                new SpEkKosul(kisi, kisi.Kolonlar[0], "IN", "1,2,3"),     // Id IN (1,2,3)
            ]);

        Assert.Contains("[TcKimlikNo] IS NULL", sp);
        Assert.Contains("[Id] IN (1,2,3)", sp);
    }

    [Fact]
    public void Ek_where_sorguda_olmayan_tabloda_atlanir_yorumlanir()
    {
        (SemaNesnesi kisi, SpKolonAdayi tc, SpCikisi ad) = TekTablo();
        SemaNesnesi firma = Onbellek().Nesneler[1]; // sorguya girmeyecek (bağ yok, çıkış yok)
        string sp = SpSihirbazi.SpUret("spT", [new SpGirisi("TcKimlikNo", tc)], [ad], [],
            ekKosullar: [new SpEkKosul(firma, firma.Kolonlar[1], "=", "x")]); // Firma.Unvan

        Assert.Contains("bu tablo sorguda yok, koşul atlandı", sp); // sessizce düşürülmez
        Assert.DoesNotContain("[Unvan] =", sp);
    }

    // ── v19-S19 (mock-up onayı 2026-08-04): CREATE OR ALTER · TOP/DISTINCT · toplama+GROUP BY · AND/OR · ORDER BY · JOIN türü ──

    [Fact]
    public void Secenekler_create_or_alter_top_distinct_uretir()
    {
        (_, SpKolonAdayi tc, SpCikisi ad) = TekTablo();
        string sp = SpSihirbazi.SpUret("spT", [new SpGirisi("TcKimlikNo", tc)], [ad], [],
            secenekler: new SpSecenekleri(CreateOrAlter: true, Top: 100, Distinct: true));

        Assert.Contains("CREATE OR ALTER PROCEDURE [dbo].[spT]", sp);
        Assert.Contains("SELECT DISTINCT TOP (100)", sp);
    }

    [Fact]
    public void Toplama_SUM_group_by_ekler_toplamasiz_cikislari()
    {
        SemaOnbellegi o = Onbellek();
        var kisi = o.Nesneler[0];
        var firma = o.Nesneler[1];
        // Ad grup, Firma.Id üzerinde SUM (sayısal olsun diye Id kullanıyoruz; test yapısal).
        var cikisAd = new SpCikisi("Ad", new SpKolonAdayi(kisi, kisi.Kolonlar[2]));                 // grup
        var cikisTop = new SpCikisi("Toplam", new SpKolonAdayi(firma, firma.Kolonlar[0]), "SUM");   // SUM(Firma.Id)
        IReadOnlyList<SpJoinAdimi> joinler = SpSihirbazi.YolBul([kisi, firma], o.YabanciAnahtarlar, []);

        string sp = SpSihirbazi.SpUret("spT", [], [cikisAd, cikisTop], joinler);

        Assert.Contains("SUM(t3.[Id]) AS [Toplam]", sp);
        Assert.Contains("GROUP BY t1.[Ad]", sp);       // toplamasız çıkış gruplandı
        Assert.DoesNotContain("GROUP BY t3.[Id]", sp); // toplama kolonu gruplanmaz
    }

    [Fact]
    public void Ek_where_baglaci_OR_yazilir()
    {
        (SemaNesnesi kisi, SpKolonAdayi tc, SpCikisi ad) = TekTablo();
        string sp = SpSihirbazi.SpUret("spT", [new SpGirisi("TcKimlikNo", tc)], [ad], [],
            ekKosullar:
            [
                new SpEkKosul(kisi, kisi.Kolonlar[2], "LIKE", "A%", "AND"),
                new SpEkKosul(kisi, kisi.Kolonlar[2], "LIKE", "B%", "OR"),
            ]);

        Assert.Contains("OR t1.[Ad] LIKE N'B%'", sp); // ikinci koşul OR ile bağlandı
    }

    [Fact]
    public void Order_by_yon_ile_yazilir()
    {
        (SemaNesnesi kisi, SpKolonAdayi tc, SpCikisi ad) = TekTablo();
        string sp = SpSihirbazi.SpUret("spT", [new SpGirisi("TcKimlikNo", tc)], [ad], [],
            secenekler: new SpSecenekleri(Siralamalar:
                [new SpSiralama(kisi, kisi.Kolonlar[2], Azalan: true)]));

        Assert.Contains("ORDER BY t1.[Ad] DESC", sp);
    }

    // ── v19-S21 (kullanıcı isteği 2026-08-04): nitelenmiş ad + JOIN sıra geçerliliği ──

    [Fact]
    public void Nitelenmis_ad_yalniz_o_tablonun_kolonunu_dondurur()
    {
        SemaOnbellegi o = Onbellek();
        // Niteliksiz "Id" 3 tabloda da var (belirsiz).
        Assert.Equal(3, SpSihirbazi.KolonAra(o, "Id").Count);
        // "Firma.Id" yalnız Firma'yı verir.
        SpKolonAdayi tek = Assert.Single(SpSihirbazi.KolonAra(o, "Firma.Id"));
        Assert.Equal("Firma", tek.Tablo.Ad);
        Assert.Equal("Id", tek.Kolon.Ad);
        // Şema da nitelenebilir.
        Assert.Single(SpSihirbazi.KolonAra(o, "Mersis.FirmaKisi.KisiId"));
        // Nitelenmiş ama yanlış tablo → boş (uydurma tablo seçilmez).
        Assert.Empty(SpSihirbazi.KolonAra(o, "OlmayanTablo.Id"));
        // @ ön eki nitelenmişte de soyulur.
        Assert.Single(SpSihirbazi.KolonAra(o, "@Kisi.TcKimlikNo"));
    }

    [Fact]
    public void Join_sirasi_gecerliligi_bagimliligi_dogrular()
    {
        SemaOnbellegi o = Onbellek();
        var kisi = o.Nesneler[0];
        var firma = o.Nesneler[1];
        IReadOnlyList<SpJoinAdimi> yol = SpSihirbazi.YolBul([kisi, firma], o.YabanciAnahtarlar, []);
        // Doğal sıra (Kisi kök → FirmaKisi → Firma) geçerli.
        Assert.True(SpSihirbazi.JoinSirasiGecerli(kisi, yol));
        // Ters çevir: Firma önce gelirse FirmaKisi'ye bağımlı → geçersiz.
        Assert.False(SpSihirbazi.JoinSirasiGecerli(kisi, [.. yol.Reverse()]));
    }

    /// <summary>v19-S22 (kullanıcı isteği 2026-08-04): FK yok + ad kuralı tutmuyorsa (degisiklikId →
    /// degisiklikTanim) elle eklenen bağ FK havuzuna girer, YolBul tabloyu bağlar, SpUret INNER JOIN üretir.</summary>
    [Fact]
    public void Elle_eklenen_bag_otomatik_bulunamayan_tabloyu_baglar()
    {
        var degisiklik = Tablo("dbo", "degisiklik", new SemaKolonu("Id", "int", false, true),
            new SemaKolonu("firmaId", "int", true, false), new SemaKolonu("degisiklikId", "int", true, false));
        var degisiklikTanim = Tablo("dbo", "degisiklikTanim", new SemaKolonu("Id", "int", false, true),
            new SemaKolonu("adi", "nvarchar(200)", true, false));
        var o = new SemaOnbellegi { YuklenmeZamaniUtc = DateTime.UtcNow, Nesneler = [degisiklik, degisiklikTanim], YabanciAnahtarlar = [] };

        // Otomatik (FK + ad kuralı): degisiklikTanim bağlanamaz (degisiklikId ad-kuralı degisiklik'e gider).
        var bag1 = new List<string>();
        SpSihirbazi.YolBul([degisiklik, degisiklikTanim], [.. SpSihirbazi.AdKuralindanIliskiler(o)], bag1);
        Assert.Contains("dbo.degisiklikTanim", bag1);

        // Elle bağ: degisiklik.degisiklikId = degisiklikTanim.Id → FK havuzuna eklenir.
        var elleFk = new YabanciAnahtar("dbo", "degisiklik", ["degisiklikId"], "dbo", "degisiklikTanim", ["Id"]);
        var bag2 = new List<string>();
        IReadOnlyList<SpJoinAdimi> joinler = SpSihirbazi.YolBul([degisiklik, degisiklikTanim], [elleFk], bag2);
        Assert.Empty(bag2);
        Assert.Contains(joinler, j => j.YeniTablo.Ad == "degisiklikTanim");

        // Kök=degisiklik (giriş) ile tutarlı SP: INNER JOIN + doğru ON, ad-kuralı uyarısı YOK.
        var giris = new SpGirisi("firmaId", new SpKolonAdayi(degisiklik, degisiklik.Kolonlar[1]));
        var cikis = new SpCikisi("adi", new SpKolonAdayi(degisiklikTanim, degisiklikTanim.Kolonlar[1]));
        string sp = SpSihirbazi.SpUret("spT", [giris], [cikis], joinler);
        Assert.Contains("INNER JOIN [dbo].[degisiklikTanim]", sp);
        Assert.Contains("= t1.[degisiklikId]", sp);     // ON degisiklikTanim.Id = degisiklik.degisiklikId
        Assert.DoesNotContain("ad kuralından", sp);     // elle bağ güvenilir → uyarı yok
    }

    [Fact]
    public void Join_turu_zorlanabilir_INNER_LEFT()
    {
        SemaOnbellegi o = Onbellek();
        var kisi = o.Nesneler[0];
        var firma = o.Nesneler[1];
        // Kök=Kisi (giriş TcKimlikNo) → YolBul kökü ile SpUret kökü tutarlı.
        var giris = new SpGirisi("TcKimlikNo", new SpKolonAdayi(kisi, kisi.Kolonlar[1]));
        var cikis = new SpCikisi("Unvan", new SpKolonAdayi(firma, firma.Kolonlar[1]));
        IReadOnlyList<SpJoinAdimi> joinler = SpSihirbazi.YolBul([kisi, firma], o.YabanciAnahtarlar, []);
        var zorlanan = joinler.Select(j => j with { TurZorla = "LEFT JOIN" }).ToList();

        string varsayilan = SpSihirbazi.SpUret("spT", [giris], [cikis], joinler);
        string sol = SpSihirbazi.SpUret("spT", [giris], [cikis], zorlanan);

        Assert.Contains("INNER JOIN", varsayilan);   // gerçek FK → varsayılan INNER
        Assert.DoesNotContain("LEFT JOIN", varsayilan);
        Assert.Contains("LEFT JOIN", sol);           // kullanıcı LEFT'e zorladı
        Assert.DoesNotContain("INNER JOIN", sol);
    }
}
