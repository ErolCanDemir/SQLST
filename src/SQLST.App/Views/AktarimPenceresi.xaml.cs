using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Views;

/// <summary>Eşleme gridinin satırı: hedef kolon + kaynak aday seçimi (boş "" = aktarma).</summary>
public sealed partial class AktarimSatiri : ObservableObject
{
    public required string HedefKolon { get; init; }
    public required string HedefTip { get; init; }
    public required bool AnahtarMi { get; init; } // hedef PK — Ekle/Güncelle kipinin anahtarı
    public required IReadOnlyList<string> KaynakAdaylar { get; init; } // "" + kaynak kolon adları

    public string HedefGosterim => AnahtarMi ? $"🔑 {HedefKolon}" : HedefKolon;

    [ObservableProperty] private string _kaynakKolon = "";
}

/// <summary>
/// 📦 Paket Aktarım penceresi (v12-S2, kullanıcı akışı 2026-07-26): kaynak (bağlı profil) DB+tablo →
/// hedef (KAYITLI profil) DB+tablo → kolon eşleme gridi (otomatik öneri) → aktar. Motor işi
/// <see cref="AktarimServisi"/>'nde; pencere yalnız seçtirir, özetler, ilerlemeyi gösterir.
/// "Önce temizle" başlatırken AÇIKÇA onaylatılır. S3'te Sorgu→Tablo sekmesi eklenecek.
/// </summary>
public partial class AktarimPenceresi : Window
{
    private readonly ConnectionProfile _kaynakProfil;
    private readonly Func<string, Task<IReadOnlyList<SemaNesnesi>>> _kaynakTablolariGetir;
    private readonly IProfileStore _profilDeposu;
    private readonly ISchemaService _semaServisi;
    private readonly ILehceSaglayici _lehceler;
    private readonly AktarimServisi _aktarim;
    private CancellationTokenSource? _cts;

    // v22-S16: son aktarımın sonucu — "⬇ Hata raporunu kaydet" bunun TAMAMINI dosyaya yazar
    // (ekran yalnız ilk 20 örneği gösterir).
    private AktarimSonucu? _sonSonuc;
    private string _sonKaynakTanim = "", _sonHedefTanim = "";

    private IReadOnlyList<SemaNesnesi> _kaynakTablolar = [];
    private IReadOnlyList<SemaNesnesi> _hedefTablolar = [];
    private IReadOnlyList<SemaKolonu> _sorguKolonlari = []; // v12-S3: "Kolonları getir" sonucu

    /// <summary>Kaynak kipi (v12-S3): sekme 0 = Tablo, 1 = Sorgu.</summary>
    private bool SorguKipiMi => KaynakKip.SelectedIndex == 1;

    public ObservableCollection<AktarimSatiri> Satirlar { get; } = [];

    public AktarimPenceresi(
        ConnectionProfile kaynakProfil,
        IReadOnlyList<string> kaynakDbler,
        string? kaynakSeciliDb,
        Func<string, Task<IReadOnlyList<SemaNesnesi>>> kaynakTablolariGetir,
        IProfileStore profilDeposu,
        ISchemaService semaServisi,
        ILehceSaglayici lehceler,
        AktarimServisi aktarim)
    {
        _kaynakProfil = kaynakProfil;
        _kaynakTablolariGetir = kaynakTablolariGetir;
        _profilDeposu = profilDeposu;
        _semaServisi = semaServisi;
        _lehceler = lehceler;
        _aktarim = aktarim;

        InitializeComponent();
        EslemeGrid.ItemsSource = Satirlar;
        KaynakProfilAdi.Text = $"{kaynakProfil.Ad}  ({kaynakProfil.Sunucu})";
        KaynakDb.ItemsSource = kaynakDbler;
        KaynakDb.SelectedItem = kaynakSeciliDb ?? kaynakDbler.FirstOrDefault();
        HataPolitikasi.ItemsSource = new[] { "İlk hatada dur", "Hatalı satırı atla ve raporla" };
        HataPolitikasi.SelectedIndex = 0;
        YazmaKipi.ItemsSource = new[] { "Yalnız ekle", "Ekle/Güncelle (🔑 anahtara göre)" };
        YazmaKipi.SelectedIndex = 0;

        // async void Loaded sınırı: hata pencereyi düşürmesin (proje deseni).
        Loaded += async (_, _) =>
        {
            try
            {
                await KaynakTablolariYukleAsync();
                IReadOnlyList<ConnectionProfile> profiller = await _profilDeposu.GetAllAsync();
                // Hedef: SQL ailesi (Mongo v12-S5'te) — kaynak profil de listede (aynı sunucuda DB'ler arası).
                HedefProfil.ItemsSource = profiller.Where(p => p.Motor != MotorTuru.Mongo).ToList();
            }
            catch (Exception ex) { Durum.Text = $"Yükleme hatası: {ex.Message}"; }
        };
    }

    private async void KaynakDb_Secildi(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _sorguKolonlari = []; // başka DB'de keşif bayat — yeniden "Kolonları getir" istenir
        GridiTazele();
        try { await KaynakTablolariYukleAsync(); }
        catch (Exception ex) { Durum.Text = $"Kaynak tablolar yüklenemedi: {ex.Message}"; }
    }

    private async Task KaynakTablolariYukleAsync()
    {
        if (KaynakDb.SelectedItem is not string db)
            return;
        _kaynakTablolar = await _kaynakTablolariGetir(db);
        KaynakTablo.ItemsSource = _kaynakTablolar.Select(t => t.TamAd).ToList();
    }

    private async void HedefProfil_Secildi(object sender, RoutedEventArgs e)
    {
        if (HedefProfil.SelectedItem is not ConnectionProfile profil)
            return;
        HedefDb.ItemsSource = null;
        HedefTablo.ItemsSource = null;
        Durum.Text = $"{profil.Ad} veritabanları okunuyor…";
        try
        {
            IReadOnlyList<VeritabaniBilgisi> dbler = await _semaServisi.VeritabanlariAsync(profil, CancellationToken.None);
            HedefDb.ItemsSource = dbler.Where(d => !d.SistemMi).Select(d => d.Ad).ToList();
            Durum.Text = "Hedef veritabanını seçin.";
        }
        catch (Exception ex) { Durum.Text = $"Hedefe bağlanılamadı: {ex.Message}"; }
    }

    private async void HedefDb_Secildi(object sender, RoutedEventArgs e)
    {
        if (HedefProfil.SelectedItem is not ConnectionProfile profil || HedefDb.SelectedItem is not string db)
            return;
        Durum.Text = $"{db} tabloları okunuyor…";
        try
        {
            SemaOnbellegi sema = await _semaServisi.YukleAsync(profil, db, CancellationToken.None);
            _hedefTablolar = [.. sema.Nesneler.Where(n => n.Tur == SemaNesneTuru.Tablo)
                .OrderBy(n => n.TamAd, StringComparer.OrdinalIgnoreCase)];
            HedefTablo.ItemsSource = _hedefTablolar.Select(t => t.TamAd).ToList();
            Durum.Text = "Hedef tabloyu seçin.";
        }
        catch (Exception ex) { Durum.Text = $"Hedef şema okunamadı: {ex.Message}"; }
    }

    /// <summary>Kaynak ya da hedef tablo değişince eşleme gridi otomatik öneriyle yeniden kurulur.</summary>
    private void TabloSecimi_Degisti(object sender, RoutedEventArgs e) => GridiTazele();

    /// <summary>Kaynak kipi (Tablo↔Sorgu) değişince aday kolon kümesi değişir → grid tazelenir.</summary>
    private void KaynakKip_Degisti(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // İçerideki ComboBox'ların SelectionChanged'i buraya KÖPÜRÜR — yalnız sekme değişimini işle.
        if (!ReferenceEquals(e.OriginalSource, KaynakKip) || !IsLoaded)
            return;
        GridiTazele();
    }

    /// <summary>Aktif kipin kaynak kolonları (tablo kolonları ya da sorgu keşfi); kaynak seçilmemişse null.</summary>
    private IReadOnlyList<SemaKolonu>? KaynakKolonlar()
    {
        if (SorguKipiMi)
            return _sorguKolonlari.Count > 0 ? _sorguKolonlari : null;
        return _kaynakTablolar.FirstOrDefault(t => t.TamAd == KaynakTablo.SelectedItem as string)?.Kolonlar;
    }

    private void GridiTazele()
    {
        Satirlar.Clear();
        IReadOnlyList<SemaKolonu>? kaynakKolonlar = KaynakKolonlar();
        SemaNesnesi? hedef = _hedefTablolar.FirstOrDefault(t => t.TamAd == HedefTablo.SelectedItem as string);
        if (kaynakKolonlar is null || hedef is null)
            return;

        IReadOnlyList<AktarimEslesmesi> oneri = AktarimEslestirici.OtomatikEsle(kaynakKolonlar, hedef.Kolonlar);
        var oneriMap = oneri.ToDictionary(o => o.HedefKolon, o => o.KaynakKolon, StringComparer.OrdinalIgnoreCase);
        List<string> adaylar = ["", .. kaynakKolonlar.Select(k => k.Ad)];

        foreach (SemaKolonu h in hedef.Kolonlar)
        {
            Satirlar.Add(new AktarimSatiri
            {
                HedefKolon = h.Ad,
                HedefTip = h.Tip,
                AnahtarMi = h.PkMi,
                KaynakAdaylar = adaylar,
                KaynakKolon = oneriMap.GetValueOrDefault(h.Ad, ""),
            });
        }
        int esli = Satirlar.Count(s => s.KaynakKolon.Length > 0);
        Durum.Text = $"{esli} kolon otomatik eşlendi, {Satirlar.Count - esli} boş (aktarılmayacak). "
            + "Gerekirse düzeltin, sonra Aktar.";
    }

    /// <summary>Sorgu→Tablo (v12-S3): sorgunun sonuç kolonlarını satır çekmeden keşfeder.</summary>
    private async void KolonlariGetir_Click(object sender, RoutedEventArgs e)
    {
        if (SorguMetni.Text.Trim().Length == 0 || KaynakDb.SelectedItem is not string db)
        {
            Durum.Text = "Önce kaynak veritabanını seçip sorguyu yazın.";
            return;
        }

        KolonlariGetirDugmesi.IsEnabled = false;
        Durum.Text = "Sorgu kolonları keşfediliyor (satır çekilmez)…";
        try
        {
            _sorguKolonlari = await _aktarim.KaynakKolonlariAsync(
                _kaynakProfil, db, SorguMetni.Text, CancellationToken.None);
            GridiTazele();
            if (HedefTablo.SelectedItem is null)
                Durum.Text = $"{_sorguKolonlari.Count} kolon bulundu — şimdi hedef tabloyu seçin.";
        }
        catch (Exception ex) { Durum.Text = $"Sorgu keşfi başarısız: {ex.Message}"; }
        finally { KolonlariGetirDugmesi.IsEnabled = true; }
    }

    private async void Aktar_Click(object sender, RoutedEventArgs e)
    {
        SemaNesnesi? kaynak = _kaynakTablolar.FirstOrDefault(t => t.TamAd == KaynakTablo.SelectedItem as string);
        SemaNesnesi? hedef = _hedefTablolar.FirstOrDefault(t => t.TamAd == HedefTablo.SelectedItem as string);
        if ((SorguKipiMi ? _sorguKolonlari.Count == 0 : kaynak is null)
            || hedef is null || HedefProfil.SelectedItem is not ConnectionProfile hedefProfil
            || KaynakDb.SelectedItem is not string kaynakDb || HedefDb.SelectedItem is not string hedefDb)
        {
            Durum.Text = SorguKipiMi
                ? "Önce sorguyu yazıp 'Kolonları getir' deyin ve hedef tabloyu seçin."
                : "Önce kaynak ve hedef tabloyu seçin.";
            return;
        }

        IReadOnlyList<AktarimEslesmesi> eslesmeler = [.. Satirlar
            .Where(s => s.KaynakKolon.Length > 0)
            .Select(s => new AktarimEslesmesi(s.KaynakKolon, s.HedefKolon))];
        if (eslesmeler.Count == 0)
        {
            Durum.Text = "En az bir kolon eşlemesi gerekli.";
            return;
        }

        // Ekle/Güncelle (v12-S4): anahtar = hedefin PK kolonları; hepsi eşlenmiş olmalı ve
        // anahtar dışında güncellenecek en az bir kolon kalmalı. MessageBox'tan ÖNCE denetlenir.
        bool ekleGuncelle = YazmaKipi.SelectedIndex == 1;
        IReadOnlyList<string> anahtarlar = ekleGuncelle
            ? [.. hedef.Kolonlar.Where(k => k.PkMi).Select(k => k.Ad)] : [];
        if (ekleGuncelle)
        {
            var esliHedefler = new HashSet<string>(
                eslesmeler.Select(m => m.HedefKolon), StringComparer.OrdinalIgnoreCase);
            if (anahtarlar.Count == 0)
            {
                Durum.Text = "Ekle/Güncelle için hedef tabloda birincil anahtar (🔑) olmalı — bu tabloda yok.";
                return;
            }
            if (!anahtarlar.All(esliHedefler.Contains))
            {
                Durum.Text = "Ekle/Güncelle için TÜM anahtar kolonlar eşlenmeli: "
                    + string.Join(", ", anahtarlar) + ".";
                return;
            }
            if (eslesmeler.All(m => anahtarlar.Contains(m.HedefKolon, StringComparer.OrdinalIgnoreCase)))
            {
                Durum.Text = "Ekle/Güncelle için anahtar dışında güncellenecek en az bir kolon eşlenmeli.";
                return;
            }
        }

        // ÖZET + (gerekirse) temizleme onayı — geri alınamaz sınıf işlem, açık onay ister.
        string kaynakTanim = SorguKipiMi ? "(sorgu sonucu)" : kaynak!.TamAd;
        string ozet = $"Kaynak: {_kaynakProfil.Ad} · {kaynakDb} · {kaynakTanim}\n"
            + $"Hedef: {hedefProfil.Ad} · {hedefDb} · {hedef.TamAd}\n"
            + $"{eslesmeler.Count} kolon · yazma: "
            + (ekleGuncelle ? $"Ekle/Güncelle (anahtar: {string.Join(", ", anahtarlar)})" : "Yalnız ekle") + "."
            + (OnceTemizle.IsChecked == true ? "\n\n⚠ ÖNCE HEDEF TABLO TEMİZLENECEK (DELETE)!" : "");
        if (MessageBox.Show(this, ozet + "\n\nBaşlatılsın mı?", "SQLST — Paket Aktarım",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // Sorgu kipinde kullanıcının SQL'i AYNEN gider (motor eşlenen kolonları ada göre seçer);
        // tablo kipinde yalnız eşlenen kolonları çeken SELECT kurulur.
        string kaynakSql = SorguKipiMi
            ? SorguMetni.Text
            : AktarimEslestirici.KaynakSelect(
                _lehceler.Getir(_kaynakProfil.Motor).TirnaklaTanimlayici, kaynak!.TamAd, eslesmeler);
        var istek = new AktarimIstegi(
            _kaynakProfil, kaynakDb, kaynakSql,
            hedefProfil, hedefDb, hedef.TamAd,
            eslesmeler,
            OnceTemizle.IsChecked == true,
            HataPolitikasi.SelectedIndex == 1
                ? AktarimHataPolitikasi.AtlaVeRaporla : AktarimHataPolitikasi.IlkHatadaDur,
            ekleGuncelle ? AktarimYazmaKipi.EkleGuncelle : AktarimYazmaKipi.YalnizEkle,
            anahtarlar);

        AktarDugmesi.IsEnabled = false;
        DurdurDugmesi.IsEnabled = true;
        Rapor.Visibility = Visibility.Collapsed;
        BtnHataRaporu.Visibility = Visibility.Collapsed; // v22-S16: eski koşunun düğmesi kalmasın
        Ilerleme.Visibility = Visibility.Visible;
        Ilerleme.IsIndeterminate = true; // toplam bilinmiyor — akışlı okuma
        _cts = new CancellationTokenSource();
        try
        {
            var ilerleme = new Progress<AktarimIlerleme>(p =>
                Durum.Text = $"Aktarılıyor… okunan {p.Okunan:N0} · eklenen {p.Yazilan:N0}"
                    + (p.Guncellenen > 0 ? $" · güncellenen {p.Guncellenen:N0}" : "")
                    + (p.Atlanan > 0 ? $" · atlanan {p.Atlanan:N0}" : ""));
            AktarimSonucu sonuc = await _aktarim.AktarAsync(istek, ilerleme, _cts.Token);

            Durum.Text = sonuc switch
            {
                { Basarili: true } => $"✔ Tamamlandı ({sonuc.Sure.TotalSeconds:F1} sn) — rapor aşağıda.",
                { IptalEdildi: true } => "■ Durduruldu — o ana dek tamamlananlar kaldı, rapor aşağıda.",
                _ => $"⚠ {sonuc.Hata}",
            };
            // v22-S16 (kullanıcı: "hataları loglayalım, nereye yazıldığını da söyleyelim"):
            // sonuç günlüğe düşer, rapor günlük yolunu söyler, hata varsa tam-rapor düğmesi çıkar.
            _sonSonuc = sonuc;
            _sonKaynakTanim = $"{_kaynakProfil.Ad} · {kaynakDb} · {kaynakTanim}";
            _sonHedefTanim = $"{hedefProfil.Ad} · {hedefDb} · {hedef.TamAd}";
            AktarimGunlugu.Logla("Paket Aktarım", _sonKaynakTanim, _sonHedefTanim, sonuc);
            Rapor.Text = RaporMetni(sonuc, AktarimGunlugu.GunlukNotu);
            Rapor.Visibility = Visibility.Visible;
            BtnHataRaporu.Visibility =
                sonuc.HataOrnekleri.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _cts = null;
            AktarDugmesi.IsEnabled = true;
            DurdurDugmesi.IsEnabled = false;
            Ilerleme.Visibility = Visibility.Collapsed;
            Ilerleme.IsIndeterminate = false;
        }
    }

    /// <summary>Aktarım sonu raporu (v12-S4): sayımlar + hız + hata örnekleri, kopyalanabilir metin.
    /// internal: v13 İçe Aktar penceresi de aynı raporu kullanır. v22-S16: örnekler ekranda İLK 20
    /// ile sınırlı (servis 10.000'e kadar toplar — tamamı "⬇ Hata raporunu kaydet" dosyasında);
    /// <paramref name="gunlukNotu"/> verilirse sona eklenir (nereye loglandığı kullanıcıya söylenir).</summary>
    internal static string RaporMetni(AktarimSonucu s, string? gunlukNotu = null)
    {
        long islenen = s.Yazilan + s.Guncellenen;
        double hiz = s.Sure.TotalSeconds > 0.05 ? islenen / s.Sure.TotalSeconds : islenen;
        var rapor = new System.Text.StringBuilder()
            .AppendLine($"Okunan {s.Okunan:N0} · Eklenen {s.Yazilan:N0} · Güncellenen {s.Guncellenen:N0}"
                + $" · Atlanan {s.Atlanan:N0} · Süre {s.Sure.TotalSeconds:F1} sn (~{hiz:N0} satır/sn)");
        if (s.Hata is not null)
            rapor.AppendLine($"Sonuç: {s.Hata}");
        if (s.HataOrnekleri.Count > 0)
        {
            int goster = Math.Min(s.HataOrnekleri.Count, AktarimGunlugu.UiOrnekTavani);
            rapor.AppendLine(s.HataOrnekleri.Count > goster
                ? $"Hatalı satır örnekleri (ilk {goster} — toplam {s.HataOrnekleri.Count:N0}; tamamı: ⬇ Hata raporunu kaydet):"
                : $"Hatalı satır örnekleri (ilk {goster}):");
            foreach (string ornek in s.HataOrnekleri.Take(goster))
                rapor.AppendLine($"  {ornek}");
        }
        if (gunlukNotu is not null)
            rapor.AppendLine(gunlukNotu);

        return rapor.ToString().TrimEnd();
    }

    /// <summary>v22-S16: atlanan satırların TAM listesini (ekran ilk 20'yi gösterir) dosyaya yazar.</summary>
    private void HataRaporu_Click(object sender, RoutedEventArgs e)
    {
        if (_sonSonuc is not { } s)
            return;
        var kutu = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Metin raporu (*.txt)|*.txt",
            FileName = $"aktarim-hatalari-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (kutu.ShowDialog(this) != true)
            return;
        try
        {
            System.IO.File.WriteAllText(kutu.FileName,
                AktarimGunlugu.HataRaporuMetni("Paket Aktarım", _sonKaynakTanim, _sonHedefTanim, s),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Durum.Text = $"Hata raporu kaydedildi: {kutu.FileName}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Durum.Text = $"Rapor yazılamadı: {ex.Message}";
        }
    }

    private void Durdur_Click(object sender, RoutedEventArgs e)
        => IptalYardimcisi.ArkaPlandaIptal(_cts); // m.15: Cancel UI'da bloklayabilir — havuzda
}
