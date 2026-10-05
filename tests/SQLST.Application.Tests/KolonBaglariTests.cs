using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>🔗 Kolon bağları çekirdeği (v20-S10): giden/gelen FK çıkarımı + JOIN sorgusu üretimi.</summary>
public class KolonBaglariTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());
    private static readonly ILehce Postgres = new PostgresLehcesi(new DpapiSecretProtector());

    private static readonly IReadOnlyList<YabanciAnahtar> Fkler =
    [
        new("dbo", "Siparis", ["MusteriId"], "dbo", "Musteri", ["Id"], "FK_Siparis_Musteri"),
        new("dbo", "Fatura", ["MusteriId"], "dbo", "Musteri", ["Id"], "FK_Fatura_Musteri"),
        new("dbo", "SiparisKalem", ["SiparisId", "KalemNo"], "dbo", "Siparis", ["Id", "No"], "FK_Kalem_Siparis"),
        new("satis", "Iade", ["MusteriId"], "dbo", "Musteri", ["Id"], null),
    ];

    [Fact]
    public void Giden_bag_bulunur_ve_karsi_kolon_eslenir()
    {
        IReadOnlyList<KolonBaglari.Bag> baglar = KolonBaglari.Bul("dbo", "Siparis", "MusteriId", Fkler);

        KolonBaglari.Bag bag = Assert.Single(baglar);
        Assert.True(bag.Giden);
        Assert.Equal("FK_Siparis_Musteri", bag.Fk.Ad);
        Assert.Equal("Id", bag.KarsiKolon);
    }

    [Fact]
    public void Gelen_baglar_bulunur_tum_isaret_edenler()
    {
        IReadOnlyList<KolonBaglari.Bag> baglar = KolonBaglari.Bul("dbo", "Musteri", "Id", Fkler);

        Assert.Equal(3, baglar.Count); // Siparis + Fatura + satis.Iade
        Assert.All(baglar, b => Assert.False(b.Giden));
        Assert.All(baglar, b => Assert.Equal("MusteriId", b.KarsiKolon));
    }

    [Fact]
    public void Bilesik_fk_karsi_kolon_indeksle_eslesir()
    {
        IReadOnlyList<KolonBaglari.Bag> baglar = KolonBaglari.Bul("dbo", "SiparisKalem", "KalemNo", Fkler);

        KolonBaglari.Bag bag = Assert.Single(baglar);
        Assert.Equal("No", bag.KarsiKolon); // 2. bacak → hedefin 2. kolonu (Id değil)
    }

    [Fact]
    public void Ayni_kolon_hem_giden_hem_gelen_olabilir_giden_once()
    {
        // Siparis.Id: SiparisKalem'in hedefi (gelen). Siparis'e bir de giden ekleyelim: Id → Arsiv.Id
        IReadOnlyList<YabanciAnahtar> fkler =
            [.. Fkler, new("dbo", "Siparis", ["Id"], "dbo", "Arsiv", ["Id"], "FK_Siparis_Arsiv")];

        IReadOnlyList<KolonBaglari.Bag> baglar = KolonBaglari.Bul("dbo", "Siparis", "Id", fkler);

        Assert.Equal(2, baglar.Count);
        Assert.True(baglar[0].Giden);  // gidenler listede önce
        Assert.False(baglar[1].Giden);
    }

    [Fact]
    public void Kiyas_buyuk_kucuk_duyarsiz()
    {
        IReadOnlyList<KolonBaglari.Bag> baglar = KolonBaglari.Bul("DBO", "SIPARIS", "musteriid", Fkler);
        Assert.Single(baglar);
    }

    [Fact]
    public void Sema_farkliysa_eslesmez()
    {
        // satis.Siparis diye bir tablo yok — dbo.Siparis'in FK'sı satis şemasına mal edilmez
        Assert.Empty(KolonBaglari.Bul("satis", "Siparis", "MusteriId", Fkler));
    }

    [Fact]
    public void Sema_bos_ise_tablo_adiyla_eslesir()
    {
        // MySQL gibi şemasız dünyada (ya da FK kaydı şemasız geldiyse) tablo adı yeter
        IReadOnlyList<KolonBaglari.Bag> baglar = KolonBaglari.Bul(null, "Siparis", "MusteriId", Fkler);
        Assert.Single(baglar);
    }

    [Fact]
    public void Bag_yoksa_bos_liste()
    {
        Assert.Empty(KolonBaglari.Bul("dbo", "Musteri", "Ad", Fkler));
    }

    [Fact]
    public void Gosterim_yon_taraflar_ve_kisit_adini_icerir()
    {
        KolonBaglari.Bag bag = KolonBaglari.Bul("dbo", "Siparis", "MusteriId", Fkler)[0];

        Assert.Equal("▲ dbo.Siparis(MusteriId) → dbo.Musteri(Id)  ·  FK_Siparis_Musteri", bag.Gosterim);
    }

    [Fact]
    public void Gosterim_adsiz_fk_ve_gelen_yon()
    {
        KolonBaglari.Bag iade = KolonBaglari.Bul("dbo", "Musteri", "Id", Fkler)
            .Single(b => b.Fk.KaynakTablo == "Iade");

        Assert.Equal("▼ satis.Iade(MusteriId) → dbo.Musteri(Id)", iade.Gosterim);
    }

    [Fact]
    public void Join_sorgusu_mssql_top_ve_koseli()
    {
        KolonBaglari.Bag bag = KolonBaglari.Bul("dbo", "Siparis", "MusteriId", Fkler)[0];

        string sql = KolonBaglari.JoinSorgusu(bag, Mssql);

        Assert.Equal(
            "SELECT TOP (100) * FROM [dbo].[Siparis] k JOIN [dbo].[Musteri] h ON k.[MusteriId] = h.[Id];",
            sql);
    }

    [Fact]
    public void Join_sorgusu_bilesik_fk_tum_bacaklar_on_kosulunda()
    {
        KolonBaglari.Bag bag = KolonBaglari.Bul("dbo", "SiparisKalem", "SiparisId", Fkler)[0];

        string sql = KolonBaglari.JoinSorgusu(bag, Mssql);

        Assert.Contains("k.[SiparisId] = h.[Id] AND k.[KalemNo] = h.[No]", sql);
    }

    [Fact]
    public void Join_sorgusu_postgres_limit_sonda()
    {
        KolonBaglari.Bag bag = KolonBaglari.Bul("dbo", "Siparis", "MusteriId", Fkler)[0];

        string sql = KolonBaglari.JoinSorgusu(bag, Postgres);

        Assert.StartsWith("SELECT * FROM", sql);
        Assert.Contains("LIMIT 100", sql);
    }
}
