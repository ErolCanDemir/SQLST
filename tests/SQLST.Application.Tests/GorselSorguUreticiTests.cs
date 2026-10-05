using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v6-S1 — Görsel Sorgu Tasarımcısı'nın SQL üretici çekirdeği. Tuval/sürükle-bırak WPF'tir
/// ve gözle doğrulanır; ÜRETİLEN SQL ise saf mantıktır ve burada motor motor kanıtlanır.
/// </summary>
public class GorselSorguUreticiTests
{
    private static readonly ISecretProtector Koruyucu = new DpapiSecretProtector();

    private static SemaNesnesi Tablo(string sema, string ad) =>
        new("db", sema, ad, SemaNesneTuru.Tablo, [], []);

    [Fact]
    public void Bos_tablo_listesi_bos_dize_verir()
    {
        // Çağıran düğmeyi pasifler; üretici yine de patlamaz.
        Assert.Equal("", GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), []));
    }

    [Fact]
    public void Tek_tablo_MSSQL_koseli_tirnakla()
    {
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [Tablo("satis", "Siparis")]);
        Assert.Equal("SELECT *\nFROM [satis].[Siparis];", sql);
    }

    [Theory]
    [InlineData("mssql", "[satis].[Siparis]")]
    [InlineData("postgres", "\"satis\".\"Siparis\"")]
    [InlineData("mysql", "`satis`.`Siparis`")]
    [InlineData("oracle", "\"satis\".\"Siparis\"")]
    public void Tek_tablo_HER_MOTORDA_dogru_tirnaklanir(string motorId, string beklenenAd)
    {
        ILehce lehce = LehceyiKur(motorId);
        string sql = GorselSorguUretici.Uret(lehce, [Tablo("satis", "Siparis")]);
        Assert.Equal($"SELECT *\nFROM {beklenenAd};", sql);
    }

    [Fact]
    public void Iki_tablo_ACIK_CROSS_JOIN_uretir()
    {
        // S1'de bağ çizilemez; iki bağsız tablo dürüstçe CROSS JOIN olur (S2 gerçek join'e çevirir).
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu),
            [Tablo("satis", "Musteri"), Tablo("satis", "Siparis")]);

        Assert.Equal(
            "SELECT *\nFROM [satis].[Musteri]\n    CROSS JOIN [satis].[Siparis];",
            sql);
    }

    [Fact]
    public void Sema_bossa_yalniz_ad_tirnaklanir()
    {
        // MySQL'de bazen şema=veritabanı ve nesne şemasız gelebilir — nokta atılmamalı.
        string sql = GorselSorguUretici.Uret(new MySqlLehcesi(Koruyucu), [Tablo("", "musteri")]);
        Assert.Equal("SELECT *\nFROM `musteri`;", sql);
    }

    // ── S2: JOIN üretimi ────────────────────────────────────────────────────

    private static GorselJoin Join(SemaNesnesi sol, SemaNesnesi sag, JoinTuru tur,
        params (string, string)[] kolonlar)
        => new(sol, sag, tur, [.. kolonlar.Select(k => new GorselKolonEsi(k.Item1, k.Item2))]);

    [Fact]
    public void Iki_tablo_INNER_JOIN_ON_ile()
    {
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m, s],
            [Join(m, s, JoinTuru.Inner, ("Id", "MusteriId"))]);

        Assert.Equal(
            "SELECT *\nFROM [satis].[Musteri]\n    INNER JOIN [satis].[Siparis] "
          + "ON [satis].[Musteri].[Id] = [satis].[Siparis].[MusteriId];",
            sql);
    }

    [Fact]
    public void LEFT_JOIN_kelimesi_dogru()
    {
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m, s],
            [Join(m, s, JoinTuru.Left, ("Id", "MusteriId"))]);

        Assert.Contains("LEFT JOIN [satis].[Siparis]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FROM_capasi_ters_yondeyse_LEFT_RIGHTe_cevrilir()
    {
        // Bağ Musteri→Siparis LEFT tanımlı; ama FROM çapası Siparis. Musteri'nin satırlarını
        // korumak için join RIGHT'a çevrilmeli (ve kolon çiftleri takas edilmeli).
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [s, m], // çapa = Siparis
            [Join(m, s, JoinTuru.Left, ("Id", "MusteriId"))]);

        Assert.Equal(
            "SELECT *\nFROM [satis].[Siparis]\n    RIGHT JOIN [satis].[Musteri] "
          + "ON [satis].[Siparis].[MusteriId] = [satis].[Musteri].[Id];",
            sql);
    }

    [Fact]
    public void Bilesik_anahtar_AND_ile_baglanir()
    {
        SemaNesnesi a = Tablo("dbo", "A"), b = Tablo("dbo", "B");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [a, b],
            [Join(a, b, JoinTuru.Inner, ("K1", "F1"), ("K2", "F2"))]);

        Assert.Contains(
            "ON [dbo].[A].[K1] = [dbo].[B].[F1] AND [dbo].[A].[K2] = [dbo].[B].[F2]",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Uc_tablo_zinciri_A_B_C()
    {
        SemaNesnesi a = Tablo("dbo", "A"), b = Tablo("dbo", "B"), c = Tablo("dbo", "C");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [a, b, c],
            [Join(a, b, JoinTuru.Inner, ("Id", "AId")), Join(b, c, JoinTuru.Inner, ("Id", "BId"))]);

        Assert.Equal(
            "SELECT *\nFROM [dbo].[A]"
          + "\n    INNER JOIN [dbo].[B] ON [dbo].[A].[Id] = [dbo].[B].[AId]"
          + "\n    INNER JOIN [dbo].[C] ON [dbo].[B].[Id] = [dbo].[C].[BId];",
            sql);
    }

    [Fact]
    public void Kopuk_bilesen_CROSS_JOIN_ile_koprulenir()
    {
        // A-B bağlı, C bağsız → C dürüstçe CROSS JOIN (sessizce kaybolmaz).
        SemaNesnesi a = Tablo("dbo", "A"), b = Tablo("dbo", "B"), c = Tablo("dbo", "C");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [a, b, c],
            [Join(a, b, JoinTuru.Inner, ("Id", "AId"))]);

        Assert.Equal(
            "SELECT *\nFROM [dbo].[A]"
          + "\n    INNER JOIN [dbo].[B] ON [dbo].[A].[Id] = [dbo].[B].[AId]"
          + "\n    CROSS JOIN [dbo].[C];",
            sql);
    }

    [Fact]
    public void JOIN_ON_kolonlari_HER_MOTORDA_tirnaklanir()
    {
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        GorselJoin[] j = [Join(m, s, JoinTuru.Inner, ("Id", "MusteriId"))];

        Assert.Contains("ON \"satis\".\"Musteri\".\"Id\" = \"satis\".\"Siparis\".\"MusteriId\"",
            GorselSorguUretici.Uret(new PostgresLehcesi(Koruyucu), [m, s], j), StringComparison.Ordinal);
        Assert.Contains("ON `satis`.`Musteri`.`Id` = `satis`.`Siparis`.`MusteriId`",
            GorselSorguUretici.Uret(new MySqlLehcesi(Koruyucu), [m, s], j), StringComparison.Ordinal);
    }

    [Fact]
    public void FULL_OUTER_JOIN_yazilir()
    {
        SemaNesnesi a = Tablo("dbo", "A"), b = Tablo("dbo", "B");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [a, b],
            [Join(a, b, JoinTuru.Full, ("Id", "AId"))]);

        Assert.Contains("FULL OUTER JOIN [dbo].[B]", sql, StringComparison.Ordinal);
    }

    // ── S3: WHERE üretimi ───────────────────────────────────────────────────

    private static GorselKosul Kosul(SemaNesnesi t, string kolon, KosulOperatoru op, string deger = "", bool veya = false)
        => new(t, kolon, op, deger, veya);

    [Fact]
    public void WHERE_metin_esitligi_N_tirnakli_MSSQL()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m], null,
            [Kosul(m, "sehir", KosulOperatoru.Esit, "Ankara")]);

        Assert.Equal(
            "SELECT *\nFROM [satis].[Musteri]\nWHERE [satis].[Musteri].[sehir] = N'Ankara';",
            sql);
    }

    [Fact]
    public void WHERE_sayi_TIRNAKSIZ()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m], null,
            [Kosul(m, "yas", KosulOperatoru.Buyuk, "30")]);

        Assert.Contains("WHERE [satis].[Musteri].[yas] > 30;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void WHERE_LIKE_ve_IS_NULL()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        Assert.Contains("[sehir] LIKE N'%An%'",
            GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m], null,
                [Kosul(m, "sehir", KosulOperatoru.Icerir, "An")]), StringComparison.Ordinal);
        Assert.Contains("[sehir] IS NULL",
            GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m], null,
                [Kosul(m, "sehir", KosulOperatoru.Bos)]), StringComparison.Ordinal);
    }

    [Fact]
    public void WHERE_AND_ve_OR_baglaclari()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m], null,
        [
            Kosul(m, "sehir", KosulOperatoru.Esit, "Ankara"),
            Kosul(m, "yas", KosulOperatoru.Buyuk, "30"),                    // AND (varsayılan)
            Kosul(m, "aktif", KosulOperatoru.Esit, "1", veya: true),        // OR
        ]);

        Assert.Equal(
            "SELECT *\nFROM [satis].[Musteri]"
          + "\nWHERE [satis].[Musteri].[sehir] = N'Ankara'"
          + "\n  AND [satis].[Musteri].[yas] > 30"
          + "\n   OR [satis].[Musteri].[aktif] = 1;",
            sql);
    }

    [Fact]
    public void WHERE_deger_PG_de_N_ONEKSIZ()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        string sql = GorselSorguUretici.Uret(new PostgresLehcesi(Koruyucu), [m], null,
            [Kosul(m, "sehir", KosulOperatoru.Esit, "Ankara")]);

        Assert.Contains("= 'Ankara'", sql, StringComparison.Ordinal); // PG'de N yok
        Assert.DoesNotContain("N'Ankara'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void WHERE_tirnak_KACISLANIR_enjeksiyon_olmaz()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m], null,
            [Kosul(m, "ad", KosulOperatoru.Esit, "O'Brien")]);

        Assert.Contains("= N'O''Brien'", sql, StringComparison.Ordinal); // tek tırnak ikilendi
    }

    [Fact]
    public void WHERE_JOIN_ile_birlikte()
    {
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m, s],
            [Join(m, s, JoinTuru.Inner, ("Id", "MusteriId"))],
            [Kosul(s, "tutar", KosulOperatoru.BuyukEsit, "100")]);

        Assert.Contains("INNER JOIN [satis].[Siparis]", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE [satis].[Siparis].[tutar] >= 100;", sql, StringComparison.Ordinal);
    }

    // ── S4: kolon seçimi (SELECT listesi) ───────────────────────────────────

    [Fact]
    public void SECILI_kolonlar_SELECT_listesi_uretir()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m], null, null,
            [new GorselKolonAlani(m, "Id"), new GorselKolonAlani(m, "Ad")]);

        Assert.StartsWith("SELECT [satis].[Musteri].[Id], [satis].[Musteri].[Ad]\nFROM", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Secim_YOKSA_SELECT_yildiz_kalir()
    {
        SemaNesnesi m = Tablo("satis", "Musteri");
        Assert.StartsWith("SELECT *", GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m]), StringComparison.Ordinal);
    }

    [Fact]
    public void Iki_tablodan_secili_kolonlar_tam_nitelenir()
    {
        SemaNesnesi m = Tablo("satis", "Musteri"), s = Tablo("satis", "Siparis");
        string sql = GorselSorguUretici.Uret(new MssqlLehcesi(Koruyucu), [m, s],
            [Join(m, s, JoinTuru.Inner, ("Id", "MusteriId"))], null,
            [new GorselKolonAlani(m, "Ad"), new GorselKolonAlani(s, "Tutar")]);

        Assert.StartsWith("SELECT [satis].[Musteri].[Ad], [satis].[Siparis].[Tutar]\nFROM", sql, StringComparison.Ordinal);
    }

    private static ILehce LehceyiKur(string motorId) => motorId switch
    {
        "mssql" => new MssqlLehcesi(Koruyucu),
        "postgres" => new PostgresLehcesi(Koruyucu),
        "mysql" => new MySqlLehcesi(Koruyucu),
        "oracle" => new OracleLehcesi(Koruyucu),
        _ => throw new ArgumentException(motorId),
    };
}
