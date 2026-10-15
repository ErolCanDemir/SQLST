using System.Windows.Controls;
using System.Windows.Threading;
using SQLST.App.Views;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 📦 Paket Aktarım penceresi UI testi (v12-S2). GERÇEK pencereyi paylaşımlı STA'da sahte
/// köprülerle açar; motor tarafı zaten AktarimTests'te canlı LocalDB ile kanıtlı — burada
/// yalnız EKRAN akışı doğrulanır:
///  • Hedef profil listesi SQL ailesine süzülür (Mongo v12-S5'e dek görünmez).
///  • Hedef DB listesinde sistem veritabanları yoktur; hedef tablo listesinde view yoktur
///    (hedefe yazılır — view'a INSERT sürprizine kapı açılmaz).
///  • İki tablo seçilince eşleme gridi hedef kolon başına kurulur, aynı adlılar OTOMATİK
///    eşlenir, karşılıksız hedef kolon boş kalır (boş = aktarılmaz).
/// </summary>
public class AktarimPenceresiStaTests
{
    private sealed record Sonuc(
        int HedefProfilSayisi, string? IlkHedefProfilAd,
        IReadOnlyList<string> HedefDbler, IReadOnlyList<string> HedefTablolar,
        IReadOnlyList<(string Hedef, string Kaynak)> Eslesmeler);

    internal sealed class SahteProfilDeposu(IReadOnlyList<ConnectionProfile> profiller) : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(profiller);

        public Task SaveAsync(ConnectionProfile profil, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class SahteSemaServisi : ISchemaService
    {
        public Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VeritabaniBilgisi>>(
                [new("master", true), new("HedefDb", false)]);

        public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
            => Task.FromResult(new SemaOnbellegi
            {
                Nesneler =
                [
                    new("HedefDb", "dbo", "MusteriYedek", SemaNesneTuru.Tablo,
                        [new("Id", "int", false, true), new("Ad", "nvarchar(100)", true, false),
                         new("Soyad", "nvarchar(100)", true, false)], []),
                    new("HedefDb", "dbo", "MusteriView", SemaNesneTuru.View,
                        [new("Id", "int", false, false)], []),
                ],
                YuklenmeZamaniUtc = DateTime.UtcNow,
            });

        public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(
            ConnectionProfile profil, SemaNesnesi tablo, CancellationToken ct)
            => throw new NotSupportedException(); // aktarım ekranı edit metası istemez

        public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(
            ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);

        public Task<IReadOnlyList<Indeks>> IndekslerAsync(
            ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Indeks>>([]);
    }

    /// <summary>Sorgu kipi testi için keşfi sahteleyen motor (canlı DB'siz).</summary>
    internal sealed class SahteAktarim(ILehceSaglayici lehceler) : AktarimServisi(lehceler)
    {
        public string? SonSorgu;

        public override Task<IReadOnlyList<SemaKolonu>> KaynakKolonlariAsync(
            ConnectionProfile profil, string? veritabani, string sql, CancellationToken ct)
        {
            SonSorgu = sql;
            return Task.FromResult<IReadOnlyList<SemaKolonu>>(
                [new("MusteriNo", "int", false, false), new("Ad", "nvarchar(100)", true, false)]);
        }
    }

    [Fact]
    public void Sorgu_kipi_kesif_kolonlariyla_grid_kurar_tablo_kipinden_bagimsiz()
    {
        (int sekmeGecisinde, IReadOnlyList<(string Hedef, string Kaynak)> eslesmeler,
            IReadOnlyList<string> adaylar, string? gidenSorgu) = StaOrtak.Sta().Invoke(() =>
        {
            (AktarimPenceresi w, SahteAktarim motor) = PencereKur();
            // Hedef hazır: profil → DB → tablo (dbo.MusteriYedek: Id, Ad, Soyad)
            ((ComboBox)w.FindName("HedefProfil")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((ComboBox)w.FindName("HedefDb")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((ComboBox)w.FindName("HedefTablo")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            // Sorgu sekmesine geç: keşif YOKKEN grid boşalır (tablo kipi eşlemesi taşınmaz).
            ((TabControl)w.FindName("KaynakKip")).SelectedIndex = 1;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(150));
            int gecisteSatir = w.Satirlar.Count;

            ((TextBox)w.FindName("SorguMetni")).Text = "SELECT MusteriNo, Ad FROM dbo.Musteri WHERE Puan > 5";
            ((Button)w.FindName("KolonlariGetirDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));

            var sonuc = (gecisteSatir,
                (IReadOnlyList<(string, string)>)w.Satirlar.Select(r => (r.HedefKolon, r.KaynakKolon)).ToList(),
                (IReadOnlyList<string>)w.Satirlar[0].KaynakAdaylar, motor.SonSorgu);
            w.Close();
            return sonuc;
        });

        Assert.Equal(0, sekmeGecisinde); // keşifsiz sorgu kipi: grid boş — bayat eşleme kalmaz
        Assert.Equal("SELECT MusteriNo, Ad FROM dbo.Musteri WHERE Puan > 5", gidenSorgu);
        Assert.Equal(3, eslesmeler.Count);
        Assert.Contains(("Ad", "Ad"), eslesmeler);        // sorgu kolonu adıyla otomatik
        Assert.Contains(("Id", ""), eslesmeler);          // sorguda karşılığı yok → boş
        Assert.Contains(("Soyad", ""), eslesmeler);
        Assert.Equal(["", "MusteriNo", "Ad"], adaylar);   // adaylar keşiften gelir, tablodan değil
    }

    [Fact]
    public void Ekle_guncelle_kipinde_anahtar_eslenmeden_aktarim_baslamaz()
    {
        (string eksikAnahtarUyari, string setYokUyari) = StaOrtak.Sta().Invoke(() =>
        {
            (AktarimPenceresi w, _) = PencereKur();
            ((ComboBox)w.FindName("HedefProfil")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((ComboBox)w.FindName("HedefDb")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((ComboBox)w.FindName("KaynakTablo")).SelectedIndex = 0;
            ((ComboBox)w.FindName("HedefTablo")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));

            ((ComboBox)w.FindName("YazmaKipi")).SelectedIndex = 1; // Ekle/Güncelle
            var aktar = (Button)w.FindName("AktarDugmesi");
            var durum = (System.Windows.Controls.TextBlock)w.FindName("Durum");

            // 🔑 Id eşlemesi kaldırılır → MessageBox'a GELMEDEN durum uyarısı.
            w.Satirlar.First(s => s.HedefKolon == "Id").KaynakKolon = "";
            aktar.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string uyari1 = durum.Text;

            // Id eşli ama anahtar dışı eşleme yok → yine engellenir.
            w.Satirlar.First(s => s.HedefKolon == "Id").KaynakKolon = "Id";
            w.Satirlar.First(s => s.HedefKolon == "Ad").KaynakKolon = "";
            aktar.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string uyari2 = durum.Text;

            w.Close();
            return (uyari1, uyari2);
        });

        Assert.Contains("anahtar kolonlar eşlenmeli", eksikAnahtarUyari);
        Assert.Contains("anahtar dışında", setYokUyari);
    }

    /// <summary>
    /// v23-S32 "yeni tabloya aktar": hedef kutusuna listede OLMAYAN ad yazılınca kip yeni tabloya döner — eşleme
    /// gridi gizlenir, kolon önizlemesi kaynaktan birebir gelir (PK notlu), rozet/PK kutusu/Script düğmesi görünür,
    /// düğme "Oluştur ve aktar", temizle/UPSERT kapanır; Script'i göster SELECT INTO + PK üretir (aynı sunucu).
    /// Var olan ad yalnız adla ("MusteriYedek") yazılırsa eşleme kipi kalır ve durum "ZATEN VAR" der.
    /// </summary>
    [Fact]
    public void Yeni_ad_yazinca_kip_yeni_tabloya_doner_var_olan_ad_eslemede_kalir()
    {
        (bool yeniKip, int esleme, IReadOnlyList<(int, string, string, string)> onizleme, string dugme,
            bool temizleAcik, bool pkAcik, string script, bool varOlanKip, string varOlanDurum) = StaOrtak.Sta().Invoke(() =>
        {
            (AktarimPenceresi w, _) = PencereKur();
            ((ComboBox)w.FindName("HedefProfil")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((ComboBox)w.FindName("HedefDb")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(300));
            ((ComboBox)w.FindName("KaynakTablo")).SelectedIndex = 0;
            StaOrtak.Pump(TimeSpan.FromMilliseconds(150));

            var hedefTablo = (ComboBox)w.FindName("HedefTablo");
            hedefTablo.Text = "dbo.Musteri_2025"; // listede yok → yeni
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
            bool yeni = w.YeniTabloMu;
            var onizlemeSatirlari = w.Onizleme.Select(o => (o.Sira, o.Kolon, o.Tip, o.Not)).ToList();
            string dugmeMetni = (string)((Button)w.FindName("AktarDugmesi")).Content;
            bool temizle = ((CheckBox)w.FindName("OnceTemizle")).IsEnabled;
            bool pk = ((CheckBox)w.FindName("PkOlustur")).IsEnabled;
            bool gridGizli = ((DataGrid)w.FindName("EslemeGrid")).Visibility != System.Windows.Visibility.Visible;
            Assert.True(gridGizli);
            Assert.Equal(System.Windows.Visibility.Visible, ((DataGrid)w.FindName("OnizlemeGrid")).Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, ((Button)w.FindName("ScriptGosterDugmesi")).Visibility);

            ((Button)w.FindName("ScriptGosterDugmesi")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string scriptMetni = ((TextBox)w.FindName("Rapor")).Text;

            // Yalnız ad: var olan dbo.MusteriYedek'e çarpar → eşleme kipi + açık uyarı.
            hedefTablo.Text = "MusteriYedek";
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
            bool varOlan = w.YeniTabloMu;
            string durum = ((TextBlock)w.FindName("Durum")).Text;
            int eslemeSayisi = w.Satirlar.Count;

            w.Close();
            return (yeni, eslemeSayisi, (IReadOnlyList<(int, string, string, string)>)onizlemeSatirlari, dugmeMetni,
                temizle, pk, scriptMetni, varOlan, durum);
        });

        Assert.True(yeniKip);
        Assert.Equal([(1, "Id", "int", "🔑 PK (hedefte de PK)"), (2, "Ad", "nvarchar(100)", ""), (3, "Eposta", "nvarchar(200)", "")], onizleme);
        Assert.Equal("📦 Oluştur ve aktar", dugme);
        Assert.False(temizleAcik);
        Assert.True(pkAcik);
        // Kaynak "Canlı" (localdb) ile hedef "Yedek Sunucu" farklı sunucu → CREATE + akışlı script.
        Assert.Contains("CREATE TABLE [dbo].[Musteri_2025]", script);
        Assert.Contains("PRIMARY KEY", script);
        Assert.Contains("SELECT [Id], [Ad], [Eposta] FROM [dbo].[Musteri]", script);

        Assert.False(varOlanKip);
        Assert.Contains("ZATEN VAR", varOlanDurum);
        Assert.Equal(3, esleme); // dbo.MusteriYedek'in 3 kolonu eşleme gridinde
    }

    /// <summary>"💾 Tabloya kaydet" yolu (baslangicSql): Sorgu kipi + SQL dolu + kolonlar keşfedilmiş, hedef = kaynak
    /// profil (listede yoksa eklenir) + kaynak DB seçili; sorgu kipinde PK kutusu kapalı (PK bilinemez).</summary>
    [Fact]
    public void Baslangic_sql_ile_sorgu_kipinde_hedef_ayni_baglanti_hazir_acilir()
    {
        (int kip, string sorgu, string? hedefProfilAd, string? hedefDb, string? gidenSorgu, bool yeni, bool pkAcik, int onizleme) =
            StaOrtak.Sta().Invoke(() =>
        {
            StaOrtak.Birlestir("PaletAcik.xaml");
            StaOrtak.Birlestir("Tema.xaml");
            var kaynakProfil = new ConnectionProfile { Ad = "Canlı", Sunucu = @"(localdb)\MSSQLLocalDB" };
            var lehceler = new LehceSaglayici(new DpapiSecretProtector());
            var motor = new SahteAktarim(lehceler);
            var w = new AktarimPenceresi(
                kaynakProfil, ["HedefDb", "DigerDb"], "HedefDb",
                _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>([]),
                new SahteProfilDeposu([new ConnectionProfile { Ad = "Yedek Sunucu", Motor = MotorTuru.Mssql }]),
                new SahteSemaServisi(), lehceler, motor,
                baslangicSql: "SELECT MusteriNo, Ad FROM dbo.Musteri WHERE Puan > 5")
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            StaOrtak.Pump(TimeSpan.FromMilliseconds(700)); // Loaded → sıralı başlangıç kurulumu

            var hedefTablo = (ComboBox)w.FindName("HedefTablo");
            hedefTablo.Text = "dbo.Rapor_2025";
            StaOrtak.Pump(TimeSpan.FromMilliseconds(200));
            var sonuc = (
                ((TabControl)w.FindName("KaynakKip")).SelectedIndex,
                ((TextBox)w.FindName("SorguMetni")).Text,
                (((ComboBox)w.FindName("HedefProfil")).SelectedItem as ConnectionProfile)?.Ad,
                ((ComboBox)w.FindName("HedefDb")).SelectedItem as string,
                motor.SonSorgu, w.YeniTabloMu, ((CheckBox)w.FindName("PkOlustur")).IsEnabled, w.Onizleme.Count);
            w.Close();
            return sonuc;
        });

        Assert.Equal(1, kip);
        Assert.Equal("SELECT MusteriNo, Ad FROM dbo.Musteri WHERE Puan > 5", sorgu);
        Assert.Equal(sorgu, gidenSorgu);          // keşif otomatik yapıldı
        Assert.Equal("Canlı", hedefProfilAd);     // kayıtsız aktif profil listeye eklendi ve seçildi
        Assert.Equal("HedefDb", hedefDb);
        Assert.True(yeni);
        Assert.False(pkAcik);                      // sorgu sonucunda PK bilinemez
        Assert.Equal(2, onizleme);                 // MusteriNo, Ad
    }

    /// <summary>Ortak pencere kurulumu: sahte köprüler + sahte keşif motoru.</summary>
    private static (AktarimPenceresi, SahteAktarim) PencereKur()
    {
        StaOrtak.Birlestir("PaletAcik.xaml");
        StaOrtak.Birlestir("Tema.xaml");

        var kaynakProfil = new ConnectionProfile { Ad = "Canlı", Sunucu = "(localdb)\\MSSQLLocalDB" };
        var kaynakTablolar = new List<SemaNesnesi>
        {
            new("KaynakDb", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new("Id", "int", false, true), new("Ad", "nvarchar(100)", true, false),
                 new("Eposta", "nvarchar(200)", true, false)], []),
        };
        var lehceler = new LehceSaglayici(new DpapiSecretProtector());
        var motor = new SahteAktarim(lehceler);
        var w = new AktarimPenceresi(
            kaynakProfil, ["KaynakDb", "DigerDb"], "KaynakDb",
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(kaynakTablolar),
            new SahteProfilDeposu([new ConnectionProfile { Ad = "Yedek Sunucu", Motor = MotorTuru.Mssql }]),
            new SahteSemaServisi(), lehceler, motor)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w.Show();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(400)); // Loaded → kaynak tablolar + profiller
        return (w, motor);
    }

    [Fact]
    public void Ekran_akisi_profil_suzme_sistem_db_suzme_ve_otomatik_esleme()
    {
        Sonuc s = StaOrtak.Sta().Invoke(Kosu);

        // Mongo profili listeye girmez (v12-S5'e dek); v23-S32: kayıtsız KAYNAK profil listenin SONUNA eklenir
        // (aynı sunucuya "Tabloya kaydet" için) → 2 profil, ilk sıra değişmez.
        Assert.Equal(2, s.HedefProfilSayisi);
        Assert.Equal("Yedek Sunucu", s.IlkHedefProfilAd);
        // Sistem DB'leri ve view'lar hedef aday değildir.
        Assert.Equal(["HedefDb"], s.HedefDbler);
        Assert.Equal(["dbo.MusteriYedek"], s.HedefTablolar);
        // Grid hedef kolon başına satır: Id/Ad aynı adla otomatik, Soyad kaynakta yok → boş.
        Assert.Equal(3, s.Eslesmeler.Count);
        Assert.Contains(("Id", "Id"), s.Eslesmeler);
        Assert.Contains(("Ad", "Ad"), s.Eslesmeler);
        Assert.Contains(("Soyad", ""), s.Eslesmeler);
    }

    private static Sonuc Kosu()
    {
        StaOrtak.Birlestir("PaletAcik.xaml");
        StaOrtak.Birlestir("Tema.xaml");

        var kaynakProfil = new ConnectionProfile { Ad = "Canlı", Sunucu = "(localdb)\\MSSQLLocalDB" };
        var kaynakTablolar = new List<SemaNesnesi>
        {
            new("KaynakDb", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new("Id", "int", false, true), new("Ad", "nvarchar(100)", true, false),
                 new("Eposta", "nvarchar(200)", true, false)], []),
        };
        var koruyucu = new DpapiSecretProtector();
        var lehceler = new LehceSaglayici(koruyucu);

        var w = new AktarimPenceresi(
            kaynakProfil, ["KaynakDb", "DigerDb"], "KaynakDb",
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(kaynakTablolar),
            new SahteProfilDeposu(
            [
                new ConnectionProfile { Ad = "Yedek Sunucu", Motor = MotorTuru.Mssql },
                new ConnectionProfile { Ad = "Mongo Arşiv", Motor = MotorTuru.Mongo },
            ]),
            new SahteSemaServisi(), lehceler, new AktarimServisi(lehceler))
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w.Show();
        StaOrtak.Pump(TimeSpan.FromMilliseconds(400)); // Loaded → kaynak tablolar + profiller

        var hedefProfil = (ComboBox)w.FindName("HedefProfil");
        int profilSayisi = hedefProfil.Items.Count;
        string? ilkAd = (hedefProfil.Items[0] as ConnectionProfile)?.Ad;

        hedefProfil.SelectedIndex = 0;
        StaOrtak.Pump(TimeSpan.FromMilliseconds(300)); // → hedef DB listesi

        var hedefDb = (ComboBox)w.FindName("HedefDb");
        var dbler = hedefDb.Items.Cast<string>().ToList();
        hedefDb.SelectedIndex = 0;
        StaOrtak.Pump(TimeSpan.FromMilliseconds(300)); // → hedef tablolar

        var hedefTablo = (ComboBox)w.FindName("HedefTablo");
        var tablolar = hedefTablo.Items.Cast<string>().ToList();

        ((ComboBox)w.FindName("KaynakTablo")).SelectedIndex = 0;
        hedefTablo.SelectedIndex = 0;
        StaOrtak.Pump(TimeSpan.FromMilliseconds(200)); // → eşleme gridi

        var eslesmeler = w.Satirlar.Select(r => (r.HedefKolon, r.KaynakKolon)).ToList();
        w.Close();
        return new Sonuc(profilSayisi, ilkAd, dbler, tablolar,
            eslesmeler.Select(e => (e.HedefKolon, e.KaynakKolon)).ToList());
    }
}
