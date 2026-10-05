using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// 🪄 SP Sihirbazı (v19-S19 — adımlı yeniden tasarım, mock-up onayı 2026-08-04): 4 adım —
/// ①Tanımla ②Eşle ③Koşullar &amp; Kurallar ④Önizle &amp; Üret. Tüm profesyonel eklemeler:
/// CREATE OR ALTER · TOP/DISTINCT · çıkış AS + toplama (GROUP BY) · JOIN türü seçimi · WHERE AND/OR ·
/// ORDER BY · canlı SQL önizleme. Çekirdek SAF <see cref="SpSihirbazi"/>'nda; burada adım akışı + kurucu UI.
/// </summary>
public partial class SpSihirbaziPenceresi : Window
{
    private readonly Func<string?, Task<SemaOnbellegi?>> _onbellekGetir;
    private readonly Action<string, string, string?> _sekmeAc;

    /// <summary>Bir eşleme satırı: ad → aday kolon (combo) + (çıkışta) AS kutusu + toplama combo.</summary>
    private sealed class EslemeSatiri
    {
        public required string Ad;
        public required bool Giris;
        public required IReadOnlyList<SpKolonAdayi> Adaylar;
        public required ComboBox Secim;
        public TextBox? AsKutu;
        public ComboBox? Toplama;
        public SpKolonAdayi? SeciliAday => Adaylar.Count > 0 ? Adaylar[Math.Max(0, Secim.SelectedIndex)] : null;
    }

    private SemaOnbellegi? _onbellek;
    private readonly List<EslemeSatiri> _satirlar = [];
    private List<SpJoinAdimi> _joinlar = [];
    private readonly List<string> _baglanamayan = [];
    private readonly List<YabanciAnahtar> _elleFkler = []; // v19-S22: kullanıcının elle eklediği bağlar
    private readonly Dictionary<string, SemaNesnesi> _tumTablolar = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SpEkKosul> _ekKosullar = [];
    private readonly List<SpKural> _kurallar = [];
    private readonly List<SpSiralama> _siralamalar = [];
    private readonly Dictionary<string, SemaNesnesi> _sorguTablolari = new(StringComparer.OrdinalIgnoreCase);
    private string _sonAnalizGirdisi = "\0"; // giriş+çıkış metni değişince yeniden analiz
    private int _aktifAdim = 1;

    private static readonly string[] WhereOperatorleri =
        ["=", "<>", ">", ">=", "<", "<=", "LIKE", "IN", "IS NULL", "IS NOT NULL"];
    private static readonly string[] KuralOperatorleri =
        ["=", "<>", ">", ">=", "<", "<=", "IS NULL", "IS NOT NULL"];
    private static readonly string[] ToplamaSecenekleri =
        ["— toplama yok —", "COUNT", "SUM", "MIN", "MAX", "AVG"];

    public SpSihirbaziPenceresi(
        IReadOnlyList<string> veritabanlari, string? aktifDb,
        Func<string?, Task<SemaOnbellegi?>> onbellekGetir, Action<string, string, string?> sekmeAc)
    {
        _onbellekGetir = onbellekGetir;
        _sekmeAc = sekmeAc;
        InitializeComponent();
        Veritabani.ItemsSource = veritabanlari;
        Veritabani.SelectedItem = aktifDb ?? veritabanlari.FirstOrDefault();
        Onizleme.SyntaxHighlighting = EditorTema.Tanim(App.KoyuTemaAcik);
        AdimGorunumuGuncelle();
    }

    // ── Adım gezinme ─────────────────────────────────────────────────────────

    private void Adim_Tik(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int n))
            _ = AdimaGitAsync(n);
    }

    private void Ileri_Click(object sender, RoutedEventArgs e) => _ = AdimaGitAsync(_aktifAdim + 1);
    private void Geri_Click(object sender, RoutedEventArgs e) => _ = AdimaGitAsync(_aktifAdim - 1);

    private async Task AdimaGitAsync(int hedef)
    {
        hedef = Math.Clamp(hedef, 1, 4);

        // Adım 2+'ye geçiş: analiz güncel değilse yap (çıkış yoksa 1'de kal).
        if (hedef >= 2 && !await AnaliziSaglaAsync())
        {
            AktifAdimiGoster(1);
            return;
        }
        if (hedef >= 3)
        {
            KurucuSecicileriDoldur();
        }
        if (hedef == 4)
        {
            OnizlemeyiTazele();
        }
        AktifAdimiGoster(hedef);
    }

    private void AktifAdimiGoster(int n)
    {
        _aktifAdim = n;
        Icerik1.Visibility = n == 1 ? Visibility.Visible : Visibility.Collapsed;
        Icerik2.Visibility = n == 2 ? Visibility.Visible : Visibility.Collapsed;
        Icerik3.Visibility = n == 3 ? Visibility.Visible : Visibility.Collapsed;
        Icerik4.Visibility = n == 4 ? Visibility.Visible : Visibility.Collapsed;
        AdimGorunumuGuncelle();
    }

    /// <summary>
    /// Fırçayı DİNAMİK bağlar (tema değişince kendiliğinden güncellensin) ve öğeyi geri verir.
    /// Nesne başlatıcısının içinden <c>SetResourceReference</c> çağrılamadığı için bu yardımcı var:
    /// <c>(Brush)FindResource(...)</c> yazımı fırçayı o anki temada DONDURUYORDU ve sihirbaz araç
    /// sekmesi olarak yaşadığından tema değişiminde eski renkte kalıyordu (25 Ağu 2026 bulgusu).
    /// </summary>
    private static T Dinamik<T>(T oge, DependencyProperty ozellik, string anahtar)
        where T : FrameworkElement
    {
        oge.SetResourceReference(ozellik, anahtar);
        return oge;
    }

    private void AdimGorunumuGuncelle()
    {
        (Border kap, Border no)[] adimlar = [(Adim1, Adim1No), (Adim2, Adim2No), (Adim3, Adim3No), (Adim4, Adim4No)];
        for (int i = 0; i < 4; i++)
        {
            int adimNo = i + 1;
            bool aktif = adimNo == _aktifAdim, tamam = adimNo < _aktifAdim;
            // ⚠ SetResourceReference — FindResource DEĞİL (kullanıcı bulgusu 25 Ağu 2026: "koyu modda
            // madde başlıkları siyah olduğu için görünmüyor"). FindResource TEK SEFERLİK statik
            // aramadır: fırçayı o anki temada DONDURUR. Sihirbaz araç SEKMESİ olarak yaşadığı için
            // kullanıcı tema değiştirince bu öğeler ESKİ temanın renginde kalıyordu — açık temada
            // kurulup koyuya geçilince metin #111827'de donuyor ve koyu zeminde kayboluyordu.
            // SetResourceReference DynamicResource semantiğidir; tema değişince kendiliğinden çözülür.
            // Regresyon kapısı: Tema_acikten_koyuya_donunce_basliklar_koyu_kalmaz.
            adimlar[i].kap.SetResourceReference(Border.BorderBrushProperty, aktif ? "VurguFircasi" : "KenarFircasi");
            adimlar[i].no.SetResourceReference(Border.BackgroundProperty, aktif ? "VurguFircasi" : "PencereZeminFircasi");
            var yazi = (TextBlock)adimlar[i].no.Child;
            yazi.Text = tamam ? "✓" : adimNo.ToString();
            if (aktif)
                yazi.Foreground = Brushes.White; // vurgu zemininde bilerek sabit beyaz
            else
                yazi.SetResourceReference(TextBlock.ForegroundProperty, tamam ? "BasariFircasi" : "MetinFircasi");
        }
        GeriDugmesi.Visibility = _aktifAdim > 1 ? Visibility.Visible : Visibility.Collapsed;
        IleriDugmesi.Visibility = _aktifAdim < 4 ? Visibility.Visible : Visibility.Collapsed;
        UretDugmesi.Visibility = _aktifAdim == 4 ? Visibility.Visible : Visibility.Collapsed;
        KopyalaDugmesi.Visibility = _aktifAdim == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Adım 2: analiz + eşleme + JOIN yolu ──────────────────────────────────

    private static IReadOnlyList<string> Adlar(string metin)
        => [.. metin.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Analiz güncel değilse yeniden yapar (giriş/çıkış metni değişince). Çıkış yoksa false.</summary>
    private async Task<bool> AnaliziSaglaAsync()
    {
        IReadOnlyList<string> girisAdlari = Adlar(Girisler.Text);
        IReadOnlyList<string> cikisAdlari = Adlar(Cikislar.Text);
        if (cikisAdlari.Count == 0)
        {
            Durum.Text = "⚠ En az bir ÇIKIŞ kolonu gerekli.";
            return false;
        }

        string girdi = Veritabani.SelectedItem + "|" + Girisler.Text + "||" + Cikislar.Text;
        if (girdi == _sonAnalizGirdisi && _satirlar.Count > 0)
            return true; // değişmemiş — kullanıcının seçimlerini koru

        Durum.Text = "Şema okunuyor…";
        _onbellek = await _onbellekGetir(Veritabani.SelectedItem as string);
        if (_onbellek is null)
        {
            Durum.Text = "⚠ Şema okunamadı — bağlantıyı/veritabanını kontrol edin.";
            return false;
        }

        EslemePanel.Children.Clear();
        _satirlar.Clear();
        _ekKosullar.Clear(); _kurallar.Clear(); _siralamalar.Clear(); _elleFkler.Clear();
        WhereListe.Children.Clear(); KuralListe.Children.Clear(); SiralaListe.Children.Clear(); ElleBagListe.Children.Clear();

        // v19-S22: elle JOIN combo'ları TÜM şema tablolarıyla dolar (bağlanamayan tabloyu da seçebilmek için).
        _tumTablolar.Clear();
        foreach (SemaNesnesi t in _onbellek.Nesneler.Where(n => n.Tur == SemaNesneTuru.Tablo))
            _tumTablolar[t.TamAd] = t;
        List<string> tumAdlar = _tumTablolar.Keys.OrderBy(k => k).ToList();
        ElleTablo1.ItemsSource = tumAdlar;
        ElleTablo2.ItemsSource = tumAdlar;

        int eslesmeyen = 0;
        foreach (string ad in girisAdlari) EslemeSatiriEkle(ad, giris: true, ref eslesmeyen);
        foreach (string ad in cikisAdlari) EslemeSatiriEkle(ad, giris: false, ref eslesmeyen);

        _sonAnalizGirdisi = girdi;
        JoinlariHesapla();
        Durum.Text = eslesmeyen > 0
            ? $"✔ {_satirlar.Count} ad işlendi ({eslesmeyen} eşleşmedi — giriş=parametre, çıkış=NULL kolon)."
            : $"✔ {_satirlar.Count} ad eşleşti.";
        return true;
    }

    private void EslemeSatiriEkle(string tamAd, bool giris, ref int eslesmeyen)
    {
        // v19-S21: "Tablo.Kolon" nitelenmiş ad ARAMADA kullanılır; param/AS/etiket adı SON PARÇADIR
        // (nitelemenin kendisi geçerli bir parametre/kolon adı değil).
        IReadOnlyList<SpKolonAdayi> adaylar = SpSihirbazi.KolonAra(_onbellek!, tamAd);
        string ad = tamAd.Contains('.') ? tamAd[(tamAd.LastIndexOf('.') + 1)..].Trim() : tamAd;
        if (adaylar.Count == 0)
            eslesmeyen++;

        var combo = new ComboBox
        {
            ItemsSource = adaylar.Count > 0
                ? adaylar.Select(a => a.Gosterim).ToList()
                : [giris ? "— eşleşme yok (parametre) —" : "— eşleşme yok (NULL kolon) —"],
            SelectedIndex = 0, Margin = new Thickness(6, 0, 0, 0), MinWidth = 300,
            IsEnabled = adaylar.Count > 1,
        };
        combo.SelectionChanged += (_, _) => EslemeDegisti();

        var satir = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        var rozet = Dinamik(
            new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = giris ? "GİRİŞ" : "ÇIKIŞ", FontSize = 10.5, FontWeight = FontWeights.Bold },
            },
            Border.BackgroundProperty, giris ? "BilgiZeminFircasi" : "PanelZeminFircasi");
        DockPanel.SetDock(rozet, Dock.Left);
        satir.Children.Add(rozet);
        satir.Children.Add(new TextBlock
        {
            Text = "  " + ad + (adaylar.Count > 1 ? $"  ({adaylar.Count} aday)" : adaylar.Count == 0 ? "  ⚠" : ""),
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 150,
        });

        var esleme = new EslemeSatiri { Ad = ad, Giris = giris, Adaylar = adaylar, Secim = combo };

        // Çıkış satırında AS (yeniden adlandır) + toplama.
        if (!giris)
        {
            satir.Children.Add(combo);
            var asKutu = new TextBox
            {
                Text = ad, Width = 120, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2),
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Çıkış adı (AS)",
            };
            asKutu.TextChanged += (_, _) => { }; // önizleme adım 4'te tazelenir
            var toplama = new ComboBox
            {
                ItemsSource = ToplamaSecenekleri, SelectedIndex = 0, Width = 130,
                Margin = new Thickness(6, 0, 0, 0), ToolTip = "Toplama (COUNT/SUM…) → GROUP BY otomatik",
            };
            var asEtiket = Dinamik(new TextBlock { Text = " AS ", VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, "SolukMetinFircasi");
            satir.Children.Add(asEtiket);
            satir.Children.Add(asKutu);
            satir.Children.Add(toplama);
            esleme.AsKutu = asKutu;
            esleme.Toplama = toplama;
        }
        else
        {
            satir.Children.Add(combo);
        }

        EslemePanel.Children.Add(satir);
        _satirlar.Add(esleme);
    }

    private void EslemeDegisti()
    {
        JoinlariHesapla();
        if (_aktifAdim == 4)
            OnizlemeyiTazele();
    }

    /// <summary>Eşleşen seçili adayların tabloları (giriş önce) — kök tutarlılığı için sıralı, tekilleştirilmiş.</summary>
    private List<SemaNesnesi> SorguyaGirenTablolar()
    {
        var liste = new List<SemaNesnesi>();
        foreach (EslemeSatiri s in _satirlar.OrderByDescending(s => s.Giris)) // girişler önce
            if (s.SeciliAday is { } a && !liste.Any(t => t.TamAd.Equals(a.Tablo.TamAd, StringComparison.OrdinalIgnoreCase)))
                liste.Add(a.Tablo);
        return liste;
    }

    /// <summary>Eşlemeden JOIN yolunu YENİDEN HESAPLAR (YolBul) ve çizer — mapping değişince.</summary>
    private void JoinlariHesapla()
    {
        _baglanamayan.Clear();
        _sorguTablolari.Clear();
        if (_onbellek is null)
        {
            JoinPanel.Children.Clear();
            return;
        }

        List<SemaNesnesi> tablolar = SorguyaGirenTablolar();
        foreach (SemaNesnesi t in tablolar)
            _sorguTablolari.TryAdd(t.TamAd, t);

        // v19-S22: gerçek FK + ad kuralı + KULLANICININ ELLE eklediği bağlar birlikte.
        List<YabanciAnahtar> tumFkler =
            [.. _onbellek.YabanciAnahtarlar, .. SpSihirbazi.AdKuralindanIliskiler(_onbellek), .. _elleFkler];
        _joinlar = [.. SpSihirbazi.YolBul(tablolar, tumFkler, _baglanamayan)];
        foreach (SpJoinAdimi j in _joinlar)
            _sorguTablolari.TryAdd(j.YeniTablo.TamAd, j.YeniTablo);

        JoinYolunuCiz();
    }

    /// <summary>Mevcut <see cref="_joinlar"/>'ı çizer (yeniden hesaplamaz) — tür değişimi/taşıma sonrası.</summary>
    private void JoinYolunuCiz()
    {
        JoinPanel.Children.Clear();
        List<SemaNesnesi> tablolar = SorguyaGirenTablolar();
        if (tablolar.Count > 0)
            JoinPanel.Children.Add(TabloCipi(tablolar[0].Ad));
        for (int i = 0; i < _joinlar.Count; i++)
        {
            JoinPanel.Children.Add(Dinamik(new TextBlock { Text = " → ", VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, "SolukMetinFircasi"));
            JoinPanel.Children.Add(JoinTasiDugmeleri(i));
            JoinPanel.Children.Add(JoinTurAnahtari(i));
            JoinPanel.Children.Add(TabloCipi(_joinlar[i].YeniTablo.Ad));
        }

        JoinNot.Text = _baglanamayan.Count > 0
            ? $"⚠ Bağlanamayan: {string.Join(", ", _baglanamayan)} — ad kuralından da yol bulunamadı. Farklı aday seçin."
            : _joinlar.Any(j => j.AdKuralindan)
                ? "⚠ Bazı bağlar ad kuralından (XId→X) — varsayılan LEFT JOIN; doğrulayın ya da INNER'a çevirin. ◀▶ ile sırayı değiştirebilirsiniz."
                : _joinlar.Count > 1 ? "◀▶ ile JOIN sırasını değiştirebilirsiniz." : "";
    }

    private static Border TabloCipi(string ad)
    {
        var cip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(2),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = ad, FontWeight = FontWeights.SemiBold },
        };
        cip.SetResourceReference(BackgroundProperty, "PencereZeminFircasi");
        cip.SetResourceReference(BorderBrushProperty, "KenarFircasi");
        return cip;
    }

    /// <summary>v19-S21: JOIN'i sırada geri/ileri taşıma (◀ ▶) — bağımlılığı bozan taşıma reddedilir.</summary>
    private StackPanel JoinTasiDugmeleri(int indeks)
    {
        var kap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Ok(string metin, int yon, bool etkin)
        {
            var b = new Button
            {
                Content = metin, Padding = new Thickness(4, 0, 4, 0), FontSize = 12, MinHeight = 0,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, IsEnabled = etkin,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = yon < 0 ? "Öne al" : "Geriye al",
            };
            b.SetResourceReference(ForegroundProperty, "SolukMetinFircasi");
            b.Click += (_, _) => JoinTasi(indeks, yon);
            return b;
        }
        kap.Children.Add(Ok("◀", -1, indeks > 0));
        kap.Children.Add(Ok("▶", +1, indeks < _joinlar.Count - 1));
        return kap;
    }

    private void JoinTasi(int indeks, int yon)
    {
        int hedef = indeks + yon;
        if (hedef < 0 || hedef >= _joinlar.Count)
            return;
        var yeni = new List<SpJoinAdimi>(_joinlar);
        (yeni[indeks], yeni[hedef]) = (yeni[hedef], yeni[indeks]);
        SemaNesnesi? kok = SorguyaGirenTablolar().FirstOrDefault();
        if (kok is null || !SpSihirbazi.JoinSirasiGecerli(kok, yeni))
        {
            Durum.Text = "⚠ Bu taşıma JOIN bağımlılığını bozar — bir join, kendinden önce eklenmemiş tabloya bağlanamaz.";
            return;
        }
        _joinlar = yeni;
        JoinYolunuCiz();
        if (_aktifAdim == 4)
            OnizlemeyiTazele();
    }

    private Border JoinTurAnahtari(int indeks)
    {
        var kap = new StackPanel { Orientation = Orientation.Horizontal };
        void Dugme(string tur)
        {
            var b = new Button { Content = tur.Replace(" JOIN", ""), Padding = new Thickness(7, 2, 7, 2), FontSize = 11, MinHeight = 0, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            bool secili = _joinlar[indeks].JoinTuru == tur;
            b.SetResourceReference(BackgroundProperty, secili ? "VurguKoyuFircasi" : "PanelZeminFircasi");
            if (secili) { b.Foreground = Brushes.White; } else { b.SetResourceReference(ForegroundProperty, "SolukMetinFircasi"); }
            b.Click += (_, _) =>
            {
                _joinlar[indeks] = _joinlar[indeks] with { TurZorla = tur };
                JoinYolunuCiz();
                if (_aktifAdim == 4) OnizlemeyiTazele();
            };
            kap.Children.Add(b);
        }
        Dugme("INNER JOIN");
        Dugme("LEFT JOIN");
        return Dinamik(
            new Border
            {
                CornerRadius = new CornerRadius(7), Margin = new Thickness(2), BorderThickness = new Thickness(1),
                Child = kap, VerticalAlignment = VerticalAlignment.Center,
            },
            BorderBrushProperty, "KenarFircasi");
    }

    // ── v19-S22: Elle JOIN bağı ──────────────────────────────────────────────

    private void ElleTablo1_Secildi(object sender, SelectionChangedEventArgs e)
        => ElleKolon1.ItemsSource = ElleTabloKolonlari(ElleTablo1.SelectedItem as string);
    private void ElleTablo2_Secildi(object sender, SelectionChangedEventArgs e)
        => ElleKolon2.ItemsSource = ElleTabloKolonlari(ElleTablo2.SelectedItem as string);

    private List<string>? ElleTabloKolonlari(string? tam)
        => tam is not null && _tumTablolar.TryGetValue(tam, out SemaNesnesi? t)
            ? t.Kolonlar.Select(k => k.Ad).ToList() : null;

    private void ElleBagEkle_Click(object sender, RoutedEventArgs e)
    {
        if (ElleTablo1.SelectedItem is not string tam1 || !_tumTablolar.TryGetValue(tam1, out SemaNesnesi? t1)
            || ElleTablo2.SelectedItem is not string tam2 || !_tumTablolar.TryGetValue(tam2, out SemaNesnesi? t2)
            || ElleKolon1.SelectedItem is not string k1 || ElleKolon2.SelectedItem is not string k2)
        {
            Durum.Text = "⚠ Elle bağ için iki tablo ve iki kolon seçin.";
            return;
        }
        if (t1.TamAd.Equals(t2.TamAd, StringComparison.OrdinalIgnoreCase))
        {
            Durum.Text = "⚠ Bağın iki ucu farklı tablolar olmalı.";
            return;
        }
        // Ad=null → gerçek/güvenilir bağ gibi INNER JOIN (ad kuralı uyarısı yok).
        _elleFkler.Add(new YabanciAnahtar(t1.Sema, t1.Ad, [k1], t2.Sema, t2.Ad, [k2]));
        JoinlariHesapla(); // yeni bağ FK havuzuna girdi → yolu yeniden bul
        ElleBagListesiniCiz();
        if (_aktifAdim == 4)
            OnizlemeyiTazele();
        Durum.Text = $"✔ Elle bağ eklendi: {t1.Ad}.{k1} = {t2.Ad}.{k2}"
                   + (_baglanamayan.Count == 0 ? " (tüm tablolar bağlandı)" : "");
    }

    private void ElleBagListesiniCiz()
    {
        ElleBagListe.Children.Clear();
        for (int i = 0; i < _elleFkler.Count; i++)
        {
            YabanciAnahtar f = _elleFkler[i];
            ElleBagListe.Children.Add(OgeSatiri(
                $"{f.KaynakTablo}.{f.KaynakKolonlar[0]} = {f.HedefTablo}.{f.HedefKolonlar[0]}",
                i, _elleFkler, () => { ElleBagListesiniCiz(); JoinlariHesapla(); if (_aktifAdim == 4) OnizlemeyiTazele(); }));
        }
    }

    // ── Adım 3: WHERE / kural / ORDER BY kurucular ───────────────────────────

    private void KurucuSecicileriDoldur()
    {
        List<string> tabloAdlari = _sorguTablolari.Keys.OrderBy(k => k).ToList();
        WhereTablo.ItemsSource = tabloAdlari;
        SiralaTablo.ItemsSource = tabloAdlari;
        WhereBaglac.ItemsSource = new[] { "AND", "OR" };
        WhereBaglac.SelectedIndex = 0;
        WhereOperator.ItemsSource = WhereOperatorleri; WhereOperator.SelectedIndex = 0;
        SiralaYon.ItemsSource = new[] { "ASC ↑", "DESC ↓" }; SiralaYon.SelectedIndex = 0;
        KuralParam.ItemsSource = _satirlar.Where(s => s.Giris).Select(s => s.Ad).ToList();
        KuralParam.SelectedIndex = KuralParam.Items.Count > 0 ? 0 : -1;
        KuralOperator.ItemsSource = KuralOperatorleri; KuralOperator.SelectedIndex = 0;
    }

    private void WhereTablo_Secildi(object sender, SelectionChangedEventArgs e)
        => WhereKolon.ItemsSource = TabloKolonlari(WhereTablo.SelectedItem as string);
    private void SiralaTablo_Secildi(object sender, SelectionChangedEventArgs e)
        => SiralaKolon.ItemsSource = TabloKolonlari(SiralaTablo.SelectedItem as string);

    private List<string>? TabloKolonlari(string? tam)
    {
        if (tam is not null && _sorguTablolari.TryGetValue(tam, out SemaNesnesi? t))
            return t.Kolonlar.Select(k => k.Ad).ToList();
        return null;
    }

    private void WhereOperator_Secildi(object sender, SelectionChangedEventArgs e)
        => WhereDeger.IsEnabled = WhereOperator.SelectedItem as string is not ("IS NULL" or "IS NOT NULL");
    private void KuralOperator_Secildi(object sender, SelectionChangedEventArgs e)
        => KuralDeger.IsEnabled = KuralOperator.SelectedItem as string is not ("IS NULL" or "IS NOT NULL");

    private void WhereEkle_Click(object sender, RoutedEventArgs e)
    {
        if (WhereTablo.SelectedItem is not string tam || !_sorguTablolari.TryGetValue(tam, out SemaNesnesi? tablo)
            || WhereKolon.SelectedItem is not string kolonAd || tablo.Kolonlar.FirstOrDefault(k => k.Ad == kolonAd) is not { } kolon
            || WhereOperator.SelectedItem is not string op)
        {
            Durum.Text = "⚠ WHERE için tablo, kolon ve operatör seçin.";
            return;
        }
        bool degersiz = op is "IS NULL" or "IS NOT NULL";
        string baglac = _ekKosullar.Count == 0 ? "AND" : WhereBaglac.SelectedItem as string ?? "AND";
        _ekKosullar.Add(new SpEkKosul(tablo, kolon, op, degersiz ? null : WhereDeger.Text, baglac));
        WhereDeger.Clear();
        WhereListesiniCiz();
    }

    private void KuralEkle_Click(object sender, RoutedEventArgs e)
    {
        if (KuralParam.SelectedItem is not string param || KuralOperator.SelectedItem is not string op)
        {
            Durum.Text = "⚠ Kural için parametre ve operatör seçin.";
            return;
        }
        bool degersiz = op is "IS NULL" or "IS NOT NULL";
        _kurallar.Add(new SpKural(param, op, degersiz ? null : KuralDeger.Text,
            string.IsNullOrWhiteSpace(KuralMesaj.Text) ? null : KuralMesaj.Text));
        KuralDeger.Clear(); KuralMesaj.Clear();
        KuralListesiniCiz();
    }

    private void SiralaEkle_Click(object sender, RoutedEventArgs e)
    {
        if (SiralaTablo.SelectedItem is not string tam || !_sorguTablolari.TryGetValue(tam, out SemaNesnesi? tablo)
            || SiralaKolon.SelectedItem is not string kolonAd || tablo.Kolonlar.FirstOrDefault(k => k.Ad == kolonAd) is not { } kolon)
        {
            Durum.Text = "⚠ Sıralama için tablo ve kolon seçin.";
            return;
        }
        bool azalan = (SiralaYon.SelectedItem as string)?.Contains("DESC") == true;
        _siralamalar.Add(new SpSiralama(tablo, kolon, azalan));
        SiralaListesiniCiz();
    }

    private void WhereListesiniCiz()
    {
        WhereListe.Children.Clear();
        for (int i = 0; i < _ekKosullar.Count; i++)
        {
            SpEkKosul k = _ekKosullar[i];
            string bag = i == 0 ? "" : k.Baglac + " ";
            string deger = k.Operator is "IS NULL" or "IS NOT NULL" ? "" : $" {k.Deger}";
            WhereListe.Children.Add(OgeSatiri($"{bag}{k.Tablo.Ad}.{k.Kolon.Ad} {k.Operator}{deger}", i, _ekKosullar, WhereListesiniCiz));
        }
    }

    private void KuralListesiniCiz()
    {
        KuralListe.Children.Clear();
        for (int i = 0; i < _kurallar.Count; i++)
        {
            SpKural k = _kurallar[i];
            string deger = k.Operator is "IS NULL" or "IS NOT NULL" ? "" : $" {k.Deger}";
            string mesaj = string.IsNullOrWhiteSpace(k.Mesaj) ? "(otomatik mesaj)" : $"\"{k.Mesaj}\"";
            KuralListe.Children.Add(OgeSatiri($"IF @{k.Param} {k.Operator}{deger} → hata {mesaj}", i, _kurallar, KuralListesiniCiz));
        }
    }

    private void SiralaListesiniCiz()
    {
        SiralaListe.Children.Clear();
        for (int i = 0; i < _siralamalar.Count; i++)
        {
            SpSiralama s = _siralamalar[i];
            SiralaListe.Children.Add(OgeSatiri($"{s.Tablo.Ad}.{s.Kolon.Ad} {(s.Azalan ? "DESC" : "ASC")}", i, _siralamalar, SiralaListesiniCiz));
        }
    }

    private DockPanel OgeSatiri<T>(string metin, int indeks, List<T> liste, Action yenidenCiz)
    {
        var satir = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var sil = Dinamik(
            new Button
            {
                Content = "✕", Padding = new Thickness(5, 0, 5, 0), FontSize = 11, MinHeight = 0,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Kaldır",
            },
            ForegroundProperty, "TehlikeFircasi");
        sil.Click += (_, _) => { liste.RemoveAt(indeks); yenidenCiz(); };
        DockPanel.SetDock(sil, Dock.Left);
        satir.Children.Add(sil);
        satir.Children.Add(new TextBlock
        {
            Text = metin, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        });
        return satir;
    }

    // ── Adım 4: canlı önizleme + üret ────────────────────────────────────────

    private string TaslakUret()
    {
        var girisler = new List<SpGirisi>();
        var cikislar = new List<SpCikisi>();
        foreach (EslemeSatiri s in _satirlar)
        {
            if (s.Giris)
            {
                girisler.Add(new SpGirisi(s.Ad, s.SeciliAday));
            }
            else
            {
                string? toplama = s.Toplama?.SelectedIndex > 0 ? s.Toplama.SelectedItem as string : null;
                string ad = string.IsNullOrWhiteSpace(s.AsKutu?.Text) ? s.Ad : s.AsKutu!.Text.Trim();
                cikislar.Add(new SpCikisi(ad, s.SeciliAday, toplama));
            }
        }

        var secenekler = new SpSecenekleri(
            Top: int.TryParse(TopKutu.Text.Trim(), out int t) && t > 0 ? t : null,
            Distinct: Distinct.IsChecked == true,
            Siralamalar: _siralamalar);

        return SpSihirbazi.SpUret(
            SpAdi.Text.Trim().Length > 0 ? SpAdi.Text.Trim() : "spYeniSorgu",
            girisler, cikislar, _joinlar, _ekKosullar, _kurallar, secenekler);
    }

    private void OnizlemeyiTazele()
    {
        Onizleme.SyntaxHighlighting = EditorTema.Tanim(App.KoyuTemaAcik);
        Onizleme.Text = TaslakUret();
        int kural = _kurallar.Count, where = _ekKosullar.Count, sirala = _siralamalar.Count;
        Durum.Text = $"Canlı önizleme · {where} WHERE · {kural} kural · {sirala} sıralama"
                   + (_baglanamayan.Count > 0 ? "  ⚠ bağlanamayan tablo var" : "");
    }

    private void Uret_Click(object sender, RoutedEventArgs e)
    {
        if (_satirlar.All(s => s.Giris))
        {
            Durum.Text = "⚠ En az bir çıkış alanı gerekli.";
            return;
        }
        _sekmeAc($"CREATE {SpAdi.Text}", TaslakUret(), Veritabani.SelectedItem as string);
        Durum.Text = "🪄 Taslak editörde açıldı — gözden geçirip F5 ile oluşturun.";
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TaslakUret());
            Durum.Text = "📋 SQL panoya kopyalandı.";
        }
        catch (Exception)
        {
            Durum.Text = "⚠ Panoya kopyalanamadı.";
        }
    }
}
