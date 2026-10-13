using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

public enum GuvenlikGorunumu { Loginler, Kullanicilar }
public enum LoginSuzgeci { Tumu, Sql, Windows, Kapali, Sorunlu }
public enum KullaniciSuzgeci { Tumu, Yetim, Rolsuz }

/// <summary>Login satırı.</summary>
public sealed record LoginGorunum(Login Login, string DurumMetni, string DurumTuru, string RollerMetni, string ParolaMetni, string DbMetni)
{
    public string Ad => Login.Ad;
    public string Tur => Login.Tur;
    public string VarsayilanDb => Login.VarsayilanDb;
    public int Oturum => Login.OturumSayisi;
    public bool Sysadmin => Login.SunucuRolleri.Contains("sysadmin");
}

/// <summary>Kullanıcı satırı.</summary>
public sealed record KullaniciGorunum(Kullanici Kullanici, string LoginMetni, string LoginTuru, string RollerMetni)
{
    public string Ad => Kullanici.Ad;
    public string Tur => Kullanici.Tur;
    public string Kimlik => Kullanici.Kimlik;
    public string Sema => Kullanici.Sema;
    public bool Yetim => Kullanici.Yetim;
}

/// <summary>Rol çipi: tıklayınca ekle/çıkar (onaylı). Tehlikeli roller kırmızı (K5).</summary>
public sealed partial class RolCipi : ObservableObject
{
    public required string Ad { get; init; }
    public bool Tehlikeli => GuvenlikSorgulari.TehlikeliRol(Ad);
    [ObservableProperty] private bool _uye;
}

/// <summary>Eşleme satırı (login → DB kullanıcı).</summary>
public sealed record EslemeGorunum(string Veritabani, string Kullanici, string Roller, bool KullaniciVar)
{
    public string KullaniciMetni => KullaniciVar ? Kullanici : "kullanıcı yok";
}

/// <summary>Yetki satırı.</summary>
public sealed record YetkiGorunum(Yetki Yetki)
{
    public string Sinif => Yetki.Sinif switch { "OBJECT_OR_COLUMN" => "Nesne", "SCHEMA" => "Şema", "DATABASE" => "Veritabanı", "SERVER" => "Sunucu", _ => Yetki.Sinif };
    public string Nesne => Yetki.Nesne;
    public string Izin => Yetki.Izin;
    public string Durum => Yetki.Durum switch { "GRANT_WITH_GRANT_OPTION" => "GRANT (devredebilir)", _ => Yetki.Durum };
    public string DurumTuru => Yetki.Deny ? "hata" : "ok";
    public string Veren => Yetki.Veren;
}

/// <summary>
/// 🔐 GÜVENLİK SEKMESİ (v23-S29 S1 — docs/15-guvenlik-arastirma.md, K1–K9; mockup docs/mockup/guvenlik.html onaylı).
/// Sunucu düzeyi tek sekme: Login'ler (sunucu) / Kullanıcılar (seçili DB); detayda rol çipleri (tık → onay → ALTER ROLE),
/// açık yetkiler, DB eşlemeleri; işlemler: login aç/kapat, rol ekle-çıkar, kullanıcı oluştur, yetim bağla — hepsi onaylı ve
/// "Script olarak al". Tüm sunucu konuşması köprüden; VM saf, sahte köprüyle test edilir.
/// </summary>
public sealed partial class GuvenlikSekmesiViewModel : ObservableObject, ISekme
{
    [ObservableProperty] private bool _sabit;

    private readonly SunucuKomutu _komut;
    private readonly Action<string, string, string?> _sekmeAc;
    private readonly Func<OnayIstegi, bool> _onayla;
    private readonly bool _saltOkunur;
    private readonly string _sunucuAdi;
    private bool _yukleniyor;

    private IReadOnlyList<Login> _tumLoginler = [];
    private IReadOnlyDictionary<string, List<LoginEslemesi>> _eslemeler = new Dictionary<string, List<LoginEslemesi>>();
    private IReadOnlyList<Kullanici> _tumKullanicilar = [];
    private IReadOnlyList<string> _sunucuRolAdlari = GuvenlikSorgulari.SabitSunucuRolleri;
    private IReadOnlyList<string> _dbRolAdlari = [];

    public string Baslik => $"🔐 Güvenlik · {_sunucuAdi}";
    public SekmeDurumu Durum => SekmeDurumu.Tamamlandi;
    public Task KapatAsync() => Task.CompletedTask;

    public GuvenlikSekmesiViewModel(string sunucuAdi, SunucuKomutu komut, Action<string, string, string?> sekmeAc,
        Func<OnayIstegi, bool> onayla, bool saltOkunur)
    {
        _sunucuAdi = sunucuAdi;
        _komut = komut;
        _sekmeAc = sekmeAc;
        _onayla = onayla;
        _saltOkunur = saltOkunur;
    }

    private Task<QueryResult> Sunucu(string sql) => _komut(sql, "master", 60, CancellationToken.None);
    private Task<QueryResult> Db(string sql) => _komut(sql, SeciliDb, 60, CancellationToken.None);

    // ── durum ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurumRozeti), nameof(DurumTuru), nameof(IslemYapilabilir))]
    private GuvenlikOzet? _ozet;

    public string DurumRozeti => Ozet switch
    {
        null => UyariVar ? "Okunamadı" : "Bağlanılıyor…",
        { KarmaKimlik: true } => "Karma kimlik (SQL + Windows)",
        _ => "Yalnız Windows kimliği",
    };

    public string DurumTuru => Ozet is null ? "gri" : "ok";

    [ObservableProperty] private string _meta = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UyariVar), nameof(DurumRozeti))]
    private string _uyari = "";

    public bool UyariVar => Uyari.Length > 0;

    /// <summary>K3/K6/K7: işlemler yalnız izin varsa ve salt-okunur değilse.</summary>
    public bool IslemYapilabilir => Ozet is { } o && (o.LoginDegistirir || o.Sysadmin) && !_saltOkunur;

    [ObservableProperty] private string _bilgi = "";
    [ObservableProperty] private string _sonYenileme = "";
    [ObservableProperty] private string _durumCubugu = "";

    // ── görünüm / süzgeç ──

    [ObservableProperty] private GuvenlikGorunumu _gorunum = GuvenlikGorunumu.Loginler;
    [ObservableProperty] private LoginSuzgeci _loginSuzgeci = LoginSuzgeci.Tumu;
    [ObservableProperty] private KullaniciSuzgeci _kullaniciSuzgeci = KullaniciSuzgeci.Tumu;
    [ObservableProperty] private string _aramaMetni = "";
    public ObservableCollection<string> Veritabanlari { get; } = [];
    [ObservableProperty] private string _seciliDb = "";

    public bool GorunumLoginler { get => Gorunum == GuvenlikGorunumu.Loginler; set { if (value) Gorunum = GuvenlikGorunumu.Loginler; } }
    public bool GorunumKullanicilar { get => Gorunum == GuvenlikGorunumu.Kullanicilar; set { if (value) Gorunum = GuvenlikGorunumu.Kullanicilar; } }
    public bool SuzgecTumu { get => LoginSuzgeci == LoginSuzgeci.Tumu; set { if (value) LoginSuzgeci = LoginSuzgeci.Tumu; } }
    public bool SuzgecSql { get => LoginSuzgeci == LoginSuzgeci.Sql; set { if (value) LoginSuzgeci = LoginSuzgeci.Sql; } }
    public bool SuzgecWindows { get => LoginSuzgeci == LoginSuzgeci.Windows; set { if (value) LoginSuzgeci = LoginSuzgeci.Windows; } }
    public bool SuzgecKapali { get => LoginSuzgeci == LoginSuzgeci.Kapali; set { if (value) LoginSuzgeci = LoginSuzgeci.Kapali; } }
    public bool SuzgecSorunlu { get => LoginSuzgeci == LoginSuzgeci.Sorunlu; set { if (value) LoginSuzgeci = LoginSuzgeci.Sorunlu; } }
    public bool KSuzgecTumu { get => KullaniciSuzgeci == KullaniciSuzgeci.Tumu; set { if (value) KullaniciSuzgeci = KullaniciSuzgeci.Tumu; } }
    public bool KSuzgecYetim { get => KullaniciSuzgeci == KullaniciSuzgeci.Yetim; set { if (value) KullaniciSuzgeci = KullaniciSuzgeci.Yetim; } }
    public bool KSuzgecRolsuz { get => KullaniciSuzgeci == KullaniciSuzgeci.Rolsuz; set { if (value) KullaniciSuzgeci = KullaniciSuzgeci.Rolsuz; } }

    public int LoginSayisi => _tumLoginler.Count;
    public int SqlSayisi => _tumLoginler.Count(l => l.SqlLogin);
    public int WindowsSayisi => _tumLoginler.Count(l => !l.SqlLogin);
    public int KapaliSayisi => _tumLoginler.Count(l => l.Kapali);
    public int SorunluSayisi => _tumLoginler.Count(l => l.Sorunlu);
    public int KullaniciSayisi => _tumKullanicilar.Count;
    public int YetimSayisi => _tumKullanicilar.Count(k => k.Yetim);
    public int RolsuzSayisi => _tumKullanicilar.Count(k => !k.Roller.Any(r => r != "public"));

    partial void OnGorunumChanged(GuvenlikGorunumu value)
    {
        OnPropertyChanged(nameof(GorunumLoginler)); OnPropertyChanged(nameof(GorunumKullanicilar));
        if (value == GuvenlikGorunumu.Kullanicilar && _tumKullanicilar.Count == 0 && SeciliDb.Length > 0)
            _ = KullanicilariYukleAsync();
        else
            ListeyiKur();
    }

    partial void OnLoginSuzgeciChanged(LoginSuzgeci value)
    {
        foreach (string ad in new[] { nameof(SuzgecTumu), nameof(SuzgecSql), nameof(SuzgecWindows), nameof(SuzgecKapali), nameof(SuzgecSorunlu) }) OnPropertyChanged(ad);
        ListeyiKur();
    }

    partial void OnKullaniciSuzgeciChanged(KullaniciSuzgeci value)
    {
        foreach (string ad in new[] { nameof(KSuzgecTumu), nameof(KSuzgecYetim), nameof(KSuzgecRolsuz) }) OnPropertyChanged(ad);
        ListeyiKur();
    }

    partial void OnAramaMetniChanged(string value) => ListeyiKur();

    partial void OnSeciliDbChanged(string value)
    {
        OnPropertyChanged(nameof(KullaniciOlusturMetni)); OnPropertyChanged(nameof(KullaniciOlusturAcik));
        if (string.IsNullOrEmpty(value) || _yukleniyor) return;
        _ = KullanicilariYukleAsync();
    }

    // ── listeler ──

    public ObservableCollection<LoginGorunum> Loginler { get; } = [];
    public ObservableCollection<KullaniciGorunum> Kullanicilar { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginSecili), nameof(SeciliBaslik), nameof(AcKapatMetni), nameof(AcKapatAcik), nameof(KullaniciOlusturAcik))]
    private LoginGorunum? _seciliLogin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KullaniciSecili), nameof(SeciliBaslik), nameof(YetimBaglaAcik), nameof(YetimBaglaMetni))]
    private KullaniciGorunum? _seciliKullanici;

    public bool LoginSecili => Gorunum == GuvenlikGorunumu.Loginler && SeciliLogin is not null;
    public bool KullaniciSecili => Gorunum == GuvenlikGorunumu.Kullanicilar && SeciliKullanici is not null;
    public bool SecimVar => LoginSecili || KullaniciSecili;
    public string SeciliBaslik => Gorunum == GuvenlikGorunumu.Loginler ? SeciliLogin?.Ad ?? "" : SeciliKullanici?.Ad ?? "";
    [ObservableProperty] private string _seciliMeta = "";
    [ObservableProperty] private string _seciliRozet = "";
    [ObservableProperty] private string _seciliRozetTuru = "gri";

    public ObservableCollection<RolCipi> RolCipleri { get; } = [];
    public ObservableCollection<YetkiGorunum> Yetkiler { get; } = [];
    public ObservableCollection<EslemeGorunum> Eslemeler { get; } = [];

    public string RolBasligi => Gorunum == GuvenlikGorunumu.Loginler ? "SUNUCU ROLLERİ — TIKLAYINCA EKLE/ÇIKAR (ONAYLI)" : $"VERİTABANI ROLLERİ ({SeciliDb}) — TIKLAYINCA EKLE/ÇIKAR (ONAYLI)";
    public string YetkiBasligi => Gorunum == GuvenlikGorunumu.Loginler ? "SUNUCU DÜZEYİ AÇIK YETKİLER" : "AÇIK YETKİLER (GRANT / DENY)";

    public string AcKapatMetni => SeciliLogin?.Login.Kapali == true ? "▶ Aç" : "⏸ Kapat";
    public bool AcKapatAcik => IslemYapilabilir && SeciliLogin is not null;
    public string KullaniciOlusturMetni => $"➕ Kullanıcı oluştur ({(SeciliDb.Length > 0 ? SeciliDb : "DB seçin")})…";
    public bool KullaniciOlusturAcik => IslemYapilabilir && SeciliLogin is not null && SeciliDb.Length > 0
        && !Eslemeler.Any(e => e.KullaniciVar && string.Equals(e.Veritabani, SeciliDb, StringComparison.OrdinalIgnoreCase));
    public bool YetimBaglaAcik => IslemYapilabilir && SeciliKullanici is { Yetim: true };
    public string YetimBaglaMetni => SeciliKullanici is { Yetim: true } k && _tumLoginler.Any(l => l.Ad.Equals(k.Ad, StringComparison.OrdinalIgnoreCase))
        ? $"🔗 Login'e bağla ({k.Ad})" : "🔗 Login'e bağla…";

    partial void OnSeciliLoginChanged(LoginGorunum? value)
    {
        OnPropertyChanged(nameof(SecimVar));
        if (Gorunum != GuvenlikGorunumu.Loginler) return;
        _ = LoginDetayAsync(value);
    }

    partial void OnSeciliKullaniciChanged(KullaniciGorunum? value)
    {
        OnPropertyChanged(nameof(SecimVar));
        if (Gorunum != GuvenlikGorunumu.Kullanicilar) return;
        _ = KullaniciDetayAsync(value);
    }

    // ── yükleme ──

    [RelayCommand]
    public async Task YenileAsync()
    {
        if (_yukleniyor) return;
        _yukleniyor = true;
        try
        {
            QueryResult o = await Sunucu(GuvenlikSorgulari.OzetSql());
            if (o.Hata is { } oh) { Ozet = null; Uyari = "⚠ Sunucu okunamadı: " + oh.Mesaj; return; }
            Ozet = GuvenlikSorgulari.OzetOku(o);
            if (Ozet is null) { Uyari = "⚠ Sunucu boş yanıt verdi."; return; }

            QueryResult l = await Sunucu(GuvenlikSorgulari.LoginlerSql());
            if (l.Hata is { } lh) { Uyari = "⚠ Login'ler okunamadı: " + GuvenlikSorgulari.HataMetni(lh); return; }
            IReadOnlyDictionary<string, int> oturumlar = GuvenlikSorgulari.OturumlariOku(await Sunucu(GuvenlikSorgulari.OturumSayilariSql()));
            _tumLoginler = GuvenlikSorgulari.LoginleriOku(l, oturumlar);
            QueryResult r = await Sunucu(GuvenlikSorgulari.SunucuRolleriSql());
            if (r.Hata is null && GuvenlikSorgulari.AdlariOku(r).Count > 0) _sunucuRolAdlari = GuvenlikSorgulari.AdlariOku(r);
            QueryResult e = await Sunucu(GuvenlikSorgulari.EslemeSql(Ozet.Veritabanlari));
            _eslemeler = e.Hata is null ? GuvenlikSorgulari.EslemeleriOku(e) : new Dictionary<string, List<LoginEslemesi>>();

            string seciliDb = SeciliDb;
            Veritabanlari.Clear();
            foreach (string db in Ozet.Veritabanlari) Veritabanlari.Add(db);
            if (Veritabanlari.Count > 0)
                SeciliDb = Veritabanlari.Contains(seciliDb) ? seciliDb : Veritabanlari.FirstOrDefault(d => d is not ("master" or "msdb" or "model" or "tempdb")) ?? Veritabanlari[0];

            int sysadmin = _tumLoginler.Count(x => x.SunucuRolleri.Contains("sysadmin"));
            Login? sa = _tumLoginler.FirstOrDefault(x => x.Ad == "sa");
            int yetim = _eslemeler.Keys.Count(sid => !_tumLoginler.Any(x => x.Sid == sid)); // SID'i hiçbir login'e uymayan kullanıcılar
            Meta = $"{_sunucuAdi} · SQL Server {Ozet.Surum} · {_tumLoginler.Count} login · {sysadmin} sysadmin"
                 + (sa is null ? "" : $" · sa {(sa.Kapali ? "kapalı" : "AÇIK")}") + (yetim > 0 ? $" · {yetim} yetim kullanıcı" : "");
            Uyari = Ozet switch
            {
                { TumunuGorur: false, Sysadmin: false } => "🔒 VIEW ANY DEFINITION izniniz yok — SQL Server yalnız KENDİ login'inizi listeler; diğerleri görünmez (liste eksik değil, gizli).",
                { Azure: true } => "ℹ Azure SQL Database: login listesi master ile sınırlıdır; kullanıcıların çoğu contained (DATABASE kimliği) — Kullanıcılar görünümünü kullanın.",
                _ => _saltOkunur ? "ℹ Salt-okunur bağlantı: yalnız görüntüleme ve script." : IslemYapilabilir ? "" : "ℹ ALTER ANY LOGIN izniniz yok — işlemler kapalı, görüntüleme ve script açık.",
            };
            SonYenileme = $"yenilendi {DateTime.Now:HH:mm:ss}";
            DurumCubugu = $"🔐 {_tumLoginler.Count} login · {sysadmin} sysadmin · {yetim} yetim kullanıcı";
            if (Gorunum == GuvenlikGorunumu.Kullanicilar || _tumKullanicilar.Count > 0)
                await KullanicilariYukleAsync();
            else
                ListeyiKur();
        }
        finally
        {
            _yukleniyor = false;
        }
    }

    private async Task KullanicilariYukleAsync()
    {
        if (SeciliDb.Length == 0) return;
        QueryResult k = await Db(GuvenlikSorgulari.KullanicilarSql());
        if (k.Hata is { } kh) { Bilgi = "⚠ Kullanıcılar okunamadı: " + GuvenlikSorgulari.HataMetni(kh); _tumKullanicilar = []; }
        else _tumKullanicilar = GuvenlikSorgulari.KullanicilariOku(k);
        QueryResult r = await Db(GuvenlikSorgulari.DbRolleriSql());
        _dbRolAdlari = r.Hata is null ? GuvenlikSorgulari.AdlariOku(r) : [];
        ListeyiKur();
    }

    private void ListeyiKur()
    {
        string ara = AramaMetni.Trim();
        string? onceki = SeciliLogin?.Ad;
        Loginler.Clear();
        foreach (Login l in _tumLoginler.Where(l => LoginSuzgeci switch
                 {
                     LoginSuzgeci.Sql => l.SqlLogin, LoginSuzgeci.Windows => !l.SqlLogin, LoginSuzgeci.Kapali => l.Kapali, LoginSuzgeci.Sorunlu => l.Sorunlu, _ => true,
                 } && (ara.Length == 0 || l.Ad.Contains(ara, StringComparison.OrdinalIgnoreCase))))
        {
            (string durum, string tur) = l switch
            {
                { Kapali: true } => ("kapalı", "gri"), { Kilitli: true } => ("kilitli", "uyari"), { SuresiDolmus: true } => ("süresi dolmuş", "uyari"), _ => ("açık", "ok"),
            };
            List<LoginEslemesi> es = _eslemeler.TryGetValue(l.Sid, out List<LoginEslemesi>? v) ? v : [];
            Loginler.Add(new LoginGorunum(l, durum, tur, string.Join(", ", l.SunucuRolleri.Where(x => x != "public")),
                l.SqlLogin ? l.ParolaDegisti?.ToString("dd.MM.yyyy") ?? "—" : "—",
                es.Count == 0 ? "—" : string.Join(", ", es.Select(x => x.Veritabani))));
        }
        string? oncekiK = SeciliKullanici?.Ad;
        Kullanicilar.Clear();
        foreach (Kullanici k in _tumKullanicilar.Where(k => KullaniciSuzgeci switch
                 {
                     KullaniciSuzgeci.Yetim => k.Yetim, KullaniciSuzgeci.Rolsuz => !k.Roller.Any(r => r != "public"), _ => true,
                 } && (ara.Length == 0 || k.Ad.Contains(ara, StringComparison.OrdinalIgnoreCase))))
        {
            Login? login = k.BagliLogin is { } ad ? _tumLoginler.FirstOrDefault(x => x.Ad == ad) : null;
            (string lm, string lt) = k.Yetim ? ("yetim", "hata")
                : login is { Kapali: true } ? (k.BagliLogin + " (kapalı)", "gri")
                : login is { Sorunlu: true } ? (k.BagliLogin + " (kilitli)", "uyari")
                : k.BagliLogin is { } b ? (b, "ok")
                : (k.Kimlik == "DATABASE" ? "contained" : k.Kimlik == "NONE" ? "login'siz (bilerek)" : "login yok", "gri");
            Kullanicilar.Add(new KullaniciGorunum(k, lm, lt, string.Join(", ", k.Roller.Where(x => x != "public"))));
        }
        foreach (string ad in new[] { nameof(LoginSayisi), nameof(SqlSayisi), nameof(WindowsSayisi), nameof(KapaliSayisi), nameof(SorunluSayisi), nameof(KullaniciSayisi), nameof(YetimSayisi), nameof(RolsuzSayisi), nameof(RolBasligi), nameof(YetkiBasligi) })
            OnPropertyChanged(ad);
        SeciliLogin = Loginler.FirstOrDefault(x => x.Ad == onceki) ?? (Gorunum == GuvenlikGorunumu.Loginler ? Loginler.FirstOrDefault() : null);
        SeciliKullanici = Kullanicilar.FirstOrDefault(x => x.Ad == oncekiK) ?? (Gorunum == GuvenlikGorunumu.Kullanicilar ? Kullanicilar.FirstOrDefault() : null);
        // Görünüm değişince seçim aynı kalabilir → detay yine kurulsun
        if (Gorunum == GuvenlikGorunumu.Loginler && SeciliLogin is { } sl && SeciliLogin.Ad == onceki) _ = LoginDetayAsync(sl);
        if (Gorunum == GuvenlikGorunumu.Kullanicilar && SeciliKullanici is { } sk && SeciliKullanici.Ad == oncekiK) _ = KullaniciDetayAsync(sk);
        OnPropertyChanged(nameof(LoginSecili)); OnPropertyChanged(nameof(KullaniciSecili)); OnPropertyChanged(nameof(SecimVar)); OnPropertyChanged(nameof(SeciliBaslik));
    }

    private async Task LoginDetayAsync(LoginGorunum? sec)
    {
        RolCipleri.Clear(); Yetkiler.Clear(); Eslemeler.Clear();
        if (sec is null) { SeciliMeta = ""; SeciliRozet = ""; return; }
        Login l = sec.Login;
        SeciliMeta = $"{l.Tur} login · oluşturma {l.Olusturma?.ToString("dd.MM.yyyy") ?? "—"}"
            + (l.SqlLogin ? $" · politika {(l.PolitikaAcik ? "AÇIK" : "KAPALI")}" : "") + $" · {l.OturumSayisi} açık oturum";
        SeciliRozet = sec.DurumMetni; SeciliRozetTuru = sec.DurumTuru;
        foreach (string rol in _sunucuRolAdlari.Where(r => r != "public"))
            RolCipleri.Add(new RolCipi { Ad = rol, Uye = l.SunucuRolleri.Contains(rol) });
        if (_eslemeler.TryGetValue(l.Sid, out List<LoginEslemesi>? es))
            foreach (LoginEslemesi e in es)
                Eslemeler.Add(new EslemeGorunum(e.Veritabani, e.Kullanici, string.Join(", ", e.Roller.Where(x => x != "public")), true));
        foreach (string db in Ozet?.Veritabanlari ?? [])
            if (!Eslemeler.Any(e => e.Veritabani.Equals(db, StringComparison.OrdinalIgnoreCase)) && db is not ("master" or "msdb" or "model" or "tempdb"))
                Eslemeler.Add(new EslemeGorunum(db, "", "", false));
        OnPropertyChanged(nameof(KullaniciOlusturAcik));
        QueryResult y = await Sunucu(GuvenlikSorgulari.SunucuYetkileriSql(l.Ad));
        if (SeciliLogin?.Ad != l.Ad) return;
        if (y.Hata is null)
            foreach (Yetki yk in GuvenlikSorgulari.YetkileriOku(y)) Yetkiler.Add(new YetkiGorunum(yk));
    }

    private async Task KullaniciDetayAsync(KullaniciGorunum? sec)
    {
        RolCipleri.Clear(); Yetkiler.Clear(); Eslemeler.Clear();
        if (sec is null) { SeciliMeta = ""; SeciliRozet = ""; return; }
        Kullanici k = sec.Kullanici;
        SeciliMeta = $"{k.Tur} kullanıcı · kimlik {k.Kimlik} · şema {k.Sema} · oluşturma {k.Olusturma?.ToString("dd.MM.yyyy") ?? "—"}";
        SeciliRozet = k.Yetim ? "yetim — login'e bağlı değil" : sec.LoginMetni;
        SeciliRozetTuru = k.Yetim ? "hata" : "ok";
        foreach (string rol in _dbRolAdlari.Where(r => r != "public"))
            RolCipleri.Add(new RolCipi { Ad = rol, Uye = k.Roller.Contains(rol) });
        QueryResult y = await Db(GuvenlikSorgulari.DbYetkileriSql(k.Ad));
        if (SeciliKullanici?.Ad != k.Ad) return;
        if (y.Hata is null)
            foreach (Yetki yk in GuvenlikSorgulari.YetkileriOku(y)) Yetkiler.Add(new YetkiGorunum(yk));
    }

    // ── işlemler ──

    private async Task<bool> KosVeBildirAsync(string sql, string? db, string basari)
    {
        QueryResult r = await _komut(sql, db, 60, CancellationToken.None);
        if (r.Hata is { } h) { Bilgi = "✖ " + GuvenlikSorgulari.HataMetni(h); return false; }
        Bilgi = basari;
        return true;
    }

    /// <summary>Rol çipi tıklandı: ekle/çıkar — onay (tehlikeli rol → Tehlike); kendi login'ini sysadmin'den çıkarma engellenir (K5).</summary>
    [RelayCommand]
    private async Task RolDegistirAsync(RolCipi? cip)
    {
        if (cip is null || !IslemYapilabilir) return;
        bool ekle = !cip.Uye;
        bool sunucu = Gorunum == GuvenlikGorunumu.Loginler;
        string? hedef = sunucu ? SeciliLogin?.Ad : SeciliKullanici?.Ad;
        if (hedef is null) return;
        if (sunucu && !ekle && cip.Ad == "sysadmin" && string.Equals(hedef, Ozet?.KendiLogin, StringComparison.OrdinalIgnoreCase))
        {
            Bilgi = "⛔ Kendi login'inizi sysadmin'den çıkaramazsınız (kilitlenirsiniz).";
            return;
        }
        string sql = sunucu ? GuvenlikSorgulari.SunucuRolSql(cip.Ad, hedef, ekle) : GuvenlikSorgulari.DbRolSql(cip.Ad, hedef, ekle);
        var istek = new OnayIstegi(ekle ? "Role eklensin mi?" : "Rolden çıkarılsın mı?",
            $"{hedef} → {cip.Ad}\n{sql}" + (cip.Tehlikeli ? "\n\n⚠ Bu rol tam yetki verir/alır." : ""),
            ekle ? "Ekle" : "Çıkar", cip.Tehlikeli);
        if (!_onayla(istek)) return;
        string basari = $"✔ {hedef} {(ekle ? "eklendi:" : "çıkarıldı:")} {cip.Ad}";
        if (await KosVeBildirAsync(sql, sunucu ? "master" : SeciliDb, basari))
        {
            cip.Uye = ekle;
            await YenileAsync();
            Bilgi = basari;
        }
    }

    [RelayCommand]
    private async Task AcKapatAsync()
    {
        if (!AcKapatAcik || SeciliLogin is not { } s) return;
        bool kapat = !s.Login.Kapali;
        if (kapat && string.Equals(s.Ad, Ozet?.KendiLogin, StringComparison.OrdinalIgnoreCase))
        {
            Bilgi = "⛔ Kendi login'inizi kapatamazsınız.";
            return;
        }
        bool tehlike = (!kapat && s.Ad == "sa") || (kapat && (s.Sysadmin || s.Tur == "Windows grup"));
        string sql = GuvenlikSorgulari.LoginAcKapatSql(s.Ad, kapat);
        string mesaj = kapat
            ? $"{s.Ad} devre dışı kalacak; yeni oturum açamaz." + (s.Oturum > 0 ? $"\n⚠ Şu an {s.Oturum} açık oturumu var — onlar kesilmez, yenisi açılamaz." : "") + $"\n{sql}"
            : $"{s.Ad} yeniden oturum açabilecek.\n{sql}" + (s.Ad == "sa" ? "\n\n⚠ sa açmak güvenlik riskidir — güçlü parola ve politika şart." : "");
        if (!_onayla(new OnayIstegi(kapat ? "Login kapatılsın mı?" : "Login açılsın mı?", mesaj, kapat ? "⏸ Kapat" : "▶ Aç", tehlike)))
            return;
        string basari = $"✔ {s.Ad} {(kapat ? "kapatıldı" : "açıldı")}.";
        if (await KosVeBildirAsync(sql, "master", basari))
        {
            await YenileAsync();
            Bilgi = basari;
        }
    }

    /// <summary>Seçili login için seçili DB'de aynı adlı kullanıcı oluştur.</summary>
    [RelayCommand]
    private async Task KullaniciOlusturAsync()
    {
        if (!KullaniciOlusturAcik || SeciliLogin is not { } s) return;
        string sql = GuvenlikSorgulari.KullaniciOlusturSql(s.Ad);
        if (!_onayla(new OnayIstegi("Kullanıcı oluşturulsun mu?", $"[{SeciliDb}] veritabanında {s.Ad} login'i için kullanıcı:\n{sql}\n(rol verilmez — sonra çiplerden ekleyin)", "➕ Oluştur")))
            return;
        string basari = $"✔ [{SeciliDb}] içinde {s.Ad} kullanıcısı oluşturuldu.";
        if (await KosVeBildirAsync(sql, SeciliDb, basari))
        {
            await YenileAsync();
            Bilgi = basari;
        }
    }

    /// <summary>Yetim kullanıcıyı aynı adlı login'e bağla (ALTER USER … WITH LOGIN) — farklı ad S2 sihirbazı.</summary>
    [RelayCommand]
    private async Task YetimBaglaAsync()
    {
        if (!YetimBaglaAcik || SeciliKullanici is not { } k) return;
        Login? login = _tumLoginler.FirstOrDefault(l => l.Ad.Equals(k.Ad, StringComparison.OrdinalIgnoreCase));
        if (login is null)
        {
            Bilgi = $"⚠ '{k.Ad}' adlı login yok — önce login oluşturun (S2) ya da açılan script'te login adını yazıp çalıştırın.";
            _sekmeAc($"yetim-{k.Ad}.sql", GuvenlikSorgulari.YetimBaglaSql(k.Ad, "<login adı>"), SeciliDb);
            return;
        }
        string sql = GuvenlikSorgulari.YetimBaglaSql(k.Ad, login.Ad);
        if (!_onayla(new OnayIstegi("Yetim kullanıcı bağlansın mı?", $"[{SeciliDb}].{k.Ad} → login {login.Ad}\n{sql}", "🔗 Bağla")))
            return;
        string basari = $"✔ {k.Ad} login'e bağlandı.";
        if (await KosVeBildirAsync(sql, SeciliDb, basari))
        {
            await YenileAsync();
            Bilgi = basari;
        }
    }

    [RelayCommand]
    private void ScriptAl()
    {
        if (Gorunum == GuvenlikGorunumu.Loginler && SeciliLogin is { } l)
        {
            List<LoginEslemesi> es = _eslemeler.TryGetValue(l.Login.Sid, out List<LoginEslemesi>? v) ? v : [];
            _sekmeAc($"login-{l.Ad}.sql", GuvenlikSorgulari.LoginScripti(l.Login, es, DateTime.Now, _sunucuAdi), "master");
        }
        else if (SeciliKullanici is { } k)
            _sekmeAc($"kullanici-{k.Ad}.sql", GuvenlikSorgulari.KullaniciScripti(k.Kullanici, SeciliDb, [.. Yetkiler.Select(y => y.Yetki)], DateTime.Now, _sunucuAdi), SeciliDb);
    }
}
