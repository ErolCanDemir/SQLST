using System.Windows;
using System.Windows.Threading;
using SQLST.App.Views;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S3 saha turu-3 m.5 — kullanıcı: "Find ekranında tabloyu seçince sort, filter gibi alanlara
/// giriş yaparken koleksiyon ALANLARINI da getirsin. Şu an alanları değil alanlardan SONRASINI
/// getiriyor."
///
/// KÖK NEDEN: Mongo'da koleksiyon LİSTESİ bilerek ALANSIZ çekilir (MainViewModel.LogTabloNesneleriAsync
/// — koleksiyon başı $sample süpürmesi listeyi çok yavaşlatıyordu). Find yardımcısı da alan sözlüğünü
/// yalnız o listeden kuruyordu → sözlük boş → tamamlama anahtar konumunda hiç öneri üretemiyor,
/// geriye yalnız DEĞER konumundaki öneriler kalıyordu: Sort'ta 1/-1, Project'te 1/0, Filter'da
/// $operatörler. Kullanıcının "alanlardan sonrası" dediği tam olarak bu.
/// </summary>
public class MongoBulAlanOnerisiTests
{
    private static readonly bool Etkin = OperatingSystem.IsWindows();

    /// <summary>Mongo'nun GERÇEK hâli: koleksiyon adları var, Kolonlar BOŞ.</summary>
    private static Task<IReadOnlyList<SemaNesnesi>> AlansizListe()
        => Task.FromResult<IReadOnlyList<SemaNesnesi>>(
        [
            new SemaNesnesi("log", "", "oturumlar", SemaNesneTuru.Koleksiyon, [], []),
            new SemaNesnesi("log", "", "istekler", SemaNesneTuru.Koleksiyon, [], []),
        ]);

    private static void Pencerede(
        Func<string, Task<IReadOnlyList<string>>>? alanlariGetir,
        Action<MongoBulPenceresi> denetle)
    {
        Dispatcher d = StaOrtak.Sta();
        d.Invoke(() =>
        {
            StaOrtak.Birlestir("PaletKoyu.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var w = new MongoBulPenceresi((_, _) => { }, null, AlansizListe, alanlariGetir)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(250)); // Loaded → koleksiyon listesi
            denetle(w);
            w.Close();
        });
    }

    [Fact]
    public void Koleksiyon_secilince_alanlar_on_demand_getirilir()
    {
        if (!Etkin)
            return;

        int cagri = 0;
        Pencerede(
            k => { cagri++; return Task.FromResult<IReadOnlyList<string>>(["_id", "kullaniciId", "ts"]); },
            w =>
            {
                Assert.Empty(w.SeciliAlanlar()); // koleksiyon seçilmeden alan yok

                w.KoleksiyonKutusu.Text = "oturumlar";
                StaOrtak.Pump(TimeSpan.FromMilliseconds(250));

                // ESKİDEN burası BOŞ kalıyordu → Sort/Filter'da alan önerisi hiç çıkmıyordu.
                Assert.Equal(["_id", "kullaniciId", "ts"], w.SeciliAlanlar());
                Assert.Equal("3 alan", w.AlanNotu.Text);
                Assert.Equal(1, cagri);

                // İkinci kez aynı koleksiyon: sözlükten gelir, sunucuya TEKRAR gidilmez.
                w.KoleksiyonKutusu.Text = "oturumla";
                w.KoleksiyonKutusu.Text = "oturumlar";
                StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
                Assert.Equal(1, cagri);
            });
    }

    /// <summary>Alanlar gelince tamamlama ANAHTAR konumunda gerçekten alan önerir (asıl şikâyet).</summary>
    [Fact]
    public void Alanlar_gelince_sort_ve_filter_anahtar_konumunda_alan_onerir()
    {
        if (!Etkin)
            return;

        Pencerede(
            _ => Task.FromResult<IReadOnlyList<string>>(["_id", "kullaniciId", "ts"]),
            w =>
            {
                w.KoleksiyonKutusu.Text = "oturumlar";
                StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
                IReadOnlyList<string> alanlar = w.SeciliAlanlar();

                // Sort: boş kutuda imleç anahtar konumundadır → ALAN adları gelmeli (1/-1 değil)
                MongoTamamlamaSonucu? sort = MongoBulTamamlama.Oner(MongoKutu.Sort, "{ \"", 3, alanlar);
                Assert.NotNull(sort);
                Assert.Equal(["_id", "kullaniciId", "ts"], sort!.Oneriler.Select(o => o.Goster));

                // Filter: "ku" öneki alanı süzer
                MongoTamamlamaSonucu? filter = MongoBulTamamlama.Oner(MongoKutu.Filter, "{ \"ku", 5, alanlar);
                Assert.NotNull(filter);
                Assert.Equal(["kullaniciId"], filter!.Oneriler.Select(o => o.Goster));

                // ESKİ DAVRANIŞ (alan sözlüğü boşken): anahtar konumunda öneri YOK, yalnız değer
                // konumundaki 1/-1 kalıyordu — "alanları değil alanlardan sonrasını getiriyor".
                Assert.Null(MongoBulTamamlama.Oner(MongoKutu.Sort, "{ \"", 3, []));
                Assert.Equal(["1", "-1"],
                    MongoBulTamamlama.Oner(MongoKutu.Sort, "{ \"ts\": ", 8, [])!.Oneriler.Select(o => o.Goster));
            });
    }

    [Fact]
    public void Alan_okunamazsa_ekran_calisir_ve_neden_yazar()
    {
        if (!Etkin)
            return;

        Pencerede(
            _ => throw new InvalidOperationException("bağlantı koptu"),
            w =>
            {
                w.KoleksiyonKutusu.Text = "oturumlar";
                StaOrtak.Pump(TimeSpan.FromMilliseconds(250));

                Assert.Empty(w.SeciliAlanlar());
                Assert.Contains("okunamadı", w.AlanNotu.Text);
                Assert.True(w.IsVisible); // öneri bir kolaylıktır — pencere düşmez
            });
    }

    /// <summary>Adı tam yazılmamışken sunucuya gidilmez (her tuşta sorgu atılmasın).</summary>
    [Fact]
    public void Yarim_yazilan_adda_sunucuya_gidilmez()
    {
        if (!Etkin)
            return;

        int cagri = 0;
        Pencerede(
            _ => { cagri++; return Task.FromResult<IReadOnlyList<string>>(["a"]); },
            w =>
            {
                foreach (string parca in new[] { "o", "ot", "otu", "oturum" })
                {
                    w.KoleksiyonKutusu.Text = parca;
                    StaOrtak.Pump(TimeSpan.FromMilliseconds(80));
                }
                Assert.Equal(0, cagri);

                w.KoleksiyonKutusu.Text = "oturumlar"; // tam ad
                StaOrtak.Pump(TimeSpan.FromMilliseconds(250));
                Assert.Equal(1, cagri);
            });
    }
}
