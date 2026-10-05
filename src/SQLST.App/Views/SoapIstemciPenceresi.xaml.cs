using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Views;

/// <summary>
/// 🔌 SOAP İstemcisi penceresi (v14-S2, beyin fırtınası kararı 2026-07-26): WSDL yükle →
/// operasyon seç → örnek zarfı düzenle → gönder → yanıt + süre. Amaç, SP saran WCF servislerini
/// SQLST İÇİNDE test etmektir — SP'yi değiştirip aynı araçta servis katmanını doğrularsın.
/// Ağ işi <see cref="SoapIstemcisi"/>'nde, çözümleme SAF <see cref="WsdlCozumleyici"/>'de.
/// </summary>
public partial class SoapIstemciPenceresi : Window
{
    private readonly SoapIstemcisi _istemci;
    private readonly ISoapDeposu? _depo;
    private readonly ISecretProtector? _protector; // v16: ortam parolasını DPAPI ile şifreler/çözer
    private readonly Func<string, string, bool, Task<AsistanCevabi>>? _aiYorumla;
    private SoapServis? _servis;
    private IReadOnlyList<SoapGecmisKaydi> _gecmis = [];
    private IReadOnlyList<SoapOrtamKaydi> _ortamlar = [];
    private string _sonYanitHam = ""; // AI yorumlatma ham gövdeyle çalışır

    public SoapIstemciPenceresi(
        SoapIstemcisi istemci,
        ISoapDeposu? depo = null,
        Func<string, string, bool, Task<AsistanCevabi>>? aiYorumla = null,
        ISecretProtector? protector = null)
    {
        _istemci = istemci;
        _depo = depo;
        _protector = protector;
        _aiYorumla = aiYorumla;
        InitializeComponent();
        AiYorumlaDugmesi.Visibility = aiYorumla is null ? Visibility.Collapsed : Visibility.Visible;

        // #13 (2026-07-29): Zarf/Yanit XML renklendirmesi + zemin TEMA-FARKINDA (etiket/değer ayrı renk).
        XmlTemasiUygula(Zarf);
        XmlTemasiUygula(Yanit);

        // async void Loaded sınırı: depo okunamazsa pencere yine çalışır (proje deseni).
        Loaded += async (_, _) =>
        {
            try
            {
                await OrtamlariYukleAsync();
                await GecmisiYukleAsync();
            }
            catch (Exception ex) { Durum.Text = $"Geçmiş/ortam yüklenemedi: {ex.Message}"; }
        };
    }

    private async Task OrtamlariYukleAsync()
    {
        if (_depo is null)
            return;
        _ortamlar = await _depo.OrtamlarAsync();
        Ortamlar.ItemsSource = _ortamlar.Select(o => o.Ad).ToList();
    }

    private async Task GecmisiYukleAsync()
    {
        if (_depo is null)
            return;
        _gecmis = await _depo.GecmisAsync();
        Gecmis.ItemsSource = _gecmis.Select(g =>
            $"{g.ZamanUtc.ToLocalTime():HH:mm} {(g.FaultMu ? "⚠" : g.HttpDurum == 0 ? "✖" : "✔")} "
            + $"{OperasyonKisa(g.Aksiyon)} · {g.SureMs} ms").ToList();
    }

    private static string OperasyonKisa(string aksiyon)
        => aksiyon.Length == 0 ? "(action yok)" : aksiyon[(aksiyon.LastIndexOf('/') + 1)..];

    /// <summary>Bearer türü seçili mi (v19-S2) — SoapKimlik'in örtük kodlamasıyla uyumlu.</summary>
    private bool BearerSecili => KimlikTuru.SelectedIndex == 1;

    /// <summary>UI'dan kimlik (v16 Basic; v19-S2 Bearer): Basic'te kullanıcı adı, Bearer'da token
    /// zorunludur — boşsa null (kimlik gönderilmez).</summary>
    private SoapKimlik? KimlikAl()
    {
        if (BearerSecili)
            return Parola.Password.Length == 0 ? null : new SoapKimlik(null, Parola.Password);
        return KullaniciAdi.Text.Trim().Length == 0
            ? null : new SoapKimlik(KullaniciAdi.Text.Trim(), Parola.Password);
    }

    /// <summary>Bearer'da kullanıcı adı anlamsız — kutu kapatılır, etiket "Token:" olur (v19-S2).</summary>
    private void KimlikTuru_Degisti(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (KullaniciAdi is null) // InitializeComponent sırasında erken tetiklenme
            return;
        KullaniciAdi.IsEnabled = !BearerSecili;
        ParolaEtiket.Text = BearerSecili ? "Token:" : "Parola:";
    }

    /// <summary>🔐 WS-Security (v19-S2): kimlik kutularındaki kullanıcı+parolayla zarfa OASIS
    /// UsernameToken yazar — istek anında gizlice değil, editörde GÖRÜNÜR (şeffaflık ilkesi).</summary>
    private void WsSecurity_Click(object sender, RoutedEventArgs e)
    {
        byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        (string? yeni, string? hata) = SQLST.Application.WsSecurityUretici.UsernameTokenEkle(
            Zarf.Text, KullaniciAdi.Text.Trim(), Parola.Password,
            Convert.ToBase64String(nonce), DateTime.UtcNow);

        if (yeni is null)
        {
            Durum.Text = $"⚠ {hata}";
            return;
        }

        Zarf.Text = yeni;
        Durum.Text = "🔐 WS-Security UsernameToken zarfa eklendi (PasswordText + Nonce + Created) — "
            + "göndermeden önce editörde görebilir/düzenleyebilirsiniz.";
    }

    private void Ortam_Secildi(object sender, RoutedEventArgs e)
    {
        SoapOrtamKaydi? ortam = _ortamlar.FirstOrDefault(o => o.Ad == Ortamlar.SelectedItem as string);
        if (ortam is null)
            return;
        WsdlUrl.Text = ortam.WsdlUrl;
        Adres.Text = ortam.Adres;
        KullaniciAdi.Text = ortam.KullaniciAdi ?? "";
        // Parola DPAPI şifreli saklanır; çözebiliyorsak kutuya koy (başka makine/kullanıcıda çözülemez → boş).
        Parola.Password = ortam.ParolaSifreli is { Length: > 0 } s && _protector is not null
            ? Coz(s) : "";
        // v19-S2: kullanıcı adı boş + parola alanı dolu = Bearer kaydı (örtük kodlama) — tür seçici uyarlanır.
        KimlikTuru.SelectedIndex = KullaniciAdi.Text.Length == 0 && Parola.Password.Length > 0 ? 1 : 0;
        Durum.Text = $"'{ortam.Ad}' ortamı seçildi — Yükle ile operasyonları alın (adres hazır)."
            + (ortam.KullaniciAdi is { Length: > 0 } || Parola.Password.Length > 0 ? " Kimlik yüklendi." : "");
    }

    private string Coz(string sifreli)
    {
        try { return _protector!.Coz(sifreli); }
        catch { return ""; } // başka DPAPI bağlamında şifrelenmiş — çözülemez, kullanıcı yeniden girer
    }

    private async void OrtamKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (_depo is null)
        {
            Durum.Text = "Ortam deposu bağlı değil (pencere köprüsüz açıldı) — bu bir hatadır, bildirin.";
            return;
        }
        if (WsdlUrl.Text.Trim().Length == 0)
        {
            Durum.Text = "Kaydedilecek WSDL adresi yok — önce WSDL kutusunu doldurun.";
            return;
        }

        var pencere = new OrtamKaydetPenceresi(WsdlUrl.Text.Trim(), Adres.Text.Trim()) { Owner = this };
        if (pencere.ShowDialog() != true)
            return;

        // Kimlik: kullanıcı adı düz, parola/token DPAPI ile şifreli saklanır (bağlantı parolası
        // deseni). v19-S2: Bearer'da kullanıcı adı YOKTUR ama token yine kaydedilir (örtük kodlama:
        // kullanıcı boş + parola dolu = Bearer; yüklerken tür seçici buna göre kurulur).
        string? kullanici = BearerSecili || KullaniciAdi.Text.Trim().Length == 0
            ? null : KullaniciAdi.Text.Trim();
        string? parolaSifreli = Parola.Password.Length > 0 && _protector is not null
            && (kullanici is not null || BearerSecili)
            ? _protector.Sifrele(Parola.Password) : null;
        await _depo.OrtamKaydetAsync(new SoapOrtamKaydi(
            pencere.OrtamAdi, WsdlUrl.Text.Trim(), Adres.Text.Trim(), kullanici, parolaSifreli));
        await OrtamlariYukleAsync();
        Ortamlar.SelectedItem = pencere.OrtamAdi;
        Durum.Text = $"'{pencere.OrtamAdi}' ortamı kaydedildi.";
    }

    private void Gecmis_CiftTik(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Gecmis.SelectedIndex < 0 || Gecmis.SelectedIndex >= _gecmis.Count)
            return;
        SoapGecmisKaydi kayit = _gecmis[Gecmis.SelectedIndex];
        Adres.Text = kayit.Adres;
        Aksiyon.Text = kayit.Aksiyon;
        Zarf.Text = kayit.Zarf;
        GonderDugmesi.IsEnabled = true;
        Durum.Text = $"Geçmişten yüklendi: {OperasyonKisa(kayit.Aksiyon)} "
            + $"({kayit.ZamanUtc.ToLocalTime():dd.MM HH:mm}) — düzenleyip yeniden gönderebilirsiniz.";
    }

    private void AiYorumla_Click(object sender, RoutedEventArgs e)
    {
        if (_aiYorumla is null || _sonYanitHam.Length == 0)
            return;
        bool fault = _sonYanitHam.Contains(":Fault>", StringComparison.Ordinal);
        async Task<AsistanCevabi?> Gorev() => await _aiYorumla(Zarf.Text, _sonYanitHam, fault);
        new AsistanCevapPenceresi("AI — SOAP yanıtı yorumu", Gorev()) { Owner = this }.Show();
    }

    private async void Yukle_Click(object sender, RoutedEventArgs e)
    {
        string url = WsdlUrl.Text.Trim();
        if (url.Length == 0)
        {
            Durum.Text = "WSDL adresi boş — örn: http://sunucu/Servis.svc?wsdl";
            return;
        }

        YukleDugmesi.IsEnabled = false;
        Durum.Text = "WSDL indiriliyor…";
        try
        {
            (string ana, IReadOnlyList<string> ekler) =
                await _istemci.WsdlIndirAsync(url, KimlikAl(), CancellationToken.None);
            ServisiKur(ana, ekler);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException
            or System.Xml.XmlException or InvalidOperationException or TaskCanceledException)
        {
            Durum.Text = $"WSDL yüklenemedi: {ex.Message}";
        }
        finally
        {
            YukleDugmesi.IsEnabled = true;
        }
    }

    /// <summary>#12 (kullanıcı isteği 2026-07-29): WSDL'i YEREL DOSYADAN yükle (dosya seçici).</summary>
    private async void Dosya_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "WSDL dosyası seç",
            Filter = "WSDL / XML (*.wsdl;*.xml)|*.wsdl;*.xml|Tüm dosyalar (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        WsdlUrl.Text = dlg.FileName;
        YukleDugmesi.IsEnabled = false;
        Durum.Text = "WSDL dosyadan okunuyor…";
        try
        {
            (string ana, IReadOnlyList<string> ekler) =
                await _istemci.WsdlDosyadanOkuAsync(dlg.FileName, CancellationToken.None);
            ServisiKur(ana, ekler);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            Durum.Text = $"WSDL dosyası okunamadı: {ex.Message}";
        }
        finally
        {
            YukleDugmesi.IsEnabled = true;
        }
    }

    /// <summary>WSDL çözümleme + UI doldurma — URL (Yukle) ve dosya (Dosya) yolları paylaşır.</summary>
    private void ServisiKur(string ana, IReadOnlyList<string> ekler)
    {
        _servis = WsdlCozumleyici.Cozumle(ana, ekler);
        ServisAdi.Text = _servis.Ad;
        Adres.Text = _servis.Adres;
        // v19-S17: arama kutusu yeni servis yüklenince sıfırlanır; ≥1 operasyon varsa görünür.
        OperasyonArama.Clear();
        AramaSatiri.Visibility = _servis.Operasyonlar.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OperasyonListesiniTazele();
        if (Operasyonlar.Items.Count > 0)
            Operasyonlar.SelectedIndex = 0;
        Durum.Text = $"{_servis.Operasyonlar.Count} operasyon yüklendi"
            + (ekler.Count > 0 ? $" ({ekler.Count + 1} parçalı WSDL)" : "")
            + (_servis.Uyarilar.Count > 0 ? $". ⚠ {string.Join(" · ", _servis.Uyarilar)}" : ".");
    }

    /// <summary>v19-S17 (kullanıcı isteği 2026-08-04): operasyon listesini arama kutusuna göre süzer
    /// (harf duyarsız içeren). Seçili operasyon süzgeçte kalıyorsa seçim KORUNUR.</summary>
    private void OperasyonListesiniTazele()
    {
        if (_servis is null)
            return;
        string? seciliyken = Operasyonlar.SelectedItem as string;
        string ara = OperasyonArama.Text.Trim();
        IEnumerable<string> adlar = _servis.Operasyonlar.Select(o => o.Ad);
        if (ara.Length > 0)
            adlar = adlar.Where(a => a.Contains(ara, StringComparison.OrdinalIgnoreCase));
        List<string> liste = [.. adlar];
        Operasyonlar.ItemsSource = liste;
        // Seçili olan hâlâ listedeyse koru; değilse boş bırak (Gönder düğmesi Operasyon_Secildi'de güncellenir).
        if (seciliyken is not null && liste.Contains(seciliyken))
            Operasyonlar.SelectedItem = seciliyken;
    }

    private void OperasyonArama_Degisti(object sender, TextChangedEventArgs e)
    {
        AramaTemizleDugmesi.Visibility = OperasyonArama.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OperasyonListesiniTazele();
    }

    private void OperasyonAramaTemizle_Click(object sender, RoutedEventArgs e)
    {
        OperasyonArama.Clear();
        OperasyonArama.Focus();
    }

    /// <summary>#13 (2026-07-29): editöre tema-farkında XML renklendirmesi + tema zemini uygular.
    /// Etiket/öznitelik/yorum EditorTema.XmlTanim'da boyanır; içerik (girilen değerler) editör ön
    /// planına (amber) düşer → etiket ile değer gözle ayrışır. Tema App.KoyuTemaAcik'ten okunur.</summary>
    private void XmlTemasiUygula(ICSharpCode.AvalonEdit.TextEditor ed)
    {
        bool koyu = App.KoyuTemaAcik;
        ed.SyntaxHighlighting = EditorTema.XmlTanim(koyu);
        ed.Background = TryFindResource("PanelZeminFircasi") as Brush
            ?? new SolidColorBrush(koyu ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xFC, 0xFD, 0xFF));
        // İçerik = "değer" rengi (amber): girilen parametreler etiketlerden gözle ayrışsın (iki temada okunur).
        ed.Foreground = new SolidColorBrush(koyu ? Color.FromRgb(0xE0, 0xA4, 0x58) : Color.FromRgb(0xB4, 0x53, 0x09));
        ed.LineNumbersForeground = new SolidColorBrush(koyu ? Color.FromRgb(0x6A, 0x6A, 0x6A) : Color.FromRgb(0x9A, 0xA0, 0xA6));
    }

    private void Operasyon_Secildi(object sender, RoutedEventArgs e)
    {
        SoapOperasyon? op = SeciliOperasyon();
        GonderDugmesi.IsEnabled = op is not null;
        if (op is null)
            return;
        Aksiyon.Text = op.SoapAction;
        Zarf.Text = op.OrnekZarf;
        Durum.Text = op.Parametreler.Count == 0
            ? $"{op.Ad}: parametresiz — doğrudan gönderebilirsiniz."
            : $"{op.Ad}: {op.Parametreler.Count} parametre — "
                + string.Join(", ", op.Parametreler.Select(p =>
                    $"{p.Ad} ({p.Tip}{(p.Dizi ? "[]" : "")}{(p.SecimlikMi ? ", seçimlik" : "")})"))
                + ". Örnek değerleri düzenleyip gönderin.";
    }

    private SoapOperasyon? SeciliOperasyon()
        => _servis?.Operasyonlar.FirstOrDefault(o => o.Ad == Operasyonlar.SelectedItem as string);

    private void Zarf_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
        {
            e.Handled = true;
            Gonder_Click(sender, e);
        }
    }

    private async void Gonder_Click(object sender, RoutedEventArgs e)
    {
        string adres = Adres.Text.Trim();
        if (adres.Length == 0 || Zarf.Text.Trim().Length == 0)
        {
            Durum.Text = "Adres ve zarf boş olamaz.";
            return;
        }

        GonderDugmesi.IsEnabled = false;
        Durum.Text = "Gönderiliyor…";
        Yanit.Text = "";
        try
        {
            SoapCevap cevap = await _istemci.CagirAsync(
                adres, Aksiyon.Text.Trim(), Zarf.Text, KimlikAl(), CancellationToken.None);

            if (cevap.Hata is not null)
            {
                Durum.Text = $"⚠ Ulaşılamadı: {cevap.Hata} ({cevap.Sure.TotalMilliseconds:N0} ms)";
                return;
            }

            _sonYanitHam = cevap.Govde;
            Yanit.Text = XmlGuzellestir(cevap.Govde);
            AiYorumlaDugmesi.IsEnabled = _aiYorumla is not null;
            Durum.Text = cevap.FaultMu
                ? $"⚠ SOAP FAULT döndü (HTTP {cevap.HttpDurum}, {cevap.Sure.TotalMilliseconds:N0} ms) — ayrıntı yanıtta."
                : $"✔ HTTP {cevap.HttpDurum} · {cevap.Sure.TotalMilliseconds:N0} ms";

            // Kalıcı geçmiş (v14-S3): kayıt at, listeyi tazele — depo yoksa sessizce atlanır.
            if (_depo is not null)
            {
                await _depo.GecmisEkleAsync(new SoapGecmisKaydi(
                    0, DateTime.UtcNow, adres, Aksiyon.Text.Trim(), Zarf.Text,
                    cevap.HttpDurum, (long)cevap.Sure.TotalMilliseconds, cevap.FaultMu));
                await GecmisiYukleAsync();
            }
        }
        finally
        {
            GonderDugmesi.IsEnabled = true;
        }
    }

    /// <summary>Yanıt XML'ini girintiler; XML değilse (HTML hata sayfası vb.) olduğu gibi bırakır.</summary>
    private static string XmlGuzellestir(string metin)
    {
        try
        {
            return XDocument.Parse(metin).ToString();
        }
        catch (System.Xml.XmlException)
        {
            return metin;
        }
    }
}
