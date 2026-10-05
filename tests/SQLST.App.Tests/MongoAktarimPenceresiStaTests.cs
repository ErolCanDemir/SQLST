using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SQLST.App.Views;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// Mongo Paket Aktarım penceresi UI testi (v12-S5). Motor canlı MongoAktarimTests'te kanıtlı —
/// burada yalnız EKRAN akışı: hedef profil listesi YALNIZ Mongo ailesi; koleksiyon adları
/// ÇIPLAK (TamAd "db.koleksiyon" değil — LogAnaliz dersi); hedef DB/koleksiyon YAZILABİLİR
/// (yeni ad = Mongo örtük oluşturur); eksik seçimde Aktar MessageBox'a GELMEDEN uyarır.
/// </summary>
public class MongoAktarimPenceresiStaTests
{
    private sealed class SahteMongoSemaServisi : ISchemaService
    {
        public Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VeritabaniBilgisi>>(
                [new("admin", true), new("LogArsiv", false)]);

        public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
            => Task.FromResult(new SemaOnbellegi
            {
                Nesneler =
                [
                    new(veritabani!, veritabani!, "EskiLoglar", SemaNesneTuru.Koleksiyon,
                        [new("_id", "objectId", false, true), new("Aciklama", "string", true, false)], []),
                ],
                YuklenmeZamaniUtc = DateTime.UtcNow,
            });

        public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(
            ConnectionProfile profil, SemaNesnesi tablo, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(
            ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);
    }

    [Fact]
    public void Mongo_ekrani_profil_suzme_ciplak_koleksiyon_adi_ve_yazilabilir_hedef()
    {
        (int profilSayisi, string? ilkProfilAd, IReadOnlyList<string> koleksiyonlar,
            IReadOnlyList<string> hedefDbler, string eksikUyari, string hedefDbYazilan,
            IReadOnlyList<(string Kaynak, string Hedef)> gridSatirlar) =
            StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");

            var kaynakProfil = new ConnectionProfile
                { Ad = "Mongo Canlı", Sunucu = "localhost:27017", Motor = MotorTuru.Mongo };
            var koleksiyonNesneleri = new List<SemaNesnesi>
            {
                new("LogDb", "LogDb", "ExceptionLog", SemaNesneTuru.Koleksiyon,
                    [new("_id", "objectId", false, true), new("Mesaj", "string", true, false),
                     new("Zaman", "date", true, false)], []),
                new("LogDb", "LogDb", "IslemLog", SemaNesneTuru.Koleksiyon, [], []),
            };

            var w = new MongoAktarimPenceresi(
                kaynakProfil, ["LogDb", "AppDb"], "LogDb",
                _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(koleksiyonNesneleri),
                new AktarimPenceresiStaTests.SahteProfilDeposu(
                [
                    new ConnectionProfile { Ad = "SQL Yedek", Motor = MotorTuru.Mssql },
                    new ConnectionProfile { Ad = "Mongo Arşiv", Motor = MotorTuru.Mongo },
                ]),
                new SahteMongoSemaServisi(), new MongoAktarimServisi(new DpapiSecretProtector()))
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(400));

            var hedefProfil = (ComboBox)w.FindName("HedefProfil");
            var kaynakKoleksiyon = (ComboBox)w.FindName("KaynakKoleksiyon");
            int sayi = hedefProfil.Items.Count;
            string? ilkAd = (hedefProfil.Items[0] as ConnectionProfile)?.Ad;
            var kolListe = kaynakKoleksiyon.Items.Cast<string>().ToList();

            hedefProfil.SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            var hedefDb = (ComboBox)w.FindName("HedefDb");
            var dbListe = hedefDb.Items.Cast<string>().ToList();

            // Yazılabilir hedef: listede OLMAYAN yeni adlar yazılır (arşiv DB'si henüz yok senaryosu).
            hedefDb.Text = "YeniArsivDb";
            ((ComboBox)w.FindName("HedefKoleksiyon")).Text = "LogYedek";

            // Kaynak koleksiyon HENÜZ seçilmedi → Aktar MessageBox'a gelmeden uyarır.
            ((Button)w.FindName("AktarDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            string uyari = ((TextBlock)w.FindName("Durum")).Text;
            string yazilan = hedefDb.Text;

            // v12-S6: kaynak koleksiyon seçilince eşleme gridi envanterle kurulur, varsayılan birebir.
            kaynakKoleksiyon.SelectedIndex = 0; // ExceptionLog (3 alanlı)
            StaOrtak.Pump(TimeSpan.FromMilliseconds(150));
            var gridler = w.Satirlar.Select(s => (s.KaynakAlan, s.HedefAlan)).ToList();

            w.Close();
            return (sayi, ilkAd, (IReadOnlyList<string>)kolListe, (IReadOnlyList<string>)dbListe,
                uyari, yazilan, (IReadOnlyList<(string, string)>)gridler);
        });

        Assert.Equal(1, profilSayisi);              // SQL profili listeye girmez
        Assert.Equal("Mongo Arşiv", ilkProfilAd);
        Assert.Equal(["ExceptionLog", "IslemLog"], koleksiyonlar); // çıplak ad — "LogDb.X" değil
        Assert.Equal(["LogArsiv"], hedefDbler);     // sistem DB'leri (admin) süzülür
        Assert.Contains("Kaynak koleksiyonu seçin", eksikUyari);
        Assert.Equal("YeniArsivDb", hedefDbYazilan); // yazılan yeni ad korunur
        // Grid $sample envanterinden kuruldu; varsayılan birebir (dokunulmazsa belge olduğu gibi).
        Assert.Equal([("_id", "_id"), ("Mesaj", "Mesaj"), ("Zaman", "Zaman")], gridSatirlar);
    }
}
