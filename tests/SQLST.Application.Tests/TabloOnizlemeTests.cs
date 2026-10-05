using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 m.10 fikir 9 — editörde tablo önizleme: imleçteki adı çıkarma (köşeli parantez, şema,
/// üç parçalı ad), anahtar kelimeyi reddetme, şemada eşleştirme ve hizalı mini tablo metni.
/// </summary>
public class TabloOnizlemeTests
{
    private static SemaNesnesi Tablo(string sema, string ad, params SemaKolonu[] kolonlar)
        => new("Db", sema, ad, SemaNesneTuru.Tablo, kolonlar, []);

    [Theory]
    [InlineData("SELECT * FROM dbo.Talep t", "dbo.Talep")]       // nokta ile nitelenmiş
    [InlineData("SELECT * FROM [dbo].[Talep]", "dbo.Talep")]     // köşeli parantezler temizlenir
    [InlineData("SELECT * FROM Talep", "Talep")]                  // şemasız
    [InlineData("SELECT * FROM KdsDemo.dbo.Talep", "dbo.Talep")] // üç parçalıda son iki parça
    public void Tanimlayici_cikarilir(string satir, string beklenen)
    {
        int ofset = satir.IndexOf("Talep", StringComparison.Ordinal);
        Assert.Equal(beklenen, TabloOnizleme.TanimlayiciCikar(satir, ofset));
    }

    [Fact]
    public void Anahtar_kelime_bosluk_ve_sayi_reddedilir()
    {
        const string satir = "SELECT TOP 200 * FROM dbo.Talep";
        Assert.Null(TabloOnizleme.TanimlayiciCikar(satir, satir.IndexOf("FROM", StringComparison.Ordinal)));
        Assert.Null(TabloOnizleme.TanimlayiciCikar(satir, satir.IndexOf("200", StringComparison.Ordinal)));
        Assert.Null(TabloOnizleme.TanimlayiciCikar(satir, satir.IndexOf(" * ", StringComparison.Ordinal)));
        Assert.Null(TabloOnizleme.TanimlayiciCikar("", 0));
        Assert.Null(TabloOnizleme.TanimlayiciCikar(satir, 999));
    }

    [Fact]
    public void Semada_eslesir_semasiz_ad_da_bulunur()
    {
        List<SemaNesnesi> nesneler = [Tablo("satis", "Talep"), Tablo("dbo", "Musteri")];

        Assert.Equal("satis", TabloOnizleme.TabloBul(nesneler, "Talep")!.Sema);
        Assert.Equal("satis", TabloOnizleme.TabloBul(nesneler, "satis.Talep")!.Sema);
        Assert.Null(TabloOnizleme.TabloBul(nesneler, "dbo.Talep"));   // şema tutmuyor
        Assert.Null(TabloOnizleme.TabloBul(nesneler, "YokTablo"));
    }

    [Fact]
    public void Kolon_ozeti_pk_fk_isaretler_ve_kirpar()
    {
        SemaNesnesi t = Tablo("dbo", "Talep",
            new SemaKolonu("Id", "int", false, PkMi: true),
            new SemaKolonu("DurumId", "int", false, false, FkMi: true),
            new SemaKolonu("Not", "nvarchar(200)", true, false));

        string ozet = TabloOnizleme.KolonOzeti(t);
        Assert.Contains("🔑 Id  ·  int", ozet);
        Assert.Contains("🔗 DurumId", ozet);
        Assert.Contains("Not  ·  nvarchar(200), null", ozet);

        SemaNesnesi genis = Tablo("dbo", "Genis",
            [.. Enumerable.Range(1, 20).Select(i => new SemaKolonu($"K{i}", "int", false, false))]);
        Assert.Contains("+6 kolon daha", TabloOnizleme.KolonOzeti(genis));   // 20 - 14
    }

    [Fact]
    public void Mini_tablo_hizali_ve_kirpilmis()
    {
        var veri = new ResultSetData
        {
            Kolonlar = [new KolonBilgisi("Id", "int"), new KolonBilgisi("Ad", "nvarchar")],
            Satirlar = [[1, "Ali"], [102, "Çok çok çok uzun bir değer buraya"], [3, DBNull.Value]],
        };

        string metin = TabloOnizleme.MiniTablo(veri);
        string[] satirlar = metin.Split('\n');

        Assert.Equal("Id   Ad", satirlar[0]);          // başlık, en uzun değere göre hizalı
        Assert.StartsWith("──", satirlar[1]);           // ayraç
        Assert.Contains("NULL", metin);                 // null hücre açıkça
        Assert.Contains("…", metin);                    // uzun değer kırpıldı
        Assert.All(satirlar.Skip(2).Take(3), s => Assert.True(s.Length <= 40));
    }

    [Fact]
    public void Bos_sonuc_ve_kolon_tavani()
    {
        var bos = new ResultSetData { Kolonlar = [new KolonBilgisi("Id", "int")], Satirlar = [] };
        Assert.Contains("0 satır", TabloOnizleme.MiniTablo(bos));

        var genis = new ResultSetData
        {
            Kolonlar = [.. Enumerable.Range(1, 12).Select(i => new KolonBilgisi($"K{i}", "int"))],
            Satirlar = [[.. Enumerable.Range(1, 12).Cast<object?>()]],
        };
        Assert.Contains("+4 kolon daha", TabloOnizleme.MiniTablo(genis));   // 12 - 8
    }
}
