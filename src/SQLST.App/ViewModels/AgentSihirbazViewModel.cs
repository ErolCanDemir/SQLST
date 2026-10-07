using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Sihirbaz kipi: yeni job · mevcut job'u yerinde düzenle · seçili job'dan kopya.</summary>
public enum AgentSihirbazKipi
{
    Yeni,
    Duzenle,
    Kopya,
}

/// <summary>Agent sekmesinin sihirbaz açma isteği — MainViewModel pencereyi kurar (VM test edilebilir kalsın).</summary>
public sealed record AgentSihirbazIstegi(
    AgentSihirbazKipi Kip, AgentJob? Job, IReadOnlyList<AgentAdim> Adimlar, IReadOnlyList<AgentZamanlama> Zamanlamalar,
    IReadOnlyList<string> Kategoriler, string VarsayilanSahip, string Sunucu);

/// <summary>Sihirbazdaki bir adım. "N. adıma git" hedefi NUMARA değil NESNE tutar — sıra değişince
/// hedef kendiliğinden doğru numarayı alır.</summary>
public sealed partial class AgentAdimTaslagi : ObservableObject
{
    /// <summary>Eylem seçenekleri — sıra <see cref="EylemKodlari"/> ile birebir.</summary>
    public static IReadOnlyList<string> EylemSecenekleri { get; } =
        ["Sonraki adıma geç", "Başarıyla çık (job başarılı)", "Hatayla çık (job başarısız)", "Belirli adıma git"];

    private static readonly int[] EylemKodlari = [3, 1, 2, 4];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Gosterim))] private int _numara;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Gosterim))] private string _ad = "";

    /// <summary>"N. adıma git" listesinde: "2 · Birleştir".</summary>
    public string Gosterim => $"{Numara} · {Ad}";

    // Tema'nın ComboBox şablonu seçili öğede DisplayMemberPath'i uygulamıyor (tür adı görünüyordu).
    public override string ToString() => Gosterim;
    [ObservableProperty] private string _veritabani = "master";
    [ObservableProperty] private string _komut = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BasaridaHedefGorunur))] private int _basaridaIndex;
    [ObservableProperty] private AgentAdimTaslagi? _basaridaHedef;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HatadaHedefGorunur))] private int _hatadaIndex = 2;
    [ObservableProperty] private AgentAdimTaslagi? _hatadaHedef;
    [ObservableProperty] private int _yenidenDeneme;
    [ObservableProperty] private int _denemeAraligi;
    [ObservableProperty] private string _denetimSonucu = "";
    [ObservableProperty] private string _denetimTuru = "";

    /// <summary>T-SQL dışı adım (PowerShell, CmdExec…) — sihirbazda salt görüntü, aynen korunur (K4).</summary>
    public string AltSistem { get; init; } = "TSQL";
    public int? ProxyId { get; init; }
    public string? CiktiDosyasi { get; init; }
    public int Bayraklar { get; init; }

    public bool SaltGoruntu => !AltSistem.Equals("TSQL", StringComparison.OrdinalIgnoreCase);
    public bool Duzenlenebilir => !SaltGoruntu;
    public string Tur => SaltGoruntu ? AltSistem : "T-SQL";
    public bool BasaridaHedefGorunur => BasaridaIndex == 3;
    public bool HatadaHedefGorunur => HatadaIndex == 3;

    public int BasaridaEylem => EylemKodlari[Math.Clamp(BasaridaIndex, 0, 3)];
    public int HatadaEylem => EylemKodlari[Math.Clamp(HatadaIndex, 0, 3)];

    public string BasaridaMetni => AgentSorgulari.EylemMetni(BasaridaEylem, BasaridaHedef?.Numara ?? 0);

    public string HatadaMetni => AgentSorgulari.EylemMetni(HatadaEylem, HatadaHedef?.Numara ?? 0)
        + (YenidenDeneme > 0 ? $" ({YenidenDeneme} deneme)" : "");

    public static int EylemIndex(int kod) => Math.Max(0, Array.IndexOf(EylemKodlari, kod));

    public AgentAdim Model() => new(
        Numara, Ad.Trim(), AltSistem, Komut, string.IsNullOrWhiteSpace(Veritabani) || SaltGoruntu && Veritabani == "—" ? null : Veritabani,
        BasaridaEylem, BasaridaEylem == 4 ? BasaridaHedef?.Numara ?? 0 : 0,
        HatadaEylem, HatadaEylem == 4 ? HatadaHedef?.Numara ?? 0 : 0,
        Math.Max(0, YenidenDeneme), Math.Max(0, DenemeAraligi), ProxyId, CiktiDosyasi, Bayraklar);

    /// <summary>Liste metinleri numaralar değişince tazelensin.</summary>
    public void MetinleriTazele()
    {
        OnPropertyChanged(nameof(BasaridaMetni));
        OnPropertyChanged(nameof(HatadaMetni));
    }

    partial void OnYenidenDenemeChanged(int value) => OnPropertyChanged(nameof(HatadaMetni));
    partial void OnBasaridaIndexChanged(int value) => OnPropertyChanged(nameof(BasaridaMetni));
    partial void OnBasaridaHedefChanged(AgentAdimTaslagi? value) => OnPropertyChanged(nameof(BasaridaMetni));
    partial void OnHatadaIndexChanged(int value) => OnPropertyChanged(nameof(HatadaMetni));
    partial void OnHatadaHedefChanged(AgentAdimTaslagi? value) => OnPropertyChanged(nameof(HatadaMetni));
    partial void OnKomutChanged(string value) => DenetimSonucu = "";
}

/// <summary>Sihirbazdaki bir zamanlama — SSMS'in tüm türleri; Türkçe cümle canlı (<see cref="Metin"/>).</summary>
public sealed partial class AgentZamanlamaTaslagi : ObservableObject
{
    public static IReadOnlyList<string> TurSecenekleri { get; } =
        ["Bir kez", "Günlük", "Haftalık", "Aylık", "Agent başlarken", "CPU boştayken"];

    private static readonly int[] TurKodlari = [1, 4, 8, 16, 64, 128];

    public static IReadOnlyList<string> SiraSecenekleri { get; } = ["ilk", "ikinci", "üçüncü", "dördüncü", "son"];
    private static readonly int[] SiraKodlari = [1, 2, 4, 8, 16];

    public static IReadOnlyList<string> GoreliGunSecenekleri { get; } =
        ["pazartesi", "salı", "çarşamba", "perşembe", "cuma", "cumartesi", "pazar", "gün", "hafta içi günü", "hafta sonu günü"];
    private static readonly int[] GoreliGunKodlari = [2, 3, 4, 5, 6, 7, 1, 8, 9, 10];

    public static IReadOnlyList<string> BirimSecenekleri { get; } = ["dakikada", "saatte", "saniyede"];
    private static readonly int[] BirimKodlari = [4, 8, 2];

    public event EventHandler? Degisti;

    [ObservableProperty] private string _ad = "Zamanlama";
    [ObservableProperty] private bool _acik = true;
    [ObservableProperty] private int _turIndex = 1;
    [ObservableProperty] private int _her = 1;
    [ObservableProperty] private bool _pzt;
    [ObservableProperty] private bool _sal;
    [ObservableProperty] private bool _car;
    [ObservableProperty] private bool _per;
    [ObservableProperty] private bool _cum;
    [ObservableProperty] private bool _cmt;
    [ObservableProperty] private bool _paz;
    [ObservableProperty] private bool _aylikGoreli;
    [ObservableProperty] private int _aylikGun = 1;
    [ObservableProperty] private int _siraIndex;
    [ObservableProperty] private int _goreliGunIndex;
    [ObservableProperty] private bool _gunIciTekrar;
    [ObservableProperty] private string _saat = "02:00";
    [ObservableProperty] private int _tekrarAraligi = 15;
    [ObservableProperty] private int _birimIndex;
    [ObservableProperty] private string _araBaslangic = "08:00";
    [ObservableProperty] private string _araBitis = "18:00";
    [ObservableProperty] private string _baslangicTarihi = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    [ObservableProperty] private bool _bitisVar;
    [ObservableProperty] private string _bitisTarihi = DateTime.Today.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"Her N …" alanının birimi (günde/haftada/ayda bir).</summary>
    public string HerBirimi => TurKodu switch { 4 => "günde bir", 8 => "haftada bir", _ => "ayda bir" };

    public int TurKodu => TurKodlari[Math.Clamp(TurIndex, 0, TurKodlari.Length - 1)];
    public bool BirKezMi => TurKodu == 1;
    public bool GunlukMu => TurKodu == 4;
    public bool HaftalikMi => TurKodu == 8;
    public bool AylikMi => TurKodu == 16;
    public bool TekrarliMi => TurKodu is 4 or 8 or 16;
    public bool SaatliMi => TurKodu is not (64 or 128);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Metin) or nameof(BirKezMi) or nameof(GunlukMu) or nameof(HaftalikMi)
            or nameof(AylikMi) or nameof(TekrarliMi) or nameof(SaatliMi) or nameof(TurKodu) or nameof(HerBirimi)
            or nameof(BitisGorunur) or nameof(GunIciGorunur))
            return;
        if (e.PropertyName == nameof(TurIndex))
        {
            foreach (string ad in new[] { nameof(TurKodu), nameof(BirKezMi), nameof(GunlukMu), nameof(HaftalikMi), nameof(AylikMi), nameof(TekrarliMi), nameof(SaatliMi), nameof(HerBirimi), nameof(BitisGorunur), nameof(GunIciGorunur) })
                base.OnPropertyChanged(new PropertyChangedEventArgs(ad));
        }
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Metin)));
        Degisti?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"HH:mm" ya da "HH:mm:ss" → hhmmss; geçersizse -1.</summary>
    public static int SaatKodu(string metin)
    {
        string[] p = (metin ?? "").Trim().Split(':');
        if (p.Length is < 2 or > 3
            || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out int s) || s > 23
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out int d) || d > 59)
            return -1;
        int sn = 0;
        if (p.Length == 3 && (!int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out sn) || sn > 59))
            return -1;
        return s * 10000 + d * 100 + sn;
    }

    /// <summary>"yyyy-MM-dd" → yyyymmdd; geçersizse -1 (ham değer kuralı: tarih DB biçiminde yazılır).</summary>
    public static int TarihKodu(string metin)
        => DateTime.TryParseExact((metin ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t)
            ? t.Year * 10000 + t.Month * 100 + t.Day
            : -1;

    /// <summary>Bitiş tarihi bir kez/Agent başlarken/CPU boşta türlerinde anlamsız.</summary>
    public bool BitisGorunur => TekrarliMi;

    /// <summary>Gün içi tekrar yalnız günlük/haftalık/aylıkta.</summary>
    public bool GunIciGorunur => TekrarliMi;

    /// <summary>Saat/tarih alanlarında yazım hatası varsa açıklaması (yoksa null).</summary>
    public string? SaatHatasi
    {
        get
        {
            if (SaatliMi && TarihKodu(BaslangicTarihi) < 0)
                return $"Zamanlama \"{Ad}\": başlangıç tarihi yyyy-MM-dd biçiminde olmalı (ör. 2026-10-06).";
            if (TekrarliMi && BitisVar && TarihKodu(BitisTarihi) < 0)
                return $"Zamanlama \"{Ad}\": bitiş tarihi yyyy-MM-dd biçiminde olmalı.";
            if (!SaatliMi)
                return null;
            if (GunIciTekrar && TekrarliMi)
                return SaatKodu(AraBaslangic) < 0 || SaatKodu(AraBitis) < 0 ? $"Zamanlama \"{Ad}\": aralık saatleri SS:dd biçiminde olmalı." : null;
            return SaatKodu(Saat) < 0 ? $"Zamanlama \"{Ad}\": saat SS:dd biçiminde olmalı (ör. 02:00)." : null;
        }
    }

    public int HaftaMaskesi => (Paz ? 1 : 0) | (Pzt ? 2 : 0) | (Sal ? 4 : 0) | (Car ? 8 : 0) | (Per ? 16 : 0) | (Cum ? 32 : 0) | (Cmt ? 64 : 0);

    public AgentZamanlama Model(int id = 0)
    {
        int tur = TurKodu;
        bool tekrar = GunIciTekrar && tur is 4 or 8 or 16;
        int bas = tekrar ? SaatKodu(AraBaslangic) : SaatKodu(Saat);
        int bit = tekrar ? SaatKodu(AraBitis) : 235959;
        if (tur is 64 or 128)
            (bas, bit) = (0, 235959);
        if (AylikMi && AylikGoreli)
            tur = 32;
        int aralik = tur switch
        {
            4 => Math.Max(0, Her),
            8 => HaftaMaskesi,
            16 => AylikGun,
            32 => GoreliGunKodlari[Math.Clamp(GoreliGunIndex, 0, GoreliGunKodlari.Length - 1)],
            _ => 0,
        };
        return new AgentZamanlama(
            id, Ad.Trim(), Acik, tur, aralik,
            tekrar ? BirimKodlari[Math.Clamp(BirimIndex, 0, 2)] : (tur is 1 or 64 or 128 ? 0 : 1), // msdb bir kezde 0 saklar
            tekrar ? Math.Max(0, TekrarAraligi) : 0,
            tur == 32 ? SiraKodlari[Math.Clamp(SiraIndex, 0, 4)] : 0,
            tur is 8 or 16 or 32 ? Math.Max(0, Her) : 0,
            Math.Max(0, TarihKodu(BaslangicTarihi)),
            BitisVar && tur is not (1 or 64 or 128) ? Math.Max(0, TarihKodu(BitisTarihi)) : 99991231,
            Math.Max(0, bas), Math.Max(0, bit));
    }

    public string Metin => SaatHatasi is null ? "→ " + AgentSorgulari.ZamanlamaMetni(Model()) : "→ (saat geçersiz)";

    /// <summary>Düzenleme/kopya: msdb zamanlamasını forma açar.</summary>
    public static AgentZamanlamaTaslagi Kur(AgentZamanlama z)
    {
        static string Saat(int k) => string.Create(CultureInfo.InvariantCulture, $"{k / 10000:00}:{k / 100 % 100:00}") + (k % 100 == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $":{k % 100:00}"));
        static string Tarih(int k, DateTime vars) => (AgentSorgulari.AgentZamani(k, 0) ?? vars).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var t = new AgentZamanlamaTaslagi
        {
            Ad = z.Ad,
            Acik = z.Acik,
            TurIndex = Math.Max(0, Array.IndexOf(TurKodlari, z.FreqType == 32 ? 16 : z.FreqType)),
            AylikGoreli = z.FreqType == 32,
            Her = z.FreqType == 4 ? Math.Max(1, z.FreqInterval) : Math.Max(1, z.RecurrenceFactor),
            AylikGun = z.FreqType == 16 ? Math.Clamp(z.FreqInterval, 1, 31) : 1,
            SiraIndex = Math.Max(0, Array.IndexOf(SiraKodlari, z.RelativeInterval)),
            GoreliGunIndex = z.FreqType == 32 ? Math.Max(0, Array.IndexOf(GoreliGunKodlari, z.FreqInterval)) : 0,
            GunIciTekrar = z.SubdayType is 2 or 4 or 8,
            BirimIndex = Math.Max(0, Array.IndexOf(BirimKodlari, z.SubdayType)),
            TekrarAraligi = z.SubdayInterval > 0 ? z.SubdayInterval : 15,
            BaslangicTarihi = Tarih(z.BaslangicTarihi, DateTime.Today),
            BitisVar = z.BitisTarihi is > 0 and < 99991231,
            BitisTarihi = Tarih(z.BitisTarihi is > 0 and < 99991231 ? z.BitisTarihi : 0, DateTime.Today.AddMonths(1)),
        };
        if (t.GunIciTekrar)
        {
            t.AraBaslangic = Saat(z.BaslangicSaati);
            t.AraBitis = Saat(z.BitisSaati);
        }
        else
        {
            t.Saat = Saat(z.BaslangicSaati);
        }
        if (z.FreqType == 8)
        {
            t.Paz = (z.FreqInterval & 1) != 0; t.Pzt = (z.FreqInterval & 2) != 0; t.Sal = (z.FreqInterval & 4) != 0;
            t.Car = (z.FreqInterval & 8) != 0; t.Per = (z.FreqInterval & 16) != 0; t.Cum = (z.FreqInterval & 32) != 0;
            t.Cmt = (z.FreqInterval & 64) != 0;
        }
        return t;
    }
}

/// <summary>
/// ⏱ SQL Agent S3 — job oluşturma/düzenleme sihirbazı (v23-S18; mockup docs/mockup/sql-agent-sihirbaz.html
/// ONAYLI). Dört adım: Genel · Adımlar · Zamanlama · Özet ve script. Job OLUŞTURULMAZ (K3): script
/// üretilir, yeni sorgu sekmesinde açılır. Adım türü yalnız T-SQL (K4); mevcut job'daki T-SQL dışı adımlar
/// salt görüntü olarak AYNEN korunur. Düzenleme script'i job'u silmeden günceller (geçmiş korunur).
/// Söz dizimi denetimi SET NOEXEC ile — komut çalıştırılmaz.
/// </summary>
public sealed partial class AgentSihirbazViewModel : ObservableObject
{
    private readonly AgentSihirbazIstegi _istek;
    private readonly Func<string, string?, CancellationToken, Task<QueryResult>> _calistir; // (sql, db) — denetim
    private readonly Func<IReadOnlyList<(string Baslik, string Metin)>> _acikSorgular;
    private readonly Action<string, string> _scriptAc;                                     // (başlık, script)

    public AgentSihirbazViewModel(
        AgentSihirbazIstegi istek, IReadOnlyList<string> veritabanlari,
        Func<string, string?, CancellationToken, Task<QueryResult>> calistir,
        Func<IReadOnlyList<(string Baslik, string Metin)>> acikSorgular,
        Action<string, string> scriptAc)
    {
        _istek = istek;
        _calistir = calistir;
        _acikSorgular = acikSorgular;
        _scriptAc = scriptAc;
        foreach (string db in veritabanlari)
            Veritabanlari.Add(db);
        foreach (string k in istek.Kategoriler.Where(k => k.Length > 0).Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
            Kategoriler.Add(k);
        if (!Kategoriler.Contains(VarsayilanKategori))
            Kategoriler.Insert(0, VarsayilanKategori);

        Adimlar.CollectionChanged += (_, _) => { Numarala(); Denetle(); };
        Zamanlamalar.CollectionChanged += (_, e) =>
        {
            foreach (AgentZamanlamaTaslagi z in e.NewItems?.OfType<AgentZamanlamaTaslagi>() ?? [])
                z.Degisti += (_, _) => Denetle();
            OnPropertyChanged(nameof(ZamanlamaYok));
            Denetle();
        };

        if (istek.Job is { } j)
        {
            _ad = istek.Kip == AgentSihirbazKipi.Kopya ? j.Ad + " (kopya)" : j.Ad;
            _acik = j.Acik;
            _kategori = j.Kategori.Length > 0 ? j.Kategori : VarsayilanKategori;
            _sahip = j.Sahip;
            _aciklama = j.Aciklama == "No description available." ? "" : j.Aciklama;
            AdimlariKur(istek.Adimlar);
            foreach (AgentZamanlama z in istek.Zamanlamalar)
                Zamanlamalar.Add(AgentZamanlamaTaslagi.Kur(z));
        }
        else
        {
            _sahip = istek.VarsayilanSahip;
            Adimlar.Add(YeniAdim());
        }
        SeciliAdim = Adimlar.FirstOrDefault();
        SeciliZamanlama = Zamanlamalar.FirstOrDefault();
        Denetle();
    }

    private const string VarsayilanKategori = "[Uncategorized (Local)]";

    public AgentSihirbazKipi Kip => _istek.Kip;

    public string PencereBasligi => Kip switch
    {
        AgentSihirbazKipi.Duzenle => $"✎ Job düzenle — {_istek.Job?.Ad} · {_istek.Sunucu}",
        AgentSihirbazKipi.Kopya => $"⧉ Job kopyası — {_istek.Sunucu}",
        _ => $"⏱ Yeni job — {_istek.Sunucu}",
    };

    // ── sayfalar ─────────────────────────────────────────────────────────────

    public IReadOnlyList<string> SayfaAdlari { get; } = ["Genel", "Adımlar", "Zamanlama", "Özet ve script"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeriAcik), nameof(IleriAcik), nameof(SayfaBasligi), nameof(SayfaAciklamasi))]
    private int _sayfa;

    partial void OnSayfaChanged(int value)
    {
        if (value == 3)
            ScriptOnizleme = ScriptMetni() ?? "Önce alt çubuktaki hataları düzeltin — script üretilemez.";
    }

    public bool GeriAcik => Sayfa > 0;
    public bool IleriAcik => Sayfa < 3;
    public string SayfaBasligi => SayfaAdlari[Sayfa];

    public string SayfaAciklamasi => Sayfa switch
    {
        0 => "Job'un adı, kategorisi, sahibi ve ne iş yaptığı.",
        1 => "Job sırayla bu adımları koşar. Sihirbazda adım türü T-SQL'dir.",
        2 => "Job'un ne zaman kendiliğinden çalışacağı. Zamanlama eklemezseniz yalnız elle başlatılır.",
        _ => "Script yeni sorgu sekmesinde açılır — çalıştırılmaz. Gözden geçirip msdb'de siz koşarsınız.",
    };

    [RelayCommand] private void Geri() { if (Sayfa > 0) Sayfa--; }

    [RelayCommand] private void Ileri() { if (Sayfa < 3) Sayfa++; }

    [RelayCommand] private void SayfayaGit(string? no) { if (int.TryParse(no, out int n) && n is >= 0 and <= 3) Sayfa = n; }

    // ── 1 · Genel ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _ad = "";
    [ObservableProperty] private bool _acik = true;
    [ObservableProperty] private string _kategori = VarsayilanKategori;
    [ObservableProperty] private string _sahip = "";
    [ObservableProperty] private string _aciklama = "";

    public ObservableCollection<string> Kategoriler { get; } = [];
    public ObservableCollection<string> Veritabanlari { get; } = [];

    partial void OnAdChanged(string value) => Denetle();

    // ── 2 · Adımlar ──────────────────────────────────────────────────────────

    public ObservableCollection<AgentAdimTaslagi> Adimlar { get; } = [];

    [ObservableProperty] private AgentAdimTaslagi? _seciliAdim;

    public IReadOnlyList<string> EylemSecenekleri => AgentAdimTaslagi.EylemSecenekleri;

    private AgentAdimTaslagi YeniAdim(string? ad = null)
    {
        var a = new AgentAdimTaslagi
        {
            Ad = ad ?? $"Adım {Adimlar.Count + 1}",
            Veritabani = Veritabanlari.FirstOrDefault() ?? "master",
        };
        a.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(AgentAdimTaslagi.DenetimSonucu) or nameof(AgentAdimTaslagi.DenetimTuru)))
                Denetle();
        };
        return a;
    }

    /// <summary>msdb adımlarını taslağa çevirir; "N. adıma git" numaraları NESNE hedefe bağlanır.</summary>
    private void AdimlariKur(IReadOnlyList<AgentAdim> kaynak)
    {
        var liste = kaynak.OrderBy(a => a.Id).Select(a => (Kaynak: a, Taslak: new AgentAdimTaslagi
        {
            AltSistem = a.AltSistem, ProxyId = a.ProxyId, CiktiDosyasi = a.CiktiDosyasi, Bayraklar = a.Bayraklar,
        })).ToList();
        // Önce hepsi listeye girer, hedefler SONRA bağlanır: her ekleme Numarala'yı tetikler ve
        // henüz listede olmayan hedefi "silinmiş" sayıp boşaltırdı (ileri atlamalar kaybolurdu).
        foreach ((AgentAdim k, AgentAdimTaslagi t) in liste)
        {
            t.Ad = k.Ad;
            t.Veritabani = k.Veritabani ?? (t.SaltGoruntu ? "—" : "master");
            t.Komut = k.Komut;
            t.BasaridaIndex = AgentAdimTaslagi.EylemIndex(k.BasaridaEylem);
            t.HatadaIndex = AgentAdimTaslagi.EylemIndex(k.HatadaEylem);
            t.YenidenDeneme = k.YenidenDeneme;
            t.DenemeAraligi = k.DenemeAraligi;
            t.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not (nameof(AgentAdimTaslagi.DenetimSonucu) or nameof(AgentAdimTaslagi.DenetimTuru)))
                    Denetle();
            };
            Adimlar.Add(t);
        }
        foreach ((AgentAdim k, AgentAdimTaslagi t) in liste)
        {
            t.BasaridaHedef = k.BasaridaEylem == 4 ? liste.FirstOrDefault(x => x.Kaynak.Id == k.BasaridaAdim).Taslak : null;
            t.HatadaHedef = k.HatadaEylem == 4 ? liste.FirstOrDefault(x => x.Kaynak.Id == k.HatadaAdim).Taslak : null;
        }
    }

    private void Numarala()
    {
        for (int i = 0; i < Adimlar.Count; i++)
            Adimlar[i].Numara = i + 1;
        foreach (AgentAdimTaslagi a in Adimlar)
        {
            if (a.BasaridaHedef is { } h1 && !Adimlar.Contains(h1))
                a.BasaridaHedef = null; // silinen adıma giden hedef boşalır — denetim yakalar
            if (a.HatadaHedef is { } h2 && !Adimlar.Contains(h2))
                a.HatadaHedef = null;
            a.MetinleriTazele();
        }
    }

    [RelayCommand]
    private void AdimEkle()
    {
        AgentAdimTaslagi a = YeniAdim();
        Adimlar.Add(a);
        SeciliAdim = a;
    }

    [RelayCommand]
    private void AdimCogalt()
    {
        if (SeciliAdim is not { SaltGoruntu: false } k)
            return;
        AgentAdimTaslagi a = YeniAdim(k.Ad + " (2)");
        a.Veritabani = k.Veritabani;
        a.Komut = k.Komut;
        a.BasaridaIndex = k.BasaridaIndex;
        a.BasaridaHedef = k.BasaridaHedef;
        a.HatadaIndex = k.HatadaIndex;
        a.HatadaHedef = k.HatadaHedef;
        a.YenidenDeneme = k.YenidenDeneme;
        a.DenemeAraligi = k.DenemeAraligi;
        Adimlar.Insert(Adimlar.IndexOf(k) + 1, a);
        SeciliAdim = a;
    }

    [RelayCommand]
    private void AdimYukari() => Tasi(-1);

    [RelayCommand]
    private void AdimAsagi() => Tasi(1);

    private void Tasi(int yon)
    {
        if (SeciliAdim is not { } a)
            return;
        int i = Adimlar.IndexOf(a), j = i + yon;
        if (j < 0 || j >= Adimlar.Count)
            return;
        Adimlar.Move(i, j);
        SeciliAdim = a;
    }

    [RelayCommand]
    private void AdimSil()
    {
        if (SeciliAdim is not { } a)
            return;
        int i = Adimlar.IndexOf(a);
        Adimlar.Remove(a);
        SeciliAdim = Adimlar.Count == 0 ? null : Adimlar[Math.Min(i, Adimlar.Count - 1)];
    }

    /// <summary>Açık sorgu sekmeleri (başlık) — "Açık sorgu sekmesinden al" menüsü.</summary>
    public IReadOnlyList<(string Baslik, string Metin)> AcikSorgular() => _acikSorgular();

    public void SorgudanAl(string metin)
    {
        if (SeciliAdim is { SaltGoruntu: false } a)
            a.Komut = metin;
    }

    /// <summary>Seçili adımın komutunu ÇALIŞTIRMADAN derler (SET NOEXEC) — kendi veritabanında.</summary>
    [RelayCommand]
    public async Task DenetleAsync()
    {
        if (SeciliAdim is not { SaltGoruntu: false } a)
            return;
        IReadOnlyList<string> parcalar = AgentSorgulari.GoIleBol(a.Komut);
        if (parcalar.Count == 0)
        {
            (a.DenetimSonucu, a.DenetimTuru) = ("Komut boş.", "hata");
            return;
        }
        a.DenetimSonucu = "Denetleniyor…";
        a.DenetimTuru = "gri";
        for (int i = 0; i < parcalar.Count; i++)
        {
            QueryResult r = await _calistir(AgentSorgulari.DenetimSql(parcalar[i]), a.Veritabani, CancellationToken.None);
            if (r.Hata is { } h)
            {
                string yer = parcalar.Count > 1 ? $"{i + 1}. parça, " : "";
                int satir = Math.Max(1, h.Satir - 1); // NOEXEC satırı düşülür
                (a.DenetimSonucu, a.DenetimTuru) = ($"⚠ {yer}satır {satir}: {h.Mesaj}", "hata");
                return;
            }
        }
        (a.DenetimSonucu, a.DenetimTuru) = ("✓ Söz dizimi geçerli", "ok");
    }

    // ── 3 · Zamanlama ────────────────────────────────────────────────────────

    public ObservableCollection<AgentZamanlamaTaslagi> Zamanlamalar { get; } = [];

    [ObservableProperty] private AgentZamanlamaTaslagi? _seciliZamanlama;

    public bool ZamanlamaYok => Zamanlamalar.Count == 0;

    [RelayCommand]
    private void ZamanlamaEkle()
    {
        var z = new AgentZamanlamaTaslagi { Ad = Zamanlamalar.Count == 0 ? "Zamanlama" : $"Zamanlama {Zamanlamalar.Count + 1}", Pzt = true, Sal = true, Car = true, Per = true, Cum = true };
        Zamanlamalar.Add(z);
        SeciliZamanlama = z;
    }

    [RelayCommand]
    private void ZamanlamaSil()
    {
        if (SeciliZamanlama is not { } z)
            return;
        Zamanlamalar.Remove(z);
        SeciliZamanlama = Zamanlamalar.FirstOrDefault();
    }

    // ── denetim + script ─────────────────────────────────────────────────────

    public ObservableCollection<string> Hatalar { get; } = [];
    public ObservableCollection<string> Uyarilar { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScriptUretCommand))]
    private bool _hataVar;

    [ObservableProperty] private string _dogrulamaOzeti = "";
    [ObservableProperty] private string _scriptOnizleme = "";

    private AgentJob JobModeli() => new(
        _istek.Job?.JobId ?? Guid.Empty, Ad.Trim(), Acik, (Kategori ?? "").Trim(), (Sahip ?? "").Trim(), Aciklama ?? "",
        null, AgentSonuc.Bilinmiyor, null, null, null, false, null, null, Adimlar.Count, Zamanlamalar.Count > 0,
        _istek.Job?.BildirimOperatoru ?? "", _istek.Job?.BildirimSeviyesiEposta ?? 0,
        _istek.Job?.BildirimSeviyesiOlay ?? 2, 1);

    public void Denetle()
    {
        Hatalar.Clear();
        Uyarilar.Clear();
        foreach ((bool hata, string mesaj) in AgentSorgulari.Dogrula(
                     JobModeli(), [.. Adimlar.Select(a => a.Model())], [.. Zamanlamalar.Select(z => z.Model())]))
            (hata ? Hatalar : Uyarilar).Add(mesaj);
        foreach (string? s in Zamanlamalar.Select(z => z.SaatHatasi).Where(s => s is not null))
            Hatalar.Add(s!);
        HataVar = Hatalar.Count > 0;
        DogrulamaOzeti = $"Ad {(string.IsNullOrWhiteSpace(Ad) ? "—" : "✓")} · {Adimlar.Count} adım · {Zamanlamalar.Count} zamanlama";
        if (Sayfa == 3)
            ScriptOnizleme = ScriptMetni() ?? "Önce alt çubuktaki hataları düzeltin — script üretilemez.";
    }

    /// <summary>Hata yoksa script; varsa null.</summary>
    public string? ScriptMetni()
    {
        if (HataVar)
            return null;
        AgentJob job = JobModeli();
        AgentAdim[] adimlar = [.. Adimlar.Select(a => a.Model())];
        AgentZamanlama[] zaman = [.. Zamanlamalar.Select(z => z.Model())];
        return Kip == AgentSihirbazKipi.Duzenle && _istek.Job is { } mevcut
            ? AgentSorgulari.GuncellemeScripti(mevcut.JobId, job, adimlar, zaman,
                [.. _istek.Zamanlamalar.Select(z => z.Id)], _istek.Sunucu, DateTime.Now)
            : AgentSorgulari.OlusturmaScripti(job, adimlar, zaman, _istek.Sunucu, DateTime.Now);
    }

    /// <summary>Pencere kapanmalı mı (script sekmeye açıldı).</summary>
    [ObservableProperty] private bool _tamamlandi;

    private bool ScriptUretebilir() => !HataVar;

    [RelayCommand(CanExecute = nameof(ScriptUretebilir))]
    private void ScriptUret()
    {
        if (ScriptMetni() is not { } script)
            return;
        string onek = Kip == AgentSihirbazKipi.Duzenle ? "agent-duzenle" : "agent-yeni";
        _scriptAc($"{onek}-{Ad.Trim()}", script);
        Tamamlandi = true;
    }
}
