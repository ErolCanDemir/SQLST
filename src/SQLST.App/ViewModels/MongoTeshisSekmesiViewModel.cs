using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.ViewModels;

/// <summary>
/// MongoDB Yönetim Paneli (V3 — kullanıcı isteği 2026-07-18). SQL Server panelinin
/// karşılığı ama Mongo'nun kendi kavramlarıyla: veritabanı özeti (dbStats), koleksiyon
/// boyutları ($collStats), INDEX KULLANIMI ($indexStats — hiç kullanılmayanlar üstte)
/// ve çalışan işlemler (currentOp). Salt okunur; hiçbir şey otomatik değiştirilmez.
/// </summary>
public partial class MongoTeshisSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly MongoTeshisServisi _servis;
    private readonly Func<ConnectionProfile?> _profilGetir;
    private bool _yuklendi;

    public MongoTeshisSekmesiViewModel(
        MongoTeshisServisi servis,
        Func<ConnectionProfile?> profilGetir,
        ObservableCollection<string> veritabaniAdlari,
        ISorguGecmisiDeposu gecmis)
    {
        _servis = servis;
        _profilGetir = profilGetir;
        VeritabaniAdlari = veritabaniAdlari;
        DenetimIzi = new DenetimIziViewModel(gecmis);
    }

    public string Baslik => "🛠 Yönetim Paneli";

    public ObservableCollection<string> VeritabaniAdlari { get; }

    /// <summary>Denetim izi (v10-S4): "kim ne iş yapmış" — dört motorda paylaşımlı bileşen.</summary>
    public DenetimIziViewModel DenetimIzi { get; }

    [ObservableProperty] private SekmeDurumu _durum = SekmeDurumu.Bosta;
    [ObservableProperty] private string _sunucuBandi = "";
    [ObservableProperty] private string _bilgi = "";
    /// <summary>Panelin baktığı veritabanı — koleksiyon ve index bölümleri buna bağlıdır.</summary>
    [ObservableProperty] private string? _secilenVeritabani;

    public ObservableCollection<SonucSetiGorunumu> OzetSetleri { get; } = [];
    public ObservableCollection<SonucSetiGorunumu> KoleksiyonSetleri { get; } = [];
    public ObservableCollection<SonucSetiGorunumu> IndexSetleri { get; } = [];
    public ObservableCollection<SonucSetiGorunumu> IslemSetleri { get; } = [];
    /// <summary>m.26 fikir 5/10: system.profile'dan index önerileri + yavaş sorgu listesi.</summary>
    public ObservableCollection<SonucSetiGorunumu> ProfilSetleri { get; } = [];

    /// <summary>Profiler kapalıysa nedenini ve AÇMA komutunu söyler (sessiz boş liste yerine).</summary>
    [ObservableProperty] private string _profilBandi = "";

    public bool CalisiyorMu => Durum == SekmeDurumu.Calisiyor;

    [RelayCommand]
    public async Task YenileAsync()
    {
        ConnectionProfile? profil = _profilGetir();
        if (profil is null || CalisiyorMu)
            return;

        Durum = SekmeDurumu.Calisiyor;
        Bilgi = "MongoDB durum komutları çalıştırılıyor…";
        try
        {
            (string surum, TimeSpan uptime) = await _servis.SunucuBilgisiAsync(profil, CancellationToken.None);
            SunucuBandi = $"MongoDB {surum} · çalışma süresi {(int)uptime.TotalDays} gün {uptime.Hours} sa "
                        + $"{uptime.Minutes} dk · index sayaçları ($indexStats) sunucu yeniden başlayınca sıfırlanır.";

            IslemSetleri.Clear();
            Ekle(IslemSetleri, await _servis.CalisanIslemlerAsync(profil, CancellationToken.None),
                "Çalışan işlemler (currentOp — aktif)");

            await VeritabaniBazlilariYukleAsync(profil);
            await DenetimIzi.YukleAsync(profil); // Aktivite/Denetim (v10-S4)

            Durum = SekmeDurumu.Tamamlandi;
        }
        catch (Exception ex) when (ex is MongoDB.Driver.MongoException or TimeoutException
                                      or InvalidOperationException or OperationCanceledException)
        {
            Durum = SekmeDurumu.Hata;
            Bilgi = $"Yönetim paneli verileri okunamadı: {ex.Message}";
        }
        finally
        {
            _yuklendi = true;
        }
    }

    /// <summary>Seçili veritabanına bağlı bölümler: özet, koleksiyonlar, index kullanımı.</summary>
    private async Task VeritabaniBazlilariYukleAsync(ConnectionProfile profil)
    {
        OzetSetleri.Clear();
        KoleksiyonSetleri.Clear();
        IndexSetleri.Clear();
        ProfilSetleri.Clear();

        if (SecilenVeritabani is not { } vt || string.IsNullOrWhiteSpace(vt))
        {
            Bilgi = "Üstteki listeden bir veritabanı seçin.";
            return;
        }

        Ekle(OzetSetleri, await _servis.VeritabaniOzetiAsync(profil, vt, CancellationToken.None),
            $"Veritabanı özeti — {vt}");

        ResultSetData koleksiyonlar = await _servis.KoleksiyonlarAsync(profil, vt, CancellationToken.None);
        Ekle(KoleksiyonSetleri, koleksiyonlar, $"Koleksiyonlar — {vt} (boyuta göre)");

        ResultSetData indexler = await _servis.IndexKullanimiAsync(profil, vt, CancellationToken.None);
        Ekle(IndexSetleri, indexler, $"Index kullanımı — {vt} (hiç kullanılmayanlar üstte)");

        await ProfilBolumuYukleAsync(profil, vt);

        int kullanilmayan = indexler.Satirlar.Count(r => r.Length > 3 && r[3] is long ops && ops == 0);
        Bilgi = $"{koleksiyonlar.Satirlar.Count} koleksiyon · {indexler.Satirlar.Count} index"
              + (kullanilmayan > 0 ? $" · ⚠ {kullanilmayan} index hiç kullanılmamış (sayaç başlangıcından beri)" : "");
    }

    /// <summary>
    /// m.26 fikir 5 + 10: system.profile'dan **index önerileri** (COLLSCAN yapan gerçek yavaş
    /// sorgulardan) ve **yavaş sorgu listesi**. MongoDB'de SQL Server'ın "missing index" DMV'si
    /// yoktur — tek dürüst kaynak profiler'dır; kapalıysa neden boş olduğu ve AÇMA komutu yazılır.
    /// </summary>
    private async Task ProfilBolumuYukleAsync(ConnectionProfile profil, string vt)
    {
        (bool acik, int slowMs) = await _servis.ProfilerDurumuAsync(profil, vt, CancellationToken.None);
        if (!acik)
        {
            ProfilBandi = "ℹ Profiler KAPALI — yavaş sorgu geçmişi toplanmıyor, bu yüzden index önerisi "
                        + $"üretilemiyor. Açmak için: db.setProfilingLevel(1, {{ slowms: 100 }})  ·  [{vt}]";
            return;
        }

        IReadOnlyList<ProfilKaydi> kayitlar =
            await _servis.YavasSorgularAsync(profil, vt, 500, CancellationToken.None);
        IReadOnlyList<IndexOnerisi> oneriler = MongoIndexOnerisi.Uret(kayitlar);

        ProfilBandi = $"Profiler AÇIK (slowms={slowMs}) · {kayitlar.Count} kayıt incelendi · "
                    + $"{oneriler.Count} index önerisi. Öneriler ÇALIŞTIRILMAZ — kopyalayıp kendiniz koşarsınız.";

        Ekle(ProfilSetleri, new ResultSetData
        {
            Kolonlar =
            [
                new KolonBilgisi("Koleksiyon", "", null), new KolonBilgisi("Alanlar", "", null),
                new KolonBilgisi("Kaç kez", "", null), new KolonBilgisi("Toplam ms", "", null),
                new KolonBilgisi("Önerilen komut", "", null),
            ],
            Satirlar = [.. oneriler.Select(o => new object?[]
                { o.Koleksiyon, string.Join(", ", o.Alanlar), o.KacKez, o.ToplamMs, o.Komut })],
        }, "🔍 Index önerileri (index kullanmayan yavaş sorgulardan)");

        Ekle(ProfilSetleri, await _servis.YavasSorguTablosuAsync(profil, vt, 100, CancellationToken.None),
            "🐢 Yavaş sorgular (system.profile — en yavaştan)");
    }

    private static void Ekle(ObservableCollection<SonucSetiGorunumu> hedef, ResultSetData veri, string baslik)
    {
        SonucSeti set = SonucBicimleyici.TabloyaCevir(veri);
        hedef.Add(new SonucSetiGorunumu
        {
            Set = set,
            Baslik = $"{baslik}  ({set.SatirSayisi:N0} satır)",
            BaslikGorunur = true,
        });
    }

    partial void OnSecilenVeritabaniChanged(string? value)
    {
        if (_yuklendi && _profilGetir() is { } profil)
            _ = VeritabaniBazlilariYukleAsync(profil);
    }

    public Task KapatAsync() => Task.CompletedTask; // salt okunur, kalıcı bağlantı yok
}
