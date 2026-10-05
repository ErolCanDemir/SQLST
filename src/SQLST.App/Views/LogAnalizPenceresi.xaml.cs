using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// Log Analizi penceresi (v20-S3 "Log Analizi 2.0"): veritabanı → tablo → mesaj kolonu (+ seviye kolonu)
/// seçilir; son 24 saat / tarih aralığı / tüm zamanlar kapsamında en çok tekrarlayan hata imzaları,
/// örneklem DEĞİL <b>GERÇEK toplam sayısıyla</b> listelenir (imza LIKE kalıbı → tek taramada sayım).
/// Seviye çipleriyle süzülür, grupta renkli seviye rozeti + İlk/Son görülme + "YENİ" işareti gösterilir;
/// bir gruba çift tık → arkasındaki HAM kayıtlar + zaman dağılımı (<see cref="LogDetayPenceresi"/>).
/// Sonuç listesinde arama, panoya kopyalama / CSV dışa aktarma ve son seçim hatırlama vardır.
/// Sorgu üretimi + gruplama SAF <see cref="LogTabloAnaliz"/>'de; pencere yalnız seçtirir/çalıştırır.
/// </summary>
public partial class LogAnalizPenceresi : Window
{
    private const int Orneklem = 20_000;   // keşif (imza öğrenme) örneklemi
    private const int DetayEnFazla = 500;  // bir grubun ham kayıt tavanı (drill-down + sparkline)

    private enum Kapsam { Son24, Aralik, Tumu }

    private readonly MotorTuru _motor;
    private readonly Func<string, Task<IReadOnlyList<SemaNesnesi>>> _tablolariGetir;
    private readonly Func<string, string, CancellationToken, Task<QueryResult>> _calistir;
    private CancellationTokenSource? _analizCts; // v20-S6: ⏹ Durdur — çalışan analizi/detayı keser
    private readonly Func<string, Task<AsistanCevabi>>? _asistanSor;
    private readonly Func<Task<(string? Db, string? Tablo, string? Kolon)>>? _sonSecimOku;
    private readonly Action<string?, string?, string?>? _secimKaydet;

    private IReadOnlyList<SemaNesnesi> _tablolar = [];
    private bool _ilkDolum = true;

    // Son çalıştırılan analizin bağlamı (gerçek-sayım + detay bunu kullanır)
    private string _veritabani = "";
    private string _hedef = "";        // SQL: TamAd · Mongo: çıplak koleksiyon
    private string _mesajKolon = "";
    private string? _zamanKolon;
    private Kapsam _kapsamModu = Kapsam.Tumu;
    private DateTime? _kapsamBas, _kapsamBit;
    private Func<Task>? _sonAnaliz;    // seviye çipi değişince yeniden koşulur

    private List<LogGrupGorunum> _tumGruplar = [];
    private bool _gercekSayimTamam;

    // Son seçim hatırlama (tek seferlik ilk yükleme tercihleri)
    private string? _hatirlaTablo, _hatirlaKolon;

    public LogAnalizPenceresi(
        MotorTuru motor,
        IReadOnlyList<string> veritabanlari,
        string? seciliVeritabani,
        Func<string, Task<IReadOnlyList<SemaNesnesi>>> tablolariGetir,
        Func<string, string, CancellationToken, Task<QueryResult>> calistir,
        Func<string, Task<AsistanCevabi>>? asistanSor = null,
        Func<Task<(string?, string?, string?)>>? sonSecimOku = null,
        Action<string?, string?, string?>? secimKaydet = null)
    {
        _asistanSor = asistanSor;
        _sonSecimOku = sonSecimOku;
        _secimKaydet = secimKaydet;

        InitializeComponent();
        if (_asistanSor is null)
        {
            AiOzetDugmesi.Visibility = Visibility.Collapsed;
            AiOzetGenisletici.Visibility = Visibility.Collapsed;
        }
        _motor = motor;
        _tablolariGetir = tablolariGetir;
        _calistir = calistir;

        if (_motor == MotorTuru.Mongo)
        {
            TabloEtiketi.Text = "Koleksiyon:";
            KolonEtiketi.Text = "Mesaj alanı:";
            SeviyeEtiketi.Text = "Seviye alanı:";
            LogNotu.Text = "Not: Bu ekranı exception/log KOLEKSİYONLARI için kullanınız. Koleksiyonu ve mesaj "
                + "alanını seçin — en çok tekrarlayan kayıtlar GERÇEK toplam sayısıyla listelenir. Bir gruba "
                + "çift tıklayarak arkasındaki ham belgeleri ve zaman dağılımını görebilirsiniz.";
            Ozet.Text = "Veritabanı, koleksiyon ve mesaj alanını seçip “Analiz et”e basın.";
        }

        VeritabaniKutusu.ItemsSource = veritabanlari;
        VeritabaniKutusu.SelectedItem =
            seciliVeritabani is not null && veritabanlari.Contains(seciliVeritabani)
                ? seciliVeritabani
                : veritabanlari.FirstOrDefault();

        Loaded += async (_, _) =>
        {
            _ilkDolum = false;
            try
            {
                // Son seçim hatırlama: kayıtlı DB/tablo/kolon varsa ön-seç (yalnız ilk açılışta).
                if (_sonSecimOku is not null)
                {
                    (string? db, string? tablo, string? kolon) = await _sonSecimOku();
                    _hatirlaTablo = tablo;
                    _hatirlaKolon = kolon;
                    if (db is not null && VeritabaniKutusu.Items.Contains(db))
                        VeritabaniKutusu.SelectedItem = db;
                }
                await TablolariYukleAsync();
            }
            catch (Exception ex) { Ozet.Text = $"Tablolar yüklenemedi: {ex.Message}"; }
        };
    }

    private async void Veritabani_Secildi(object sender, RoutedEventArgs e)
    {
        if (_ilkDolum)
            return;
        TabloKutusu.ItemsSource = null;
        KolonKutusu.ItemsSource = null;
        SeviyeKolonuKutusu.ItemsSource = null;
        Grid.ItemsSource = null;
        try { await TablolariYukleAsync(); }
        catch (Exception ex) { Ozet.Text = $"Tablolar yüklenemedi: {ex.Message}"; }
    }

    private async Task TablolariYukleAsync()
    {
        if (VeritabaniKutusu.SelectedItem is not string veritabani)
        {
            Ozet.Text = "Analiz edilecek veritabanı seçin.";
            return;
        }

        _tablolar = await _tablolariGetir(veritabani);
        TabloKutusu.ItemsSource = _tablolar.Select(t => t.TamAd).ToList();

        // Hatırlanan tablo varsa onu, yoksa adı log/exception çağrıştıran ilk tabloyu ön-seç.
        string? aday = _hatirlaTablo is { } ht && _tablolar.Any(t => t.TamAd == ht)
            ? ht
            : _tablolar.Select(t => t.TamAd).FirstOrDefault(ad =>
                ad.Contains("log", StringComparison.OrdinalIgnoreCase)
                || ad.Contains("exception", StringComparison.OrdinalIgnoreCase)
                || ad.Contains("hata", StringComparison.OrdinalIgnoreCase));
        _hatirlaTablo = null; // tek seferlik
        if (aday is not null)
            TabloKutusu.SelectedItem = aday;
        else if (_tablolar.Count == 0)
            Ozet.Text = $"{veritabani} içinde tablo bulunamadı (şema henüz yüklenmemiş olabilir).";
    }

    private async void Tablo_Secildi(object sender, RoutedEventArgs e)
    {
        SemaNesnesi? tablo = _tablolar.FirstOrDefault(t => t.TamAd == TabloKutusu.SelectedItem as string);
        if (tablo is null)
            return;

        // v19-S8: Mongo'da alanlar faz-1 önbelleğinde boştur → seçilen koleksiyondan hafif find(limit 50).
        IReadOnlyList<SemaKolonu> kolonlar = tablo.Kolonlar;
        if (kolonlar.Count == 0 && _motor == MotorTuru.Mongo
            && VeritabaniKutusu.SelectedItem is string veritabani)
        {
            KolonKutusu.ItemsSource = new[] { "alanlar yükleniyor…" };
            KolonKutusu.SelectedIndex = 0;
            KolonKutusu.IsEnabled = false;
            try
            {
                QueryResult sonuc = await _calistir(veritabani,
                    $$"""{ "find": "{{tablo.Ad.Replace("\"", "\\\"")}}", "limit": 50 }""", CancellationToken.None);
                kolonlar = sonuc.Basarili && sonuc.ResultSetler.Count > 0
                    ? [.. sonuc.ResultSetler[0].Kolonlar.Select(k =>
                        new SemaKolonu(k.Ad, "string", true, false))]
                    : [];
                if (kolonlar.Count == 0)
                    Ozet.Text = sonuc.Hata is not null
                        ? $"⚠ Alanlar okunamadı: {sonuc.Hata.Mesaj}"
                        : "⚠ Koleksiyon boş görünüyor — alan listesi çıkarılamadı (başka koleksiyon seçin).";
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                kolonlar = [];
                Ozet.Text = $"⚠ Alanlar okunamadı: {ex.Message}";
            }
            finally { KolonKutusu.IsEnabled = true; }

            if (TabloKutusu.SelectedItem as string != tablo.TamAd)
                return;
        }

        List<string> adlar = [.. kolonlar.Select(k => k.Ad)];
        KolonKutusu.ItemsSource = adlar;
        KolonKutusu.SelectedItem = _hatirlaKolon is { } hk && adlar.Contains(hk)
            ? hk
            : LogTabloAnaliz.MesajKolonuTahmini(kolonlar);
        _hatirlaKolon = null; // tek seferlik

        // Seviye kolonu: "(yok)" + kolonlar; ada göre otomatik tahmin.
        SeviyeKolonuKutusu.ItemsSource = new[] { "(yok)" }.Concat(adlar).ToList();
        SeviyeKolonuKutusu.SelectedItem = LogTabloAnaliz.SeviyeKolonuTahmini(kolonlar) ?? "(yok)";
    }

    private void SeviyeKolonu_Secildi(object sender, RoutedEventArgs e)
        => SuzgecSatiri.Visibility = SeviyeKolonuAl() is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Seçili seviye kolonu (SQL kolon / Mongo alan); "(yok)"/boş ise null.</summary>
    private string? SeviyeKolonuAl()
        => SeviyeKolonuKutusu.SelectedItem as string is { Length: > 0 } s && s != "(yok)" ? s : null;

    /// <summary>İşaretli seviye çipleri → HAM seviye değer listesi; hiçbiri işaretli değilse null (tümü).</summary>
    private IReadOnlyList<string>? SeciliSeviyeDegerleri()
    {
        List<string> kovalar = [];
        if (CipHata.IsChecked == true) kovalar.Add("Hata");
        if (CipUyari.IsChecked == true) kovalar.Add("Uyarı");
        if (CipBilgi.IsChecked == true) kovalar.Add("Bilgi");
        return kovalar.Count == 0 ? null : LogTabloAnaliz.SeviyeDegerleriCoklu(kovalar);
    }

    private async void SeviyeCipi_Degisti(object sender, RoutedEventArgs e)
    {
        if (_sonAnaliz is not null)
            await _sonAnaliz();  // seviye süzgeci sorgunun parçası → yeniden koş
    }

    /// <summary>Ortak seçim doğrulaması: DB + tablo + kolon; şema nesnesi ve Mongo için ÇIPLAK ad.</summary>
    private (string Veritabani, string Tablo, string Kolon, SemaNesnesi? Nesne, string Hedef)? SecimAl()
    {
        if (VeritabaniKutusu.SelectedItem is not string veritabani)
        {
            Ozet.Text = "Önce veritabanını seçin.";
            return null;
        }
        string? tablo = TabloKutusu.SelectedItem as string;
        string? kolon = KolonKutusu.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(tablo) || string.IsNullOrWhiteSpace(kolon))
        {
            Ozet.Text = "Önce tabloyu ve mesaj kolonunu seçin.";
            return null;
        }
        SemaNesnesi? nesne = _tablolar.FirstOrDefault(t => t.TamAd == tablo);
        string hedef = _motor == MotorTuru.Mongo ? nesne?.Ad ?? tablo : tablo;
        return (veritabani, tablo, kolon, nesne, hedef);
    }

    private async void Analiz_Click(object sender, RoutedEventArgs e) => await Son24Analiz();
    private async void AralikAnaliz_Click(object sender, RoutedEventArgs e) => await AralikAnaliz();

    /// <summary>Günlük panel: SON 24 SAAT (zaman kolonu otomatik; yoksa tüm tablo + açık bildirim).</summary>
    private async Task Son24Analiz()
    {
        if (SecimAl() is not { } s)
            return;
        _sonAnaliz = Son24Analiz;
        string? zamanKolonu = s.Nesne is null ? null : LogTabloAnaliz.ZamanKolonuTahmini(s.Nesne.Kolonlar);
        BaglamKur(s, zamanKolonu, zamanKolonu is null ? Kapsam.Tumu : Kapsam.Son24, null, null);

        string? sevKol = SeviyeKolonuAl();
        IReadOnlyList<string>? sevDeg = SeciliSeviyeDegerleri();
        string sql = zamanKolonu is null
            ? LogTabloAnaliz.OrneklemSorgusu(_motor, s.Hedef, s.Kolon, Orneklem, null, sevKol, sevDeg)
            : LogTabloAnaliz.Orneklem24SaatSorgusu(_motor, s.Hedef, s.Kolon, zamanKolonu, Orneklem, sevKol, sevDeg);
        string kapsam = zamanKolonu is null
            ? "zaman kolonu bulunamadı — tüm tablodan örneklendi"
            : "son 24 saat";
        string durum = $"{s.Veritabani} · {s.Tablo} — {(zamanKolonu is null ? "tüm tablo" : "son 24 saat")} okunuyor…";
        await AnalizKos(sql, durum, kapsam, AnalizDugmesi);
    }

    /// <summary>Tüm Zamanlar paneli: tarih aralığı ya da (iki tarih boşsa) tüm tablo örneklemi.</summary>
    private async Task AralikAnaliz()
    {
        if (SecimAl() is not { } s)
            return;
        _sonAnaliz = AralikAnaliz;
        string? zamanKolonu = s.Nesne is null ? null : LogTabloAnaliz.ZamanKolonuTahmini(s.Nesne.Kolonlar);
        string? sevKol = SeviyeKolonuAl();
        IReadOnlyList<string>? sevDeg = SeciliSeviyeDegerleri();

        DateTime? bas = BaslangicTarihi.SelectedDate, bit = BitisTarihi.SelectedDate;
        if (bas is null && bit is null)
        {
            BaglamKur(s, zamanKolonu, Kapsam.Tumu, null, null);
            await AnalizKos(
                LogTabloAnaliz.OrneklemSorgusu(_motor, s.Hedef, s.Kolon, Orneklem, zamanKolonu, sevKol, sevDeg),
                $"{s.Veritabani} · {s.Tablo} — tüm tablodan ilk {Orneklem:N0} kayıt okunuyor…",
                "tüm zamanlar", AralikAnalizDugmesi);
            return;
        }
        if (bas is null || bit is null)
        {
            Ozet.Text = "Aralık için iki tarihi de seçin (ya da ikisini de boş bırakın = tüm zamanlar).";
            return;
        }
        if (bit < bas)
        {
            Ozet.Text = "Bitiş tarihi başlangıçtan önce olamaz.";
            return;
        }
        if (zamanKolonu is null)
        {
            Ozet.Text = $"{s.Tablo} içinde tarih/zaman kolonu bulunamadı — tarih aralığı uygulanamıyor. "
                + "(İki tarihi boş bırakırsanız tüm tablo örneklenir.)";
            return;
        }

        DateTime basT = bas.Value.Date, bitT = bit.Value.Date.AddDays(1); // bitiş günü DAHİL
        BaglamKur(s, zamanKolonu, Kapsam.Aralik, basT, bitT);
        string aralikMetni = $"{basT:dd.MM.yyyy} – {bit.Value:dd.MM.yyyy}";
        await AnalizKos(
            LogTabloAnaliz.OrneklemAralikSorgusu(_motor, s.Hedef, s.Kolon, zamanKolonu, basT, bitT, Orneklem, sevKol, sevDeg),
            $"{s.Veritabani} · {s.Tablo} — {aralikMetni} okunuyor…", aralikMetni, AralikAnalizDugmesi);
    }

    private void BaglamKur((string Veritabani, string Tablo, string Kolon, SemaNesnesi? Nesne, string Hedef) s,
        string? zamanKolonu, Kapsam kapsam, DateTime? bas, DateTime? bit)
    {
        _veritabani = s.Veritabani;
        _hedef = s.Hedef;
        _mesajKolon = s.Kolon;
        _zamanKolon = zamanKolonu;
        _kapsamModu = kapsam;
        _kapsamBas = bas;
        _kapsamBit = bit;
    }

    /// <summary>
    /// Ortak koşu (v20-S6): keşif → gruplama → örneklem sonuçlarını HEMEN göster → GERÇEK sayımı arkadan
    /// getirip güncelle. Süre SINIRSIZ (dev tabloda tarama uzun sürer, normaldir); "⏹ Durdur" çalışan
    /// sorguyu ANINDA keser — o an örneklem sonuçları ekranda kalır.
    /// </summary>
    private async Task AnalizKos(string sql, string durum, string kapsam, Button dugme)
    {
        CancellationToken ct = IslemBaslat();
        dugme.IsEnabled = false;
        Ozet.Text = durum;
        Grid.ItemsSource = null;
        _tumGruplar = [];
        try
        {
            QueryResult sonuc = await _calistir(_veritabani, sql, ct);
            if (sonuc.IptalEdildi) { Ozet.Text = "Analiz durduruldu."; return; }
            if (sonuc.Hata is { } hata) { Ozet.Text = $"Sorgu hatası: {hata.Mesaj}"; return; }
            if (sonuc.ResultSetler.Count == 0) { Ozet.Text = "Sonuç dönmedi."; return; }

            ResultSetData rs = sonuc.ResultSetler[0];
            int mesajIdx = Math.Max(0, KolonIndeksi(rs.Kolonlar, _mesajKolon));
            int zamanIdx = _zamanKolon is null ? -1 : KolonIndeksi(rs.Kolonlar, _zamanKolon);
            string? sevKol = SeviyeKolonuAl();
            int sevIdx = sevKol is null ? -1 : KolonIndeksi(rs.Kolonlar, sevKol);

            IReadOnlyList<TabloLogGrubu> gruplar = LogTabloAnaliz.Grupla(
                rs.Satirlar.Select(satir => (
                    Mesaj: satir.Length > mesajIdx ? satir[mesajIdx] : null,
                    Zaman: zamanIdx >= 0 && satir.Length > zamanIdx ? satir[zamanIdx] : null,
                    Seviye: sevIdx >= 0 && satir.Length > sevIdx ? satir[sevIdx] : null)),
                enFazla: 50);

            DateTime esik = DateTime.Now.AddHours(-24);
            GruplariGoster(gruplar, esik);   // örneklem sonuçları HEMEN görünsün
            KaydetSecim();

            if (_tumGruplar.Count == 0)
            {
                Ozet.Text = $"{sonuc.ToplamSatir:N0} kayıt okundu ({kapsam}) — gruplanacak metin bulunamadı (kolon boş olabilir).";
                return;
            }
            Ozet.Text = $"{_tumGruplar.Count} grup · {kapsam} — örneklem gösteriliyor, gerçek toplam hesaplanıyor… (⏹ Durdur ile örneklemle kalırsınız)";

            // GERÇEK sayım (dev tabloda uzun sürebilir; iptal → örneklem sonuçları korunur).
            IReadOnlyList<TabloLogGrubu> gercek = await GercekSayimUygula(gruplar, ct);
            GruplariGoster(gercek, esik);
            Ozet.Text = $"{_tumGruplar.Count} grup · {kapsam}. En çok: “{Kisalt(_tumGruplar[0].OrnekMesaj)}” — {_tumGruplar[0].Sayi:N0} kez"
                + (_gercekSayimTamam ? " (gerçek toplam)." : $" (örneklem — ilk {Orneklem:N0} kayıt; gerçek sayım durduruldu/alınamadı).");
        }
        catch (OperationCanceledException) { Ozet.Text = "Analiz durduruldu (örneklem gösteriliyor)."; }
        finally { dugme.IsEnabled = true; IslemBitti(); }
    }

    /// <summary>Grupları görünüm sarmalayıcısına çevirip ("YENİ" bayrağıyla) grid'e süzerek yansıtır.</summary>
    private void GruplariGoster(IReadOnlyList<TabloLogGrubu> gruplar, DateTime yeniEsik)
    {
        _tumGruplar = [.. gruplar.Select(g => new LogGrupGorunum(g, g.IlkGorulme is { } ig && ig >= yeniEsik))];
        SuzUygula();
    }

    /// <summary>
    /// GERÇEK sayım geçişi: örneklemde bulunan imzaların LIKE kalıplarını TEK taramada sayar,
    /// grup sayılarını gerçek toplamla değiştirir ve yeniden sıralar. İptal/başarısızlıkta (dev tabloda
    /// LIKE full-scan çok uzun sürer → Durdur) örneklem sayıları korunur — sessizce düşmez, özette belirtilir.
    /// </summary>
    private async Task<IReadOnlyList<TabloLogGrubu>> GercekSayimUygula(IReadOnlyList<TabloLogGrubu> gruplar, CancellationToken ct)
    {
        _gercekSayimTamam = false;
        List<TabloLogGrubu> desenli = [.. gruplar.Where(g => !string.IsNullOrEmpty(g.Desen))];
        if (desenli.Count == 0)
            return gruplar;
        try
        {
            string sql = LogTabloAnaliz.GercekSayimSorgusu(
                _motor, _hedef, _mesajKolon, [.. desenli.Select(g => g.Desen!)],
                _zamanKolon, _kapsamModu == Kapsam.Son24, _kapsamBas, _kapsamBit,
                SeviyeKolonuAl(), SeciliSeviyeDegerleri());
            QueryResult r = await _calistir(_veritabani, sql, ct);
            if (r.IptalEdildi || !r.Basarili || r.ResultSetler.Count == 0 || r.ResultSetler[0].Satirlar.Count == 0)
                return gruplar;

            ResultSetData rs = r.ResultSetler[0];
            object?[] satir = rs.Satirlar[0];
            List<TabloLogGrubu> yeni = new(desenli.Count);
            for (int i = 0; i < desenli.Count; i++)
            {
                int idx = KolonIndeksi(rs.Kolonlar, $"c{i}");
                int sayi = idx >= 0 && satir.Length > idx ? KonvInt(satir[idx]) : desenli[i].Sayi;
                yeni.Add(desenli[i] with { Sayi = sayi, GercekSayimMi = true });
            }
            _gercekSayimTamam = true;
            return [.. yeni.OrderByDescending(g => g.Sayi).ThenBy(g => g.Imza, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            return gruplar; // örneklem sayılarıyla devam
        }
    }

    /// <summary>Yeni kesilebilir işlem başlatır (öncekini iptal eder), "⏹ Durdur"u gösterir, token döner.</summary>
    private CancellationToken IslemBaslat()
    {
        IptalYardimcisi.ArkaPlandaIptal(_analizCts, birak: true); // m.15: Cancel UI'da bloklayabilir
        _analizCts = new CancellationTokenSource();
        DurdurDugmesi.Visibility = Visibility.Visible;
        return _analizCts.Token;
    }

    private void IslemBitti() => DurdurDugmesi.Visibility = Visibility.Collapsed;

    private void Durdur_Click(object sender, RoutedEventArgs e)
    {
        IptalYardimcisi.ArkaPlandaIptal(_analizCts); // m.15: Cancel UI'da bloklayabilir — havuzda
        Ozet.Text = "Durduruluyor…";
    }

    /// <summary>Sonuç listesini arama metnine göre süzer (istemci tarafı; seviye zaten sorguda).</summary>
    private void SuzUygula()
    {
        string q = SonucSuz.Text?.Trim() ?? "";
        IEnumerable<LogGrupGorunum> gor = _tumGruplar;
        if (q.Length > 0)
            gor = gor.Where(g => g.OrnekMesaj.Contains(q, StringComparison.OrdinalIgnoreCase));
        Grid.ItemsSource = gor.ToList();
    }

    private void SonucSuz_Degisti(object sender, TextChangedEventArgs e) => SuzUygula();

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => DetayAc();
    private void Detay_Click(object sender, RoutedEventArgs e) => DetayAc();

    /// <summary>Seçili grubun LIKE kalıbına uyan HAM kayıtları getirip <see cref="LogDetayPenceresi"/>'nde açar.</summary>
    private async void DetayAc()
    {
        if (Grid.SelectedItem is not LogGrupGorunum g || string.IsNullOrEmpty(g.Desen))
            return;
        CancellationToken ct = IslemBaslat();  // dev tabloda detay taraması da uzun sürebilir → Durdur'la kesilir
        DetayDugmesi.IsEnabled = false;
        Ozet.Text = "Ham kayıtlar getiriliyor… (⏹ Durdur ile iptal)";
        try
        {
            string sql = LogTabloAnaliz.DetaySorgusu(
                _motor, _hedef, _mesajKolon, g.Desen!, DetayEnFazla,
                _zamanKolon, _kapsamModu == Kapsam.Son24, _kapsamBas, _kapsamBit,
                SeviyeKolonuAl(), SeciliSeviyeDegerleri());
            QueryResult r = await _calistir(_veritabani, sql, ct);
            if (r.IptalEdildi) { Ozet.Text = "Detay durduruldu."; return; }
            if (r.Hata is { } h) { Ozet.Text = $"Detay hatası: {h.Mesaj}"; return; }
            if (r.ResultSetler.Count == 0 || r.ResultSetler[0].Satirlar.Count == 0)
            {
                Ozet.Text = "Bu grubun ham kaydı gelmedi (kapsam dışında olabilir).";
                return;
            }
            new LogDetayPenceresi(Kisalt(g.OrnekMesaj), g.Sayi, r.ResultSetler[0], _zamanKolon).Show();
            Ozet.Text = $"“{Kisalt(g.OrnekMesaj)}” — {r.ResultSetler[0].Satirlar.Count:N0} ham kayıt açıldı.";
        }
        catch (OperationCanceledException) { Ozet.Text = "Detay durduruldu."; }
        finally { DetayDugmesi.IsEnabled = true; IslemBitti(); }
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.ItemsSource is not IReadOnlyList<LogGrupGorunum> g || g.Count == 0)
        {
            Ozet.Text = "Kopyalanacak sonuç yok.";
            return;
        }
        try { Clipboard.SetText(SatirMetni(g, '\t')); Ozet.Text = $"{g.Count} satır panoya kopyalandı."; }
        catch (Exception ex) { Ozet.Text = $"Kopyalanamadı: {ex.Message}"; }
    }

    private void Csv_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.ItemsSource is not IReadOnlyList<LogGrupGorunum> g || g.Count == 0)
        {
            Ozet.Text = "Aktarılacak sonuç yok.";
            return;
        }
        var kutu = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV dosyası (*.csv)|*.csv",
            FileName = "log-analizi.csv",
        };
        if (kutu.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(kutu.FileName, SatirMetni(g, ',', csv: true), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Ozet.Text = $"{g.Count} satır CSV'ye aktarıldı: {kutu.FileName}";
        }
        catch (Exception ex) { Ozet.Text = $"CSV yazılamadı: {ex.Message}"; }
    }

    private static string SatirMetni(IReadOnlyList<LogGrupGorunum> gruplar, char ayrac, bool csv = false)
    {
        var sb = new StringBuilder();
        string[] baslik = ["Seviye", "Kez", "Ilk gorulme", "Son gorulme", "Yeni", "Ornek mesaj"];
        sb.AppendLine(string.Join(ayrac, csv ? baslik.Select(h => Alan(h, ayrac, csv)) : baslik));
        foreach (LogGrupGorunum g in gruplar)
        {
            string[] hucreler =
            [
                g.Seviye ?? "",
                g.Sayi.ToString(),
                g.IlkGorulme?.ToString("dd.MM.yyyy HH:mm") ?? "",
                g.SonGorulme?.ToString("dd.MM.yyyy HH:mm") ?? "",
                g.Yeni ? "YENI" : "",
                TekSatirMetin(g.OrnekMesaj),
            ];
            sb.AppendLine(string.Join(ayrac, hucreler.Select(h => Alan(h, ayrac, csv))));
        }
        return sb.ToString();
    }

    private static string Alan(string s, char ayrac, bool csv)
    {
        if (!csv)
            return s;
        return s.Contains(ayrac) || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    private static string TekSatirMetin(string s)
        => s.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ").Replace("\r", " ⏎ ").Replace('\t', ' ');

    private void KaydetSecim()
        => _secimKaydet?.Invoke(_veritabani,
            TabloKutusu.SelectedItem as string, KolonKutusu.SelectedItem as string);

    private static int KolonIndeksi(IReadOnlyList<KolonBilgisi> kolonlar, string ad)
    {
        for (int i = 0; i < kolonlar.Count; i++)
            if (string.Equals(kolonlar[i].Ad, ad, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static int KonvInt(object? d)
    {
        try { return d is null or DBNull ? 0 : (int)Math.Min(int.MaxValue, Convert.ToInt64(d)); }
        catch { return 0; }
    }

    private static string Kisalt(string s) => s.Length <= 90 ? s : s[..90] + "…";

    private async void AiOzetle_Click(object sender, RoutedEventArgs e)
    {
        if (_asistanSor is null)
            return;
        if (Grid.ItemsSource is not IReadOnlyList<LogGrupGorunum> gruplar || gruplar.Count == 0)
        {
            Ozet.Text = "Özetlenecek grup yok — önce Analiz et.";
            return;
        }
        AiOzetDugmesi.IsEnabled = false;
        AiOzetAlani.Text = "🤖 Model düşünüyor…";
        AiOzetGenisletici.IsExpanded = true;
        try
        {
            string metin = string.Join("\n", gruplar.Take(25).Select(g => $"{g.Sayi}× {g.OrnekMesaj}"));
            AsistanCevabi cevap = await _asistanSor(AsistanIstemleri.LogOzetle(metin));
            AiOzetAlani.Text = cevap.Metin;
        }
        finally { AiOzetDugmesi.IsEnabled = true; }
    }
}

/// <summary>Grid satırı: çekirdek <see cref="TabloLogGrubu"/> + "YENİ" bayrağı (son 24 saatte ilk görülen).</summary>
public sealed record LogGrupGorunum(TabloLogGrubu Grup, bool Yeni)
{
    private IReadOnlyList<string>? _ekDegerler;

    public int Sayi => Grup.Sayi;
    public string? Seviye => Grup.Seviye;
    public DateTime? IlkGorulme => Grup.IlkGorulme;
    public DateTime? SonGorulme => Grup.SonGorulme;
    public string OrnekMesaj => Grup.OrnekMesaj;
    public string? Desen => Grup.Desen;
    public Visibility YeniGorunur => Yeni ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Ek kolon değerleri (v22-S12) — başlık sırasıyla, null → "". Grid'e kod-arkasından
    /// eklenen dinamik kolonlar <c>EkDegerler[i]</c> yoluyla bağlanır; çalışma tipi bilerek
    /// List&lt;string&gt; (WPF indeksli binding yolunun sorunsuz çözdüğü tip).</summary>
    public IReadOnlyList<string> EkDegerler
        => _ekDegerler ??= (Grup.EkDegerler ?? []).Select(v => v ?? "").ToList();
}
