using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v6-S5 — Script → Görsel (tersine) ayrıştırıcı. En güçlü test ROUND-TRIP'tir: üretici bir
/// sorgu üretir, ayrıştırıcı onu geri çözer, model eşleşmeli. Ayrıca elle yazılmış SELECT'ler
/// ve hata halleri kontrol edilir. ScriptDom T-SQL'dir → yalnız SQL Server sözdizimi.
/// </summary>
public class GorselSorguCozumleyiciTests
{
    private static readonly ISecretProtector Koruyucu = new DpapiSecretProtector();
    private static readonly ILehce Mssql = new MssqlLehcesi(Koruyucu);

    private static SemaNesnesi Tablo(string sema, string ad) => new("db", sema, ad, SemaNesneTuru.Tablo, [], []);

    [Fact]
    public void RoundTrip_JOIN_ve_WHERE_geri_cozulur()
    {
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        string sql = GorselSorguUretici.Uret(Mssql, [m, s],
            [new GorselJoin(m, s, JoinTuru.Inner, [new GorselKolonEsi("Id", "MusteriId")])],
            [new GorselKosul(s, "tutar", KosulOperatoru.BuyukEsit, "100", false)]);

        CozumlenmisSorgu? c = GorselSorguCozumleyici.Coz(sql, out string hata);

        Assert.NotNull(c);
        Assert.Equal("", hata);
        Assert.Empty(c!.Uyarilar);

        Assert.Equal(2, c.Tablolar.Count);
        Assert.Contains(c.Tablolar, t => t is { Ad: "Musteri", Sema: "satis" });

        CozumlenmisJoin j = Assert.Single(c.Joinler);
        Assert.Equal(JoinTuru.Inner, j.Tur);
        Assert.Equal("Musteri", j.SolTakma);
        Assert.Equal("Siparis", j.SagTakma);
        Assert.Equal("Id", j.Kolonlar[0].SolKolon);
        Assert.Equal("MusteriId", j.Kolonlar[0].SagKolon);

        CozumlenmisKosul k = Assert.Single(c.Kosullar);
        Assert.Equal("Siparis", k.Takma);
        Assert.Equal("tutar", k.Kolon);
        Assert.Equal(KosulOperatoru.BuyukEsit, k.Operator);
        Assert.Equal("100", k.Deger);
    }

    [Fact]
    public void RoundTrip_LEFT_JOIN_ve_metin_kosulu()
    {
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        string sql = GorselSorguUretici.Uret(Mssql, [m, s],
            [new GorselJoin(m, s, JoinTuru.Left, [new GorselKolonEsi("Id", "MusteriId")])],
            [new GorselKosul(m, "sehir", KosulOperatoru.Esit, "Ankara", false)]);

        CozumlenmisSorgu? c = GorselSorguCozumleyici.Coz(sql, out _);

        Assert.Equal(JoinTuru.Left, c!.Joinler[0].Tur);
        Assert.Equal("Ankara", c.Kosullar[0].Deger); // N'Ankara' → "Ankara"
        Assert.Equal(KosulOperatoru.Esit, c.Kosullar[0].Operator);
    }

    [Fact]
    public void RoundTrip_LIKE_icerir_ve_IS_NULL()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        string sql = GorselSorguUretici.Uret(Mssql, [m], null,
        [
            new GorselKosul(m, "ad", KosulOperatoru.Icerir, "An", false),
            new GorselKosul(m, "sehir", KosulOperatoru.Bos, "", false),
        ]);

        CozumlenmisSorgu? c = GorselSorguCozumleyici.Coz(sql, out _);

        Assert.Equal(KosulOperatoru.Icerir, c!.Kosullar[0].Operator);
        Assert.Equal("An", c.Kosullar[0].Deger);   // %An% → An
        Assert.Equal(KosulOperatoru.Bos, c.Kosullar[1].Operator);
    }

    [Fact]
    public void RoundTrip_secili_kolonlar()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        string sql = GorselSorguUretici.Uret(Mssql, [m], null, null,
            [new GorselKolonAlani(m, "Id"), new GorselKolonAlani(m, "Ad")]);

        CozumlenmisSorgu? c = GorselSorguCozumleyici.Coz(sql, out _);

        Assert.Equal(2, c!.Secimler.Count);
        Assert.Contains(c.Secimler, s => s is { Takma: "Musteri", Kolon: "Id" });
    }

    [Fact]
    public void Elle_yazilmis_takma_adli_sorgu_cozulur()
    {
        const string sql = """
            SELECT k.ad, s.tutar
            FROM satis.Musteri AS k
            INNER JOIN satis.Siparis AS s ON k.id = s.musteri_id
            WHERE s.tutar > 50 OR k.aktif = 1;
            """;

        CozumlenmisSorgu? c = GorselSorguCozumleyici.Coz(sql, out string hata);

        Assert.NotNull(c);
        Assert.Equal("", hata);
        Assert.Equal(2, c!.Tablolar.Count);
        Assert.Contains(c.Tablolar, t => t is { Ad: "Musteri", Takma: "k" });
        Assert.Equal("k", c.Joinler[0].SolTakma);
        Assert.Equal("s", c.Joinler[0].SagTakma);
        Assert.Equal(2, c.Kosullar.Count);
        Assert.True(c.Kosullar[1].VeyaMi); // ikinci koşul OR
    }

    [Fact]
    public void Karmasik_sorgu_temsil_edilemeyen_parcalari_UYARIR()
    {
        // Kullanıcı bulgusu 2026-07-21: karmaşık sorgu sessizce basitleşip FARKLI sonuç veriyordu.
        // DISTINCT / GROUP BY / ORDER BY / SELECT alt sorgusu / ON=alt sorgu (CROSS) hepsi uyarılmalı.
        const string sql = """
            SELECT DISTINCT k.ad, (SELECT COUNT(*) FROM satis.Siparis s WHERE s.mid = k.id) AS cnt
            FROM satis.Musteri k
            INNER JOIN satis.Siparis o ON k.id = o.mid AND o.durum IN (1, 2)
            INNER JOIN satis.Il il ON il.id = (SELECT TOP 1 x FROM satis.T)
            WHERE k.sehir = 'Ankara' AND k.tip IN (1, 2)
            GROUP BY k.ad
            ORDER BY k.ad;
            """;

        CozumlenmisSorgu? c = GorselSorguCozumleyici.Coz(sql, out string hata);

        Assert.NotNull(c);
        Assert.Equal("", hata);
        string u = string.Join(" | ", c!.Uyarilar);
        Assert.Contains("DISTINCT", u, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", u, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", u, StringComparison.Ordinal);
        Assert.Contains("CROSS JOIN", u, StringComparison.Ordinal);   // il.id = (alt sorgu) → bağsız
        Assert.Contains("SELECT öğesi", u, StringComparison.Ordinal); // COUNT(*) alt sorgusu
    }

    [Fact]
    public void Basit_sorguda_HIC_uyari_olmaz()
    {
        // Onay penceresi yalnız gerçekten kayıp varsa çıksın: temiz sorguda uyarı listesi boş.
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        string sql = GorselSorguUretici.Uret(Mssql, [m, s],
            [new GorselJoin(m, s, JoinTuru.Inner, [new GorselKolonEsi("Id", "MusteriId")])],
            [new GorselKosul(s, "tutar", KosulOperatoru.BuyukEsit, "100", false)]);

        CozumlenmisSorgu? c = GorselSorguCozumleyici.Coz(sql, out _);

        Assert.Empty(c!.Uyarilar);
    }

    [Fact]
    public void SELECT_olmayan_metin_hata_verir()
    {
        Assert.Null(GorselSorguCozumleyici.Coz("UPDATE x SET a = 1;", out string hata));
        Assert.NotEqual("", hata);
    }

    [Fact]
    public void Ayristirilamayan_SQL_hata_verir()
    {
        Assert.Null(GorselSorguCozumleyici.Coz("SELECT FROM WHERE ((", out string hata));
        Assert.Contains("ayrıştırılamadı", hata, StringComparison.Ordinal);
    }
}
