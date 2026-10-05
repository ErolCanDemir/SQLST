using System.Text.Json;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.26 fikir 1 — "bu id nerede geçiyor?": MongoDB'de FK olmadığı için tarama alan
/// TİPİNE dayanır. Bu sınıf aday seçimini (tip uyumu, çoklu tip, tavan) ve üretilen sorguların
/// biçimini sabitler — ObjectId Extended JSON ile yazılmalı ki sürücü gerçek ObjectId olarak çözsün.
/// </summary>
public class MongoIdIziTests
{
    private static SemaNesnesi Koleksiyon(string ad, params (string Ad, string Tip)[] alanlar)
        => new("Db", "Db", ad, SemaNesneTuru.Koleksiyon,
            [.. alanlar.Select(a => new SemaKolonu(a.Ad, a.Tip, false, a.Ad == "_id"))], []);

    [Theory]
    [InlineData("507f1f77bcf86cd799439011", true)]
    [InlineData("507F1F77BCF86CD799439011", true)]   // büyük harf onaltılık
    [InlineData("507f1f77bcf86cd79943901", false)]   // 23 hane
    [InlineData("zzzf1f77bcf86cd799439011", false)]  // onaltılık değil
    [InlineData("Ali Veli", false)]
    public void ObjectId_bicimi_taninir(string deger, bool beklenen)
        => Assert.Equal(beklenen, MongoIdIzi.ObjectIdMi(deger));

    [Fact]
    public void ObjectId_aranirken_yalniz_objectId_alanlari_aday()
    {
        List<SemaNesnesi> koleksiyonlar =
        [
            Koleksiyon("kullanicilar", ("_id", "objectId"), ("ad", "string")),
            Koleksiyon("oturumlar", ("kullaniciId", "objectId"), ("giris", "date")),
            Koleksiyon("ayarlar", ("anahtar", "string"), ("deger", "string")),
        ];

        IReadOnlyList<IzAdayi> adaylar = MongoIdIzi.Adaylar(koleksiyonlar, "507f1f77bcf86cd799439011");

        Assert.Equal(2, adaylar.Count);
        Assert.Contains(new IzAdayi("kullanicilar", "_id"), adaylar);
        Assert.Contains(new IzAdayi("oturumlar", "kullaniciId"), adaylar);
    }

    [Fact]
    public void Coklu_tipli_alan_da_aday_olur() // envanterde "objectId|string" görülebilir
    {
        List<SemaNesnesi> k = [Koleksiyon("log", ("aktorId", "objectId|string"), ("mesaj", "string"))];
        Assert.Single(MongoIdIzi.Adaylar(k, "507f1f77bcf86cd799439011"));
    }

    [Fact]
    public void Metin_ve_sayi_kendi_tipindeki_alanlari_tarar()
    {
        List<SemaNesnesi> k =
        [
            Koleksiyon("log", ("kod", "string"), ("sayac", "int32"), ("kimlik", "objectId")),
        ];

        Assert.Equal([new IzAdayi("log", "kod")], MongoIdIzi.Adaylar(k, "ABC-1"));
        Assert.Equal([new IzAdayi("log", "sayac")], MongoIdIzi.Adaylar(k, "42"));
    }

    [Fact]
    public void Envanteri_bos_koleksiyon_ve_tablo_turu_atlanir()
    {
        List<SemaNesnesi> k =
        [
            Koleksiyon("bos"),                                                    // alan envanteri yok
            new("Db", "dbo", "SqlTablo", SemaNesneTuru.Tablo,
                [new SemaKolonu("Id", "objectId", false, true)], []),             // Mongo koleksiyonu değil
        ];
        Assert.Empty(MongoIdIzi.Adaylar(k, "507f1f77bcf86cd799439011"));
    }

    [Fact]
    public void Aday_tavani_asilmaz() // dev şemada sorgu yağmuru olmasın
    {
        List<SemaNesnesi> k =
        [
            .. Enumerable.Range(1, 40).Select(i => Koleksiyon($"k{i}",
                [.. Enumerable.Range(1, 10).Select(j => ($"alan{j}", "objectId"))])),
        ];
        Assert.Equal(MongoIdIzi.AdayTavani, MongoIdIzi.Adaylar(k, "507f1f77bcf86cd799439011").Count);
    }

    [Fact]
    public void Sayim_sorgusu_gecerli_json_ve_objectId_extended()
    {
        string sorgu = MongoIdIzi.SayimSorgusu(new IzAdayi("oturumlar", "kullaniciId"), "507f1f77bcf86cd799439011");

        using JsonDocument belge = JsonDocument.Parse(sorgu); // ⚠ bozuk JSON üretmediğimizin kanıtı
        Assert.Equal("oturumlar", belge.RootElement.GetProperty("aggregate").GetString());
        Assert.Contains("\"$oid\"", sorgu);      // sürücü gerçek ObjectId olarak çözsün
        Assert.Contains("\"$count\"", sorgu);
    }

    [Fact]
    public void Bul_sorgusu_limitli_ve_metin_kacisli()
    {
        string sorgu = MongoIdIzi.BulSorgusu(new IzAdayi("log", "mesaj"), "tırnak\"lı", limit: 50);

        using JsonDocument belge = JsonDocument.Parse(sorgu);
        Assert.Equal("log", belge.RootElement.GetProperty("find").GetString());
        Assert.Equal(50, belge.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal("tırnak\"lı", belge.RootElement.GetProperty("filter").GetProperty("mesaj").GetString());
    }

    [Fact]
    public void Sayi_degeri_ciplak_yazilir()
    {
        string sorgu = MongoIdIzi.SayimSorgusu(new IzAdayi("log", "sayac"), "42");
        using JsonDocument belge = JsonDocument.Parse(sorgu);
        JsonElement match = belge.RootElement.GetProperty("pipeline")[0].GetProperty("$match");
        Assert.Equal(42, match.GetProperty("sayac").GetInt32());   // tırnaklı "42" DEĞİL
    }
}
