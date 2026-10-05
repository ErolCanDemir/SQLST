using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.10 fikir 2 — tanım tablosu sözlüğü: hücre başına scalar sorgu yerine tablo BİR KEZ
/// toptan okunur. Bu sınıf saf kısmı sabitler: sorgu şekli, sözlüğe çevirme kuralları (yinelenen/
/// null anahtar), tavan aşımında "tanım tablosu değil" kararı ve kültürden bağımsız anahtar.
/// </summary>
public class LookupSozluguTests
{
    private static readonly ILehce Mssql = new MssqlLehcesi(new DpapiSecretProtector());

    private static ResultSetData Set(params object?[][] satirlar) => new()
    {
        Kolonlar = [new KolonBilgisi("Id", "int"), new KolonBilgisi("Ad", "nvarchar")],
        Satirlar = satirlar,
    };

    [Fact]
    public void Sorgu_iki_kolonu_tavanla_okur()
    {
        string sql = LookupSozlugu.SorguYaz(Mssql, "dbo", "Durum", "Id", "Ad", tavan: 100);

        Assert.Contains("[Id]", sql);
        Assert.Contains("[Ad]", sql);
        Assert.Contains("[dbo].[Durum]", sql);
        Assert.Contains("101", sql); // tavan+1 — aşımı ANLAMAK için bir fazla satır istenir
    }

    [Fact]
    public void Sozluk_anahtar_aciklama_esler()
    {
        Dictionary<string, string>? s = LookupSozlugu.Coz(Set([1, "Beklemede"], [3, "Onaylandı"]));

        Assert.NotNull(s);
        Assert.Equal("Onaylandı", s!["3"]);
        Assert.Equal("Beklemede", s["1"]);
    }

    [Fact]
    public void Tavan_asilirsa_null_doner() // dev tablo = tanım tablosu değil → tooltip kapanır
        => Assert.Null(LookupSozlugu.Coz(Set([1, "a"], [2, "b"], [3, "c"]), tavan: 2));

    [Fact]
    public void Null_anahtar_atlanir_yinelenende_ilki_kalir()
    {
        Dictionary<string, string>? s = LookupSozlugu.Coz(
            Set([DBNull.Value, "boş"], [null, "boş2"], [7, "İlk"], [7, "Sonraki"]));

        Assert.NotNull(s);
        Assert.Single(s!);
        Assert.Equal("İlk", s!["7"]);
    }

    [Fact]
    public void Anahtar_kulturden_bagimsiz_ve_tipten_bagimsiz()
    {
        Assert.Equal("3", LookupSozlugu.Anahtar(3));
        Assert.Equal("3", LookupSozlugu.Anahtar(" 3 "));        // hücre metni boşluklu gelebilir
        Assert.Equal("3.5", LookupSozlugu.Anahtar(3.5));        // ondalık ayraç HER ZAMAN nokta
        Assert.Equal("", LookupSozlugu.Anahtar(DBNull.Value));
        Assert.Equal("", LookupSozlugu.Anahtar(null));
    }

    [Fact]
    public void Guid_ve_metin_anahtarlar_da_calisir()
    {
        var g = Guid.NewGuid();
        Dictionary<string, string>? s = LookupSozlugu.Coz(Set([g, "Kayıt"], ["KOD-1", "Kodlu"]));

        Assert.Equal("Kayıt", s![g.ToString()]);
        Assert.Equal("Kodlu", s["kod-1"]); // eşleşme büyük/küçük harf duyarsız
    }
}
