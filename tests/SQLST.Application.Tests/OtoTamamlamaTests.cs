using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

public class OtoTamamlamaTests
{
    private static SemaOnbellegi Onbellek() => new()
    {
        YuklenmeZamaniUtc = DateTime.UtcNow,
        Nesneler =
        [
            new SemaNesnesi("db", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "int", false, true), new SemaKolonu("Ad", "nvarchar(50)", false, false)], []),
            new SemaNesnesi("db", "dbo", "Siparis", SemaNesneTuru.Tablo,
                [new SemaKolonu("MusteriId", "int", false, false)], []),
            new SemaNesnesi("db", "rapor", "vwOzet", SemaNesneTuru.View,
                [new SemaKolonu("Toplam", "decimal(18,2)", true, false)], []),
            new SemaNesnesi("db", "dbo", "spGetOrders", SemaNesneTuru.StoredProcedure, [], []),
        ],
    };

    [Fact]
    public void Duz_sozcukte_nesneler_ve_anahtar_sozcukler_onerilir()
    {
        const string metin = "SELECT * FROM Mus";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out int kelimeBasi);

        Assert.Equal(metin.Length - 3, kelimeBasi); // "Mus" başı
        Assert.Contains(oneriler, o => o.Metin == "Musteri");
        Assert.Contains(oneriler, o => o.Metin == "spGetOrders");
        Assert.Contains(oneriler, o => o.Metin == "SELECT");
        // nesneler anahtar sözcüklerden önce gelmeli
        Assert.True(oneriler.First(o => o.Metin == "Musteri").Oncelik
                  > oneriler.First(o => o.Metin == "SELECT").Oncelik);
    }

    [Fact]
    public void Onbellek_yokken_yalniz_anahtar_sozcukler()
    {
        var oneriler = OtoTamamlama.Oner("SEL", 3, null, out _);
        Assert.NotEmpty(oneriler);
        Assert.All(oneriler, o => Assert.Equal(1, o.Oncelik));
    }

    [Fact]
    public void Tablo_adi_nokta_kolonlari_onerir()
    {
        const string metin = "SELECT Musteri. FROM Musteri";
        int ofset = metin.IndexOf('.') + 1;

        var oneriler = OtoTamamlama.Oner(metin, ofset, Onbellek(), out int kelimeBasi);

        Assert.Equal(ofset, kelimeBasi); // boş sözcük — nokta sonrası
        Assert.Equal(2, oneriler.Count);
        Assert.Contains(oneriler, o => o.Metin == "Id" && o.Aciklama!.Contains("PK"));
        Assert.Contains(oneriler, o => o.Metin == "Ad");
    }

    [Theory]
    [InlineData("SELECT m. FROM dbo.Musteri m")]              // AS'siz takma ad
    [InlineData("SELECT m. FROM dbo.Musteri AS m")]           // AS'li
    [InlineData("SELECT m. FROM [dbo].[Musteri] m WHERE 1=1")] // köşeli
    public void Takma_ad_nokta_tablonun_kolonlarini_onerir(string metin)
    {
        int ofset = metin.IndexOf('.') + 1;

        var oneriler = OtoTamamlama.Oner(metin, ofset, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "Id");
        Assert.Contains(oneriler, o => o.Metin == "Ad");
    }

    [Fact]
    public void Join_takma_adi_da_cozulur()
    {
        const string metin = "SELECT s. FROM Musteri m INNER JOIN Siparis s ON s.MusteriId = m.Id";
        int ofset = metin.IndexOf('.') + 1;

        var oneriler = OtoTamamlama.Oner(metin, ofset, Onbellek(), out _);

        Assert.Single(oneriler);
        Assert.Equal("MusteriId", oneriler[0].Metin);
    }

    [Fact]
    public void Sema_hem_sema_hem_tablo_adindaysa_SEMA_kazanir() // kullanıcı isteği 2026-07-21
    {
        // "ortak" hem bir ŞEMA hem de (dbo'da) bir TABLO adı. "ortak." → şemadaki tablolar gelmeli.
        var onbellek = new SemaOnbellegi
        {
            YuklenmeZamaniUtc = DateTime.UtcNow,
            Nesneler =
            [
                new SemaNesnesi("db", "ortak", "Il", SemaNesneTuru.Tablo, [new SemaKolonu("Id", "int", false, true)], []),
                new SemaNesnesi("db", "dbo", "ortak", SemaNesneTuru.Tablo, [new SemaKolonu("Kolonu", "int", false, false)], []),
            ],
        };
        const string metin = "SELECT * FROM ortak.";

        var oneriler = OtoTamamlama.Oner(metin, metin.Length, onbellek, out _);

        Assert.Contains(oneriler, o => o.Metin == "Il");         // şema ortak'ın tablosu
        Assert.DoesNotContain(oneriler, o => o.Metin == "Kolonu"); // dbo.ortak TABLOSUNUN kolonu DEĞİL
    }

    [Fact]
    public void Sema_nokta_o_semanin_nesnelerini_onerir()
    {
        const string metin = "SELECT * FROM rapor.";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Single(oneriler);
        Assert.Equal("vwOzet", oneriler[0].Metin);
    }

    [Fact]
    public void Cozulmeyen_niteleyicide_bos_liste_pencere_acilmaz()
    {
        const string metin = "SELECT xyz.";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);
        Assert.Empty(oneriler);
    }

    [Fact]
    public void From_sonrasi_where_takma_ad_sanilmaz()
    {
        // "FROM Musteri WHERE" — WHERE takma ad değil; "Musteri." kolonları yine çözülmeli
        const string metin = "SELECT Musteri. FROM Musteri WHERE Id = 1";
        int ofset = metin.IndexOf('.') + 1;

        var oneriler = OtoTamamlama.Oner(metin, ofset, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "Id");
    }

    // ── Kullanıcı istekleri 2026-07-21 ────────────────────────────────────────

    [Fact]
    public void Yorum_satirinda_oneri_YOK() // (3)
    {
        const string metin = "SELECT * FROM Musteri\n-- burada Mus";
        Assert.Empty(OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _));
    }

    [Fact]
    public void Blok_yorumda_ve_string_icinde_oneri_YOK() // (3)
    {
        const string blok = "SELECT /* Mus";
        Assert.Empty(OtoTamamlama.Oner(blok, blok.Length, Onbellek(), out _));
        const string str = "SELECT 'Mus";
        Assert.Empty(OtoTamamlama.Oner(str, str.Length, Onbellek(), out _));
    }

    [Fact]
    public void Kapali_yorumdan_SONRA_oneri_gelir() // (3) — yorum bitince normale döner
    {
        const string metin = "SELECT /* not */ Mus";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);
        Assert.Contains(oneriler, o => o.Metin == "Musteri");
    }

    [Fact]
    public void WHERE_sonrasi_kolon_onerilir() // (6)
    {
        const string metin = "SELECT * FROM Musteri WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "Ad");  // Musteri kolonu
        Assert.Contains(oneriler, o => o.Metin == "Id");
    }

    [Fact]
    public void Nesne_onerisi_sema_nitelikli_EKLER() // (4) — süzülen ad sade, giren şema.ad
    {
        const string metin = "SELECT * FROM Mus";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        TamamlamaOnerisi musteri = oneriler.First(o => o.Metin == "Musteri");
        Assert.Equal("dbo.Musteri", musteri.Ekle);
    }

    [Fact]
    public void FROM_baglaminda_kolon_ONERILMEZ() // (4/6 bağlam ayrımı)
    {
        const string metin = "SELECT * FROM Musteri JOIN ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.DoesNotContain(oneriler, o => o.Metin == "Ad");    // kolon önerisi yok
        Assert.Contains(oneriler, o => o.Metin == "Siparis");     // tablo önerisi var
    }

    // ── Kullanıcı istekleri 2026-07-22 ────────────────────────────────────────

    [Fact]
    public void Duz_sozcukte_SEMA_adlari_da_onerilir() // "şemalar otomatik tamamlamaya çıkmıyor"
    {
        const string metin = "SELECT * FROM ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        // dbo ve rapor şemaları öneri gelmeli, SemaEtiketi ile işaretli (nesnelerden ayrı boyanır)
        Assert.Contains(oneriler, o => o.Metin == "dbo" && o.Aciklama == OtoTamamlama.SemaEtiketi);
        Assert.Contains(oneriler, o => o.Metin == "rapor" && o.Aciklama == OtoTamamlama.SemaEtiketi);
        // dbo 3 nesnede geçse de TEK şema önerisi (tekilleştirilir)
        Assert.Single(oneriler, o => o.Metin == "dbo" && o.Aciklama == OtoTamamlama.SemaEtiketi);
    }

    [Fact]
    public void Sema_onerisi_uye_noktasinda_gelmez() // "." sonrası yalnız üyeler; şema listesi değil
    {
        const string metin = "SELECT * FROM Musteri.";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.DoesNotContain(oneriler, o => o.Aciklama == OtoTamamlama.SemaEtiketi);
        Assert.Contains(oneriler, o => o.Metin == "Id"); // Musteri kolonları
    }

    // ── Kullanıcı istekleri 2026-07-28 (editör tamamlama v2) ──────────────────

    [Fact]
    public void Join_takma_adi_ON_baglaminda_duz_sozcuk_olarak_onerilir() // #1
    {
        // "…JOIN employee.kisi k ON " → 'k' önerilmeli. employee.kisi ÖNBELLEKTE YOK ama alias
        // sorgu metninden gelir → yine de çıkar (şemadan bağımsız). Kullanıcının bildirdiği durum.
        const string metin = "SELECT * FROM dbo.Musteri m INNER JOIN employee.kisi k ON ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "k" && o.Aciklama!.Contains("takma ad"));
        Assert.Contains(oneriler, o => o.Metin == "m" && o.Aciklama!.Contains("takma ad"));
    }

    [Fact]
    public void Takma_ad_onbellek_yuklenmeden_de_onerilir() // #1 — şemadan bağımsız
    {
        const string metin = "SELECT * FROM dbo.Musteri m WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, null, out _);

        Assert.Contains(oneriler, o => o.Metin == "m" && o.Aciklama!.Contains("takma ad"));
    }

    [Fact]
    public void Takma_ad_FROM_JOIN_baglaminda_ONERILMEZ() // #1 — bağlam ayrımı (Tablo'da alias yok)
    {
        // JOIN'den sonra Tablo bağlamı: yeni tablo yazılır, alias referansı değil → 'm' önerilmez.
        const string metin = "SELECT * FROM Musteri m INNER JOIN ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.DoesNotContain(oneriler, o => o.Aciklama is not null && o.Aciklama.Contains("takma ad"));
        Assert.Contains(oneriler, o => o.Metin == "Siparis"); // tablo önerisi yerinde
    }

    [Fact]
    public void WHERE_baglaminda_tablo_ve_sema_ONERILMEZ_yalniz_kolon() // #2
    {
        const string metin = "SELECT * FROM Musteri WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "Ad");           // kapsam kolonu VAR
        Assert.DoesNotContain(oneriler, o => o.Metin == "Siparis"); // tüm-tablo listesi YOK
        Assert.DoesNotContain(oneriler, o => o.Aciklama == OtoTamamlama.SemaEtiketi); // şema listesi YOK
    }

    [Fact]
    public void FROM_baglaminda_tablo_ve_sema_gelmeye_devam_eder() // #2 — Kolon-dışı bozulmadı
    {
        const string metin = "SELECT * FROM ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "Musteri");                          // tablo VAR
        Assert.Contains(oneriler, o => o.Metin == "dbo" && o.Aciklama == OtoTamamlama.SemaEtiketi); // şema VAR
    }

    // ── Kullanıcı bulgusu 2026-07-28: WHERE kolonları YALNIZ imleçteki ifadeden gelmeli ──────

    [Fact]
    public void Cok_ifadede_WHERE_yalniz_imlecteki_ifadenin_tablosunu_onerir()
    {
        // İki ayrı ifade boş satırla ayrık; imleç 2. ifadenin WHERE'inde. Yalnız Siparis kolonları
        // gelmeli — 1. ifadenin Musteri kolonu (Ad) SIZMAMALI (editördeki tüm tablolar geliyordu).
        const string metin = "SELECT * FROM Musteri WHERE Id = 1\n\nSELECT * FROM Siparis WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "MusteriId"); // Siparis kolonu (imleçteki ifade)
        Assert.DoesNotContain(oneriler, o => o.Metin == "Ad");  // Musteri kolonu — DİĞER ifade, sızmaz
    }

    [Fact]
    public void Joinli_ifadede_WHERE_tum_tablolarin_kolonlarini_onerir()
    {
        const string metin = "SELECT * FROM Musteri m INNER JOIN Siparis s ON s.MusteriId = m.Id WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "Ad");        // Musteri
        Assert.Contains(oneriler, o => o.Metin == "MusteriId"); // Siparis
    }

    [Fact]
    public void Joinli_ifade_baska_ifadenin_tablosunu_katmaz()
    {
        // 1. ifade rapor.vwOzet; 2. ifade Musteri JOIN Siparis. İmleç 2.'nin WHERE'inde → iki JOIN
        // tablosunun kolonları gelir ama vwOzet'in "Toplam"ı gelmez.
        const string metin =
            "SELECT * FROM rapor.vwOzet\n\nSELECT * FROM Musteri m JOIN Siparis s ON s.MusteriId = m.Id WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "Ad");           // Musteri (imleçteki ifade)
        Assert.Contains(oneriler, o => o.Metin == "MusteriId");    // Siparis (imleçteki ifade)
        Assert.DoesNotContain(oneriler, o => o.Metin == "Toplam"); // vwOzet — DİĞER ifade
    }

    [Fact]
    public void Cok_ifadede_kelimeBasi_MUTLAK_kalir() // CompletionWindow.StartOffset editör metnine göre
    {
        const string metin = "SELECT 1\n\nSELECT * FROM Mus";
        OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out int kelimeBasi);

        Assert.Equal(metin.Length - 3, kelimeBasi); // "Mus" başı, ifadeye göreli değil MUTLAK
    }

    // ── Kullanıcı istekleri 2026-07-29 (editör tamamlama v2b) ─────────────────

    private static SemaOnbellegi IkiTabloId() => new() // iki tabloda da "Id" var (belirsiz)
    {
        YuklenmeZamaniUtc = DateTime.UtcNow,
        Nesneler =
        [
            new SemaNesnesi("db", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "int", false, true), new SemaKolonu("Ad", "nvarchar(50)", false, false)], []),
            new SemaNesnesi("db", "dbo", "Adres", SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "int", false, true), new SemaKolonu("Sehir", "nvarchar(50)", false, false)], []),
        ],
    };

    [Fact]
    public void Join_belirsiz_kolon_a_b_nitelenmis_onerilir() // #6 — "join'de aynı adlı kolon karışıyor"
    {
        const string metin = "SELECT * FROM Musteri m JOIN Adres a ON a.Id = m.Id WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, IkiTabloId(), out _);

        // Belirsiz "Id": SADE verilmez; m.Id ve a.Id nitelenmiş (Ekle) olarak gelir.
        Assert.DoesNotContain(oneriler, o => o.Metin == "Id" && o.Ekle is null);
        Assert.Contains(oneriler, o => o.Ekle == "m.Id");
        Assert.Contains(oneriler, o => o.Ekle == "a.Id");
        // Tekil kolonlar sade kalır (nitelendirmeye gerek yok).
        Assert.Contains(oneriler, o => o.Metin == "Ad" && o.Ekle is null);
        Assert.Contains(oneriler, o => o.Metin == "Sehir" && o.Ekle is null);
    }

    [Fact]
    public void Self_join_ayni_tablo_iki_alias_nitelenmis() // #6 — self-join: her oluşum ayrı
    {
        const string metin = "SELECT * FROM Musteri a JOIN Musteri b ON a.Id = b.Id WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        // Musteri iki kez → Id ve Ad belirsiz → a./b. nitelenmiş.
        Assert.Contains(oneriler, o => o.Ekle == "a.Id");
        Assert.Contains(oneriler, o => o.Ekle == "b.Id");
        Assert.Contains(oneriler, o => o.Ekle == "a.Ad");
        Assert.Contains(oneriler, o => o.Ekle == "b.Ad");
    }

    [Fact]
    public void Tek_tabloda_kolon_sade_kalir_nitelenmez() // #6 — tek tabloda nitelendirme yok
    {
        const string metin = "SELECT * FROM Musteri WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "Id" && o.Ekle is null);
        Assert.DoesNotContain(oneriler, o => o.Ekle == "Musteri.Id");
    }

    [Fact]
    public void Yuklem_baglaminda_yalniz_yuklem_sozcukleri() // #4 — WHERE'de cümle başlatıcı gürültüsü yok
    {
        const string metin = "SELECT * FROM Musteri WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "AND");           // yüklem sözcüğü VAR
        Assert.Contains(oneriler, o => o.Metin == "LIKE");
        Assert.DoesNotContain(oneriler, o => o.Metin == "SELECT");  // cümle başlatıcı YOK
        Assert.DoesNotContain(oneriler, o => o.Metin == "INSERT INTO");
        Assert.DoesNotContain(oneriler, o => o.Metin == "FROM");
    }

    [Fact]
    public void Kolon_baglaminda_tam_sozcuk_listesi_korunur() // #4 — Kolon/Genel/Tablo bozulmadı
    {
        const string metin = "SELECT * FROM Mus"; // Tablo bağlamı ("Mus" FROM'dan sonra)
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "SELECT");
        Assert.Contains(oneriler, o => o.Metin == "INSERT INTO");
    }

    [Fact]
    public void SELECT_baglaminda_tum_kolonlar_onerisi_gelir() // #7 — tek tablo, sade liste
    {
        const string metin = "SELECT  FROM Musteri";
        int ofset = "SELECT ".Length; // imleç SELECT ile FROM arasında (Kolon bağlamı)
        var oneriler = OtoTamamlama.Oner(metin, ofset, Onbellek(), out _);

        TamamlamaOnerisi? tum = oneriler.FirstOrDefault(o => o.Metin.Contains("tüm kolonlar"));
        Assert.NotNull(tum);
        Assert.Equal("Id, Ad", tum!.Ekle); // Musteri kolonları virgülle
    }

    [Fact]
    public void SELECT_join_tum_kolonlar_niteleyicili() // #7 — JOIN'de kolonlar niteleyiciyle
    {
        const string metin = "SELECT  FROM Musteri m JOIN Siparis s ON s.MusteriId = m.Id";
        int ofset = "SELECT ".Length;
        var oneriler = OtoTamamlama.Oner(metin, ofset, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Ekle == "m.Id, m.Ad"); // Musteri
        Assert.Contains(oneriler, o => o.Ekle == "s.MusteriId"); // Siparis
    }

    [Fact]
    public void WHERE_baglaminda_tum_kolonlar_onerisi_YOK() // #7 — yalnız Kolon bağlamında
    {
        const string metin = "SELECT * FROM Musteri WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.DoesNotContain(oneriler, o => o.Metin.Contains("tüm kolonlar"));
    }

    [Theory]
    [InlineData("SELECT * FROM ")]                                   // FROM + boşluk
    [InlineData("SELECT * FROM Musteri JOIN ")]                      // JOIN
    [InlineData("SELECT * FROM Musteri INNER JOIN ")]                // çok-sözcüklü JOIN
    [InlineData("SELECT * FROM Musteri WHERE ")]                     // WHERE
    [InlineData("SELECT * FROM Musteri m JOIN Siparis s ON ")]       // ON
    [InlineData("SELECT * FROM Musteri WHERE Id = 1 AND ")]          // AND
    [InlineData("SELECT * FROM Musteri GROUP BY ")]                  // BY
    [InlineData("SELECT Id, ")]                                      // virgül (kolon listesi)
    [InlineData("SELECT ")]                                          // SELECT + boşluk
    public void OtoAcilmali_cumle_sozcugu_sonrasi_true(string metin) // #3 (2026-07-29)
        => Assert.True(OtoTamamlama.OtoAcilmali(metin, metin.Length));

    [Theory]
    [InlineData("SELECT * FROM Musteri WHERE Id = ")]  // '=' sonrası — mid-ifade, tetikleme
    [InlineData("SELECT * FROM Mus")]                  // kelime yazılıyor, boşluk yok
    [InlineData("SELECT * FROM Musteri m")]            // alias, boşluk yok
    [InlineData("")]                                   // boş
    public void OtoAcilmali_uygunsuz_yerde_false(string metin) // #3
        => Assert.False(OtoTamamlama.OtoAcilmali(metin, metin.Length));

    [Fact]
    public void OtoAcilmali_yorumda_tetiklemez() // #3 — yorum satırında pencere hiç açılmasın
    {
        const string metin = "SELECT * FROM Musteri\n-- WHERE ";
        Assert.False(OtoTamamlama.OtoAcilmali(metin, metin.Length));
    }

    private static SemaOnbellegi FkOnbellek() => new() // Siparis.MusteriId → Musteri.Id
    {
        YuklenmeZamaniUtc = DateTime.UtcNow,
        Nesneler =
        [
            new SemaNesnesi("db", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "int", false, true)], []),
            new SemaNesnesi("db", "dbo", "Siparis", SemaNesneTuru.Tablo,
                [new SemaKolonu("MusteriId", "int", false, false)], []),
        ],
        YabanciAnahtarlar =
        [
            new YabanciAnahtar("dbo", "Siparis", ["MusteriId"], "dbo", "Musteri", ["Id"]),
        ],
    };

    [Fact]
    public void ON_baglaminda_FK_esitligi_ilk_oneri() // #8 (2026-07-29)
    {
        const string metin = "SELECT * FROM Musteri m JOIN Siparis s ON ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, FkOnbellek(), out _);

        TamamlamaOnerisi fk = oneriler.First(o => o.Aciklama is not null && o.Aciklama.StartsWith("FK:"));
        Assert.Equal("s.MusteriId = m.Id", fk.Ekle);              // hazır JOIN eşitliği
        Assert.Equal(oneriler.Max(o => o.Oncelik), fk.Oncelik);  // listenin EN ÜSTÜNDE
    }

    [Fact]
    public void WHERE_baglaminda_FK_onerisi_YOK() // #8 — yalnız ON'da (WHERE'de değil)
    {
        const string metin = "SELECT * FROM Musteri m JOIN Siparis s ON s.MusteriId = m.Id WHERE ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, FkOnbellek(), out _);

        Assert.DoesNotContain(oneriler, o => o.Aciklama is not null && o.Aciklama.StartsWith("FK:"));
    }

    [Fact]
    public void FK_yoksa_ON_baglaminda_FK_onerisi_uretilmez() // #8 — FK'sız önbellekte çökmeyi/uydurmayı önle
    {
        const string metin = "SELECT * FROM Musteri m JOIN Siparis s ON ";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _); // varsayılan: FK yok

        Assert.DoesNotContain(oneriler, o => o.Aciklama is not null && o.Aciklama.StartsWith("FK:"));
    }

    private static SemaOnbellegi SpOnbellek() => new() // parametreli SP + fonksiyon
    {
        YuklenmeZamaniUtc = DateTime.UtcNow,
        Nesneler =
        [
            new SemaNesnesi("db", "dbo", "spSiparisGetir", SemaNesneTuru.StoredProcedure, [],
                [new SemaParametresi("@MusteriId", "int", false), new SemaParametresi("@Tarih", "datetime", false)]),
            new SemaNesnesi("db", "dbo", "fnHesapla", SemaNesneTuru.Fonksiyon, [],
                [new SemaParametresi("@Tutar", "decimal(18,2)", false)]),
        ],
    };

    [Fact]
    public void EXEC_sonrasi_parametre_imzasi() // #9 (2026-07-29)
    {
        const string metin = "EXEC dbo.spSiparisGetir ";
        ParametreImzasi? imza = OtoTamamlama.ParametreImzasiBul(metin, metin.Length, SpOnbellek());

        Assert.NotNull(imza);
        Assert.Equal("dbo.spSiparisGetir", imza!.NesneAd);
        Assert.Equal(2, imza.Parametreler.Count);
        Assert.Equal("@MusteriId", imza.Parametreler[0].Ad);
    }

    [Fact]
    public void Fonksiyon_paranteziyle_parametre_imzasi() // #9 — "fn(" deseni
    {
        const string metin = "SELECT dbo.fnHesapla(";
        ParametreImzasi? imza = OtoTamamlama.ParametreImzasiBul(metin, metin.Length, SpOnbellek());

        Assert.NotNull(imza);
        Assert.Equal("dbo.fnHesapla", imza!.NesneAd);
        Assert.Single(imza.Parametreler);
    }

    [Fact]
    public void Cagri_disinda_parametre_imzasi_YOK() // #9 — sıradan sorguda ipucu yok
        => Assert.Null(OtoTamamlama.ParametreImzasiBul(
            "SELECT * FROM Musteri WHERE ", 27, SpOnbellek()));

    [Fact]
    public void Kapanan_parantez_sonrasi_imza_YOK() // #9 — '(' dengeliyse çağrı bitmiştir
        => Assert.Null(OtoTamamlama.ParametreImzasiBul(
            "SELECT dbo.fnHesapla(5) ", "SELECT dbo.fnHesapla(5) ".Length, SpOnbellek()));

    [Fact]
    public void Yorumda_parametre_imzasi_YOK() // #9
        => Assert.Null(OtoTamamlama.ParametreImzasiBul(
            "-- EXEC dbo.spSiparisGetir ", "-- EXEC dbo.spSiparisGetir ".Length, SpOnbellek()));

    // ── @parametre önerileri (kullanıcı bulgusu 2026-07-31) ───────────────────

    [Fact]
    public void Belgede_gecen_parametreler_onerilir()
    {
        const string metin = "DECLARE @MusteriId INT = 5;\nSELECT * FROM Musteri WHERE Id = @";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out int kelimeBasi);

        Assert.Contains(oneriler, o => o.Metin == "@MusteriId" && o.Aciklama == "parametre");
        Assert.Equal(metin.Length - 1, kelimeBasi); // '@' kelimenin parçası — süzme @'la çalışır
    }

    [Fact]
    public void Yazilan_tek_gecisli_parametre_kendini_onermez()
    {
        // "@Yeni" yalnız yazılmakta olan sözcük — kendi kendine öneri gürültüsü olmaz;
        // @MusteriId (başka geçiş) önerilir.
        const string metin = "DECLARE @MusteriId INT;\nSET @Yeni";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "@MusteriId");
        Assert.DoesNotContain(oneriler, o => o.Metin == "@Yeni");
    }

    [Fact]
    public void Sistem_degiskenleri_parametre_sayilmaz() // @@ROWCOUNT vb.
    {
        const string metin = "SELECT @@ROWCOUNT;\nDECLARE @Ad INT;\nSELECT @";
        var oneriler = OtoTamamlama.Oner(metin, metin.Length, Onbellek(), out _);

        Assert.Contains(oneriler, o => o.Metin == "@Ad");
        Assert.DoesNotContain(oneriler, o => o.Metin == "@ROWCOUNT" || o.Metin == "@@ROWCOUNT");
    }
}
