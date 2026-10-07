using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Onay sorusu (v23-S20): iletişim kutusu kalın başlık + açıklama + ne olacağını söyleyen düğme gösterir;
/// <paramref name="Tehlikeli"/> ise düğme kırmızı ve varsayılan odak "Vazgeç"te. VM pencere bilmez.
/// </summary>
public sealed record OnayIstegi(string Baslik, string Mesaj, string OnayMetni, bool Tehlikeli = false);

/// <summary>Job listesi satırı — rozet metni/türü ve ham biçimli zamanlar hazır.</summary>
public sealed record AgentJobSatiri(AgentJob Job, DateTime SunucuSaati)
{
    public string Ad => Job.Ad;
    public string Kategori => Job.Kategori;
    public string Sahip => Job.Sahip;
    public bool Acik => Job.Acik;
    public bool Calisiyor => Job.Calisiyor;
    public string AcikIsareti => Job.Acik ? "✔" : "—";

    public string SonKosuMetni => Job.SonKosu is { } t ? AgentSorgulari.ZamanMetni(t) : "hiç çalışmadı";

    public string SonucMetni => Job.Calisiyor
        ? "▶ Çalışıyor" + (Job.CalisanAdim is { } a ? $" · adım {a}/{Math.Max(a, Job.AdimSayisi)}" : "")
        : Job.SonSonuc == AgentSonuc.Hata && Job.HataliAdim is { } h
            ? $"Hata · adım {h}"
            : AgentSorgulari.SonucMetni(Job.SonSonuc);

    /// <summary>Rozet rengi: ok · hata · calis · iptal · gri (XAML DataTrigger'ları buna bakar).</summary>
    public string SonucTuru => Job.Calisiyor ? "calis" : Job.SonSonuc switch
    {
        AgentSonuc.Basarili => "ok",
        AgentSonuc.Hata => "hata",
        AgentSonuc.Iptal or AgentSonuc.YenidenDeneme => "iptal",
        AgentSonuc.Suruyor => "calis",
        _ => "gri",
    };

    public string SureMetni => Job.Calisiyor
        ? Job.CalismaBaslangici is { } b ? AgentSorgulari.SureMetni(SunucuSaati - b) + "…" : "…"
        : Job.SonSure is { } s ? AgentSorgulari.SureMetni(s) : "—";

    public string SonrakiMetni => !Job.Acik ? "— (kapalı)"
        : !Job.ZamanlamaVar ? "— (zamanlama yok)"
        : Job.SonrakiKosu is { } t ? AgentSorgulari.ZamanMetni(t) : "—";
}

/// <summary>Geçmiş sekmesi satırı.</summary>
public sealed record AgentGecmisGorunum(AgentGecmisSatiri Satir)
{
    public string ZamanMetni => AgentSorgulari.ZamanMetni(Satir.Zaman);
    public string AdimMetni => Satir.AdimId == 0 ? "(job)" : $"{Satir.AdimId} · {Satir.AdimAdi}";
    public string SonucMetni => AgentSorgulari.SonucMetni(Satir.Sonuc);

    public string SonucTuru => Satir.Sonuc switch
    {
        AgentSonuc.Basarili => "ok",
        AgentSonuc.Hata => "hata",
        AgentSonuc.Suruyor => "calis",
        _ => "iptal",
    };

    public string SureMetni => AgentSorgulari.SureMetni(Satir.Sure);
    public string TekSatirMesaj => Satir.Mesaj.ReplaceLineEndings(" ");
    public string Mesaj => Satir.Mesaj;
}

/// <summary>Adımlar sekmesi satırı.</summary>
public sealed record AgentAdimGorunum(AgentAdim Adim)
{
    public int Id => Adim.Id;
    public string Ad => Adim.Ad;
    public string Tur => Adim.AltSistem.Equals("TSQL", StringComparison.OrdinalIgnoreCase) ? "T-SQL" : Adim.AltSistem;
    public string Veritabani => Adim.Veritabani ?? "—";
    public string BasaridaMetni => AgentSorgulari.EylemMetni(Adim.BasaridaEylem, Adim.BasaridaAdim);

    public string HatadaMetni => AgentSorgulari.EylemMetni(Adim.HatadaEylem, Adim.HatadaAdim)
        + (Adim.YenidenDeneme > 0 ? $" · {Adim.YenidenDeneme} deneme / {Adim.DenemeAraligi} dk" : "");

    public string Komut => Adim.Komut;
    public bool TsqlMi => Tur == "T-SQL";
}

/// <summary>Zamanlamalar sekmesi satırı — Türkçe cümleyle.</summary>
public sealed record AgentZamanlamaGorunum(AgentZamanlama Zamanlama)
{
    public string Ad => Zamanlama.Ad;
    public string AcikIsareti => Zamanlama.Acik ? "✔" : "—";
    public string Metin => AgentSorgulari.ZamanlamaMetni(Zamanlama);
}

/// <summary>Liste süzgeci (kapsül çipler).</summary>
public enum AgentSuzgeci
{
    Tumu,
    Hatali,
    Calisan,
    Kapali,
}

/// <summary>
/// ⏱ SQL AGENT SEKMESİ (v23-S16 — araştırma: docs/11-sql-agent-arastirma.md; kararlar K1–K6;
/// mockup: docs/mockup/sql-agent.html, kullanıcı onaylı). Üstte Agent durumu (çalışıyor / durmuş /
/// bu sürümde yok / yetki yok — her biri Türkçe yönlendirmeyle), ortada SSMS "Job Activity
/// Monitor" karşılığı liste + süzgeç çipleri, altta seçili job'un Geçmiş/Adımlar/Zamanlamalar'ı.
/// İşlemler (S2): başlat (baştan ya da seçili adımdan) · durdur · aç/kapat — hepsi ONAYLI ve
/// salt-okunur bağlantıda kapalı · "Script olarak al" (çalıştırmadan sekmeye). Tüm sunucu
/// konuşması msdb köprüsünden (delege) — VM saf, testte sahte köprüyle koşar.
/// </summary>
public sealed partial class AgentSekmesiViewModel : ObservableObject, ISekme
{
    [ObservableProperty] private bool _sabit;

    private readonly Func<string, CancellationToken, Task<QueryResult>> _calistir; // msdb'de
    private readonly Action<string, string, string?> _sekmeAc;                    // ÇALIŞTIRMADAN
    private readonly Func<OnayIstegi, bool> _onayla;
    private readonly bool _saltOkunur;

    private List<AgentJob> _tumJoblar = [];
    private IReadOnlyList<AgentGecmisSatiri> _tumGecmis = [];
    private DateTime _sunucuSaati = DateTime.Now;
    private bool _yukleniyor;
    private CancellationTokenSource? _otoYenileCts;

    /// <summary>Otomatik yenileme aralığı (sn) — testler kısaltabilsin diye alan.</summary>
    public int OtoYenilemeSn { get; set; } = 10;

    /// <summary>Başlat/Durdur sonrası Agent'ın durumu yazması için beklenen süre (ms).</summary>
    public int IslemSonrasiBeklemeMs { get; set; } = 1500;

    public AgentSekmesiViewModel(
        Func<string, CancellationToken, Task<QueryResult>> calistir,
        Action<string, string, string?> sekmeAc,
        Func<OnayIstegi, bool> onayla,
        bool saltOkunur)
    {
        _calistir = calistir;
        _sekmeAc = sekmeAc;
        _onayla = onayla;
        _saltOkunur = saltOkunur;
    }

    // ── durum ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurumRozeti), nameof(DurumTuru), nameof(SunucuBilgisi), nameof(IslemYapilabilir),
        nameof(BaslatAcik), nameof(DurdurAcik))]
    private AgentSunucuDurumu? _durum;

    public string DurumRozeti => Durum?.Servis switch
    {
        AgentServisi.Calisiyor => "Agent çalışıyor",
        AgentServisi.Durmus => "Agent durmuş",
        AgentServisi.Yok => "Agent yok",
        AgentServisi.Bilinmiyor => "Agent durumu bilinmiyor",
        _ => UyariVar ? "Okunamadı" : "Bağlanılıyor…",
    };

    /// <summary>ok · uyari · hata · gri — rozet rengi.</summary>
    public string DurumTuru => Durum?.Servis switch
    {
        AgentServisi.Calisiyor => "ok",
        AgentServisi.Durmus => "uyari",
        AgentServisi.Yok => "hata",
        _ => "gri",
    };

    public string SunucuBilgisi => Durum is { } d ? $"{d.Sunucu} · {d.Surum} · yetki: {d.YetkiMetni}" : "";

    /// <summary>Büyük yönlendirme bandı (Agent yok / durmuş / yetki yok / okunamadı); boşsa gizli.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UyariVar), nameof(DurumRozeti))]
    private string _uyari = "";

    public bool UyariVar => Uyari.Length > 0;

    /// <summary>İşlem sonucu / bilgi satırı (durum çubuğunda).</summary>
    [ObservableProperty] private string _bilgi = "";

    [ObservableProperty] private string _sonYenileme = "";

    // ── liste + süzgeç ───────────────────────────────────────────────────────

    public ObservableCollection<AgentJobSatiri> Joblar { get; } = [];

    [ObservableProperty] private AgentSuzgeci _suzgec = AgentSuzgeci.Tumu;

    partial void OnSuzgecChanged(AgentSuzgeci value)
    {
        ListeyiKur();
        OnPropertyChanged(nameof(SuzgecTumu));
        OnPropertyChanged(nameof(SuzgecHatali));
        OnPropertyChanged(nameof(SuzgecCalisan));
        OnPropertyChanged(nameof(SuzgecKapali));
    }

    // Kapsül çipler (RadioButton) için iki yönlü bağlanabilir uçlar.
    public bool SuzgecTumu { get => Suzgec == AgentSuzgeci.Tumu; set { if (value) Suzgec = AgentSuzgeci.Tumu; } }
    public bool SuzgecHatali { get => Suzgec == AgentSuzgeci.Hatali; set { if (value) Suzgec = AgentSuzgeci.Hatali; } }
    public bool SuzgecCalisan { get => Suzgec == AgentSuzgeci.Calisan; set { if (value) Suzgec = AgentSuzgeci.Calisan; } }
    public bool SuzgecKapali { get => Suzgec == AgentSuzgeci.Kapali; set { if (value) Suzgec = AgentSuzgeci.Kapali; } }

    [ObservableProperty] private string _aramaMetni = "";

    partial void OnAramaMetniChanged(string value) => ListeyiKur();

    public ObservableCollection<string> Kategoriler { get; } = [TumKategoriler];

    private const string TumKategoriler = "Tümü";

    [ObservableProperty] private string _seciliKategori = TumKategoriler;

    partial void OnSeciliKategoriChanged(string value) => ListeyiKur();

    [ObservableProperty] private int _tumSayisi;
    [ObservableProperty] private int _hataliSayisi;
    [ObservableProperty] private int _calisanSayisi;
    [ObservableProperty] private int _kapaliSayisi;

    public string DurumCubugu => $"⏱ {TumSayisi} job · {CalisanSayisi} çalışıyor · {HataliSayisi} hatalı";

    // ── seçili job + detay ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JobSecili), nameof(SeciliJobAciklama), nameof(AcKapatMetni),
        nameof(BaslatAcik), nameof(DurdurAcik))]
    private AgentJobSatiri? _seciliJob;

    public bool JobSecili => SeciliJob is not null;

    public string SeciliJobAciklama => SeciliJob?.Job.Aciklama is { Length: > 0 } a
        && a != "No description available." ? "· " + a : "";

    public string AcKapatMetni => SeciliJob?.Acik == false ? "▶ Aç" : "⏸ Kapat";

    private Guid? _detayJobId;

    partial void OnSeciliJobChanged(AgentJobSatiri? value)
    {
        TamMesaj = "";
        if (value is null)
        {
            Gecmis.Clear();
            Adimlar.Clear();
            Zamanlamalar.Clear();
            _detayJobId = null;
            return;
        }
        GecmisiKur(value.Job.JobId);
        if (_detayJobId != value.Job.JobId)
            _ = DetayYukleAsync(value.Job.JobId);
    }

    public ObservableCollection<AgentGecmisGorunum> Gecmis { get; } = [];
    public ObservableCollection<AgentAdimGorunum> Adimlar { get; } = [];
    public ObservableCollection<AgentZamanlamaGorunum> Zamanlamalar { get; } = [];

    [ObservableProperty] private AgentGecmisGorunum? _seciliGecmis;

    partial void OnSeciliGecmisChanged(AgentGecmisGorunum? value) => TamMesaj = value?.Mesaj ?? "";

    [ObservableProperty] private string _tamMesaj = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeciliAdimdanMetni))]
    private AgentAdimGorunum? _seciliAdim;

    public string SeciliAdimdanMetni => SeciliAdim is { } a ? $"Seçili adımdan başlat: {a.Id} · {a.Ad}" : "Seçili adımdan başlat (Adımlar'dan seçin)";

    /// <summary>Alt sekme: 0 Geçmiş · 1 Adımlar · 2 Zamanlamalar.</summary>
    [ObservableProperty] private int _altSekme;

    // ── yükleme ──────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task YenileAsync()
    {
        if (_yukleniyor)
            return;
        _yukleniyor = true;
        try
        {
            QueryResult d = await _calistir(AgentSorgulari.DurumSorgusu(), CancellationToken.None);
            if (d.Hata is { } dh)
            {
                Durum = null;
                Uyari = $"⚠ msdb okunamadı: {dh.Mesaj}";
                return;
            }
            Durum = AgentSorgulari.DurumOku(d);
            if (Durum is null)
            {
                Uyari = "⚠ Agent durumu okunamadı (sunucu boş yanıt verdi).";
                return;
            }
            _sunucuSaati = Durum.SunucuSaati;

            if (!Durum.GorebilirMi)
            {
                TumunuTemizle();
                Uyari = "🔒 Job'ları görme yetkiniz yok. msdb'de SQLAgentReaderRole (tümü), SQLAgentUserRole "
                    + "(yalnız kendi job'larınız) ya da sysadmin gerekir — \"job yok\" değil, görünmüyor.";
                return;
            }
            Uyari = Durum.Servis switch
            {
                AgentServisi.Yok => "⚠ Bu sunucuda SQL Server Agent yok. Express ve LocalDB sürümlerinde Agent servisi "
                    + "bulunmaz (Azure SQL Database'de de yok); job tanımlanabilir ama hiç çalışmaz. Zamanlanmış iş için "
                    + "Standard/Developer sürümü ya da Windows Görev Zamanlayıcı gerekir.",
                AgentServisi.Durmus => "⏸ Agent servisi durmuş. Job'lar görüntülenir ama zamanı gelen hiçbir job çalışmaz; "
                    + "Başlat kapalıdır. Servisi SQL Server Configuration Manager'dan başlatın.",
                _ => Durum.HepsiniGorurMu ? "" : "ℹ SQLAgentUserRole: yalnız sahibi olduğunuz job'lar görünür.",
            };

            QueryResult jobs = await _calistir(AgentSorgulari.JobListesiSorgusu(), CancellationToken.None);
            if (jobs.Hata is { } jh)
            {
                Uyari = $"⚠ Job listesi okunamadı: {AgentSorgulari.HataMetni(jh)}";
                return;
            }
            QueryResult gecmis = await _calistir(AgentSorgulari.GecmisSorgusu(), CancellationToken.None);
            _tumGecmis = gecmis.Hata is null ? AgentSorgulari.GecmisOku(gecmis) : [];
            QueryResult aktivite = await _calistir(AgentSorgulari.AktiviteSorgusu(), CancellationToken.None);
            _tumJoblar = [.. AgentSorgulari.JoblariKur(jobs, _tumGecmis, aktivite.Hata is null ? aktivite : null)];

            string seciliKategori = SeciliKategori;
            Kategoriler.Clear();
            Kategoriler.Add(TumKategoriler);
            foreach (string k in _tumJoblar.Select(j => j.Kategori).Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
                Kategoriler.Add(k);
            SeciliKategori = Kategoriler.Contains(seciliKategori) ? seciliKategori : TumKategoriler;

            ListeyiKur();
            SonYenileme = "Son yenileme " + _sunucuSaati.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            _yukleniyor = false;
        }
    }

    private void TumunuTemizle()
    {
        _tumJoblar = [];
        _tumGecmis = [];
        ListeyiKur();
    }

    /// <summary>Süzgeç + arama + kategoriyi uygular; seçili job'u (id ile) korur.</summary>
    private void ListeyiKur()
    {
        Guid? secili = SeciliJob?.Job.JobId;
        TumSayisi = _tumJoblar.Count;
        HataliSayisi = _tumJoblar.Count(HataliMi);
        CalisanSayisi = _tumJoblar.Count(j => j.Calisiyor);
        KapaliSayisi = _tumJoblar.Count(j => !j.Acik);
        OnPropertyChanged(nameof(DurumCubugu));

        IEnumerable<AgentJob> sorgu = Suzgec switch
        {
            AgentSuzgeci.Hatali => _tumJoblar.Where(HataliMi),
            AgentSuzgeci.Calisan => _tumJoblar.Where(j => j.Calisiyor),
            AgentSuzgeci.Kapali => _tumJoblar.Where(j => !j.Acik),
            _ => _tumJoblar,
        };
        if (AramaMetni.Trim() is { Length: > 0 } ara)
            sorgu = sorgu.Where(j => j.Ad.Contains(ara, StringComparison.CurrentCultureIgnoreCase));
        if (SeciliKategori != TumKategoriler)
            sorgu = sorgu.Where(j => j.Kategori == SeciliKategori);

        Joblar.Clear();
        foreach (AgentJob j in sorgu)
            Joblar.Add(new AgentJobSatiri(j, _sunucuSaati));
        SeciliJob = Joblar.FirstOrDefault(j => j.Job.JobId == secili);
    }

    private static bool HataliMi(AgentJob j) => !j.Calisiyor && j.SonSonuc == AgentSonuc.Hata;

    private void GecmisiKur(Guid jobId)
    {
        Gecmis.Clear();
        foreach (AgentGecmisSatiri g in _tumGecmis.Where(g => g.JobId == jobId))
            Gecmis.Add(new AgentGecmisGorunum(g));
    }

    private async Task DetayYukleAsync(Guid jobId)
    {
        _detayJobId = jobId;
        Adimlar.Clear();
        Zamanlamalar.Clear();
        QueryResult adim = await _calistir(AgentSorgulari.AdimSorgusu(jobId), CancellationToken.None);
        QueryResult zaman = await _calistir(AgentSorgulari.ZamanlamaSorgusu(jobId), CancellationToken.None);
        if (_detayJobId != jobId)
            return; // bu arada başka job seçildi
        foreach (AgentAdim a in AgentSorgulari.AdimlariOku(adim))
            Adimlar.Add(new AgentAdimGorunum(a));
        foreach (AgentZamanlama z in AgentSorgulari.ZamanlamalariOku(zaman))
            Zamanlamalar.Add(new AgentZamanlamaGorunum(z));
        if (adim.Hata is { } h)
            Bilgi = $"⚠ Adımlar okunamadı: {AgentSorgulari.HataMetni(h)}";
    }

    // ── otomatik yenileme ────────────────────────────────────────────────────

    [ObservableProperty] private bool _otomatikYenile;

    partial void OnOtomatikYenileChanged(bool value)
    {
        _otoYenileCts?.Cancel();
        _otoYenileCts = null;
        if (!value)
            return;
        _otoYenileCts = new CancellationTokenSource();
        _ = OtoYenileDonguAsync(_otoYenileCts.Token);
    }

    /// <summary>PeriodicTimer'a jeton VERİLMEZ (iptalde fırlatır — Profiler'daki bilinen tuzak); iptal Dispose ile.</summary>
    private async Task OtoYenileDonguAsync(CancellationToken ct)
    {
        using var tik = new PeriodicTimer(TimeSpan.FromSeconds(OtoYenilemeSn));
        await using CancellationTokenRegistration kayit = ct.Register(tik.Dispose);
        while (await tik.WaitForNextTickAsync())
        {
            if (ct.IsCancellationRequested)
                return;
            await YenileAsync();
        }
    }

    // ── S2: işlemler ─────────────────────────────────────────────────────────

    /// <summary>Başlat/Durdur sunucuya gidebilir mi: salt-okunur değil + Agent yok/durmuş değil
    /// (durum bilinmiyorsa denenir — sunucu dürüst hatayı verir).</summary>
    public bool IslemYapilabilir => !_saltOkunur && Durum?.Servis is AgentServisi.Calisiyor or AgentServisi.Bilinmiyor;

    public bool BaslatAcik => IslemYapilabilir && SeciliJob is { Calisiyor: false };

    public bool DurdurAcik => IslemYapilabilir && SeciliJob is { Calisiyor: true };

    public bool AcKapatAcik => !_saltOkunur;

    [RelayCommand]
    public Task BaslatAsync()
    {
        AgentAdimGorunum? ilk = Adimlar.FirstOrDefault(a => a.Id == SeciliJob?.Job.BaslangicAdimi) ?? Adimlar.FirstOrDefault();
        return BaslatIcAsync(null, ilk is null ? "" : $" (baştan — {ilk.Id}. adım: {ilk.Ad})");
    }

    [RelayCommand]
    public Task SeciliAdimdanBaslatAsync()
    {
        if (SeciliAdim is not { } a)
        {
            Bilgi = "Önce Adımlar sekmesinden başlanacak adımı seçin.";
            return Task.CompletedTask;
        }
        return BaslatIcAsync(a.Ad, $" ({a.Id}. adımdan: {a.Ad})");
    }

    private async Task BaslatIcAsync(string? adimAdi, string aciklama)
    {
        if (!IslemKapisi(out AgentJob? job) || job is null)
            return;
        if (job.Calisiyor)
        {
            Bilgi = "Job zaten çalışıyor.";
            return;
        }
        if (!_onayla(new OnayIstegi("Job başlatılsın mı?", $"\"{job.Ad}\" şimdi başlatılacak{aciklama}.", "▶ Başlat")))
            return;
        await IslemCalistirAsync(AgentSorgulari.BaslatSql(job.JobId, adimAdi), $"▶ \"{job.Ad}\" başlatıldı{aciklama}.");
    }

    [RelayCommand]
    public async Task DurdurAsync()
    {
        if (!IslemKapisi(out AgentJob? job) || job is null)
            return;
        if (!job.Calisiyor)
        {
            Bilgi = "Job şu an çalışmıyor — durdurulacak bir koşu yok.";
            return;
        }
        if (!_onayla(new OnayIstegi("Job durdurulsun mu?",
                $"Çalışan \"{job.Ad}\" durdurulacak — sürmekte olan adım iptal edilir.", "■ Durdur", Tehlikeli: true)))
            return;
        await IslemCalistirAsync(AgentSorgulari.DurdurSql(job.JobId), $"■ \"{job.Ad}\" için durdurma istendi.");
    }

    /// <summary>Aç/kapat Agent servisi DURMUŞKEN de çalışır (yalnız msdb tanımı değişir).</summary>
    [RelayCommand]
    public async Task AcKapatAsync()
    {
        if (SeciliJob?.Job is not { } job)
            return;
        if (_saltOkunur)
        {
            Bilgi = "🔒 Salt-okunur bağlantı: job açma/kapatma kapalı.";
            return;
        }
        bool yeni = !job.Acik;
        OnayIstegi soru = yeni
            ? new OnayIstegi("Job açılsın mı?", $"\"{job.Ad}\" açılacak — zamanlaması yeniden devreye girer.", "▶ Aç")
            : new OnayIstegi("Job kapatılsın mı?", $"\"{job.Ad}\" kapatılacak — zamanı gelince ÇALIŞMAZ (elle başlatılabilir).", "⏸ Kapat");
        if (!_onayla(soru))
            return;
        await IslemCalistirAsync(AgentSorgulari.AcKapatSql(job.JobId, yeni),
            yeni ? $"✔ \"{job.Ad}\" açıldı." : $"⏸ \"{job.Ad}\" kapatıldı.", bekle: false);
    }

    private bool IslemKapisi(out AgentJob? job)
    {
        job = SeciliJob?.Job;
        if (job is null)
            return false;
        if (_saltOkunur)
        {
            Bilgi = "🔒 Salt-okunur bağlantı: job başlatma/durdurma kapalı.";
            return false;
        }
        if (Durum?.Servis is AgentServisi.Yok or AgentServisi.Durmus)
        {
            Bilgi = Durum.Servis == AgentServisi.Yok
                ? "Bu sunucuda Agent yok — job çalıştırılamaz."
                : "Agent servisi durmuş — önce servisi başlatın.";
            return false;
        }
        return true;
    }

    private async Task IslemCalistirAsync(string sql, string basariMetni, bool bekle = true)
    {
        QueryResult r = await _calistir(sql, CancellationToken.None);
        if (r.Hata is { } h)
        {
            Bilgi = "⚠ " + AgentSorgulari.HataMetni(h);
            return;
        }
        Bilgi = basariMetni;
        if (bekle && IslemSonrasiBeklemeMs > 0)
            await Task.Delay(IslemSonrasiBeklemeMs); // Agent'ın aktiviteyi yazmasına fırsat
        await YenileAsync();
    }

    /// <summary>📜 Script olarak al: job'u yeniden oluşturan script — ÇALIŞTIRMADAN sekmeye (msdb).</summary>
    [RelayCommand]
    public async Task ScriptOlarakAlAsync()
    {
        if (SeciliJob?.Job is not { } job)
            return;
        if (_detayJobId != job.JobId || (Adimlar.Count == 0 && job.AdimSayisi > 0))
            await DetayYukleAsync(job.JobId);
        string script = AgentSorgulari.OlusturmaScripti(
            job, [.. Adimlar.Select(a => a.Adim)], [.. Zamanlamalar.Select(z => z.Zamanlama)],
            Durum?.Sunucu ?? "?", _sunucuSaati);
        _sekmeAc($"agent-{job.Ad}", script, "msdb");
        Bilgi = $"📜 \"{job.Ad}\" script'i yeni sekmede açıldı (çalıştırılmadı).";
    }

    // ── S3: sihirbaz (yeni · düzenle · kopya) ────────────────────────────────

    /// <summary>Sihirbaz penceresini açan köprü — MainViewModel bağlar (VM pencere bilmez).</summary>
    public Action<AgentSihirbazIstegi>? SihirbazIste { get; set; }

    private IReadOnlyList<string> KategoriAdlari() => [.. _tumJoblar.Select(j => j.Kategori).Distinct()];

    [RelayCommand]
    public void YeniJob()
        => SihirbazIste?.Invoke(new AgentSihirbazIstegi(AgentSihirbazKipi.Yeni, null, [], [], KategoriAdlari(),
            Durum?.Giris ?? "", Durum?.Sunucu ?? "?"));

    [RelayCommand]
    public Task JobDuzenleAsync() => SihirbazIcinAsync(AgentSihirbazKipi.Duzenle);

    [RelayCommand]
    public Task JobKopyalaAsync() => SihirbazIcinAsync(AgentSihirbazKipi.Kopya);

    private async Task SihirbazIcinAsync(AgentSihirbazKipi kip)
    {
        if (SeciliJob?.Job is not { } job)
            return;
        if (_detayJobId != job.JobId || (Adimlar.Count == 0 && job.AdimSayisi > 0))
            await DetayYukleAsync(job.JobId);
        SihirbazIste?.Invoke(new AgentSihirbazIstegi(kip, job, [.. Adimlar.Select(a => a.Adim)],
            [.. Zamanlamalar.Select(z => z.Zamanlama)], KategoriAdlari(), Durum?.Giris ?? "", Durum?.Sunucu ?? "?"));
    }

    /// <summary>Seçili T-SQL adımının komutunu kendi veritabanında sorgu sekmesinde açar.</summary>
    [RelayCommand]
    public void AdimiSekmedeAc()
    {
        if (SeciliAdim is not { } a)
            return;
        if (!a.TsqlMi)
        {
            Bilgi = $"Bu adım {a.Tur} — yalnız T-SQL adımları sorgu sekmesinde açılır.";
            return;
        }
        _sekmeAc($"agent-adim-{a.Ad}", a.Komut, a.Adim.Veritabani);
    }

    // ── ISekme ───────────────────────────────────────────────────────────────
    public string Baslik => "⏱ SQL Agent";

    // ISekme.Durum açık uygulama: VM'in kendi Durum'u Agent sunucu durumudur.
    SekmeDurumu ISekme.Durum => SekmeDurumu.Tamamlandi;

    public Task KapatAsync()
    {
        OtomatikYenile = false;
        return Task.CompletedTask;
    }
}
