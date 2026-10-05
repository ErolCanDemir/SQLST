using SQLST.App;
using SQLST.App.Views;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S16 — paket aktarımı hata raporu/günlük kilitleri (kullanıcı: "hataları loglayalım,
/// nereye loglandığını da söyleyelim"). İş bölümü: EKRAN ilk 20 örneği gösterir; TAM liste
/// "⬇ Hata raporunu kaydet" dosyasına, özet + ilk 200 örnek uygulama günlüğüne gider.
/// Bu sınır/format sözleşmeleri sessizce bozulursa saha yine "neden atlandı?" cevapsız kalır.
/// </summary>
public class AktarimGunluguTests
{
    private static AktarimSonucu Sonuc(int ornekSayisi, long atlanan) => new(
        Basarili: true, Okunan: 100, Yazilan: 100 - atlanan, Guncellenen: 0, Atlanan: atlanan,
        Hata: null, Sure: TimeSpan.FromSeconds(2),
        HataOrnekleri: [.. Enumerable.Range(1, ornekSayisi).Select(i => $"Satır {i}: sığmadı")]);

    [Fact]
    public void Hata_raporu_dosyasi_TUM_ornekleri_icerir() // ekranın 20 sınırı dosyada YOK
    {
        string metin = AktarimGunlugu.HataRaporuMetni("Paket Aktarım", "K", "H", Sonuc(25, 25));

        Assert.Contains("Satır 1: sığmadı", metin);
        Assert.Contains("Satır 21: sığmadı", metin);   // 20'den sonrası da dosyada
        Assert.Contains("Satır 25: sığmadı", metin);
        Assert.Contains("Kaynak: K", metin);
        Assert.Contains("Hedef:  H", metin);
        Assert.DoesNotContain("daha atlandı", metin);  // tavana takılmadı → uydurma not yok
    }

    [Fact]
    public void Hata_raporu_tavan_asimini_acikca_soyler() // sessiz kırpma yok
    {
        // Atlanan 30, örnek 25 — 5'inin mesajı toplanamamış (servis tavanı senaryosu).
        string metin = AktarimGunlugu.HataRaporuMetni("Paket Aktarım", "K", "H", Sonuc(25, 30));
        Assert.Contains("5 satır daha atlandı", metin);
    }

    [Fact]
    public void Ekran_raporu_ilk_20_ile_sinirli_ve_gunluk_yolunu_soyler()
    {
        string metin = AktarimPenceresi.RaporMetni(Sonuc(25, 25), AktarimGunlugu.GunlukNotu);

        Assert.Contains("Satır 20: sığmadı", metin);
        Assert.DoesNotContain("Satır 21: sığmadı", metin);          // ekranda 20'den sonrası yok
        Assert.Contains("toplam 25", metin);                        // ama toplamı açıkça yazar
        Assert.Contains("Hata raporunu kaydet", metin);             // tamamına giden yolu gösterir
        Assert.Contains("günlüğe yazıldı", metin);                  // kullanıcı isteği: nereye loglandı
        Assert.Contains("sqlst-", metin);                           // günlük dosya adı kalıbı
    }

    [Fact]
    public void Ekran_raporu_az_ornekte_eski_bicimiyle_ayni() // İçe Aktar penceresi de bunu kullanır
    {
        string metin = AktarimPenceresi.RaporMetni(Sonuc(3, 3));
        Assert.Contains("Hatalı satır örnekleri (ilk 3):", metin);
        Assert.DoesNotContain("günlüğe yazıldı", metin); // not istenmedikçe eklenmez (Dosya İçe Aktar)
    }
}
