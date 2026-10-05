using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>🕸 Kayıt Haritası çekirdeği (2026-07-31): tablo çıkarımı + üst/alt ilişki sorguları.
/// Özellik eşitliği (2026-08-03): üretici ILehce ile motor-parametrik — PG/MySQL da sabitlenir.</summary>
public class KayitHaritasiTests
{
    private static readonly ILehce Kurallar = new MssqlLehcesi(new DpapiSecretProtector());
    private static readonly ILehce Postgres = new PostgresLehcesi(new DpapiSecretProtector());
    private static readonly ILehce MySql = new MySqlLehcesi(new DpapiSecretProtector());

    private static SemaNesnesi Tablo(string sema, string ad)
        => new("db", sema, ad, SemaNesneTuru.Tablo, [], []);

    private static readonly IReadOnlyList<YabanciAnahtar> Fkler =
    [
        new YabanciAnahtar("dbo", "Siparis", ["MusteriId"], "dbo", "Musteri", ["Id"]),
    ];

    [Theory]
    [InlineData("SELECT * FROM a WHERE Id = 1", "a")]
    [InlineData("select top 10 * from [Mersis].[Kisi] where x=1", "Mersis.Kisi")]
    [InlineData("SELECT * FROM A JOIN B ON B.Id = A.Id", null)] // çok tablolu — belirsiz
    public void Tablo_cikarimi(string sql, string? beklenen)
        => Assert.Equal(beklenen, KayitHaritasi.TabloCikar(sql));

    [Fact]
    public void Ust_kayit_sorgusu_fk_degerinden() // Siparis satırı → ▲ Musteri
    {
        var iliskiler = KayitHaritasi.IliskiSorgulari(
            Tablo("dbo", "Siparis"),
            new Dictionary<string, object?> { ["Id"] = 7, ["MusteriId"] = 5 },
            Fkler, Kurallar);

        KayitHaritasi.Iliski ust = Assert.Single(iliskiler);
        Assert.StartsWith("▲ dbo.Musteri", ust.Baslik);
        Assert.Contains("FROM [dbo].[Musteri] WHERE [Id] = 5", ust.Sql);
        Assert.Contains("TOP (100)", ust.Sql);
        Assert.Contains("AS [İlişki]", ust.Sql); // grid'de ilişki adı kolonu
    }

    [Fact]
    public void Alt_kayit_sorgusu_pk_degerinden() // Musteri satırı → ▼ Siparis
    {
        var iliskiler = KayitHaritasi.IliskiSorgulari(
            Tablo("dbo", "Musteri"),
            new Dictionary<string, object?> { ["Id"] = 5, ["Ad"] = "Ali" },
            Fkler, Kurallar);

        KayitHaritasi.Iliski alt = Assert.Single(iliskiler);
        Assert.StartsWith("▼ dbo.Siparis", alt.Baslik);
        Assert.Contains("FROM [dbo].[Siparis] WHERE [MusteriId] = 5", alt.Sql);
    }

    [Fact]
    public void Null_fk_bacagi_atlanir_ve_script_yorumlu()
    {
        var iliskiler = KayitHaritasi.IliskiSorgulari(
            Tablo("dbo", "Siparis"),
            new Dictionary<string, object?> { ["Id"] = 7, ["MusteriId"] = DBNull.Value },
            Fkler, Kurallar);
        Assert.Empty(iliskiler); // bağ kurulmamış — sorgu üretilmez

        string script = KayitHaritasi.ScriptUret("dbo.Musteri",
            new Dictionary<string, object?>(),
            [new KayitHaritasi.Iliski("▲ x", "SELECT 1;", "SELECT TOP (1) 1;", 1, "dbo", "x", "1=1")]);
        Assert.Contains("🕸 Kayıt Haritası", script);
        Assert.Contains("-- ▲ x", script);
    }

    [Fact]
    public void Delete_script_once_altlar_sonra_kaynak_ustler_yok() // 🗑 (2026-07-31)
    {
        var musteri = new SemaNesnesi("db", "dbo", "Musteri", SemaNesneTuru.Tablo,
            [new SemaKolonu("Id", "int", false, true), new SemaKolonu("Ad", "nvarchar(50)", false, false)], []);
        string? script = KayitHaritasi.DeleteScriptUret(musteri,
            new Dictionary<string, object?> { ["Id"] = 5, ["Ad"] = "Ali" }, Fkler, Kurallar);

        Assert.NotNull(script);
        int altIdx = script!.IndexOf("DELETE FROM [dbo].[Siparis] WHERE [MusteriId] = 5;", StringComparison.Ordinal);
        int kaynakIdx = script.IndexOf("DELETE FROM [dbo].[Musteri] WHERE [Id] = 5;", StringComparison.Ordinal);
        Assert.True(altIdx >= 0 && kaynakIdx > altIdx); // önce alt, sonra kaynak
        Assert.DoesNotContain("Ad =", script);          // üst/ilgisiz kolon yok
        Assert.Contains("GÜVENLİ YAZMA", script);       // uyarı başlığı

        // PK'sız tablo → null (güvenli hedefleme yok)
        var pksiz = new SemaNesnesi("db", "dbo", "Log", SemaNesneTuru.Tablo,
            [new SemaKolonu("Mesaj", "nvarchar(100)", true, false)], []);
        Assert.Null(KayitHaritasi.DeleteScriptUret(pksiz, new Dictionary<string, object?>(), Fkler, Kurallar));
    }

    // --- Özellik eşitliği (2026-08-03): PG/MySQL çıktıları ---

    [Fact]
    public void Postgres_iliski_sorgusu_cift_tirnak_ve_limit()
    {
        var iliskiler = KayitHaritasi.IliskiSorgulari(
            Tablo("public", "siparis"),
            new Dictionary<string, object?> { ["id"] = 7, ["MusteriId"] = 5 },
            [new YabanciAnahtar("public", "siparis", ["MusteriId"], "public", "musteri", ["id"])],
            Postgres);

        KayitHaritasi.Iliski ust = Assert.Single(iliskiler);
        Assert.Contains("FROM \"public\".\"musteri\" WHERE \"id\" = 5 LIMIT 100;", ust.Sql);
        Assert.Contains("AS \"İlişki\"", ust.Sql);
        Assert.DoesNotContain("TOP", ust.Sql);
        Assert.DoesNotContain("N'", ust.Sql); // PG'de Unicode öneki yok
    }

    [Fact]
    public void Mysql_delete_script_backtick_ve_semasiz()
    {
        var musteri = new SemaNesnesi("db", "sirket", "musteri", SemaNesneTuru.Tablo,
            [new SemaKolonu("id", "int", false, true)], []);
        string? script = KayitHaritasi.DeleteScriptUret(musteri,
            new Dictionary<string, object?> { ["id"] = 5 },
            [new YabanciAnahtar("sirket", "siparis", ["musteri_id"], "sirket", "musteri", ["id"])],
            MySql);

        Assert.NotNull(script);
        int altIdx = script!.IndexOf("DELETE FROM `siparis` WHERE `musteri_id` = 5;", StringComparison.Ordinal);
        int kaynakIdx = script.IndexOf("DELETE FROM `musteri` WHERE `id` = 5;", StringComparison.Ordinal);
        Assert.True(altIdx >= 0 && kaynakIdx > altIdx); // sıra korunur; MySQL'de şema öneki yok
    }

    // ═══════════ v22-S9: dolu-süzme · 2. seviye · lookup atlama (kullanıcı isteği 25 Ağu 2026) ═══════════

    /// <summary>Sıradan tablo (çok kolonlu, tek referans, açıklama kolonu yok) → tanım tablosu DEĞİL.</summary>
    private static SemaNesnesi Gercek(string sema, string ad) => new(
        "db", sema, ad, SemaNesneTuru.Tablo,
        [
            new("Id", "int", false, true), new("Tutar", "decimal(18,2)", true, false),
            new("Tarih", "datetime2", true, false), new("Adet", "int", true, false),
            new("Notlar", "nvarchar(max)", true, false), new("Durum", "int", true, false),
        ], []);

    /// <summary>Tanım tablosu: az kolon + bariz açıklama kolonu (Ad).</summary>
    private static SemaNesnesi TanimTablo(string sema, string ad) => new(
        "db", sema, ad, SemaNesneTuru.Tablo,
        [new("Id", "int", false, true), new("Ad", "nvarchar(50)", true, false)], []);

    /// <summary>
    /// "Dolu mu?" sondası COUNT DEĞİL, TOP 1 olmalı — büyük tabloda COUNT tam tarama yaptırır,
    /// oysa yalnız "en az bir satır var mı" soruluyor. Bu sondanın ucuzluğu özelliğin şartı.
    /// </summary>
    [Fact]
    public void Varlik_sondasi_count_degil_tek_satir()
    {
        var iliskiler = KayitHaritasi.IliskiSorgulari(
            Tablo("dbo", "Siparis"),
            new Dictionary<string, object?> { ["Id"] = 7, ["MusteriId"] = 5 },
            Fkler, Kurallar);

        KayitHaritasi.Iliski i = Assert.Single(iliskiler);
        Assert.Contains("TOP (1)", i.VarlikSql);
        Assert.DoesNotContain("COUNT", i.VarlikSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM [dbo].[Musteri] WHERE [Id] = 5", i.VarlikSql);
        Assert.Equal(1, i.Seviye);
    }

    /// <summary>
    /// 🏷 Lookup atlama: ▲ tanım tablosuna gitmez AMA ▼ altlar elenmez. Ayrım kasıtlı — kullanıcı
    /// "tanım satırını görmek" istemiyor; oysa bir tanımı KULLANAN gerçek kayıtlar tam da aradığı şey.
    /// </summary>
    [Fact]
    public void Tanim_tablosuna_giden_ust_atlanir_altlar_kalir()
    {
        IReadOnlyList<YabanciAnahtar> fk =
        [
            new("dbo", "Siparis", ["DurumId"], "dbo", "SiparisDurumu", ["Id"]),  // ▲ tanıma
            new("dbo", "SiparisSatir", ["SiparisId"], "dbo", "Siparis", ["Id"]), // ▼ gerçek
        ];
        IReadOnlyList<SemaNesnesi> tablolar =
            [Gercek("dbo", "Siparis"), TanimTablo("dbo", "SiparisDurumu"), Gercek("dbo", "SiparisSatir")];

        var iliskiler = KayitHaritasi.IliskiSorgulari(
            Gercek("dbo", "Siparis"),
            new Dictionary<string, object?> { ["Id"] = 7, ["DurumId"] = 2 },
            fk, Kurallar, tablolar: tablolar, tanimTablolariniAtla: true);

        Assert.DoesNotContain(iliskiler, i => i.Ad == "SiparisDurumu"); // ▲ tanım atlandı
        Assert.Contains(iliskiler, i => i.Ad == "SiparisSatir");        // ▼ gerçek kaldı

        // Süzgeç KAPALIYKEN eski davranış birebir sürer (geriye dönük uyum).
        var hepsi = KayitHaritasi.IliskiSorgulari(
            Gercek("dbo", "Siparis"),
            new Dictionary<string, object?> { ["Id"] = 7, ["DurumId"] = 2 },
            fk, Kurallar, tablolar: tablolar);
        Assert.Contains(hepsi, i => i.Ad == "SiparisDurumu");
    }

    /// <summary>
    /// 🔗 2. seviye ALT SORGUYLA kurulur — 1. seviyenin satırları önce çekilmez. Böylece 1. seviyede
    /// birden çok satır varsa hepsi kapsanır ve fazladan veri okunmaz.
    /// </summary>
    [Fact]
    public void Ikinci_seviye_alt_sorguyla_kurulur()
    {
        IReadOnlyList<YabanciAnahtar> fk =
        [
            new("dbo", "SiparisSatir", ["SiparisId"], "dbo", "Siparis", ["Id"]),
            new("dbo", "SiparisSatir", ["UrunId"], "dbo", "Urun", ["Id"]),
        ];
        // 1. seviye: Siparis satırından ▼ SiparisSatir
        var birinci = KayitHaritasi.IliskiSorgulari(
            Gercek("dbo", "Siparis"),
            new Dictionary<string, object?> { ["Id"] = 7 },
            fk, Kurallar);
        KayitHaritasi.Iliski satirlar = Assert.Single(birinci);
        Assert.Equal("SiparisSatir", satirlar.Ad);

        // 2. seviye: SiparisSatir → ▲ Urun
        var ikinci = KayitHaritasi.IkinciSeviye(
            satirlar, Gercek("dbo", "Siparis"), fk, Kurallar,
            tablolar: [Gercek("dbo", "Urun")]);

        KayitHaritasi.Iliski urun = Assert.Single(ikinci);
        Assert.Equal("Urun", urun.Ad);
        Assert.Equal(2, urun.Seviye);
        Assert.Contains("üzerinden", urun.Baslik); // hangi tablo üzerinden geldiği başlıkta
        // Koşul alt sorgu: Urun.Id IN (SELECT UrunId FROM SiparisSatir WHERE <1. seviye koşulu>)
        Assert.Contains("IN (SELECT [UrunId] FROM [dbo].[SiparisSatir] WHERE [SiparisId] = 7)", urun.Sql);
    }

    // ── v23-S9: 🕸 İNME köprüsü + çok kolonlu FK + lookup kuralı (canlı tanı 1 Eki 2026) ─────

    /// <summary>
    /// ÇOK REFERANS ALAN GENİŞ TABLO LOOKUP DEĞİLDİR (v23-S9 — kullanıcı: "Talep ile bile
    /// eksiklerimiz oluyor"): eski kural "gelen FK ≥ 2 VEYA ≤5 kolon" idi — KdsDemo'da 5 FK alan
    /// 19 kolonlu talep.Talepler, TalepNo metin kolonu yüzünden lookup sanılıp haritanın ▲
    /// yönünden atılıyordu (Fatura satırından Talep'e çıkılamıyordu). Kural artık: açıklama
    /// kolonu VAR ve tablo KÜÇÜK (≤5 kolon). Küçük gerçek lookup'lar tanım kalır.
    /// </summary>
    [Fact]
    public void Cok_referansli_genis_tablo_lookup_sayilmaz()
    {
        var talep = new SemaNesnesi("db", "talep", "Talepler", SemaNesneTuru.Tablo,
            [
                new("Id", "int", false, true), new("TalepNo", "nvarchar(20)", true, false),
                new("ServisId", "int", true, false), new("AracId", "int", true, false),
                new("GirisTarihi", "datetime2", true, false), new("SikayetNotu", "nvarchar(max)", true, false),
                new("ToplamTutar", "decimal(18,2)", true, false),
            ], []);
        IReadOnlyList<YabanciAnahtar> fk =
        [
            new("finans", "Faturalar", ["TalepId"], "talep", "Talepler", ["Id"]),
            new("finans", "Odemeler", ["TalepId"], "talep", "Talepler", ["Id"]),
            new("talep", "TalepIslemleri", ["TalepId"], "talep", "Talepler", ["Id"]),
        ];

        Assert.False(LookupCozumleyici.TanimTablosuMu(talep, fk, out _, out int gelen));
        Assert.Equal(3, gelen); // bilgi değeri korunur

        // Küçük gerçek lookup hâlâ tanımdır (dar kural onları kaybetmez).
        Assert.True(LookupCozumleyici.TanimTablosuMu(TanimTablo("ortak", "Renkler"), fk, out string? aciklama, out _));
        Assert.Equal("Ad", aciklama);
    }

    /// <summary>
    /// Harita grid'inden TEKRAR harita istenince kaynak tablo [İlişki] başlığından çözülür —
    /// eski yol script'in İLK FROM'unu alıp Fatura satırının Id'sini BAŞKA talebin Id'si
    /// sanıyordu (sessiz yanlış harita; "belgenin altındakilere inemiyoruz"un kök nedeni).
    /// </summary>
    [Theory]
    [InlineData("▼▼ finans.Faturalar (TalepId → talep.Talepler.Id) · talep.Talepler üzerinden",
        "finans", "Faturalar")]
    [InlineData("▲ ortak.Iller (TescilIlId → Id)", "ortak", "Iller")]
    [InlineData("▼ SiparisSatir (SiparisId → bu.Id)", "", "SiparisSatir")] // şemasız motor (MySQL)
    public void Iliski_basligindan_kaynak_tablo_cozulur(string baslik, string sema, string ad)
    {
        (string Sema, string Ad)? sonuc = KayitHaritasi.IliskiBasligindanTablo(baslik);
        Assert.NotNull(sonuc);
        Assert.Equal(sema, sonuc.Value.Sema);
        Assert.Equal(ad, sonuc.Value.Ad);
    }

    [Theory]
    [InlineData("sıradan hücre değeri")] // harita başlığı değil
    [InlineData("")]
    public void Iliski_basligi_olmayan_deger_null_doner(string deger)
        => Assert.Null(KayitHaritasi.IliskiBasligindanTablo(deger));

    [Fact]
    public void Iliski_basligi_string_olmayan_degerde_null()
        => Assert.Null(KayitHaritasi.IliskiBasligindanTablo(DBNull.Value));

    /// <summary>
    /// ÇOK KOLONLU FK 2. seviyede artık SESSİZCE ATLANMAZ (v23-S9): tuple-IN her motorda
    /// olmadığından koşul EXISTS ile kurulur; kolonlar tablo adıyla nitelenir (ad çakışması
    /// yanlış kolona bağlanmasın). Tek kolonlu yol IN ile birebir eski davranıştadır.
    /// </summary>
    [Fact]
    public void Ikinci_seviye_cok_kolonlu_fk_exists_ile_kurulur()
    {
        IReadOnlyList<YabanciAnahtar> fk =
        [
            new("dbo", "Belge", ["TalepId"], "dbo", "Talep", ["Id"]),
            new("dbo", "BelgeEk", ["BelgeYil", "BelgeNo"], "dbo", "Belge", ["Yil", "No"]),
        ];
        var birinci = KayitHaritasi.IliskiSorgulari(
            Gercek("dbo", "Talep"), new Dictionary<string, object?> { ["Id"] = 7 }, fk, Kurallar);
        KayitHaritasi.Iliski belge = Assert.Single(birinci);

        var ikinci = KayitHaritasi.IkinciSeviye(belge, Gercek("dbo", "Talep"), fk, Kurallar);

        KayitHaritasi.Iliski ek = Assert.Single(ikinci);
        Assert.Equal("BelgeEk", ek.Ad);
        Assert.Contains("EXISTS (SELECT 1 FROM [dbo].[Belge] WHERE "
            + "[Belge].[Yil] = [BelgeEk].[BelgeYil] AND [Belge].[No] = [BelgeEk].[BelgeNo] "
            + "AND ([TalepId] = 7))", ek.Sql);
    }

    /// <summary>2. seviye kaynağa ya da 1. seviyeye GERİ DÖNMEZ — yoksa harita kendi üstüne kapanır.</summary>
    [Fact]
    public void Ikinci_seviye_kaynaga_geri_donmez()
    {
        IReadOnlyList<YabanciAnahtar> fk =
            [new("dbo", "SiparisSatir", ["SiparisId"], "dbo", "Siparis", ["Id"])];
        var birinci = KayitHaritasi.IliskiSorgulari(
            Gercek("dbo", "Siparis"), new Dictionary<string, object?> { ["Id"] = 7 }, fk, Kurallar);

        var ikinci = KayitHaritasi.IkinciSeviye(
            Assert.Single(birinci), Gercek("dbo", "Siparis"), fk, Kurallar);

        Assert.Empty(ikinci); // tek komşu kaynağın kendisiydi → hiçbir şey eklenmedi
    }

    /// <summary>
    /// Script'te YALNIZ dolu ilişkiler yer alır (kullanıcı kararı 25 Ağu 2026: "ilişki olmayanlar
    /// hiç gelmesin"). Boşların DÖKÜMÜ yazılmaz; SAYISI başlıkta kalır ki kaç tanesinin elendiği
    /// kaybolmasın. Atlanan tanım tablosu ise yazılır — o "boş çıktı" değil, bizim elediğimiz bir
    /// tablodur; yazılmazsa kullanıcı "X neden yok?" sorusunun cevabını üründe bulamaz.
    /// </summary>
    [Fact]
    public void Suzulmus_script_yalniz_dolulari_yazar_bos_dokumu_yok()
    {
        var dolu = new KayitHaritasi.Iliski(
            "▼ dbo.SiparisSatir (x)", "SELECT 1;", "SELECT TOP (1) 1;", 1, "dbo", "SiparisSatir", "1=1");
        var dolayli = new KayitHaritasi.Iliski(
            "▲▲ dbo.Urun (y)", "SELECT 2;", "SELECT TOP (1) 1;", 2, "dbo", "Urun", "1=1");

        string script = KayitHaritasi.SuzulmusScriptUret(
            "dbo.Siparis", [dolu, dolayli],
            bosSayisi: 3,
            atlananTanim: ["dbo.SiparisDurumu"]);

        Assert.Contains("2 DOLU ilişki (1 doğrudan · 1 dolaylı)", script);
        Assert.Contains("SELECT 1;", script);
        Assert.Contains("SELECT 2;", script);
        Assert.Contains("3 ilişki boş çıktı, getirilmedi", script); // sayı kaldı
        Assert.DoesNotContain("BOŞ ÇIKAN", script);                 // döküm GİTTİ
        Assert.Contains("Atlanan tanım/lookup tablosu: dbo.SiparisDurumu", script);
    }

    /// <summary>Hiç boş yoksa "boş çıktı" cümlesi de yazılmaz — gereksiz gürültü olmasın.</summary>
    [Fact]
    public void Bos_yoksa_bos_cumlesi_yazilmaz()
    {
        var dolu = new KayitHaritasi.Iliski(
            "▼ dbo.X (x)", "SELECT 1;", "SELECT TOP (1) 1;", 1, "dbo", "X", "1=1");

        string script = KayitHaritasi.SuzulmusScriptUret("dbo.Siparis", [dolu], bosSayisi: 0, atlananTanim: []);

        Assert.DoesNotContain("boş çıktı", script);
        Assert.DoesNotContain("Atlanan tanım", script);
    }
}
