using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SQLST.App.Converters;
using SQLST.App.ViewModels;
using SQLST.App.Views;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App;

public partial class MainWindow : Window // partial: GeneratedRegex
{
    private readonly MainViewModel _vm;
    private readonly Func<BaglantiPenceresi> _baglantiPenceresiGetir;
    private readonly List<ICSharpCode.AvalonEdit.TextEditor> _editorler = [];
    private readonly ISnippetDeposu _snippetDeposu;
    private ConnectionProfile? _profil;

    private readonly SQLST.Application.SemaKopyalamaServisi _semaKopyalama; // 📤 şema kopyalama (2026-07-31)
    private readonly IProfileStore _profilDeposu;
    private readonly ISchemaService _semaServisi;
    private readonly ILehceSaglayici _lehceSaglayici;
    private readonly Infrastructure.AktarimServisi _aktarimServisi;
    private readonly Infrastructure.MongoAktarimServisi _mongoAktarimServisi;
    private readonly Infrastructure.DosyaAktarimServisi _dosyaAktarimServisi;
    private readonly Infrastructure.SoapIstemcisi _soapIstemcisi;
    private readonly ISoapDeposu _soapDeposu;
    private readonly Infrastructure.RestIstemcisi _restIstemcisi; // 🌐 REST İstemcisi (v20-S8)
    private readonly IRestDeposu _restDeposu;
    private readonly IGeriAlDeposu _geriAlDeposu;
    private readonly ISecretProtector _protector; // SOAP ortam parolası şifreleme (v16)

    public MainWindow(
        MainViewModel vm, Func<BaglantiPenceresi> baglantiPenceresiGetir, ISnippetDeposu snippetDeposu,
        IAyarDeposu ayarDeposu, ISecretProtector protector,
        IProfileStore profilDeposu, ISchemaService semaServisi, ILehceSaglayici lehceSaglayici,
        Infrastructure.AktarimServisi aktarimServisi,
        Infrastructure.MongoAktarimServisi mongoAktarimServisi,
        Infrastructure.DosyaAktarimServisi dosyaAktarimServisi,
        Infrastructure.SoapIstemcisi soapIstemcisi,
        ISoapDeposu soapDeposu,
        Infrastructure.RestIstemcisi restIstemcisi,
        IRestDeposu restDeposu,
        IGeriAlDeposu geriAlDeposu,
        SQLST.Application.SemaKopyalamaServisi semaKopyalama)
    {
        _semaKopyalama = semaKopyalama;
        _profilDeposu = profilDeposu;
        _semaServisi = semaServisi;
        _lehceSaglayici = lehceSaglayici;
        _aktarimServisi = aktarimServisi;
        _mongoAktarimServisi = mongoAktarimServisi;
        _dosyaAktarimServisi = dosyaAktarimServisi;
        _soapIstemcisi = soapIstemcisi;
        _soapDeposu = soapDeposu;
        _restIstemcisi = restIstemcisi;
        _restDeposu = restDeposu;
        _geriAlDeposu = geriAlDeposu;
        _protector = protector;
        InitializeComponent();
        _vm = vm;
        _baglantiPenceresiGetir = baglantiPenceresiGetir;
        _snippetDeposu = snippetDeposu;
        DataContext = vm;
        // m.30: sekme görselleri canlı tutulur — kapanan sekmenin sunucusu atılsın (bellek) ve
        // seçim değişmeden kapanan sekmeler de yakalansın diye koleksiyon da dinlenir.
        _vm.Sekmeler.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(SekmeIcerikleriniEsitle);
        // m.10 fikir 2: şema önbelleği bayatlayınca (profil/bağlantı değişimi) lookup sözlükleri de gitmeli.
        _vm.OnbellekTemizlendi += () => _lookupOnbellek.Temizle();
        Loaded += (_, _) => SekmeIcerikleriniEsitle();
        Loaded += (_, _) => DonmaNobetcisiniBaslat(); // 🛩 v22-S1: UI donmasını SAYISIYLA loga yaz
        // 🤖 Asistan yapılandırması DOSYADAN (kullanıcı kararı 2026-07-26: "kimse değiştiremesin,
        // butonda olmasın") — ayar penceresi kaldırıldı; eski SQLite ayarı ilk açılışta dosyaya göçer.
        var asistanConfig = new AsistanConfigDosyasi(protector);
        vm.AsistanConfigOku = asistanConfig.Oku;
        vm.AsistanConfigYaz = asistanConfig.Yaz;
        // ⏪ Geri Al paketi (V15-S3): sekmeler Güvenli Yazma COMMIT'inde eski satırları buraya yazar.
        vm.GeriAlDeposu = geriAlDeposu;
        // Uzun sorgu bildirimi yalnız kullanıcı başka yerdeyken çıkar (V2-S2).
        vm.PencereAktifMi = () => IsActive;
        // WHERE'siz DML onayı (FG-6.2): VM sorar, görünüm iletişim kutusu açar (tehlikeli — odak Vazgeç'te).
        vm.YazmaOnayiIste = mesaj => Iletisim.Sor(this, "SQLST — WHERE'siz yazma", "WHERE'siz yazma çalıştırılsın mı?",
            mesaj, "Yine de çalıştır", IletisimTuru.Tehlike);
        // Edit modu "Show Script" (V2-S5): üretilen DML önizlenir, onay pencereden gelir.
        vm.DuzenlemeOnayiIste = script =>
            new DmlOnizlemePenceresi(script) { Owner = this }.ShowDialog() == true;
        // ⏱ SQL Agent (v23-S16): başlat/durdur/aç-kapat onayı.
        vm.AgentOnayiIste = soru => Iletisim.Sor(this, "SQLST — SQL Agent", soru.Baslik, soru.Mesaj, soru.OnayMetni,
            soru.Tehlikeli ? IletisimTuru.Tehlike : IletisimTuru.Soru);
        // ⏱ SQL Agent S3 (v23-S18): job oluşturma/düzenleme sihirbazı penceresi.
        vm.AgentSihirbaziGoster = sihirbaz => new AgentSihirbazPenceresi(sihirbaz) { Owner = this }.ShowDialog();
        // 🔎 FTS S4 (v23-S17): doldurma / değişiklik izleme / katalog işlemleri onayı.
        vm.FtsOnayiIste = soru => Iletisim.Sor(this, "SQLST — Full-Text", soru.Baslik, soru.Mesaj, soru.OnayMetni,
            soru.Tehlikeli ? IletisimTuru.Tehlike : IletisimTuru.Soru);
        // Script → Görsel (v6-S5): sorgu tam temsil edilemiyorsa ne atlanacağını gösterip sor.
        vm.GorseleCevirmeOnayiIste = mesaj => Iletisim.Sor(this, "SQLST — Görsele çevir",
            "Sorgu görsele tam çevrilemiyor", mesaj, "Yine de çevir", IletisimTuru.Uyari);
        // Metin arama (V5-S2): sonuca çift tıklanınca tanım yeni sekmede EŞLEŞEN SATIRDA açılır.
        // Sekme henüz görsel ağaca girmediğinden SatiraGit o an bağlı değildir (SaglayicilariBagla
        // editörün Loaded'ında çalışır) → imleç taşıma Loaded sonrasına ertelenir.
        vm.SatirdaAc = (sekme, satir) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            () => sekme.SatiraGit?.Invoke(satir));

        // Palet, ⇄ Karşılaştır'ı da açabilsin (V2-S10)
        vm.KarsilastirAc = () => PencereGoster(new KarsilastirmaPenceresi(_vm, _profil) { Owner = this });

        // Gömülü TSQL şeması koyu zeminde okunmaz (3. tur) — tema geçişinde canlı editörler
        // koyu/açık renklendirme tanımına geçirilir (EditorTema).
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.KoyuTema))
                foreach (ICSharpCode.AvalonEdit.TextEditor editor in _editorler)
                {
                    editor.SyntaxHighlighting = EditorTema.Tanim(vm.KoyuTema);
                    EditorSecimRengiUygula(editor);
                }
        };

        // Ctrl+P → "her yere atla" paleti (FG-2.9)
        var paletKomutu = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(paletKomutu, (_, _) => PaletiAc()));
        InputBindings.Add(new InputBinding(paletKomutu, new KeyGesture(Key.P, ModifierKeys.Control)));

        // Ctrl+S → aktif sorguyu .sql dosyasına kaydet (kullanıcı isteği 2026-07-21).
        var kaydetKomutu = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(kaydetKomutu, (_, _) => SeciliyiKaydet()));
        InputBindings.Add(new InputBinding(kaydetKomutu, new KeyGesture(Key.S, ModifierKeys.Control)));

        // Ctrl+O → .sql dosyasını sekmede aç (v20-S14, kullanıcı isteği 2026-08-07) — Ctrl+S'in aynası.
        var acKomutu = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(acKomutu, (_, _) => DosyaAc()));
        InputBindings.Add(new InputBinding(acKomutu, new KeyGesture(Key.O, ModifierKeys.Control)));
    }

    private void DosyaAc_Click(object sender, RoutedEventArgs e) => DosyaAc();

    /// <summary>
    /// 📂 .sql dosyalarını sekmelerde açar (Ctrl+O / ray düğmesi): kodlama BOM→sıkı UTF-8→1254
    /// sırasıyla çözülür (SqlDosyaKodlama — mojibake yasak); dev dosyada onay sorulur.
    /// </summary>
    private void DosyaAc()
    {
        var diyalog = new OpenFileDialog
        {
            Filter = "SQL dosyası (*.sql)|*.sql|Metin dosyası (*.txt)|*.txt|Tüm dosyalar (*.*)|*.*",
            Multiselect = true,
        };
        if (diyalog.ShowDialog(this) == true)
            DosyalariAc(diyalog.FileNames);
    }

    private void DosyalariAc(IEnumerable<string> yollar)
    {
        foreach (string yol in yollar)
        {
            try
            {
                var bilgi = new FileInfo(yol);
                if (bilgi.Length > 10 * 1024 * 1024
                    && !Iletisim.Sor(this, "SQLST — Dosya Aç", "Büyük dosya",
                        $"{bilgi.Name} {bilgi.Length / (1024.0 * 1024):0.#} MB — büyük dosya editörü yavaşlatabilir.",
                        "Yine de aç", IletisimTuru.Uyari))
                    continue;

                KodlanmisMetin metin = SqlDosyaKodlama.Coz(File.ReadAllBytes(yol));
                _vm.DosyadanSekmeAc(Path.GetFileName(yol), metin.Metin, metin.KodlamaAdi, metin.Uyari);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Iletisim.Hata(this, "SQLST — Dosya Aç", $"{Path.GetFileName(yol)} açılamadı", ex);
            }
        }
    }

    /// <summary>Gezgin'den pencereye .sql sürükle-bırak (v20-S14) — aynı açılış yolundan geçer.</summary>
    private void PencereyeDosyaBirakildi(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] yollar)
            return;
        string[] uygun = [.. yollar.Where(y =>
            Path.GetExtension(y).ToLowerInvariant() is ".sql" or ".txt")];
        if (uygun.Length == 0)
        {
            _vm.Durum = "Sürüklenen dosya .sql/.txt değil — yok sayıldı.";
            return;
        }
        DosyalariAc(uygun);
        e.Handled = true;
    }

    /// <summary>
    /// Aktif sekmenin SQL'ini .sql dosyasına kaydeder (Ctrl+S). Sorgu sekmesinde editör metni;
    /// Görsel Sorgu sekmesinde ise üretilen SCRIPT kaydedilir — kullanıcı bu dönüşüm için önceden
    /// uyarılır (kullanıcı isteği 2026-07-21). Diğer sekmelerde sessizce yok sayılır.
    /// </summary>
    private void SeciliyiKaydet()
    {
        string sql;
        string varsayilanAd;
        SorguSekmesiViewModel? kaydedilen = null; // kaydedince sekme adını dosya adına çevirmek için

        switch (_vm.SeciliSekme)
        {
            case SorguSekmesiViewModel sorgu:
                sql = sorgu.Belge.Text;
                varsayilanAd = DosyaAdinaCevir(sorgu.Baslik);
                kaydedilen = sorgu;
                break;

            case GorselSorguSekmesiViewModel gorsel:
                if (gorsel.ScriptiUret() is not { } uretilen)
                    return; // üretilemedi (tuval boş / profil yok) — VM Bilgi'yi doldurdu
                if (!Iletisim.Sor(this, "SQLST — Görsel sorguyu kaydet", "Script olarak kaydedilecek",
                        "Görsel sorgu doğrudan kaydedilemez; üretilen SCRIPT (SQL) hali kaydedilir.", "💾 Kaydet"))
                    return;
                sql = uretilen;
                varsayilanAd = "gorsel-sorgu";
                break;

            default:
                return; // kaydedilecek bir sorgu yok
        }

        var diyalog = new SaveFileDialog
        {
            Filter = "SQL dosyası (*.sql)|*.sql|Tüm dosyalar (*.*)|*.*",
            FileName = varsayilanAd.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? varsayilanAd : varsayilanAd + ".sql",
        };
        if (diyalog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(diyalog.FileName, sql, new System.Text.UTF8Encoding(true));
            _vm.Durum = $"Kaydedildi: {Path.GetFileName(diyalog.FileName)}";
            // Kaydedilen sorgu sekmesi artık dosya adını taşısın (kullanıcı isteği 2026-07-23).
            if (kaydedilen is not null)
            {
                kaydedilen.Baslik = Path.GetFileName(diyalog.FileName);
                kaydedilen.BaslikOtomatikMi = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(this, "SQLST", "Dosya kaydedilemedi", ex);
        }
    }

    /// <summary>Sekme başlığını geçerli bir dosya adına indirger (geçersiz karakterleri '_' yapar).</summary>
    private static string DosyaAdinaCevir(string baslik)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            baslik = baslik.Replace(c, '_');
        return string.IsNullOrWhiteSpace(baslik) ? "sorgu" : baslik;
    }

    /// <summary>
    /// Bağımsız (modal olmayan) araç penceresini açar ve KAPANDIĞINDA ana pencereyi yeniden
    /// etkinleştirir (kullanıcı bulgusu 2026-08-09: bir araç penceresi X ile kapanınca ana SQLST
    /// penceresi başka uygulamaların ARKASINA düşüyordu). Owner VERİLMEZ deseni (v19-S21: bağımsız
    /// pencere → Alt+Tab serbest) korunur; öne alma Closed'da Activate() ile sağlanır. Owner'lı
    /// pencereler için de zararsızdır (sahip zaten yeniden etkinleşir) — tek yol, tutarlı davranış.
    /// </summary>
    /// <summary>
    /// Pencereyi MODELSİZ gösterir (kullanıcı ana pencereyle çalışmayı sürdürebilsin).
    /// <see cref="PencereKapatma.Kur"/> ŞART (v22-S4 saha turu-4 m.1): modelsiz pencerede
    /// <c>IsCancel</c> düğmesi ve Esc WPF tarafından işlenmez — kapat düğmesi ölü kalır.
    /// </summary>
    private void PencereGoster(Window pencere)
    {
        PencereKapatma.Kur(pencere);
        pencere.Closed += (_, _) => Activate();
        pencere.Show();
    }

    private void PaletiAc()
        => new PaletPenceresi(_vm.PaletOgeleriKur()) { Owner = this }.ShowDialog();

    /// <summary>Üst bardaki arama/komut kutusu (yerleşim v2): tıklama = Ctrl+P paleti.</summary>
    private void PaletKutusu_Click(object sender, RoutedEventArgs e) => PaletiAc();

    /// <summary>Bağlantı rozeti tıklaması (yerleşim v2): ⏏ ile aynı yol — bağlantı ekranı.</summary>
    private void BaglantiRozeti_Tik(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => BaglantiDegistir_Click(sender, e);

    /// <summary>Sol ray hover (2026-07-25): sabit değilken üstüne gelince açılır, çekilince kapanır.</summary>
    private void Ray_MouseEnter(object sender, MouseEventArgs e) => _vm.RayHover(icinde: true);

    private void Ray_MouseLeave(object sender, MouseEventArgs e) => _vm.RayHover(icinde: false);

    /// <summary>Kapanışta açık sekmeler kaydedilir — sonraki açılışta geri gelir (V2-S2, FG-3.6).</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _vm.OturumuKaydet();
        base.OnClosing(e);
    }

    /// <summary>Aktif profil (S3+ dilimleri sorgu için buradan okuyacak).</summary>
    public ConnectionProfile? AktifProfil => _profil;

    /// <summary>Bağlantı ekranından gelen profili uygular: bağlam metni dolar, şema yüklenir.</summary>
    public void ProfilUygula(ConnectionProfile profil)
    {
        _profil = profil;

        string kullanici = profil.Kimlik == KimlikTuru.Windows
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : profil.KullaniciAdi ?? "";
        string rozetler = profil.SaltOkunur ? "  ·  🔒 salt-okunur" : "";
        BaglamMetni.Text = $"⚡ {profil.Sunucu}  ·  {kullanici}{rozetler}";
        Title = $"SQLST — {profil.Ad}";

        _ = _vm.ProfilYukleAsync(profil); // hata yönetimi VM içinde (Durum'a yazar), fire-and-forget güvenli

        // 📂 v23-S7: çift tık / "Birlikte aç" ile gelen .sql dosyaları BAĞLANTI KURULUNCA açılır
        // (pencere gösterilmeden sekme açılamazdı). Liste ilk kullanımda boşaltılır — bağlantı
        // DEĞİŞİMİNDE aynı dosyalar yeniden açılmaz.
        if (App.BekleyenDosyalar is { Length: > 0 } bekleyen)
        {
            App.BekleyenDosyalar = null;
            DosyalariAc(bekleyen);
        }
    }

    private async void Yenile_Click(object sender, RoutedEventArgs e)
        => await _vm.YenileAsync();

    /// <summary>Geçmiş kaydına çift tık → sorgu yeni sekmede (FG-3.7; çalıştırmaz, sadece açar).</summary>
    private void Gecmis_CiftTik(object sender, MouseButtonEventArgs e)
    {
        if (((ListBox)sender).SelectedItem is GecmisGorunumu kayit)
            _vm.GecmistenSekmeAc(kayit);
    }

    // ---- Karşılaştırma araçları + HTML rapor (V2-S9) ----

    /// <summary>
    /// "⇄ Karşılaştır" → v7 Karşılaştırma sekmesini açar (iki bağlantı · şema/veri farkı). Kullanıcı
    /// kararı 2026-07-21: ayrı düğme yok, mevcut Karşılaştır bunu açar. Eski sonuç-diff/snapshot
    /// penceresi kod tabanında duruyor (palete/ileride bağlanabilir).
    /// </summary>
    private void Karsilastir_Click(object sender, RoutedEventArgs e)
    {
        // Kullanıcı isteği 2026-08-09: sekme yerine AYRI PENCERE (SOAP istemcisi gibi). VM'i MainViewModel
        // kurar (özel bağımlılıklar orada); pencere YukleAsync'i kendi çağırır. Owner VERİLMEZ (v19-S21) +
        // kapanınca ana pencere öne alınır (PencereGoster).
        if (_vm.KarsilastirmaVmKur() is not { } vm)
            return;
        PencereGoster(new Views.KarsilastirmaSekmePenceresi(vm));
    }

    /// <summary>🧾 Log analizi SEKMESİNİ açar (kullanıcı isteği 2026-08-09: ayrı pencere yerine sekme).</summary>
    private void LogAnaliz_Click(object sender, RoutedEventArgs e) => _vm.LogAnalizAc();

    /// <summary>🔍 Profiler sekmesini açar (v23-S1 — yalnız MSSQL, ProfilerGorunur kapısı).</summary>
    /// <summary>🔎 FTS sekmesi (v23 S1+S2) — ray düğmesi; tek örnek VM'de.</summary>
    private void Fts_Click(object sender, RoutedEventArgs e) => _vm.FtsAc();

    /// <summary>⏱ SQL Agent sekmesi (v23-S16) — ray düğmesi; tek örnek VM'de.</summary>
    private void Agent_Click(object sender, RoutedEventArgs e) => _vm.AgentAc();

    /// <summary>"▶ Başlat ▾" — baştan / seçili adımdan seçimi açılır menüyle.</summary>
    private void AgentBaslatMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } dugme)
        {
            menu.DataContext = dugme.DataContext;
            menu.PlacementTarget = dugme;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void Profiler_Click(object sender, RoutedEventArgs e) => _vm.ProfilerAc();

    /// <summary>🕸 v23-S3: seçili satır kilitlenme raporuysa görsel şema penceresini açar
    /// (düğme + olay gridine çift tık aynı yolu kullanır; deadlock değilse sessizce yok sayılır).</summary>
    private void ProfilerDeadlockAc_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfilerSekmesiViewModel vm
            || !vm.DeadlockSecili || vm.TamMetin is not { Length: > 0 } xml)
            return;
        PencereGoster(new Views.DeadlockPenceresi(xml, vm.SeciliZaman));
    }

    /// <summary>⧉ v23-S5: seçili Batch/RPC sorgusunu (grup örneği dahil) yeni sorgu sekmesinde
    /// açar — olayın veritabanı önseçili; kullanıcı planına bakar, düzeltir, çalıştırır.</summary>
    private void ProfilerSekmedeAc_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfilerSekmesiViewModel vm
            || !vm.SekmedeAcilabilir || vm.TamMetin is not { Length: > 0 } sql)
            return;
        _vm.SekmeAc("profiler.sql", sql, vm.SekmeVeritabani);
    }

    // ── 🔍 Profiler dışa aktarma (K3: CSV + pano; VM veriyi üretir, pencere I/O — LogAnaliz deseni) ──
    private void ProfilerKopyala_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfilerSekmesiViewModel vm)
            return;
        if (!vm.SonucVar) { vm.DurumBildir("Kopyalanacak olay yok."); return; }
        try { Clipboard.SetText(vm.SatirMetni('\t')); vm.DurumBildir($"{vm.Olaylar.Count:N0} olay panoya kopyalandı."); }
        catch (Exception ex) { vm.DurumBildir($"Kopyalanamadı: {ex.Message}"); }
    }

    private void ProfilerCsv_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfilerSekmesiViewModel vm)
            return;
        if (!vm.SonucVar) { vm.DurumBildir("Aktarılacak olay yok."); return; }
        var kutu = new SaveFileDialog { Filter = "CSV dosyası (*.csv)|*.csv", FileName = "profiler-akisi.csv" };
        if (kutu.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(kutu.FileName, vm.SatirMetni(',', csv: true),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            vm.DurumBildir($"{vm.Olaylar.Count:N0} olay CSV'ye aktarıldı: {kutu.FileName}");
        }
        catch (Exception ex) { vm.DurumBildir($"CSV yazılamadı: {ex.Message}"); }
    }

    // ── 🧾 Log Analizi sekmesi görünüm-hizmetleri (VM veriyi/sorguyu üretir, pencere I/O'yu yapar;
    //     Karşılaştırma sekmesindeki desen) ──
    private void LogGrid_CiftTik(object sender, MouseButtonEventArgs e) => _ = LogDetayAcAsync(sender);
    private void LogDetay_Click(object sender, RoutedEventArgs e) => _ = LogDetayAcAsync(sender);

    /// <summary>Seçili log grubunun HAM kayıtlarını (VM getirir) <see cref="Views.LogDetayPenceresi"/>'nde açar.</summary>
    private async Task LogDetayAcAsync(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogAnalizSekmesiViewModel vm)
            return;
        (ResultSetData? rs, string baslik, int sayi, string? zamanKolon) = await vm.DetayGetirAsync();
        if (rs is null)
            return;
        // Owner VERİLMEZ + kapanınca ana pencere öne alınır (PencereGoster).
        PencereGoster(new Views.LogDetayPenceresi(baslik, sayi, rs, zamanKolon));
    }

    private void LogKopyala_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogAnalizSekmesiViewModel vm)
            return;
        if (!vm.SonucVar) { vm.DurumBildir("Kopyalanacak sonuç yok."); return; }
        try { Clipboard.SetText(vm.SatirMetni('\t')); vm.DurumBildir($"{vm.Gruplar.Count} satır panoya kopyalandı."); }
        catch (Exception ex) { vm.DurumBildir($"Kopyalanamadı: {ex.Message}"); }
    }

    private void LogCsv_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogAnalizSekmesiViewModel vm)
            return;
        if (!vm.SonucVar) { vm.DurumBildir("Aktarılacak sonuç yok."); return; }
        var kutu = new SaveFileDialog { Filter = "CSV dosyası (*.csv)|*.csv", FileName = "log-analizi.csv" };
        if (kutu.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(kutu.FileName, vm.SatirMetni(',', csv: true),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            vm.DurumBildir($"{vm.Gruplar.Count} satır CSV'ye aktarıldı: {kutu.FileName}");
        }
        catch (Exception ex) { vm.DurumBildir($"CSV yazılamadı: {ex.Message}"); }
    }

    // ── 🧾 Log Analizi: EK KOLONLAR (v22-S12 "birden fazla kolonu yan yana") ──
    // WPF DataGridColumn'ları veriye bağlanamaz (DataGrid.Columns DP değil) — bilinen sınır. VM,
    // koşu bitince EkKolonBasliklari'nı yükseltir; buradaki köprü grid'e kolonları o an ekler.
    // Sekme şablonu HER sekme geçişinde yeniden kurulduğu için Loaded'da (a) mevcut analiz kolonları
    // yeniden çizilir, (b) VM dinlenmeye alınır; Unloaded aboneliği bırakır (sızıntı/çift abone olmaz).

    /// <summary>Log grid'inin XAML'de tanımlı SABİT kolon sayısı: Kez · YENİ · İlk · Son · Örnek mesaj.
    /// Bunun ötesindeki her kolon dinamik ek kolondur (yeniden kurulurken sökülür).</summary>
    private const int LogSabitKolonSayisi = 5;

    private void LogGrid_Yuklendi(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.DataContext is not LogAnalizSekmesiViewModel vm)
            return;
        LogGridAboneligiBirak(grid); // Loaded üst üste gelirse (tema/şablon) çift abonelik olmasın
        System.ComponentModel.PropertyChangedEventHandler isleyici = (_, a) =>
        {
            if (a.PropertyName == nameof(LogAnalizSekmesiViewModel.EkKolonBasliklari))
                LogEkKolonlariUygula(grid, vm);
        };
        vm.PropertyChanged += isleyici;
        grid.Tag = (vm, isleyici);      // Unloaded'da çözebilmek için (DataContext o an güvenilmez)
        LogEkKolonlariUygula(grid, vm); // sekmeye dönüşte son analizin ek kolonları geri gelsin
    }

    private void LogGrid_Bosaltildi(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid)
            LogGridAboneligiBirak(grid);
    }

    private static void LogGridAboneligiBirak(DataGrid grid)
    {
        if (grid.Tag is (LogAnalizSekmesiViewModel vm, System.ComponentModel.PropertyChangedEventHandler isleyici))
        {
            vm.PropertyChanged -= isleyici;
            grid.Tag = null;
        }
    }

    /// <summary>Sabit kolonların ötesini söküp VM'in güncel ek kolon başlıklarını ekler. Hücre,
    /// Örnek mesaj kolonuyla aynı biçimdir: tek satıra indirilmiş metin + tam değer ToolTip'te.</summary>
    private static void LogEkKolonlariUygula(DataGrid grid, LogAnalizSekmesiViewModel vm)
    {
        while (grid.Columns.Count > LogSabitKolonSayisi)
            grid.Columns.RemoveAt(grid.Columns.Count - 1);
        for (int i = 0; i < vm.EkKolonBasliklari.Count; i++)
        {
            var metin = new FrameworkElementFactory(typeof(TextBlock));
            metin.SetBinding(TextBlock.TextProperty, new Binding($"EkDegerler[{i}]")
            {
                Converter = Converters.HucreMetniConverter.Ornek, // ⏎'li metin tek satıra iner (m.9 koruması dahil)
            });
            metin.SetBinding(FrameworkElement.ToolTipProperty, new Binding($"EkDegerler[{i}]"));
            metin.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            metin.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = vm.EkKolonBasliklari[i],
                Width = new DataGridLength(180),
                CellTemplate = new DataTemplate { VisualTree = metin },
            });
        }
    }

    /// <summary>📦 Paket Aktarım penceresi (v12-S2): kaynak=bağlı profil; hedef=kayıtlı profilden.
    /// Mongo'da (v12-S5) koleksiyon→koleksiyon arşivleme penceresi açılır — aile karışmaz.</summary>
    private void PaketAktarim_Click(object sender, RoutedEventArgs e)
    {
        if (_profil is null)
            return;
        if (_profil.Motor == MotorTuru.Mongo)
        {
            PencereGoster(new Views.MongoAktarimPenceresi(_profil, _vm.LogAnalizVeritabanlari, _vm.LogAnalizAktifVeritabani,
                _vm.LogAnalizTablolariAsync, _profilDeposu, _semaServisi, _mongoAktarimServisi)
                { Owner = this });
            return;
        }

        PencereGoster(new Views.AktarimPenceresi(_profil, _vm.LogAnalizVeritabanlari, _vm.LogAnalizAktifVeritabani,
            _vm.LogAnalizTablolariAsync, _profilDeposu, _semaServisi, _lehceSaglayici, _aktarimServisi)
            { Owner = this });
    }

    /// <summary>🔌 SOAP İstemcisi (v14-S2): WSDL → operasyon → zarf → çağrı — SP saran WCF
    /// servislerini AYNI araçta test etme (beyin fırtınası kararı 2026-07-26).</summary>
    private void SoapIstemci_Click(object sender, RoutedEventArgs e)
        // Depo + AI köprüleri BURADAN geçer (kullanıcı bulgusu 2026-07-26: köprüsüz açılınca
        // ortam/geçmiş boş kalıyor, kaydetme çalışmıyordu).
        // v19-S21 (kullanıcı bulgusu 2026-08-04): Owner VERİLMEZ — sahipli pencere ana pencerenin
        // hep üstünde kalır, Alt+Tab ile ana pencereye geçilemiyordu. Bağımsız (taskbar'da ayrı,
        // Alt+Tab ile serbestçe geçilir) yapılır; kapanınca ana pencere öne alınır (PencereGoster).
        => PencereGoster(new Views.SoapIstemciPenceresi(_soapIstemcisi, _soapDeposu,
            (istek, yanit, fault) => _vm.AsistanSoapYorumlaAsync(istek, yanit, fault),
            _protector));

    /// <summary>🌐 REST İstemcisi (v20-S8): HTTP/REST test — SOAP'ın kardeşi. Depo + AI köprüsü buradan
    /// geçer; Owner VERİLMEZ (v19-S21 Alt+Tab), kapanınca ana pencere öne alınır. Şifreleme depoda (DPAPI).</summary>
    private void RestIstemci_Click(object sender, RoutedEventArgs e) => RestPenceresiAc();

    /// <summary>
    /// REST penceresini açar. v20-S12: bağlı profil varsa "Tabloya kaydet" için DB bağımlılıkları
    /// geçilir (profil + aktif DB + lehçe + Excel/TXT içe aktarımıyla PAYLAŞILAN DosyaAktarimServisi);
    /// yoksa null → düğme gizli. Mongo şemasızdır (CREATE TABLE yok) → onu da dışarıda bırak.
    /// (v22-S1 m.10: "toplu kaynak" parametresi kaldırıldı — grid'den REST'e gönderme özelliği silindi.)
    /// </summary>
    private void RestPenceresiAc()
        => PencereGoster(new Views.RestIstemciPenceresi(_restIstemcisi, _restDeposu,
            (ozet, govde, durum) => _vm.AsistanRestYorumlaAsync(ozet, govde, durum),
            _profil is { Motor: not MotorTuru.Mongo } p
                ? new Views.RestTabloKaydiBaglami(p, _vm.LogAnalizAktifVeritabani,
                    _lehceSaglayici.Getir(p.Motor), _dosyaAktarimServisi)
                : null,
            // v20-S13: SQL motorunda "değişken SQL'den çöz" için scalar sorgu delegesi (Mongo hariç).
            _profil is { Motor: not MotorTuru.Mongo }
                ? _vm.RestScalarSorguAsync
                : (Func<string, Task<string>>?)null,
            // v20-S13: "Yanıt ↔ DB karşılaştır" için tam-satır sorgu delegesi (Mongo hariç).
            _profil is { Motor: not MotorTuru.Mongo }
                ? _vm.RestSorguSonucAsync
                : (Func<string, Task<(IReadOnlyList<string>, IReadOnlyList<object?[]>)>>?)null,
            // v20-S13: "tarifle → istek üret" için AI delegesi (motordan bağımsız — DB gerektirmez).
            aciklama => _vm.AsistanRestIstekUretAsync(aciklama),
            // v20-S13 madde 6: "yanıttan CREATE TABLE öner" için AI delegesi (görsel tasarımcıya iletilir).
            veriOzeti => _vm.AsistanRestSemaOnerAsync(veriOzeti),
            // v20-S13 madde 7: iki yanıtı AI ile diff yorumla.
            (onceki, simdiki) => _vm.AsistanRestDiffAsync(onceki, simdiki)));

    // v22-S1 (saha turu-2 m.10): "Sorgu sonucu → REST" (ResteGonder_Click) KALDIRILDI — kullanıcı
    // "işlevli bir kullanışı yok" dedi. Grid butonu, REST penceresindeki 🔁 Toplu Gönder, toplu sonuç
    // penceresi ve RestTopluGenisletici birlikte silindi (yarım/erişilemez özellik bırakmamak için).

    /// <summary>🔁 LINQ ⇄ SQL (v20-S11; m.22 devamı 2026-08-14: pencere değil SEKME olarak açılır —
    /// içerik AracSekmesiViewModel ile sekmede barınır, açıksa ona geçilir).</summary>
    private void LinqSql_Click(object sender, RoutedEventArgs e)
        => _vm.AracSekmesiAcVeyaSec("🔁 LINQ ⇄ SQL", () => new Views.LinqSqlPenceresi(
            () => _vm.LinqBaglamiAsync(),
            (sql, db) => _vm.LinqSqlSekmedeAcAsync(sql, db)));

    /// <summary>⏪ Geri Al Paketleri (V15-S4, BF-1): eski hali göster + ters DML'i sekmede aç.</summary>
    private void GeriAl_Click(object sender, RoutedEventArgs e)
        => PencereGoster(new Views.GeriAlPenceresi(_geriAlDeposu,
            (baslik, sql, db) => _vm.SekmeAc(baslik, sql, db)) { Owner = this });

    /// <summary>📥 Excel/TXT İçe Aktar penceresi (v13-S2/S3): dosya → önizleme → hedef tabloya INSERT.</summary>
    private void IceAktar_Click(object sender, RoutedEventArgs e)
    {
        if (_profil is null)
            return;
        PencereGoster(new Views.DosyaImportPenceresi(_profil, _vm.LogAnalizVeritabanlari, _vm.LogAnalizAktifVeritabani,
            _vm.LogAnalizTablolariAsync, _lehceSaglayici, _dosyaAktarimServisi,
            new SQLST.Infrastructure.FarkOkumaServisi(_lehceSaglayici),
            (baslik, sql, db) => _vm.SekmeAc(baslik, sql, db),
            // BF-3 (kod inceleme): tam eşitlemede computed/rowversion'ı dışlamak + identity INSERT'i
            // sarmak için hedef tablonun düzenleme meta'sını (identity/computed bayrakları) getir.
            async nesne => _profil is null ? null
                : await _semaServisi.DuzenlemeMetaAsync(_profil, nesne, CancellationToken.None)) { Owner = this });
    }

    // EditSorgu_Click / EditSorguPenceresi (v6a) EMEKLİ — kullanıcı düzeltmesi 2026-07-26:
    // "sorguyu aynı pencerede yazıp sonucunu düzenlemek istiyorum" → sekme içi FiltreSql
    // (DuzenlemeSekmesiViewModel) ayrı pencerenin yerini aldı.

    /// <summary>
    /// Denetim izinde çift tık (v22-S3 saha turu-3 m.2 — kullanıcı: "çift tıklayınca sorguyu
    /// görebilmem için pencere açılsın, normal gridimizde olduğu gibi"): kaydın TAM SQL'ini
    /// sonuç gridiyle aynı <see cref="Views.HucrePenceresi"/>'nde açar (kopyalanabilir, JSON'sa
    /// girintilenir). Başlık çubuğunda hangi kayıt olduğu yazar — grid 30 satır gösterirken
    /// açılan pencerenin hangisi olduğu kaybolmasın.
    /// </summary>
    private void DenetimGrid_CiftTik(object sender, MouseButtonEventArgs e)
    {
        // Başlık/kaydırma çubuğu çift tıklarını ele: yalnız hücre üstündeyken aç (sonuç gridiyle aynı kapı)
        if (e.OriginalSource is not DependencyObject kaynak || UstEleman<DataGridCell>(kaynak) is null)
            return;
        if (sender is not DataGrid grid || grid.SelectedItem is not DenetimKaydiGorunumu kayit)
            return;

        e.Handled = true;
        PencereGoster(new Views.HucrePenceresi(kayit.Sql)
        {
            Owner = this,
            Title = $"SQLST — {kayit.Zaman} · {kayit.Kullanici} · {kayit.Tur}",
        });
    }

    /// <summary>Denetim izini (görünen kayıtlar) CSV'ye aktarır (v10-S4 — paylaşımlı). Excel: ';' + UTF-8 BOM.</summary>
    private void DenetimCsv_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DenetimIziViewModel vm)
            return;
        var diyalog = new SaveFileDialog
        {
            Filter = "CSV dosyası (*.csv)|*.csv",
            FileName = $"denetim-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        static string A(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Zaman;Kullanıcı;İşlem;Veritabanı;Durum;SüreMs;Satır;SQL");
        foreach (DenetimKaydiGorunumu d in vm.Kayitlar)
            sb.AppendLine(string.Join(';', A(d.Zaman), A(d.Kullanici), A(d.Tur), A(d.Veritabani),
                A(d.Durum), d.SureMs, d.SatirSayisi, A(d.Sql.Replace('\r', ' ').Replace('\n', ' '))));

        File.WriteAllText(diyalog.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
        vm.Bilgi = $"CSV kaydedildi: {Path.GetFileName(diyalog.FileName)}";
    }

    // 🔀 Karşılaştırma sekmesi AYRI PENCEREYE taşındı (2026-08-09): SagParola_Changed +
    // Şema/Veri eşitleme + CSV işleyicileri artık KarsilastirmaSekmePenceresi.xaml.cs içinde.

    /// <summary>
    /// V5-S1: sorgunun execution plan'ını ayrı sekmede açar. Tahmini/gerçek ayrımı
    /// kullanıcıya sorulmaz — motorun verebildiği plan alınır (bkz. MainViewModel.PlanAcAsync).
    /// </summary>
    private async void Plan_Click(object sender, RoutedEventArgs e)
    {
        // Düğmeye basılan sekme her zaman seçili sekmedir; yine de VM'e onu seçtirip
        // istemeden başka sekmenin planını almayalım.
        if (((FrameworkElement)sender).DataContext is SorguSekmesiViewModel sekme)
            _vm.SeciliSekme = sekme;
        await _vm.PlanAcAsync();
    }

    /// <summary>BF-7: sorgunun planını tek Türkçe "neden yavaş?" raporuna çevirip pencerede gösterir.</summary>
    private async void NedenYavas_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SorguSekmesiViewModel sekme)
            _vm.SeciliSekme = sekme;

        (SQLST.Application.YavaslikRaporu? rapor, string ad, string? db, string? _) = await _vm.NedenYavasAsync();
        if (rapor is null)
            return; // durum çubuğu nedeni gösterdi

        new Views.NedenYavasPenceresi(rapor, ad,
            script => _vm.SekmeAc("neden-yavas.sql", script, db)) { Owner = this }.ShowDialog();
    }

    /// <summary>Ö6: sekmenin son sonucunu tek HTML dosyasına aktarır.</summary>
    /// <summary>
    /// Görsel ağaçta yukarı yürüyerek düğmenin ait olduğu sorgu sekmesi VM'ini bulur.
    /// Rapor/Tümünü dışa aktar butonları set şablonuna taşınınca (2026-07-23) DataContext'leri
    /// SonucSetiGorunumu oldu — doğrudan cast sessizce çakılıyordu ("çalışmıyor" bulgusu).
    /// </summary>
    private static SorguSekmesiViewModel? SekmeVmBul(object sender)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: SorguSekmesiViewModel vm })
                return vm;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private async void HtmlRapor_Click(object sender, RoutedEventArgs e)
    {
        if (SekmeVmBul(sender) is not SorguSekmesiViewModel sekme)
            return;
        if (sekme.SonCalisanSql is null || sekme.SonucSetleri.Count == 0)
        {
            _vm.Durum = "Rapor için önce bu sekmede bir sorgu çalıştırın.";
            return;
        }

        var diyalog = new SaveFileDialog
        {
            Filter = "HTML raporu (*.html)|*.html",
            FileName = $"sqlst-rapor-{DateTime.Now:yyyyMMdd-HHmmss}.html",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        try
        {
            string html = HtmlRaporYazici.Yaz(
                _profil?.Sunucu ?? "-", sekme.SecilenVeritabani, sekme.SonCalisanSql,
                sekme.SureMetni,
                [.. sekme.SonucSetleri.Select(s => (s.Baslik, s.Tablo))],
                DateTime.Now);
            await HtmlRaporYazici.DosyayaYazAsync(html, diyalog.FileName);
            _vm.Durum = $"HTML raporu kaydedildi: {Path.GetFileName(diyalog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(this, "SQLST", "Rapor yazılamadı", ex);
        }
    }

    // ---- Kısayol kılavuzu (F1 / üst şerit düğmesi) ----

    private void Kisayollar_Click(object sender, RoutedEventArgs e) => KisayollariGoster();

    private void Kisayollar_Executed(object sender, ExecutedRoutedEventArgs e) => KisayollariGoster();

    private void KisayollariGoster()
        => new KisayolPenceresi { Owner = this }.ShowDialog();

    /// <summary>
    /// Kod parçası yönetimi (V5-S4). Kapanışta değil, her kayıtta ana pencerenin snippet
    /// önbelleği tazelenir — kullanıcı pencereyi açık bırakıp editöre dönebilir.
    /// </summary>
    /// <summary>
    /// Auto-refresh anahtarı (V5-S4). Kutunun kendi işaretini doğrudan bağlamıyoruz: VM
    /// isteği REDDEDEBİLİR (yazma sorgusu) ve o zaman kutunun geri dönmesi gerekir —
    /// bu yüzden karar VM'de verilir, kutu sonucu yansıtır.
    /// </summary>
    private void OtoYenile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox kutu || kutu.DataContext is not SorguSekmesiViewModel sekme)
            return;

        sekme.OtoYenileAcikDegistir(kutu.IsChecked == true);
        kutu.IsChecked = sekme.OtoYenileAcik;   // reddedildiyse kutu geri döner
    }

    /// <summary>"Tablolar" grubu dışında ya da MongoDB'de (SQL dışı) sağ-tık menüsünü iptal eder (v8).</summary>
    private void TabloGrubuMenu_Acilyor(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        // Üreteç artık motor-farkında (MSSQL/PG/MySQL/Oracle) → SQL ailesinin dördünde de açılır.
        // AktifLehce yalnız Mongo/profil-yoksa null'dur; menü orada anlamsız (koleksiyon şemasızdır).
        if ((sender as FrameworkElement)?.DataContext is not GezginGrubu grup
            || !grup.Baslik.StartsWith("Tablolar", StringComparison.OrdinalIgnoreCase)
            || _vm.AktifLehce is null)
            e.Handled = true;
    }

    /// <summary>Tablo oluşturma sihirbazını açar (v8-S1): üretilen CREATE TABLE yeni sekmede açılır.</summary>
    private void TabloOlustur_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GezginGrubu grup || _vm.AktifLehce is not { } lehce)
            return;

        SemaNesnesi? ornek = grup.Nesneler.FirstOrDefault();
        string db = ornek?.Veritabani ?? _vm.VarsayilanVeritabani ?? "";
        // Varsayılan şema: gruptaki bir tablodan (en doğru); yoksa MSSQL'de dbo, diğerlerinde boş.
        string sema = ornek?.Sema ?? (lehce.MotorId == "mssql" ? "dbo" : "");
        var pencere = new Views.TabloOlusturPenceresi(lehce, sema, (baslik, sql) => _vm.SekmeAc(baslik, sql, db))
        {
            Owner = this,
        };
        pencere.ShowDialog();
    }

    /// <summary>Mongo find yardımcısını açar (v8): alanlardan find JSON üretip yeni sekmede açar.
    /// Aktif sekmede bir find belgesi varsa panel ondan ön-doldurulur (borç kapanışı: geri-ayrıştırma).</summary>
    private void MongoBul_Click(object sender, RoutedEventArgs e)
    {
        string db = _vm.VarsayilanVeritabani ?? "";
        string? mevcut = (_vm.SeciliSekme as SorguSekmesiViewModel)?.MetinSaglayici?.Invoke();
        // Otomatik doldurma köprüsü (#3): koleksiyon+alan envanteri şema önbelleğinden.
        new Views.MongoBulPenceresi((baslik, json) => _vm.SekmeAc(baslik, json, db), mevcut,
            () => _vm.LogAnalizTablolariAsync(db),
            // m.5: koleksiyon LİSTESİ Mongo'da alansız gelir (hız) → alanlar seçilen koleksiyon
            // için on-demand keşfedilir; Filter/Sort/Project önerileri buna dayanır.
            koleksiyon => _vm.MongoKoleksiyonAlanlariAsync(db, koleksiyon))
        { Owner = this }.ShowDialog();
    }

    private void Snippetler_Click(object sender, RoutedEventArgs e)
    {
        // Liste BAĞLI OLUNAN MOTORA göre süzülür (kullanıcı isteği 2026-07-19) — editördeki
        // öneri listesiyle aynı küme olsun diye. Bağlantı yoksa tümü listelenir.
        var vm = new SnippetlerViewModel(_snippetDeposu, _profil?.Motor)
        {
            DegistiBildir = () => _vm.SnippetleriYenileAsync(),
        };
        PencereGoster(new SnippetPenceresi(vm) { Owner = this });
    }

    private void SekmeKapat_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ISekme sekme)
            _vm.SekmeKapat(sekme);
    }

    // ── m.30: sekme görselleri canlı tutulur — grid kaydırması/seçimi sekme geçişinde kaybolmaz ──

    /// <summary>SelectionChanged içerikteki ComboBox/DataGrid'lerden de KABARCIKLANIR — yalnız
    /// TabControl'ün kendi seçimi işlenir; sekme kapanışları da (seçim değişmese bile)
    /// koleksiyon olayından yakalanır (kurucudaki abonelik).</summary>
    private void SekmeKontrolu_SecimDegisti(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SekmeKontrolu))
            SekmeIcerikleriniEsitle();
    }

    /// <summary>
    /// 🛩 DONMA NÖBETÇİSİ (v22-S1; kullanıcı 2026-08-17 üçüncü kez "büyük tabloda patlıyor/donuyor",
    /// ekran görüntülerinde pencere "Not Responding"). Kod okumakla donmanın YERİ bulunamıyor —
    /// bu nöbetçi ÖLÇÜYOR: UI thread'inde 500 ms'de bir tik atar; iki tik arası 1,5 sn'yi aşarsa
    /// UI o kadar süre bloke kalmıştır → loga SAYISIYLA yazılır (kaç sn, o an yönetilen yığın kaç MB,
    /// kaçıncı gen2). Böylece bir dahaki donmada teşhis tahmin değil kayıt olur; süreç çökse de
    /// (Serilog File sink tamponsuz) son satır orada durur.
    /// </summary>
    private void DonmaNobetcisiniBaslat()
    {
        // 🧭 v22-S4 (çökme denetimi): DIŞ GÖZLEMCİ — nöbetçi izlediği thread'in üstünde yaşamamalı.
        // Aşağıdaki DispatcherTimer UI blokeyken TİK ATAMAZ; 20 Ağu kanıtında 832 sn'lik blok ancak
        // UI çözüLÜNCE loglanabildi (blok İÇİNDE süreç ölseydi tek satır kalmazdı). Iz.DonmaGozlemcisi
        // havuz thread'inde yaşar: UI yalnız kalbi damgalar; 2 sn'yi aşan blokta donma.txt'ye blok
        // SÜRERKEN yazılır — süreç blok içinde ölse de iz kalır. Eski Serilog uyarısı da duruyor:
        // blok ÇÖZÜLÜNCE süre/yığın özetini o veriyor (20 Ağu'da işe yaradığı kanıtlı).
        var gozlemci = new Infrastructure.Iz.DonmaGozlemcisi();
        Closed += (_, _) => gozlemci.Dispose();

        var kronometre = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan sonTik = kronometre.Elapsed;
        var nobetci = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        nobetci.Tick += (_, _) =>
        {
            gozlemci.Kalp(); // UI canlı — dış gözlemciye haber
            TimeSpan simdi = kronometre.Elapsed;
            TimeSpan gecen = simdi - sonTik;
            sonTik = simdi;
            if (gecen < TimeSpan.FromSeconds(1.5))
                return;
            Serilog.Log.Warning(
                "🛩 UI {Saniye:F1} sn bloke kaldı · yığın {Yigin} MB · gen2 {Gen2} · sekme {Sekme}",
                gecen.TotalSeconds, Infrastructure.BellekNobetcisi.YiginBayt() / 1024 / 1024,
                GC.CollectionCount(2), _vm.SeciliSekme?.Baslik);
        };
        nobetci.Start();
    }

    /// <summary>Seçili sekmenin görselini yuvada bir kez kurar, diğerlerini GİZLER (sökmez) —
    /// kapanan sekmelerin sunucuları atılır (VM/grid belleği serbest kalsın).</summary>
    private void SekmeIcerikleriniEsitle()
    {
        if (SekmeKontrolu.Template?.FindName("SekmeIcerikYuvasi", SekmeKontrolu) is not Grid yuva)
            return;

        for (int i = yuva.Children.Count - 1; i >= 0; i--)
            if (yuva.Children[i] is ContentPresenter eski && eski.Content is ISekme s && !_vm.Sekmeler.Contains(s))
                yuva.Children.RemoveAt(i);

        object? secili = SekmeKontrolu.SelectedItem;
        ContentPresenter? seciliSunucu = null;
        foreach (ContentPresenter sunucu in yuva.Children.OfType<ContentPresenter>())
        {
            bool bu = ReferenceEquals(sunucu.Content, secili);
            sunucu.Visibility = bu ? Visibility.Visible : Visibility.Collapsed;
            if (bu)
                seciliSunucu = sunucu;
        }

        if (seciliSunucu is null && secili is not null)
            yuva.Children.Add(new ContentPresenter { Content = secili }); // şablon TabControl.Resources'tan çözülür
    }

    /// <summary>📌 Sekme sağ tık → sabitle/kaldır (v20-S21 saha m.13).</summary>
    private void SekmeSabitDegistir_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ISekme sekme)
            _vm.SekmeSabitDegistir(sekme);
    }

    /// <summary>Sekme sağ tık → "Diğerlerini kapat" (v20-S21 saha m.12).</summary>
    private void DigerSekmeleriKapat_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ISekme sekme)
            _vm.DigerSekmeleriKapat(sekme);
    }

    /// <summary>Sekme sağ tık → "Tümünü kapat" (v20-S21 saha m.12).</summary>
    private void TumSekmeleriKapat_Click(object sender, RoutedEventArgs e)
        => _vm.TumSekmeleriKapat();

    /// <summary>
    /// Seçili metin HER İKİ TEMADA okunur olsun (kullanıcı bulgusu 2026-07-29). AvalonEdit'in
    /// varsayılan yarı saydam moru sözdizimi renkleriyle çakışıp seçili sorguyu okunmaz yapıyordu.
    /// Temaya uygun DÜZ seçim zemini + ZITLIK garantili tek renk ön plan: seçim sırasında sözdizimi
    /// renkleri bilerek bastırılır (seçim geçicidir — kullanıcı çalıştırmak için seçer; okunması esastır).
    /// </summary>
    private void EditorSecimRengiUygula(ICSharpCode.AvalonEdit.TextEditor editor)
    {
        bool koyu = _vm.KoyuTema;
        editor.TextArea.SelectionBrush = new SolidColorBrush(koyu
            ? Color.FromRgb(0x2E, 0x55, 0x7A)   // koyu tema: koyu mavi zemin
            : Color.FromRgb(0xB4, 0xD5, 0xFE)); // açık tema: açık mavi zemin
        editor.TextArea.SelectionForeground = new SolidColorBrush(koyu
            ? Color.FromRgb(0xF2, 0xF2, 0xF2)   // koyu zeminde açık metin
            : Color.FromRgb(0x14, 0x14, 0x14)); // açık zeminde koyu metin
        editor.TextArea.SelectionBorder = null; // kenarlık gürültüsü olmasın
    }

    /// <summary>
    /// AvalonEdit MVVM köprüsü: seçim/imleç view'da yaşar; VM'e sağlayıcı delegeler
    /// bağlanır (FG-3.3, V2-S3). ContentTemplate tek editör örneğini sekmeler arasında
    /// paylaştığı için DataContext değişiminde yenilenir.
    /// </summary>
    private void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        var editor = (ICSharpCode.AvalonEdit.TextEditor)sender;
        SaglayicilariBagla(editor);
        editor.SyntaxHighlighting = EditorTema.Tanim(_vm.KoyuTema); // XAML'daki "TSQL" açık tema varsayımıdır
        EditorSecimRengiUygula(editor);

        if (editor.Tag is "bagli") // Loaded tekrar tetiklenebilir — olaylar bir kez bağlanır
        {
            // Sekme geçişinde Unloaded listeden düşürmüştü; tekrar görünür olunca geri al
            // (tema döngüsü yalnız YÜKLÜ editörleri dolaşır — inceleme bulgusu 2026-07-23).
            if (!_editorler.Contains(editor))
                _editorler.Add(editor);
            return;
        }
        editor.Tag = "bagli";
        _editorler.Add(editor);
        // Şablon söküldüğünde (sekme kapandı ya da tip değişti) ölü editör listede KALMASIN —
        // eskiden liste hiç budanmıyordu; uzun oturumda AvalonEdit ağaçları pencere ömrünce
        // köklü kalıyordu (inceleme bulgusu 2026-07-23). Tema, yeniden Loaded'da zaten tazelenir.
        editor.Unloaded += (_, _) => _editorler.Remove(editor);
        editor.DataContextChanged += (_, _) => SaglayicilariBagla(editor);

        // m.10 fikir 9: tablo adının üstünde Ctrl+hover → kolonlar + ilk satırlar (sekme açmadan).
        editor.TextArea.TextView.MouseHover += (s, olay) => TabloOnizlemeGoster(editor, olay);
        editor.TextArea.TextView.MouseHoverStopped += (_, _) => _onizlemeIpucu.IsOpen = false;

        // Sorgu penceresi içi arama (kullanıcı isteği 2026-07-20): AvalonEdit'in yerleşik
        // arama paneli — Ctrl+F açar, F3/Enter sonraki, Shift+F3 önceki, Esc kapatır.
        // Butonların ne yaptığı belli olsun diye Türkçe tooltip'ler (kullanıcı isteği 2026-07-21).
        var aramaPaneli = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(editor);
        aramaPaneli.Localization = new AramaYerellestirme();
        // Eşleşme vurgusu TEMA-FARKINDA (v20-S17, kullanıcı bulgusu 2026-08-08): varsayılan açık
        // yeşil, koyu temada açık metinle OKUNMUYORDU. Yarı saydam amber palet fırçası —
        // DynamicResource olduğundan tema değişince kendiliğinden döner.
        aramaPaneli.SetResourceReference(
            ICSharpCode.AvalonEdit.Search.SearchPanel.MarkerBrushProperty, "AramaVurguFircasi");

        // Hatalı sorgu satırının altına kırmızı dalga (kullanıcı isteği 2026-07-23); Mongo'da VM doldurmaz.
        editor.TextArea.TextView.BackgroundRenderers.Add(new HataAltCizgisi(editor));

        // Kod katlama (kullanıcı isteği 2026-07-27: "if, begin gibi tüm alanlarda end'ine kadar
        // küçültme"): BEGIN/END, CASE, çok satırlı yorum blokları katlanabilir. Aralıklar SAF
        // SqlCozumleyici.KatlamaAraliklari'ndan (ScriptDom token akışı) gelir; yalnız MSSQL.
        //
        // KRİTİK (kullanıcı bulgusu 2026-07-27 "Invalid document" çağlaması): editör örneği sekmeler
        // arasında PAYLAŞILIR; sekme değişince TextArea.Document YENİ belgeye bağlanır. FoldingManager
        // ise KURULDUĞU belgeye bağlıdır — eski yöneticiyle UpdateFoldings çağırmak "Invalid document"
        // fırlatır ve global yakalayıcı her tick'te MessageBox açardı. Bu yüzden: DataContext (=belge)
        // değişince yönetici SÖKÜLÜP YENİDEN kurulur; ve güncelleme HER İHTİMALE karşı try/catch ile
        // sarılır — katlama bir konfordur, uygulamayı ASLA çökertmez.
        ICSharpCode.AvalonEdit.Folding.FoldingManager? katlamaYoneticisi = KatlamaKur(editor, null);
        var katlamaSayaci = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(500) };
        katlamaSayaci.Tick += (_, _) => { katlamaSayaci.Stop(); KatlamalariGuncelle(editor, katlamaYoneticisi); };
        editor.TextChanged += (_, _) => { katlamaSayaci.Stop(); katlamaSayaci.Start(); };
        editor.Unloaded += (_, _) => katlamaSayaci.Stop(); // kapanışta bekleyen tick ölü editöre dokunmasın
        // DOĞRU TETİK: DataContextChanged DEĞİL, DocumentChanged. Sekme değişince editör.Document
        // YENİ belgeye bağlanır; eski FoldingManager eski belgeye bağlı kalır ve AvalonEdit kenar
        // boşluğunu YENİDEN ÇİZERKEN (benim çağrım değil!) "Invalid document" fırlatır. Belge değişir
        // değişmez yönetici sökülüp yeni belgeye kurulur — böylece bayat çizim hiç oluşmaz.
        editor.DocumentChanged += (_, _) =>
        {
            katlamaSayaci.Stop();
            katlamaYoneticisi = KatlamaKur(editor, katlamaYoneticisi);
            KatlamalariGuncelle(editor, katlamaYoneticisi);
        };
        KatlamalariGuncelle(editor, katlamaYoneticisi);

        // IntelliSense (V2-S3, FG-3.8): '.' sonrası kendiliğinden, Ctrl+Space ile her yerde
        editor.TextArea.TextEntered += (_, args) =>
        {
            if (args.Text == ".")
            {
                _ = TamamlamaGosterAsync(editor);
                return;
            }

            // Mongo JSON tetikleri (C7, 2026-07-25): '"' anahtar/koleksiyon yazımı, '$' operatör,
            // '{' ve ',' yeni anahtar konumu — find belgesi yazarken öneri kendiliğinden düşer.
            if (_vm.MotorMongoMu && args.Text is "\"" or "$" or "{" or ",")
            {
                _ = TamamlamaGosterAsync(editor);
                return;
            }

            // #9 (kullanıcı isteği 2026-07-29): SP/fonksiyon parametre ipucu. '(' fonksiyon çağrısı,
            // ',' sonraki argüman — ayrı yüzen pencere (completion'dan bağımsız), SQL motorlarında.
            if (!_vm.MotorMongoMu && args.Text is "(" or ",")
            {
                _ = ParametreIpucuGosterAsync(editor);
                return;
            }

            // '@' tetiği (kullanıcı bulgusu 2026-07-31): belgede geçen @parametreler önerilsin.
            if (!_vm.MotorMongoMu && args.Text == "@")
            {
                _ = TamamlamaGosterAsync(editor);
                return;
            }

            // #3 oto-tetik (kullanıcı isteği 2026-07-29): cümle sözcüğü + boşlukta öneri KENDİLİĞİNDEN
            // açılsın (FROM/JOIN/WHERE/ON… sonrası tablo/kolon yazılır). Yalnız pencere kapalıyken,
            // SQL motorlarında (Mongo'nun kendi tetikleri yukarıda) ve boşluk yeni yazıldığında.
            if (_tamamlamaPenceresi is null && args.Text == " " && !_vm.MotorMongoMu
                && OtoTamamlama.OtoAcilmali(editor.Text, editor.CaretOffset))
            {
                _ = TamamlamaGosterAsync(editor);
                return;
            }

            // #9: EXEC sp <boşluk> → parametre ipucu (cümle-tamamlama tetiklenmediyse buraya düşer;
            // çağrı dışındaysa ParametreIpucuGosterAsync açık ipucu varsa kapatır).
            if (!_vm.MotorMongoMu && args.Text == " ")
            {
                _ = ParametreIpucuGosterAsync(editor);
                return;
            }

            // Kelime belli uzunluğa gelince kendiliğinden aç: yalnız pencere kapalıysa ve imleçteki
            // kelime TAM bu uzunlukta olduğunda tetikler; açıldıktan sonra AvalonEdit süzer, her tuşta
            // yeniden açmayız (== eşik, >= değil).
            // Eşik 3→2 (kullanıcı isteği 2026-07-22: "Id kolonları öneri gelmiyor"): "Id"/"No" gibi
            // 2 harfli kolonlar 3-eşikte ASLA yakalanamıyordu. 2 hâlâ ilk harfte pop-up yapmaz.
            if (_tamamlamaPenceresi is null && args.Text.Length == 1
                && (char.IsLetterOrDigit(args.Text[0]) || args.Text[0] == '_')
                && ImlecKelimeUzunlugu(editor) == 2)
                _ = TamamlamaGosterAsync(editor);
        };
        // Editör kısayolları (kullanıcı isteği 2026-07-23). Ctrl+K bir AKORdur: ardından C = yorumla,
        // U = yorumu kaldır (SSMS deseni). Ctrl+/ tek tuşta değiştir (toggle). Bayrak editör başına
        // kapanışta yaşar (her Editor_Loaded kendi yerelini kapatır).
        bool ctrlKBekliyor = false;
        editor.TextArea.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            {
                args.Handled = true;
                _ = TamamlamaGosterAsync(editor);
                return;
            }
            // Ctrl+H → Bul ve Değiştir (AvalonEdit'te hazır replace paneli yok — kullanıcı isteği 2026-07-23)
            if (args.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
            {
                args.Handled = true;
                PencereGoster(new Views.BulDegistirPenceresi(editor) { Owner = this });
                return;
            }

            if (ctrlKBekliyor) // Ctrl+K sonrası ikinci tuş (C/U — Ctrl basılı olsa da olmasa da)
            {
                ctrlKBekliyor = false;
                if (args.Key == Key.C) { YorumDegistir(editor, yorumla: true); args.Handled = true; }
                else if (args.Key == Key.U) { YorumDegistir(editor, yorumla: false); args.Handled = true; }
                return;
            }
            if (args.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ctrlKBekliyor = true;
                args.Handled = true;
                return;
            }
            if (args.Key is Key.OemQuestion or Key.Oem2 && Keyboard.Modifiers == ModifierKeys.Control)
            {
                YorumDegistir(editor, yorumla: null); // toggle
                args.Handled = true;
            }
        };
    }

    /// <summary>
    /// Seçili satırları (seçim yoksa imleç satırı) satır yorumuna alır/çıkarır (T-SQL <c>--</c>).
    /// <paramref name="yorumla"/> null ise DEĞİŞTİR: ilk dolu satır yorumluysa hepsini aç, değilse kapat.
    /// Yorum satır başına (sütun 0) eklenir/kaldırılır; tek geri-al adımı için toplu güncelleme.
    /// </summary>
    private static void YorumDegistir(ICSharpCode.AvalonEdit.TextEditor editor, bool? yorumla)
    {
        var doc = editor.Document;
        int bas = doc.GetLineByOffset(editor.SelectionStart).LineNumber;
        int son = doc.GetLineByOffset(editor.SelectionStart + editor.SelectionLength).LineNumber;

        bool YorumluMu(int satirNo)
        {
            var l = doc.GetLineByNumber(satirNo);
            string m = doc.GetText(l.Offset, l.Length);
            return m.AsSpan(m.Length - m.TrimStart().Length).StartsWith("--");
        }

        // Toggle: ilk DOLU satırın durumuna göre karar (hepsi yorumluysa aç, değilse kapat).
        bool ekle;
        if (yorumla is { } y)
            ekle = y;
        else
        {
            int ilkDolu = bas;
            while (ilkDolu < son && doc.GetText(doc.GetLineByNumber(ilkDolu).Offset, doc.GetLineByNumber(ilkDolu).Length).Trim().Length == 0)
                ilkDolu++;
            ekle = !YorumluMu(ilkDolu);
        }

        doc.BeginUpdate();
        try
        {
            for (int ln = bas; ln <= son; ln++)
            {
                var l = doc.GetLineByNumber(ln);
                string m = doc.GetText(l.Offset, l.Length);
                int bosluk = m.Length - m.TrimStart().Length;
                if (ekle)
                {
                    if (m.Trim().Length > 0) // boş satırı yorumlama
                        doc.Insert(l.Offset, "--");
                }
                else if (m.AsSpan(bosluk).StartsWith("--"))
                {
                    doc.Remove(l.Offset + bosluk, 2);
                }
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }

    /// <summary>
    /// Editördeki BEGIN/END·CASE·çok satırlı yorum bloklarını katlanabilir yapar (kullanıcı
    /// isteği 2026-07-27). Yalnız SQL Server: aralıklar T-SQL token akışından gelir; diğer
    /// motorlarda katlama temizlenir (JSON/PLpg katlaması ayrı iş). NewFolding listesi artan
    /// başlangıçla verilir — FoldingManager iç içe blokları böyle bekler.
    /// </summary>
    /// <summary>Eski yöneticiyi (varsa) söküp geçerli belgeye yeni bir FoldingManager kurar; her şey korumalı.</summary>
    private static ICSharpCode.AvalonEdit.Folding.FoldingManager? KatlamaKur(
        ICSharpCode.AvalonEdit.TextEditor editor, ICSharpCode.AvalonEdit.Folding.FoldingManager? eski)
    {
        try
        {
            if (eski is not null)
                ICSharpCode.AvalonEdit.Folding.FoldingManager.Uninstall(eski); // bayat çizimi durdurur
            return ICSharpCode.AvalonEdit.Folding.FoldingManager.Install(editor.TextArea);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Kod katlama kurulamadı (yut)");
            return null;
        }
    }

    private void KatlamalariGuncelle(
        ICSharpCode.AvalonEdit.TextEditor editor, ICSharpCode.AvalonEdit.Folding.FoldingManager? yonetici)
    {
        // Katlama YARDIMCI bir konfordur: hangi nedenle olursa olsun (belge swap yarışı, kapanış
        // sırası, beklenmedik ofset) uygulamayı ÇÖKERTMEZ — hata yutulur, editör çalışmaya devam eder.
        if (yonetici is null)
            return;
        try
        {
            if (!_vm.MotorMssqlMu)
            {
                yonetici.Clear();
                return;
            }

            List<ICSharpCode.AvalonEdit.Folding.NewFolding> katlamalar =
                [.. SqlCozumleyici.KatlamaAraliklari(editor.Text)
                    .Where(a => a.Son <= editor.Document.TextLength)
                    .Select(a => new ICSharpCode.AvalonEdit.Folding.NewFolding(a.Bas, a.Son))];
            yonetici.UpdateFoldings(katlamalar, firstErrorOffset: -1);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Kod katlama güncellenemedi (yut)");
        }
    }

    private static void SaglayicilariBagla(ICSharpCode.AvalonEdit.TextEditor editor)
    {
        if (editor.DataContext is not SorguSekmesiViewModel sekme)
            return;

        sekme.MetinSaglayici = () => editor.SelectionLength > 0 ? editor.SelectedText : editor.Text;
        // MetinSaglayici'yla AYNI koşul: seçim koşuyorsa hata satırları seçimin başladığı belge
        // satırına göre eşlenir (inceleme bulgusu 2026-07-23 — alt çizgi yanlış satıra iniyordu).
        sekme.SecimBasiSatiri = () => editor.SelectionLength > 0
            ? editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber
            : 1;
        sekme.ImlecSaglayici = () => editor.CaretOffset;
        sekme.AralikSec = editor.Select;
        sekme.SatiraGit = satir =>
        {
            satir = Math.Clamp(satir, 1, editor.Document.LineCount);
            editor.TextArea.Caret.Line = satir;
            editor.TextArea.Caret.Column = 1;
            editor.ScrollToLine(satir);
            editor.Focus();
        };
    }

    private ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow? _tamamlamaPenceresi;
    private ICSharpCode.AvalonEdit.CodeCompletion.InsightWindow? _ipucuPenceresi; // #9 parametre ipucu

    /// <summary>
    /// v22-S4 Edit m.2 (kullanıcı: "Edit modda da sorgu editöründeki gibi öneriler olsun, normal
    /// textbox yerine"): Edit sekmesinin sorgu kutusu gerçek editöre çevrildi. Kurulum ana editörün
    /// (Editor_Loaded) SADE alt kümesidir — renklendirme, tema, Ctrl+F arama ve IntelliSense
    /// tetikleri; katlama/hata çizgisi/önizleme gibi SorguSekmesi'ne özgü parçalar bilerek yok.
    /// F5 / Ctrl+Enter, TextArea kısayolları gölgeleyebildiği için PreviewKeyDown ile yönlendirilir.
    /// </summary>
    private void DuzenlemeEditor_Loaded(object sender, RoutedEventArgs e)
    {
        var editor = (ICSharpCode.AvalonEdit.TextEditor)sender;
        editor.SyntaxHighlighting = EditorTema.Tanim(_vm.KoyuTema);
        EditorSecimRengiUygula(editor);

        if (editor.Tag is "bagli") // Loaded tekrar tetiklenebilir — olaylar bir kez bağlanır
        {
            if (!_editorler.Contains(editor))
                _editorler.Add(editor); // tema döngüsü yalnız listelenen editörleri boyar
            return;
        }
        editor.Tag = "bagli";
        _editorler.Add(editor);
        editor.Unloaded += (_, _) => _editorler.Remove(editor);

        var aramaPaneli = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(editor);
        aramaPaneli.Localization = new AramaYerellestirme();
        aramaPaneli.SetResourceReference(
            ICSharpCode.AvalonEdit.Search.SearchPanel.MarkerBrushProperty, "AramaVurguFircasi");

        // IntelliSense tetikleri — ana editörle aynı hisler: '.' sonrası, cümle sözcüğü + boşluk,
        // 2 harflik kelime eşiği; Ctrl+Space aşağıdaki PreviewKeyDown'da.
        editor.TextArea.TextEntered += (_, args) =>
        {
            if (args.Text == "."
                || (_tamamlamaPenceresi is null && args.Text == " "
                    && OtoTamamlama.OtoAcilmali(editor.Text, editor.CaretOffset))
                || (_tamamlamaPenceresi is null && args.Text.Length == 1
                    && (char.IsLetterOrDigit(args.Text[0]) || args.Text[0] == '_')
                    && ImlecKelimeUzunlugu(editor) == 2))
                _ = TamamlamaGosterAsync(editor);
        };
        editor.TextArea.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            {
                args.Handled = true;
                _ = TamamlamaGosterAsync(editor);
                return;
            }
            if ((args.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
                || (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
            {
                if (editor.DataContext is DuzenlemeSekmesiViewModel vm
                    && vm.FiltreCalistirCommand.CanExecute(null))
                {
                    args.Handled = true;
                    vm.FiltreCalistirCommand.Execute(null);
                }
            }
        };
    }

    /// <summary>İmlecin hemen solundaki tanımlayıcı kelimenin uzunluğu (harf/rakam/alt çizgi).</summary>
    private static int ImlecKelimeUzunlugu(ICSharpCode.AvalonEdit.TextEditor editor)
    {
        string metin = editor.Text;
        int i = editor.CaretOffset, n = 0;
        while (i - 1 >= 0 && (char.IsLetterOrDigit(metin[i - 1]) || metin[i - 1] == '_'))
        {
            i--;
            n++;
        }
        return n;
    }

    private async Task TamamlamaGosterAsync(ICSharpCode.AvalonEdit.TextEditor editor)
    {
        // v22-S4 Edit m.2: tamamlama artık İKİ sekme türünde çalışır — Edit modunun sorgu editörü de
        // aynı önerileri alır. Şema önbelleğini çözmek için gereken tek şey veritabanı adıdır.
        SorguSekmesiViewModel? sekme = null;
        string? veritabani;
        switch (editor.DataContext)
        {
            case SorguSekmesiViewModel s:
                sekme = s;
                veritabani = s.SecilenVeritabani;
                break;
            case DuzenlemeSekmesiViewModel d:
                veritabani = d.Veritabani;
                break;
            default:
                return;
        }

        // İlk çağrıda şema yüklenebilir (ağaç açılmamışsa) — dönüşte güncel imleçle üret
        SemaOnbellegi? onbellek = await _vm.OnbellekGetirAsync(veritabani);

        // m.10 fikir 10: imleç bir FK kolonuyla karşılaştırmanın sağındaysa, tanım tablosunun
        // DEĞERLERİ anlamıyla önerilir ("3 Onaylandı"); bağlam netse olağan öneriler gösterilmez.
        // Yalnız sorgu sekmesinde — Edit'in köprüleri yok (öneri yine tam çalışır).
        if (sekme is not null && onbellek is not null
            && await TanimDegerleriniOnerAsync(editor, sekme, onbellek))
            return;
        // Motor ve snippet listesi geçilir (V5-S4): öneriler motora göre daralır — Mongo'da
        // T-SQL anahtar sözcükleri önerilmez, snippet'ler zaten depoda süzülmüştür.
        IReadOnlyList<TamamlamaOnerisi> oneriler = OtoTamamlama.Oner(
            editor.Text, editor.CaretOffset, onbellek, out int kelimeBasi,
            _profil?.Motor ?? MotorTuru.Mssql, _vm.Snippetler);
        if (oneriler.Count == 0 || kelimeBasi > editor.CaretOffset)
            return;

        _tamamlamaPenceresi?.Close();
        var pencere = new ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow(editor.TextArea)
        {
            StartOffset = kelimeBasi,
        };
        pencere.CompletionList.IsFiltering = true;
        var veriler = oneriler.OrderByDescending(o => o.Oncelik).ThenBy(o => o.Metin)
            .Select(o => new TamamlamaVerisi(o)).ToList();
        foreach (TamamlamaVerisi veri in veriler)
            pencere.CompletionList.CompletionData.Add(veri);

        // Açılır liste İÇERİĞE GÖRE genişlesin (kullanıcı bulgusu 2026-07-20): sabit genişlik
        // uzun tablo/SP adlarını kırpıyordu. En uzun satırı (simge + ad + tür etiketi) ölçüp
        // pencereyi ona göre genişlet; alt/üst sınırla (mikro pencere ya da absürt genişlik olmasın).
        double dip = VisualTreeHelper.GetDpi(editor).PixelsPerDip;
        var tipYuzu = new Typeface("Segoe UI");
        double enGenis = 0;
        foreach (TamamlamaVerisi veri in veriler)
        {
            string satir = veri.TurEtiketi.Length > 0 ? $"{veri.Text}    {veri.TurEtiketi}" : veri.Text;
            var olcum = new FormattedText(satir, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, tipYuzu, 12, Brushes.Black, dip);
            if (olcum.Width > enGenis)
                enGenis = olcum.Width;
        }
        pencere.Width = Math.Clamp(enGenis + 72, 240, 680); // simge + kaydırma çubuğu + kenar payı

        // AvalonEdit tamamlama penceresi AYRI bir Window'dur; uygulamanın örtük Window stilini almaz →
        // koyu temada zemin BEYAZ kalıyordu, açık renk metnimiz okunmuyordu (kullanıcı bulgusu 2026-07-21).
        // Aktif temanın fırçalarıyla açıkça boya (fırçalar DynamicResource; her açılışta güncel tema).
        Brush? zemin = TryFindResource("PanelZeminFircasi") as Brush;
        Brush? metin = TryFindResource("MetinFircasi") as Brush;
        Brush? kenar = TryFindResource("KenarFircasi") as Brush;
        if (zemin is not null) pencere.Background = zemin;
        if (metin is not null) pencere.Foreground = metin;
        if (kenar is not null) pencere.BorderBrush = kenar;

        pencere.Closed += (_, _) => _tamamlamaPenceresi = null;
        _tamamlamaPenceresi = pencere;
        pencere.Show();

        // ListBox şablon uygulandıktan (Show) sonra hazır: satırlar tam genişliğe uzasın (tür etiketi
        // sağa yaslansın) VE liste zemini/metni de temaya boyansın (pencere zemininin beyaz kalmaması
        // için asıl kritik olan burası — AvalonEdit listeyi kendi beyaz zeminiyle çiziyordu).
        if (pencere.CompletionList.ListBox is { } liste)
        {
            liste.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            if (zemin is not null) liste.Background = zemin;
            if (metin is not null) liste.Foreground = metin;
            liste.BorderThickness = new Thickness(0);
        }

        string onek = editor.Text[kelimeBasi..editor.CaretOffset];
        if (onek.Length > 0)
            pencere.CompletionList.SelectItem(onek);

        // Boş süzülme TAKILI KALMASIN (kullanıcı isteği 2026-07-21): hızlı yazınca liste hiçbir
        // öğeyle eşleşmeyip boş kutu ekranda asılı kalıyordu. Hem açılışta hem her tuştan sonra
        // (AvalonEdit süzdükten SONRA — Background öncelik) liste boşsa pencereyi kapat.
        void BosSuzulmeVarsaKapat()
        {
            if (_tamamlamaPenceresi == pencere && pencere.CompletionList.ListBox is { Items.Count: 0 })
                pencere.Close();
        }
        void TuslamadaDenetle(object? s, System.Windows.Input.TextCompositionEventArgs a)
            => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, BosSuzulmeVarsaKapat);
        editor.TextArea.TextEntered += TuslamadaDenetle;
        pencere.Closed += (_, _) => editor.TextArea.TextEntered -= TuslamadaDenetle;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, BosSuzulmeVarsaKapat);
    }

    /// <summary>#9 (2026-07-29): imleç bir SP/fonksiyon çağrısının argüman bölgesindeyse parametre
    /// imzasını yüzen ipuçta gösterir (AvalonEdit InsightWindow); çağrı dışındaysa açık ipucu kapanır.
    /// Öneri üretimi SAF OtoTamamlama.ParametreImzasiBul'da — burası yalnız gösterim.</summary>
    private async Task ParametreIpucuGosterAsync(ICSharpCode.AvalonEdit.TextEditor editor)
    {
        if (editor.DataContext is not SorguSekmesiViewModel sekme)
            return;

        SemaOnbellegi? onbellek = await _vm.OnbellekGetirAsync(sekme.SecilenVeritabani);
        ParametreImzasi? imza =
            OtoTamamlama.ParametreImzasiBul(editor.Text, editor.CaretOffset, onbellek);
        if (imza is null)
        {
            _ipucuPenceresi?.Close(); // çağrıdan çıkıldı → ipucu kalkar
            return;
        }

        string parametreler = imza.Parametreler.Count == 0
            ? "parametre yok"
            : string.Join(", ", imza.Parametreler.Select(p =>
                $"{p.Ad} {p.Tip}{(p.CikisMi ? " OUTPUT" : "")}"));
        var icerik = new TextBlock
        {
            Text = $"{imza.NesneAd} ({parametreler})",
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            Margin = new Thickness(8, 4, 8, 4),
        };
        if (TryFindResource("MetinFircasi") is Brush metin)
            icerik.Foreground = metin;

        _ipucuPenceresi?.Close();
        var pencere = new ICSharpCode.AvalonEdit.CodeCompletion.InsightWindow(editor.TextArea) { Content = icerik };
        if (TryFindResource("PanelZeminFircasi") is Brush zemin)
            pencere.Background = zemin;
        if (TryFindResource("KenarFircasi") is Brush kenar)
            pencere.BorderBrush = kenar;
        pencere.Closed += (_, _) => { if (_ipucuPenceresi == pencere) _ipucuPenceresi = null; };
        _ipucuPenceresi = pencere;
        pencere.Show();
    }

    /// <summary>Mesajlar'da "Satır N" içeren satıra çift tık → editörde o satıra git (V2-S3).</summary>
    private void Mesajlar_CiftTik(object sender, MouseButtonEventArgs e)
    {
        var kutu = (TextBox)sender;
        if (kutu.DataContext is not SorguSekmesiViewModel sekme)
            return;

        // Çift tık anındaki fare konumundan karakteri bul (CaretIndex bazen henüz güncellenmemiş olur)
        int karakter = kutu.GetCharacterIndexFromPoint(e.GetPosition(kutu), snapToText: true);
        if (karakter < 0)
            karakter = Math.Max(0, kutu.CaretIndex);

        int satirIndeksi = kutu.GetLineIndexFromCharacterIndex(karakter);
        if (satirIndeksi < 0)
            return;

        Match m = MesajSatirDeseni().Match(kutu.GetLineText(satirIndeksi));
        if (m.Success && int.TryParse(m.Groups[1].Value, out int satir))
        {
            sekme.SatiraGit?.Invoke(satir);
            _vm.Durum = $"Editör {satir}. satıra gitti.";
        }
    }

    [GeneratedRegex(@"Satır (\d+)")]
    private static partial Regex MesajSatirDeseni();

    // ---- Nesne gezgini eylemleri (S5) ----

    /// <summary>
    /// SP/view/fonksiyona çift tık → ALTER script'i (FG-5.1). Tablo çift tıkta
    /// kolonlarına açılır (SSMS davranışı), script açılmaz.
    /// </summary>
    private async void Agac_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UstTreeViewItem(e.OriginalSource as DependencyObject)?.DataContext is not SemaNesnesi nesne
            || nesne.Tur == SemaNesneTuru.Tablo)
            return;

        e.Handled = true; // düğüm açılıp kapanmasın
        await _vm.NesneScriptiAcAsync(nesne);
    }

    /// <summary>
    /// v19-S9: tema butonu — açılır menü: "Açık Tema ▸ 6 palet" · "Koyu Tema ▸ 6 palet" (üstüne
    /// gelince alt seçenekler açılır) + hızlı "Açık ⇄ Koyu" geçişi. Seçili kip ve varyant ✓ işaretli;
    /// menü her açılışta taze kurulur (seçim değişmiş olabilir).
    /// </summary>
    private void TemaMenusu_Ac(object sender, RoutedEventArgs e)
    {
        // (ad, etiket, vurgu, zemin) — yuvarlak renk önizlemesi menüde paleti tarif eder (v19-S9 cila).
        (string Ad, string Etiket, string KoyuVurgu, string KoyuZemin, string AcikVurgu)[] paletler =
        [
            ("indigo", "İndigo (varsayılan)", "#818CF8", "#161A2E", "#4F46E5"),
            ("grafit", "Grafit", "#818CF8", "#17181C", "#334155"),
            ("slate", "Slate Mavi", "#38BDF8", "#0F172A", "#0284C7"),
            ("petrol", "Petrol", "#2DD4BF", "#0F1D1C", "#0D9488"),
            ("amber", "Amber", "#F59E0B", "#17181C", "#D97706"),
            ("kor", "Kor (kırmızı-sarı-siyah)", "#E8362B", "#0E0F12", "#D62212"),
            ("vs", "Visual Studio (SSMS)", "#0078D4", "#1F1F1F", "#005FB8"), // v22-S15: mockup 6
            ("antrasit", "Antrasit + Turkuaz (klasik ızgara)", "#26C6DA", "#1C2226", "#00838F"), // v23-S14
        ];

        static System.Windows.Shapes.Ellipse Yuvarlak(string vurgu, string zemin) => new()
        {
            Width = 14, Height = 14,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(vurgu)!),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(zemin)!),
            StrokeThickness = 2.5,
        };

        MenuItem KipMenusu(string baslik, bool koyu, string secili)
        {
            var kip = new MenuItem { Header = baslik, IsChecked = _vm.KoyuTema == koyu };
            foreach ((string ad, string etiket, string koyuVurgu, string koyuZemin, string acikVurgu) in paletler)
            {
                var oge = new MenuItem
                {
                    Header = etiket,
                    IsChecked = secili == ad,
                    Icon = koyu ? Yuvarlak(koyuVurgu, koyuZemin) : Yuvarlak(acikVurgu, "#E6E9F0"),
                };
                oge.Click += (_, _) => _vm.TemaPaletiSec(koyu, ad);
                kip.Items.Add(oge);
            }
            return kip;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = (Button)sender,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 200,
        };
        menu.Items.Add(KipMenusu("☀  Açık Tema", koyu: false, App.SeciliAcikPalet));
        menu.Items.Add(KipMenusu("🌙  Koyu Tema", koyu: true, App.SeciliKoyuPalet));
        menu.Items.Add(new Separator());
        var hizli = new MenuItem { Header = "Açık ⇄ Koyu değiştir" };
        hizli.Click += (_, _) => _vm.TemaDegistir();
        menu.Items.Add(hizli);
        menu.IsOpen = true;
    }

    /// <summary>v19-S7: gezginde veritabanı düğümü seçilince aktif sekme o veritabanına geçer.</summary>
    private void Agac_SecimDegisti(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.GezginVeritabani dugum)
            _vm.GezgindenVeritabaniSecildi(dugum.Ad);
    }

    private static TreeViewItem? UstTreeViewItem(DependencyObject? kaynak)
    {
        while (kaynak is not null and not TreeViewItem)
            kaynak = VisualTreeHelper.GetParent(kaynak);
        return kaynak as TreeViewItem;
    }

    /// <summary>Sağ tık menüsü öğeleri DataContext'i TextBlock'tan miras alır (SemaNesnesi).</summary>
    private static SemaNesnesi? MenuNesnesi(object sender)
        => (sender as FrameworkElement)?.DataContext as SemaNesnesi;

    private async void IlkNSatir_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } nesne)
            await _vm.IlkNSatirAsync(nesne);
    }

    /// <summary>
    /// 👁 Önizleme (v22-S3, saha turu-3 m.1 — kullanıcı: "Ctrl ile tablonun üzerine gelince yaptığımız
    /// önizlemeyi sağ tıkla da koyalım"). Editördeki ipucuyla AYNI içerik ve AYNI 30 sn'lik önbellek
    /// kullanılır (aynı tabloya art arda bakışta ikinci sorgu gitmez); fark yalnız kabuktur: burada
    /// kalıcı bir pencere açılır, oradan kopyalanabilir ve "ilk 100 satırı sekmede aç" ile derine inilir.
    /// MongoDB'de satır getirilmez (kolon listesi yine dolu) — hover davranışının aynısı.
    /// </summary>
    private void Onizleme_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is not { } nesne || _vm.AktifProfil is not { } profil)
            return;

        Func<CancellationToken, Task<(ResultSetData? Veri, string? Hata)>>? satirGetir = null;
        if (profil.Motor != MotorTuru.Mongo)
        {
            string anahtar = $"{nesne.Veritabani}|{nesne.TamAd}";
            satirGetir = async _ =>
            {
                if (_onizlemeOnbellek.TryGetValue(anahtar, out (DateTime Zaman, ResultSetData Veri) kayit)
                    && DateTime.UtcNow - kayit.Zaman < OnizlemeOmru)
                    return (kayit.Veri, null);

                string sql = _lehceSaglayici.Getir(profil.Motor).IlkNSatirSorgusu(nesne, 5);
                (ResultSetData? veri, string? hata) = await _vm.TekSetSorguAsync(sql, nesne.Veritabani, 5);
                if (veri is not null)
                    _onizlemeOnbellek[anahtar] = (DateTime.UtcNow, veri);
                return (veri, hata);
            };
        }

        PencereGoster(new Views.TabloOnizlemePenceresi(
            nesne, satirGetir, hedef => _ = _vm.IlkNSatirAsync(hedef, 100)) { Owner = this });
    }

    /// <summary>Tablo → düzenleme sekmesi (V2-S5, FG-4.9).</summary>
    private async void Duzenle_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } tablo)
            await _vm.DuzenlemeAcAsync(tablo);
    }

    /// <summary>v20-S4: tabloyu Görsel Sorgu tasarımcısına "indir" (sürükle-bırakın menü karşılığı).</summary>
    private void GorseleEkle_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } nesne)
            _vm.GorseleTabloEkle(nesne);
    }

    /// <summary>
    /// v20-S10: kolona sağ tık → 🔗 Tüm bağlantılarını göster. Kolonun tablosu ağaçtan bulunur
    /// (SemaKolonu üst nesnesini bilmez): kolonun TreeViewItem'ının BİR üstündeki TreeViewItem.
    /// </summary>
    private async void KolonBaglari_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu menu }
            || menu.PlacementTarget is not FrameworkElement hedef
            || hedef.DataContext is not SemaKolonu kolon)
            return;

        TreeViewItem? kolonOge = UstTreeViewItem(hedef);
        TreeViewItem? tabloOge = kolonOge is null ? null : UstTreeViewItem(VisualTreeHelper.GetParent(kolonOge));
        if (tabloOge?.DataContext is not SemaNesnesi tablo)
            return;

        IReadOnlyList<KolonBaglari.Bag>? baglar = await _vm.KolonBaglariAsync(tablo, kolon.Ad);
        if (baglar is null)
            return; // Mongo/bağlantısız — neden durum çubuğunda

        PencereGoster(new KolonBaglariPenceresi($"{tablo.TamAd}.{kolon.Ad}", baglar,
            bag => _ = _vm.KolonBagiJoinAcAsync(tablo, bag)) { Owner = this });
    }

    // ---- Görsel Sorgu Tasarımcısı: ağaçtan sürükle, tuvale bırak (v6-S1) ----

    private Point _agacTiklamaNoktasi;

    /// <summary>Sürükleme eşiği için başlangıç noktasını kaydeder (normal tık/çift tık bozulmaz).</summary>
    private void Agac_MouseDown(object sender, MouseButtonEventArgs e)
        => _agacTiklamaNoktasi = e.GetPosition(null);

    /// <summary>
    /// Eşiği aşan sol-tuş sürüklemesinde, altındaki düğüm bir TABLO ise sürüklemeyi başlatır.
    /// Yalnız tablolar tuvale konur (v6-S1) — view/SP/fonksiyon sürüklenmez.
    /// </summary>
    private void Agac_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        Point simdi = e.GetPosition(null);
        if (Math.Abs(simdi.X - _agacTiklamaNoktasi.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(simdi.Y - _agacTiklamaNoktasi.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (UstTreeViewItem(e.OriginalSource as DependencyObject)?.DataContext
                is SemaNesnesi { Tur: SemaNesneTuru.Tablo } tablo)
        {
            DragDrop.DoDragDrop((DependencyObject)sender,
                new DataObject(typeof(SemaNesnesi), tablo), DragDropEffects.Copy);
        }
    }

    /// <summary>Tuval yalnız TABLO düğümünü kabul eder (kopyalama imleci).</summary>
    private void GorselTuval_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SemaNesnesi))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Bırakılan tabloyu, bırakma konumuna kutu olarak ekler.</summary>
    private void GorselTuval_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GorselSorguSekmesiViewModel sekme }
            || e.Data.GetData(typeof(SemaNesnesi)) is not SemaNesnesi tablo)
            return;

        Point p = e.GetPosition((IInputElement)sender);
        sekme.TabloEkle(tablo, p.X, p.Y);
    }

    /// <summary>Kutu başlığından sürükleme: tuvaldeki konumu günceller (negatife düşmez).</summary>
    private void Kutu_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GorselSorguKutusu kutu)
        {
            kutu.X = Math.Max(0, kutu.X + e.HorizontalChange);
            kutu.Y = Math.Max(0, kutu.Y + e.VerticalChange);
        }
    }

    // ── Veritabanı Haritası (v9-S2) tuval etkileşimleri ───────────────────────

    /// <summary>Kart başlığından sürükleme. Delta cihaz birimindedir → zoom ölçeğine bölünür.</summary>
    private void HaritaDugum_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HaritaDugumGorunumu d)
            return;
        double olcek = _vm.SeciliSekme is HaritaSekmesiViewModel h ? h.Olcek : 1.0;
        d.X = Math.Max(0, d.X + e.HorizontalChange / olcek);
        d.Y = Math.Max(0, d.Y + e.VerticalChange / olcek);
    }

    /// <summary>Karta girişte: o kart + komşuları aydınlanır, gerisi soluklaşır.</summary>
    private void HaritaDugum_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_vm.SeciliSekme is HaritaSekmesiViewModel h
            && (sender as FrameworkElement)?.DataContext is HaritaDugumGorunumu d)
            h.Odakla(d);
    }

    private void HaritaDugum_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_vm.SeciliSekme is HaritaSekmesiViewModel h)
            h.OdakTemizle();
    }

    /// <summary>Ctrl+tekerlek → yakınlaştırma (0.3×–2.5×). Ctrl'süz tekerlek normal kaydırmaya kalır.</summary>
    private void HaritaTuval_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _vm.SeciliSekme is not HaritaSekmesiViewModel h)
            return;
        h.Olcek = Math.Clamp(h.Olcek + (e.Delta > 0 ? 0.1 : -0.1), 0.3, 2.5);
        e.Handled = true;
    }

    /// <summary>Kart gövdesine tık → Görsele göndermek için seç/seçimi kaldır (başlık = sürükleme, Thumb yutar).</summary>
    private void HaritaDugum_Tik(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SeciliSekme is HaritaSekmesiViewModel h
            && (sender as FrameworkElement)?.DataContext is HaritaDugumGorunumu d)
            h.SecimiDegistir(d);
    }

    /// <summary>Sürükleme bitince kart konumlarını kalıcılaştır (DB başına — v9-S3).</summary>
    private void HaritaDugum_DragCompleted(object sender, DragCompletedEventArgs e)
        => (_vm.SeciliSekme as HaritaSekmesiViewModel)?.KaydetIstensin();

    // ---- 🤖 AI bağlam düğmeleri (v11-S7): sonuç AYRI pencerede, asistan sekmesi açılmaz ----

    private Action<string, string> AiSekmeyeAc => (b, s) => _vm.SekmeAc(b, s, _vm.LogAnalizAktifVeritabani);

    /// <summary>Eylemi tetikleyen öğenin sekmesi; menü öğesinde DataContext akmazsa AKTİF sekmeye düşer.</summary>
    private SorguSekmesiViewModel? AiSekme(object sender)
        => (sender as FrameworkElement)?.DataContext as SorguSekmesiViewModel
            ?? _vm.SeciliSekme as SorguSekmesiViewModel;

    private string? SekmeSorgusu(object sender) => AiSekme(sender)?.MetinSaglayici?.Invoke();

    /// <summary>🤖 Tek AI düğmesi (2026-07-27): açılır menüyü düğmenin altında açar.</summary>
    private void AiMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.ContextMenu is { } menu)
        {
            menu.PlacementTarget = b; // DataContext (sekme VM) düğmeden akar → menü eylemleri doğru sekmeyi görür
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void AiAsistanAc_Click(object sender, RoutedEventArgs e) => _vm.AsistanAcCommand.Execute(null);

    private void AiDegerlendir_Click(object sender, RoutedEventArgs e)
        => PencereGoster(new Views.AsistanCevapPenceresi("AI Değerlendir",
            _vm.AsistanDegerlendirAsync(SekmeSorgusu(sender)), AiSekmeyeAc) { Owner = this });

    private void AiAcikla_Click(object sender, RoutedEventArgs e)
        => PencereGoster(new Views.AsistanCevapPenceresi("AI Açıkla",
            _vm.AsistanAciklaAsync(SekmeSorgusu(sender))) { Owner = this });

    private void AiHataCoz_Click(object sender, RoutedEventArgs e)
    {
        SorguSekmesiViewModel? sekme = AiSekme(sender);
        PencereGoster(new Views.AsistanCevapPenceresi("AI Hatayı çözdür",
            _vm.AsistanHataCozAsync(sekme?.MetinSaglayici?.Invoke(), sekme?.SonHataMesaji),
            AiSekmeyeAc) { Owner = this });
    }

    private void AiPlanYorumla_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlanSekmesiViewModel plan)
            return;
        async Task<AsistanCevabi?> Sar() => await _vm.AsistanPlanYorumlaAsync(plan.Plan);
        PencereGoster(new Views.AsistanCevapPenceresi("AI Planı yorumlat", Sar()) { Owner = this });
    }

    /// <summary>Harita arama kutusunda ENTER: ilk eşleşen tabloya ODAKLAN (#1 — mahalledeyken de çalışır).</summary>
    private void HaritaArama_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SeciliSekme is HaritaSekmesiViewModel h)
        {
            h.AramaOdakla();
            e.Handled = true;
        }
    }

    /// <summary>Haritayı PNG'ye aktarır (v9-S4): tuvali %100 ölçekte bitmap'e render eder.</summary>
    private void HaritaPng_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SeciliSekme is not HaritaSekmesiViewModel h || TuvalBul(this) is not { Content: FrameworkElement tuval })
            return;

        var diyalog = new SaveFileDialog
        {
            Filter = "PNG görüntü (*.png)|*.png",
            FileName = $"veritabani-haritasi-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        double eskiOlcek = h.Olcek;
        h.Olcek = 1.0;          // dışa aktarma %100 ölçekte, kırpılmadan
        tuval.UpdateLayout();

        int w = (int)Math.Ceiling(tuval.ActualWidth), yuk = (int)Math.Ceiling(tuval.ActualHeight);
        if (w > 0 && yuk > 0)
        {
            var rtb = new RenderTargetBitmap(w, yuk, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(tuval);
            var kodlayici = new PngBitmapEncoder();
            kodlayici.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(diyalog.FileName))
                kodlayici.Save(fs);
            h.Bilgi = $"PNG kaydedildi ({w}×{yuk}): {Path.GetFileName(diyalog.FileName)}";
        }

        h.Olcek = eskiOlcek;
        tuval.UpdateLayout();
    }

    /// <summary>Haritayı vektör SVG'ye aktarır (v9-S4): SAF üretici, aktif temaya göre.</summary>
    private void HaritaSvg_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SeciliSekme is not HaritaSekmesiViewModel h)
            return;

        var diyalog = new SaveFileDialog
        {
            Filter = "SVG dosyası (*.svg)|*.svg",
            FileName = $"veritabani-haritasi-{DateTime.Now:yyyyMMdd-HHmmss}.svg",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        File.WriteAllText(diyalog.FileName, h.SvgUret(_vm.KoyuTema), new System.Text.UTF8Encoding(false));
        h.Bilgi = $"SVG kaydedildi: {Path.GetFileName(diyalog.FileName)}";
    }

    /// <summary>Aktif harita sekmesinin tuval ScrollViewer'ını görsel ağaçta bulur (AutomationId ile).</summary>
    private static ScrollViewer? TuvalBul(DependencyObject kok)
    {
        int n = VisualTreeHelper.GetChildrenCount(kok);
        for (int i = 0; i < n; i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(kok, i);
            if (c is ScrollViewer sv && System.Windows.Automation.AutomationProperties.GetAutomationId(sv) == "HaritaTuval")
                return sv;
            if (TuvalBul(c) is { } bulunan)
                return bulunan;
        }
        return null;
    }

    /// <summary>Kutudaki ✕ → kutuyu tuvalden kaldırır.</summary>
    private void GorselKutuSil_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GorselSorguKutusu kutu
            && _vm.SeciliSekme is GorselSorguSekmesiViewModel sekme)
            sekme.KutuSil(kutu);
    }

    // ---- Görsel Sorgu: kutuları bağlama (JOIN) — v6-S2; SÜRÜKLE-BIRAK (kullanıcı bulgusu 2026-07-20) ----

    private GorselSorguKutusu? _baglaKaynak;
    private FrameworkElement? _baglaUc;

    /// <summary>
    /// ⚬'dan SÜRÜKLEME başlatır: kaynağı arar ve mouse'u yakalar (bırakış nerede olursa olsun
    /// <see cref="GorselBaglantiUcu_MouseUp"/>'a gelir). Eski "iki küçük ⚬'ı arka arkaya vur"
    /// yöntemi zordu (5 denemede tutuyordu — kullanıcı bulgusu); artık hedef, karşı tablonun
    /// TÜM kutusudur (koca hedef → kolay bağlama).
    /// </summary>
    private void GorselBaglantiUcu_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // ⚬ kutu taşımayı/tıklamayı tetiklemesin
        if (sender is not FrameworkElement fe || fe.DataContext is not GorselSorguKutusu kutu
            || _vm.SeciliSekme is not GorselSorguSekmesiViewModel sekme)
            return;

        _baglaKaynak = kutu;
        _baglaUc = fe;
        fe.CaptureMouse();
        sekme.Bilgi = $"{kutu.Nesne.Ad}: sürükleyip bağlamak istediğiniz tablonun üstünde bırakın (JOIN).";
    }

    /// <summary>Sürükleme bırakılınca: altındaki hedef kutuyu hit-test'le bulup JOIN kurar.</summary>
    private async void GorselBaglantiUcu_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_baglaUc is null || _baglaKaynak is null)
            return;
        e.Handled = true;
        FrameworkElement uc = _baglaUc;
        GorselSorguKutusu kaynak = _baglaKaynak;
        _baglaUc = null;
        _baglaKaynak = null;
        uc.ReleaseMouseCapture();

        if (_vm.SeciliSekme is not GorselSorguSekmesiViewModel sekme)
            return;

        GorselSorguKutusu? hedef = BirakilanKutu(e.GetPosition(this), kaynak);
        if (hedef is null)
        {
            sekme.Bilgi = "Bağlama iptal edildi — çizgiyi başka bir tablonun üstünde bırakın.";
            return;
        }

        // JOIN kurulur (FK varsa ON kolonları otomatik dolar) ve düzenleyici hemen açılır.
        GorselBaglanti? bag = await sekme.BaglaAsync(kaynak, hedef);
        if (bag is not null)
            GorselJoinDuzenle(sekme, bag);
    }

    /// <summary>Pencere-koordinatındaki noktanın altındaki (kaynaktan farklı) tablo kutusunu bulur.</summary>
    private GorselSorguKutusu? BirakilanKutu(Point noktaWindow, GorselSorguKutusu kaynak)
    {
        DependencyObject? d = VisualTreeHelper.HitTest(this, noktaWindow)?.VisualHit;
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: GorselSorguKutusu k } && k != kaynak)
                return k;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Bağlantı çizgisine/etiketine ÇİFT tık → join düzenleyici penceresi.</summary>
    private void GorselBaglanti_Tik(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2
            || (sender as FrameworkElement)?.DataContext is not GorselBaglanti bag
            || _vm.SeciliSekme is not GorselSorguSekmesiViewModel sekme)
            return;
        e.Handled = true;
        GorselJoinDuzenle(sekme, bag);
    }

    private void GorselJoinDuzenle(GorselSorguSekmesiViewModel sekme, GorselBaglanti bag)
    {
        // MySQL FULL OUTER JOIN'i desteklemez → düzenleyicide gizlenir ("yarım özellik yok").
        bool mysql = _profil?.Motor == MotorTuru.MySql;
        var pencere = new GorselJoinPenceresi(bag, mysql) { Owner = this };
        pencere.ShowDialog();
        if (pencere.Silindi)
            sekme.BaglantiSil(bag);
        bag.OzetiTazele();
    }

    /// <summary>Kutuya ÇİFT tık → bu tablonun WHERE koşulları düzenleyicisi (v6-S3).</summary>
    private void GorselKutu_CiftTik(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.DataContext is not GorselSorguKutusu kutu)
            return;
        e.Handled = true;
        new GorselWherePenceresi(kutu) { Owner = this }.ShowDialog();
        kutu.KosullariTazele(); // rozet/görünürlük güncellensin
        GorselSekmeBul(sender as DependencyObject)?.CipleriTazele(); // çip şeridi de (#2)
    }

    // ---- WHERE huni popover'ı (v11-öncesi #2, kullanıcı onayı 2026-07-25) ----
    // Kolon satırındaki huni → ayrı pencere DEĞİL, yerinde küçük popover: operatör + tipe göre
    // değer (tarih=takvim, bit=Evet/Hayır, diğer=metin). Eklenen koşul üstte ÇİP olur.

    private Popup? _huniPopup;

    /// <summary>Görsel ağaçtan yukarı yürüyüp Görsel Sorgu sekme VM'ini bulur.</summary>
    private static GorselSorguSekmesiViewModel? GorselSekmeBul(DependencyObject? d)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: GorselSorguSekmesiViewModel s })
                return s;
        }
        return null;
    }

    private void GorselHuni_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not GorselKolonSecimi secim)
            return;

        // Kutu + sekme VM görsel ağaçtan (satırın DataContext'i kolon; kutu ve sekme üstlerde).
        GorselSorguKutusu? kutu = null;
        GorselSorguSekmesiViewModel? sekme = null;
        for (DependencyObject? d = fe; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (kutu is null && d is FrameworkElement { DataContext: GorselSorguKutusu k })
                kutu = k;
            if (d is FrameworkElement { DataContext: GorselSorguSekmesiViewModel s })
            {
                sekme = s;
                break;
            }
        }
        if (kutu is null || sekme is null)
            return;

        e.Handled = true;
        HuniPopoverAc(fe, sekme, kutu, secim.Kolon);
    }

    private void HuniPopoverAc(
        FrameworkElement hedef, GorselSorguSekmesiViewModel sekme, GorselSorguKutusu kutu, SemaKolonu kolon)
    {
        _huniPopup?.IsOpen = false;

        bool tarihMi = kolon.Tip.Contains("date", StringComparison.OrdinalIgnoreCase)
            || kolon.Tip.Contains("time", StringComparison.OrdinalIgnoreCase);
        bool bitMi = kolon.Tip.Contains("bit", StringComparison.OrdinalIgnoreCase)
            || kolon.Tip.Contains("bool", StringComparison.OrdinalIgnoreCase);

        var opKutusu = new ComboBox
        {
            ItemsSource = GorselWherePenceresi.Operatorler,
            DisplayMemberPath = "Etiket",
            SelectedIndex = 0,
            MinWidth = 190,
        };

        // Değer editörü kolon TİPİNE göre: tarih=takvim, bit=Evet/Hayır, diğer=metin kutusu.
        FrameworkElement degerEditoru;
        Func<string> degerOku;
        if (tarihMi)
        {
            var dp = new DatePicker { MinWidth = 150 };
            degerEditoru = dp;
            degerOku = () => dp.SelectedDate?.ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        else if (bitMi)
        {
            var cb = new ComboBox
            {
                ItemsSource = new[] { "Evet (1)", "Hayır (0)" },
                SelectedIndex = 0,
                MinWidth = 120,
            };
            degerEditoru = cb;
            degerOku = () => cb.SelectedIndex == 0 ? "1" : "0"; // sayı → üretici tırnaksız yazar
        }
        else
        {
            var tb = new TextBox { MinWidth = 170, Padding = new Thickness(4, 3, 4, 3) };
            degerEditoru = tb;
            degerOku = () => tb.Text;
        }

        // IS NULL / IS NOT NULL değer istemez — editör pasifleşir (WHERE penceresiyle aynı kural).
        opKutusu.SelectionChanged += (_, _) =>
        {
            KosulOperatoru op = ((OperatorSecimi)opKutusu.SelectedItem).Deger;
            degerEditoru.IsEnabled = op is not (KosulOperatoru.Bos or KosulOperatoru.DoluDegil);
        };

        var ekle = new Button
        {
            Content = "＋ Ekle",
            Padding = new Thickness(14, 4, 14, 4),
            Style = (Style)FindResource("BirincilDugme"),
            IsDefault = true,
        };
        void Ekle()
        {
            KosulOperatoru op = ((OperatorSecimi)opKutusu.SelectedItem).Deger;
            string deger = degerOku();
            bool degerGerekli = op is not (KosulOperatoru.Bos or KosulOperatoru.DoluDegil);
            if (degerGerekli && deger.Length == 0)
                return; // boş değerle dolu koşul olmaz — üretici zaten atlardı, hiç ekleme
            sekme.KosulEkle(kutu, kolon.Ad, op, deger);
            _huniPopup!.IsOpen = false;
        }
        ekle.Click += (_, _) => Ekle();
        if (degerEditoru is TextBox metinKutusu)
            metinKutusu.KeyDown += (_, args) => { if (args.Key == Key.Enter) Ekle(); };

        var baslik = new TextBlock
        {
            Text = $"{kutu.Nesne.TamAd}.{kolon.Ad}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 8),
        };
        baslik.SetResourceReference(TextBlock.ForegroundProperty, "VurguFircasi");

        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(baslik);
        panel.Children.Add(opKutusu);
        degerEditoru.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(degerEditoru);
        var altSatir = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(ekle, Dock.Right);
        altSatir.Children.Add(ekle);
        altSatir.Children.Add(new Border());
        panel.Children.Add(altSatir);

        var cerceve = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = panel,
        };
        cerceve.SetResourceReference(Border.BackgroundProperty, "PencereZeminFircasi");
        cerceve.SetResourceReference(Border.BorderBrushProperty, "VurguFircasi");

        _huniPopup = new Popup
        {
            PlacementTarget = hedef,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,   // dışarı tık = vazgeç (hafif popover davranışı)
            AllowsTransparency = true,
            Child = cerceve,
            IsOpen = true,
        };
        degerEditoru.Focus();
    }

    /// <summary>
    /// Düzenleme grid'i kolon kuralları (V2-S5): identity/computed/rowversion hücreleri
    /// salt-okunur (sunucu malı — 07-r2 §4); başlık tooltip'i tipi + rolü söyler.
    /// </summary>
    private void DuzenlemeGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (((DataGrid)sender!).DataContext is not DuzenlemeSekmesiViewModel vm
            || vm.Meta?.Kolonlar.FirstOrDefault(k => k.Ad == e.PropertyName) is not { } kolon)
            return;

        e.Column.IsReadOnly = !kolon.Yazilabilir;

        // Hücre içeriği + düzenleme kutusu dikey ortalı (kullanıcı bulgusu 2026-08-06): satır
        // MinRowHeight'tan uzun olunca metin/imleç üste yapışıyordu; sonuç grid'iyle aynı hiza.
        if (e.Column is DataGridTextColumn metinKolon)
        {
            // v23-S13 ("hiçbir alan formatlanmasın"): gösterim SSMS ham biçimi, geri yazım
            // kültür-güvenli ayrıştırma (DuzenlemeHamConverter — tr-TR'de "1250.75"→125075 tuzağı).
            if (metinKolon.Binding is Binding mevcut
                && ((DataGrid)sender!).ItemsSource is System.Data.DataView gorunum
                && gorunum.Table?.Columns[e.PropertyName] is { } dataKolon)
            {
                metinKolon.Binding = new Binding(mevcut.Path.Path)
                {
                    Mode = mevcut.Mode,
                    UpdateSourceTrigger = mevcut.UpdateSourceTrigger,
                    ValidatesOnExceptions = true,
                    Converter = new DuzenlemeHamConverter(dataKolon.DataType, kolon.GosterimTipi),
                    ConverterCulture = System.Globalization.CultureInfo.InvariantCulture,
                };
            }

            var goster = new Style(typeof(TextBlock));
            goster.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            metinKolon.ElementStyle = goster;

            var duzenle = new Style(typeof(TextBox));
            duzenle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            metinKolon.EditingElementStyle = duzenle;
        }

        string rol = kolon switch
        {
            { IdentityMi: true } => " · identity (sunucu üretir)",
            { ComputedMi: true } => " · computed (sunucu hesaplar)",
            { RowversionMi: true } => " · rowversion (eşzamanlılık)",
            { PkMi: true } => " · PK",
            _ => "",
        };
        var temaStili = TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var baslikStili = new Style(typeof(DataGridColumnHeader), temaStili);
        baslikStili.Setters.Add(new Setter(ToolTipProperty, $"{kolon.Ad}  ·  {kolon.GosterimTipi}{rol}"));
        e.Column.HeaderStyle = baslikStili;
    }

    /// <summary>
    /// Edit modu grid köprüleri (V5-S5). Hücre seçimi ve pano WPF'in işidir; VM bunları
    /// kendi başına bilemez, delegelerle sorar.
    /// </summary>
    private void DuzenlemeGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.DataContext is not DuzenlemeSekmesiViewModel sekme)
            return;

        sekme.SeciliHucreler = () =>
        {
            var liste = new List<(int, string)>();
            foreach (DataGridCellInfo hucre in grid.SelectedCells)
            {
                if (hucre.Item is DataRowView satir && hucre.Column is { } kolon)
                {
                    int satirNo = satir.Row.Table.Rows.IndexOf(satir.Row);
                    if (satirNo >= 0)
                        liste.Add((satirNo, KolonAdi(kolon)));
                }
            }
            return liste;
        };

        sekme.BaslangicHucresi = () =>
        {
            if (grid.CurrentCell is not { IsValid: true } gecerli
                || gecerli.Item is not DataRowView satir || gecerli.Column is null)
                return null;

            int satirNo = satir.Row.Table.Rows.IndexOf(satir.Row);
            int kolonNo = satir.Row.Table.Columns.IndexOf(KolonAdi(gecerli.Column));
            return satirNo < 0 || kolonNo < 0 ? null : (satirNo, kolonNo);
        };

        sekme.TopluGuncellemeIste = kolonlar =>
        {
            var pencere = new TopluGuncellemePenceresi(kolonlar) { Owner = this };
            return pencere.ShowDialog() == true ? pencere.Sonuc : null;
        };
    }

    /// <summary>AutoGenerateColumns başlığı kolon adıdır; binding yolundan almak daha güvenli.</summary>
    private static string KolonAdi(DataGridColumn kolon)
        => kolon is DataGridBoundColumn { Binding: Binding baglama } && baglama.Path?.Path is { } yol
            ? yol
            : kolon.Header?.ToString() ?? "";

    /// <summary>
    /// Ctrl+0 → seçili hücrelere NULL (SSMS kısayolu); Ctrl+V → çoklu satır yapıştırma.
    /// Yapıştırma yalnız düzenleme MODUNDA DEĞİLKEN devreye girer — hücre içi metin
    /// yapıştırmayı bozmamak için.
    /// </summary>
    private void DuzenlemeGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid || grid.DataContext is not DuzenlemeSekmesiViewModel sekme)
            return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (!ctrl)
            return;

        if (e.Key is Key.D0 or Key.NumPad0)
        {
            sekme.NullAta();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V && Clipboard.ContainsText())
        {
            sekme.Yapistir(Clipboard.GetText());
            e.Handled = true;
        }
    }

    private void SelectScripti_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } nesne)
            _vm.SelectScriptiAc(nesne);
    }

    /// <summary>＋ Yeni veritabanı (2026-07-31): ad sor → CREATE DATABASE → ağaç/listeler tazelenir.</summary>
    private async void YeniVeritabani_Click(object sender, RoutedEventArgs e)
    {
        var pencere = new Views.YeniVeritabaniPenceresi { Owner = this };
        if (pencere.ShowDialog() == true)
            await _vm.YeniVeritabaniOlusturAsync(pencere.Ad);
    }

    /// <summary>🕸 Kayıt Haritası (2026-07-31): satır sağ-tık → FK'larla bağlı üst/alt kayıtlar,
    /// yorumlu script olarak sekmede ÇALIŞTIRILIR (çoklu grid + düzenlenebilir SELECT'ler).</summary>
    private async void KayitHaritasi_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Parent is not ContextMenu menu
            || menu.PlacementTarget is not DataGrid grid
            || grid.SelectedItem is not System.Data.DataRowView satirGorunum
            || _vm.SeciliSekme is not SorguSekmesiViewModel sekme
            || _vm.AktifProfil is not { } profil)
        {
            Iletisim.Bilgi(this, "SQLST — Kayıt Haritası", "Önce bir satır seçin",
                "Kayıt haritası, sonuçta seçili satırın ilişkili kayıtlarını gösterir.");
            return;
        }

        var satir = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Data.DataColumn kolon in satirGorunum.Row.Table.Columns)
            satir[kolon.ColumnName] = satirGorunum.Row[kolon];

        string? tabloAdi = SQLST.Application.KayitHaritasi.TabloCikar(sekme.SonCalisanSql ?? "");

        // 🕸 İNME (v23-S9 — canlı tanı 1 Eki 2026, kullanıcı: "belgenin altındakilere inemiyoruz"):
        // satır bir HARİTA grid'inden geliyorsa ([İlişki] kolonu var) kaynak tablo o başlıktan
        // çözülür — script'in İLK FROM'u değil. Eski yol, Fatura satırından inişte faturanın
        // Id'sini ilk tablonun (Talepler) Id'si sanıp BAŞKA kaydın haritasını getiriyordu
        // (sessiz yanlış). [İlişki] sözlükten de çıkarılır — gerçek kolon değil.
        if (satir.TryGetValue("İlişki", out object? iliskiDegeri)
            && SQLST.Application.KayitHaritasi.IliskiBasligindanTablo(iliskiDegeri) is { } inilen)
        {
            tabloAdi = string.IsNullOrEmpty(inilen.Sema) ? inilen.Ad : $"{inilen.Sema}.{inilen.Ad}";
            satir.Remove("İlişki");
        }

        SemaOnbellegi? onbellek = await _vm.OnbellekGetirAsync(sekme.SecilenVeritabani);
        string? sade = tabloAdi?.Split('.')[^1];
        string? sema = tabloAdi is not null && tabloAdi.Contains('.') ? tabloAdi.Split('.')[^2] : null;
        SemaNesnesi? tablo = tabloAdi is null ? null : onbellek?.Nesneler.FirstOrDefault(n =>
            n.Tur == SemaNesneTuru.Tablo && n.Ad.Equals(sade, StringComparison.OrdinalIgnoreCase)
            && (sema is null || n.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase)));
        if (tablo is null || onbellek is null)
        {
            Iletisim.Bilgi(this, "SQLST — Kayıt Haritası", "Kaynak tablo belirlenemedi",
                "Kayıt haritası TEK tablolu SELECT sonuçlarında çalışır.");
            return;
        }

        await KayitHaritasiKurAsync(tablo, satir, onbellek, profil, sekme.SecilenVeritabani);
    }

    /// <summary>
    /// Bir haritada en fazla kaç "dolu mu?" sondası atılır — her sonda bir gidiş-dönüş.
    /// 40 → 200 (kullanıcı kararı 25 Ağu 2026: "tavanı da 40'tan artıralım"). Sondalar TOP 1 olduğu
    /// için tek tek ucuz; asıl maliyet gidiş-dönüş sayısı. Tavan tamamen KALDIRILMADI — çok dallanan
    /// bir şemada sonda sayısı patlayıp arayüzü dakikalarca kilitleyebilir; 200 hem geniş hem sonlu.
    /// </summary>
    private const int HaritaEnFazlaSonda = 200;

    /// <summary>
    /// 🕸 Kayıt Haritası v2 (v22-S9, kullanıcı isteği 25 Ağu 2026): ilişkileri ÖNCE yoklar, yalnız
    /// DOLU olanları script'e alır; dolu olanların komşularını da (2. seviye) ekler; tanım/lookup
    /// tablolarını atlar.
    ///
    /// Eskiden bütün ilişkiler koşulur, çoğu boş grid olarak açılırdı. Artık her ilişki için önce
    /// ucuz bir "en az bir satır var mı" sondası atılır (TOP 1, COUNT değil) ve boşlar SELECT'e
    /// dönüşmez — ama SESSİZCE de kaybolmaz: script sonunda yorumla listelenir (bkz.
    /// <see cref="SQLST.Application.KayitHaritasi.SuzulmusScriptUret"/>).
    /// </summary>
    private async Task KayitHaritasiKurAsync(
        SemaNesnesi tablo, Dictionary<string, object?> satir, SemaOnbellegi onbellek,
        ConnectionProfile profil, string? veritabani)
    {
        ILehce lehce = _lehceSaglayici.Getir(profil.Motor);
        IReadOnlyList<YabanciAnahtar> fkler = onbellek.YabanciAnahtarlar;
        IReadOnlyList<SemaNesnesi> tablolar = onbellek.Nesneler;

        // Süzgeçli ve süzgeçsiz listeyi ayrı ayrı üretip FARKINI alıyoruz: böylece "lookup diye neyi
        // atladık" kullanıcıya raporlanabiliyor (sessiz eleme yok) ve saf katmanın imzası şişmiyor.
        var hepsi = SQLST.Application.KayitHaritasi.IliskiSorgulari(
            tablo, satir, fkler, lehce, tablolar: tablolar);
        var birinci = SQLST.Application.KayitHaritasi.IliskiSorgulari(
            tablo, satir, fkler, lehce, tablolar: tablolar, tanimTablolariniAtla: true);
        var atlananTanim = hepsi.Where(h => !birinci.Any(b => b.Baslik == h.Baslik))
            .Select(h => h.Baslik).ToList();

        if (hepsi.Count == 0)
        {
            Iletisim.Bilgi(this, "SQLST — Kayıt Haritası", "İlişki bulunamadı",
                $"{tablo.TamAd} için şemada FK ilişkisi yok (ya da satırın FK kolonları NULL).");
            return;
        }

        var dolular = new List<SQLST.Application.KayitHaritasi.Iliski>();
        int bosSayisi = 0;
        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sonda = 0;
        bool tavanaTakildi = false;

        async Task<bool> DoluMuAsync(SQLST.Application.KayitHaritasi.Iliski i)
        {
            if (sonda >= HaritaEnFazlaSonda)
            {
                tavanaTakildi = true;
                return false;
            }
            sonda++;
            _vm.Durum = $"🕸 Kayıt Haritası: {sonda}. ilişki yoklanıyor — {i.Sema}.{i.Ad}…";
            (ResultSetData? set, string? _) = await _vm.TekSetSorguAsync(i.VarlikSql, veritabani, 1);
            // Sorgu hata verirse (izin yok, tablo yok…) DOLU SAYMIYORUZ ama boş da demiyoruz:
            // set null ise ilişkiyi atlıyoruz — uydurma bir "dolu" göstermek yanlış olurdu.
            return set is { Satirlar.Count: > 0 };
        }

        foreach (SQLST.Application.KayitHaritasi.Iliski i in birinci)
        {
            if (await DoluMuAsync(i))
            {
                dolular.Add(i);
                gorulen.Add($"{i.Sema}.{i.Ad}");
            }
            else if (!tavanaTakildi)
            {
                bosSayisi++;
            }
        }

        // 2. seviye YALNIZ dolu olanların üzerinden — boş bir tablonun komşusu da boş olur.
        foreach (SQLST.Application.KayitHaritasi.Iliski d in dolular.ToList())
        {
            var ikinci = SQLST.Application.KayitHaritasi.IkinciSeviye(
                d, tablo, fkler, lehce, tablolar: tablolar, tanimTablolariniAtla: true, gorulen: gorulen);
            foreach (SQLST.Application.KayitHaritasi.Iliski i in ikinci)
            {
                if (await DoluMuAsync(i))
                    dolular.Add(i);
                else if (!tavanaTakildi)
                    bosSayisi++;
            }
        }

        _vm.Durum = "";
        if (dolular.Count == 0)
        {
            Iletisim.Bilgi(this, "SQLST — Kayıt Haritası", "Bağlı kayıt yok",
                $"{tablo.TamAd} için {hepsi.Count} ilişki var ama bu kayıt için hiçbiri dolu değil — "
                + "bu satıra bağlı başka kayıt bulunmuyor.");
            return;
        }

        string? not = tavanaTakildi
            ? $"⚠ SONDA TAVANI: en fazla {HaritaEnFazlaSonda} ilişki yoklandı, kalanlar denenmedi "
              + "(harita çok dallanıyor — bu script eksik olabilir)."
            : null;

        _vm.SekmeAcVeCalistir($"harita-{tablo.Ad}",
            SQLST.Application.KayitHaritasi.SuzulmusScriptUret(
                tablo.TamAd, dolular, bosSayisi, atlananTanim, not),
            veritabani);
    }

    /// <summary>🗑 Kayıt Haritası DELETE (2026-07-31): alt kayıtlar + kaynak satır için DELETE script'i —
    /// ÇALIŞTIRILMAZ, sekmede açılır (Güvenli Yazma + 🔍 önizlemeyle koşulmalı).</summary>
    private async void KayitHaritasiDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Parent is not ContextMenu menu
            || menu.PlacementTarget is not DataGrid grid
            || grid.SelectedItem is not System.Data.DataRowView satirGorunum
            || _vm.SeciliSekme is not SorguSekmesiViewModel sekme
            || _vm.AktifProfil is not { } profil)
            return;

        var satir = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Data.DataColumn kolon in satirGorunum.Row.Table.Columns)
            satir[kolon.ColumnName] = satirGorunum.Row[kolon];

        string? tabloAdi = SQLST.Application.KayitHaritasi.TabloCikar(sekme.SonCalisanSql ?? "");
        // 🕸 İNME (v23-S9): harita grid'inden gelen satırda kaynak, [İlişki] başlığından çözülür —
        // yoksa buradaki tehlike haritadakinden BÜYÜK: yanlış tabloya/yanlış Id'ye DELETE script'i
        // üretilirdi (çalıştırılmasa da tuzak). Gerekçe: KayitHaritasi_Click'teki not.
        if (satir.TryGetValue("İlişki", out object? iliskiDegeri)
            && SQLST.Application.KayitHaritasi.IliskiBasligindanTablo(iliskiDegeri) is { } inilen)
        {
            tabloAdi = string.IsNullOrEmpty(inilen.Sema) ? inilen.Ad : $"{inilen.Sema}.{inilen.Ad}";
            satir.Remove("İlişki");
        }

        SemaOnbellegi? onbellek = await _vm.OnbellekGetirAsync(sekme.SecilenVeritabani);
        string? sade = tabloAdi?.Split('.')[^1];
        string? sema = tabloAdi is not null && tabloAdi.Contains('.') ? tabloAdi.Split('.')[^2] : null;
        SemaNesnesi? tablo = tabloAdi is null ? null : onbellek?.Nesneler.FirstOrDefault(n =>
            n.Tur == SemaNesneTuru.Tablo && n.Ad.Equals(sade, StringComparison.OrdinalIgnoreCase)
            && (sema is null || n.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase)));
        if (tablo is null || onbellek is null)
        {
            Iletisim.Bilgi(this, "SQLST — DELETE script'i", "Kaynak tablo belirlenemedi",
                "DELETE script'i tek tablolu SELECT sonuçlarında üretilebilir.");
            return;
        }

        string? script = SQLST.Application.KayitHaritasi.DeleteScriptUret(
            tablo, satir, onbellek.YabanciAnahtarlar, _lehceSaglayici.Getir(profil.Motor));
        if (script is null)
        {
            Iletisim.Uyari(this, "SQLST — DELETE script'i", "Güvenli DELETE üretilemez",
                $"{tablo.TamAd}: PK yok ya da PK değeri sonuçta seçili değil.");
            return;
        }

        _vm.SekmeAc($"delete-{tablo.Ad}", script, sekme.SecilenVeritabani); // ÇALIŞTIRILMAZ — bilinçli
    }

    /// <summary>🔎 Veri Arama SEKMESİNİ açar (kullanıcı isteği 2026-08-09: ayrı pencere yerine sekme).</summary>
    private void VeriArama_Click(object sender, RoutedEventArgs e) => _vm.VeriAramaAc();

    /// <summary>🪄 SP Sihirbazı (2026-07-31): sol ray → giriş/çıkışlardan SP taslağı üret.</summary>
    private void SpSihirbazi_Click(object sender, RoutedEventArgs e)
    {
        string? aktifDb = (_vm.SeciliSekme as SorguSekmesiViewModel)?.SecilenVeritabani ?? _vm.VarsayilanVeritabani;
        // m.22 devamı (2026-08-14): pencere değil SEKME olarak açılır (yönetim paneli gibi).
        _vm.AracSekmesiAcVeyaSec("🪄 SP Sihirbazı", () => new Views.SpSihirbaziPenceresi(
            _vm.VeritabaniAdlari, aktifDb,
            db => _vm.OnbellekGetirAsync(db),
            (baslik, sql, db) => _vm.SekmeAc(baslik, sql, db)));
    }

    /// <summary>📤 Şema Kopyalama (2026-07-31): DB düğümü sağ-tık → şemayı başka bağlantıya kur.</summary>
    private async void SemaKopyala_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.AktifProfil is not { } kaynak
            || (sender as FrameworkElement)?.DataContext is not ViewModels.GezginVeritabani dugum)
            return;
        IReadOnlyList<ConnectionProfile> profiller = await _profilDeposu.GetAllAsync();
        PencereGoster(new Views.SemaKopyalamaPenceresi(_semaKopyalama, kaynak, dugum.Ad, profiller) { Owner = this });
    }

    /// <summary>🔗 Bağımlılıklar (2026-08-03): nesne sağ-tık → tembel yüklemeli AĞAÇ penceresi
    /// (kullanıcı isteği: SP zinciri / ALTER etki analizi); iki-grid script görünümü pencerede düğme.</summary>
    private void Bagimliliklar_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is not { } nesne || _vm.AktifProfil is not { } profil)
            return;
        PencereGoster(new Views.BagimlilikAgaciPenceresi(
            nesne, nesne.Veritabani,
            sql => _vm.LogAnalizSorgusuAsync(nesne.Veritabani, sql),
            (baslik, sql, db) => _vm.SekmeAcVeCalistir(baslik, sql, db),
            _lehceSaglayici.Getir(profil.Motor).MotorId) { Owner = this });
    }

    private async void InsertOrnegi_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is not { } nesne)
            return;

        // #11: kaç satır INSERT üretileceğini mini pencerede sor (kullanıcı isteği 2026-07-29).
        var pencere = new Views.InsertSatirPenceresi { Owner = this };
        if (pencere.ShowDialog() == true)
            await _vm.InsertSablonuAcAsync(nesne, pencere.Satir);
    }

    /// <summary>m.29: INSERT örneğinin UPDATE eşi — satır sayısı sorusu yok, tek şablon.</summary>
    private async void UpdateOrnegi_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } nesne)
            await _vm.UpdateSablonuAcAsync(nesne);
    }

    private async void SpCalistir_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is not { } sp)
            return;

        // Parametresiz SP: doğrudan EXEC + çalıştır (eski yol). Parametreli SP: SSMS gibi
        // değer giriş penceresi (kullanıcı isteği 2026-07-27) → EXEC üret → sekmede aç/çalıştır.
        if (sp.Parametreler.Count == 0)
        {
            await _vm.SpCalistirAsync(sp);
            return;
        }

        var pencere = new Views.SpParametrePenceresi(sp) { Owner = this };
        if (pencere.ShowDialog() != true || pencere.Sonuc is null)
            return;

        SorguSekmesiViewModel sekme = _vm.SekmeAc($"EXEC {sp.Ad}", pencere.Sonuc, sp.Veritabani);
        if (pencere.CalistirIstendi)
            await sekme.CalistirAsync();
    }

    /// <summary>🔌 v14-S4: SP imzasından WCF sarmalayıcı iskeleti — SQLST-N sekmesinde açılır.</summary>
    private void ServisIskeleti_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } sp)
            _vm.SekmeAc("", SQLST.Application.ServisIskeletiUretici.Uret(sp), null);
    }

    private async void NesneScripti_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } nesne)
            await _vm.NesneScriptiAcAsync(nesne);
    }

    // ---- Nesne tarihçesi + Script-as (V2-S8) ----

    private async void ScriptCreate_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } nesne)
            await _vm.ScriptAsAcAsync(nesne, dropCreate: false);
    }

    private async void ScriptDropCreate_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } nesne)
            await _vm.ScriptAsAcAsync(nesne, dropCreate: true);
    }

    private async void ScriptCreateTable_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is { } nesne)
            await _vm.CreateTableAcAsync(nesne);
    }

    private async void Tarihce_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is not { } nesne)
            return;

        // HER AÇILIŞTA sunucudaki güncel tanım da işlenir (kullanıcı bulgusu 2026-07-19).
        // Eskiden bu yalnız yerel liste BOŞSA yapılıyordu; "alter-öncesi" kaydı listeyi
        // zaten doldurduğu için pratikte hiç çalışmıyor ve pencere BAYAT açılıyordu.
        // Yan fayda: nesne başkası tarafından dışarıda değiştirildiyse de yakalanır —
        // "dışarıda değişti" rozetinin dayandığı karşılaştırma ancak böyle güncel olur.
        IReadOnlyList<TarihceKaydi> kayitlar = await _vm.TarihceyiBaslatAsync(nesne);
        if (kayitlar.Count == 0)
            kayitlar = await _vm.TarihceListesiAsync(nesne);   // tanım okunamadı (şifreli) → eldekini göster

        if (kayitlar.Count == 0)
        {
            Iletisim.Bilgi(this, "SQLST — Tarihçe", "Tarihçe başlatılamadı",
                $"{nesne.TamAd} tanımı okunamadı (şifreli olabilir).");
            return;
        }
        new TarihcePenceresi(nesne.TamAd, kayitlar,
            (baslik, sql) => _vm.SekmeAc(baslik, sql, nesne.Veritabani)) { Owner = this }.ShowDialog();
    }

    private void KolonlariKopyala_Click(object sender, RoutedEventArgs e)
    {
        if (MenuNesnesi(sender) is not { } nesne)
            return;

        Clipboard.SetText(NesneScriptleyici.KolonListesi(nesne));
        _vm.Durum = $"{nesne.TamAd}: {nesne.Kolonlar.Count} kolon adı panoya kopyalandı.";
    }

    // ---- Sonuç grid'leri (S4) ----

    /// <summary>
    /// Otomatik üretilen kolonlara S4 davranışları takılır: DBNull hücreler soluk
    /// italik "NULL" görünür (FG-4.8), kolon başlığı tooltip'i SQL tipini gösterir (FG-4.10).
    /// </summary>
    private void Grid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        // v23-S13 ("db nasıl ise öyle"): bit kolonu onay kutusu DEĞİL — SSMS gibi 1/0 METNİ.
        // (Sonuç grid'i salt-okunur; düzenleme grid'inde onay kutusu kalır — orada girdi aracıdır.)
        if (e.Column is DataGridCheckBoxColumn)
            e.Column = new DataGridTextColumn { Header = e.Column.Header, SortMemberPath = e.PropertyName };
        if (e.Column is not DataGridTextColumn kolon)
            return;

        // Özel karakterli kolon adları (ör. AS [Musteri.Ad]) düz path'i bozar;
        // DataRowView indexer biçimi ("[ad]", ^-kaçışlı) her adla çalışır.
        string guvenliYol = GuvenliBindingYolu(e.PropertyName);
        // v23-S13: kolonun SQL tipi converter'a gider — ham metin SSMS'le birebir (datetime .fff,
        // datetime2(n) n hane, money 4 hane, bit 1/0, guid BÜYÜK). Kültür bağlamaya hiç girmez.
        string? sqlTip = ((DataGrid)sender!).DataContext is SonucSetiGorunumu tipSet
            && tipSet.KolonTipleri.TryGetValue(e.PropertyName, out string? t) ? t : null;
        kolon.Binding = new Binding(guvenliYol) { Converter = HucreMetniConverter.Ornek, ConverterParameter = sqlTip };
        // v23-S8 (kullanıcı bulgusu 1 Eki 2026): pano bağlaması AYRI ve HAM verilir — verilmezse
        // Ctrl+C görüntü binding'ini kullanır: uzun hücre "… (N karakter — çift tık)" kırpığıyla,
        // çok satırlı hücre ⏎'li haliyle kopyalanıyordu ("kopyalayamıyorum, illa açmam gerekiyor").
        kolon.ClipboardContentBinding = new Binding(guvenliYol) { Converter = PanoHamConverter.Ornek, ConverterParameter = sqlTip };

        var hucreStili = new Style(typeof(TextBlock));
        // Başlık metni Padding=7 ile içeriden başlar; hücre metni de aynı hizadan başlasın
        // (hücre kenarlığı 1px + 6px kenar boşluğu = 7) — kolon başlığı/veri kayması olmasın.
        hucreStili.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0)));
        hucreStili.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        // Tek satır + taşınca "…" (kullanıcı bulgusu 2026-07-20): çok satırlı metin converter'da
        // zaten ⏎'ye indirildi; NoWrap + ellipsis satır yüksekliğini SABİT tutar (yan yana hizalı).
        hucreStili.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        hucreStili.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        var dbNullTetik = new DataTrigger
        {
            Binding = new Binding(guvenliYol) { Converter = DbNullMuConverter.Ornek },
            Value = true,
        };
        dbNullTetik.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Gray));
        dbNullTetik.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
        hucreStili.Triggers.Add(dbNullTetik);
        // m.10 fikir 2: 🔗 FK/tanım kolonlarında hover → "3 → Onaylandı". Tooltip'in AÇILABİLMESİ
        // için değerinin null olmaması gerekir (yer tutucu); işleyici ya gerçek açıklamayı yazar
        // ya da e.Handled ile tooltip'i hiç göstermez (FK olmayan kolonlarda gürültü yok).
        hucreStili.Setters.Add(new Setter(ToolTipProperty, "…"));
        hucreStili.Setters.Add(new EventSetter(ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler(HucreLookup_ToolTipAciliyor)));
        kolon.ElementStyle = hucreStili;

        // Başlık stili: tema tabanı + (varsa) tip tooltip'i + sağ tık "bu kolonu gizle" (kolon araçları).
        var temaStili = TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var baslikStili = new Style(typeof(DataGridColumnHeader), temaStili);
        if (((DataGrid)sender!).DataContext is SonucSetiGorunumu set
            && set.KolonTipleri.TryGetValue(e.PropertyName, out string? tip))
            baslikStili.Setters.Add(new Setter(ToolTipProperty, $"{e.PropertyName}  ·  {tip}"));

        // Sağ tık → bu kolonu gizle (huniyi açmadan tek kolon gizlemenin kısayolu — 2026-07-28).
        var baslikMenu = new ContextMenu();
        var gizleOge = new MenuItem { Header = "🚫 Bu kolonu gizle", Tag = kolon };
        gizleOge.Click += KolonuGizle_Click;
        baslikMenu.Items.Add(gizleOge);
        // m.10 fikir 5: kolon istatistiği (MIN/MAX/AVG/SUM/DISTINCT/NULL) — T-SQL'e özgü (ScriptDom).
        var istatistikOge = new MenuItem
        {
            Header = "📊 İstatistik (MIN/MAX/ORT/DISTINCT/NULL)",
            Tag = e.PropertyName,
            ToolTip = "Sorgunun süzgeci korunarak tek sorguyla hesaplanır — gridde görünen değil, eşleşen TÜM satırlar",
        };
        istatistikOge.Click += KolonIstatistik_Click;
        baslikMenu.Items.Add(istatistikOge);
        baslikStili.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, baslikMenu));
        kolon.HeaderStyle = baslikStili;
    }

    // ---- m.10 fikir 3: hücreden hızlı filtre (sağ tık → WHERE'e koşul ekle + yeniden çalıştır) ----

    /// <summary>Sağ tık menüsü açılırken filtre öğelerinin başlığını/görünürlüğünü seçili hücreye
    /// göre kurar: "✚ [Tutar] = 310,75 filtrele". T-SQL'e özgü (ScriptDom) → yalnız MSSQL'de görünür;
    /// ≥/≤ yalnız sıralanabilir tiplerde (metin/bool'da gizli).</summary>
    private void SonucMenu_Aciliyor(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid || grid.ContextMenu is null)
            return;

        (string Kolon, object? Deger)? hucre = SeciliHucreBilgisi(grid);
        bool acik = hucre is not null
            && _vm.AktifProfil is { Motor: MotorTuru.Mssql }
            && _vm.SeciliSekme is SorguSekmesiViewModel { SonCalisanSql.Length: > 0 };
        bool siralanabilir = acik && HizliFiltre.SiralanabilirMi(hucre!.Value.Deger);
        string kisaDeger = acik ? DegerKisalt(hucre!.Value.Deger) : "";
        string kolonAd = acik ? hucre!.Value.Kolon : "";

        foreach (object oge in grid.ContextMenu.Items)
        {
            if (oge is Separator { Tag: "F:Ayrac" } ayrac)
            {
                ayrac.Visibility = acik ? Visibility.Visible : Visibility.Collapsed;
                continue;
            }
            if (oge is not MenuItem { Tag: string etiket } menu)
                continue;

            // v22-S1 (saha turu-2 m.5): MOTOR süzgeci burada kurulur. XAML'de
            // "RelativeSource AncestorType=Window" bağlaması ContextMenu'nun popup ağacından
            // Window'u BULAMIYOR → binding düşüp Visibility varsayılanda (Visible) kalıyordu;
            // Mongo'ya özel "bu id nerede geçiyor" MSSQL'de de görünüyordu.
            if (etiket.StartsWith("M:"))
            {
                menu.Visibility = MotorGorunurlugu(etiket, menu.Visibility);
                continue;
            }
            if (!etiket.StartsWith("F:"))
                continue;

            bool kiyas = etiket is "F:BuyukEsit" or "F:KucukEsit";
            menu.Visibility = acik && (!kiyas || siralanabilir) ? Visibility.Visible : Visibility.Collapsed;
            if (menu.Visibility != Visibility.Visible)
                continue;

            menu.Header = etiket switch
            {
                "F:Esit" => $"✚ Bu değere filtrele  ({kolonAd} = {kisaDeger})",
                "F:EsitDegil" => $"⊘ Bu değeri hariç tut  ({kolonAd} <> {kisaDeger})",
                "F:BuyukEsit" => $"≥ Bundan büyüklere filtrele  ({kolonAd} >= {kisaDeger})",
                _ => $"≤ Bundan küçüklere filtrele  ({kolonAd} <= {kisaDeger})",
            };
        }
    }

    private static Visibility Gorunurluk(bool acik) => acik ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Motora özgü menü öğelerinin görünürlüğünü Tag'e göre kurar (v22-S1, saha turu-2 m.5).
    /// ContextMenu kendi popup ağacında yaşadığı için içindeki "RelativeSource AncestorType=Window"
    /// bağlaması Window'u BULAMAZ; binding düşer ve Visibility varsayılanda (Visible) kalırdı —
    /// bu yüzden motora özel öğeler her motorda görünüyordu. Tag sözleşmesi: M:Mongo · M:Mssql · M:Script.
    /// </summary>
    private void MotorMenusu_Aciliyor(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu })
            return;
        foreach (object oge in menu.Items)
        {
            if (oge is MenuItem { Tag: string etiket } mi && etiket.StartsWith("M:"))
                mi.Visibility = MotorGorunurlugu(etiket, mi.Visibility);
        }
    }

    private Visibility MotorGorunurlugu(string etiket, Visibility mevcut) => etiket switch
    {
        "M:Mongo" => Gorunurluk(_vm.MotorMongoMu),
        "M:Mssql" => Gorunurluk(_vm.MotorMssqlMu),
        "M:Script" => Gorunurluk(_vm.MotorScriptDestekli),
        _ => mevcut,
    };

    private async void HizliFiltre_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string etiket } || MenuGrid(sender) is not { } grid)
            return;
        if (_vm.SeciliSekme is not SorguSekmesiViewModel sekme || SeciliHucreBilgisi(grid) is not { } hucre)
            return;
        if (_vm.AktifProfil is not { } profil)
            return;

        FiltreIliskisi iliski = etiket switch
        {
            "F:EsitDegil" => FiltreIliskisi.EsitDegil,
            "F:BuyukEsit" => FiltreIliskisi.BuyukEsit,
            "F:KucukEsit" => FiltreIliskisi.KucukEsit,
            _ => FiltreIliskisi.Esit,
        };

        (string? yeniSql, string? hata) = HizliFiltre.Uygula(
            sekme.SonCalisanSql ?? "", hucre.Kolon, iliski, hucre.Deger, _lehceSaglayici.Getir(profil.Motor));
        if (yeniSql is null)
        {
            _vm.Durum = hata ?? "Hızlı filtre uygulanamadı.";
            return;
        }

        // Editör metni de güncellenir (ne çalıştığı hep görünür); AvalonEdit'in geri alma yığını
        // sayesinde Ctrl+Z eski sorguyu geri getirir.
        sekme.Belge.Text = yeniSql;
        _vm.Durum = "Hızlı filtre uygulandı — geri almak için Ctrl+Z.";
        await sekme.CalistirAsync();
    }

    /// <summary>Sağ tıklanan/seçili hücrenin kolon adı + değeri (yoksa null).</summary>
    private static (string Kolon, object? Deger)? SeciliHucreBilgisi(DataGrid grid)
    {
        DataGridCellInfo hucre = grid.SelectedCells.Count > 0 ? grid.SelectedCells[0] : grid.CurrentCell;
        if (!hucre.IsValid || hucre.Item is not DataRowView satir || hucre.Column is null)
            return null;
        string kolon = KolonAdiCoz(hucre.Column.SortMemberPath ?? "");
        return kolon.Length > 0 && satir.Row.Table.Columns.Contains(kolon)
            ? (kolon, satir.Row[kolon])
            : null;
    }

    private static string DegerKisalt(object? deger)
    {
        string metin = deger is null or DBNull
            ? "NULL"
            : Convert.ToString(deger, System.Globalization.CultureInfo.CurrentCulture) ?? "";
        metin = metin.ReplaceLineEndings(" ").Trim();
        return metin.Length <= 28 ? metin : metin[..28] + "…";
    }

    // ---- m.26 fikir 9: shell komutu yapıştır ----

    /// <summary>
    /// v22-S3 saha turu-3 m.4 (kullanıcı: "shell yapıştır butonuna basınca ekran açılmıyor muydu?
    /// açılmıyor, bir sorun var"): ESKİDEN ekran yoktu — pano sessizce okunur, çevrilemezse yalnız
    /// durum çubuğuna yazılırdı; pano beklenen biçimde değilse (en sık hâl) ekranda HİÇBİR ŞEY
    /// olmuyordu. Artık ekran açılır: pano ön-dolu gelir, metin düzenlenebilir, çeviri (ve hata)
    /// anında görünür.
    /// </summary>
    private void MongoShellYapistir_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SeciliSekme is not SorguSekmesiViewModel sekme || _vm.AktifProfil is not { Motor: MotorTuru.Mongo })
            return;

        string? db = sekme.SecilenVeritabani;
        PencereGoster(new Views.MongoShellPenceresi(
            PanoMetni(),
            json => _vm.SekmeAc("📋 shell", json, db)) { Owner = this });
    }

    /// <summary>
    /// Panoyu GÜVENLE okur. <c>Clipboard.GetText</c> pano başka bir süreç tarafından kilitliyken
    /// COM hatası fırlatır (Windows'ta sık); burada boş metne düşülür — ekran yine açılır, kullanıcı
    /// elle yapıştırır.
    /// </summary>
    private static string PanoMetni()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return "";
        }
    }

    // ---- m.26 fikir 7: $lookup sihirbazı ----

    private async void MongoLookup_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SeciliSekme is not SorguSekmesiViewModel sekme || _vm.AktifProfil is not { Motor: MotorTuru.Mongo })
            return;

        string? db = sekme.SecilenVeritabani;
        SemaOnbellegi? onbellek = await _vm.OnbellekGetirAsync(db);
        if (onbellek is null || onbellek.Nesneler.Count == 0)
        {
            _vm.Durum = "Koleksiyon listesi okunamadı.";
            return;
        }

        PencereGoster(new Views.MongoLookupPenceresi(onbellek.Nesneler,
            (baslik, sorgu) => _vm.SekmeAc(baslik, sorgu, db),
            // v22-S1 m.13: hedef alan index'siz ise sihirbaz UYARIR + createIndex komutunu verir.
            async (koleksiyon, ct) => db is null ? [] : await _vm.MongoIndexAlanlariAsync(db, koleksiyon, ct)));
    }

    // ---- m.26 fikir 1: ObjectId izi (bu id nerede geçiyor?) ----

    /// <summary>
    /// v22-S3 saha turu-3 m.3: pencere ANINDA açılır. Koleksiyon envanteri (şema yükü) ESKİDEN
    /// burada, pencere açılmadan önce beklenirdi — büyük veritabanında ekranda hiçbir şey olmadığı
    /// için kullanıcı özelliği bozuk sanıyordu. Artık envanteri pencere kendi içinde okur.
    /// </summary>
    private void MongoIdIzi_Click(object sender, RoutedEventArgs e)
    {
        if (MenuGrid(sender) is not { } grid || SeciliHucreBilgisi(grid) is not { } hucre)
            return;
        if (_vm.SeciliSekme is not SorguSekmesiViewModel sekme || _vm.AktifProfil is not { Motor: MotorTuru.Mongo })
            return;

        string deger = Convert.ToString(hucre.Deger, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (deger.Length == 0)
        {
            _vm.Durum = "Boş hücre — aranacak değer yok.";
            return;
        }

        string? db = sekme.SecilenVeritabani;
        PencereGoster(new Views.MongoIdIziPenceresi(
            deger,
            // Envanter pencerenin İÇİNDE okunur (m.3) ve artık iptal edilebilir; null = okunamadı.
            async ct => await _vm.OnbellekGetirAsync(db, ct) is { } onbellek
                ? MongoIdIzi.Adaylar(onbellek.Nesneler, deger)
                : null,
            async (sorgu, ct) => await MongoSayimAsync(sorgu, db, ct),
            (baslik, sorgu) => _vm.SekmeAcVeCalistir(baslik, sorgu, db),
            // v22-S1 m.12 (hız): index'li aday alanlar ÖNCE taransın — ölçümde index'li sayım 2 ms,
            // index'siz aynı sorgu 493 ms (600k belge). Okunamazsa boş liste → sıra değişmez.
            async (koleksiyon, ct) => db is null
                ? []
                : await _vm.MongoIndexAlanlariAsync(db, koleksiyon, ct)));
    }

    /// <summary>Tek adayın eşleşme sayısı; hata/iptalde null — tarama tek alan yüzünden durmaz.
    /// v22-S1 (m.11): ct sorguya da GEÇİLİR — ⏹ Durdur o an koşan sayımı da keser.</summary>
    private async Task<long?> MongoSayimAsync(string sorgu, string? veritabani, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;
        (ResultSetData? set, _) = await _vm.TekSetSorguAsync(sorgu, veritabani, 1, ct);
        if (set is null || set.Satirlar.Count == 0 || set.Satirlar[0].Length == 0)
            return null; // $count eşleşme yoksa HİÇ satır döndürmez — "0" demektir
        object? hucre = set.Satirlar[0][0];
        return hucre is null or DBNull
            ? null
            : Convert.ToInt64(hucre, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- m.10 fikir 10: WHERE'de tanım değeri önerisi ----

    /// <summary>
    /// İmleç bir FK kolonuyla karşılaştırma bağlamındaysa (<c>WHERE DurumId = ▮</c>, <c>IN (…, ▮</c>)
    /// tanım tablosunun değerlerini önerir. Sözlük lookup tooltip'iyle AYNI önbellekten gelir
    /// (bir kez okunur). Bağlam yoksa/tanım tablosu değilse false döner → olağan tamamlama koşar.
    /// </summary>
    private async Task<bool> TanimDegerleriniOnerAsync(
        ICSharpCode.AvalonEdit.TextEditor editor, SorguSekmesiViewModel sekme, SemaOnbellegi onbellek)
    {
        if (_vm.AktifProfil is not { Motor: MotorTuru.Mssql } profil || onbellek.YabanciAnahtarlar.Count == 0)
            return false;

        string metin = editor.Text;
        int imlec = editor.CaretOffset;
        if (TanimDegeriBaglami.KolonCikar(metin, imlec) is not { } kolonAd)
            return false;

        // Kaynak tablo çıkarılabiliyorsa (tek tablolu sorgu) FK ARAMASI ONA DARALTILIR; JOIN'li
        // sorguda kolon adına göre aranır ve birden çok farklı hedef varsa öneri açılmaz.
        string? kaynakTablo = KayitHaritasi.TabloCikar(metin)?.Split('.')[^1];
        if (TanimDegeriBaglami.HedefBul(onbellek.YabanciAnahtarlar, kolonAd, kaynakTablo)
            is not { } hedef)
            return false;

        SemaNesnesi? hedefNesne = TabloOnizleme.TabloBul(onbellek.Nesneler, $"{hedef.Sema}.{hedef.Tablo}");
        if (hedefNesne is null)
            return false;
        LookupCozumu coz = LookupCozumleyici.Coz(hedefNesne, hedef.AnahtarKolon, [], onbellek.Nesneler);
        // Açıklama kolonu yoksa önerilecek "anlam" da yoktur (kod listesi zaten faydasız).
        string? aciklamaKolon = coz.AciklamaKolon
            ?? hedefNesne.Kolonlar.FirstOrDefault(k => !k.PkMi && k.Tip.Contains("char", StringComparison.OrdinalIgnoreCase))?.Ad;
        if (aciklamaKolon is null)
            return false;

        string sozlukAnahtari = LookupOnbellegi.SozlukAnahtari(
            sekme.SecilenVeritabani, hedef.Sema, hedef.Tablo, hedef.AnahtarKolon, aciklamaKolon);
        if (!_lookupOnbellek.Yuklendi(sozlukAnahtari))
        {
            string sql = LookupSozlugu.SorguYaz(_lehceSaglayici.Getir(profil.Motor),
                hedef.Sema, hedef.Tablo, hedef.AnahtarKolon, aciklamaKolon);
            await _lookupOnbellek.YukleAsync(sozlukAnahtari,
                () => _vm.LookupSozlukSorgusuAsync(sql, sekme.SecilenVeritabani, LookupSozlugu.Tavan + 1));
        }

        IReadOnlyList<(string Kod, string Aciklama)> degerler = _lookupOnbellek.Tumu(sozlukAnahtari);
        if (degerler.Count == 0)
            return false;

        // Kısmen yazılmış kod (ör. "= 3▮") önerinin başlangıcı sayılır ki seçim onu DEĞİŞTİRSİN.
        int kelimeBasi = imlec;
        while (kelimeBasi > 0 && (char.IsLetterOrDigit(metin[kelimeBasi - 1]) || metin[kelimeBasi - 1] == '_'))
            kelimeBasi--;

        DegerPenceresiAc(editor, degerler, kelimeBasi,
            $"{hedef.Sema}.{hedef.Tablo}.{aciklamaKolon}", metin[kelimeBasi..imlec]);
        return true;
    }

    private void DegerPenceresiAc(
        ICSharpCode.AvalonEdit.TextEditor editor, IReadOnlyList<(string Kod, string Aciklama)> degerler,
        int kelimeBasi, string kaynakNotu, string onek)
    {
        _tamamlamaPenceresi?.Close();
        var pencere = new ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow(editor.TextArea)
        {
            StartOffset = kelimeBasi,
            Width = 320,
            ToolTip = $"Tanım tablosundan: {kaynakNotu}",
        };
        pencere.CompletionList.IsFiltering = true;
        foreach ((string kod, string aciklama) in degerler.OrderBy(d => d.Kod, StringComparer.OrdinalIgnoreCase))
            pencere.CompletionList.CompletionData.Add(new Views.DegerTamamlamaVerisi(kod, aciklama));

        // AvalonEdit tamamlama penceresi ayrı Window'dur — tema fırçalarıyla açıkça boyanır.
        Brush? zemin = TryFindResource("PanelZeminFircasi") as Brush;
        Brush? metinFircasi = TryFindResource("MetinFircasi") as Brush;
        if (zemin is not null) pencere.Background = zemin;
        if (metinFircasi is not null) pencere.Foreground = metinFircasi;
        pencere.Closed += (_, _) => _tamamlamaPenceresi = null;
        _tamamlamaPenceresi = pencere;
        pencere.Show();
        if (pencere.CompletionList.ListBox is { } liste)
        {
            liste.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            if (zemin is not null) liste.Background = zemin;
            if (metinFircasi is not null) liste.Foreground = metinFircasi;
            liste.BorderThickness = new Thickness(0);
        }
        if (onek.Length > 0)
            pencere.CompletionList.SelectItem(onek);
    }

    // ---- m.10 fikir 9: editörde tablo önizleme (Ctrl+hover) ----

    private readonly ToolTip _onizlemeIpucu = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    /// <summary>Önizleme satırları kısa ömürlü önbellekte: aynı tabloya art arda bakışta sorgu tekrarlanmaz.</summary>
    private readonly Dictionary<string, (DateTime Zaman, ResultSetData Veri)> _onizlemeOnbellek = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan OnizlemeOmru = TimeSpan.FromSeconds(30);

    private async void TabloOnizlemeGoster(ICSharpCode.AvalonEdit.TextEditor editor, MouseEventArgs e)
    {
        // Ctrl şartı bilinçli: sıradan hover'da hiçbir şey açılmaz (yazarken gürültü olmasın).
        if (Keyboard.Modifiers != ModifierKeys.Control || _vm.AktifProfil is not { } profil)
            return;
        if (editor.DataContext is not SorguSekmesiViewModel sekme)
            return;
        if (editor.GetPositionFromPoint(e.GetPosition(editor)) is not { } konum)
            return;

        int ofset = editor.Document.GetOffset(konum.Location);
        ICSharpCode.AvalonEdit.Document.DocumentLine satir = editor.Document.GetLineByOffset(ofset);
        // Tüm belgeyi kopyalamak yerine YALNIZ satır alınır (uzun script'te hover ucuz kalsın).
        string? ad = TabloOnizleme.TanimlayiciCikar(
            editor.Document.GetText(satir.Offset, satir.Length), ofset - satir.Offset);
        if (ad is null || _vm.OnbellekVarsa(sekme.SecilenVeritabani) is not { } onbellek)
            return;
        if (TabloOnizleme.TabloBul(onbellek.Nesneler, ad) is not { } nesne)
            return;

        var kutu = new StackPanel { MaxWidth = 620 };
        kutu.Children.Add(new TextBlock
        {
            Text = $"📄 {nesne.TamAd} — {nesne.Kolonlar.Count} kolon",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        kutu.Children.Add(OnizlemeMetni(TabloOnizleme.KolonOzeti(nesne)));

        var veriKutusu = new TextBlock
        {
            Text = profil.Motor == MotorTuru.Mongo ? "(ilk satırlar yalnız SQL motorlarında)" : "ilk satırlar alınıyor…",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = .85,
        };
        kutu.Children.Add(veriKutusu);
        kutu.Children.Add(new TextBlock
        {
            Text = "Ctrl+hover · Esc kapatır",
            FontSize = 10.5,
            Opacity = .6,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _onizlemeIpucu.Content = kutu;
        _onizlemeIpucu.PlacementTarget = editor;
        _onizlemeIpucu.IsOpen = true;
        e.Handled = true;

        if (profil.Motor == MotorTuru.Mongo)
            return;

        string anahtar = $"{sekme.SecilenVeritabani}|{nesne.TamAd}";
        if (_onizlemeOnbellek.TryGetValue(anahtar, out (DateTime Zaman, ResultSetData Veri) kayit)
            && DateTime.UtcNow - kayit.Zaman < OnizlemeOmru)
        {
            veriKutusu.Text = TabloOnizleme.MiniTablo(kayit.Veri);
            return;
        }

        string sql = _lehceSaglayici.Getir(profil.Motor).IlkNSatirSorgusu(nesne, 5);
        (ResultSetData? veri, string? hata) = await _vm.TekSetSorguAsync(sql, sekme.SecilenVeritabani, 5);
        if (veri is null)
        {
            veriKutusu.Text = $"(ilk satırlar alınamadı: {hata})";
            return;
        }
        _onizlemeOnbellek[anahtar] = (DateTime.UtcNow, veri);
        veriKutusu.Text = TabloOnizleme.MiniTablo(veri);
    }

    private static TextBlock OnizlemeMetni(string metin) => new()
    {
        Text = metin,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 11.5,
    };

    // ---- m.10 fikir 5: kolon istatistiği ----

    private void KolonIstatistik_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string ozellikAdi })
            return;
        if (_vm.SeciliSekme is not SorguSekmesiViewModel sekme || _vm.AktifProfil is not { } profil)
            return;
        if (profil.Motor != MotorTuru.Mssql)
        {
            _vm.Durum = "Kolon istatistiği şimdilik yalnız SQL Server'da çalışır.";
            return;
        }

        string kolonAd = KolonAdiCoz(ozellikAdi);
        bool sayisal = SayisalKolonMu(sekme, kolonAd);
        (string? sql, string? not, string? hata) = KolonIstatistigi.SorguYaz(
            sekme.SonCalisanSql ?? "", kolonAd, sayisal, _lehceSaglayici.Getir(profil.Motor));
        if (sql is null)
        {
            _vm.Durum = hata ?? "İstatistik alınamadı.";
            return;
        }

        string? db = sekme.SecilenVeritabani;
        PencereGoster(new Views.KolonIstatistikPenceresi(kolonAd, async () =>
        {
            (ResultSetData? set, string? sorguHatasi) = await _vm.TekSetSorguAsync(sql, db, 1);
            return set is null
                ? (null, null, sorguHatasi)
                : (KolonIstatistigi.Bicimle(set), not, null);
        })
        { Owner = this });
    }

    /// <summary>Kolonun CLR tipi sayısal mı (AVG/SUM yalnız o zaman istenir) — sonucun kendi
    /// tablosundan okunur; tip bilinmiyorsa sayısal SAYILMAZ (yanlış AVG yerine eksik bilgi).</summary>
    private static bool SayisalKolonMu(SorguSekmesiViewModel sekme, string kolonAd)
    {
        foreach (SonucSetiGorunumu set in sekme.SonucSetleri)
        {
            if (!set.Tablo.Columns.Contains(kolonAd))
                continue;
            Type tip = set.Tablo.Columns[kolonAd]!.DataType;
            return tip == typeof(int) || tip == typeof(long) || tip == typeof(short) || tip == typeof(byte)
                || tip == typeof(decimal) || tip == typeof(double) || tip == typeof(float);
        }
        return false;
    }

    // ---- m.10 fikir 2: otomatik lookup tooltip'i (FK hücresinde "3 → Onaylandı") ----

    private readonly LookupOnbellegi _lookupOnbellek = new();

    /// <summary>
    /// FK/tanım kolonundaki hücrede tooltip açılırken çağrılır. Tanım tablosu sözlüğü ÖNBELLEKTEYSE
    /// açıklama anında yazılır (sorgu YOK); değilse "çözülüyor…" gösterilip sözlük bir kez toptan
    /// okunur ve tooltip AÇIKKEN güncellenir (ToolTip nesnesinin Content'i canlı değişir).
    /// FK olmayan/boş hücrede tooltip hiç açılmaz (e.Handled).
    /// </summary>
    private void HucreLookup_ToolTipAciliyor(object sender, ToolTipEventArgs e)
    {
        e.Handled = true; // varsayılan: tooltip YOK — yalnız çözülebilen FK hücresinde açılır
        if (sender is not TextBlock tb || _vm.AktifProfil is not { Motor: not MotorTuru.Mongo } profil)
            return;
        if (AtaBul<DataGridCell>(tb) is not { Column: { } sutun } || tb.DataContext is not DataRowView satir)
        {
            Serilog.Log.Debug("🔎 lookup ipucu: hücre/satır bağlamı yok");
            return;
        }
        if (AtaVeriBaglami<SorguSekmesiViewModel>(tb) is not { } sekme)
        {
            Serilog.Log.Debug("🔎 lookup ipucu: sorgu sekmesi bağlamı bulunamadı");
            return;
        }

        string kolonAd = KolonAdiCoz(sutun.SortMemberPath ?? "");
        if (kolonAd.Length == 0 || !satir.Row.Table.Columns.Contains(kolonAd))
        {
            Serilog.Log.Debug("🔎 lookup ipucu: kolon adı çözülemedi ({Yol})", sutun.SortMemberPath);
            return;
        }
        object? deger = satir.Row[kolonAd];
        if (deger is null or DBNull)
            return;

        if (KayitHaritasi.TabloCikar(sekme.SonCalisanSql ?? "") is not { } tabloAdi)
        {
            Serilog.Log.Debug("🔎 lookup ipucu: tek tablolu SELECT değil — kolon {Kolon}", kolonAd);
            return;
        }

        // ⚡ v22-S1 (saha turu-2 m.4 "çalışmıyor"): şema önbelleği HENÜZ YOKSA eskiden sessizce
        // vazgeçiliyordu — kullanıcı ağacı açmadan sorgu çalıştırdığında (ör. önceki oturumdan geri
        // yüklenen sekme) özellik HİÇ çalışmıyordu. Artık ipucu "çözülüyor…" ile açılır ve şema
        // arka planda YÜKLENİR (m.23'ün OnbellekGetir köprüsüyle aynı yol), sonra içerik güncellenir.
        var ipucu = LookupIpucu("🔎 çözülüyor…", $"{tabloAdi}.{kolonAd}");
        tb.ToolTip = ipucu;
        e.Handled = false;
        _ = LookupIpucuCozAsync(ipucu, tabloAdi, kolonAd, deger, profil, sekme.SecilenVeritabani);
    }

    /// <summary>
    /// İpucu içeriğini çözer: şema önbelleği (gerekirse YÜKLENİR) → FK → tanım sözlüğü. Her başarısız
    /// adımda ipucu NEDENİNİ yazar (eskiden sessizce kapanıyordu — kullanıcı "çalışmıyor" dedi; artık
    /// "FK yok", "şema yüklenemedi" gibi tek satır görünür ve aynısı loga da düşer).
    /// </summary>
    private async Task LookupIpucuCozAsync(
        ToolTip ipucu, string tabloAdi, string kolonAd, object? deger,
        ConnectionProfile profil, string? veritabani)
    {
        SemaOnbellegi? onbellek = _vm.OnbellekVarsa(veritabani) ?? await _vm.OnbellekGetirAsync(veritabani);
        if (onbellek is null)
        {
            ipucu.Content = LookupIcerik($"{deger}", "şema okunamadı — ipucu çözülemedi");
            Serilog.Log.Debug("🔎 lookup ipucu: şema önbelleği yüklenemedi ({Db})", veritabani);
            return;
        }

        // Şema+ad; eşleşmezse yalnız ad (tek eşleşme koşuluyla) — saf + testli (v22-S1 m.4)
        SemaNesnesi? kaynak = LookupCozumleyici.KaynakTabloBul(onbellek.Nesneler, tabloAdi);
        if (kaynak is null)
        {
            ipucu.Content = LookupIcerik($"{deger}", $"'{tabloAdi}' şema önbelleğinde bulunamadı");
            Serilog.Log.Debug("🔎 lookup ipucu: kaynak tablo yok ({Tablo})", tabloAdi);
            return;
        }

        LookupCozumu coz = LookupCozumleyici.Coz(kaynak, kolonAd, onbellek.YabanciAnahtarlar, onbellek.Nesneler);
        if (!coz.FkVar || coz.AciklamaKolon is null || coz.AnahtarKolon is null || coz.HedefTablo is null)
        {
            ipucu.Content = LookupIcerik($"{deger}", coz.Mesaj);
            Serilog.Log.Debug("🔎 lookup ipucu: çözülemedi — {Mesaj}", coz.Mesaj);
            return;
        }

        // Kullanıcı kararı (2026-08-10, çift tık akışında): tanım tablosu SAYILMASA da referansı
        // GÖSTER ama etiketle. Aynı kural ipucunda da geçerli — eskiden burada susuyordu.
        string kaynakNotu = $"{coz.HedefSema}.{coz.HedefTablo}.{coz.AciklamaKolon}"
            + (coz.TanimTablosu ? "" : " · tanım tablosu değil");
        string sozlukAnahtari = LookupOnbellegi.SozlukAnahtari(
            veritabani, coz.HedefSema ?? "", coz.HedefTablo, coz.AnahtarKolon, coz.AciklamaKolon);

        if (_lookupOnbellek.Yuklendi(sozlukAnahtari))
        {
            ipucu.Content = _lookupOnbellek.Bul(sozlukAnahtari, deger) is { } hazir
                ? LookupIcerik($"{deger} → {hazir}", $"{kaynakNotu} · önbellekten")
                : LookupIcerik($"{deger} → (karşılık yok)", kaynakNotu);
            return;
        }

        await LookupSozluguYukleVeGosterAsync(
            ipucu, sozlukAnahtari, coz, profil, veritabani, deger, kaynakNotu);
    }

    private async Task LookupSozluguYukleVeGosterAsync(
        ToolTip ipucu, string sozlukAnahtari, LookupCozumu coz, ConnectionProfile profil,
        string? veritabani, object? deger, string kaynakNotu)
    {
        ILehce lehce = _lehceSaglayici.Getir(profil.Motor);
        string sql = LookupSozlugu.SorguYaz(lehce, coz.HedefSema ?? "", coz.HedefTablo!,
            coz.AnahtarKolon!, coz.AciklamaKolon!);

        await _lookupOnbellek.YukleAsync(sozlukAnahtari,
            () => _vm.LookupSozlukSorgusuAsync(sql, veritabani, LookupSozlugu.Tavan + 1));

        // Tooltip hâlâ açıksa içeriği CANLI güncellenir; kapandıysa zararsız (sözlük önbellekte kaldı).
        ipucu.Content = _lookupOnbellek.Bul(sozlukAnahtari, deger) is { } aciklama
            ? LookupIcerik($"{deger} → {aciklama}", kaynakNotu)
            : LookupIcerik($"{deger} → (karşılık yok)", kaynakNotu);
    }

    private static ToolTip LookupIpucu(string baslik, string alt) => new() { Content = LookupIcerik(baslik, alt) };

    private static StackPanel LookupIcerik(string baslik, string alt)
    {
        var kutu = new StackPanel();
        kutu.Children.Add(new TextBlock { Text = baslik, FontWeight = FontWeights.SemiBold });
        kutu.Children.Add(new TextBlock
        {
            Text = alt,
            FontSize = 11,
            Opacity = .75,
            Margin = new Thickness(0, 2, 0, 0),
        });
        return kutu;
    }

    /// <summary>Görsel ağaçta ilk T atası (hücre/sütun bağlamı için).</summary>
    private static T? AtaBul<T>(DependencyObject? oge) where T : DependencyObject
    {
        while (oge is not null and not T)
            oge = VisualTreeHelper.GetParent(oge);
        return oge as T;
    }

    /// <summary>Görsel ağaçta DataContext'i T olan ilk ata (sekme VM'ine ulaşmak için).</summary>
    private static T? AtaVeriBaglami<T>(DependencyObject? oge) where T : class
    {
        while (oge is not null)
        {
            if (oge is FrameworkElement { DataContext: T hedef })
                return hedef;
            oge = VisualTreeHelper.GetParent(oge);
        }
        return null;
    }

    // ---- Kolon araçları (huni menüsü — kullanıcı isteği 2026-07-28) ----

    private void KolonHuni_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is System.Windows.Controls.Primitives.Popup popup)
            popup.IsOpen = true;
    }

    /// <summary>Huni içindeki kolon aramasına göre onay-kutusu listesini süzer.</summary>
    private void KolonAra_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (((TextBox)sender).Tag is not ItemsControl liste)
            return;
        string ara = ((TextBox)sender).Text.Trim();
        liste.Items.Filter = ara.Length == 0
            ? null
            : o => o is DataGridColumn c && (c.Header?.ToString() ?? "").Contains(ara, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bu sonuçta TÜMÜ boş (NULL/boş metin) olan kolonları gizler.</summary>
    private void BosKolonlariGizle_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not DataGrid grid
            || ((FrameworkElement)sender).DataContext is not SonucSetiGorunumu set)
            return;

        var bos = new HashSet<string>(GridKolonAraclari.BosKolonlar(set.Tablo), StringComparer.Ordinal);
        foreach (DataGridColumn k in grid.Columns)
            if (k.Header is string ad && bos.Contains(ad))
                k.Visibility = Visibility.Collapsed;
    }

    private void TumKolonlariGoster_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is DataGrid grid)
            foreach (DataGridColumn k in grid.Columns)
                k.Visibility = Visibility.Visible;
    }

    private void KolonuGizle_Click(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).Tag is DataGridColumn k)
            k.Visibility = Visibility.Collapsed;
    }

    private static string GuvenliBindingYolu(string kolonAdi)
        => kolonAdi.All(c => char.IsLetterOrDigit(c) || c == '_')
            ? kolonAdi
            : "[" + kolonAdi.Replace("^", "^^").Replace("]", "^]") + "]";

    /// <summary>
    /// v23-S8 (kullanıcı bulgusu 1 Eki 2026: "hücreye tıklayıp Ctrl+C yapınca kopyalayamıyorum,
    /// illa açmam gerekiyor"): Ctrl+C DEVRALINDI. WPF'in yerleşik kopyası görüntü binding'inden
    /// geçiyordu — uzun hücre "… (N karakter — çift tık)" KIRPIĞIYLA, çok satırlı hücre ⏎'li
    /// haliyle panoya gidiyordu; tek hücrede bile sona \r\n ekleniyordu (WHERE koşuluna
    /// yapıştırınca bozuyor). Artık: TEK hücre = ham değer, satır sonu EKLENMEDEN (SSMS gibi);
    /// ÇOKLU seçim = ham TSV (hücre <see cref="SonucBicimleyici.HucreMetni"/> — ⧉ kopyala/CSV
    /// ile aynı sözleşme; satır sırası seçimde ilk görülme, kolonlar DisplayIndex sırasında).
    /// Not: PreviewExecuted şablon içindeki CommandBinding'de TETİKLENMİYOR (ölçüldü) — Executed.
    /// </summary>
    private void Grid_Kopyala(object sender, ExecutedRoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        e.Handled = true;
        List<DataGridCellInfo> gecerli = [.. grid.SelectedCells.Where(c => c.IsValid && c.Item is DataRowView)];
        if (gecerli.Count == 0)
            return;

        string DegerMetni(DataGridCellInfo c)
        {
            string kolonAdi = c.Column.SortMemberPath is { Length: > 0 } yol
                ? KolonAdiCoz(yol)
                : c.Column.Header?.ToString() ?? "";
            DataRow satir = ((DataRowView)c.Item).Row;
            return HamDeger.Metin(satir, satir.Table.Columns[kolonAdi]!); // v23-S13: SQL tipiyle ham
        }

        string metin = gecerli.Count == 1
            ? DegerMetni(gecerli[0])
            : string.Join("\r\n", gecerli
                .GroupBy(c => c.Item) // ilk görülme sırası (sürükleme seçimi grid sırasıyla gelir)
                .Select(satir => string.Join('\t', satir
                    .OrderBy(c => c.Column.DisplayIndex)
                    .Select(DegerMetni))));
        // Pano paylaşımlı kaynaktır — başka süreç tutuyorsa CLIPBRD_E_CANT_OPEN gelir (RDP/pano
        // yöneticisi; süit koşusunda da ölçüldü). WinForms'un yerleşik yeniden denemesi gibi
        // kısa aralıkla 3 deneme; yine olmazsa durum çubuğuna dürüst mesaj.
        for (int deneme = 1; ; deneme++)
        {
            try
            {
                Clipboard.SetText(metin);
                return;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                if (deneme >= 3)
                {
                    _vm.Durum = $"Kopyalanamadı (pano meşgul): {ex.Message}";
                    return;
                }
                System.Threading.Thread.Sleep(40);
            }
        }
    }

    private void KopyalaBaslikli_Click(object sender, RoutedEventArgs e)
        => PanoyaKopyala(sender, basliklarla: true);

    private void KopyalaBassiz_Click(object sender, RoutedEventArgs e)
        => PanoyaKopyala(sender, basliklarla: false);

    /// <summary>Seçili satırları (seçim yoksa tümünü) sekmeli metin olarak panoya alır (FG-4.2).</summary>
    private static void PanoyaKopyala(object sender, bool basliklarla)
    {
        var dugme = (FrameworkElement)sender;
        if (dugme.DataContext is not SonucSetiGorunumu set)
            return;

        List<DataRow>? secili = dugme.Tag is DataGrid grid ? SeciliSatirlar(grid) : null;
        Clipboard.SetText(SonucBicimleyici.PanoMetni(set.Tablo, secili, basliklarla));
    }

    /// <summary>Hücre seçiminde de satır kopyalanabilsin: tam satır seçimi yoksa hücrelerin satırları alınır.</summary>
    private static List<DataRow>? SeciliSatirlar(DataGrid grid)
    {
        if (grid.SelectedItems.Count > 0)
            return [.. grid.SelectedItems.OfType<DataRowView>().Select(v => v.Row)];
        if (grid.SelectedCells.Count > 0)
            return [.. grid.SelectedCells.Select(c => c.Item).OfType<DataRowView>().Select(v => v.Row).Distinct()];
        return null;
    }

    // ---- Grid süper güçleri (V2-S6) ----

    /// <summary>Dev seçim/sonuçta güvenli sayılan azami hücre (v19-S15 — donma/OOM kalkanı).</summary>
    private const int SecimHucreTavani = 200_000;

    /// <summary>Seçim istatistiği (Excel durum çubuğu deseni): COUNT/SUM/AVG/MIN/MAX.</summary>
    private void Grid_SecimDegisti(object sender, SelectedCellsChangedEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.DataContext is not SonucSetiGorunumu set)
            return;

        // v19-S15: istatistik hesabı seçimin TAMAMINI gezmez — dev seçimde (Shift ile geniş
        // aralık) UI iş parçacığında saniyeler süren tur atılıyordu; tavandan sonrası kesilir.
        int seciliHucre = grid.SelectedCells.Count;
        IEnumerable<object?> degerler = grid.SelectedCells
            .Take(SecimHucreTavani)
            .Where(c => c.IsValid && c.Item is DataRowView)
            .Select(c => ((DataRowView)c.Item).Row[c.Column.SortMemberPath is { Length: > 0 } yol
                ? KolonAdiCoz(yol)
                : c.Column.Header?.ToString() ?? ""]);
        string metin = SecimIstatistikcisi.Hesapla(degerler).Metin;
        set.SecimIstatistigi = seciliHucre > SecimHucreTavani
            ? $"ilk {SecimHucreTavani:N0} hücreden: {metin}"
            : metin;
    }

    /// <summary>
    /// v19-S15 (canlı test 2026-08-04 "uygulama patladı" — MSSQL dahil): Ctrl+A / sol-üst köşe.
    /// Dev sonuçta WPF, satır×kolon kadar hücre-seçim nesnesi üretir — donma + OOM. Eşik üstünde
    /// SEÇİM HİÇ yapılmaz; istatistik TÜM sonuç üzerinden arka planda hesaplanıp gösterilir.
    /// </summary>
    private async void Grid_TumunuSec(object sender, ExecutedRoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        e.Handled = true;
        if (grid.DataContext is not SonucSetiGorunumu set || set.Tablo is not { } tablo)
            return;

        if ((long)tablo.Rows.Count * tablo.Columns.Count <= SecimHucreTavani)
        {
            grid.SelectAllCells(); // küçük sonuçta varsayılan davranış aynen
            return;
        }

        set.SecimIstatistigi = "⏳ tüm sonuç üzerinden hesaplanıyor…";
        string metin = await Task.Run(() => SecimIstatistikcisi.Hesapla(
            tablo.Rows.Cast<System.Data.DataRow>().SelectMany(r => r.ItemArray)).Metin);
        set.SecimIstatistigi = $"tüm sonuç ({tablo.Rows.Count:N0} satır) — {metin}";
    }

    /// <summary>AutoGenerate kolon yolu "[ad]" kaçışlı olabilir — DataTable kolon adına çevir.</summary>
    private static string KolonAdiCoz(string yol)
        => yol.StartsWith('[') && yol.EndsWith(']')
            ? yol[1..^1].Replace("^]", "]").Replace("^^", "^")
            : yol;

    /// <summary>Hücreye çift tık → tam içerik penceresi (FG-4.7); JSON ise girintili.</summary>
    private void Grid_HucreCiftTik(object sender, MouseButtonEventArgs e)
    {
        // Başlık/scrollbar çift tıklarını ele: yalnız hücre üzerindeyken aç
        if (e.OriginalSource is DependencyObject kaynak
            && UstEleman<DataGridCell>(kaynak) is not null)
        {
            HucreyiGoster((DataGrid)sender);
            e.Handled = true;
        }
    }

    private void HucreGoruntule_Click(object sender, RoutedEventArgs e)
    {
        if (MenuGrid(sender) is { } grid)
            HucreyiGoster(grid);
    }

    private void HucreyiGoster(DataGrid grid)
    {
        DataGridCellInfo hucre = grid.SelectedCells.Count > 0 ? grid.SelectedCells[0] : grid.CurrentCell;
        if (!hucre.IsValid || hucre.Item is not DataRowView satir || hucre.Column is null)
            return;

        string kolonAd = KolonAdiCoz(hucre.Column.SortMemberPath ?? "");
        object? deger = satir.Row[kolonAd];

        // v20-S14 madde 3: SQL motorunda + dolu hücrede "🔎 Lookup araştır" delegesi ver (kolon FK ise açıklama).
        Func<Task<string>>? lookup = null;
        if (_vm.SeciliSekme is SorguSekmesiViewModel sekme
            && _vm.AktifProfil is { Motor: not MotorTuru.Mongo }
            && deger is not null and not DBNull)
        {
            object? hucreDeger = deger;
            lookup = () => LookupArastirAsync(sekme, kolonAd, hucreDeger);
        }
        new HucrePenceresi(deger, lookup,
            satir.Row.Table.Columns[kolonAd]?.ExtendedProperties[HamDeger.TipAnahtari] as string) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// v20-S14 madde 3: tıklanan hücrenin kolonu bir FK ise referans (lookup) tablosunun AÇIKLAMASINI getirir.
    /// Kaynak tablo SonCalisanSql'den (<see cref="KayitHaritasi.TabloCikar"/>, tek-tablolu SELECT); FK/şema
    /// önbellekten; "tanım tablosu mu" sorgusuz sinyallerle (<see cref="LookupCozumleyici"/>); açıklama tek
    /// scalar sorguyla. Sonuç: "değer → açıklama  [hedef.kolon · tanım tablosu/değil]".
    /// </summary>
    private async Task<string> LookupArastirAsync(SorguSekmesiViewModel sekme, string kolonAd, object? deger)
    {
        string? tabloAdi = KayitHaritasi.TabloCikar(sekme.SonCalisanSql ?? "");
        if (tabloAdi is null)
            return "Kaynak tablo belirlenemedi — lookup yalnız tek-tablolu SELECT sonuçlarında çalışır.";
        if (_vm.AktifProfil is not { } profil)
            return "Bağlantı yok.";

        SemaOnbellegi? onbellek = await _vm.OnbellekGetirAsync(sekme.SecilenVeritabani);
        if (onbellek is null)
            return "Şema önbelleği yüklenemedi.";

        string sade = tabloAdi.Split('.')[^1];
        string? sema = tabloAdi.Contains('.') ? tabloAdi.Split('.')[^2] : null;
        SemaNesnesi? kaynak = onbellek.Nesneler.FirstOrDefault(n => n.Tur == SemaNesneTuru.Tablo
            && n.Ad.Equals(sade, StringComparison.OrdinalIgnoreCase)
            && (sema is null || n.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase)));
        if (kaynak is null)
            return $"Kaynak tablo '{tabloAdi}' şemada bulunamadı.";

        LookupCozumu coz = LookupCozumleyici.Coz(kaynak, kolonAd, onbellek.YabanciAnahtarlar, onbellek.Nesneler);
        if (!coz.FkVar || coz.AciklamaKolon is null)
            return coz.Mesaj; // FK yok ya da açıklama kolonu yok → referansı/nedeni söyle

        ILehce lehce = _lehceSaglayici.Getir(profil.Motor);
        string sql = $"SELECT {lehce.SatirSinirBasi(1)}{lehce.TirnaklaTanimlayici(coz.AciklamaKolon)} "
            + $"FROM {lehce.TamAdYaz(coz.HedefSema ?? "", coz.HedefTablo!)} "
            + $"WHERE {lehce.TirnaklaTanimlayici(coz.AnahtarKolon!)} = {SqlDeger(deger)}{lehce.SatirSinirSonu(1)}";

        string? aciklama = await _vm.RestScalarSorguAsync(sql);
        string etiket = coz.TanimTablosu ? "✓ tanım tablosu" : "tanım tablosu değil";
        return $"{deger} → {(string.IsNullOrEmpty(aciklama) ? "(eşleşen kayıt yok)" : aciklama)}"
            + $"    [{coz.HedefSema}.{coz.HedefTablo}.{coz.AciklamaKolon} · {etiket}]";
    }

    /// <summary>Hücre değerini SQL literaline çevirir: sayı olduğu gibi; diğerleri tek-tırnaklı (kaçışlı).</summary>
    private static string SqlDeger(object? deger)
    {
        if (deger is null or DBNull)
            return "NULL";
        if (deger is byte or sbyte or short or ushort or int or uint or long or ulong or decimal or double or float)
            return Convert.ToString(deger, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        string s = Convert.ToString(deger, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return "'" + s.Replace("'", "''") + "'";
    }

    private void InsertKopyala_Click(object sender, RoutedEventArgs e)
    {
        if (MenuGrid(sender) is not { DataContext: SonucSetiGorunumu set } grid)
            return;
        Clipboard.SetText(GridScriptleyici.InsertOlarak(set.Tablo, SeciliSatirlar(grid)));
        _vm.Durum = "INSERT script'i panoya kopyalandı — [Tablo] adını düzenlemeyi unutmayın.";
    }

    private void InListeKopyala_Click(object sender, RoutedEventArgs e)
    {
        if (MenuGrid(sender) is not { } grid)
            return;
        IEnumerable<object?> degerler = grid.SelectedCells
            .Where(c => c.IsValid && c.Item is DataRowView)
            .Select(c => (object?)((DataRowView)c.Item).Row[KolonAdiCoz(c.Column.SortMemberPath ?? "")]);
        Clipboard.SetText(GridScriptleyici.InListesi(degerler));
        _vm.Durum = "IN listesi panoya kopyalandı.";
    }

    // ---- Teşhis Merkezi (V2-S7): çift tık → script'i sekmeye aç (İNCELE akışı) ----

    private void TeshisEksik_CiftTik(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.DataContext is TeshisSekmesiViewModel vm && grid.SelectedItem is EksikIndexSatiri satir)
            vm.EksikScriptiAc(satir);
    }

    private void TeshisKullanilmayan_CiftTik(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.DataContext is TeshisSekmesiViewModel vm && grid.SelectedItem is KullanilmayanIndex satir)
            vm.DisableScriptiAc(satir);
    }

    /// <summary>Bağlam menüsü öğesinden sahibi DataGrid'e ulaş.</summary>
    private static DataGrid? MenuGrid(object sender)
        => ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as DataGrid;

    private static T? UstEleman<T>(DependencyObject? kaynak) where T : DependencyObject
    {
        while (kaynak is not null and not T)
            kaynak = VisualTreeHelper.GetParent(kaynak);
        return kaynak as T;
    }

    /// <summary>Result set'i tip koruyan .xlsx'e kaydeder (FG-4.4).</summary>
    private async void XlsxKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SonucSetiGorunumu set)
            return;

        var diyalog = new SaveFileDialog
        {
            Filter = "Excel dosyası (*.xlsx)|*.xlsx",
            FileName = $"sqlst-sonuc-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        try
        {
            await XlsxYazici.DosyayaYazAsync(set.Tablo, diyalog.FileName);
            _vm.Durum = $"Excel kaydedildi: {Path.GetFileName(diyalog.FileName)} ({set.Tablo.Rows.Count:N0} satır, tipler korunarak)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Iletisim.Hata(this, "SQLST", "Excel yazılamadı", ex);
        }
    }

    /// <summary>Result set'i tip koruyan JSON'a kaydeder (FG-4.4).</summary>
    private async void JsonKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SonucSetiGorunumu set)
            return;

        var diyalog = new SaveFileDialog
        {
            Filter = "JSON dosyası (*.json)|*.json",
            FileName = $"sqlst-sonuc-{DateTime.Now:yyyyMMdd-HHmmss}.json",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        try
        {
            await JsonYazici.DosyayaYazAsync(set.Tablo, diyalog.FileName);
            _vm.Durum = $"JSON kaydedildi: {Path.GetFileName(diyalog.FileName)} ({set.Tablo.Rows.Count:N0} satır)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(this, "SQLST", "JSON yazılamadı", ex);
        }
    }

    /// <summary>Result set'i CSV dosyasına kaydeder (FG-4.3; UTF-8 BOM, Excel uyumlu).</summary>
    private async void CsvKaydet_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SonucSetiGorunumu set)
            return;

        var diyalog = new SaveFileDialog
        {
            Filter = "CSV dosyası (*.csv)|*.csv",
            FileName = $"sqlst-sonuc-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        try
        {
            await CsvYazici.DosyayaYazAsync(set.Tablo, diyalog.FileName);
            _vm.Durum = $"CSV kaydedildi: {Path.GetFileName(diyalog.FileName)} ({set.Tablo.Rows.Count:N0} satır)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Iletisim.Hata(this, "SQLST", "CSV yazılamadı", ex);
        }
    }

    private CancellationTokenSource? _disaAktarCts;

    /// <summary>
    /// "⬇ Tümünü dışa aktar" (v6): son çalıştırılan sorguyu AKIŞLA tüm satırlarıyla CSV'ye
    /// yazar — 100.000'lik grid sınırına takılmadan, sabit bellekle. İlerleme durum çubuğunda.
    /// </summary>
    private async void TumunuDisaAktar_Click(object sender, RoutedEventArgs e)
    {
        if (SekmeVmBul(sender) is not SorguSekmesiViewModel sekme
            || !sekme.DisaAktarilabilir)
            return;

        var diyalog = new SaveFileDialog
        {
            Filter = "CSV dosyası (*.csv)|*.csv",
            FileName = $"sqlst-tumu-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        IptalYardimcisi.ArkaPlandaIptal(_disaAktarCts, birak: true); // m.15: Cancel UI'da bloklayabilir
        _disaAktarCts = new CancellationTokenSource();
        var ilerleme = new Progress<long>(n => _vm.Durum = $"Dışa aktarılıyor… {n:N0} satır");
        try
        {
            long yazilan = await sekme.TumunuCsvyeAktarAsync(diyalog.FileName, ilerleme, _disaAktarCts.Token);
            _vm.Durum = $"Tümü dışa aktarıldı: {Path.GetFileName(diyalog.FileName)}  ({yazilan:N0} satır)";
        }
        catch (OperationCanceledException)
        {
            _vm.Durum = "Dışa aktarma iptal edildi.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            Iletisim.Hata(this, "SQLST", "Dışa aktarılamadı", ex);
            _vm.Durum = "Dışa aktarma başarısız.";
        }
    }

    /// <summary>
    /// Bağlantı değiştir (FG-1.9) — #8 rev2 (kullanıcı, 2026-07-25 akşam): "Değiştir'e basınca bağlı
    /// ekran KAPANSIN, yenisine bağlanılsın." Akış açılıştakiyle AYNI: ana pencere gizlenir, bağlantı
    /// ekranı bağımsız açılır; bağlanınca BaglanVeAc ana pencereyi YENİ profille geri getirir.
    /// X/kapat = vazgeç ama eski bağlantıya SESSİZ DÖNÜŞ YOK (ilk #8 şikayeti "yeniden bağlanmış
    /// gibi" buydu) — açılışla tutarlı: bağlantı ekranını kapatmak uygulamadan çıkmaktır (oturum,
    /// ana pencere kapanırken zaten kaydedilir). Modal deneme (1f01ce5) yanlıştı: eski ekran arkada
    /// açık kalıyor, "kapanmıyor / yenisine bağlanmıyor" karmaşası doğuruyordu.
    /// </summary>
    private void BaglantiDegistir_Click(object sender, RoutedEventArgs e)
    {
        BaglantiPenceresi pencere = _baglantiPenceresiGetir();
        pencere.Closed += (_, _) =>
        {
            if (!pencere.BaglantiKuruldu)
                System.Windows.Application.Current.Shutdown(); // vazgeçti → çıkış (gizli ana pencereye dönülmez)
        };
        Hide();
        pencere.Show();
    }
}
