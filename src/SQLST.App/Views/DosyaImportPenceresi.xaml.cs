using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Views;

/// <summary>
/// 📥 Excel/TXT İçe Aktar penceresi (v13-S2/S3, kullanıcı akışı 2026-07-26): dosya seç →
/// (TXT'de ayraç/kodlama, Excel'de sayfa) → ÖNİZLE (ilk 200 satır + tip etiketli başlıklar) →
/// HEDEF veritabanı+tablo → eşleme gridi (aynı ad OTOMATİK, boş=aktarma, 🔑 işaretli) →
/// 📥 Aktar: özet onayı, 500'lük partiler, Durdur, sonunda kopyalanabilir rapor.
/// Okuma <see cref="DosyaOkuyucu"/>'da, yazma <see cref="DosyaAktarimServisi"/>'nde — TXT
/// değerleri seçilen kültürle tipe çevrilir. S4'te "yeni tablo (CREATE)" yolu eklenecek.
/// </summary>
public partial class DosyaImportPenceresi : Window
{
    private static readonly (string Etiket, char Karakter)[] Ayraclar =
    [
        ("Noktalı virgül ( ; )", ';'),
        ("Virgül ( , )", ','),
        ("Sekme (Tab)", '\t'),
        ("Dikey çizgi ( | )", '|'),
    ];

    private static readonly (string Etiket, char? Karakter)[] SatirAyraclari =
    [
        ("Otomatik (satır sonu)", null),
        ("Dikey çizgi ( | )", '|'),
        ("Noktalı virgül ( ; )", ';'),
        ("Yaklaşık (~)", '~'),
    ];

    private readonly ConnectionProfile _hedefProfil;
    private readonly Func<string, Task<IReadOnlyList<SemaNesnesi>>> _tablolariGetir;
    private readonly ILehceSaglayici _lehceler;
    private readonly DosyaAktarimServisi _aktarim;
    private readonly FarkOkumaServisi _farkOkuma;
    private readonly Action<string, string, string?> _sekmeAc;
    private readonly Func<SemaNesnesi, Task<DuzenlemeMetasi?>>? _metaGetir; // computed/identity bilgisi (BF-3 kod inceleme)
    private CancellationTokenSource? _cts;

    private DosyaOnizleme? _onizleme;
    private IReadOnlyList<SemaNesnesi> _hedefTablolar = [];
    private readonly Dictionary<string, string> _tipBasliklari = new(StringComparer.Ordinal);

    public System.Collections.ObjectModel.ObservableCollection<AktarimSatiri> Satirlar { get; } = [];

    public DosyaImportPenceresi(
        ConnectionProfile hedefProfil,
        IReadOnlyList<string> hedefDbler,
        string? hedefSeciliDb,
        Func<string, Task<IReadOnlyList<SemaNesnesi>>> tablolariGetir,
        ILehceSaglayici lehceler,
        DosyaAktarimServisi aktarim,
        FarkOkumaServisi farkOkuma,
        Action<string, string, string?> sekmeAc,
        Func<SemaNesnesi, Task<DuzenlemeMetasi?>>? metaGetir = null)
    {
        _hedefProfil = hedefProfil;
        _tablolariGetir = tablolariGetir;
        _lehceler = lehceler;
        _aktarim = aktarim;
        _farkOkuma = farkOkuma;
        _sekmeAc = sekmeAc;
        _metaGetir = metaGetir;

        InitializeComponent();
        EslemeGrid.ItemsSource = Satirlar;
        Tur.ItemsSource = new[] { "Excel", "CSV", "TXT" };
        Ayrac.ItemsSource = Ayraclar.Select(a => a.Etiket).ToList();
        Ayrac.SelectedIndex = 0;
        SatirAyrac.ItemsSource = SatirAyraclari.Select(a => a.Etiket).ToList();
        SatirAyrac.SelectedIndex = 0;
        Kodlama.ItemsSource = DosyaOkuyucu.Kodlamalar;
        Kodlama.SelectedIndex = 0;
        Kultur.ItemsSource = new[]
        {
            "Türkçe (1.250,75 · 24.07.2026)",
            "Uluslararası (1,250.75 · 2026-07-24)",
        };
        YazmaKipi.ItemsSource = new[]
        {
            "Yalnız ekle",
            "Ekle/Güncelle (🔑 anahtara göre)",
            "Tam eşitle (🔑 fark önizle → Güvenli Yazma)",
        };
        YazmaKipi.SelectedIndex = 0;
        HataPolitikasi.ItemsSource = new[] { "İlk hatada dur", "Hatalı satırı atla ve raporla" };
        HataPolitikasi.SelectedIndex = 0;
        HedefDb.ItemsSource = hedefDbler;
        HedefDb.SelectedItem = hedefSeciliDb ?? hedefDbler.FirstOrDefault();
        // Editable hedef: yazılan HER değişiklikte kip çözülür (yeni ad ↔ var olan tablo) —
        // SelectionChanged yetmez, Text programatik/elle değişince de tazelenmeli (v13-S4).
        HedefTablo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => EslemeyiTazele()));
    }


    private CultureInfo SeciliKultur => Kultur.SelectedIndex == 1
        ? CultureInfo.InvariantCulture : new CultureInfo("tr-TR");

    /// <summary>Ayraç combosu: listedeki etiket ya da kullanıcının yazdığı TEK karakter.</summary>
    private char? SeciliAyrac()
    {
        string metin = Ayrac.Text.Trim();
        foreach ((string etiket, char karakter) in Ayraclar)
        {
            if (metin == etiket)
                return karakter;
        }

        return metin.Length == 1 ? metin[0] : null;
    }

    /// <summary>TXT satır ayracı: "Otomatik" → null (CRLF/LF); etiket ya da yazılan tek karakter.</summary>
    private char? SeciliSatirAyrac()
    {
        string metin = SatirAyrac.Text.Trim();
        foreach ((string etiket, char? karakter) in SatirAyraclari)
        {
            if (metin == etiket)
                return karakter;
        }

        return metin.Length == 1 ? metin[0] : null;
    }

    private enum DosyaTuru { Excel, Csv, Txt }

    private DosyaTuru SeciliTur => (DosyaTuru)Math.Max(0, Tur.SelectedIndex);

    /// <summary>Tür kuralları (kullanıcı 2026-07-26): Excel=ayarsız (İLK sayfa okunur — "sayfa
    /// koymanın amacı nedir, gerek yoktu") · CSV=yalnız kodlama (ayraç OTOMATİK algılanır) ·
    /// TXT=kolon+satır ayracı+kodlama. Gözat uzantıdan ön-seçer.</summary>
    private void Tur_Degisti(object sender, RoutedEventArgs e)
    {
        if (SatirAyrac is null)
            return; // InitializeComponent sürüyor

        bool excel = SeciliTur == DosyaTuru.Excel, txt = SeciliTur == DosyaTuru.Txt;
        AyracEtiketi.Visibility = Ayrac.Visibility = txt ? Visibility.Visible : Visibility.Collapsed;
        SatirAyracEtiketi.Visibility = SatirAyrac.Visibility = txt ? Visibility.Visible : Visibility.Collapsed;
        KodlamaEtiketi.Visibility = Kodlama.Visibility = excel ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Gozat_Click(object sender, RoutedEventArgs e)
    {
        var diyalog = new OpenFileDialog
        {
            Filter = "Excel ve metin dosyaları|*.xlsx;*.xlsm;*.xls;*.txt;*.csv"
                   + "|Excel (*.xlsx;*.xls)|*.xlsx;*.xlsm;*.xls"
                   + "|Metin (*.txt;*.csv)|*.txt;*.csv|Tümü (*.*)|*.*",
        };
        if (diyalog.ShowDialog(this) != true)
            return;

        DosyaSec(diyalog.FileName);
    }

    /// <summary>Diyalog-sonrası dosya seçimi (ayrı metot: STA testi diyalogsuz çağırır).
    /// Tür uzantıdan ÖN-SEÇİLİR (kullanıcı değiştirebilir); ayar görünürlüğünü Tur_Degisti kurar.</summary>
    internal void DosyaSec(string yol)
    {
        DosyaYolu.Text = yol;
        OnizleDugmesi.IsEnabled = true;

        string uzanti = Path.GetExtension(yol).ToLowerInvariant();
        Tur.SelectedIndex = uzanti is ".xlsx" or ".xlsm" or ".xls" ? 0 : uzanti == ".csv" ? 1 : 2;
        Durum.Text = $"{Path.GetFileName(yol)} seçildi — Önizle'ye basın.";
    }

    /// <summary>Tür kurallarına göre okuma ayarları: CSV'de kolon ayracı OTOMATİK algılanır ve
    /// aktarım da AYNI ayracı kullansın diye saklanır; TXT'de kullanıcı seçimi geçerlidir.</summary>
    private (bool Excel, char Ayrac, char? SatirAyraci, string? Sayfa, bool Baslik, string? Kodlama)?
        OkumaAyarlari()
    {
        string? kodlamaAdi = Kodlama.SelectedItem as string;
        bool baslik = IlkSatirBaslik.IsChecked == true;
        switch (SeciliTur)
        {
            case DosyaTuru.Excel:
                return (true, ';', null, null, baslik, null); // sayfa hep İLK — ek ayar yok
            case DosyaTuru.Csv:
                _algilananAyrac ??= DosyaOkuyucu.AyracAlgila(
                    DosyaYolu.Text, DosyaOkuyucu.KodlamaCoz(kodlamaAdi));
                return (false, _algilananAyrac.Value, null, null, baslik, kodlamaAdi);
            default:
                if (SeciliAyrac() is not { } ayrac)
                {
                    Durum.Text = "Kolon ayracı tek karakter olmalı (listeden seçin ya da tek karakter yazın).";
                    return null;
                }
                if (SatirAyrac.Text.Trim().Length > 1 && SeciliSatirAyrac() is null
                    && SatirAyraclari.All(a => a.Etiket != SatirAyrac.Text.Trim()))
                {
                    Durum.Text = "Satır ayracı tek karakter olmalı (ya da 'Otomatik' seçin).";
                    return null;
                }
                return (false, ayrac, SeciliSatirAyrac(), null, baslik, kodlamaAdi);
        }
    }

    private char? _algilananAyrac; // CSV: önizlemede algılanan ayraç — aktarım aynı ayracı kullanır

    private async void Onizle_Click(object sender, RoutedEventArgs e)
    {
        string yol = DosyaYolu.Text;
        if (yol.Length == 0)
            return;

        _algilananAyrac = null; // dosya/ayar değişmiş olabilir — yeniden algıla
        if (OkumaAyarlari() is not { } a)
            return;
        CultureInfo kultur = SeciliKultur;

        OnizleDugmesi.IsEnabled = false;
        Durum.Text = "Okunuyor…";
        try
        {
            // Dosya IO arka planda — büyük dosyada pencere donmasın.
            _onizleme = await Task.Run(() => a.Excel
                ? DosyaOkuyucu.ExcelOnizle(yol, a.Sayfa, a.Baslik, kultur)
                : DosyaOkuyucu.MetinOnizle(yol, a.Ayrac, DosyaOkuyucu.KodlamaCoz(a.Kodlama),
                    a.Baslik, kultur, a.SatirAyraci));

            GridiKur(_onizleme);
            Durum.Text = _onizleme.Kolonlar.Count == 0
                ? "Dosya boş görünüyor."
                : $"{_onizleme.Kolonlar.Count} kolon · {_onizleme.Satirlar.Count} satır"
                    // "Aktarımda tümü okunur" güvencesi (kullanıcı sorusu 2026-07-31): 200 yalnız
                    // ÖNİZLEME sınırıdır; Aktar dosyayı baştan AKIŞLA (sınırsız) okur.
                    + (_onizleme.Kesildi ? $" (ilk {DosyaOkuyucu.EnCokOnizlemeSatiri} önizlendi — AKTARIMDA TÜM DOSYA okunur)" : "")
                    + (SeciliTur == DosyaTuru.Csv ? $" · ayraç '{AyracGoster(a.Ayrac)}' algılandı" : "")
                    + ". Kolon tipleri başlıkta — hedef tabloyu seçin.";
            EslemeyiTazele(); // hedef tablo zaten seçiliyse eşleme yeni kolonlarla kurulur
        }
        catch (Exception ex)
        {
            // ExcelDataReader'ın "Invalid file signature"ı kullanıcıya bir şey söylemiyor —
            // en olası neden tür/dosya uyumsuzluğudur, Türkçe ve yol gösterir biçimde de.
            Durum.Text = ex.Message.Contains("file signature", StringComparison.OrdinalIgnoreCase)
                ? "Bu dosya Excel biçiminde değil — Tür'ü CSV ya da TXT yapıp yeniden önizleyin."
                : $"Okunamadı: {ex.Message}";
        }
        finally
        {
            OnizleDugmesi.IsEnabled = true;
        }
    }

    private static string AyracGoster(char c) => c == '\t' ? "Tab" : c.ToString();

    /// <summary>Önizlemeyi DataGrid'e bağlar: DataTable (adlar normalize) + başlıkta tip etiketi.</summary>
    private void GridiKur(DosyaOnizleme onizleme)
    {
        _tipBasliklari.Clear();
        var tablo = new DataTable();
        foreach (DosyaKolonu kolon in onizleme.Kolonlar)
        {
            tablo.Columns.Add(kolon.Ad, typeof(object));
            _tipBasliklari[kolon.Ad] = $"{kolon.Ad}\n{TipEtiketi(kolon)}";
        }

        tablo.BeginLoadData();
        foreach (object?[] satir in onizleme.Satirlar)
        {
            object?[] dolu = new object?[tablo.Columns.Count];
            Array.Copy(satir, dolu, Math.Min(satir.Length, dolu.Length)); // kısa satır → kalanlar null
            tablo.Rows.Add(dolu);
        }

        tablo.EndLoadData();
        OnizlemeGrid.ItemsSource = tablo.DefaultView;
    }

    private static string TipEtiketi(DosyaKolonu k) => k.Tip switch
    {
        DosyaTipi.TamSayi => "tam sayı",
        DosyaTipi.Ondalik => "ondalık",
        DosyaTipi.Tarih => "tarih",
        DosyaTipi.Bool => "true/false",
        _ => k.EnUzunMetin > 0 ? $"metin({k.EnUzunMetin})" : "metin",
    };

    private void OnizlemeGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        // Başlık iki satır: kolon adı + tahmin edilen tip (bağlama adı değişmez — S3 eşlemesi ada dayanır).
        if (_tipBasliklari.TryGetValue(e.PropertyName, out string? baslik))
            e.Column.Header = baslik;
    }

    // ---- HEDEF + eşleme + aktarım (v13-S3) ----

    private async void HedefDb_Secildi(object sender, RoutedEventArgs e)
    {
        if (HedefTablo is null)
            return; // InitializeComponent sürerken ctor'daki ilk seçim tetiklenebilir
        try
        {
            if (HedefDb.SelectedItem is not string db)
                return;
            _hedefTablolar = (await _tablolariGetir(db))
                .Where(t => t.Tur == SemaNesneTuru.Tablo).ToList();
            HedefTablo.ItemsSource = _hedefTablolar.Select(t => t.TamAd).ToList();
        }
        catch (Exception ex) { Durum.Text = $"Hedef tablolar yüklenemedi: {ex.Message}"; }
    }

    /// <summary>Seçili VAR OLAN hedef tablo; yazılan ad listede yoksa null (yeni tablo kipi).</summary>
    private SemaNesnesi? SeciliHedef()
        => _hedefTablolar.FirstOrDefault(t => t.TamAd.Equals(
            HedefTablo.Text.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Önizleme ya da hedef tablo değişince eşleme yeniden kurulur (aynı ad otomatik).
    /// YENİ TABLO kipinde (v13-S4: yazılan ad listede yok) satırlar DOSYA kolonlarından kurulur —
    /// hedef tip önerilen SQL tipidir, boşaltılan kolon CREATE'e girmez.</summary>
    private void EslemeyiTazele()
    {
        Satirlar.Clear();
        SemaNesnesi? hedef = SeciliHedef();
        string yeniAd = HedefTablo.Text.Trim();
        AktarDugmesi.IsEnabled = _onizleme is not null && (hedef is not null || yeniAd.Length > 0);
        if (_onizleme is null)
            return;

        if (hedef is null && yeniAd.Length > 0)
        {
            string motorId = _lehceler.Getir(_hedefProfil.Motor).MotorId;
            List<string> dosyaAdaylar = ["", .. _onizleme.Kolonlar.Select(k => k.Ad)];
            foreach (DosyaKolonu k in _onizleme.Kolonlar)
            {
                Satirlar.Add(new AktarimSatiri
                {
                    HedefKolon = k.Ad,
                    HedefTip = DosyaTipiEslemesi.SqlTipi(motorId, k),
                    AnahtarMi = false,
                    KaynakAdaylar = dosyaAdaylar,
                    KaynakKolon = k.Ad,
                });
            }

            Durum.Text = $"YENİ tablo: '{yeniAd}' — {Satirlar.Count} kolon önerilen tiplerle "
                + "oluşturulacak (boşalttığınız kolon CREATE'e girmez). Aktar'da script ONAYINIZA sunulur.";
            return;
        }

        if (hedef is null)
            return;

        // Dosya kolonları eşleştirici için SemaKolonu kılığına girer (ad bazlı otomatik eşleşme).
        IReadOnlyList<SemaKolonu> kaynakKolonlar =
            [.. _onizleme.Kolonlar.Select(k => new SemaKolonu(k.Ad, TipEtiketi(k), k.BosVar, false))];
        var oneri = AktarimEslestirici.OtomatikEsle(kaynakKolonlar, hedef.Kolonlar)
            .ToDictionary(o => o.HedefKolon, o => o.KaynakKolon, StringComparer.OrdinalIgnoreCase);
        List<string> adaylar = ["", .. _onizleme.Kolonlar.Select(k => k.Ad)];

        foreach (SemaKolonu h in hedef.Kolonlar)
        {
            Satirlar.Add(new AktarimSatiri
            {
                HedefKolon = h.Ad,
                HedefTip = h.Tip,
                AnahtarMi = h.PkMi,
                KaynakAdaylar = adaylar,
                KaynakKolon = oneri.GetValueOrDefault(h.Ad, ""),
            });
        }

        int esli = Satirlar.Count(s => s.KaynakKolon.Length > 0);
        Durum.Text = $"{esli} kolon otomatik eşlendi, {Satirlar.Count - esli} boş (aktarılmayacak). "
            + "Gerekirse düzeltin, sonra Aktar.";
    }

    private async void Aktar_Click(object sender, RoutedEventArgs e)
    {
        // Tam eşitle (BF-3): akış-INSERT yerine fark önizleme + Güvenli Yazma script'i — ayrı yol.
        if (YazmaKipi.SelectedIndex == 2)
        {
            await TamEsitleAsync();
            return;
        }

        SemaNesnesi? hedef = SeciliHedef();
        bool yeniTablo = hedef is null && HedefTablo.Text.Trim().Length > 0;
        if (_onizleme is null || (hedef is null && !yeniTablo) || HedefDb.SelectedItem is not string hedefDb)
        {
            Durum.Text = "Önce dosyayı önizleyin ve hedef tabloyu seçin (ya da yeni ad yazın).";
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

        // YENİ TABLO yolu (v13-S4): DDL üret → onay penceresi → OnceDdl ile aktar.
        // Onay penceresi "Oluştur ve aktar" dediği için ayrıca MessageBox açılmaz.
        string? onceDdl = null;
        string hedefTabloAdi = hedef?.TamAd ?? "";
        if (yeniTablo)
        {
            if (YazmaKipi.SelectedIndex == 1)
            {
                Durum.Text = "Yeni tabloda birincil anahtar üretilmez — Ekle/Güncelle yerine 'Yalnız ekle' kullanın.";
                return;
            }

            ILehce lehce = _lehceler.Getir(_hedefProfil.Motor);
            string yazilan = HedefTablo.Text.Trim();
            int nokta = yazilan.IndexOf('.');
            (string? sema, string ad) = nokta > 0
                ? (yazilan[..nokta], yazilan[(nokta + 1)..])
                : (lehce.MotorId == "mssql" ? "dbo" : null, yazilan);

            IReadOnlyList<Application.YeniKolon> kolonlar = [.. Satirlar
                .Where(s => s.KaynakKolon.Length > 0)
                .Select(s => new Application.YeniKolon(s.HedefKolon, s.HedefTip, NullOlabilir: true))];
            (string? ddl, string? ddlHata) = Application.TabloOlusturucu.Uret(
                new Application.YeniTablo(sema, ad, kolonlar), lehce);
            if (ddl is null)
            {
                Durum.Text = $"Tablo scripti üretilemedi: {ddlHata}";
                return;
            }

            if (new DdlOnayPenceresi(ddl) { Owner = this }.ShowDialog() != true)
                return;

            onceDdl = ddl;
            // INSERT'in hedef adı tırnaklı tam ad — Türkçe/boşluklu adlar da güvenli.
            hedefTabloAdi = sema is null
                ? lehce.TirnaklaTanimlayici(ad) : lehce.TamAdYaz(sema, ad);
        }

        // Ekle/Güncelle doğrulaması (v12-S4 kuralları) — MessageBox'tan ÖNCE.
        bool ekleGuncelle = YazmaKipi.SelectedIndex == 1;
        IReadOnlyList<string> anahtarlar = ekleGuncelle
            ? [.. (hedef?.Kolonlar ?? []).Where(k => k.PkMi).Select(k => k.Ad)] : [];
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

        string yol = DosyaYolu.Text;
        if (onceDdl is null)
        {
            // Var olan tabloya yazarken özet onayı; yeni tablo yolunda DDL onayı bu görevi gördü.
            string ozet = $"Dosya: {Path.GetFileName(yol)}"
                + (_onizleme.Kesildi ? $" (önizlemede ilk {DosyaOkuyucu.EnCokOnizlemeSatiri} satır — tamamı aktarılır)" : "") + "\n"
                + $"Hedef: {_hedefProfil.Ad} · {hedefDb} · {hedefTabloAdi}\n"
                + $"{eslesmeler.Count} kolon · yazma: "
                + (ekleGuncelle ? $"Ekle/Güncelle (anahtar: {string.Join(", ", anahtarlar)})" : "Yalnız ekle") + "."
                + (OnceTemizle.IsChecked == true ? "\n\n⚠ ÖNCE HEDEF TABLO TEMİZLENECEK (DELETE)!" : "");
            bool temizle = OnceTemizle.IsChecked == true;
            if (!Iletisim.Sor(this, "SQLST — İçe Aktar", "İçe aktarma başlatılsın mı?", ozet,
                    temizle ? "Temizle ve aktar" : "▶ Aktarımı başlat", temizle ? IletisimTuru.Tehlike : IletisimTuru.Soru))
                return;
        }

        var istek = new DosyaAktarimIstegi(
            _hedefProfil, hedefDb, hedefTabloAdi, eslesmeler, _onizleme.Kolonlar,
            SeciliKultur.Name.Length == 0 ? null : SeciliKultur.Name,
            OnceTemizle.IsChecked == true,
            HataPolitikasi.SelectedIndex == 1
                ? AktarimHataPolitikasi.AtlaVeRaporla : AktarimHataPolitikasi.IlkHatadaDur,
            ekleGuncelle ? AktarimYazmaKipi.EkleGuncelle : AktarimYazmaKipi.YalnizEkle,
            anahtarlar, onceDdl);

        // Önizlemeyle AYNI okuma ayarları (CSV'de algılanan ayraç dahil) — tutarlılık şart.
        if (OkumaAyarlari() is not { } a)
            return;

        AktarDugmesi.IsEnabled = false;
        DurdurDugmesi.IsEnabled = true;
        Rapor.Visibility = Visibility.Collapsed;
        Ilerleme.Visibility = Visibility.Visible;
        Ilerleme.IsIndeterminate = true; // toplam bilinmiyor — dosya akışla okunur
        _cts = new CancellationTokenSource();
        try
        {
            var ilerleme = new Progress<AktarimIlerleme>(p =>
                Durum.Text = $"Aktarılıyor… okunan {p.Okunan:N0} · eklenen {p.Yazilan:N0}"
                    + (p.Guncellenen > 0 ? $" · güncellenen {p.Guncellenen:N0}" : "")
                    + (p.Atlanan > 0 ? $" · atlanan {p.Atlanan:N0}" : ""));
            // Task.Run: dosya akışının senkron IO'su UI thread'ine binmesin.
            AktarimSonucu sonuc = await Task.Run(() => _aktarim.AktarAsync(istek,
                a.Excel
                    ? DosyaOkuyucu.ExcelAkis(yol, a.Sayfa, a.Baslik)
                    : DosyaOkuyucu.MetinAkis(yol, a.Ayrac, DosyaOkuyucu.KodlamaCoz(a.Kodlama),
                        a.Baslik, a.SatirAyraci),
                ilerleme, _cts.Token));

            Durum.Text = sonuc switch
            {
                { Basarili: true } => $"✔ Tamamlandı ({sonuc.Sure.TotalSeconds:F1} sn) — rapor aşağıda.",
                { IptalEdildi: true } => "■ Durduruldu — o ana dek tamamlananlar kaldı, rapor aşağıda.",
                _ => $"⚠ {sonuc.Hata}",
            };
            Rapor.Text = AktarimPenceresi.RaporMetni(sonuc);
            Rapor.Visibility = Visibility.Visible;
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

    private void Durdur_Click(object sender, RoutedEventArgs e)
        => IptalYardimcisi.ArkaPlandaIptal(_cts); // m.15: Cancel UI'da bloklayabilir — havuzda

    // ---- Tam eşitle (BF-3, 2026-07-27) ----

    /// <summary>
    /// BF-3 tam eşitleme: dosyayı ve HEDEF tabloyu okur, farkı hesaplar (YENİ/DEĞİŞEN/SİLİNMİŞ),
    /// önizleme penceresini açar. Bu pencere HİÇBİR yazma yapmaz — üretilen INSERT/UPDATE/DELETE
    /// script'i Güvenli Yazma sekmesine düşer; kullanıcı orada çalıştırıp satır sayısını görerek
    /// COMMIT/ROLLBACK verir. Anahtar = hedef PK (upsert kipiyle aynı kural).
    /// </summary>
    private async Task TamEsitleAsync()
    {
        SemaNesnesi? hedef = SeciliHedef();
        if (_onizleme is null || hedef is null || HedefDb.SelectedItem is not string db)
        {
            Durum.Text = "Tam eşitleme için önce dosyayı önizleyin ve VAR OLAN bir hedef tablo seçin.";
            return;
        }

        IReadOnlyList<AktarimEslesmesi> eslesmeler = [.. Satirlar
            .Where(s => s.KaynakKolon.Length > 0)
            .Select(s => new AktarimEslesmesi(s.KaynakKolon, s.HedefKolon))];
        IReadOnlyList<string> anahtarlar = [.. hedef.Kolonlar.Where(k => k.PkMi).Select(k => k.Ad)];
        if (anahtarlar.Count == 0)
        {
            Durum.Text = "Tam eşitleme için hedef tabloda birincil anahtar (🔑) olmalı — bu tabloda yok.";
            return;
        }

        var esliHedefler = new HashSet<string>(eslesmeler.Select(m => m.HedefKolon), StringComparer.OrdinalIgnoreCase);
        if (!anahtarlar.All(esliHedefler.Contains))
        {
            Durum.Text = "Tam eşitleme için TÜM anahtar kolonlar eşlenmeli: " + string.Join(", ", anahtarlar) + ".";
            return;
        }

        // Sunucunun yönettiği kolonlar (computed/rowversion) fark/DML DIŞINDA tutulur (kod inceleme
        // 2026-07-27): Excel'le kıyaslanırsa sahte "değişti", INSERT/UPDATE'e girerse "güncellenemez" hatası.
        DuzenlemeMetasi? meta = _metaGetir is null ? null : await _metaGetir(hedef);
        var yazilamaz = new HashSet<string>(
            meta?.Kolonlar.Where(k => k.ComputedMi || k.RowversionMi).Select(k => k.Ad) ?? [],
            StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> hedefKolonlar = [.. eslesmeler.Select(m => m.HedefKolon).Where(k => !yazilamaz.Contains(k))];
        var anahtarSeti = new HashSet<string>(anahtarlar, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> karsilastirilan = [.. hedefKolonlar.Where(k => !anahtarSeti.Contains(k))];
        if (karsilastirilan.Count == 0)
        {
            Durum.Text = "Tam eşitleme için anahtar dışında en az bir kolon eşlenmeli (karşılaştırılacak alan).";
            return;
        }

        if (OkumaAyarlari() is not { } a)
            return;

        string yol = DosyaYolu.Text;
        CultureInfo kultur = SeciliKultur;
        ILehce lehce = _lehceler.Getir(_hedefProfil.Motor);

        // Kaynak kolon adı → dosya sütun indeksi (akış satırları bu sırada gelir) + tip tahmini.
        var kaynakIndeks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _onizleme.Kolonlar.Count; i++)
            kaynakIndeks[_onizleme.Kolonlar[i].Ad] = i;
        var kaynakTip = _onizleme.Kolonlar.ToDictionary(k => k.Ad, k => k.Tip, StringComparer.OrdinalIgnoreCase);

        AktarDugmesi.IsEnabled = false;
        Ilerleme.Visibility = Visibility.Visible;
        Ilerleme.IsIndeterminate = true;
        Durum.Text = "Dosya ve tablo okunuyor, fark hesaplanıyor…";
        try
        {
            // Dosyayı TAM oku → hedef kolon adıyla anahtarlı sözlük (TXT string → tipli çevrilir,
            // Excel zaten tipli); IO arka planda, pencere donmasın.
            List<IReadOnlyDictionary<string, object?>> excel = await Task.Run(() =>
            {
                var liste = new List<IReadOnlyDictionary<string, object?>>();
                IEnumerable<object?[]> akis = a.Excel
                    ? DosyaOkuyucu.ExcelAkis(yol, a.Sayfa, a.Baslik)
                    : DosyaOkuyucu.MetinAkis(yol, a.Ayrac, DosyaOkuyucu.KodlamaCoz(a.Kodlama), a.Baslik, a.SatirAyraci);
                foreach (object?[] satir in akis)
                {
                    var d = new Dictionary<string, object?>(hedefKolonlar.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (AktarimEslesmesi m in eslesmeler)
                    {
                        if (yazilamaz.Contains(m.HedefKolon))
                            continue; // computed/rowversion → diff'e ve INSERT'e girmez
                        object? ham = kaynakIndeks.TryGetValue(m.KaynakKolon, out int ix) && ix < satir.Length
                            ? satir[ix] : null;
                        d[m.HedefKolon] = HucreyiCevir(ham, kaynakTip.GetValueOrDefault(m.KaynakKolon, DosyaTipi.Metin), kultur);
                    }

                    liste.Add(d);
                }

                return liste;
            });

            // Excel'de yinelenen anahtar → çift INSERT (PK ihlali) / çift UPDATE; önceden uyar (kod inceleme).
            int cift = Application.ExcelFarkKarsilastirici.YinelenenAnahtarSayisi(excel, anahtarlar);
            if (cift > 0 && !Iletisim.Sor(this, "SQLST — Yinelenen anahtar",
                    $"Excel'de {cift} satır yinelenen anahtara sahip",
                    $"Aynı {string.Join("+", anahtarlar)} birden çok satırda. Tam eşitlemede bu, yeni satırlarda çift "
                    + "INSERT (anahtar ihlali → tüm işlem geri alınır) ya da aynı satıra çift UPDATE demektir.",
                    "Yine de devam et", IletisimTuru.Tehlike))
            {
                Durum.Text = "Tam eşitleme iptal — Excel'de yinelenen anahtarlar var.";
                return;
            }

            IReadOnlyList<IReadOnlyDictionary<string, object?>> tabloSatirlar =
                await _farkOkuma.TabloyuOkuAsync(_hedefProfil, db, hedef.Sema, hedef.Ad, hedefKolonlar, CancellationToken.None);

            Application.FarkEsitleyici.EsitlemeSonucu sonuc = Application.FarkEsitleyici.Hesapla(
                lehce, hedef.Sema, hedef.Ad, excel, tabloSatirlar, anahtarlar, karsilastirilan, silmeDahil: false);

            Durum.Text = $"Fark: {sonuc.YeniSayisi} yeni · {sonuc.DegisenSayisi} değişen · {sonuc.SilinenSayisi} yalnız tabloda. "
                + "Önizleme penceresinde inceleyin.";

            new FarkOnizlemePenceresi(sonuc.Fark, lehce, hedef.Sema, hedef.Ad, anahtarlar,
                sql => _sekmeAc($"Eşitle: {hedef.Ad}", sql, db), meta) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            Durum.Text = $"Tam eşitleme hazırlanamadı: {ex.Message}";
        }
        finally
        {
            AktarDugmesi.IsEnabled = true;
            Ilerleme.Visibility = Visibility.Collapsed;
            Ilerleme.IsIndeterminate = false;
        }
    }

    /// <summary>
    /// TXT/CSV string değerini kültürle tipe çevirir (Excel zaten tipli → olduğu gibi geçer); boş → null.
    /// Çevrilemezse ham metni bırakır — fark ekranında "değişti" görünür, sessiz yanlış değil.
    /// <see cref="DosyaAktarimServisi"/>.Donustur ile AYNI kural; olgun ithal yazma yolunu bozmamak
    /// için (kullanıcı hassasiyeti) ayrı tutuldu.
    /// </summary>
    private static object? HucreyiCevir(object? ham, DosyaTipi tip, CultureInfo kultur)
    {
        if (ham is null or "")
            return null;
        if (ham is not string s)
            return ham; // Excel — hücre tipiyle geldi

        s = s.Trim();
        try
        {
            return tip switch
            {
                DosyaTipi.TamSayi => long.Parse(s, NumberStyles.Integer, kultur),
                DosyaTipi.Ondalik => decimal.Parse(s, NumberStyles.Number, kultur),
                DosyaTipi.Tarih => DateTime.Parse(s, kultur, DateTimeStyles.None),
                DosyaTipi.Bool => bool.Parse(s),
                _ => s,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return s; // ham bırak — görünür fark
        }
    }
}
