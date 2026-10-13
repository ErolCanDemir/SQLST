using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Sorgu listesi satırı.</summary>
public sealed record QsSorguGorunum(QsSorgu Sorgu, string Kisa, string Nesne, string YurutmeMetni, string ToplamMetni, string OrtMetni,
    string CpuMetni, string OkumaMetni, string RozetMetni, string RozetTuru)
{
    public long QueryId => Sorgu.QueryId;
    public int PlanSayisi => Sorgu.PlanSayisi;
}

/// <summary>Plan satırı.</summary>
public sealed record QsPlanGorunum(QsPlan Plan, string YurutmeMetni, string OrtMetni, string EnYuksekMetni, string SonCalismaMetni, string DurumMetni, string DurumTuru)
{
    public long PlanId => Plan.PlanId;
}

/// <summary>
/// 📈 QUERY STORE SEKMESİ (v23-S28 — docs/14-query-store-arastirma.md, K1–K9; mockup docs/mockup/query-store.html onaylı).
/// DB'ye bağlı sekme: üstte durum (açık / kapalı / salt-okunur + nedeni / desteklemiyor / izin yok — Türkçe bant), görünüm ·
/// metrik · istatistik · pencere çipleri, sorgu listesi, altta seçili sorgunun SQL'i + planları + plan başına süre çubuğu;
/// işlemler: planı aç (Plan sekmesi), sorguyu sekmede aç, planı zorla / kaldır (onaylı), Query Store'u aç (onaylı), flush.
/// Tüm sunucu konuşması köprüden — VM saf, sahte köprüyle test edilir.
/// </summary>
public sealed partial class QueryStoreSekmesiViewModel : ObservableObject, ISekme
{
    [ObservableProperty] private bool _sabit;

    private readonly SunucuKomutu _komut;                    // DB bağlamında koşar (VeritabaniOverride = Veritabani)
    private readonly Action<string, string, string?> _sekmeAc; // (başlık, sql, db) — ÇALIŞTIRMADAN
    private readonly Action<string, string> _planAc;           // (plan XML, başlık) — Plan sekmesi
    private readonly Func<OnayIstegi, bool> _onayla;
    private readonly bool _saltOkunur;
    private bool _yukleniyor;

    public string Veritabani { get; }
    public string Baslik => $"📈 Query Store · {Veritabani}";
    public SekmeDurumu Durum => SekmeDurumu.Tamamlandi;
    public Task KapatAsync() => Task.CompletedTask;

    public QueryStoreSekmesiViewModel(string veritabani, SunucuKomutu komut, Action<string, string, string?> sekmeAc,
        Action<string, string> planAc, Func<OnayIstegi, bool> onayla, bool saltOkunur)
    {
        Veritabani = veritabani;
        _komut = komut;
        _sekmeAc = sekmeAc;
        _planAc = planAc;
        _onayla = onayla;
        _saltOkunur = saltOkunur;
    }

    private Task<QueryResult> Kos(string sql, int timeoutSn = 60) => _komut(sql, Veritabani, timeoutSn, CancellationToken.None);

    // ── durum ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurumRozeti), nameof(DurumTuru), nameof(DurumMeta), nameof(AcilebilirMi), nameof(IslemYapilabilir), nameof(FlushAcik))]
    private QsDurum? _qs;

    public string DurumRozeti => Qs switch
    {
        null => UyariVar ? "Okunamadı" : "Bağlanılıyor…",
        { Destekli: false } => "Query Store yok (sürüm)",
        { Acik: true } => "Query Store AÇIK · READ_WRITE",
        { SaltOkunur: true } => "Query Store SALT-OKUNUR",
        { Kapali: true } => "Query Store KAPALI",
        _ => "Query Store: " + Qs.Durum,
    };

    public string DurumTuru => Qs switch
    {
        { Destekli: false } => "hata",
        { Acik: true } => "ok",
        { SaltOkunur: true } => "uyari",
        _ => "gri",
    };

    public string DurumMeta => Qs is { Destekli: true, Kapali: false } q
        ? $"yakalama {q.YakalamaModu} · {q.CurMb:N0} / {q.MaxMb:N0} MB · aralık {q.AralikDk} dk · {q.SorguSayisi:N0} sorgu · {q.PlanSayisi:N0} plan"
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UyariVar), nameof(DurumRozeti))]
    private string _uyari = "";

    public bool UyariVar => Uyari.Length > 0;

    /// <summary>K2: kapalıysa ve ALTER izni varsa (salt-okunur profil hariç) "Aç" düğmesi.</summary>
    public bool AcilebilirMi => Qs is { Destekli: true, Kapali: true, AlterIzni: true } && !_saltOkunur;

    /// <summary>Plan zorla / kaldır: ALTER izni + salt-okunur değil + QS açık.</summary>
    public bool IslemYapilabilir => Qs is { Destekli: true, AlterIzni: true, Kapali: false } && !_saltOkunur;

    public bool FlushAcik => Qs is { Destekli: true, Acik: true, AlterIzni: true } && !_saltOkunur;

    [ObservableProperty] private string _bilgi = "";
    [ObservableProperty] private string _sonYenileme = "";

    // ── çipler ──

    [ObservableProperty] private QsGorunum _gorunum = QsGorunum.EnPahali;
    [ObservableProperty] private QsMetrik _metrik = QsMetrik.Sure;
    [ObservableProperty] private QsIstatistik _istatistik = QsIstatistik.Toplam;
    [ObservableProperty] private QsPencere _pencere = QsPencere.Saat24;

    partial void OnGorunumChanged(QsGorunum value) { CipleriBildir(); _ = ListeyiYukleAsync(); }
    partial void OnMetrikChanged(QsMetrik value) { CipleriBildir(); if (Gorunum == QsGorunum.EnPahali) _ = ListeyiYukleAsync(); }
    partial void OnIstatistikChanged(QsIstatistik value) { CipleriBildir(); if (Gorunum == QsGorunum.EnPahali) _ = ListeyiYukleAsync(); }
    partial void OnPencereChanged(QsPencere value) { CipleriBildir(); _ = ListeyiYukleAsync(); }

    public bool GorunumEnPahali { get => Gorunum == QsGorunum.EnPahali; set { if (value) Gorunum = QsGorunum.EnPahali; } }
    public bool GorunumGerileyen { get => Gorunum == QsGorunum.Gerileyen; set { if (value) Gorunum = QsGorunum.Gerileyen; } }
    public bool GorunumZorlanmis { get => Gorunum == QsGorunum.Zorlanmis; set { if (value) Gorunum = QsGorunum.Zorlanmis; } }
    public bool MetrikSure { get => Metrik == QsMetrik.Sure; set { if (value) Metrik = QsMetrik.Sure; } }
    public bool MetrikCpu { get => Metrik == QsMetrik.Cpu; set { if (value) Metrik = QsMetrik.Cpu; } }
    public bool MetrikOkuma { get => Metrik == QsMetrik.Okuma; set { if (value) Metrik = QsMetrik.Okuma; } }
    public bool MetrikYurutme { get => Metrik == QsMetrik.Yurutme; set { if (value) Metrik = QsMetrik.Yurutme; } }
    public bool IstatistikToplam { get => Istatistik == QsIstatistik.Toplam; set { if (value) Istatistik = QsIstatistik.Toplam; } }
    public bool IstatistikOrtalama { get => Istatistik == QsIstatistik.Ortalama; set { if (value) Istatistik = QsIstatistik.Ortalama; } }
    public bool IstatistikEnYuksek { get => Istatistik == QsIstatistik.EnYuksek; set { if (value) Istatistik = QsIstatistik.EnYuksek; } }
    public bool PencereSaat1 { get => Pencere == QsPencere.Saat1; set { if (value) Pencere = QsPencere.Saat1; } }
    public bool PencereSaat24 { get => Pencere == QsPencere.Saat24; set { if (value) Pencere = QsPencere.Saat24; } }
    public bool PencereGun7 { get => Pencere == QsPencere.Gun7; set { if (value) Pencere = QsPencere.Gun7; } }
    public bool PencereGun30 { get => Pencere == QsPencere.Gun30; set { if (value) Pencere = QsPencere.Gun30; } }
    public bool PencereTumu { get => Pencere == QsPencere.Tumu; set { if (value) Pencere = QsPencere.Tumu; } }

    /// <summary>Metrik/istatistik çipleri yalnız "En pahalı"da anlamlı.</summary>
    public bool MetrikSecilebilir => Gorunum == QsGorunum.EnPahali;
    public bool PencereSecilebilir => Gorunum != QsGorunum.Zorlanmis;

    private void CipleriBildir()
    {
        foreach (string ad in new[] { nameof(GorunumEnPahali), nameof(GorunumGerileyen), nameof(GorunumZorlanmis), nameof(MetrikSure), nameof(MetrikCpu),
            nameof(MetrikOkuma), nameof(MetrikYurutme), nameof(IstatistikToplam), nameof(IstatistikOrtalama), nameof(IstatistikEnYuksek),
            nameof(PencereSaat1), nameof(PencereSaat24), nameof(PencereGun7), nameof(PencereGun30), nameof(PencereTumu),
            nameof(MetrikSecilebilir), nameof(PencereSecilebilir), nameof(ListeNotu) })
            OnPropertyChanged(ad);
    }

    public string ListeNotu => Gorunum switch
    {
        QsGorunum.Gerileyen => $"{QueryStoreSorgulari.PencereAdi(Pencere)} ortalaması, önceki 7 güne göre ≥ %50 ve ≥ 10 ms kötüleşenler · süreler ms",
        QsGorunum.Zorlanmis => "zorlanmış planı olan sorgular · tüm zamanlar · süreler ms",
        _ => $"{QueryStoreSorgulari.PencereAdi(Pencere)} · {QueryStoreSorgulari.MetrikAdi(Metrik)} ({QueryStoreSorgulari.IstatistikAdi(Istatistik)}) sırasıyla ilk 50 · süreler ms (Query Store µs tutar)",
    };

    // ── liste ──

    public ObservableCollection<QsSorguGorunum> Sorgular { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SorguSecili), nameof(SeciliSql), nameof(SeciliBaslik), nameof(SeciliRozet), nameof(SeciliRozetTuru))]
    private QsSorguGorunum? _seciliSorgu;

    public bool SorguSecili => SeciliSorgu is not null;
    public string SeciliSql => SeciliSorgu?.Sorgu.Sql ?? "";
    public string SeciliBaslik => SeciliSorgu is { } s ? $"#{s.QueryId}" + (s.Nesne != "—" ? " · " + s.Nesne : "") : "";
    public string SeciliRozet => SeciliSorgu is { Sorgu.PlanDegisti: true, Sorgu.OncekiOrtMs: { } o } s
        ? $"plan değişti — {QueryStoreSorgulari.PencereAdi(Pencere)} ortalama {QueryStoreSorgulari.MsMetni(s.Sorgu.OrtMs)}, önceki 7 gün {QueryStoreSorgulari.MsMetni(o)} (×{(o > 0 ? s.Sorgu.OrtMs / o : 0):0.0})"
        : SeciliSorgu?.RozetMetni ?? "";
    public string SeciliRozetTuru => SeciliSorgu?.RozetTuru ?? "gri";

    partial void OnSeciliSorguChanged(QsSorguGorunum? value)
    {
        Planlar.Clear();
        SeciliPlan = null;
        PlanGrafigi = null;
        if (value is not null)
            _ = PlanlariYukleAsync(value.QueryId);
    }

    public ObservableCollection<QsPlanGorunum> Planlar { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanSecili), nameof(ZorlaMetni), nameof(KaldirAcik), nameof(ZorlaAcik), nameof(PlaniAcMetni))]
    private QsPlanGorunum? _seciliPlan;

    public bool PlanSecili => SeciliPlan is not null;
    public string PlaniAcMetni => SeciliPlan is { } p ? $"▶ Planı aç (plan {p.PlanId})" : "▶ Planı aç";
    public string ZorlaMetni => SeciliPlan is { } p ? $"📌 Planı zorla (plan {p.PlanId})" : "📌 Planı zorla";
    public bool ZorlaAcik => IslemYapilabilir && SeciliPlan is { Plan.Zorlanmis: false };
    public bool KaldirAcik => IslemYapilabilir && SeciliPlan is { Plan.Zorlanmis: true };

    [ObservableProperty] private GrafikModeli? _planGrafigi;

    // ── yükleme ──

    [RelayCommand]
    public async Task YenileAsync()
    {
        if (_yukleniyor) return;
        _yukleniyor = true;
        try
        {
            QueryResult s = await Kos(QueryStoreSorgulari.SurumSql());
            if (s.Hata is { } sh)
            {
                Qs = null;
                Uyari = "⚠ Veritabanı okunamadı: " + sh.Mesaj;
                return;
            }
            QsDurum? onDurum = QueryStoreSorgulari.DurumOku(s, null);
            QueryResult? d = null;
            if (onDurum is { Destekli: true })
            {
                d = await Kos(QueryStoreSorgulari.DurumSql());
                if (d.Hata is { } dh)
                {
                    Qs = onDurum;
                    Uyari = "⚠ Query Store durumu okunamadı: " + QueryStoreSorgulari.HataMetni(dh);
                    return;
                }
            }
            Qs = QueryStoreSorgulari.DurumOku(s, d);
            if (Qs is null) { Uyari = "⚠ Sunucu boş yanıt verdi."; return; }
            Uyari = Qs switch
            {
                { Destekli: false } => $"⚠ Bu sunucu (SQL Server iç sürüm {Qs.SurumMajor}) Query Store desteklemiyor — 2016 (13) ve üstü gerekir.",
                { OkumaIzni: false } => "🔒 VIEW DATABASE STATE izniniz yok — Query Store verisi görünmez (db_owner ya da bu izin gerekir).",
                { Kapali: true } => "⚠ Query Store bu veritabanında KAPALI. Açılınca sorgu/plan geçmişi bu andan itibaren birikir (geçmişe dönük veri yoktur). "
                    + "Önerilen ayar: READ_WRITE · yakalama AUTO · 1 GB · 60 dk aralık." + (Qs.AlterIzni ? "" : " Açmak için ALTER izni gerekir."),
                { SaltOkunur: true } => $"✖ Salt-okunur: {Qs.ReadonlyNedeni} — yeni istatistik yazılmıyor. "
                    + (Qs.ReadonlyReason == 65536 ? "Boyutu büyütün ya da temizleyin (ALTER DATABASE … SET QUERY_STORE (MAX_STORAGE_SIZE_MB = …) / CLEAR)." : ""),
                _ => "",
            };
            SonYenileme = $"yenilendi {DateTime.Now:HH:mm:ss}";
            if (Qs is { Destekli: true, OkumaIzni: true, Kapali: false })
                await ListeyiYukleAsync();
            else
            {
                Sorgular.Clear();
                SeciliSorgu = null;
                Bilgi = ""; // bayat "N sorgu" kalmasın (piksel bulgusu)
            }
        }
        finally
        {
            _yukleniyor = false;
        }
    }

    private async Task ListeyiYukleAsync()
    {
        if (Qs is not { Destekli: true, OkumaIzni: true, Kapali: false })
            return;
        string sql = Gorunum switch
        {
            QsGorunum.Gerileyen => QueryStoreSorgulari.GerilenSql(Pencere),
            QsGorunum.Zorlanmis => QueryStoreSorgulari.ZorlanmisSql(),
            _ => QueryStoreSorgulari.EnPahaliSql(Metrik, Istatistik, Pencere),
        };
        QueryResult r = await Kos(sql, 120);
        long? onceki = SeciliSorgu?.QueryId;
        Sorgular.Clear();
        if (r.Hata is { } h)
        {
            Bilgi = "⚠ Liste okunamadı: " + QueryStoreSorgulari.HataMetni(h);
            SeciliSorgu = null;
            return;
        }
        foreach (QsSorgu q in QueryStoreSorgulari.SorgulariOku(r))
            Sorgular.Add(SatirKur(q));
        Bilgi = $"{Sorgular.Count} sorgu";
        SeciliSorgu = Sorgular.FirstOrDefault(x => x.QueryId == onceki) ?? Sorgular.FirstOrDefault();
    }

    private static QsSorguGorunum SatirKur(QsSorgu q)
    {
        (string rozet, string tur) = q switch
        {
            { ZorlamaHatasi: true } => ("zorlama hatası", "hata"),
            { PlanDegisti: true } => ("plan değişti", "uyari"),
            { Zorlanmis: true } => ("zorlanmış", "ok"),
            _ => ("", "gri"),
        };
        return new QsSorguGorunum(q, QueryStoreSorgulari.KisaSql(q.Sql), q.Nesne ?? "—", QueryStoreSorgulari.SayiMetni(q.Yurutme),
            QueryStoreSorgulari.MsMetni(q.ToplamMs), QueryStoreSorgulari.MsMetni(q.OrtMs), QueryStoreSorgulari.MsMetni(q.CpuToplamMs),
            QueryStoreSorgulari.SayiMetni(q.OkumaToplam), rozet, tur);
    }

    private async Task PlanlariYukleAsync(long queryId)
    {
        QueryResult r = await Kos(QueryStoreSorgulari.PlanlarSql(queryId, Pencere));
        if (SeciliSorgu?.QueryId != queryId) return; // bayat
        Planlar.Clear();
        if (r.Hata is { } h) { Bilgi = "⚠ Planlar okunamadı: " + QueryStoreSorgulari.HataMetni(h); return; }
        IReadOnlyList<QsPlan> planlar = QueryStoreSorgulari.PlanlariOku(r);
        long? suAnki = planlar.Where(p => p.SonCalisma is not null).OrderByDescending(p => p.SonCalisma).Select(p => (long?)p.PlanId).FirstOrDefault();
        foreach (QsPlan p in planlar)
        {
            (string durum, string tur) = p switch
            {
                { ZorlamaHatasi: not null } => ("zorlama hatası: " + p.ZorlamaHatasi, "hata"),
                { Zorlanmis: true } => ("zorlanmış", "ok"),
                _ when p.PlanId == suAnki => ("şu anki", "bilgi"),
                _ => ("eski", "gri"),
            };
            Planlar.Add(new QsPlanGorunum(p, QueryStoreSorgulari.SayiMetni(p.Yurutme), p.Yurutme > 0 ? QueryStoreSorgulari.MsMetni(p.OrtMs) : "—",
                p.Yurutme > 0 ? QueryStoreSorgulari.MsMetni(p.EnYuksekMs) : "—", p.SonCalisma?.ToString("dd.MM.yyyy HH:mm") ?? "—", durum, tur));
        }
        SeciliPlan = Planlar.FirstOrDefault(p => p.PlanId == suAnki) ?? Planlar.FirstOrDefault();
        PlanGrafigi = planlar.Count == 0 ? null : new GrafikModeli(GrafikTuru.Cubuk,
            [.. planlar.Select(p => "plan " + p.PlanId)],
            [new GrafikSerisi("Ortalama süre (ms)", [.. planlar.Select(p => p.Yurutme > 0 ? Math.Round(p.OrtMs, 1) : (double?)null)])],
            planlar.Count, null);
    }

    // ── işlemler ──

    [RelayCommand]
    private void SorguyuAc()
    {
        if (SeciliSorgu is { } s)
            _sekmeAc($"qs-{s.QueryId}.sql", s.Sorgu.Sql, Veritabani);
    }

    [RelayCommand]
    private async Task PlaniAcAsync()
    {
        if (SeciliPlan is not { } p) return;
        QueryResult r = await Kos(QueryStoreSorgulari.PlanXmlSql(p.PlanId));
        string? xml = r.Hata is null ? QueryStoreSorgulari.PlanXmlOku(r) : null;
        if (string.IsNullOrEmpty(xml))
        {
            Bilgi = "⚠ Plan okunamadı: " + (r.Hata is { } h ? QueryStoreSorgulari.HataMetni(h) : "boş plan");
            return;
        }
        _planAc(xml, $"QS plan {p.PlanId} · {Veritabani}");
        Bilgi = $"Plan {p.PlanId} Plan sekmesinde açıldı.";
    }

    [RelayCommand]
    private Task ZorlaAsync() => PlanIslemiAsync(zorla: true);

    [RelayCommand]
    private Task ZorlamayiKaldirAsync() => PlanIslemiAsync(zorla: false);

    private async Task PlanIslemiAsync(bool zorla)
    {
        if (SeciliSorgu is not { } s || SeciliPlan is not { } p || !IslemYapilabilir) return;
        string sql = zorla ? QueryStoreSorgulari.ZorlaSql(s.QueryId, p.PlanId) : QueryStoreSorgulari.ZorlamaKaldirSql(s.QueryId, p.PlanId);
        var istek = zorla
            ? new OnayIstegi("Plan zorlansın mı?", $"SQL Server, #{s.QueryId} sorgusu için her zaman plan {p.PlanId}'i kullanacak (sp_query_store_force_plan). "
                + "Geri alınabilir: 'Zorlamayı kaldır'.", "📌 Planı zorla")
            : new OnayIstegi("Zorlama kaldırılsın mı?", $"#{s.QueryId} için plan {p.PlanId} zorlaması kaldırılacak; iyileştirici yeniden plan seçecek.", "✕ Zorlamayı kaldır");
        if (!_onayla(istek)) return;
        QueryResult r = await Kos(sql);
        if (r.Hata is { } h) { Bilgi = "✖ " + QueryStoreSorgulari.HataMetni(h); return; }
        await ListeyiYukleAsync();
        await PlanlariYukleAsync(s.QueryId);
        // sonuç mesajı yenilemelerden SONRA — liste "N sorgu" yazıp eziyordu (VM testi yakaladı)
        Bilgi = zorla ? $"✔ Plan {p.PlanId} zorlandı." : $"✔ Plan {p.PlanId} zorlaması kaldırıldı.";
    }

    [RelayCommand]
    private void ZorlaScriptiAl()
    {
        if (SeciliSorgu is { } s && SeciliPlan is { } p)
            _sekmeAc($"qs-plan-{p.PlanId}.sql", QueryStoreSorgulari.ZorlaSql(s.QueryId, p.PlanId) + "\n-- kaldırmak için:\n-- " + QueryStoreSorgulari.ZorlamaKaldirSql(s.QueryId, p.PlanId), Veritabani);
    }

    /// <summary>K2: Query Store'u önerilen ayarlarla aç (onaylı).</summary>
    [RelayCommand]
    private async Task AcAsync()
    {
        if (!AcilebilirMi) return;
        if (!_onayla(new OnayIstegi("Query Store açılsın mı?",
                $"[{Veritabani}] için Query Store açılacak: {QueryStoreSorgulari.OnerilenAcmaSecenekleri.Replace(", ", " · ")}. "
                + "Sorgu/plan geçmişi bu andan itibaren birikir; en çok 1 GB yer kullanır.", "▶ Query Store'u aç")))
            return;
        QueryResult r = await _komut(QueryStoreSorgulari.AcSql(Veritabani), "master", 120, CancellationToken.None);
        if (r.Hata is { } h) { Bilgi = "✖ " + QueryStoreSorgulari.HataMetni(h); return; }
        await YenileAsync();
        Bilgi = "✔ Query Store açıldı — geçmiş bu andan itibaren birikir.";
    }

    [RelayCommand]
    private void AcScriptiAl() => _sekmeAc($"query-store-ac-{Veritabani}.sql", QueryStoreSorgulari.AcSql(Veritabani), "master");

    /// <summary>K8: bellekteki istatistiği diske yaz (taze liste).</summary>
    [RelayCommand]
    private async Task FlushAsync()
    {
        if (!FlushAcik) return;
        QueryResult r = await Kos(QueryStoreSorgulari.FlushSql());
        if (r.Hata is { } h) { Bilgi = "✖ " + QueryStoreSorgulari.HataMetni(h); return; }
        await YenileAsync();
        Bilgi = "✔ Bellekteki istatistik diske yazıldı, liste tazelendi.";
    }
}
