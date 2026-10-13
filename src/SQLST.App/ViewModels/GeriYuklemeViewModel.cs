using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Yedek seti satırı (HEADERONLY).</summary>
public sealed record YedekSetiGorunum(int Konum, string Tur, string Veritabani, string Bitis, string Boyut, string Surum, string Not, YedekSeti Set)
{
    public override string ToString() => $"{Konum} · {Tur} · {Bitis}";
}

/// <summary>MOVE satırı — hedef düzenlenebilir.</summary>
public sealed partial class DosyaTasimaGorunum : ObservableObject
{
    public required string Mantiksal { get; init; }
    public required string Tur { get; init; }
    public required string Boyut { get; init; }
    public required string Orijinal { get; init; }
    [ObservableProperty] private string _hedef = "";
}

/// <summary>Tarihçe satırı (msdb, tüm DB'ler) — tıklayınca dosya + set seçilir.</summary>
public sealed record TarihceGorunum(string Veritabani, string Tarih, string Tur, string Boyut, string Dosya, int Konum);

/// <summary>
/// ⏪ Geri yükle penceresi (v23-S26 S2 — K1–K9): kaynak dosya (sunucu gezgini / msdb tarihçesi) → 🔎 Oku (HEADERONLY)
/// → set seçimi → FILELISTONLY → MOVE tablosu → hedef ad/seçenekler → canlı script; ⏪ Geri yükle Tehlike onayıyla ürün
/// içinde koşar (ilerleme DMV'den, iptal ikinci onay — K9). Zincir (tam → fark → günlük) SAF çekirdekten.
/// </summary>
public sealed partial class GeriYuklemeViewModel : ObservableObject
{
    private readonly SunucuKomutu _komut;
    private readonly Action<string, string> _scriptAc;
    private readonly Func<string, string, string, bool> _onay; // (başlık, mesaj, onay düğmesi) → Tehlike kutusu
    private readonly string _sunucuAdi;
    private readonly string _kaynakDb;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<YedekSeti> _setler = [];
    private IReadOnlyList<YedekSeti> _zincir = [];
    private bool _kuruluyor;

    public string PencereBasligi => $"⏪ Geri yükle — {_kaynakDb}";
    public int IlerlemeAraligiMs { get; set; } = 1000;

    [ObservableProperty] private string _dosya = "";
    public ObservableCollection<YedekSetiGorunum> Setler { get; } = [];
    [ObservableProperty] private YedekSetiGorunum? _seciliSet;
    public ObservableCollection<DosyaTasimaGorunum> Dosyalar { get; } = [];
    public ObservableCollection<TarihceGorunum> Tarihce { get; } = [];
    [ObservableProperty] private TarihceGorunum? _seciliTarihce;

    [ObservableProperty] private string _hedefDb;
    [ObservableProperty] private bool _replace;
    [ObservableProperty] private bool _baglantilariKes;
    [ObservableProperty] private bool _noRecovery;

    [ObservableProperty] private GeriYuklemeBilgisi? _bilgi;
    [ObservableProperty] private string _sunucuOzeti = "";
    [ObservableProperty] private string _hedefNotu = "";
    [ObservableProperty] private string _zincirNotu = "";
    [ObservableProperty] private string _setNotu = "";
    [ObservableProperty] private string _script = "";
    [ObservableProperty] private string _engel = "";
    [ObservableProperty] private string _yetkiNotu = "";
    [ObservableProperty] private bool _okunuyor;
    [ObservableProperty] private bool _calisiyor;
    [ObservableProperty] private bool _bitti;
    [ObservableProperty] private bool _basarili;
    [ObservableProperty] private double _yuzde;
    [ObservableProperty] private string _ilerlemeMetni = "";
    [ObservableProperty] private string _durum = "";
    [ObservableProperty] private string _log = "";

    public bool HedefMevcut => Bilgi is not null && Bilgi.VeritabaniVar(HedefDb.Trim());
    public bool GeriYuklenebilirMi => !Calisiyor && !Okunuyor && Bilgi is not null && _zincir.Count > 0 && Engel.Length == 0;
    public bool DogrulanabilirMi => !Calisiyor && !Okunuyor && SeciliSet is not null;
    public bool IlerlemeGorunur => Calisiyor || Bitti;

    public GeriYuklemeViewModel(string kaynakDb, string sunucuAdi, SunucuKomutu komut, Action<string, string> scriptAc,
        Func<string, string, string, bool> onay)
    {
        _kaynakDb = kaynakDb;
        _sunucuAdi = sunucuAdi;
        _komut = komut;
        _scriptAc = scriptAc;
        _onay = onay;
        _hedefDb = kaynakDb;
        ScriptiYenile();
    }

    public GeriYuklemeAyari Ayar => new(Dosya.Trim(), [.. _zincir.Select(s => s.Konum)], HedefDb.Trim(),
        [.. Dosyalar.Select(d => new DosyaTasima(d.Mantiksal, d.Hedef.Trim()))], Replace, BaglantilariKes, NoRecovery, HedefMevcut);

    partial void OnHedefDbChanged(string value)
    {
        OnPropertyChanged(nameof(HedefMevcut));
        HedefYollariOner();
        HedefNotunuKur();
        ScriptiYenile();
    }

    partial void OnReplaceChanged(bool value) => ScriptiYenile();
    partial void OnBaglantilariKesChanged(bool value) => ScriptiYenile();
    partial void OnNoRecoveryChanged(bool value) => ScriptiYenile();
    partial void OnDosyaChanged(string value)
    {
        // dosya değişti → eski setler geçersiz
        if (_kuruluyor) return;
        Setler.Clear(); Dosyalar.Clear(); _setler = []; _zincir = [];
        SeciliSet = null;
        SetNotu = "";
        ScriptiYenile();
    }

    partial void OnCalisiyorChanged(bool value) { Bildir(); OnPropertyChanged(nameof(IlerlemeGorunur)); }
    partial void OnOkunuyorChanged(bool value) => Bildir();
    partial void OnBittiChanged(bool value) => OnPropertyChanged(nameof(IlerlemeGorunur));
    partial void OnBilgiChanged(GeriYuklemeBilgisi? value) { OnPropertyChanged(nameof(HedefMevcut)); Bildir(); }
    partial void OnEngelChanged(string value) => Bildir();

    private void Bildir()
    {
        OnPropertyChanged(nameof(GeriYuklenebilirMi));
        OnPropertyChanged(nameof(DogrulanabilirMi));
    }

    partial void OnSeciliSetChanged(YedekSetiGorunum? value)
    {
        Bildir();
        if (value is null || _kuruluyor)
        {
            if (value is null) { _zincir = []; Dosyalar.Clear(); ScriptiYenile(); }
            return;
        }
        _ = SetSecildiAsync(value);
    }

    /// <summary>Sunucu klasör gezgini köprüsü (pencere kullanır).</summary>
    public Task<QueryResult> KlasorKomutuAsync(string yol, CancellationToken ct) => _komut(YedekSorgulari.KlasorSql(yol), null, 30, ct);

    private void HedefNotunuKur()
    {
        if (Bilgi is null) { HedefNotu = ""; return; }
        string hedef = HedefDb.Trim();
        HedefNotu = hedef.Length == 0 ? ""
            : HedefMevcut
                ? $"⚠ '{hedef}' mevcut — üzerine yazılacak, içindeki her şey silinir (REPLACE gerekir, geri alınamaz)."
                : $"ℹ '{hedef}' yeni veritabanı olarak oluşturulacak (kopya) — kaynak '{_kaynakDb}' dokunulmaz. Dosya adları hedef ada göre önerildi.";
    }

    private void HedefYollariOner()
    {
        if (_zincir.Count == 0 || Dosyalar.Count == 0) return;
        IReadOnlyList<YedekDosyasi> dosyalar = [.. Dosyalar.Select(d => new YedekDosyasi(d.Mantiksal, d.Orijinal, d.Tur == "Günlük" ? "L" : "D", 0))];
        IReadOnlyList<DosyaTasima> oneri = GeriYuklemeSorgulari.HedefYollariOner(dosyalar, _zincir[0].Veritabani, HedefDb, Bilgi);
        for (int i = 0; i < Dosyalar.Count && i < oneri.Count; i++)
            Dosyalar[i].Hedef = oneri[i].Hedef;
    }

    private void ScriptiYenile()
    {
        if (_kuruluyor) return;
        GeriYuklemeAyari a = Ayar;
        Script = _zincir.Count == 0 ? "-- Önce yedek dosyasını okuyup bir set seçin." : GeriYuklemeSorgulari.Script(a, _zincir, DateTime.Now, _sunucuAdi);
        IReadOnlyList<string> h = GeriYuklemeSorgulari.Dogrula(a, _zincir, Bilgi);
        Engel = h.Count == 0 ? "" : h[0];
    }

    /// <summary>Sunucu bilgisi + msdb tarihçesi (tüm DB'ler, son 30).</summary>
    public async Task YukleAsync(CancellationToken ct = default)
    {
        Okunuyor = true;
        Durum = "Sunucu bilgisi alınıyor…";
        try
        {
            QueryResult r = await _komut(GeriYuklemeSorgulari.BilgiSql(), null, 30, ct);
            if (r.Hata is not null) { Durum = "Sunucu bilgisi alınamadı: " + r.Hata.Mesaj; return; }
            Bilgi = GeriYuklemeSorgulari.BilgiOku(r);
            if (Bilgi is null) { Durum = "Sunucu bilgisi okunamadı."; return; }
            SunucuOzeti = $"{_sunucuAdi} · SQL Server {Bilgi.Surum}";
            YetkiNotu = Bilgi.Sysadmin ? "✔ Yetki: sysadmin"
                : Bilgi.OlusturmaIzni ? "✔ Yetki: CREATE DATABASE var (dbcreator)"
                : "⚠ Yetki: yeni veritabanı oluşturma izni yok — yalnız sahibi olduğunuz veritabanının üzerine yazabilirsiniz";
            HedefNotunuKur();

            QueryResult t = await _komut(YedekSorgulari.TarihceSql(null, 30), "msdb", 30, ct);
            Tarihce.Clear();
            if (t.Hata is null)
                foreach (YedekKaydi k in YedekSorgulari.TarihceOku(t))
                    Tarihce.Add(new TarihceGorunum(k.Veritabani, k.Bitis.ToString("dd.MM.yyyy HH:mm"), YedekSorgulari.TurAdi(k.Tur),
                        YedekSorgulari.BoyutMetni(k.BoyutBayt), k.Dosya, k.Konum));
            ScriptiYenile();
            Durum = "Hazır — yedek dosyasını seçip 🔎 Oku'ya basın.";
        }
        finally
        {
            Okunuyor = false;
        }
    }

    /// <summary>Tarihçeden seçim: dosya + konum → oku ve o seti seç.</summary>
    public async Task TarihcedenSecAsync(TarihceGorunum kayit, CancellationToken ct = default)
    {
        Dosya = kayit.Dosya;
        await OkuAsync(kayit.Konum, ct);
    }

    /// <summary>🔎 Oku: HEADERONLY → setler; <paramref name="konum"/> verilmişse o, yoksa en yeni set seçilir.</summary>
    [RelayCommand]
    public Task OkuAsync() => OkuAsync(null, CancellationToken.None);

    public async Task OkuAsync(int? konum, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Dosya)) { SetNotu = "Dosya yolu boş."; return; }
        Okunuyor = true;
        Durum = "Dosya okunuyor (RESTORE HEADERONLY)…";
        try
        {
            QueryResult r = await _komut(GeriYuklemeSorgulari.HeaderSql(Dosya.Trim()), null, 120, ct);
            _kuruluyor = true;
            Setler.Clear(); Dosyalar.Clear(); _zincir = [];
            SeciliSet = null;
            _kuruluyor = false;
            if (r.Hata is not null)
            {
                _setler = [];
                SetNotu = "✖ " + GeriYuklemeSorgulari.HataMetni(r.Hata);
                Durum = SetNotu;
                ScriptiYenile();
                return;
            }
            _setler = GeriYuklemeSorgulari.SetleriOku(r);
            foreach (YedekSeti s in _setler)
                Setler.Add(new YedekSetiGorunum(s.Konum, YedekSorgulari.TurAdi(s.Tur), s.Veritabani, s.Bitis.ToString("dd.MM.yyyy HH:mm"),
                    YedekSorgulari.BoyutMetni(s.Boyut), $"{s.SurumMajor}.{s.SurumMinor}", s.KopyaMi ? "yalnız kopya" : "", s));
            SetNotu = _setler.Count == 0 ? "Dosyada yedek seti yok." : $"Dosyada {_setler.Count} yedek seti var (RESTORE HEADERONLY).";
            Durum = SetNotu;
            YedekSetiGorunum? sec = konum is { } k ? Setler.FirstOrDefault(s => s.Konum == k) : Setler.LastOrDefault();
            if (sec is not null)
                SeciliSet = sec; // → SetSecildiAsync
            else
                ScriptiYenile();
        }
        finally
        {
            Okunuyor = false;
        }
    }

    private async Task SetSecildiAsync(YedekSetiGorunum sec)
    {
        Okunuyor = true;
        try
        {
            (IReadOnlyList<YedekSeti> zincir, string? uyari) = GeriYuklemeSorgulari.ZincirKur(_setler, sec.Konum);
            _zincir = zincir;
            ZincirNotu = uyari ?? (zincir.Count == 1 ? "" : "Zincir: " + string.Join(" → ", zincir.Select(z => $"{YedekSorgulari.TurAdi(z.Tur)} #{z.Konum}")) + " — gerekli setler otomatik öne eklendi.");
            QueryResult f = await _komut(GeriYuklemeSorgulari.FileListSql(Dosya.Trim(), zincir[0].Konum), null, 120, CancellationToken.None);
            Dosyalar.Clear();
            if (f.Hata is null)
            {
                IReadOnlyList<YedekDosyasi> dosyalar = GeriYuklemeSorgulari.DosyalariOku(f);
                foreach (YedekDosyasi d in dosyalar)
                {
                    var g = new DosyaTasimaGorunum
                    {
                        Mantiksal = d.Mantiksal, Tur = d.Tur.Equals("L", StringComparison.OrdinalIgnoreCase) ? "Günlük" : "Veri",
                        Boyut = YedekSorgulari.BoyutMetni(d.Boyut), Orijinal = d.Fiziksel,
                    };
                    g.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DosyaTasimaGorunum.Hedef)) ScriptiYenile(); };
                    Dosyalar.Add(g);
                }
                HedefYollariOner();
            }
            else
                SetNotu = "✖ Dosya listesi okunamadı: " + GeriYuklemeSorgulari.HataMetni(f.Hata);
            HedefNotunuKur();
            ScriptiYenile();
        }
        finally
        {
            Okunuyor = false;
        }
    }

    /// <summary>🔍 Doğrula: RESTORE VERIFYONLY — veri değişmez.</summary>
    [RelayCommand]
    public async Task DogrulaAsync()
    {
        if (SeciliSet is null) return;
        Okunuyor = true;
        Durum = "Doğrulanıyor (RESTORE VERIFYONLY)…";
        try
        {
            QueryResult r = await _komut(GeriYuklemeSorgulari.VerifySql(Dosya.Trim(), SeciliSet.Konum), null, 0, CancellationToken.None);
            Durum = r.Hata is null
                ? "✔ Yedek seti geçerli: " + (r.Mesajlar.LastOrDefault() ?? "RESTORE VERIFYONLY başarılı")
                : "✖ Doğrulama: " + GeriYuklemeSorgulari.HataMetni(r.Hata);
        }
        finally
        {
            Okunuyor = false;
        }
    }

    [RelayCommand]
    private void ScriptiAl() => _scriptAc($"geri-yukle-{HedefDb.Trim()}.sql", Script);

    /// <summary>⏪ Geri yükle: Tehlike onayı (K5) → master bağlamında, sınırsız zaman aşımı → DMV ilerlemesi.</summary>
    [RelayCommand]
    public async Task GeriYukleAsync()
    {
        if (!GeriYuklenebilirMi) return;
        string hedef = HedefDb.Trim();
        string mesaj = HedefMevcut
            ? $"[{hedef}] üzerine yazılacak — içindeki her şey silinecek, yedekteki hâline dönecek. Bu işlem GERİ ALINAMAZ."
              + (BaglantilariKes ? "\nAçık bağlantılar kesilecek (SINGLE_USER)." : "")
            : $"[{hedef}] yeni veritabanı olarak oluşturulacak ({_zincir.Count} set, {Dosyalar.Count} dosya).";
        if (!_onay("Geri yükleme başlatılsın mı?", mesaj, "⏪ Geri yükle"))
            return;

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        Calisiyor = true; Bitti = false; Basarili = false; Yuzde = 0; Log = "";
        IlerlemeMetni = "⏳ Başlatılıyor…";
        Durum = "⏪ Geri yükleme sürüyor — iptal, veritabanını RESTORING durumunda bırakabilir.";
        var kronometre = System.Diagnostics.Stopwatch.StartNew();
        string script = Script;
        try
        {
            Task<QueryResult> komut = _komut(script, "master", 0, ct);
            while (!komut.IsCompleted)
            {
                await Task.WhenAny(komut, Task.Delay(IlerlemeAraligiMs, CancellationToken.None));
                if (komut.IsCompleted) break;
                try
                {
                    QueryResult i = await _komut(YedekSorgulari.IlerlemeSql(null), null, 10, CancellationToken.None);
                    if (YedekSorgulari.IlerlemeOku(i) is { } ilerleme)
                    {
                        Yuzde = ilerleme.Yuzde;
                        IlerlemeMetni = $"⏳ Geri yükleniyor… %{ilerleme.Yuzde:0}"
                            + (ilerleme.Kalan is { } k ? $" · tahmini kalan {k:m\\:ss}" : "") + $" · geçen {kronometre.Elapsed:m\\:ss}";
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            QueryResult sonuc = await komut;
            kronometre.Stop();
            Log = string.Join("\n", sonuc.Mesajlar);
            if (sonuc.IptalEdildi || ct.IsCancellationRequested)
            {
                Durum = $"■ İptal edildi — [{hedef}] RESTORING durumunda kalmış olabilir; yeniden geri yükleyin ya da DROP edin.";
                IlerlemeMetni = "İptal edildi";
            }
            else if (sonuc.Hata is not null)
            {
                Durum = "✖ " + GeriYuklemeSorgulari.HataMetni(sonuc.Hata);
                IlerlemeMetni = "Hata";
            }
            else
            {
                Basarili = true; Yuzde = 100;
                IlerlemeMetni = $"✔ Tamamlandı · {kronometre.Elapsed:m\\:ss}";
                Durum = NoRecovery
                    ? $"✔ [{hedef}] geri yüklendi, RESTORING'de bekliyor (NORECOVERY) — fark/günlükle devam edin."
                    : $"✔ [{hedef}] geri yüklendi ve kullanıma hazır." + (HedefMevcut ? "" : " Nesne gezginini ⟳ ile tazeleyin.");
            }
        }
        catch (OperationCanceledException)
        {
            Durum = $"■ İptal edildi — [{hedef}] RESTORING durumunda kalmış olabilir.";
            IlerlemeMetni = "İptal edildi";
        }
        finally
        {
            Calisiyor = false; Bitti = true;
            _cts.Dispose(); _cts = null;
        }
    }

    /// <summary>■ İptal — K9: ikinci onay (RESTORING riski).</summary>
    [RelayCommand]
    private void Iptal()
    {
        if (_cts is null) return;
        if (_onay("Geri yükleme iptal edilsin mi?",
                "İptal edilirse veritabanı RESTORING durumunda kalabilir; sonra yeniden geri yüklemeniz ya da DROP etmeniz gerekir.",
                "Yine de iptal et"))
            _cts.Cancel();
    }
}
