using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Views;

/// <summary>
/// Mongo eşleme gridinin satırı (v12-S6): kaynak alan ($sample envanteri) + yazılabilir hedef ad.
/// Varsayılan hedef = kaynak adı (birebir); boş = alan aktarılmaz.
/// </summary>
public sealed partial class MongoAktarimSatiri : ObservableObject
{
    public required string KaynakAlan { get; init; }
    public required string Tip { get; init; }

    [ObservableProperty] private string _hedefAlan = "";
    [ObservableProperty] private IReadOnlyList<string> _hedefAdaylar = [];
}

/// <summary>
/// 📦 Mongo→Mongo Paket Aktarım penceresi (v12-S5; kullanıcı senaryosu: log koleksiyonunu arşiv
/// DB'sine taşıma). SQL penceresinden farkları: kolon eşleme gridi YOK (belgeler olduğu gibi),
/// kaynakta isteğe bağlı find süzgeci, hedef DB/koleksiyon YAZILABİLİR (Mongo örtük oluşturur),
/// Ekle/Güncelle _id'ye göredir. Motor işi <see cref="MongoAktarimServisi"/>'nde.
/// </summary>
public partial class MongoAktarimPenceresi : Window
{
    private readonly ConnectionProfile _kaynakProfil;
    private readonly Func<string, Task<IReadOnlyList<SemaNesnesi>>> _kaynakKoleksiyonlariGetir;
    private readonly IProfileStore _profilDeposu;
    private readonly ISchemaService _semaServisi;
    private readonly MongoAktarimServisi _aktarim;
    private CancellationTokenSource? _cts;

    // v22-S16: son aktarımın sonucu — "⬇ Hata raporunu kaydet" bunun TAMAMINI dosyaya yazar.
    private AktarimSonucu? _sonSonuc;
    private string _sonKaynakTanim = "", _sonHedefTanim = "";
    private IReadOnlyList<SemaNesnesi> _kaynakKoleksiyonNesneleri = [];
    private SemaOnbellegi? _hedefSema; // hedef DB envanteri — aday listeleri buradan

    public ObservableCollection<MongoAktarimSatiri> Satirlar { get; } = [];

    public MongoAktarimPenceresi(
        ConnectionProfile kaynakProfil,
        IReadOnlyList<string> kaynakDbler,
        string? kaynakSeciliDb,
        Func<string, Task<IReadOnlyList<SemaNesnesi>>> kaynakKoleksiyonlariGetir,
        IProfileStore profilDeposu,
        ISchemaService semaServisi,
        MongoAktarimServisi aktarim)
    {
        _kaynakProfil = kaynakProfil;
        _kaynakKoleksiyonlariGetir = kaynakKoleksiyonlariGetir;
        _profilDeposu = profilDeposu;
        _semaServisi = semaServisi;
        _aktarim = aktarim;

        InitializeComponent();
        EslemeGrid.ItemsSource = Satirlar;
        KaynakProfilAdi.Text = $"{kaynakProfil.Ad}  ({kaynakProfil.Sunucu})";
        KaynakDb.ItemsSource = kaynakDbler;
        KaynakDb.SelectedItem = kaynakSeciliDb ?? kaynakDbler.FirstOrDefault();
        YazmaKipi.ItemsSource = new[] { "Yalnız ekle", "Ekle/Güncelle (_id'ye göre)" };
        YazmaKipi.SelectedIndex = 0;
        HataPolitikasi.ItemsSource = new[] { "İlk hatada dur", "Hatalı belgeyi atla ve raporla" };
        HataPolitikasi.SelectedIndex = 0;

        // async void Loaded sınırı: hata pencereyi düşürmesin (proje deseni).
        Loaded += async (_, _) =>
        {
            try
            {
                await KaynakKoleksiyonlariYukleAsync();
                IReadOnlyList<ConnectionProfile> profiller = await _profilDeposu.GetAllAsync();
                // Hedef yalnız Mongo ailesi — belgeler tabloya değil koleksiyona gider.
                HedefProfil.ItemsSource = profiller.Where(p => p.Motor == MotorTuru.Mongo).ToList();
            }
            catch (Exception ex) { Durum.Text = $"Yükleme hatası: {ex.Message}"; }
        };
    }

    private async void KaynakDb_Secildi(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        try { await KaynakKoleksiyonlariYukleAsync(); }
        catch (Exception ex) { Durum.Text = $"Kaynak koleksiyonlar yüklenemedi: {ex.Message}"; }
    }

    private async Task KaynakKoleksiyonlariYukleAsync()
    {
        if (KaynakDb.SelectedItem is not string db)
            return;
        _kaynakKoleksiyonNesneleri = await _kaynakKoleksiyonlariGetir(db);
        // Mongo'da koleksiyon adı ÇIPLAK geçer (TamAd "db.koleksiyon" değil — LogAnaliz dersi).
        KaynakKoleksiyon.ItemsSource = _kaynakKoleksiyonNesneleri.Select(k => k.Ad).ToList();
    }

    /// <summary>Kaynak koleksiyon seçilince eşleme gridi $sample envanteriyle kurulur (v12-S6).</summary>
    private void KaynakKoleksiyon_Secildi(object sender, RoutedEventArgs e)
    {
        Satirlar.Clear();
        SemaNesnesi? koleksiyon = _kaynakKoleksiyonNesneleri
            .FirstOrDefault(k => k.Ad == KaynakKoleksiyon.SelectedItem as string);
        if (koleksiyon is null)
            return;

        IReadOnlyList<string> adaylar = HedefAdaylar();
        foreach (SemaKolonu alan in koleksiyon.Kolonlar)
        {
            Satirlar.Add(new MongoAktarimSatiri
            {
                KaynakAlan = alan.Ad,
                Tip = alan.Tip,
                HedefAlan = alan.Ad, // varsayılan birebir — dokunulmazsa belge OLDUĞU GİBİ gider
                HedefAdaylar = adaylar,
            });
        }

        Durum.Text = koleksiyon.Kolonlar.Count > 0
            ? $"{koleksiyon.Kolonlar.Count} alan örneklemden geldi — varsayılan birebir; gerekirse düzeltin."
            : "Koleksiyon boş görünüyor (örneklemde belge yok) — belgeler olduğu gibi kopyalanır.";
    }

    /// <summary>Hedef koleksiyon envanteri (varsa) — yazılabilir hedef alan combosunun adayları.</summary>
    private IReadOnlyList<string> HedefAdaylar()
    {
        SemaNesnesi? hedef = _hedefSema?.Nesneler
            .FirstOrDefault(n => n.Tur == SemaNesneTuru.Koleksiyon && n.Ad == HedefKoleksiyon.Text.Trim());
        return hedef is null ? [] : [.. hedef.Kolonlar.Select(k => k.Ad)];
    }

    private void HedefKoleksiyon_Secildi(object sender, RoutedEventArgs e)
    {
        // Aday listesi tazelenir; kullanıcının yazdığı hedef adlar KORUNUR (yalnız öneri değişir).
        Dispatcher.BeginInvoke(() => // editable combo: SelectionChanged anında Text henüz eski
        {
            IReadOnlyList<string> adaylar = HedefAdaylar();
            foreach (MongoAktarimSatiri satir in Satirlar)
                satir.HedefAdaylar = adaylar;
        });
    }

    private async void HedefProfil_Secildi(object sender, RoutedEventArgs e)
    {
        if (HedefProfil.SelectedItem is not ConnectionProfile profil)
            return;
        HedefDb.ItemsSource = null;
        HedefKoleksiyon.ItemsSource = null;
        Durum.Text = $"{profil.Ad} veritabanları okunuyor…";
        try
        {
            IReadOnlyList<VeritabaniBilgisi> dbler = await _semaServisi.VeritabanlariAsync(profil, CancellationToken.None);
            HedefDb.ItemsSource = dbler.Where(d => !d.SistemMi).Select(d => d.Ad).ToList();
            Durum.Text = "Hedef veritabanını seçin ya da YENİ ad yazın.";
        }
        catch (Exception ex) { Durum.Text = $"Hedefe bağlanılamadı: {ex.Message}"; }
    }

    private async void HedefDb_Secildi(object sender, RoutedEventArgs e)
    {
        if (HedefProfil.SelectedItem is not ConnectionProfile profil || HedefDb.SelectedItem is not string db)
            return;
        try
        {
            _hedefSema = await _semaServisi.YukleAsync(profil, db, CancellationToken.None);
            HedefKoleksiyon.ItemsSource = _hedefSema.Nesneler
                .Where(n => n.Tur == SemaNesneTuru.Koleksiyon).Select(n => n.Ad).ToList();
        }
        catch (Exception ex) { Durum.Text = $"Hedef koleksiyonlar okunamadı: {ex.Message}"; }
    }

    private async void Aktar_Click(object sender, RoutedEventArgs e)
    {
        // Editable combo: SelectedItem yerine Text okunur — yeni (listede olmayan) ad da geçerli.
        string hedefDb = HedefDb.Text.Trim();
        string hedefKoleksiyon = HedefKoleksiyon.Text.Trim();
        if (KaynakDb.SelectedItem is not string kaynakDb
            || KaynakKoleksiyon.SelectedItem is not string kaynakKoleksiyon
            || HedefProfil.SelectedItem is not ConnectionProfile hedefProfil
            || hedefDb.Length == 0 || hedefKoleksiyon.Length == 0)
        {
            Durum.Text = "Kaynak koleksiyonu seçin; hedef profil + veritabanı + koleksiyon adını girin.";
            return;
        }

        bool suzgecli = Suzgec.Text.Trim().Length > 0;
        bool ekleGuncelle = YazmaKipi.SelectedIndex == 1;

        // Eşleme kararı (v12-S6): grid HİÇ değiştirilmemişse (her satır birebir) eşleme GÖNDERİLMEZ —
        // belge olduğu gibi gider ki $sample örnekleminde GÖRÜNMEYEN alanlar da taşınsın (veri kaybı
        // olmasın). Kullanıcı bir şeyi değiştirdiyse bilinçli seçimdir: yalnız listedekiler taşınır.
        bool birebir = Satirlar.All(s => s.HedefAlan.Trim() == s.KaynakAlan);
        IReadOnlyList<AktarimEslesmesi>? eslesmeler = birebir
            ? null
            : [.. Satirlar
                .Where(s => s.HedefAlan.Trim().Length > 0)
                .Select(s => new AktarimEslesmesi(s.KaynakAlan, s.HedefAlan.Trim()))];
        if (eslesmeler is { Count: 0 })
        {
            Durum.Text = "En az bir alan eşlemesi gerekli (ya da tümünü birebir bırakın).";
            return;
        }

        string ozet = $"Kaynak: {_kaynakProfil.Ad} · {kaynakDb} · {kaynakKoleksiyon}"
            + (suzgecli ? " (süzgeçli)" : " (tüm belgeler)") + "\n"
            + $"Hedef: {hedefProfil.Ad} · {hedefDb} · {hedefKoleksiyon}\n"
            + "Alanlar: " + (eslesmeler is null
                ? "birebir (belgeler olduğu gibi)."
                : $"{eslesmeler.Count} alan eşlendi — örneklemde görünmeyen alanlar AKTARILMAZ.") + "\n"
            + "Yazma: " + (ekleGuncelle ? "Ekle/Güncelle (_id'ye göre)." : "Yalnız ekle.")
            + (OnceTemizle.IsChecked == true ? "\n\n⚠ ÖNCE HEDEF KOLEKSİYON TEMİZLENECEK (deleteMany)!" : "");
        if (MessageBox.Show(this, ozet + "\n\nBaşlatılsın mı?", "SQLST — Paket Aktarım (MongoDB)",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var istek = new MongoAktarimIstegi(
            _kaynakProfil, kaynakDb, kaynakKoleksiyon,
            suzgecli ? Suzgec.Text : null,
            hedefProfil, hedefDb, hedefKoleksiyon,
            OnceTemizle.IsChecked == true,
            HataPolitikasi.SelectedIndex == 1
                ? AktarimHataPolitikasi.AtlaVeRaporla : AktarimHataPolitikasi.IlkHatadaDur,
            ekleGuncelle ? AktarimYazmaKipi.EkleGuncelle : AktarimYazmaKipi.YalnizEkle,
            eslesmeler);

        AktarDugmesi.IsEnabled = false;
        DurdurDugmesi.IsEnabled = true;
        Rapor.Visibility = Visibility.Collapsed;
        BtnHataRaporu.Visibility = Visibility.Collapsed; // v22-S16: eski koşunun düğmesi kalmasın
        Ilerleme.Visibility = Visibility.Visible;
        Ilerleme.IsIndeterminate = true; // toplam bilinmiyor — akışlı imleç
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
                { IptalEdildi: true } => "■ Durduruldu — o ana dek yazılan belgeler hedefte kaldı, rapor aşağıda.",
                _ => $"⚠ {sonuc.Hata}",
            };
            // v22-S16: sonuç günlüğe düşer, rapor günlük yolunu söyler, hata varsa tam-rapor düğmesi.
            _sonSonuc = sonuc;
            _sonKaynakTanim = $"{_kaynakProfil.Ad} · {kaynakDb} · {kaynakKoleksiyon}";
            _sonHedefTanim = $"{hedefProfil.Ad} · {hedefDb} · {hedefKoleksiyon}";
            AktarimGunlugu.Logla("Paket Aktarım (MongoDB)", _sonKaynakTanim, _sonHedefTanim, sonuc);
            Rapor.Text = RaporMetni(sonuc);
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

    /// <summary>Aktarım sonu raporu — SQL penceresiyle aynı biçim (belge terimiyle). v22-S16:
    /// örnekler ekranda İLK 20 ile sınırlı (tamamı ⬇ dosyada), günlük yolu sona eklenir.</summary>
    private static string RaporMetni(AktarimSonucu s)
    {
        long islenen = s.Yazilan + s.Guncellenen;
        double hiz = s.Sure.TotalSeconds > 0.05 ? islenen / s.Sure.TotalSeconds : islenen;
        var rapor = new System.Text.StringBuilder()
            .AppendLine($"Okunan {s.Okunan:N0} · Eklenen {s.Yazilan:N0} · Güncellenen {s.Guncellenen:N0}"
                + $" · Atlanan {s.Atlanan:N0} · Süre {s.Sure.TotalSeconds:F1} sn (~{hiz:N0} belge/sn)");
        if (s.Hata is not null)
            rapor.AppendLine($"Sonuç: {s.Hata}");
        if (s.HataOrnekleri.Count > 0)
        {
            int goster = Math.Min(s.HataOrnekleri.Count, AktarimGunlugu.UiOrnekTavani);
            rapor.AppendLine(s.HataOrnekleri.Count > goster
                ? $"Hatalı belge örnekleri (ilk {goster} — toplam {s.HataOrnekleri.Count:N0}; tamamı: ⬇ Hata raporunu kaydet):"
                : $"Hatalı belge örnekleri (ilk {goster}):");
            foreach (string ornek in s.HataOrnekleri.Take(goster))
                rapor.AppendLine($"  {ornek}");
        }
        rapor.AppendLine(AktarimGunlugu.GunlukNotu);

        return rapor.ToString().TrimEnd();
    }

    /// <summary>v22-S16: atlanan belgelerin TAM listesini (ekran ilk 20'yi gösterir) dosyaya yazar.</summary>
    private void HataRaporu_Click(object sender, RoutedEventArgs e)
    {
        if (_sonSonuc is not { } s)
            return;
        var kutu = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Metin raporu (*.txt)|*.txt",
            FileName = $"mongo-aktarim-hatalari-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (kutu.ShowDialog(this) != true)
            return;
        try
        {
            System.IO.File.WriteAllText(kutu.FileName,
                AktarimGunlugu.HataRaporuMetni("Paket Aktarım (MongoDB)", _sonKaynakTanim, _sonHedefTanim, s),
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
