using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Eksik index grid satırı: öneri + örtüşme notu + kova + hazır script (R1.1).</summary>
public sealed record EksikIndexSatiri(
    EksikIndexOnerisi Oneri, string? OrtusmeNotu, bool Soluk, string Script)
{
    public string GucMetni => Oneri.Guc switch
    {
        OneriGucu.Yuksek => "YÜKSEK",
        OneriGucu.Orta => "orta",
        _ => "düşük",
    };

    public string Veritabani => Oneri.Veritabani;
    public string Tablo => Oneri.Tablo;
    public string Anahtar => string.Join(", ", Oneri.AnahtarKolonlar);
    public string Include => string.Join(", ", Oneri.IncludeKolonlar);
    public long Kullanim => Oneri.KullanimSayisi;
    public string SonKullanim => Oneri.SonKullanim?.ToLocalTime().ToString("g") ?? "—";
    /// <summary>2019+ sürümde öneriyi isteyen sorgu (kural 6) — kolon tooltip'inde.</summary>
    public string? IsteyenSorgu => Oneri.IsteyenSorgu;
}

/// <summary>
/// Teşhis Merkezi sekmesi (V2-S7): eksik/kullanılmayan index (D.E.A.T.H. çifti),
/// RCSI karar paneli, tek-tuş havuz. Hiçbir script tek tuşla ÇALIŞMAZ — hepsi
/// "incele + sekmeye kopyala" akışıyla sorgu sekmesine gider (R1.1 kural 3, R1.5).
/// </summary>
public partial class TeshisSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly TeshisServisi _servis;
    private readonly Func<ConnectionProfile?> _profilGetir;
    private List<EksikIndexSatiri> _tumEksikler = [];
    private DateTime _sunucuBaslangici;
    private bool _yuklendi;

    public TeshisSekmesiViewModel(
        TeshisServisi servis,
        Func<ConnectionProfile?> profilGetir,
        ObservableCollection<string> veritabaniAdlari,
        ISorguGecmisiDeposu gecmis)
    {
        _servis = servis;
        _profilGetir = profilGetir;
        VeritabaniAdlari = veritabaniAdlari;
        DenetimIzi = new DenetimIziViewModel(gecmis);
    }

    public string Baslik => "🛠 Yönetim Paneli"; // UI adı kullanıcı isteği (2026-07-17); kod içi ad Teshis* kaldı

    /// <summary>View bağlar: script'i yeni sorgu sekmesinde açar (çalıştırmaz).</summary>
    public Action<string, string>? SekmeyeAc { get; set; }

    public ObservableCollection<string> VeritabaniAdlari { get; }

    [ObservableProperty] private SekmeDurumu _durum = SekmeDurumu.Bosta;
    [ObservableProperty] private string _uptimeBandi = "";
    /// <summary>Uptime &lt; 7 gün: DMV sayaçları güvenilmez (R1.1 kural 2) — band kırmızımsı olur.</summary>
    [ObservableProperty] private bool _uptimeGuvenilmez;
    [ObservableProperty] private string _bilgi = "";
    /// <summary>Düşük kovadakiler varsayılan GİZLİ (R1.1 kural 1).</summary>
    [ObservableProperty] private bool _dusukleriGoster;
    /// <summary>Kullanılmayan index + tablo boyutları bu veritabanına bakar.</summary>
    [ObservableProperty] private string? _secilenVeritabani;

    public ObservableCollection<EksikIndexSatiri> EksikIndexler { get; } = [];
    public ObservableCollection<KullanilmayanIndex> Kullanilmayanlar { get; } = [];
    public ObservableCollection<SonucSetiGorunumu> RcsiSetleri { get; } = [];
    public ObservableCollection<SonucSetiGorunumu> HavuzSetleri { get; } = [];

    /// <summary>v22-S1 m.14: "Yavaş sorgular" sekmesi — ortalama süreye ve toplam süreye göre iki
    /// liste (Mongo panelindeki "Index önerileri / Yavaş sorgular"ın SQL karşılığı).</summary>
    public ObservableCollection<SonucSetiGorunumu> YavasSetleri { get; } = [];

    /// <summary>Bakım sekmesi (V5-S3): tempdb · bloklama zinciri · istatistik tazeliği.</summary>
    public ObservableCollection<SonucSetiGorunumu> BakimSetleri { get; } = [];

    // ── Aktivite / Denetim (v10): A = denetim izi (paylaşımlı VM) + B = canlı aktivite ──

    /// <summary>Denetim izi (A): geçmiş + kim + işlem türü + süzgeç. Üç panelde paylaşılan bileşen.</summary>
    public DenetimIziViewModel DenetimIzi { get; }

    /// <summary>Canlı aktivite (B): şu an kim ne çalıştırıyor (SQL Server oturumları).</summary>
    public ObservableCollection<SonucSetiGorunumu> AktiviteSetleri { get; } = [];

    public bool CalisiyorMu => Durum == SekmeDurumu.Calisiyor;

    [RelayCommand]
    public async Task YenileAsync()
    {
        ConnectionProfile? profil = _profilGetir();
        if (profil is null || CalisiyorMu)
            return;

        Durum = SekmeDurumu.Calisiyor;
        Bilgi = "DMV'ler okunuyor…";
        try
        {
            _sunucuBaslangici = await _servis.SunucuBaslangiciAsync(profil, CancellationToken.None);
            TimeSpan uptime = DateTime.Now - _sunucuBaslangici;
            UptimeGuvenilmez = uptime.TotalDays < 7;
            UptimeBandi = $"DMV verileri {_sunucuBaslangici:dd.MM.yyyy HH:mm}'den beri birikiyor "
                        + $"(uptime {(int)uptime.TotalDays} gün {uptime.Hours} sa)."
                        + (UptimeGuvenilmez
                            ? " ⚠ 7 günden kısa — sayaçlar restart/ALTER INDEX'te sıfırlanır, sonuçlara temkinli yaklaşın."
                            : "");

            // Eksik indexler + örtüşme analizi (öneri olan veritabanlarının mevcut index'leriyle)
            IReadOnlyList<EksikIndexOnerisi> oneriler = await _servis.EksikIndexlerAsync(profil, CancellationToken.None);
            var mevcutlar = new Dictionary<string, IReadOnlyList<MevcutIndex>>(StringComparer.OrdinalIgnoreCase);
            foreach (string vt in oneriler.Select(o => o.Veritabani).Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
                mevcutlar[vt] = await _servis.MevcutIndexlerAsync(profil, vt, CancellationToken.None);

            DateTime simdi = DateTime.UtcNow;
            _tumEksikler = [.. oneriler.Select(o => new EksikIndexSatiri(
                o,
                IndexAnalizcisi.OrtusmeBul(o, mevcutlar.GetValueOrDefault(o.Veritabani) ?? []).Not,
                IndexAnalizcisi.SolukMu(o.SonKullanim, simdi),
                IndexAnalizcisi.CreateIndexScripti(o, _sunucuBaslangici)))];
            EksikleriSuz();

            await VeritabaniBazlilariYukleAsync(profil);

            // Sadeleştirme (kullanıcı kararı 2026-07-19): RCSI'den "tempdb version store" ve
            // "en eski açık işlemler", Havuz'dan "bloklanan oturumlar" setleri KALDIRILDI —
            // üçü de Bakım sekmesindeki daha iyi karşılıklarıyla mükerrerdi.
            RcsiSetleri.Clear();
            SetleriKur(RcsiSetleri, await _servis.RcsiVerileriAsync(profil, CancellationToken.None),
                ["Veritabanı durumları"]);

            HavuzSetleri.Clear();
            SetleriKur(HavuzSetleri, await _servis.HavuzVerileriAsync(profil, CancellationToken.None),
                ["En pahalı 20 sorgu (toplam CPU)", "Bağlantılar", "Son tam yedekler"]);

            // v22-S1 m.14: yavaş sorgular — Havuz'daki liste CPU'ya göre, bu ikisi SÜREYE göre.
            YavasSetleri.Clear();
            SetleriKur(YavasSetleri, await _servis.YavasSorgularAsync(profil, CancellationToken.None),
                ["En yavaş 25 sorgu (ortalama süre)", "Toplam süreyi en çok tüketen 25 sorgu"]);

            // Aktivite/Denetim (v10): A = denetim izi (paylaşımlı), B = canlı aktivite
            await DenetimIzi.YukleAsync(profil);
            AktiviteSetleri.Clear();
            SetleriKur(AktiviteSetleri, await _servis.CanliAktiviteAsync(profil, CancellationToken.None),
                ["Canlı aktivite — şu an kim ne çalıştırıyor"]);

            Durum = SekmeDurumu.Tamamlandi;
            Bilgi = $"{_tumEksikler.Count} eksik index önerisi · {Kullanilmayanlar.Count} kullanılmayan aday";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Durum = SekmeDurumu.Hata;
            Bilgi = $"Teşhis verileri okunamadı: {ex.Message}";
        }
        finally
        {
            _yuklendi = true;
        }
    }

    /// <summary>Seçili veritabanına bağlı bölümler: kullanılmayan index + tablo boyutları.</summary>
    private async Task VeritabaniBazlilariYukleAsync(ConnectionProfile profil)
    {
        Kullanilmayanlar.Clear();
        BakimSetleri.Clear();

        if (SecilenVeritabani is not { } vt)
            return;

        foreach (KullanilmayanIndex k in await _servis.KullanilmayanIndexlerAsync(profil, vt, CancellationToken.None))
            Kullanilmayanlar.Add(k);

        // Tablo boyutları havuz setlerinin sonuna eklenir (DB seçiciyle tazelenir)
        HavuzSetleri.Where(s => s.Baslik.StartsWith("Tablo boyutları", StringComparison.Ordinal)).ToList()
            .ForEach(s => HavuzSetleri.Remove(s));
        SetleriKur(HavuzSetleri, await _servis.TabloBoyutlariAsync(profil, vt, CancellationToken.None),
            [$"Tablo boyutları — {vt} (ilk 50)"]);

        // Bakım (V5-S3 + R1.3 bekleme analizi 2026-07-26) — hepsi ucuz DMV/katalog okuması.
        SetleriKur(BakimSetleri, await _servis.BakimVerileriAsync(profil, vt, CancellationToken.None),
            ["tempdb ne ile dolu?", "tempdb'yi en çok tüketen oturumlar",
             "Bloklama zinciri (kök bloklayan dahil)", $"İstatistik tazeliği — {vt} (en bayat 50)",
             "Bekleyen istekler (şu an — bloklama dışı beklemeler dahil)",
             "Sunucu geneli bekleme istatistikleri (başlangıçtan beri, ilk 15)"]);
    }

    private static void SetleriKur(
        ObservableCollection<SonucSetiGorunumu> hedef, QueryResult sonuc, string[] basliklar)
    {
        for (int i = 0; i < sonuc.ResultSetler.Count; i++)
        {
            SonucSeti set = SonucBicimleyici.TabloyaCevir(sonuc.ResultSetler[i]);
            hedef.Add(new SonucSetiGorunumu
            {
                Set = set,
                Baslik = $"{(i < basliklar.Length ? basliklar[i] : $"Set {i + 1}")}  ({set.SatirSayisi:N0} satır)",
                BaslikGorunur = true,
            });
        }
    }

    private void EksikleriSuz()
    {
        EksikIndexler.Clear();
        foreach (EksikIndexSatiri satir in _tumEksikler.Where(s => DusukleriGoster || s.Oneri.Guc != OneriGucu.Dusuk))
            EksikIndexler.Add(satir);
    }

    partial void OnDusukleriGosterChanged(bool value) => EksikleriSuz();

    partial void OnSecilenVeritabaniChanged(string? value)
    {
        if (_yuklendi && _profilGetir() is { } profil)
            _ = VeritabaniBazlilariYukleAsync(profil);
    }

    [RelayCommand]
    public void EksikScriptiAc(EksikIndexSatiri? satir)
    {
        if (satir is not null)
            SekmeyeAc?.Invoke($"IX öneri {satir.Tablo}", satir.Script);
    }

    [RelayCommand]
    public void DisableScriptiAc(KullanilmayanIndex? satir)
    {
        if (satir is not null)
            SekmeyeAc?.Invoke($"DISABLE {satir.Ad}", satir.DisableScript);
    }

    [RelayCommand]
    public void RcsiScriptiAc()
    {
        if (SecilenVeritabani is { } vt)
            SekmeyeAc?.Invoke($"RCSI geçiş — {vt}", TeshisServisi.RcsiGecisScripti(vt));
    }

    public Task KapatAsync() => Task.CompletedTask; // durum bilgisiz — kalıcı bağlantı yok
}
