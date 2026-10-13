using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Sunucu komut köprüsü: sql · veritabanı bağlamı (null = profil varsayılanı) · zaman aşımı sn (0 = sınırsız).</summary>
public delegate Task<QueryResult> SunucuKomutu(string sql, string? veritabani, int timeoutSn, CancellationToken ct);

/// <summary>Tarihçe satırının görünümü.</summary>
public sealed record YedekKaydiGorunum(string Tarih, string Tur, string Boyut, string Dosya, string Kullanici, string Not);

/// <summary>
/// 💾 Yedek al penceresi (v23-S26 S1 — K1–K9): tür/hedef/seçenekler → canlı script; "▶ Yedekle" ürün içinde koşar
/// (ilerleme DMV'den, iptal), "📄 Script olarak al" sekmede açar. Sunucu bilgisi tek sorguyla gelir; günlük/sıkıştırma/
/// izin görünürlüğü ondan türer. Her sorgu köprüden (<see cref="SunucuKomutu"/>) — sahte köprüyle test edilir.
/// </summary>
public sealed partial class YedekPenceresiViewModel : ObservableObject
{
    private readonly SunucuKomutu _komut;
    private readonly Action<string, string> _scriptAc;
    private readonly string _sunucuAdi;
    private CancellationTokenSource? _cts;

    public string Veritabani { get; }
    public string PencereBasligi => $"💾 Yedek al — {Veritabani}";

    /// <summary>İlerleme yoklama aralığı (test kısaltır).</summary>
    public int IlerlemeAraligiMs { get; set; } = 1000;

    [ObservableProperty] private YedekTuru _tur = YedekTuru.Tam;
    [ObservableProperty] private string _dosya = "";
    [ObservableProperty] private bool _kopyaOnly;
    [ObservableProperty] private bool _checksum = true;
    [ObservableProperty] private bool _sikistir;
    [ObservableProperty] private bool _uzerineYaz = true;
    [ObservableProperty] private string _ad = "";
    [ObservableProperty] private string _aciklama = "";

    [ObservableProperty] private YedekSunucuBilgisi? _bilgi;
    [ObservableProperty] private string _sunucuOzeti = "";
    [ObservableProperty] private string _sonTamYedekNotu = "";
    [ObservableProperty] private string _klasorNotu = "";
    public ObservableCollection<YedekKaydiGorunum> Tarihce { get; } = [];

    [ObservableProperty] private string _script = "";
    [ObservableProperty] private string _engel = "";          // Dogrula'nın ilk hatası — bant
    [ObservableProperty] private bool _gunlukSecilebilir = true;
    [ObservableProperty] private bool _sikistirmaGorunur = true;
    [ObservableProperty] private bool _yukleniyor;
    [ObservableProperty] private bool _calisiyor;
    [ObservableProperty] private bool _bitti;
    [ObservableProperty] private bool _basarili;
    [ObservableProperty] private double _yuzde;
    [ObservableProperty] private string _ilerlemeMetni = "";
    [ObservableProperty] private string _durum = "";
    [ObservableProperty] private string _log = "";

    public bool YedeklenebilirMi => !Calisiyor && !Yukleniyor && Bilgi is not null && Engel.Length == 0;

    /// <summary>İlerleme bölümü yalnız koşarken ve bittikten sonra görünür.</summary>
    public bool IlerlemeGorunur => Calisiyor || Bitti;

    public YedekPenceresiViewModel(string veritabani, string sunucuAdi, SunucuKomutu komut, Action<string, string> scriptAc)
    {
        Veritabani = veritabani;
        _sunucuAdi = sunucuAdi;
        _komut = komut;
        _scriptAc = scriptAc;
        Ad = $"{veritabani} tam yedek {DateTime.Now:dd.MM.yyyy}";
        ScriptiYenile();
    }

    public YedekAyari Ayar => new(Veritabani, Tur, Dosya.Trim(), KopyaOnly, Checksum, Sikistir, UzerineYaz,
        string.IsNullOrWhiteSpace(Ad) ? null : Ad, string.IsNullOrWhiteSpace(Aciklama) ? null : Aciklama);

    partial void OnTurChanged(YedekTuru value)
    {
        // dosya adı türle değişir (yalnız otomatik üretilmişse — kullanıcı yolunu elle yazdıysa dokunma)
        if (_otomatikDosya is not null && Dosya == _otomatikDosya)
            DosyayiOner();
        if (Ad.Contains(" tam yedek ") || Ad.Contains(" fark yedek ") || Ad.Contains(" günlük yedek "))
            Ad = $"{Veritabani} {YedekSorgulari.TurAdi(value).ToLowerInvariant()} yedek {DateTime.Now:dd.MM.yyyy}";
        ScriptiYenile();
    }

    partial void OnDosyaChanged(string value) => ScriptiYenile();
    partial void OnKopyaOnlyChanged(bool value) => ScriptiYenile();
    partial void OnChecksumChanged(bool value) => ScriptiYenile();
    partial void OnSikistirChanged(bool value) => ScriptiYenile();
    partial void OnUzerineYazChanged(bool value) => ScriptiYenile();
    partial void OnAdChanged(string value) => ScriptiYenile();
    partial void OnAciklamaChanged(string value) => ScriptiYenile();
    partial void OnCalisiyorChanged(bool value)
    {
        OnPropertyChanged(nameof(YedeklenebilirMi));
        OnPropertyChanged(nameof(IlerlemeGorunur));
    }

    partial void OnBittiChanged(bool value) => OnPropertyChanged(nameof(IlerlemeGorunur));
    partial void OnYukleniyorChanged(bool value) => OnPropertyChanged(nameof(YedeklenebilirMi));
    partial void OnBilgiChanged(YedekSunucuBilgisi? value) => OnPropertyChanged(nameof(YedeklenebilirMi));
    partial void OnEngelChanged(string value) => OnPropertyChanged(nameof(YedeklenebilirMi));

    private string? _otomatikDosya;

    private void DosyayiOner()
    {
        string klasor = Bilgi?.VarsayilanKlasor ?? "";
        _otomatikDosya = YedekSorgulari.YolBirlestir(klasor, YedekSorgulari.DosyaAdiOner(Veritabani, Tur, DateTime.Now));
        Dosya = _otomatikDosya;
    }

    private void ScriptiYenile()
    {
        Script = YedekSorgulari.Script(Ayar, DateTime.Now, _sunucuAdi);
        IReadOnlyList<string> hatalar = YedekSorgulari.Dogrula(Ayar, Bilgi);
        Engel = hatalar.Count == 0 ? "" : hatalar[0];
    }

    /// <summary>Sunucu bilgisi + tarihçe; varsayılan dosya yolu ve seçenek görünürlükleri buradan.</summary>
    public async Task YukleAsync(CancellationToken ct = default)
    {
        Yukleniyor = true;
        Durum = "Sunucu bilgisi alınıyor…";
        try
        {
            QueryResult r = await _komut(YedekSorgulari.SunucuBilgisiSql(Veritabani), null, 30, ct);
            if (r.Hata is not null)
            {
                Durum = "Sunucu bilgisi alınamadı: " + r.Hata.Mesaj;
                return;
            }
            Bilgi = YedekSorgulari.SunucuBilgisiOku(r);
            if (Bilgi is null)
            {
                Durum = "Sunucu bilgisi okunamadı.";
                return;
            }
            SunucuOzeti = $"{_sunucuAdi} · SQL Server {Bilgi.Surum} · {Bilgi.Edition} · kurtarma modeli {Bilgi.KurtarmaModeli}";
            GunlukSecilebilir = Bilgi.GunlukAlinabilir && Bilgi.GunlukIzni;
            SikistirmaGorunur = Bilgi.SikistirmaVar;
            Sikistir = Bilgi.SikistirmaVar && Bilgi.SikistirmaVarsayilan;
            KlasorNotu = string.IsNullOrWhiteSpace(Bilgi.YedekKlasoru)
                ? "Sunucunun yedek klasörü tanımlı değil (LocalDB/Express) — veri klasörü önerildi; yolu değiştirebilirsiniz."
                : "Varsayılan klasör sunucunun yedek klasörü (InstanceDefaultBackupPath). Dosya adı tür ve zamanla üretildi.";
            if (string.IsNullOrWhiteSpace(Dosya))
                DosyayiOner();
            ScriptiYenile();
            await TarihceyiYukleAsync(ct);
            Durum = Engel.Length > 0 ? "" : "Hazır";
        }
        finally
        {
            Yukleniyor = false;
        }
    }

    private async Task TarihceyiYukleAsync(CancellationToken ct)
    {
        QueryResult r = await _komut(YedekSorgulari.TarihceSql(Veritabani), "msdb", 30, ct);
        Tarihce.Clear();
        if (r.Hata is not null)
        {
            SonTamYedekNotu = "Yedek tarihçesi okunamadı (msdb): " + r.Hata.Mesaj;
            return;
        }
        IReadOnlyList<YedekKaydi> kayitlar = YedekSorgulari.TarihceOku(r);
        foreach (YedekKaydi k in kayitlar)
            Tarihce.Add(new YedekKaydiGorunum(
                k.Bitis.ToString("dd.MM.yyyy HH:mm"), YedekSorgulari.TurAdi(k.Tur),
                YedekSorgulari.BoyutMetni(k.SikistirilmisBayt is { } sb && sb < k.BoyutBayt ? sb : k.BoyutBayt),
                k.Dosya, k.Kullanici, k.KopyaMi ? "yalnız kopya" : ""));
        YedekKaydi? sonTam = kayitlar.FirstOrDefault(k => k.Tur == YedekTuru.Tam && !k.KopyaMi);
        SonTamYedekNotu = sonTam is null
            ? "Bu veritabanının tam yedeği yok — Fark/Günlük için önce Tam alın."
            : $"Son tam yedek: {sonTam.Bitis:dd.MM.yyyy HH:mm} ({sonTam.Dosya}, {YedekSorgulari.BoyutMetni(sonTam.BoyutBayt)}) · Fark ve günlük buna göre alınır.";
    }

    /// <summary>Sunucu klasörü (K4): xp_dirtree bir düzey.</summary>
    public async Task<IReadOnlyList<KlasorGirisi>> KlasorListeleAsync(string yol, CancellationToken ct = default)
    {
        QueryResult r = await _komut(YedekSorgulari.KlasorSql(yol), null, 30, ct);
        if (r.Hata is not null)
            throw new InvalidOperationException("Klasör listelenemedi: " + r.Hata.Mesaj);
        return YedekSorgulari.KlasorOku(r);
    }

    [RelayCommand]
    private void ScriptiAl() => _scriptAc($"yedek-{Veritabani}.sql", Script);

    /// <summary>▶ Yedekle: komut sınırsız zaman aşımıyla DB bağlamında; yan yana DMV yoklaması ilerlemeyi yazar.</summary>
    [RelayCommand]
    public async Task YedekleAsync()
    {
        if (!YedeklenebilirMi)
            return;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        Calisiyor = true; Bitti = false; Basarili = false;
        Yuzde = 0; Log = "";
        IlerlemeMetni = "⏳ Başlatılıyor…";
        Durum = "▶ Yedekleme sürüyor — \"İptal\" komutu durdurur (yarım dosya kalabilir).";
        var kronometre = System.Diagnostics.Stopwatch.StartNew();
        string script = Script;
        try
        {
            Task<QueryResult> komut = _komut(script, Veritabani, 0, ct);
            while (!komut.IsCompleted)
            {
                await Task.WhenAny(komut, Task.Delay(IlerlemeAraligiMs, CancellationToken.None));
                if (komut.IsCompleted) break;
                try
                {
                    QueryResult i = await _komut(YedekSorgulari.IlerlemeSql(Veritabani), null, 10, CancellationToken.None);
                    if (YedekSorgulari.IlerlemeOku(i) is { } ilerleme)
                    {
                        Yuzde = ilerleme.Yuzde;
                        IlerlemeMetni = $"⏳ Yedekleniyor… %{ilerleme.Yuzde:0}"
                            + (ilerleme.Kalan is { } k ? $" · tahmini kalan {k:m\\:ss}" : "")
                            + $" · geçen {kronometre.Elapsed:m\\:ss}";
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // yoklama hatası yedeklemeyi düşürmez
                }
            }
            QueryResult sonuc = await komut;
            kronometre.Stop();
            Log = string.Join("\n", sonuc.Mesajlar);
            if (sonuc.IptalEdildi || ct.IsCancellationRequested)
            {
                Durum = $"■ İptal edildi — yarım dosya kalmış olabilir: {Ayar.Dosya}";
                IlerlemeMetni = "İptal edildi";
            }
            else if (sonuc.Hata is not null)
            {
                Durum = "✖ " + YedekSorgulari.HataMetni(sonuc.Hata);
                IlerlemeMetni = "Hata";
            }
            else
            {
                Basarili = true;
                Yuzde = 100;
                IlerlemeMetni = $"✔ Tamamlandı · {kronometre.Elapsed:m\\:ss}";
                Durum = $"✔ Yedek alındı: {Ayar.Dosya}";
                await TarihceyiYukleAsync(CancellationToken.None);
                if (Tarihce.Count > 0)
                    Durum += $" ({Tarihce[0].Boyut})";
            }
        }
        catch (OperationCanceledException)
        {
            Durum = $"■ İptal edildi — yarım dosya kalmış olabilir: {Ayar.Dosya}";
            IlerlemeMetni = "İptal edildi";
        }
        finally
        {
            Calisiyor = false;
            Bitti = true;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Iptal() => _cts?.Cancel();
}
