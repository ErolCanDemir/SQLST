using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Views;

/// <summary>
/// REST yanıtını tabloya kaydetmek (v20-S12) için gereken DB bağımlılıkları — bağlı profil, aktif
/// veritabanı, motor lehçesi ve Excel/TXT içe aktarımıyla PAYLAŞILAN <see cref="DosyaAktarimServisi"/>.
/// null ise "Tabloya kaydet" düğmesi gizlenir (bağlı profil yok — ör. depo köprüsüz test).
/// </summary>
public sealed record RestTabloKaydiBaglami(
    ConnectionProfile Profil,
    string? Veritabani,
    ILehce Lehce,
    DosyaAktarimServisi Aktarim);

// v22-S1 (saha turu-2 m.10): RestTopluKaynak KALDIRILDI — "sorgu sonucu → REST toplu istek"
// özelliği kullanıcı isteğiyle tümüyle silindi ("işlevli bir kullanışı yok").

/// <summary>
/// REST/HTTP test istemcisi (v20-S8) — SOAP istemcisinin kardeşi. Method + URL + Params/Başlıklar/Gövde +
/// Basic/Bearer → Gönder → durum/süre/boyut + renklendirilmiş JSON yanıt. Geçmiş + kayıtlı istekler
/// (sağ tıkla sil), <b>JSON Olarak Aç</b> (yanıtı ayrı pencerede), <b>URL'i Kopyala / Kopyalanmış URL'i
/// Aç</b>, <b>AI Yorumla</b>. Kurumsal proxy + localhost muafiyeti <see cref="RestIstemcisi"/>'nden.
/// "⏹ Durdur" çalışan isteği anında keser; depo yoksa pencere yine çalışır.
/// </summary>
public partial class RestIstemciPenceresi : Window
{
    private readonly RestIstemcisi _istemci;
    private readonly IRestDeposu? _depo;
    private readonly Func<string, string, int, Task<AsistanCevabi>>? _aiYorumla;
    private readonly RestTabloKaydiBaglami? _tabloBaglami; // v20-S12: yanıtı tabloya kaydet
    private readonly Func<string, Task<string>>? _scalarSorgu; // v20-S13: değişkeni SQL'den çöz (ilk hücre)
    private readonly ObservableCollection<RestDegiskenGorunum> _degiskenler = []; // {{Ad}} → Değer
    private IReadOnlyList<RestOrtam> _ortamlar = []; // v20-S13 madde 8: kayıtlı ortamlar (dev/test/prod)
    private bool _ortamSecimBastir; // programatik seçimde grid'i yeniden yükleme (kullanıcının Sql sütunu korunsun)
    // v20-S13: "Yanıt ↔ DB karşılaştır" için DB tarafını (kolon adları + satırlar) getiren sorgu.
    private readonly Func<string, Task<(IReadOnlyList<string> Kolonlar, IReadOnlyList<object?[]> Satirlar)>>? _dbSorgu;
    private readonly Func<string, Task<AsistanCevabi>>? _aiIstekUret; // v20-S13: tarifle → istek üret
    private readonly Func<string, Task<AsistanCevabi>>? _aiSemaOner;  // v20-S13 madde 6: CREATE TABLE öner (tasarımcıya iletilir)
    private readonly Func<string, string, Task<AsistanCevabi>>? _aiDiff; // v20-S13 madde 7: iki yanıtı diff yorumla
    private string? _sabitliAnlik; // madde 7: "📌 Sabitle" ile alınan ÖNCEKİ yanıt anlık görüntüsü

    private readonly ObservableCollection<RestSatirGorunum> _params = [];
    private readonly ObservableCollection<RestSatirGorunum> _basliklar = [];
    /// <summary>m.21 Body anahtar/değer satırları — JSON ile iki yönlü eşitlenir.</summary>
    private readonly ObservableCollection<RestSatirGorunum> _govdeKv = [];
    private bool _govdeEsitleniyor; // tablo→JSON→tablo döngüsünü kırar
    private readonly Dictionary<string, string> _aktifDegiskenler = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private RestCevap? _sonYanit;
    private RestIstek? _sonResolved;

    private sealed record ListeOge(string Metin, object Model)
    {
        public string Grup { get; init; } = ""; // v20-S13 madde 11: koleksiyon (ad önekinden)
        public override string ToString() => Metin;
    }

    public RestIstemciPenceresi(
        RestIstemcisi istemci,
        IRestDeposu? depo = null,
        Func<string, string, int, Task<AsistanCevabi>>? aiYorumla = null,
        RestTabloKaydiBaglami? tabloBaglami = null,
        Func<string, Task<string>>? scalarSorgu = null,
        Func<string, Task<(IReadOnlyList<string> Kolonlar, IReadOnlyList<object?[]> Satirlar)>>? dbSorgu = null,
        Func<string, Task<AsistanCevabi>>? aiIstekUret = null,
        Func<string, Task<AsistanCevabi>>? aiSemaOner = null,
        Func<string, string, Task<AsistanCevabi>>? aiDiff = null)
    {
        _istemci = istemci;
        _depo = depo;
        _aiYorumla = aiYorumla;
        _tabloBaglami = tabloBaglami;
        _scalarSorgu = scalarSorgu;
        _dbSorgu = dbSorgu;
        _aiIstekUret = aiIstekUret;
        _aiSemaOner = aiSemaOner;
        _aiDiff = aiDiff;

        InitializeComponent();

        Metod.ItemsSource = Enum.GetValues<HttpMetodu>();
        Metod.SelectedItem = HttpMetodu.GET;
        KimlikTuru.ItemsSource = new[] { "Yok", "Basic", "Bearer", "API Key" };
        KimlikTuru.SelectedIndex = 0;
        ApiKeyKonum.ItemsSource = new[] { "Başlık", "Query" };
        ApiKeyKonum.SelectedIndex = 0;
        ParamGrid.ItemsSource = _params;
        BaslikGrid.ItemsSource = _basliklar;
        DegiskenGrid.ItemsSource = _degiskenler;
        GovdeKvGrid.ItemsSource = _govdeKv;

        JsonTemasiUygula(Govde);
        JsonTemasiUygula(Yanit);

        // m.21: Body iki yönlü eşitleme — tablo değişince JSON, JSON değişince (düz nesneyse) tablo.
        // AI üret / cURL yapıştır / kayıt yükle Govde.Text yazdığında da tablo kendiliğinden dolar.
        _govdeKv.CollectionChanged += (_, _) => GovdeKvdenJsonaYaz();
        Govde.TextChanged += (_, _) => GovdeJsondanTabloya();

        // v23 Postman düzeni: URL ↔ Params ÇİFT YÖNLÜ senkron (RestUrlSenkron) — satır
        // eklenince/silinince URL tazelenir; hücre düzenlemesi Param_HucreBitti'den gelir.
        _params.CollectionChanged += (_, _) => ParamlardanUrle();

        if (_aiYorumla is null)
            AiYorumlaDugmesi.Visibility = Visibility.Collapsed;
        if (_depo is null)
        {
            IstekKaydetDugmesi.Visibility = Visibility.Collapsed;
            OrtamPaneli.Visibility = Visibility.Collapsed; // ortamlar (dev/test/prod) depo gerektirir
        }
        if (_tabloBaglami is null)
            TabloKaydetDugmesi.Visibility = Visibility.Collapsed; // bağlı profil yok → tabloya kaydedilemez
        if (_scalarSorgu is null)
            DegiskenCozDugmesi.IsEnabled = false; // bağlı SQL profili yok → yalnız literal değişken kullanılır
        if (_dbSorgu is null)
            DbKarsilastirDugmesi.Visibility = Visibility.Collapsed; // bağlı SQL profili yok → karşılaştırılamaz

        Loaded += async (_, _) =>
        {
            try { await GecmisiYukleAsync(); await KayitliYukleAsync(); await OrtamlariYukleAsync(); }
            catch (Exception ex) { DurumMetni.Text = $"Yükleme hatası: {ex.Message}"; }
        };
    }

    // ── m.21: Body anahtar/değer ⇄ JSON eşitlemesi ──────────────────────────────────────────

    /// <summary>Hücre düzenlemesi bitince JSON'u tazeler (commit DataGrid'de olaydan SONRA işler —
    /// BeginInvoke ile değerin bağlanmış nesneye yazılması beklenir).</summary>
    private void GovdeKv_HucreBitti(object sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(GovdeKvdenJsonaYaz);

    private void GovdeKvdenJsonaYaz()
    {
        if (_govdeEsitleniyor)
            return;
        _govdeEsitleniyor = true;
        try
        {
            Govde.Text = _govdeKv.All(s => string.IsNullOrWhiteSpace(s.Anahtar))
                ? "" // tablo boşsa gövdeyi boş bırak — "{}" gönderilmesin
                : RestGovdeDonusturucu.KvdenJson(_govdeKv.Select(s => (s.Anahtar, s.Deger)));
        }
        finally
        {
            _govdeEsitleniyor = false;
        }
    }

    private void GovdeJsondanTabloya()
    {
        if (_govdeEsitleniyor)
            return;
        IReadOnlyList<(string Anahtar, string Deger)>? satirlar = RestGovdeDonusturucu.JsondanKv(Govde.Text);
        if (satirlar is null)
            return; // düz nesne değil / yarım JSON — tabloya dokunma, editör esas
        _govdeEsitleniyor = true;
        try
        {
            _govdeKv.Clear();
            foreach ((string anahtar, string deger) in satirlar)
                _govdeKv.Add(new RestSatirGorunum { Anahtar = anahtar, Deger = deger });
        }
        finally
        {
            _govdeEsitleniyor = false;
        }
    }

    /// <summary>AvalonEdit'e JSON vurgusu + tema-farkında arka/ön plan (fırçalar DynamicResource almaz).</summary>
    private static void JsonTemasiUygula(TextEditor ed)
    {
        bool koyu = App.KoyuTemaAcik;
        ed.SyntaxHighlighting = EditorTema.JsonTanim(koyu);
        ed.Background = koyu ? new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1a)) : Brushes.White;
        ed.Foreground = koyu ? new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xD6)) : Brushes.Black;
        ed.Options.HighlightCurrentLine = false;
    }

    // ── v23: URL ↔ Params çift yönlü senkron (Postman Params davranışı) ─────────────────────

    private bool _urlEsitleniyor; // URL→grid→URL döngüsünü kırar (Govde eşitlemesiyle aynı desen)

    /// <summary>URL kutusu değiştikçe Params grid'i tazelenir: aktif satırlar URL'nin query'sine
    /// eşitlenir (anahtarı eşleşen satırın AÇIKLAMASI korunur), ✓ kaldırılmış satırlar DOKUNULMAZ
    /// (Postman: pasif satır URL'de yoktur ama listede yaşar).</summary>
    private void Url_Degisti(object sender, TextChangedEventArgs e)
    {
        if (_urlEsitleniyor)
            return;
        _urlEsitleniyor = true;
        try
        {
            (_, IReadOnlyList<RestSatir> yeni) = RestUrlSenkron.Ayristir(UrlKutusu.Text ?? "");
            List<RestSatirGorunum> pasifler = [.. _params.Where(p => !p.Etkin)];
            List<RestSatirGorunum> eskiEtkin = [.. _params.Where(p => p.Etkin)];
            _params.Clear();
            var kullanildi = new bool[eskiEtkin.Count];
            foreach (RestSatir s in yeni)
            {
                int es = -1; // aynı anahtarlı İLK kullanılmamış eski satır — Açıklama'sı taşınır
                for (int i = 0; i < eskiEtkin.Count; i++)
                {
                    if (!kullanildi[i] && eskiEtkin[i].Anahtar == s.Anahtar) { es = i; break; }
                }
                if (es >= 0)
                    kullanildi[es] = true;
                _params.Add(new RestSatirGorunum
                {
                    Etkin = true, Anahtar = s.Anahtar, Deger = s.Deger,
                    Aciklama = es >= 0 ? eskiEtkin[es].Aciklama : "",
                });
            }
            foreach (RestSatirGorunum p in pasifler)
                _params.Add(p);
        }
        finally
        {
            _urlEsitleniyor = false;
        }
    }

    /// <summary>Param hücresi düzenlenince URL tazelenir (commit olaydan SONRA — BeginInvoke).</summary>
    private void Param_HucreBitti(object sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(ParamlardanUrle);

    private void ParamlardanUrle()
    {
        if (_urlEsitleniyor)
            return;
        _urlEsitleniyor = true;
        try
        {
            string taban = RestUrlSenkron.Ayristir(UrlKutusu.Text ?? "").Taban;
            UrlKutusu.Text = RestUrlSenkron.Kur(taban, _params.Select(p => p.Model()));
        }
        finally
        {
            _urlEsitleniyor = false;
        }
    }

    /// <summary>Seçilen metodun rengi kapalı combo'da da görünsün (Postman) — öğeler MetodOge stiliyle.</summary>
    private void Metod_Secildi(object sender, SelectionChangedEventArgs e)
        => Metod.Foreground = Metod.SelectedItem switch
        {
            HttpMetodu.GET => new SolidColorBrush(Color.FromRgb(0x1A, 0x7F, 0x37)),
            HttpMetodu.POST => new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
            HttpMetodu.PUT => new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)),
            HttpMetodu.PATCH => new SolidColorBrush(Color.FromRgb(0x69, 0x36, 0xAA)),
            HttpMetodu.DELETE => new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            _ => (Brush)FindResource("MetinFircasi"),
        };

    /// <summary>⋯ düğmesi: taşma menüsünü sol tıkla da açar (yalnız sağ tık beklenmesin).</summary>
    private void Tasma_Click(object sender, RoutedEventArgs e)
    {
        if (TasmaDugmesi.ContextMenu is { } menu)
        {
            menu.PlacementTarget = TasmaDugmesi;
            menu.IsOpen = true;
        }
    }

    // ── istek kurulumu ──────────────────────────────────────────────────────────────────────

    private RestIstek IstekKur()
    {
        List<RestSatir> paramlar = [.. _params.Where(p => !string.IsNullOrWhiteSpace(p.Anahtar)).Select(p => p.Model())];
        List<RestSatir> basliklar = [.. _basliklar.Where(b => !string.IsNullOrWhiteSpace(b.Anahtar)).Select(b => b.Model())];

        // madde 9: API Key kimliği → anahtar adı+değeri Başlık'a ya da Query'ye eklenir (Basic/Bearer'a ek seçenek).
        if (KimlikTuru.SelectedItem as string == "API Key"
            && KullaniciKutusu.Text is { Length: > 0 } apiAd
            && !string.IsNullOrEmpty(ParolaKutusu.Text))
        {
            var apiSatir = new RestSatir(apiAd.Trim(), ParolaKutusu.Text, true);
            if (ApiKeyKonum.SelectedItem as string == "Query")
                paramlar.Add(apiSatir);
            else
                basliklar.Add(apiSatir);
        }

        // v23 senkron sonrası TEK KAYNAK grid'dir: URL'nin query'si grid'de birebir var —
        // gönderime TABAN gider, yoksa RestIstemcisi.UrlBirlestir aynı çiftleri İKİNCİ kez eklerdi.
        return new(
            (HttpMetodu)(Metod.SelectedItem ?? HttpMetodu.GET),
            RestUrlSenkron.Ayristir(UrlKutusu.Text?.Trim() ?? "").Taban,
            paramlar, basliklar,
            string.IsNullOrWhiteSpace(Govde.Text) ? null : Govde.Text,
            KimlikKur());
    }

    private SoapKimlik? KimlikKur() => (KimlikTuru.SelectedItem as string) switch
    {
        "Basic" => new SoapKimlik(KullaniciKutusu.Text, ParolaKutusu.Text),
        "Bearer" => new SoapKimlik(null, ParolaKutusu.Text),
        _ => null,
    };

    private void KimlikTuru_Secildi(object sender, RoutedEventArgs e)
    {
        string tur = KimlikTuru.SelectedItem as string ?? "Yok";
        bool basic = tur == "Basic", bearer = tur == "Bearer", apiKey = tur == "API Key";
        Kullanici1Et.Visibility = KullaniciKutusu.Visibility =
            basic || apiKey ? Visibility.Visible : Visibility.Collapsed;
        Parola1Et.Visibility = ParolaKutusu.Visibility =
            basic || bearer || apiKey ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyKonum.Visibility = apiKey ? Visibility.Visible : Visibility.Collapsed;
        Kullanici1Et.Text = apiKey ? "Anahtar:" : "Kullanıcı:";
        Parola1Et.Text = apiKey ? "Değer:" : bearer ? "Token:" : "Parola:";
    }

    // ── gönderim ────────────────────────────────────────────────────────────────────────────

    private async void Gonder_Click(object sender, RoutedEventArgs e)
    {
        DegiskenleriTopla();
        RestIstek istek = IstekKur();
        if (string.IsNullOrWhiteSpace(istek.Url) || istek.Url == "https://")
        {
            DurumMetni.Text = "Önce bir URL girin.";
            return;
        }

        CancellationToken ct = IslemBaslat();
        GonderDugmesi.IsEnabled = false;
        DurumMetni.Text = "Gönderiliyor… (⏹ Durdur ile iptal)";
        try
        {
            RestIstek resolved = DegiskenCozucu.CozIstek(istek, _aktifDegiskenler);
            _sonResolved = resolved;
            RestCevap cevap = await _istemci.GonderAsync(resolved, ct);
            _sonYanit = cevap;
            YanitGoster(cevap);

            if (_depo is not null)
            {
                await _depo.GecmisEkleAsync(new RestGecmisKaydi(
                    0, DateTime.UtcNow, resolved.Metod.ToString(), resolved.Url,
                    cevap.Durum, (long)cevap.Sure.TotalMilliseconds));
                await GecmisiYukleAsync();
            }
        }
        catch (OperationCanceledException) { DurumMetni.Text = "İstek durduruldu."; }
        catch (Exception ex) { DurumMetni.Text = $"Hata: {ex.Message}"; }
        finally { GonderDugmesi.IsEnabled = true; IslemBitti(); }
    }

    private static string Kirp(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    // ── değişkenler (v20-S13: DB değeri → değişken) ──────────────────────────────────────────

    /// <summary>
    /// Değişken grid'ini <see cref="_aktifDegiskenler"/>'e toplar (adı boş olmayan her satır) — her
    /// gönderimden ÖNCE çağrılır, böylece literal düzenlemeler ve SQL'den çözülmüş değerler {{Ad}}
    /// ikamesine (tek + toplu) anında yansır. Aynı ad birden çoksa SON satır kazanır.
    /// </summary>
    private void DegiskenleriTopla()
    {
        _aktifDegiskenler.Clear();
        foreach (RestDegiskenGorunum d in _degiskenler)
            if (!string.IsNullOrWhiteSpace(d.Ad))
                _aktifDegiskenler[d.Ad.Trim()] = d.Deger ?? "";
    }

    /// <summary>
    /// SQL'i dolu değişkenleri çalıştırıp ilk hücreyi (<see cref="_scalarSorgu"/>) Değer'e yazar; sonra
    /// tümünü <see cref="_aktifDegiskenler"/>'e toplar. Hata SATIR bazında yakalanır (biri patlayınca
    /// gerisi çözülür) ve o değişkenin Değer'ine "&lt;hata: …&gt;" yazılır.
    /// </summary>
    private async void DegiskenCoz_Click(object sender, RoutedEventArgs e)
    {
        if (_scalarSorgu is null)
        {
            DurumMetni.Text = "Bağlı bir SQL profili yok — değişkenler yalnız literal kullanılabilir.";
            return;
        }

        // Grid hücresindeki düzenleme henüz commit edilmemiş olabilir — zorla (SQL/Ad kaybolmasın).
        DegiskenGrid.CommitEdit(DataGridEditingUnit.Row, true);

        List<RestDegiskenGorunum> sqlliler =
            [.. _degiskenler.Where(d => !string.IsNullOrWhiteSpace(d.Sql) && !string.IsNullOrWhiteSpace(d.Ad))];
        if (sqlliler.Count == 0)
        {
            DegiskenleriTopla();
            DurumMetni.Text = "SQL'li değişken yok — literal değerler uygulandı.";
            return;
        }

        DegiskenCozDugmesi.IsEnabled = false;
        int ok = 0;
        try
        {
            foreach (RestDegiskenGorunum d in sqlliler)
            {
                DurumMetni.Text = $"Değişken çözülüyor: {d.Ad}…";
                try { d.Deger = await _scalarSorgu(d.Sql); ok++; }
                catch (Exception ex) { d.Deger = $"<hata: {ex.Message}>"; }
            }
        }
        finally { DegiskenCozDugmesi.IsEnabled = true; }

        DegiskenleriTopla();
        DurumMetni.Text = $"Değişkenler çözüldü — {ok}/{sqlliler.Count} SQL değişkeni dolduruldu.";
    }

    /// <summary>v23 Postman rozetleri: durum kodu renkli (2xx yeşil · 3xx amber · 4xx/5xx kırmızı),
    /// yanında süre + boyut; ağ hatasında kırmızı "AĞ HATASI".</summary>
    private void RozetleriGoster(string durumMetni, Brush zemin, Brush on, string? sure, string? boyut)
    {
        DurumRozet.Visibility = Visibility.Visible;
        DurumRozet.Background = zemin;
        DurumRozetMetin.Text = durumMetni;
        DurumRozetMetin.Foreground = on;
        SureRozet.Visibility = sure is null ? Visibility.Collapsed : Visibility.Visible;
        SureRozetMetin.Text = sure is null ? "" : $"⏱ {sure}";
        BoyutRozet.Visibility = boyut is null ? Visibility.Collapsed : Visibility.Visible;
        BoyutRozetMetin.Text = boyut is null ? "" : $"⇩ {boyut}";
    }

    private static (Brush Zemin, Brush On) RozetRenk(int durum) => durum switch
    {
        >= 200 and < 300 => (new SolidColorBrush(Color.FromRgb(0xDF, 0xF2, 0xE1)), new SolidColorBrush(Color.FromRgb(0x1A, 0x7F, 0x37))),
        >= 300 and < 400 => (new SolidColorBrush(Color.FromRgb(0xFB, 0xF0, 0xD0)), new SolidColorBrush(Color.FromRgb(0x7A, 0x5A, 0x00))),
        _ => (new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE9)), new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C))),
    };

    private void YanitGoster(RestCevap cevap)
    {
        if (cevap.Hata is { } hata)
        {
            DurumMetni.Text = $"⚠ Ağ hatası: {hata}";
            (Brush z, Brush o) = RozetRenk(500);
            RozetleriGoster("AĞ HATASI", z, o, $"{cevap.Sure.TotalMilliseconds:0} ms", null);
            Yanit.SyntaxHighlighting = null;
            Yanit.Text = hata;
            YanitBaslikGrid.ItemsSource = null;
            CerezGrid.ItemsSource = null;
            BaslikSekmeBaslik.Text = "Başlıklar";
            CerezSekmeBaslik.Text = "Çerezler";
            TabloKaydetDugmesi.IsEnabled = false;
            DbKarsilastirDugmesi.IsEnabled = false;
            DegiskeneCikarDugmesi.IsEnabled = false;
            JsonPathDugmesi.IsEnabled = false;
            SabitleDugmesi.IsEnabled = true; // hata da sabitlenip diff'lenebilir (madde 7)
            return;
        }

        string boyut = cevap.Boyut < 1024 ? $"{cevap.Boyut} B" : $"{cevap.Boyut / 1024.0:0.#} KB";
        DurumMetni.Text = $"● {cevap.Durum} {cevap.DurumMetni} · {cevap.Sure.TotalMilliseconds:0} ms · {boyut}"
            + (string.IsNullOrEmpty(cevap.IcerikTipi) ? "" : $" · {cevap.IcerikTipi.Split(';')[0]}");
        (Brush zemin, Brush on) = RozetRenk(cevap.Durum);
        RozetleriGoster($"{cevap.Durum} {cevap.DurumMetni}".TrimEnd(), zemin, on,
            $"{cevap.Sure.TotalMilliseconds:0} ms", boyut);

        // Çerezler sekmesi (Postman'daki Cookies): Set-Cookie başlıkları ad=değer + öznitelik olarak.
        List<RestSatir> cerezler = [.. cevap.Basliklar
            .Where(b => b.Anahtar.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .Select(b =>
            {
                string[] parcalar = b.Deger.Split(';', 2);
                int esit = parcalar[0].IndexOf('=');
                string ad = esit > 0 ? parcalar[0][..esit].Trim() : parcalar[0].Trim();
                string kalan = (esit > 0 ? parcalar[0][(esit + 1)..] : "")
                    + (parcalar.Length > 1 ? " · " + parcalar[1].Trim() : "");
                return new RestSatir(ad, kalan);
            })];
        CerezGrid.ItemsSource = cerezler;
        BaslikSekmeBaslik.Text = $"Başlıklar ({cevap.Basliklar.Count})";
        CerezSekmeBaslik.Text = cerezler.Count == 0 ? "Çerezler" : $"Çerezler ({cerezler.Count})";

        bool json = JsonMu(cevap);
        JsonTemasiUygula(Yanit);
        if (!json)
            Yanit.SyntaxHighlighting = null;
        Yanit.Text = json ? GuzelJson(cevap.Govde) : cevap.Govde;
        YanitBaslikGrid.ItemsSource = cevap.Basliklar;
        // "Tabloya kaydet" ve "DB ile karşılaştır" yalnız JSON gövdede etkin (kolon+satır çıkarılabilir).
        bool jsonDolu = json && !string.IsNullOrWhiteSpace(cevap.Govde);
        TabloKaydetDugmesi.IsEnabled = jsonDolu;
        DbKarsilastirDugmesi.IsEnabled = jsonDolu;
        DegiskeneCikarDugmesi.IsEnabled = jsonDolu; // madde 12
        JsonPathDugmesi.IsEnabled = jsonDolu;        // madde 14
        SabitleDugmesi.IsEnabled = true; // her yanıt sabitlenip diff'lenebilir (madde 7)
    }

    /// <summary>
    /// v20-S12: JSON yanıtı kolon+satıra çevirip görsel tablo tasarımcısını (<see cref="TabloOlusturPenceresi"/>)
    /// ÖN-DOLU açar — kullanıcı kolonları/tipleri gözden geçirip düzenler, "Oluştur ve Aktar" tabloyu
    /// oluşturur ve satırları AKTİF bağlantıya yazar (Excel/TXT içe aktarımıyla paylaşılan hat).
    /// Geçersiz/boş/JSON-olmayan gövde net mesajla reddedilir — çökme yok.
    /// </summary>
    private void TabloKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (_tabloBaglami is not { } baglam)
            return;
        if (_sonYanit is not { } y || string.IsNullOrWhiteSpace(y.Govde))
        {
            DurumMetni.Text = "Önce bir yanıt alın.";
            return;
        }

        DosyaOnizleme? onizleme = JsonTabloAyristirici.Ayristir(y.Govde, out string? hata);
        if (onizleme is null)
        {
            DurumMetni.Text = $"⚠ {hata}";
            return;
        }

        // Şema varsayılanı motora göre (MSSQL dbo, diğerleri boş) — tasarımcıda değiştirilebilir.
        string sema = baglam.Lehce.MotorId == "mssql" ? "dbo" : "";
        var veri = new TabloVeriAktarimi(baglam.Profil, baglam.Veritabani, onizleme, baglam.Aktarim);
        // sekmeyeAc bu modda kullanılmaz (script sekmeye açılmaz, doğrudan oluşturulur). _aiSemaOner varsa
        // tasarımcıda "🤖 AI ile öner" görünür (madde 6 — yanıttan CREATE TABLE önerisi).
        new TabloOlusturPenceresi(baglam.Lehce, sema, (_, _) => { }, veri, _aiSemaOner) { Owner = this }.ShowDialog();
        DurumMetni.Text =
            $"Tablo tasarımcısı açıldı — {onizleme.Kolonlar.Count} kolon · {onizleme.Satirlar.Count} satır.";
    }

    /// <summary>
    /// v20-S13 "Yanıt ↔ DB karşılaştır": JSON yanıtı kolon+satıra çevirip (<see cref="JsonTabloAyristirici"/>)
    /// bir SQL sorgusunun sonucuyla anahtar kolonda karşılaştırma penceresini açar. Geçersiz JSON net
    /// mesajla reddedilir.
    /// </summary>
    private void DbKarsilastir_Click(object sender, RoutedEventArgs e)
    {
        if (_dbSorgu is not { } dbSorgu)
            return;
        if (_sonYanit is not { } y || string.IsNullOrWhiteSpace(y.Govde))
        {
            DurumMetni.Text = "Önce bir yanıt alın.";
            return;
        }

        DosyaOnizleme? apiOnizleme = JsonTabloAyristirici.Ayristir(y.Govde, out string? hata);
        if (apiOnizleme is null)
        {
            DurumMetni.Text = $"⚠ {hata}";
            return;
        }

        new RestKarsilastirPenceresi(apiOnizleme, dbSorgu) { Owner = this }.Show();
        DurumMetni.Text =
            $"Karşılaştırma penceresi açıldı — API {apiOnizleme.Satirlar.Count} satır. SQL + anahtar kolon girin.";
    }

    /// <summary>
    /// madde 12 (istek zinciri): yanıttan JSON yol ile bir değeri çıkarıp (<see cref="JsonYolAyiklayici"/>)
    /// değişkene atar — sonraki istekte <c>{{ad}}</c> olarak kullanılır (tek + toplu gönderim otomatik alır).
    /// </summary>
    private void DegiskeneCikar_Click(object sender, RoutedEventArgs e)
    {
        if (_sonYanit is not { } y || string.IsNullOrWhiteSpace(y.Govde))
        {
            DurumMetni.Text = "Önce bir yanıt alın.";
            return;
        }
        string? yol = MetinSor.Sor(this, "Yanıttan değişkene çıkar",
            "JSON yol (ör. token, data.id, items[0].ad):", "");
        if (string.IsNullOrWhiteSpace(yol))
            return;

        string? deger = JsonYolAyiklayici.Ayikla(y.Govde, yol, out string? hata);
        if (deger is null)
        {
            DurumMetni.Text = $"⚠ {hata}";
            return;
        }

        string ad = DegiskenAdiTuret(yol);
        RestDegiskenGorunum? mevcut =
            _degiskenler.FirstOrDefault(d => d.Ad.Equals(ad, StringComparison.OrdinalIgnoreCase));
        if (mevcut is null)
            _degiskenler.Add(new RestDegiskenGorunum { Ad = ad, Deger = deger });
        else
            mevcut.Deger = deger;
        DegiskenleriTopla();
        DurumMetni.Text = $"'{ad}' değişkeni atandı = {Kirp(deger, 60)}. Sonraki istekte "
            + "{{" + ad + "}}" + " kullanın (🔤 Değişkenler sekmesinde de görünür).";
    }

    /// <summary>madde 14: yanıttan JSON yol ile bir alanı/alt-ağacı çıkarır (<see cref="JsonYolAyiklayici"/>) → gösterir + panoya kopyalar.</summary>
    private void JsonPath_Click(object sender, RoutedEventArgs e)
    {
        if (_sonYanit is not { } y || string.IsNullOrWhiteSpace(y.Govde))
        {
            DurumMetni.Text = "Önce bir yanıt alın.";
            return;
        }
        string? yol = MetinSor.Sor(this, "JSON path ile alan çıkar",
            "JSON yol (ör. data, items[0], items[0].ad):", "");
        if (string.IsNullOrWhiteSpace(yol))
            return;

        string? deger = JsonYolAyiklayici.Ayikla(y.Govde, yol, out string? hata);
        if (deger is null)
        {
            DurumMetni.Text = $"⚠ {hata}";
            return;
        }
        try { Clipboard.SetText(deger); }
        catch { /* pano erişimi başarısız olabilir — yine de göster */ }
        new RestJsonPenceresi(deger) { Owner = this }.Show();
        DurumMetni.Text = $"'{yol}' çıkarıldı ({deger.Length:N0} karakter) — panoya kopyalandı, ayrı pencerede açıldı.";
    }

    /// <summary>JSON yolun son segmentinden makul bir değişken adı türetir (data.token → token, items[0].id → id).</summary>
    private static string DegiskenAdiTuret(string yol)
    {
        string son = yol.Split('.').Last().Trim();
        int koseli = son.IndexOf('[');
        if (koseli >= 0)
            son = son[..koseli].Trim();
        return son.Length > 0 ? son : "deger";
    }

    private static bool JsonMu(RestCevap c)
    {
        if (c.IcerikTipi?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        string g = c.Govde.TrimStart();
        return g.StartsWith('{') || g.StartsWith('[');
    }

    private static string GuzelJson(string ham)
    {
        try
        {
            using JsonDocument belge = JsonDocument.Parse(ham);
            return JsonSerializer.Serialize(belge.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (JsonException) { return ham; }
    }

    private CancellationToken IslemBaslat()
    {
        IptalYardimcisi.ArkaPlandaIptal(_cts, birak: true); // m.15: Cancel UI'da bloklayabilir
        _cts = new CancellationTokenSource();
        DurdurDugmesi.Visibility = Visibility.Visible;
        return _cts.Token;
    }

    private void IslemBitti() => DurdurDugmesi.Visibility = Visibility.Collapsed;

    private void Durdur_Click(object sender, RoutedEventArgs e)
    {
        IptalYardimcisi.ArkaPlandaIptal(_cts); // m.15: Cancel UI'da bloklayabilir — havuzda
        DurumMetni.Text = "Durduruluyor…";
    }

    // ── yanıt eylemleri: JSON aç · URL kopyala · URL aç · AI ────────────────────────────────

    private void JsonAc_Click(object sender, RoutedEventArgs e)
    {
        if (_sonYanit is not { } y || string.IsNullOrWhiteSpace(y.Govde))
        {
            DurumMetni.Text = "Önce bir yanıt alın.";
            return;
        }
        new RestJsonPenceresi(JsonMu(y) ? GuzelJson(y.Govde) : y.Govde).Show();
    }

    private void UrlKopyala_Click(object sender, RoutedEventArgs e)
    {
        DegiskenleriTopla();
        RestIstek r = DegiskenCozucu.CozIstek(IstekKur(), _aktifDegiskenler);
        string url = CurlCozumleyici.UrlBirlestir(r.Url, r.QueryParametreleri);
        try { Clipboard.SetText(url); DurumMetni.Text = "URL panoya kopyalandı."; }
        catch (Exception ex) { DurumMetni.Text = $"Kopyalanamadı: {ex.Message}"; }
    }

    private void UrlAc_Click(object sender, RoutedEventArgs e)
    {
        string pano = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        if (pano.Length == 0)
        {
            DurumMetni.Text = "Panoda metin yok.";
            return;
        }
        UrlKutusu.Text = pano; // query URL'de kalır; params grid'i olduğu gibi bırakılır
        DurumMetni.Text = "Panodaki URL istek alanına yüklendi — Gönder'e basın.";
    }

    /// <summary>madde 10: panodaki curl komutunu (<see cref="CurlCozumleyici.Coz"/>) isteğe çevirip UI'a yükler.</summary>
    private void CurlYapistir_Click(object sender, RoutedEventArgs e)
    {
        string curl = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(curl))
        {
            DurumMetni.Text = "Panoda metin yok — tarayıcı DevTools'ta 'Copy as cURL' yapıp tekrar deneyin.";
            return;
        }
        RestIstek? istek = CurlCozumleyici.Coz(curl);
        if (istek is null)
        {
            DurumMetni.Text = "Panodaki metin bir curl komutu değil (URL bulunamadı).";
            return;
        }
        IsteyiUygula(istek);
        DurumMetni.Text = $"cURL yüklendi ({istek.Metod} {Kisa(istek.Url)}) — gözden geçirip Gönder'e basın.";
    }

    /// <summary>madde 13: "💻 Kod üret" düğmesinin dil menüsünü (cURL/C#/Python) açar.</summary>
    private void KodUret_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } dugme)
        {
            menu.PlacementTarget = dugme;
            menu.IsOpen = true;
        }
    }

    /// <summary>madde 13: seçilen dilde bu isteğin kodunu üretir (değişkenler çözülür), ayrı pencerede gösterir + panoya kopyalar.</summary>
    private void KodUretDil_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string etiket } || !Enum.TryParse(etiket, out KodDili dil))
            return;
        DegiskenleriTopla();
        RestIstek cozulmus = DegiskenCozucu.CozIstek(IstekKur(), _aktifDegiskenler);
        string kod = IstekKodUretici.Uret(cozulmus, dil);
        try { Clipboard.SetText(kod); }
        catch { /* pano erişimi başarısız olabilir — yine de göster */ }
        new RestJsonPenceresi(kod) { Owner = this }.Show();
        DurumMetni.Text = $"{dil} kodu üretildi — panoya kopyalandı, ayrı pencerede açıldı.";
    }

    private async void AiYorumla_Click(object sender, RoutedEventArgs e)
    {
        if (_aiYorumla is null || _sonYanit is not { } y)
        {
            DurumMetni.Text = "Önce bir yanıt alın.";
            return;
        }
        string ozet = IstekOzeti(_sonResolved ?? IstekKur());
        async Task<AsistanCevabi?> Gorev() => await _aiYorumla(ozet, y.Govde, y.Durum);
        new AsistanCevapPenceresi("AI — REST yanıtı yorumu", Gorev()) { Owner = this }.Show();
    }

    /// <summary>
    /// v20-S13 "tarifle → istek üret": kullanıcıdan doğal dil açıklama alır, AI'a (<see cref="_aiIstekUret"/>)
    /// ürettirir, cevabı (<see cref="AiIstekAyristirici"/>) çözüp method/URL/başlık/gövde alanlarını DOLDURUR
    /// (kullanıcı göndermeden önce gözden geçirir). AI hatası ya da çözülemeyen cevap net mesajla gösterilir;
    /// ham cevap ayrı pencerede açılır.
    /// </summary>
    private async void AiUret_Click(object sender, RoutedEventArgs e)
    {
        if (_aiIstekUret is null)
            return;

        string? aciklama = MetinSor.Sor(this, "AI ile istek üret",
            "Ne yapmak istiyorsun? (ör. JSONPlaceholder /users a GET isteği at)", "");
        if (string.IsNullOrWhiteSpace(aciklama))
            return;

        AiUretDugmesi.IsEnabled = false;
        DurumMetni.Text = "AI istek üretiyor…";
        try
        {
            AsistanCevabi cevap = await _aiIstekUret(aciklama);
            if (!cevap.Basarili)
            {
                DurumMetni.Text = $"AI: {cevap.Metin}";
                return;
            }

            AiUretilenIstek? uretilen = AiIstekAyristirici.Ayristir(cevap.Metin, out string? hata);
            if (uretilen is null)
            {
                DurumMetni.Text = $"⚠ {hata}";
                new RestJsonPenceresi(cevap.Metin) { Owner = this }.Show(); // ham AI cevabını göster
                return;
            }

            Metod.SelectedItem = Enum.TryParse(uretilen.Metod, out HttpMetodu m) ? m : HttpMetodu.GET;
            UrlKutusu.Text = uretilen.Url;
            _basliklar.Clear();
            foreach (AiBaslik b in uretilen.Basliklar)
                _basliklar.Add(new RestSatirGorunum(new RestSatir(b.Ad, b.Deger, true)));
            Govde.Text = uretilen.Govde ?? "";
            DurumMetni.Text = $"AI isteği üretti ({uretilen.Metod}) — gözden geçirip Gönder'e basın.";
        }
        catch (Exception ex) { DurumMetni.Text = $"AI hatası: {ex.Message}"; }
        finally { AiUretDugmesi.IsEnabled = true; }
    }

    /// <summary>madde 7: şimdiki yanıtı ÖNCEKİ olarak sabitler (diff karşılaştırması için).</summary>
    private void Sabitle_Click(object sender, RoutedEventArgs e)
    {
        if (_sonYanit is not { } y)
        {
            DurumMetni.Text = "Önce bir yanıt alın.";
            return;
        }
        _sabitliAnlik = YanitAnlik(y);
        DiffYorumlaDugmesi.IsEnabled = _aiDiff is not null;
        DurumMetni.Text = "Yanıt sabitlendi (ÖNCEKİ). İkinci yanıtı alıp 🤖 Diff yorumla'ya basın.";
    }

    /// <summary>madde 7: sabitlenen (önceki) ile şimdiki yanıtın farkını AI'a yorumlatır (ayrı pencere).</summary>
    private void DiffYorumla_Click(object sender, RoutedEventArgs e)
    {
        if (_aiDiff is null || _sabitliAnlik is not { } onceki)
        {
            DurumMetni.Text = "Önce bir yanıtı 📌 Sabitleyin.";
            return;
        }
        if (_sonYanit is not { } y)
        {
            DurumMetni.Text = "Karşılaştırılacak şimdiki yanıt yok.";
            return;
        }
        string simdiki = YanitAnlik(y);
        async Task<AsistanCevabi?> Gorev() => await _aiDiff(onceki, simdiki);
        new AsistanCevapPenceresi("AI — REST yanıt farkı", Gorev()) { Owner = this }.Show();
    }

    /// <summary>Bir yanıtı diff için metne çevirir: istek özeti + durum/hata + gövde.</summary>
    private string YanitAnlik(RestCevap y)
    {
        string ozet = IstekOzeti(_sonResolved ?? IstekKur());
        string durum = y.Hata is { } h ? $"AĞ HATASI: {h}" : $"HTTP {y.Durum} {y.DurumMetni}";
        return $"{ozet}\n→ {durum}\n{y.Govde}";
    }

    private static string IstekOzeti(RestIstek i)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{i.Metod} {i.Url}");
        foreach (RestSatir b in i.Basliklar.Where(b => b.Etkin))
            sb.AppendLine($"{b.Anahtar}: {b.Deger}");
        if (!string.IsNullOrEmpty(i.Govde))
        {
            sb.AppendLine();
            sb.Append(i.Govde);
        }
        return sb.ToString();
    }

    // ── geçmiş · kayıtlı ────────────────────────────────────────────────────────────────────

    private async Task GecmisiYukleAsync()
    {
        if (_depo is null)
            return;
        IReadOnlyList<RestGecmisKaydi> kayitlar = await _depo.GecmisAsync(100);
        Gecmis.ItemsSource = kayitlar
            .Select(k => new ListeOge($"{k.Metod} {Kisa(k.Url)} · {k.Durum} · {k.SureMs}ms", k))
            .ToList();
    }

    private async Task KayitliYukleAsync()
    {
        if (_depo is null)
            return;
        IReadOnlyList<RestKayitliIstek> kayitlar = await _depo.KayitliIsteklerAsync();
        // madde 11: ad "Grup/Ad" ise ilk '/' önekine göre koleksiyona grupla; öneksiz → "(genel)".
        List<ListeOge> ogeler = [.. kayitlar.Select(k =>
        {
            int slash = k.Ad.IndexOf('/');
            string grup = slash > 0 ? k.Ad[..slash].Trim() : "(genel)";
            string ad = slash >= 0 && slash < k.Ad.Length - 1 ? k.Ad[(slash + 1)..].Trim() : k.Ad;
            return new ListeOge($"{k.Metod} · {ad}", k) { Grup = grup };
        })];
        var gorunum = new System.Windows.Data.ListCollectionView(ogeler);
        gorunum.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ListeOge.Grup)));
        KayitliListe.ItemsSource = gorunum;
    }

    // ── ortamlar (v20-S13 madde 8: dev/test/prod) ───────────────────────────────────────────

    private async Task OrtamlariYukleAsync(string? sec = null)
    {
        if (_depo is null)
            return;
        _ortamlar = await _depo.OrtamlarAsync();
        _ortamSecimBastir = true; // ItemsSource/seçim değişimi grid'i yeniden yüklemesin
        OrtamSecim.ItemsSource = _ortamlar.Select(o => o.Ad).ToList();
        if (sec is not null)
            OrtamSecim.SelectedItem = sec;
        _ortamSecimBastir = false;
    }

    /// <summary>Seçilen ortamın değişkenlerini grid'e yükler (mevcut değişkenleri DEĞİŞTİRİR) ve uygular.</summary>
    private void OrtamSecim_Degisti(object sender, SelectionChangedEventArgs e)
    {
        if (_ortamSecimBastir)
            return; // programatik seçim (kaydet sonrası) — kullanıcının grid'ini bozma
        if (OrtamSecim.SelectedItem is not string ad
            || _ortamlar.FirstOrDefault(o => o.Ad == ad) is not { } ortam)
            return;

        _degiskenler.Clear();
        foreach (RestSatir d in ortam.Degiskenler)
            _degiskenler.Add(new RestDegiskenGorunum { Ad = d.Anahtar, Deger = d.Deger });
        DegiskenleriTopla();
        DurumMetni.Text = $"'{ad}' ortamı yüklendi ({ortam.Degiskenler.Count} değişken).";
    }

    /// <summary>Grid'deki değişkenleri adlandırılmış bir ortam olarak (upsert) kaydeder — SQL sütunu değil, Ad+Değer.</summary>
    private async void OrtamKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (_depo is null)
            return;
        DegiskenGrid.CommitEdit(DataGridEditingUnit.Row, true);

        string varsayilan = OrtamSecim.SelectedItem as string ?? "";
        string? ad = MetinSor.Sor(this, "Ortamı kaydet", "Ortam adı (dev/test/prod):", varsayilan);
        if (string.IsNullOrWhiteSpace(ad))
            return;

        List<RestSatir> degiskenler = [.. _degiskenler
            .Where(d => !string.IsNullOrWhiteSpace(d.Ad))
            .Select(d => new RestSatir(d.Ad.Trim(), d.Deger ?? "", true))];
        await _depo.OrtamKaydetAsync(new RestOrtam(ad.Trim(), degiskenler));
        await OrtamlariYukleAsync(ad.Trim());
        DurumMetni.Text = $"'{ad.Trim()}' ortamı kaydedildi ({degiskenler.Count} değişken).";
    }

    private async void IstekKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (_depo is null)
            return;
        RestIstek i = IstekKur();
        string? ad = MetinSor.Sor(this, "İsteği kaydet",
            "Kayıtlı istek adı (koleksiyon için 'Grup/Ad', ör. Auth/Login):", Kisa(i.Url));
        if (ad is null)
            return;
        await _depo.IstekKaydetAsync(new RestKayitliIstek(ad, i.Metod.ToString(), i.Url, i.Govde));
        await KayitliYukleAsync();
        DurumMetni.Text = $"“{ad}” kaydedildi.";
    }

    private void Kayitli_CiftTik(object sender, MouseButtonEventArgs e) => SeciliKayitliYukle();
    private void KayitliYukle_Click(object sender, RoutedEventArgs e) => SeciliKayitliYukle();

    private void SeciliKayitliYukle()
    {
        if (KayitliListe.SelectedItem is ListeOge { Model: RestKayitliIstek k })
            IsteyiUygula(new RestIstek(
                Enum.TryParse(k.Metod, out HttpMetodu m) ? m : HttpMetodu.GET,
                k.Url, [], [], k.Govde, null));
    }

    private async void KayitliSil_Click(object sender, RoutedEventArgs e)
    {
        if (_depo is null || KayitliListe.SelectedItem is not ListeOge { Model: RestKayitliIstek k })
            return;
        if (!Iletisim.Sor(this, "SQLST — REST İstemcisi", "Kayıtlı istek silinsin mi?",
                $"“{k.Ad}” kayıtlı isteği silinecek.", "🗑 Sil", IletisimTuru.Tehlike))
            return;
        await _depo.IstekSilAsync(k.Ad);
        await KayitliYukleAsync();
        DurumMetni.Text = $"“{k.Ad}” silindi.";
    }

    private void Gecmis_CiftTik(object sender, MouseButtonEventArgs e)
    {
        if (Gecmis.SelectedItem is ListeOge { Model: RestGecmisKaydi g })
        {
            Metod.SelectedItem = Enum.TryParse(g.Metod, out HttpMetodu m) ? m : HttpMetodu.GET;
            UrlKutusu.Text = g.Url;
        }
    }

    /// <summary>Bir isteği tüm UI alanlarına yansıtır (kayıtlı yükleme).</summary>
    private void IsteyiUygula(RestIstek i)
    {
        Metod.SelectedItem = i.Metod;
        UrlKutusu.Text = i.Url;
        _params.Clear();
        foreach (RestSatir q in i.QueryParametreleri) _params.Add(new RestSatirGorunum(q));
        _basliklar.Clear();
        foreach (RestSatir b in i.Basliklar) _basliklar.Add(new RestSatirGorunum(b));
        Govde.Text = i.Govde ?? "";
        if (i.Kimlik is { BearerMi: true } bearer)
        {
            KimlikTuru.SelectedItem = "Bearer";
            ParolaKutusu.Text = bearer.Parola ?? "";
        }
        else if (i.Kimlik is { Dolu: true } basic)
        {
            KimlikTuru.SelectedItem = "Basic";
            KullaniciKutusu.Text = basic.KullaniciAdi ?? "";
            ParolaKutusu.Text = basic.Parola ?? "";
        }
        else
        {
            KimlikTuru.SelectedItem = "Yok";
        }
    }

    private static string Kisa(string s) => s.Length <= 48 ? s : "…" + s[^47..];
}
