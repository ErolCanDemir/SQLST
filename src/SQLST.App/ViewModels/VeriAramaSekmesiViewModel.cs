using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Sonuç satırı: bulunan tablonun etiketi + o tabloyu açacak SQL (çift tık köprüsü).</summary>
public sealed record VeriAramaSonucu(string Etiket, string Sql);

/// <summary>
/// Kapsam listesi öğesi (tablo seçimi). İşaretlenince VM'e haber verir; seçim süzgeç yenilense de
/// VM'deki <c>_kapsam</c> kümesinde korunur (pencere sürümündeki desen).
/// </summary>
public sealed partial class VeriAramaKapsamOgesi : ObservableObject
{
    private readonly Action<VeriAramaKapsamOgesi> _degisti;

    public VeriAramaKapsamOgesi(string ad, bool secili, Action<VeriAramaKapsamOgesi> degisti)
    {
        Ad = ad;
        _secili = secili;
        _degisti = degisti;
    }

    public string Ad { get; }

    [ObservableProperty] private bool _secili;

    partial void OnSeciliChanged(bool value) => _degisti(this);
}

/// <summary>
/// 🔎 Veri Arama SEKMESİ (kullanıcı isteği 2026-08-09: ayrı pencere yerine sekme — Kod Arama gibi):
/// değer → tüm tabloların uygun kolonlarında tablo tablo aranır (ilerlemeli, durdurulabilir);
/// bulunan tabloya çift tık → satırlar yeni sorgu sekmesinde. Sorgu üretimi SAF
/// <see cref="VeriArayici"/>'da; koşum <see cref="MainViewModel.VeriAramaSorgusuAsync"/> köprüsüyle
/// (kapsam seçiliyse süre SINIRSIZ, aksi halde tablo başı 10 sn tavan).
/// </summary>
public sealed partial class VeriAramaSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly Func<string?, Task<SemaOnbellegi?>> _onbellekGetir;
    private readonly Func<string, string, bool, CancellationToken, Task<QueryResult>> _calistir; // (db, sql, sinirli, ct)
    private readonly Action<string, string, string?> _sekmeAcVeCalistir;
    private readonly ILehce _lehce; // sorgu üretimi motor-parametrik (özellik eşitliği 2026-08-03)

    // Kapsam seçici (2026-08-03): boş küme = TÜMÜ (10 sn/tablo); doluysa yalnız seçilenler, SINIRSIZ.
    private readonly HashSet<string> _kapsam = new(StringComparer.OrdinalIgnoreCase); // TamAd
    private readonly List<string> _atlananlar = []; // son aramada süre/hata ile atlanan TamAd'lar
    private CancellationTokenSource? _cts;

    public VeriAramaSekmesiViewModel(
        IReadOnlyList<string> veritabanlari, string? aktifDb, ILehce lehce,
        Func<string?, Task<SemaOnbellegi?>> onbellekGetir,
        Func<string, string, bool, CancellationToken, Task<QueryResult>> calistir,
        Action<string, string, string?> sekmeAcVeCalistir)
    {
        _onbellekGetir = onbellekGetir;
        _calistir = calistir;
        _sekmeAcVeCalistir = sekmeAcVeCalistir;
        _lehce = lehce;
        Veritabanlari = [.. veritabanlari];
        _secilenVeritabani = aktifDb ?? Veritabanlari.FirstOrDefault();
    }

    public ObservableCollection<string> Veritabanlari { get; }

    [ObservableProperty] private string? _secilenVeritabani;

    partial void OnSecilenVeritabaniChanged(string? value) => OnPropertyChanged(nameof(Baslik));

    [ObservableProperty] private string _deger = "";

    /// <summary>Durum çubuğu metni (ISekme.Durum ile karışmasın diye ayrı ad).</summary>
    [ObservableProperty] private string _durumMetni = "Değeri yazıp Ara'ya basın.";

    public ObservableCollection<VeriAramaSonucu> Sonuclar { get; } = [];

    [ObservableProperty] private VeriAramaSonucu? _seciliSonuc;

    // ── Kapsam açılır listesi ────────────────────────────────────────────────
    public ObservableCollection<VeriAramaKapsamOgesi> KapsamOgeleri { get; } = [];

    [ObservableProperty] private string _kapsamAra = "";

    partial void OnKapsamAraChanged(string value) => _ = KapsamListesiKurAsync();

    [ObservableProperty] private bool _kapsamAcik;

    partial void OnKapsamAcikChanged(bool value)
    {
        if (value)
            _ = KapsamListesiKurAsync();
    }

    public string KapsamEtiketi => _kapsam.Count == 0
        ? "Tablolar: Tümü ▾"
        : $"Tablolar: {_kapsam.Count} seçili (SINIRSIZ) ▾";

    [ObservableProperty] private bool _atlananGorunur;

    private async Task KapsamListesiKurAsync()
    {
        SemaOnbellegi? onbellek = await _onbellekGetir(SecilenVeritabani);
        KapsamOgeleri.Clear();
        if (onbellek is null)
            return;
        string suzgec = KapsamAra.Trim();
        foreach (SemaNesnesi t in onbellek.Nesneler.Where(n => n.Tur == SemaNesneTuru.Tablo))
        {
            if (suzgec.Length > 0 && !t.TamAd.Contains(suzgec, StringComparison.OrdinalIgnoreCase))
                continue;
            KapsamOgeleri.Add(new VeriAramaKapsamOgesi(t.TamAd, _kapsam.Contains(t.TamAd), KapsamOgesiDegisti));
        }
    }

    private void KapsamOgesiDegisti(VeriAramaKapsamOgesi oge)
    {
        if (oge.Secili)
            _kapsam.Add(oge.Ad);
        else
            _kapsam.Remove(oge.Ad);
        OnPropertyChanged(nameof(KapsamEtiketi));
    }

    [RelayCommand]
    private void KapsamTemizle()
    {
        _kapsam.Clear();
        OnPropertyChanged(nameof(KapsamEtiketi));
        foreach (VeriAramaKapsamOgesi o in KapsamOgeleri)
            o.Secili = false;
    }

    /// <summary>⏱ Atlananları kapsama al + SINIRSIZ yeniden ara (ikinci tur köprüsü).</summary>
    [RelayCommand]
    private async Task AtlananAraAsync()
    {
        if (_atlananlar.Count == 0)
            return;
        _kapsam.Clear();
        foreach (string t in _atlananlar)
            _kapsam.Add(t);
        OnPropertyChanged(nameof(KapsamEtiketi));
        await AraAsync();
    }

    // ── Arama ────────────────────────────────────────────────────────────────
    private bool _araniyor;
    public bool Araniyor
    {
        get => _araniyor;
        private set
        {
            if (SetProperty(ref _araniyor, value))
            {
                OnPropertyChanged(nameof(Durum));
                AraCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool AramaMumkun => !Araniyor;

    [RelayCommand(CanExecute = nameof(AramaMumkun))]
    private async Task AraAsync()
    {
        string deger = Deger.Trim();
        if (deger.Length == 0 || SecilenVeritabani is not string db)
        {
            DurumMetni = "Aranacak değer ve veritabanı gerekli.";
            return;
        }

        SemaOnbellegi? onbellek = await _onbellekGetir(db);
        if (onbellek is null)
        {
            DurumMetni = "⚠ Şema okunamadı — bağlantıyı kontrol edin.";
            return;
        }

        // Kapsam süzgeci: seçim varsa yalnız o tablolar + SÜRE SINIRSIZ (bilinçli tercih).
        bool sinirli = _kapsam.Count == 0;
        IReadOnlyList<SemaNesnesi> tablolar = [.. onbellek.Nesneler
            .Where(n => n.Tur == SemaNesneTuru.Tablo && (sinirli || _kapsam.Contains(n.TamAd)))];
        Sonuclar.Clear();
        SeciliSonuc = null;
        _atlananlar.Clear();
        AtlananGorunur = false;
        Araniyor = true;
        _cts = new CancellationTokenSource();
        int taranan = 0, bulunanTablo = 0, atlanan = 0;
        try
        {
            // S3 (v23, K3 kararı): MSSQL'de FULLTEXT kolon haritası TEK sorguyla çekilir —
            // FTS'li tablolarda arama CONTAINS'e geçer (hız + '='in bulamadığı "uzun metnin
            // içinde geçiyor" eşleşmeleri). Harita alınamazsa (FTS'siz sunucu dahil) sessizce
            // boş kalır, eski '=' yolu birebir sürer.
            var ftsHarita = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (_lehce.MotorId == "mssql")
            {
                QueryResult harita = await _calistir(db, FtsSorgulari.KolonHaritasiSorgusu(), true, _cts.Token);
                if (harita.Hata is null)
                {
                    foreach (object?[] s in harita.ResultSetler.FirstOrDefault()?.Satirlar ?? [])
                    {
                        string tamAd = $"{s[0]}.{s[1]}";
                        if (!ftsHarita.TryGetValue(tamAd, out HashSet<string>? kolonlar))
                            ftsHarita[tamAd] = kolonlar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (s[2]?.ToString() is { Length: > 0 } kolon)
                            kolonlar.Add(kolon);
                    }
                }
            }

            foreach (SemaNesnesi tablo in tablolar)
            {
                if (_cts.IsCancellationRequested)
                    break;
                taranan++;
                DurumMetni = $"Taranıyor… {taranan}/{tablolar.Count} — {tablo.TamAd} (bulunan: {bulunanTablo})";

                ftsHarita.TryGetValue(tablo.TamAd, out HashSet<string>? ftsKolonlar);
                if (VeriArayici.SorguUret(tablo, deger, _lehce, ftsKolonlar: ftsKolonlar) is not { } sql)
                    continue; // uygun tipli kolon yok

                // Büyük-DB kontrolü: tablo başı 10 sn tavan köprüde; Durdur ÇALIŞAN sorguyu da anında
                // keser (ct geçilir). Zaman aşımı = atlanan (rapora düşer).
                QueryResult sonuc = await _calistir(db, sql, sinirli, _cts.Token);
                if (sonuc.IptalEdildi || _cts.IsCancellationRequested)
                    break;
                if (sonuc.Hata is not null)
                {
                    atlanan++;
                    _atlananlar.Add(tablo.TamAd); // ikinci turda "seçip sınırsız ara" köprüsü için
                    continue; // tek tablo hatası/zaman aşımı aramayı durdurmaz
                }
                int satir = sonuc.ResultSetler.Count > 0 ? sonuc.ResultSetler[0].Satirlar.Count : 0;
                if (satir == 0)
                    continue;

                bulunanTablo++;
                string etiket = $"{tablo.TamAd}  —  {satir}{(satir >= 100 ? "+" : "")} satır"
                    + (ftsKolonlar is { Count: > 0 } ? "  · ⚡FTS" : ""); // CONTAINS'le arandı
                Sonuclar.Add(new VeriAramaSonucu(etiket, sql));
            }

            DurumMetni = _cts.IsCancellationRequested
                ? $"■ Durduruldu — {taranan}/{tablolar.Count} tablo tarandı, {bulunanTablo} tabloda bulundu."
                : $"✔ Bitti — {tablolar.Count} tablo tarandı, {bulunanTablo} tabloda bulundu"
                  + (atlanan > 0
                      ? $" — ⏱ {atlanan} tablo {(sinirli ? "10 sn sınırını aştı/okunamadı" : "okunamadı")}"
                        + (sinirli ? " (sağdaki düğmeyle onları SINIRSIZ arayabilirsiniz)" : "")
                      : "")
                  + ". Çift tıkla satırları açın.";
            AtlananGorunur = sinirli && _atlananlar.Count > 0;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Araniyor = false;
        }
    }

    [RelayCommand]
    private void Durdur() => IptalYardimcisi.ArkaPlandaIptal(_cts); // m.15: Cancel UI'da bloklayabilir

    [RelayCommand]
    private void SonucAc()
    {
        if (SeciliSonuc is { } s)
            _sekmeAcVeCalistir("veri-arama", s.Sql, SecilenVeritabani);
    }

    // ── ISekme ───────────────────────────────────────────────────────────────
    public string Baslik => $"🔎 Veri arama · {SecilenVeritabani}";

    public SekmeDurumu Durum => Araniyor ? SekmeDurumu.Calisiyor : SekmeDurumu.Tamamlandi;

    public Task KapatAsync()
    {
        IptalYardimcisi.ArkaPlandaIptal(_cts, birak: true); // m.15: Cancel UI'da bloklayabilir
        _cts = null;
        return Task.CompletedTask;
    }
}
