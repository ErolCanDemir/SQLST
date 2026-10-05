using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v7-S1 — iki canlı şemanın farkı (SAF mantık). Nesne varlığı + ortak tablolarda kolon/PK + FK.
/// Aynı motor varsayılır; tip metinleri doğrudan kıyaslanır.
/// </summary>
public class SemaKarsilastiriciTests
{
    private static SemaKolonu K(string ad, string tip, bool nullable = false, bool pk = false)
        => new(ad, tip, nullable, pk);

    private static SemaNesnesi Tablo(string ad, params SemaKolonu[] kolonlar)
        => new("db", "dbo", ad, SemaNesneTuru.Tablo, kolonlar, []);

    private static SemaNesnesi Nesne(string ad, SemaNesneTuru tur)
        => new("db", "dbo", ad, tur, [], []);

    private static YabanciAnahtar Fk(string kaynakTablo, string kaynakKolon, string hedefTablo, string hedefKolon)
        => new("dbo", kaynakTablo, [kaynakKolon], "dbo", hedefTablo, [hedefKolon]);

    private static IReadOnlyList<SemaFarkSatiri> Karsilastir(
        IReadOnlyList<SemaNesnesi> sol, IReadOnlyList<SemaNesnesi> sag,
        IReadOnlyList<YabanciAnahtar>? solFk = null, IReadOnlyList<YabanciAnahtar>? sagFk = null)
        => SemaKarsilastirici.Karsilastir(sol, solFk ?? [], sag, sagFk ?? []);

    [Fact]
    public void Ayni_sema_FARK_vermez()
    {
        SemaNesnesi[] a = [Tablo("Musteri", K("Id", "int", pk: true), K("Ad", "nvarchar(50)"))];
        SemaNesnesi[] b = [Tablo("Musteri", K("Id", "int", pk: true), K("Ad", "nvarchar(50)"))];

        Assert.Empty(Karsilastir(a, b));
    }

    [Fact]
    public void Tablo_yalniz_bir_tarafta()
    {
        SemaNesnesi[] a = [Tablo("Musteri", K("Id", "int")), Tablo("Eski", K("Id", "int"))];
        SemaNesnesi[] b = [Tablo("Musteri", K("Id", "int")), Tablo("Yeni", K("Id", "int"))];

        IReadOnlyList<SemaFarkSatiri> f = Karsilastir(a, b);

        Assert.Contains(f, s => s.Nesne == "dbo.Eski" && s.Tur == SemaDegisim.YalnizSol && s.Kapsam == "Tablo");
        Assert.Contains(f, s => s.Nesne == "dbo.Yeni" && s.Tur == SemaDegisim.YalnizSag && s.Kapsam == "Tablo");
    }

    [Fact]
    public void Ortak_tabloda_kolon_eklendi_silindi()
    {
        SemaNesnesi[] a = [Tablo("Musteri", K("Id", "int"), K("Sehir", "nvarchar(30)"))];
        SemaNesnesi[] b = [Tablo("Musteri", K("Id", "int"), K("Eposta", "nvarchar(80)"))];

        IReadOnlyList<SemaFarkSatiri> f = Karsilastir(a, b);

        Assert.Contains(f, s => s.Kapsam == "Kolon: Sehir" && s.Tur == SemaDegisim.YalnizSol);
        Assert.Contains(f, s => s.Kapsam == "Kolon: Eposta" && s.Tur == SemaDegisim.YalnizSag);
    }

    [Fact]
    public void Kolon_tip_veya_null_veya_PK_degisimi_raporlanir()
    {
        SemaNesnesi[] a = [Tablo("Musteri", K("Id", "int", pk: true), K("Tutar", "int", nullable: false))];
        SemaNesnesi[] b = [Tablo("Musteri", K("Id", "int", pk: true), K("Tutar", "bigint", nullable: true))];

        SemaFarkSatiri f = Assert.Single(Karsilastir(a, b));

        Assert.Equal("Kolon: Tutar", f.Kapsam);
        Assert.Equal(SemaDegisim.Degisti, f.Tur);
        Assert.Equal("int NOT NULL  →  bigint NULL", f.Detay);
    }

    [Fact]
    public void FK_yalniz_bir_tarafta_ortak_tabloda()
    {
        SemaNesnesi[] a = [Tablo("Siparis", K("Id", "int"), K("MusteriId", "int")), Tablo("Musteri", K("Id", "int"))];
        SemaNesnesi[] b = [Tablo("Siparis", K("Id", "int"), K("MusteriId", "int")), Tablo("Musteri", K("Id", "int"))];

        IReadOnlyList<SemaFarkSatiri> f = Karsilastir(a, b,
            solFk: [Fk("Siparis", "MusteriId", "Musteri", "Id")], sagFk: []);

        SemaFarkSatiri fk = Assert.Single(f, s => s.Kapsam == "FK");
        Assert.Equal("dbo.Siparis", fk.Nesne);
        Assert.Equal(SemaDegisim.YalnizSol, fk.Tur);
        Assert.Contains("MusteriId", fk.Detay, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesne_varligi_TABLO_disi_turlerde_de_calisir()
    {
        SemaNesnesi[] a = [Nesne("uspRapor", SemaNesneTuru.StoredProcedure)];
        SemaNesnesi[] b = [];

        SemaFarkSatiri f = Assert.Single(Karsilastir(a, b));
        Assert.Equal("StoredProcedure", f.Kapsam);
        Assert.Equal(SemaDegisim.YalnizSol, f.Tur);
    }

    [Fact]
    public void Index_yapisal_fark_yalniz_bir_tarafta()
    {
        SemaNesnesi[] a = [Tablo("Musteri", K("Id", "int"), K("Eposta", "nvarchar(80)"))];
        SemaNesnesi[] b = [Tablo("Musteri", K("Id", "int"), K("Eposta", "nvarchar(80)"))];
        // Solda Eposta üzerinde UNIQUE index var, sağda yok → yalnız solda.
        Indeks[] solIx = [new("dbo", "Musteri", "UQ_Eposta", true, ["Eposta"])];

        IReadOnlyList<SemaFarkSatiri> f = SemaKarsilastirici.Karsilastir(a, [], b, [], solIx, []);

        SemaFarkSatiri ix = Assert.Single(f, s => s.Kapsam == "Index");
        Assert.Equal("dbo.Musteri", ix.Nesne);
        Assert.Equal(SemaDegisim.YalnizSol, ix.Tur);
        Assert.Contains("UNIQUE (Eposta)", ix.Detay, StringComparison.Ordinal);
    }

    [Fact]
    public void Ayni_yapisal_index_farkli_ADLA_fark_vermez()
    {
        SemaNesnesi[] a = [Tablo("Musteri", K("Id", "int"), K("Ad", "nvarchar(50)"))];
        SemaNesnesi[] b = [Tablo("Musteri", K("Id", "int"), K("Ad", "nvarchar(50)"))];
        // Aynı yapı (Ad üzerinde non-unique) ama FARKLI ad → yapısal imza eş → fark yok.
        Indeks[] solIx = [new("dbo", "Musteri", "IX_Ad_test", false, ["Ad"])];
        Indeks[] sagIx = [new("dbo", "Musteri", "IX_Ad_prod", false, ["Ad"])];

        IReadOnlyList<SemaFarkSatiri> f = SemaKarsilastirici.Karsilastir(a, [], b, [], solIx, sagIx);

        Assert.DoesNotContain(f, s => s.Kapsam == "Index");
    }

    [Fact]
    public void Mssql_index_sorgusu_PK_haric_kolon_bazli()
    {
        string sql = new SQLST.Infrastructure.MssqlLehcesi(new SQLST.Infrastructure.DpapiSecretProtector()).IndeksSorgusu;

        Assert.Contains("is_primary_key = 0", sql, StringComparison.Ordinal);
        Assert.Contains("is_included_column = 0", sql, StringComparison.Ordinal);
        Assert.Contains("sys.indexes", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Bir_tarafta_olmayan_tabloda_FK_farki_URETILMEZ()
    {
        // Siparis yalnız solda → "yalnız solda tablo" yeterli; ayrıca FK satırı gürültü olur.
        SemaNesnesi[] a = [Tablo("Siparis", K("Id", "int"), K("MusteriId", "int")), Tablo("Musteri", K("Id", "int"))];
        SemaNesnesi[] b = [Tablo("Musteri", K("Id", "int"))];

        IReadOnlyList<SemaFarkSatiri> f = Karsilastir(a, b,
            solFk: [Fk("Siparis", "MusteriId", "Musteri", "Id")], sagFk: []);

        Assert.DoesNotContain(f, s => s.Kapsam == "FK");
        Assert.Contains(f, s => s.Nesne == "dbo.Siparis" && s.Kapsam == "Tablo" && s.Tur == SemaDegisim.YalnizSol);
    }
}
