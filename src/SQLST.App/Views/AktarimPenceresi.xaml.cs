using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;
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

/// <summary>Yeni tablo kipinin (v23-S32) kolon önizleme satırı: kaynaktan birebir.</summary>
public sealed record OnizlemeSatiri(int Sira, string Kolon, string Tip, string Null, string Not);

/// <summary>
/// 📦 Paket Aktarım penceresi (v12-S2, kullanıcı akışı 2026-07-26): kaynak (bağlı profil) DB+tablo →
/// hedef (KAYITLI profil) DB+tablo → kolon eşleme gridi (otomatik öneri) → aktar. Motor işi
/// <see cref="AktarimServisi"/>'nde; pencere yalnız seçtirir, özetler, ilerlemeyi gösterir.
/// "Önce temizle" başlatırken AÇIKÇA onaylatılır. v12-S3: Sorgu→Tablo sekmesi.
/// <b>v23-S32 "yeni tabloya aktar" (SELECT INTO gibi):</b> hedef tablo kutusu yazılabilir; listede olmayan ad
/// = yeni tablo — eşleme gridi yerine kolon önizlemesi, aynı sunucuda motor-içi kestirme
/// (<see cref="AktarimServisi.KestirmeAktarAsync"/>), farklı sunucuda CREATE + akışlı parti
/// (<see cref="AktarimIstegi.OnceDdl"/>). Yalnız aynı motor (karar 15 Eki). <c>baslangicSql</c> verilirse
/// (sonuç şeridi "💾 Tabloya kaydet") Sorgu kipinde, hedef = aynı bağlantı/DB, tablo adı odakta açılır.
/// </summary>
public partial class AktarimPenceresi : Window
{
    private readonly ConnectionProfile _kaynakProfil;
    private readonly Func<string, Task<IReadOnlyList<SemaNesnesi>>> _kaynakTablolariGetir;
    private readonly IProfileStore _profilDeposu;
    private readonly ISchemaService _semaServisi;
    private readonly ILehceSaglayici _lehceler;
    private readonly AktarimServisi _aktarim;
    private readonly string? _baslangicSql;
    private CancellationTokenSource? _cts;
    private bool _otomatikKurulum; // baslangicSql kurulumu sırasında combo olayları susar (sıralı yükleme)

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
    public ObservableCollection<OnizlemeSatiri> Onizleme { get; } = [];

    /// <summary>Hedef tablo kutusundaki metin (seçimden ya da elle yazımdan).</summary>
    private string HedefTabloMetni => (HedefTablo.Text ?? "").Trim();

    /// <summary>Yazılan/seçilen ad var olan bir hedef tabloya çarpıyorsa o tablo ("Siparis" → dbo.Siparis dahil).</summary>
    private SemaNesnesi? SeciliHedef
        => YeniTabloyaAktarim.VarOlanTablo(HedefTabloMetni, _hedefTablolar.Select(t => t.TamAd)) is { } tam
            ? _hedefTablolar.First(t => t.TamAd == tam) : null;

    /// <summary>v23-S32: hedef DB seçili, ad yazılı ve listede YOK → yeni tablo kipi.</summary>
    public bool YeniTabloMu => HedefDb.SelectedItem is string && HedefTabloMetni.Length > 0 && SeciliHedef is null;

    public AktarimPenceresi(
        ConnectionProfile kaynakProfil,
        IReadOnlyList<string> kaynakDbler,
        string? kaynakSeciliDb,
        Func<string, Task<IReadOnlyList<SemaNesnesi>>> kaynakTablolariGetir,
        IProfileStore profilDeposu,
        ISchemaService semaServisi,
        ILehceSaglayici lehceler,
        AktarimServisi aktarim,
        string? baslangicSql = null)
    {
        _kaynakProfil = kaynakProfil;
        _kaynakTablolariGetir = kaynakTablolariGetir;
        _profilDeposu = profilDeposu;
        _semaServisi = semaServisi;
        _lehceler = lehceler;
        _aktarim = aktarim;
        _baslangicSql = baslangicSql;

        InitializeComponent();
        EslemeGrid.ItemsSource = Satirlar;
        OnizlemeGrid.ItemsSource = Onizleme;
        KaynakProfilAdi.Text = $"{kaynakProfil.Ad}  ({kaynakProfil.Sunucu})";
        KaynakDb.ItemsSource = kaynakDbler;
        KaynakDb.SelectedItem = kaynakSeciliDb ?? kaynakDbler.FirstOrDefault();
        HataPolitikasi.ItemsSource = new[] { "İlk hatada dur", "Hatalı satırı atla ve raporla" };
        HataPolitikasi.SelectedIndex = 0;
        YazmaKipi.ItemsSource = new[] { "Yalnız ekle", "Ekle/Güncelle (🔑 anahtara göre)" };
        YazmaKipi.SelectedIndex = 0;
        // Yazılabilir hedef kutusu: her tuşta kip yeniden değerlendirilir (TextChanged iç TextBox'tan köpürür).
        HedefTablo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (IsLoaded && !_otomatikKurulum)
                GridiTazele();
        }));

        // async void Loaded sınırı: hata pencereyi düşürmesin (proje deseni).
        Loaded += async (_, _) =>
        {
            try
            {
                await KaynakTablolariYukleAsync();
                IReadOnlyList<ConnectionProfile> profiller = await _profilDeposu.GetAllAsync();
                // Hedef: SQL ailesi (Mongo v12-S5'te) — kaynak profil de listede (aynı sunucuda DB'ler arası).
                var liste = profiller.Where(p => p.Motor != MotorTuru.Mongo).ToList();
                if (!liste.Any(p => p.Id == _kaynakProfil.Id))
                    liste.Add(_kaynakProfil); // kayıtsız/aktif profil de hedef olabilsin (Tabloya kaydet yolu) — sona, sıra bozulmasın
                HedefProfil.ItemsSource = liste;
                if (_baslangicSql is not null)
                    await BaslangicKurulumuAsync(liste, kaynakSeciliDb);
            }
            catch (Exception ex) { Durum.Text = $"Yükleme hatası: {ex.Message}"; }
        };
    }

    /// <summary>
    /// "💾 Tabloya kaydet" (v23-S32): Sorgu kipi + SQL dolu, kolonlar keşfedilmiş, hedef = aynı bağlantı/DB,
    /// tablo adı boş ve odakta. Combo olayları susturulup yükleme SIRAYLA await'lenir (yarış yok).
    /// </summary>
    private async Task BaslangicKurulumuAsync(IReadOnlyList<ConnectionProfile> liste, string? kaynakSeciliDb)
    {
        _otomatikKurulum = true;
        try
        {
            KaynakKip.SelectedIndex = 1;
            SorguMetni.Text = _baslangicSql;
            ConnectionProfile hedef = liste.First(p => p.Id == _kaynakProfil.Id);
            HedefProfil.SelectedItem = hedef;
            await HedefDblerYukleAsync(hedef);
            if (kaynakSeciliDb is not null && HedefDb.ItemsSource is IEnumerable<string> dbler && dbler.Contains(kaynakSeciliDb))
            {
                HedefDb.SelectedItem = kaynakSeciliDb;
                await HedefTablolariYukleAsync(hedef, kaynakSeciliDb);
            }
            await KolonlariKesfetAsync();
        }
        finally { _otomatikKurulum = false; }
        GridiTazele();
        Durum.Text = $"{_sorguKolonlari.Count} kolon keşfedildi — hedef tablo ADINI yazın (yoksa oluşturulur), sonra Oluştur ve aktar.";
        HedefTablo.Focus();
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
        if (_otomatikKurulum || HedefProfil.SelectedItem is not ConnectionProfile profil)
            return;
        await HedefDblerYukleAsync(profil);
    }

    private async Task HedefDblerYukleAsync(ConnectionProfile profil)
    {
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
        if (_otomatikKurulum || HedefProfil.SelectedItem is not ConnectionProfile profil || HedefDb.SelectedItem is not string db)
            return;
        await HedefTablolariYukleAsync(profil, db);
    }

    private async Task HedefTablolariYukleAsync(ConnectionProfile profil, string db)
    {
        Durum.Text = $"{db} tabloları okunuyor…";
        try
        {
            SemaOnbellegi sema = await _semaServisi.YukleAsync(profil, db, CancellationToken.None);
            _hedefTablolar = [.. sema.Nesneler.Where(n => n.Tur == SemaNesneTuru.Tablo)
                .OrderBy(n => n.TamAd, StringComparer.OrdinalIgnoreCase)];
            HedefTablo.ItemsSource = _hedefTablolar.Select(t => t.TamAd).ToList();
            Durum.Text = "Hedef tabloyu seçin — ya da YENİ bir ad yazın (SELECT INTO gibi oluşturulur).";
        }
        catch (Exception ex) { Durum.Text = $"Hedef şema okunamadı: {ex.Message}"; }
    }

    /// <summary>Kaynak ya da hedef tablo değişince eşleme gridi otomatik öneriyle yeniden kurulur.</summary>
    private void TabloSecimi_Degisti(object sender, RoutedEventArgs e)
    {
        if (!_otomatikKurulum)
            GridiTazele();
    }

    /// <summary>Kaynak kipi (Tablo↔Sorgu) değişince aday kolon kümesi değişir → grid tazelenir.</summary>
    private void KaynakKip_Degisti(object sender, SelectionChangedEventArgs e)
    {
        // İçerideki ComboBox'ların SelectionChanged'i buraya KÖPÜRÜR — yalnız sekme değişimini işle.
        if (!ReferenceEquals(e.OriginalSource, KaynakKip) || !IsLoaded || _otomatikKurulum)
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
        SemaNesnesi? hedef = SeciliHedef;
        if (kaynakKolonlar is not null && hedef is not null)
        {
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
            // Yalnız ad yazılıp ("Siparis") var olan tabloya çarptıysa açıkça söyle — sessiz ezme yok.
            if (!string.Equals(HedefTabloMetni, hedef.TamAd, StringComparison.OrdinalIgnoreCase))
                Durum.Text = $"Hedefte {hedef.TamAd} ZATEN VAR — eşleme kipi (var olan tabloya eklenir). " + Durum.Text;
        }
        KipiGuncelle(kaynakKolonlar);
    }

    /// <summary>
    /// v23-S32: kip yeni tabloysa eşleme gridi → kolon önizlemesi, rozet + PK kutusu + Script düğmesi görünür,
    /// "önce temizle"/UPSERT anlamsız (kapalı), düğme "Oluştur ve aktar". Değilse hepsi eski haline.
    /// </summary>
    private void KipiGuncelle(IReadOnlyList<SemaKolonu>? kaynakKolonlar)
    {
        bool yeni = YeniTabloMu;
        Visibility goster = yeni ? Visibility.Visible : Visibility.Collapsed;
        YeniRozet.Visibility = goster;
        YeniSecenekler.Visibility = goster;
        ScriptGosterDugmesi.Visibility = goster;
        OnizlemeGrid.Visibility = goster;
        EslemeGrid.Visibility = yeni ? Visibility.Collapsed : Visibility.Visible;
        AktarDugmesi.Content = yeni ? "📦 Oluştur ve aktar" : "📦 Aktar";
        OnceTemizle.IsEnabled = !yeni;
        YazmaKipi.IsEnabled = !yeni;
        if (yeni)
        {
            OnceTemizle.IsChecked = false;
            YazmaKipi.SelectedIndex = 0;
        }

        Onizleme.Clear();
        if (!yeni)
            return;

        bool pkVar = !SorguKipiMi && kaynakKolonlar?.Any(k => k.PkMi) == true;
        PkOlustur.IsEnabled = pkVar;
        PkOlustur.ToolTip = pkVar ? "Kaynağın birincil anahtarı hedefte de PRIMARY KEY olur; işaretsiz = SELECT INTO gibi yalnız kolonlar + veri"
            : SorguKipiMi ? "Sorgu sonucunun birincil anahtarı bilinemez — PK sonradan elle eklenir"
            : "Kaynak tabloda birincil anahtar yok";
        if (kaynakKolonlar is null)
        {
            Durum.Text = SorguKipiMi
                ? "Yeni tablo: önce sorguyu yazıp 'Kolonları getir' deyin — tablo o kolonlarla oluşturulur."
                : "Yeni tablo: önce kaynak tabloyu seçin — tablo onun kolonlarıyla oluşturulur.";
            return;
        }
        int sira = 0;
        foreach (SemaKolonu k in kaynakKolonlar)
        {
            string not = k.PkMi ? (PkOlustur.IsChecked == true && pkVar ? "🔑 PK (hedefte de PK)" : "🔑 PK (kaynakta; taşınmıyor)") : "";
            Onizleme.Add(new OnizlemeSatiri(++sira, k.Ad, k.Tip, k.NullOlabilir ? "evet" : "hayır", not));
        }
        if (HedefProfil.SelectedItem is ConnectionProfile hp && hp.Motor != _kaynakProfil.Motor)
        {
            Durum.Text = $"Yeni tablo kipi yalnız AYNI motorda ({_kaynakProfil.Motor}→{_kaynakProfil.Motor}) — tip çevirisi yapılmaz. "
                + "Farklı motora aktarmak için hedef tabloyu kendiniz oluşturup listeden seçin.";
            return;
        }
        bool ayniSunucu = HedefProfil.SelectedItem is ConnectionProfile hp2 && YeniTabloyaAktarim.AyniSunucuMu(_kaynakProfil, hp2);
        Durum.Text = $"Hedefte {HedefTabloMetni} yok — aktarım onu kaynağın {kaynakKolonlar.Count} kolonuyla oluşturup dolduracak"
            + (ayniSunucu ? " (aynı sunucu: tek komutla sunucu içinde)." : " (farklı sunucu: SQLST üzerinden akışlı).");
    }

    /// <summary>Sorgu→Tablo (v12-S3): sorgunun sonuç kolonlarını satır çekmeden keşfeder.</summary>
    private async void KolonlariGetir_Click(object sender, RoutedEventArgs e)
    {
        if (SorguMetni.Text.Trim().Length == 0 || KaynakDb.SelectedItem is not string)
        {
            Durum.Text = "Önce kaynak veritabanını seçip sorguyu yazın.";
            return;
        }

        KolonlariGetirDugmesi.IsEnabled = false;
        Durum.Text = "Sorgu kolonları keşfediliyor (satır çekilmez)…";
        try
        {
            await KolonlariKesfetAsync();
            GridiTazele();
            if (SeciliHedef is null && !YeniTabloMu)
                Durum.Text = $"{_sorguKolonlari.Count} kolon bulundu — şimdi hedef tabloyu seçin ya da yeni ad yazın.";
        }
        catch (Exception ex) { Durum.Text = $"Sorgu keşfi başarısız: {ex.Message}"; }
        finally { KolonlariGetirDugmesi.IsEnabled = true; }
    }

    private async Task KolonlariKesfetAsync()
    {
        if (KaynakDb.SelectedItem is not string db)
            return;
        _sorguKolonlari = await _aktarim.KaynakKolonlariAsync(_kaynakProfil, db, SorguMetni.Text, CancellationToken.None);
    }

    private async void Aktar_Click(object sender, RoutedEventArgs e)
    {
        if (YeniTabloMu)
        {
            await YeniTabloyaAktarAsync();
            return;
        }

        SemaNesnesi? kaynak = _kaynakTablolar.FirstOrDefault(t => t.TamAd == KaynakTablo.SelectedItem as string);
        SemaNesnesi? hedef = SeciliHedef;
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
        bool temizle = OnceTemizle.IsChecked == true;
        if (!Iletisim.Sor(this, "SQLST — Paket Aktarım", "Aktarım başlatılsın mı?", ozet,
                temizle ? "Temizle ve aktar" : "▶ Aktarımı başlat", temizle ? IletisimTuru.Tehlike : IletisimTuru.Soru))
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

        await KosVeRaporlaAsync("Paket Aktarım",
            $"{_kaynakProfil.Ad} · {kaynakDb} · {kaynakTanim}", $"{hedefProfil.Ad} · {hedefDb} · {hedef.TamAd}",
            (ilerleme, ct) => _aktarim.AktarAsync(istek, ilerleme, ct));
    }

    /// <summary>Yeni tablo kipinin planı (v23-S32): kestirme (aynı sunucu, motor içi) YA DA CREATE + akışlı.</summary>
    private sealed record YeniTabloPlani(
        string KaynakSql, string KaynakTanim, ConnectionProfile HedefProfil, string KaynakDb, string HedefDb,
        string HedefTablo, IReadOnlyList<SemaKolonu> Kolonlar, IReadOnlyList<string> Pk,
        YeniTabloyaAktarim.KestirmePlani? Kestirme, string? KestirmeNedeni, string? CreateSql);

    /// <summary>Girdileri denetleyip planı kurar; sorun varsa Durum'a yazar ve null döner.</summary>
    private YeniTabloPlani? YeniTabloPlaniKur()
    {
        IReadOnlyList<SemaKolonu>? kolonlar = KaynakKolonlar();
        SemaNesnesi? kaynak = _kaynakTablolar.FirstOrDefault(t => t.TamAd == KaynakTablo.SelectedItem as string);
        if (kolonlar is null || HedefProfil.SelectedItem is not ConnectionProfile hedefProfil
            || KaynakDb.SelectedItem is not string kaynakDb || HedefDb.SelectedItem is not string hedefDb)
        {
            Durum.Text = SorguKipiMi
                ? "Önce sorguyu yazıp 'Kolonları getir' deyin; hedef bağlantı/veritabanını seçin."
                : "Önce kaynak tabloyu ve hedef bağlantı/veritabanını seçin.";
            return null;
        }
        if (hedefProfil.Motor != _kaynakProfil.Motor)
        {
            Durum.Text = "Yeni tablo kipi yalnız AYNI motorda çalışır (tip çevirisi yapılmaz) — "
                + "farklı motora aktarmak için hedef tabloyu kendiniz oluşturup listeden seçin.";
            return null;
        }

        ILehce lehce = _lehceler.Getir(_kaynakProfil.Motor);
        string hedefTablo = HedefTabloMetni;
        IReadOnlyList<string> pk = PkOlustur.IsChecked == true && !SorguKipiMi
            ? [.. kolonlar.Where(k => k.PkMi).Select(k => k.Ad)] : [];
        string kaynakSql = SorguKipiMi
            ? SorguMetni.Text
            : $"SELECT {string.Join(", ", kolonlar.Select(k => lehce.TirnaklaTanimlayici(k.Ad)))} "
              + $"FROM {AktarimEslestirici.TabloYaz(lehce.TirnaklaTanimlayici, kaynak!.TamAd)}";
        string kaynakTanim = SorguKipiMi ? "(sorgu sonucu)" : kaynak!.TamAd;

        YeniTabloyaAktarim.KestirmePlani? kestirme = null;
        string? neden = "farklı sunucu";
        if (YeniTabloyaAktarim.AyniSunucuMu(_kaynakProfil, hedefProfil))
            (kestirme, neden) = YeniTabloyaAktarim.KestirmeIfadeleri(lehce, kaynakSql, kaynakDb, hedefDb, hedefTablo, pk);

        string? createSql = null;
        if (kestirme is null)
        {
            (createSql, string? hata) = YeniTabloyaAktarim.CreateTableSql(lehce, hedefTablo, kolonlar, pk);
            if (createSql is null)
            {
                Durum.Text = $"Tablo oluşturulamıyor: {hata}";
                return null;
            }
        }
        return new YeniTabloPlani(kaynakSql, kaynakTanim, hedefProfil, kaynakDb, hedefDb, hedefTablo, kolonlar, pk,
            kestirme, neden, createSql);
    }

    private async Task YeniTabloyaAktarAsync()
    {
        if (YeniTabloPlaniKur() is not { } plan)
            return;

        string ozet = $"Kaynak: {_kaynakProfil.Ad} · {plan.KaynakDb} · {plan.KaynakTanim}\n"
            + $"Hedef: {plan.HedefProfil.Ad} · {plan.HedefDb} · {plan.HedefTablo} (YENİ)"
            + (plan.Pk.Count > 0 ? $" · 🔑 PK: {string.Join(", ", plan.Pk)}" : "") + "\n"
            + $"{plan.Kolonlar.Count} kolon kaynaktan birebir; identity/index/varsayılan taşınmaz.\n\n"
            + (plan.Kestirme is not null
                ? "Yol: aynı sunucu → tek komutla SUNUCU İÇİNDE (veri ağdan geçmez); hata olursa hedef geri alınır."
                : $"Yol: SQLST üzerinden akışlı, 500'lük partiler ({plan.KestirmeNedeni}).\n"
                  + "Önce CREATE TABLE hedefte koşar; aktarım yarıda kalırsa boş/yarım tablo hedefte KALIR ve rapor söyler.");
        if (!Iletisim.Sor(this, "SQLST — Paket Aktarım", $"{plan.HedefTablo} oluşturulup doldurulsun mu?", ozet,
                "Oluştur ve aktar"))
            return;

        string kaynakTanim = $"{_kaynakProfil.Ad} · {plan.KaynakDb} · {plan.KaynakTanim}";
        string hedefTanim = $"{plan.HedefProfil.Ad} · {plan.HedefDb} · {plan.HedefTablo} (yeni)";
        if (plan.Kestirme is { } k)
        {
            await KosVeRaporlaAsync("Paket Aktarım (yeni tablo, sunucu içi)", kaynakTanim, hedefTanim,
                (_, ct) => _aktarim.KestirmeAktarAsync(_kaynakProfil, plan.KaynakDb, k.Ifadeler, k.HedefTamAd, ct));
            return;
        }

        IReadOnlyList<AktarimEslesmesi> eslesmeler = [.. plan.Kolonlar.Select(c => new AktarimEslesmesi(c.Ad, c.Ad))];
        var istek = new AktarimIstegi(
            _kaynakProfil, plan.KaynakDb, plan.KaynakSql,
            plan.HedefProfil, plan.HedefDb, plan.HedefTablo,
            eslesmeler, OnceTemizle: false,
            HataPolitikasi.SelectedIndex == 1 ? AktarimHataPolitikasi.AtlaVeRaporla : AktarimHataPolitikasi.IlkHatadaDur,
            AktarimYazmaKipi.YalnizEkle, null, OnceDdl: plan.CreateSql);
        await KosVeRaporlaAsync("Paket Aktarım (yeni tablo)", kaynakTanim, hedefTanim,
            (ilerleme, ct) => _aktarim.AktarAsync(istek, ilerleme, ct));
    }

    /// <summary>v23-S32: yeni tablo kipinin script'ini rapor kutusunda gösterir (çalıştırmaz).</summary>
    private void ScriptGoster_Click(object sender, RoutedEventArgs e)
    {
        if (YeniTabloPlaniKur() is not { } plan)
            return;
        Rapor.Text = plan.Kestirme is { } k
            ? "-- Aynı sunucu: kaynak bağlantıda tek işlemde koşar\n" + string.Join("\n", k.Ifadeler)
            : "-- Farklı sunucu: önce hedefte CREATE TABLE, sonra kaynak SELECT akışla INSERT edilir\n"
              + plan.CreateSql + "\n\n-- kaynak:\n" + plan.KaynakSql;
        Rapor.Visibility = Visibility.Visible;
        BtnHataRaporu.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ortak koşu: düğmeler/ilerleme, sonuç bandı, günlük, rapor kutusu (v22-S16 akışı).</summary>
    private async Task KosVeRaporlaAsync(string baslik, string kaynakTanim, string hedefTanim,
        Func<IProgress<AktarimIlerleme>, CancellationToken, Task<AktarimSonucu>> kos)
    {
        AktarDugmesi.IsEnabled = false;
        DurdurDugmesi.IsEnabled = true;
        Rapor.Visibility = Visibility.Collapsed;
        BtnHataRaporu.Visibility = Visibility.Collapsed; // v22-S16: eski koşunun düğmesi kalmasın
        Ilerleme.Visibility = Visibility.Visible;
        Ilerleme.IsIndeterminate = true; // toplam bilinmiyor — akışlı okuma
        Durum.Text = "Aktarılıyor…";
        _cts = new CancellationTokenSource();
        try
        {
            var ilerleme = new Progress<AktarimIlerleme>(p =>
                Durum.Text = $"Aktarılıyor… okunan {p.Okunan:N0} · eklenen {p.Yazilan:N0}"
                    + (p.Guncellenen > 0 ? $" · güncellenen {p.Guncellenen:N0}" : "")
                    + (p.Atlanan > 0 ? $" · atlanan {p.Atlanan:N0}" : ""));
            AktarimSonucu sonuc = await kos(ilerleme, _cts.Token);

            Durum.Text = sonuc switch
            {
                { Basarili: true } => $"✔ Tamamlandı ({sonuc.Sure.TotalSeconds:F1} sn) — rapor aşağıda.",
                { IptalEdildi: true } => "■ Durduruldu — o ana dek tamamlananlar kaldı, rapor aşağıda.",
                _ => $"⚠ {sonuc.Hata}",
            };
            // v22-S16 (kullanıcı: "hataları loglayalım, nereye yazıldığını da söyleyelim"):
            // sonuç günlüğe düşer, rapor günlük yolunu söyler, hata varsa tam-rapor düğmesi çıkar.
            _sonSonuc = sonuc;
            _sonKaynakTanim = kaynakTanim;
            _sonHedefTanim = hedefTanim;
            AktarimGunlugu.Logla(baslik, _sonKaynakTanim, _sonHedefTanim, sonuc);
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
