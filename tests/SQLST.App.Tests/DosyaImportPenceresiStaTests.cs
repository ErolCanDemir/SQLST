using System.Data;
using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SQLST.App.Views;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// 📥 Excel/TXT İçe Aktar penceresi UI testi (v13-S2/S3). Gerçek TXT dosyasıyla (diyalog yerine
/// DosyaSec köprüsü) doğrular: TXT seçiminde kolon+satır ayracı görünür · Önizle grid'i
/// tip etiketli başlıklarla kurar · başlıksız kip Kolon1..N · hedef tablo seçilince eşleme gridi
/// aynı adları OTOMATİK eşler (🔑 işaretli) ve Aktar açılır · Ekle/Güncelle'de anahtar eşlenmeden
/// aktarım MessageBox'a GELMEDEN engellenir.
/// </summary>
public class DosyaImportPenceresiStaTests
{
    private sealed record Sonuc(
        bool AyracGorunur, string IlkBaslik, string TutarBaslik,
        int SatirSayisi, string Durum, string BasliksizIlkKolon,
        IReadOnlyList<(string Hedef, string Kaynak)> Eslesmeler, bool AktarAcik, string UpsertUyari,
        IReadOnlyList<(string Hedef, string Tip, string Kaynak)> YeniKipSatirlar);

    [Fact]
    public void Txt_onizleme_esleme_ve_upsert_dogrulamasi()
    {
        string yol = Path.Combine(Path.GetTempPath(), $"sqlst-imp-{Guid.NewGuid():N}.txt");
        File.WriteAllText(yol,
            "Ad;Tutar;Tarih\r\nAli;1.250,75;24.07.2026\r\nAyşe;3,10;01.01.2025\r\n",
            new UTF8Encoding(false));

        try
        {
            Sonuc s = StaOrtak.Sta().Invoke(() => Kosu(yol));

            Assert.True(s.AyracGorunur);   // TXT: ayraç seçimi görünür
            Assert.StartsWith("Ad", s.IlkBaslik, StringComparison.Ordinal);
            Assert.Contains("metin", s.IlkBaslik);        // başlıkta tip etiketi
            Assert.Contains("ondalık", s.TutarBaslik);    // "1.250,75" + "3,10" → ondalık (TR)
            Assert.Equal(2, s.SatirSayisi);
            Assert.StartsWith("Kolon1", s.BasliksizIlkKolon, StringComparison.Ordinal);
            // Eşleme: hedef kolon başına satır; Ad/Tutar aynı adla otomatik, Id dosyada yok → boş.
            Assert.Equal(3, s.Eslesmeler.Count);
            Assert.Contains(("Id", ""), s.Eslesmeler);
            Assert.Contains(("Ad", "Ad"), s.Eslesmeler);
            Assert.Contains(("Tutar", "Tutar"), s.Eslesmeler);
            Assert.True(s.AktarAcik);
            Assert.Contains("anahtar kolonlar eşlenmeli", s.UpsertUyari); // 🔑 Id eşlenmeden UPSERT olmaz
            // Yeni tablo kipi (S4): dosya kolonları birebir + önerilen SQL tipleri.
            Assert.Equal(3, s.YeniKipSatirlar.Count);
            Assert.Contains(("Ad", "nvarchar(50)", "Ad"), s.YeniKipSatirlar);        // metin(4) → 50
            Assert.Contains(("Tutar", "decimal(18,4)", "Tutar"), s.YeniKipSatirlar);
            Assert.Contains(("Tarih", "datetime2", "Tarih"), s.YeniKipSatirlar);
        }
        finally
        {
            File.Delete(yol);
        }
    }

    private static Sonuc Kosu(string yol)
    {
        StaOrtak.Birlestir("PaletAcik.xaml");
        StaOrtak.Birlestir("Tema.xaml");

        var profil = new ConnectionProfile { Ad = "Canlı", Sunucu = "(localdb)\\MSSQLLocalDB" };
        var tablolar = new List<SemaNesnesi>
        {
            new("AppDb", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new("Id", "int", false, true), new("Ad", "nvarchar(50)", true, false),
                 new("Tutar", "decimal(10,2)", true, false)], []),
        };
        var lehceler = new LehceSaglayici(new DpapiSecretProtector());

        var w = new DosyaImportPenceresi(
            profil, ["AppDb"], "AppDb",
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(tablolar),
            lehceler, new DosyaAktarimServisi(lehceler),
            new FarkOkumaServisi(lehceler), (_, _, _) => { })
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w.Show();
        // ctor'daki hedef DB seçimi tabloları ASENKRON yükler → sabit süre yerine koşula bak
        // (24 Ağu 2026 kararsızlık düzeltmesi; gerekçe StaOrtak.PumpUntil belgesinde).
        var hedefTabloKutusu = (ComboBox)w.FindName("HedefTablo");
        StaOrtak.PumpUntil(() => hedefTabloKutusu.Items.Count > 0);

        w.DosyaSec(yol);
        // Tür kuralları (2026-07-26): .txt → TXT ön-seçilir; kolon VE satır ayracı görünür,
        // sayfa gizli. CSV'ye çevirince ayraç alanları da gizlenir (otomatik algı).
        var tur = (ComboBox)w.FindName("Tur");
        var ayrac = (ComboBox)w.FindName("Ayrac");
        var satirAyrac = (ComboBox)w.FindName("SatirAyrac");
        bool turTxt = tur.SelectedIndex == 2;
        bool ayracGorunur = ayrac.Visibility == System.Windows.Visibility.Visible
            && satirAyrac.Visibility == System.Windows.Visibility.Visible && turTxt;

        tur.SelectedIndex = 1; // CSV
        bool csvAyracGizli = ayrac.Visibility == System.Windows.Visibility.Collapsed
            && satirAyrac.Visibility == System.Windows.Visibility.Collapsed;
        tur.SelectedIndex = 2; // TXT'ye dön — testin kalanı TXT akışı
        if (!csvAyracGizli)
            throw new Xunit.Sdk.XunitException("CSV türünde ayraç alanları gizlenmeliydi.");

        var onizle = (Button)w.FindName("OnizleDugmesi");
        var grid = (DataGrid)w.FindName("OnizlemeGrid");
        onizle.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
        // Ön izleme Task.Run içinde okur, sonra grid'i kurar → "kolonlar kuruldu" sinyalini bekle
        StaOrtak.PumpUntil(() => grid.Columns.Count > 1 && grid.ItemsSource is not null);

        string ilkBaslik = grid.Columns[0].Header?.ToString() ?? "";
        string tutarBaslik = grid.Columns[1].Header?.ToString() ?? "";
        int satirSayisi = ((DataView)grid.ItemsSource!).Count;

        // Hedef tablo → eşleme gridi (v13-S3)
        hedefTabloKutusu.SelectedIndex = 0;
        StaOrtak.PumpUntil(() => w.Satirlar.Count > 0); // eşleme gridi kuruldu
        var eslesmeler = w.Satirlar.Select(r => (r.HedefKolon, r.KaynakKolon)).ToList();
        var aktar = (Button)w.FindName("AktarDugmesi");
        bool aktarAcik = aktar.IsEnabled;
        string durum = ((TextBlock)w.FindName("Durum")).Text;

        // Ekle/Güncelle + 🔑 Id eşlenmemiş → MessageBox'a gelmeden uyarı.
        ((ComboBox)w.FindName("YazmaKipi")).SelectedIndex = 1;
        aktar.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
        string upsertUyari = ((TextBlock)w.FindName("Durum")).Text;

        // YENİ TABLO kipi (v13-S4): listede olmayan ad yazılınca grid DOSYA kolonlarından kurulur
        // (önerilen SQL tipleriyle, birebir eşli) ve Aktar açık kalır.
        hedefTabloKutusu.Text = "YeniSatislar"; // TextChanged → kip anında çözülür (LostFocus gerekmez)
        // Yeni tablo kipinde satırlar DOSYA kolonlarından kurulur ve HedefTip dolar — o sinyali bekle
        StaOrtak.PumpUntil(() => w.Satirlar.Count > 0 && w.Satirlar.All(r => r.HedefTip.Length > 0));
        var yeniKipSatirlar = w.Satirlar.Select(r => (r.HedefKolon, r.HedefTip, r.KaynakKolon)).ToList();
        ((ComboBox)w.FindName("YazmaKipi")).SelectedIndex = 0; // sonraki aşama için geri al

        // Başlıksız kip: adlar Kolon1..N (eşleme de tazelenir — Ad artık dosyada yok sayılır).
        ((CheckBox)w.FindName("IlkSatirBaslik")).IsChecked = false;
        onizle.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
        // Grid YENİDEN kurulur; "kolon var" yetmez (eskiler duruyor) — başlığın DEĞİŞMESİNİ bekle.
        // Neye değiştiğini aşağıdaki assert denetler; burada yalnız "yeniden kuruldu" sinyali alınır.
        StaOrtak.PumpUntil(() => grid.Columns.Count > 0
            && !(grid.Columns[0].Header?.ToString() ?? "").StartsWith("Ad", StringComparison.Ordinal));
        string basliksizIlkKolon = grid.Columns[0].Header?.ToString() ?? "";

        w.Close();
        return new Sonuc(ayracGorunur, ilkBaslik, tutarBaslik, satirSayisi, durum,
            basliksizIlkKolon, [.. eslesmeler.Select(e => (e.HedefKolon, e.KaynakKolon))],
            aktarAcik, upsertUyari,
            [.. yeniKipSatirlar.Select(e => (e.HedefKolon, e.HedefTip, e.KaynakKolon))]);
    }
}
