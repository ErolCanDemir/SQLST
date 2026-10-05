using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SQLST.App.Views;
using SQLST.Application;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S1 saha turu-2 m.11 — "ObjectId izi ekranında ⏹ Durdur'a bastığımda hemen durması lazım".
/// Kök neden v20-S21 m.2/m.15b ile aynı aile: Durdur yalnız iptal İSTEĞİ gönderiyordu; Mongo sayımı
/// sunucuda sürdüğü için await geri dönene dek (komut tavanı 60 sn) döngü parkta kalıyor, ekranda
/// "Durduruluyor…" asılı duruyordu. Üstelik iptal jetonu sorguya HİÇ geçmiyordu (CancellationToken.None).
/// Bu sınıf düzeltmeyi sabitler: Durdur ANINDA sonlandırır ve geciken sayım ekranı EZEMEZ.
/// </summary>
public class MongoIdIziDurdurTests
{
    private static readonly bool Etkin = OperatingSystem.IsWindows();

    /// <summary>Adayları ANINDA veren envanter köprüsü (m.3 sonrası pencere adayları kendisi üretir).</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<IzAdayi>?>> Hazir(params IzAdayi[] adaylar)
        => _ => Task.FromResult<IReadOnlyList<IzAdayi>?>(adaylar);

    [Fact]
    public void Durdur_aninda_sonlandirir_ve_geciken_sayim_ekrani_ezmez()
    {
        if (!Etkin)
            return;

        // Hiç dönmeyen sayım: gerçek dünyada büyük koleksiyonda dakikalarca süren countDocuments.
        var kilit = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoIdIziPenceresi(
                "d8348a7b-9ee3-4442-8bc2-ebacc7b2336b",
                Hazir(new IzAdayi("QueryLog", "istekId"), new IzAdayi("AuditLog", "kayitId")),
                (_, _) => kilit.Task,
                (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 900, Height = 560,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            Assert.StartsWith("Taranıyor…", w.Ilerleme.Text); // tarama ilk adayda asılı
            Assert.True(w.DurdurDugmesi.IsEnabled);

            w.DurdurDugmesi.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            // ANINDA: sunucudan yanıt BEKLENMEZ ("Durduruluyor…" ara durumu artık yok).
            Assert.Equal("Durduruldu.", w.Ilerleme.Text);
            Assert.False(w.DurdurDugmesi.IsEnabled);
            Assert.Contains("aday alan tarandı", w.Ozet.Text);

            // Geciken sayım ŞİMDİ dönüyor (eşleşme de bulmuş) — bayat nesil: ekrana yazamaz.
            kilit.SetResult(42);
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            Assert.Empty(w.Sonuclar.Items);                  // satır eklenmedi
            Assert.Equal("Durduruldu.", w.Ilerleme.Text);    // durum ezilmedi
            Assert.False(w.DurdurDugmesi.IsEnabled);

            w.Close();
        });
    }

    // ⚡ v22-S1 m.12 (kullanıcı: "object izi ekranı büyük veritabanında çok yavaş"). ÖLÇÜM (yerel
    // MongoDB, 600.000 belge): sayım index'li alanda 2 ms, index'siz alanda 493 ms (250×); 8 aday
    // SIRAYLA 4.260 ms ↔ PARALEL 654 ms (6,5×). Denenip ELENEN iki fikir de ölçümle elendi:
    // find+limit1 varlık probu $count'tan YAVAŞ (1.555 ms ↔ 493 ms) ve tek $facet ile 5 alanı bir
    // taramada saymak 5 ayrı $count'tan YAVAŞ (6.697 ms ↔ 2.585 ms; $facet index de kullanamaz).
    // Bu testler kalan iki kazancı sabitler: eşzamanlılık ve index'li adayların öne alınması.

    [Fact]
    public void Adaylar_esZamanli_gruplar_halinde_taranir()
    {
        if (!Etkin)
            return;

        int anlikEnYuksek = 0, anlik = 0;
        var kilit = new object();
        var bekleyenler = new List<TaskCompletionSource<long?>>();
        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            IzAdayi[] adaylar = [.. Enumerable.Range(0, 12).Select(i => new IzAdayi($"Kol{i}", "alan"))];
            var w = new MongoIdIziPenceresi("abc", Hazir(adaylar),
                (_, _) =>
                {
                    var tcs = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (kilit)
                    {
                        bekleyenler.Add(tcs);
                        anlik++;
                        anlikEnYuksek = Math.Max(anlikEnYuksek, anlik);
                    }
                    return tcs.Task;
                },
                (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 900, Height = 560,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            // Aynı anda BİRDEN ÇOK sayım uçuşta olmalı (eskiden tam olarak 1'di).
            Assert.True(anlikEnYuksek > 1, $"tarama hâlâ sıralı görünüyor (en yüksek eşzamanlı: {anlikEnYuksek})");
            Assert.Equal(6, anlikEnYuksek); // grup boyu

            foreach (TaskCompletionSource<long?> t in bekleyenler.ToArray())
                t.TrySetResult(0);
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            w.Close();
        });
    }

    [Fact]
    public void Indexli_adaylar_once_taranir()
    {
        if (!Etkin)
            return;

        var sira = new List<string>();
        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            // Kol1/Kol3 index'li ("alan" bir index'in ilk alanı), Kol0/Kol2 değil.
            IzAdayi[] adaylar =
            [
                new("Kol0", "alan"), new("Kol1", "alan"), new("Kol2", "alan"), new("Kol3", "alan"),
            ];
            var w = new MongoIdIziPenceresi("abc", Hazir(adaylar),
                (sorgu, _) =>
                {
                    sira.Add(sorgu.Contains("Kol0") ? "Kol0"
                        : sorgu.Contains("Kol1") ? "Kol1"
                        : sorgu.Contains("Kol2") ? "Kol2" : "Kol3");
                    return Task.FromResult<long?>(0);
                },
                (_, _) => { },
                (koleksiyon, _) => Task.FromResult<IReadOnlyList<string>>(
                    koleksiyon is "Kol1" or "Kol3" ? ["alan"] : []))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 900, Height = 560,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(600));

            Assert.Equal(4, sira.Count);
            Assert.Equal(["Kol1", "Kol3"], sira.Take(2).Order().ToArray()); // index'liler ÖNCE
            w.Close();
        });
    }

    [Fact]
    public void Aday_yoksa_durdur_dugmesi_kapali_ve_aciklama_yazar()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoIdIziPenceresi("abc", Hazir(), (_, _) => Task.FromResult<long?>(null), (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 900, Height = 560,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

            Assert.False(w.DurdurDugmesi.IsEnabled);
            Assert.Contains("uyan alan bulunamadı", w.Ozet.Text);
            w.Close();
        });
    }

    // ---- v22-S3 saha turu-3 m.3 (kullanıcı: "sağ tıklayınca açılmıyor… sonradan açıldı çok geç;
    // ekranı açıp aramayı öyle yapması lazım") ----
    // KÖK NEDEN: koleksiyon envanteri (şema yükü) pencere AÇILMADAN ÖNCE, MainWindow'da bekleniyordu.
    // Büyük veritabanında saniyelerce ekranda hiçbir şey olmuyor → özellik bozuk sanılıyor. Üstelik
    // o bekleyiş CancellationToken.None ile yapıldığı için kesilemiyordu.

    [Fact]
    public void Envanter_beklenirken_pencere_ZATEN_ekranda_ve_ne_yaptigini_yazar()
    {
        if (!Etkin)
            return;

        // Hiç dönmeyen envanter: gerçek dünyada yüzlerce koleksiyonlu veritabanının şema yükü.
        var envanter = new TaskCompletionSource<IReadOnlyList<IzAdayi>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoIdIziPenceresi("abc", _ => envanter.Task,
                (_, _) => Task.FromResult<long?>(0), (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 900, Height = 560,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            // Pencere GÖRÜNÜR ve ne beklediğini söyler — envanter hâlâ uçuşta.
            Assert.True(w.IsVisible);
            Assert.Contains("envanter", w.Ilerleme.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(w.DurdurDugmesi.IsEnabled); // bu evre de kesilebilir

            // Envanter ŞİMDİ dönüyor → tarama normal akışına geçer.
            envanter.SetResult([new IzAdayi("Kol", "alan")]);
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));
            Assert.Equal("Tarama bitti.", w.Ilerleme.Text);

            w.Close();
        });
    }

    [Fact]
    public void Envanter_beklenirken_durdur_aninda_keser_ve_gec_gelen_envanter_tarama_baslatmaz()
    {
        if (!Etkin)
            return;

        var envanter = new TaskCompletionSource<IReadOnlyList<IzAdayi>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int sayimCagrisi = 0;
        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoIdIziPenceresi("abc", _ => envanter.Task,
                (_, _) => { sayimCagrisi++; return Task.FromResult<long?>(1); },
                (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 900, Height = 560,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

            w.DurdurDugmesi.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("Durduruldu.", w.Ilerleme.Text); // ANINDA — envanter yanıtı beklenmez

            // Geciken envanter ŞİMDİ dönüyor: bayat nesil → ne tarama başlar ne ekran ezilir.
            envanter.SetResult([new IzAdayi("Kol", "alan")]);
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            Assert.Equal(0, sayimCagrisi);
            Assert.Equal("Durduruldu.", w.Ilerleme.Text);
            Assert.Empty(w.Sonuclar.Items);
            w.Close();
        });
    }

    [Fact]
    public void Envanter_okunamazsa_neden_ekranda_yazar()
    {
        if (!Etkin)
            return;

        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            // null = envanter okunamadı (eskiden yalnız ana penceredeki durum çubuğuna yazılıyordu)
            var w = new MongoIdIziPenceresi("abc", _ => Task.FromResult<IReadOnlyList<IzAdayi>?>(null),
                (_, _) => Task.FromResult<long?>(0), (_, _) => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, Width = 900, Height = 560,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

            Assert.Contains("envanteri okunamadı", w.Ozet.Text);
            Assert.False(w.DurdurDugmesi.IsEnabled);
            w.Close();
        });
    }
}
