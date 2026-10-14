using System.Collections.ObjectModel;
using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// 📊 GRAFİK PENCERESİ (v23-S30 — kullanıcı 14 Eki: "grafiği ayrı bir ekrana alalım, oradan basınca farklı bir pencereden
/// açılsın; sol menüde de menüsü olsun, oradan sorgu çalıştırıp grafik karşılığını alalım"; mockup
/// docs/mockup/grafik-penceresi.html onaylı). İki giriş: sonuç şeridindeki 📊 (veri + SQL dolu) ve sol rail 📊 (boş).
/// Pencerede SQL + ▶ Çalıştır (F5) + oto-yenile; grafik GrafikPaneliViewModel (v23-S24/S25 çekirdeği aynen); veri tablosu.
/// G2: yalnız SELECT — yazma/DDL/EXEC reddedilir. G3: her koşu kendi bağlantısıyla (sekmenin oturumuna dokunmaz).
/// </summary>
public sealed partial class GrafikPenceresiViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, string?, CancellationToken, Task<QueryResult>> _calistir;
    private readonly Func<IReadOnlyList<(string Baslik, string Sql)>> _sekmeSqlleri;
    private readonly MotorTuru _motor;
    private readonly int _setSirasi;
    private CancellationTokenSource? _cts;
    private System.Windows.Threading.DispatcherTimer? _otoYenileSayaci;

    public string ProfilAdi { get; }
    public string Sunucu { get; }
    public string? KaynakBaslik { get; }
    public ObservableCollection<string> Veritabanlari { get; }

    [ObservableProperty] private string _sql;
    [ObservableProperty] private string? _seciliDb;
    [ObservableProperty] private DataTable? _tablo;
    [ObservableProperty] private GrafikPaneliViewModel? _grafik;
    [ObservableProperty] private bool _calisiyor;
    [ObservableProperty] private string _durum = "";
    [ObservableProperty] private string _sonucOzeti = "";
    [ObservableProperty] private string _sureMetni = "";
    [ObservableProperty] private string _sonKosu = "";
    [ObservableProperty] private bool _sqlGorunur = true;
    [ObservableProperty] private bool _veriGorunur = true;
    [ObservableProperty] private bool _otoYenile;
    [ObservableProperty] private int _otoYenileSaniye = 30;

    public string PencereBasligi => "📊 Grafik — " + (KaynakBaslik ?? "yeni") + " · " + ProfilAdi + (SeciliDb is { Length: > 0 } d ? " · " + d : "");
    public bool VeriVar => Grafik is not null;
    public bool BosDurum => Grafik is null;

    public GrafikPenceresiViewModel(string profilAdi, string sunucu, MotorTuru motor, IReadOnlyList<string> veritabanlari,
        string? veritabani, string sql, DataTable? tablo, int setSirasi, string sureMetni, string? kaynakBaslik,
        Func<string, string?, CancellationToken, Task<QueryResult>> calistir, Func<IReadOnlyList<(string Baslik, string Sql)>> sekmeSqlleri)
    {
        ProfilAdi = profilAdi;
        Sunucu = sunucu;
        _motor = motor;
        Veritabanlari = [.. veritabanlari];
        _seciliDb = veritabani ?? Veritabanlari.FirstOrDefault();
        _sql = sql;
        _setSirasi = setSirasi;
        KaynakBaslik = kaynakBaslik;
        _calistir = calistir;
        _sekmeSqlleri = sekmeSqlleri;
        if (tablo is not null)
        {
            VeriyiKur(tablo);
            SureMetni = sureMetni;
            SonucOzeti = $"{tablo.Rows.Count:N0} satır" + (sureMetni.Length > 0 ? " · " + sureMetni : "");
            Durum = "Sonuçtan açıldı — ▶ Çalıştır (F5) sorguyu bu pencerede yeniden koşturur.";
        }
        else
            Durum = "Grafiğe dönüştürmek istediğiniz sorguyu yazın ve F5'e basın.";
    }

    partial void OnSeciliDbChanged(string? value) => OnPropertyChanged(nameof(PencereBasligi));

    partial void OnGrafikChanged(GrafikPaneliViewModel? value)
    {
        OnPropertyChanged(nameof(VeriVar));
        OnPropertyChanged(nameof(BosDurum));
    }

    /// <summary>Açık sorgu sekmelerinin SQL'i — "SQL'i al ▾" menüsü.</summary>
    public IReadOnlyList<(string Baslik, string Sql)> SekmeSqlleri() => _sekmeSqlleri();

    private void VeriyiKur(DataTable tablo)
    {
        Tablo = tablo;
        if (Grafik is null)
            Grafik = new GrafikPaneliViewModel(tablo);
        else
            Grafik.TabloyuDegistir(tablo); // seçimler korunur; kolonlar değiştiyse yeniden tahmin
    }

    /// <summary>G2: pencere yalnız okuma çalıştırır. Yazma/DDL ve EXEC (prosedür yazabilir) reddedilir — temkinli kapı.</summary>
    public static string? YazmaEngeli(string sql, MotorTuru motor)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "Çalıştırılacak sorgu yok.";
        if (QueryService.PlanIcinYazmaSayilir(sql, motor) || QueryService.ExecMi(sql))
            return "Grafik penceresi yalnız SELECT çalıştırır — yazma/DDL/EXEC içeren sorgu çalıştırılmadı. Bu sorguyu sorgu sekmesinde koşturun.";
        return null;
    }

    [RelayCommand]
    public async Task CalistirAsync()
    {
        if (Calisiyor) return;
        if (YazmaEngeli(Sql, _motor) is { } engel)
        {
            Durum = "⛔ " + engel;
            return;
        }
        _cts = new CancellationTokenSource();
        Calisiyor = true;
        Durum = "⏳ Çalışıyor…";
        var kronometre = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            QueryResult r = await _calistir(Sql, SeciliDb, _cts.Token);
            kronometre.Stop();
            if (r.IptalEdildi) { Durum = "■ Durduruldu."; return; }
            if (r.Hata is { } h) { Durum = "✖ " + h.Mesaj; return; }
            if (r.ResultSetler.Count == 0) { Durum = "Sorgu sonuç kümesi döndürmedi — grafik için SELECT gerekir."; return; }
            ResultSetData rs = r.ResultSetler[Math.Min(_setSirasi, r.ResultSetler.Count - 1)];
            SonucSeti set = await Task.Run(() => SonucBicimleyici.TabloyaCevir(rs));
            VeriyiKur(set.Tablo);
            SureMetni = SonucBicimleyici.SureFormatla(r.Sure > TimeSpan.Zero ? r.Sure : kronometre.Elapsed);
            SonucOzeti = $"{set.SatirSayisi:N0} satır · {SureMetni}" + (r.SatirSiniriAsildi ? " · ilk 100.000" : "");
            SonKosu = $"son koşu {DateTime.Now:HH:mm:ss}";
            Durum = r.ResultSetler.Count > 1 ? $"✔ Tamam — sorgu {r.ResultSetler.Count} sonuç kümesi döndürdü; {Math.Min(_setSirasi, r.ResultSetler.Count - 1) + 1}. çiziliyor." : "✔ Tamam";
        }
        finally
        {
            Calisiyor = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Durdur() => _cts?.Cancel();

    partial void OnOtoYenileChanged(bool value)
    {
        _otoYenileSayaci?.Stop();
        if (!value) { _otoYenileSayaci = null; return; }
        _otoYenileSayaci = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, OtoYenileSaniye)) };
        _otoYenileSayaci.Tick += async (_, _) => { if (!Calisiyor) await CalistirAsync(); };
        _otoYenileSayaci.Start();
    }

    partial void OnOtoYenileSaniyeChanged(int value)
    {
        if (_otoYenileSayaci is not null)
            _otoYenileSayaci.Interval = TimeSpan.FromSeconds(Math.Max(2, value));
    }

    [RelayCommand]
    private void SqlGorunurDegistir() => SqlGorunur = !SqlGorunur;

    public void Dispose()
    {
        _otoYenileSayaci?.Stop();
        _cts?.Cancel();
    }
}
