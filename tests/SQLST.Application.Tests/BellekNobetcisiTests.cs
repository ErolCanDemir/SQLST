using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>💾 Fiziksel RAM nöbetçisi eşik kuralı (v20-S15 "proje hiçbir zaman patlamamalı").</summary>
public class BellekNobetcisiTests
{
    [Theory]
    [InlineData(100, 100, true)]  // eşiğe ulaşıldı — kritik
    [InlineData(150, 100, true)]  // eşik aşılmış — kesinlikle kritik
    [InlineData(99, 100, false)]  // eşiğin altı — güvenli (marj BİLEREK yok: boştaki makine bile
    [InlineData(90, 100, false)]  //   eşiğin %90'ında seyredebiliyor; marj her okumayı keserdi)
    [InlineData(50, 100, false)]
    [InlineData(0, 100, false)]
    public void Esik_kurali_esige_ulasinca(long yuk, long esik, bool beklenen)
        => Assert.Equal(beklenen, BellekNobetcisi.KritikMi(yuk, esik));

    [Fact]
    public void Esik_sifir_veya_negatifse_kritik_sayilmaz()
    {
        // GC bilgisi alınamayan/anlamsız ortamda nöbetçi okuma kesmemeli (yanlış pozitif = veri gelmez)
        Assert.False(BellekNobetcisi.KritikMi(1000, 0));
        Assert.False(BellekNobetcisi.KritikMi(1000, -1));
    }

    [Theory]
    [InlineData(85, 100, true)]   // eşiğin %85'i — baskı başlar (uyarlanır bütçe devreye girer)
    [InlineData(100, 100, true)]
    [InlineData(84, 100, false)]  // %85 altı — normal bütçeler
    [InlineData(0, 100, false)]
    [InlineData(1000, 0, false)]  // anlamsız eşik → baskı sayılmaz
    public void Baski_kurali_yuzde_seksenbes(long yuk, long esik, bool beklenen)
        => Assert.Equal(beklenen, BellekNobetcisi.BaskiAltindaMi(yuk, esik));

    // 🧱 SÜREÇ TAVANI (v22-S1, saha turu-2 m.3/7/9): makine yükü rahatken de süreç ölebilir —
    // bayt bütçesi HAM veriyi sayar, nesne grafiği 3-6x büyüktür. Bu tavan gerçek yığına bakar.

    [Theory]
    [InlineData(1200, 1200, true)]  // tavana ulaşıldı — kes
    [InlineData(1500, 1200, true)]
    [InlineData(1199, 1200, false)] // altında — okumaya devam
    [InlineData(0, 1200, false)]
    [InlineData(5000, 0, false)]    // tavan anlamsızsa kesme (yanlış pozitif = veri gelmez)
    public void Surec_tavani_kurali(long yigin, long tavan, bool beklenen)
        => Assert.Equal(beklenen, BellekNobetcisi.SurecTavaniAstiMi(yigin, tavan));

    [Fact]
    public void Yigin_olcumu_canli_deger_dondurur()
    {
        // Ölçüm gerçekten çalışıyor mu (nöbetçi buna bakıyor). NOT: canlı SurecTavaniAstiMi()
        // ÜZERİNDE iddia YOK — süitin kendi yığını tavana yaklaşabilir, test kırılgan olurdu.
        long yigin = BellekNobetcisi.YiginBayt();
        Assert.True(yigin > 0);
        Assert.False(BellekNobetcisi.SurecTavaniAstiMi(yigin, long.MaxValue)); // tavan sonsuzsa kesme yok
        Assert.True(BellekNobetcisi.SurecTavaniAstiMi(yigin, 1));             // tavan 1 bayt ise kes
    }

    // ---- v22-S4 saha turu-4 m.4: okuma KESME kuralı ----
    // ÖLÇÜLDÜ (yerel MongoDB, limitli find): makine RAM'i doluyken okuma 64 belgede, biz daha
    // 0,4 MB okumuşken kesiliyordu — oysa RAM'i dolduran BİZ değildik (VS + süit + Mongo).
    // Kullanıcıya hiç veri vermeyen ama "korudum" diyen bir davranıştı. Düzeltmeden sonra AYNI
    // ölçüm: 10.000 satır, kesme yok, yığın 84 MB.

    [Theory]
    // (bizim yığın MB, makine kritik mi, kesilmeli mi)
    [InlineData(1, true, false)]      // makine kritik ama yığınımız 1 MB → RAM'i yiyen biz değiliz
    [InlineData(200, true, false)]    // hâlâ eşiğin (256 MB) altında
    [InlineData(256, true, true)]     // eşikte + makine kritik → kes
    [InlineData(700, false, false)]   // makine rahat, süreç tavanı (1,2 GB) da aşılmadı
    [InlineData(1300, false, true)]   // SÜREÇ TAVANI tek başına yeter — makine rahat olsa bile
    [InlineData(1300, true, true)]
    public void Okuma_kesme_kurali(int yiginMb, bool makineKritik, bool beklenen)
        => Assert.Equal(beklenen, BellekNobetcisi.OkumaKesilmeli(yiginMb * 1024L * 1024, makineKritik));

    [Fact]
    public void Kucuk_yiginda_makine_kritikligi_TEK_BASINA_kesmez()
    {
        // Eski kural burada true dönüyordu — kullanıcının sorgusu ilk yüz satırda sonlanıyordu.
        Assert.False(BellekNobetcisi.OkumaKesilmeli(8L * 1024 * 1024, makineKritik: true));
    }
}
