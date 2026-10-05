using System.IO;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v22-S4 (saha turu-4 m.8, çok ajanlı çökme denetimi): faz izi. Dört turdur çökmenin NEREDE
/// olduğunu bilemedik; uçuş kaydedicide çökme ile normal kapanış BİREBİR aynı görünüyordu.
/// <see cref="Iz"/> bunu bitirir — ama ancak GERÇEKTEN yazıyorsa. Bu testler onu sabitler.
/// </summary>
public class IzTests
{
    [Fact]
    public void Faz_dosyaya_yazar_ve_uzerine_yazilir()
    {
        Iz.Faz("test-faz-bir");
        string ilk = File.ReadAllText(Iz.Yol);
        Assert.Contains("test-faz-bir", ilk);
        Assert.Contains($"pid={Environment.ProcessId}", ilk);
        Assert.Contains("yigin=", ilk);

        Iz.Faz("test-faz-iki", 4242);
        string ikinci = File.ReadAllText(Iz.Yol);

        // ÜZERİNE yazılır (dosya büyümez) — eski faz kalmaz, son faz nettir.
        Assert.DoesNotContain("test-faz-bir", ikinci);
        Assert.Contains("test-faz-iki", ikinci);
        Assert.Contains("n=4242", ikinci);
    }

    [Fact]
    public void Temiz_cikis_damgasi_yazilir()
    {
        Iz.TemizCikis();
        Assert.Contains("temiz-cikis", File.ReadAllText(Iz.Yol));
    }

    /// <summary>
    /// İz bir KOLAYLIKTIR: yazamazsa uygulamayı düşürmemeli. (Klasör yolu geçersiz olsa bile
    /// çağrı sessizce geçmeli — çökme anında atılan bir istisna, tanılamayı kurtarmak isterken
    /// asıl işi bozardı.)
    /// </summary>
    [Fact]
    public void Yazamazsa_istisna_firlatmaz()
    {
        string yedek = File.Exists(Iz.Yol) ? File.ReadAllText(Iz.Yol) : "";

        // Dosyayı paylaşımsız kilitle → WriteAllText IOException almalı, Faz onu YUTMALI.
        using (var kilit = new FileStream(Iz.Yol, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            Iz.Faz("kilitliyken");   // fırlatırsa test kırmızı
            Iz.TemizCikis();
        }

        if (yedek.Length > 0)
            File.WriteAllText(Iz.Yol, yedek);
    }

    /// <summary>
    /// v22-S4: dış gözlemci — kalp DURUNCA donma.txt yazılmalı (blok sürerken; eski nöbetçi ancak
    /// blok çözülünce yazabiliyordu). Zamana bağlı test: eşik 1 sn, gözlem periyodu 1 sn — 3,5 sn
    /// bekleyiş her makinede yeter; kalp atılan karşıt durum da sınanır.
    /// </summary>
    [Fact]
    public async Task Donma_gozlemcisi_kalp_durunca_iz_birakir()
    {
        string yol = Path.Combine(UygulamaVeriYolu.GunlukKlasoru, "donma.txt");
        File.Delete(yol);

        using (var gozlemci = new Iz.DonmaGozlemcisi(esikMs: 1_000))
        {
            gozlemci.Kalp();
            await Task.Delay(3_500); // kalp atılmıyor → blok sayılır
        }
        Assert.True(File.Exists(yol), "kalp durduğu hâlde donma.txt yazılmadı");
        Assert.Contains("UI", File.ReadAllText(yol));

        File.Delete(yol);
        using (var canli = new Iz.DonmaGozlemcisi(esikMs: 60_000))
        {
            canli.Kalp();
            await Task.Delay(1_500); // eşik çok yüksek — iz DÜŞMEMELİ
        }
        Assert.False(File.Exists(yol), "kalp canlıyken donma izi yazıldı (yanlış pozitif)");
    }
}
