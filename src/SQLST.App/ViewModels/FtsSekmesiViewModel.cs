using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Envanter satırı: FTS index'li bir tablonun özeti (salt görüntü).</summary>
public sealed record FtsEnvanterSatiri(
    string Sema, string Tablo, string Katalog, string Kolonlar, string Izleme, string Doldurma,
    long? OgeSayisi = null, long? Bekleyen = null, long? Hatali = null, string SonTur = "",
    DateTime? SonBaslangic = null, DateTime? SonBitis = null, bool DamgaVar = false,
    string Stoplist = "", bool Etkin = true, int DoldurmaKodu = 0)
{
    public string TamAd => $"{Sema}.{Tablo}";

    /// <summary>TableFulltextPopulateStatus 1–4: tam/artımlı doldurma, izleme yayılımı, arka plan güncellemesi.</summary>
    public bool DoldurmaSuruyor => DoldurmaKodu is >= 1 and <= 4;

    public string OgeMetni => OgeSayisi?.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) ?? "—";

    public string BekleyenMetni => Bekleyen is > 0 ? Bekleyen.Value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "0";

    /// <summary>"FULL · 2026-10-06 09:45:00" — ham tarih biçimi; sürüyorsa bitiş yerine "sürüyor".</summary>
    public string SonDoldurmaMetni => SonBaslangic is null ? "—"
        : $"{SonTur} · {(SonBitis ?? SonBaslangic).Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}"
          + (SonBitis is null ? " (sürüyor)" : "");

    public string EtkinMetni => Etkin ? "✔" : "— (devre dışı)";
}

/// <summary>S4 katalog satırı.</summary>
public sealed record FtsKatalogSatiri(string Ad, bool Varsayilan, long OgeSayisi, int BoyutMb, string Durum, int IndexSayisi)
{
    public string VarsayilanMetni => Varsayilan ? "★ varsayılan" : "";
    public string OgeMetni => OgeSayisi.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    public string BoyutMetni => $"{BoyutMb} MB";
}

/// <summary>S4 stoplist satırı (id 0 = sistem listesi).</summary>
public sealed record FtsStoplistSatiri(int Id, string Ad, int? KelimeSayisi)
{
    public string Gosterim => Id == 0 ? "SYSTEM (yerleşik, dile göre)" : $"{Ad} ({KelimeSayisi ?? 0} kelime)";
}

/// <summary>Sihirbazda seçilebilir metin kolonu.</summary>
public sealed partial class FtsKolonOgesi(string ad, string tip) : ObservableObject
{
    public string Ad { get; } = ad;
    public string Tip { get; } = tip;
    [ObservableProperty] private bool _secili;
}

/// <summary>
/// 🔎 FTS SEKMESİ (v23 S1+S2 — araştırma: docs/10-fts-arastirma.md; kararlar K1-K4: yalnız MSSQL,
/// canlı doğrulama kullanıcıda): üstte keşif/envanter (FTS kurulu mu · index'li tablolar ·
/// doldurma durumu), ortada CONTAINS/FREETEXT sorgu yardımcısı (kelime/önek/NEAR/çekim/serbest +
/// RANK'li), altta kurulum sihirbazı (tablo+kolon+dil+katalog → script SEKMEYE, Güvenli Yazma ile
/// kullanıcı koşar). FTS kurulu değilse (LocalDB dahil) net Türkçe yönlendirme — düğme gizlenmez.
/// </summary>
public sealed partial class FtsSekmesiViewModel : ObservableObject, ISekme
{
    [ObservableProperty] private bool _sabit;

    private readonly Func<string?, Task<SemaOnbellegi?>> _onbellekGetir;
    private readonly Func<string, string?, CancellationToken, Task<QueryResult>> _calistir; // (sql, db, ct)
    private readonly Action<string, string, string?> _sekmeAc;            // DDL — ÇALIŞTIRMADAN
    private readonly Action<string, string, string?> _sekmeAcVeCalistir;  // arama — koşarak

    private readonly Func<OnayIstegi, bool>? _onayla;   // S4 işlemleri; null → işlem yapılmaz
    private readonly bool _saltOkunur;

    public FtsSekmesiViewModel(
        IReadOnlyList<string> veritabanlari, string? aktifDb,
        Func<string?, Task<SemaOnbellegi?>> onbellekGetir,
        Func<string, string?, CancellationToken, Task<QueryResult>> calistir,
        Action<string, string, string?> sekmeAc,
        Action<string, string, string?> sekmeAcVeCalistir,
        Func<OnayIstegi, bool>? onayla = null,
        bool saltOkunur = false)
    {
        _onbellekGetir = onbellekGetir;
        _calistir = calistir;
        _sekmeAc = sekmeAc;
        _sekmeAcVeCalistir = sekmeAcVeCalistir;
        _onayla = onayla;
        _saltOkunur = saltOkunur;
        foreach (string db in veritabanlari)
            Veritabanlari.Add(db);
        _secilenVeritabani = aktifDb ?? veritabanlari.FirstOrDefault();
    }

    // ── keşif / envanter ──────────────────────────────────────────────────────
    public ObservableCollection<string> Veritabanlari { get; } = [];

    [ObservableProperty] private string? _secilenVeritabani;

    partial void OnSecilenVeritabaniChanged(string? value) => _ = YenileAsync();

    /// <summary>null = henüz bakılmadı; false = sunucuda FTS bileşeni yok (LocalDB dahil).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FtsYok))]
    private bool? _kuruluMu;

    public bool FtsYok => KuruluMu == false;

    [ObservableProperty] private string _ozet = "Yenile ile sunucunun FTS durumunu sorgulayın.";

    public ObservableCollection<FtsEnvanterSatiri> Envanter { get; } = [];

    [ObservableProperty] private FtsEnvanterSatiri? _seciliEnvanter;

    partial void OnSeciliEnvanterChanged(FtsEnvanterSatiri? value)
    {
        OnPropertyChanged(nameof(ArtimliMumkun));
        OnPropertyChanged(nameof(YonetimAcik));
        if (value is null)
            return;
        if (IzlemeModlari.Contains(value.Izleme))
            SeciliIzleme = value.Izleme;
        // Envanterden seçim yardımcıyı o tabloya kurar — tek tıkla arama.
        SeciliAramaTablo = AramaTablolari.FirstOrDefault(t => t == value.TamAd) ?? SeciliAramaTablo;
    }

    [RelayCommand]
    public async Task YenileAsync()
    {
        Envanter.Clear();
        AramaTablolari.Clear();
        KolonSecenekleri.Clear();
        Kataloglar.Clear();
        try
        {
            QueryResult kurulu = await _calistir(FtsSorgulari.KuruluMuSorgusu(), SecilenVeritabani, CancellationToken.None);
            if (kurulu.Hata is { } h)
            {
                KuruluMu = null;
                Ozet = $"⚠ Sorgulanamadı: {h.Mesaj}";
                return;
            }
            KuruluMu = kurulu.ResultSetler is [{ Satirlar: [[var deger, ..], ..] }, ..]
                && Convert.ToInt32(deger) == 1;
            if (KuruluMu != true)
            {
                Ozet = "Bu sunucuda Full-Text Search BİLEŞENİ KURULU DEĞİL (LocalDB desteklemez). "
                    + "SQL Server kurulumunda 'Full-Text and Semantic Extractions for Search' bileşeni "
                    + "eklenmeli — Express'te 'Advanced Services' sürümü gerekir. Kurulum DBA/sunucu işidir.";
                return;
            }

            await EnvanterYukleAsync();
            foreach (FtsEnvanterSatiri satir in Envanter)
                AramaTablolari.Add(satir.TamAd);

            QueryResult kataloglar = await _calistir(FtsSorgulari.KatalogSorgusu(), SecilenVeritabani, CancellationToken.None);
            foreach (object?[] s in kataloglar.ResultSetler.FirstOrDefault()?.Satirlar ?? [])
                if (s[0]?.ToString() is { Length: > 0 } ad)
                    Kataloglar.Add(ad);
            if (Kataloglar.Count == 0)
                Kataloglar.Add(VarsayilanKatalog);
            SeciliKatalog ??= Kataloglar[0];

            Ozet = Envanter.Count == 0
                ? "FTS kurulu ✓ — bu veritabanında henüz FULLTEXT INDEX yok; aşağıdaki sihirbazla kurun."
                : $"FTS kurulu ✓ — {Envanter.Count} tabloda FULLTEXT INDEX var.";
            await KataloglarYukleAsync();
            await StoplistlerYukleAsync();
            await SihirbazTablolariYukleAsync();
            CanliIzlemeGerekirseBaslat();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Ozet = $"⚠ {ex.Message}";
        }
    }

    // ── S1: arama yardımcısı ──────────────────────────────────────────────────
    public ObservableCollection<string> AramaTablolari { get; } = [];

    [ObservableProperty] private string? _seciliAramaTablo;

    partial void OnSeciliAramaTabloChanged(string? value)
    {
        KolonSecenekleri.Clear();
        KolonSecenekleri.Add(TumKolonlarEtiketi);
        FtsEnvanterSatiri? satir = Envanter.FirstOrDefault(e => e.TamAd == value);
        // Envanter kolonları "Ad (Turkish), Baslik (English)" biçiminde — adlar ayıklanır.
        foreach (string parca in (satir?.Kolonlar ?? "").Split(','))
        {
            string ad = parca.Split('(')[0].Trim();
            if (ad.Length > 0)
                KolonSecenekleri.Add(ad);
        }
        SeciliKolon = TumKolonlarEtiketi;
    }

    private const string TumKolonlarEtiketi = "(tüm kolonlar)";

    public ObservableCollection<string> KolonSecenekleri { get; } = [];

    [ObservableProperty] private string? _seciliKolon;

    /// <summary>Sıra <see cref="FtsAramaKipi"/> ile birebir.</summary>
    public IReadOnlyList<string> Kipler { get; } =
        ["Kelime / deyim", "Önek (fatu*)", "Yakınlık (NEAR)", "Çekimler (FORMSOF)", "Serbest metin (FREETEXT)"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IkinciTerimGorunur))]
    private int _secilenKipIndex;

    public bool IkinciTerimGorunur => (FtsAramaKipi)SecilenKipIndex == FtsAramaKipi.Yakinlik;

    [ObservableProperty] private string _terim = "";
    [ObservableProperty] private string _ikinciTerim = "";
    [ObservableProperty] private int _mesafe = 5;
    [ObservableProperty] private int _tavan = 100;

    /// <summary>RANK'li: CONTAINSTABLE ile skor kolonu, en alakalı üstte.</summary>
    [ObservableProperty] private bool _rankli;

    [RelayCommand]
    public async Task AraAsync()
    {
        if (SeciliAramaTablo?.Split('.') is not [var sema, var tablo])
        {
            Ozet = "Önce FTS index'li bir tablo seçin (envanterden tıklamak da seçer).";
            return;
        }
        if (string.IsNullOrWhiteSpace(Terim))
        {
            Ozet = "Aranacak terimi yazın.";
            return;
        }

        string? anahtarKolon = null;
        if (Rankli)
        {
            // CONTAINSTABLE KEY eşlemesi için tablonun tek kolonlu unique anahtarı gerekir.
            QueryResult r = await _calistir(FtsSorgulari.AnahtarIndexSorgusu(sema, tablo), SecilenVeritabani, CancellationToken.None);
            anahtarKolon = r.ResultSetler.FirstOrDefault()?.Satirlar.FirstOrDefault()?[1]?.ToString();
            if (anahtarKolon is null)
            {
                Ozet = "⚠ RANK'li arama için tek kolonlu UNIQUE anahtar bulunamadı — düz arama yapıldı.";
                Rankli = false;
            }
        }

        string sql = FtsSorgulari.AramaSorgusu(
            sema, tablo,
            SeciliKolon == TumKolonlarEtiketi ? null : SeciliKolon,
            (FtsAramaKipi)SecilenKipIndex, Terim, IkinciTerim, Mesafe, Tavan,
            Rankli, anahtarKolon);
        _sekmeAcVeCalistir($"fts-{tablo}", sql, SecilenVeritabani);
    }

    // ── S2: kurulum sihirbazı ────────────────────────────────────────────────
    private const string VarsayilanKatalog = "SqlstFtsKatalog";

    /// <summary>FTS'i OLMAYAN tablolar (sihirbaz adayları) — şema önbelleğinden.</summary>
    public ObservableCollection<string> SihirbazTablolari { get; } = [];

    [ObservableProperty] private string? _seciliSihirbazTablo;

    partial void OnSeciliSihirbazTabloChanged(string? value) => _ = SihirbazKolonlariYukleAsync(value);

    public ObservableCollection<FtsKolonOgesi> SihirbazKolonlari { get; } = [];

    public IReadOnlyList<string> Diller { get; } = [.. FtsSorgulari.DilSecenekleri.Select(d => d.Ad)];

    [ObservableProperty] private int _secilenDilIndex; // 0 = Turkish (1055)

    public ObservableCollection<string> Kataloglar { get; } = [];

    [ObservableProperty] private string? _seciliKatalog;

    [ObservableProperty] private string _sihirbazNotu = "";

    private async Task SihirbazTablolariYukleAsync()
    {
        SihirbazTablolari.Clear();
        SemaOnbellegi? onbellek = await _onbellekGetir(SecilenVeritabani);
        var ftsli = new HashSet<string>(Envanter.Select(e => e.TamAd), StringComparer.OrdinalIgnoreCase);
        foreach (SemaNesnesi t in (onbellek?.Nesneler ?? [])
                     .Where(n => n.Tur == SemaNesneTuru.Tablo && !ftsli.Contains($"{n.Sema}.{n.Ad}"))
                     .OrderBy(n => n.TamAd, StringComparer.OrdinalIgnoreCase))
            SihirbazTablolari.Add($"{t.Sema}.{t.Ad}");
    }

    private async Task SihirbazKolonlariYukleAsync(string? tamAd)
    {
        SihirbazKolonlari.Clear();
        SihirbazNotu = "";
        if (tamAd?.Split('.') is not [var sema, var tablo])
            return;
        SemaOnbellegi? onbellek = await _onbellekGetir(SecilenVeritabani);
        SemaNesnesi? nesne = (onbellek?.Nesneler ?? []).FirstOrDefault(n =>
            n.Tur == SemaNesneTuru.Tablo
            && n.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase)
            && n.Ad.Equals(tablo, StringComparison.OrdinalIgnoreCase));
        foreach (SemaKolonu k in (nesne?.Kolonlar ?? []).Where(k => MetinTipi(k.Tip)))
            SihirbazKolonlari.Add(new FtsKolonOgesi(k.Ad, k.Tip));
        if (SihirbazKolonlari.Count == 0)
            SihirbazNotu = "Bu tabloda metin (char/text) tipli kolon yok — FTS kurulacak kolon bulunamadı.";
    }

    private static bool MetinTipi(string tip)
        => tip.Contains("char", StringComparison.OrdinalIgnoreCase)
           || tip.Contains("text", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    public async Task ScriptUretAsync()
    {
        if (SeciliSihirbazTablo?.Split('.') is not [var sema, var tablo])
        {
            SihirbazNotu = "Önce sihirbazdan bir tablo seçin.";
            return;
        }
        List<(string Kolon, int Lcid)> secili = [.. SihirbazKolonlari
            .Where(k => k.Secili)
            .Select(k => (k.Ad, FtsSorgulari.DilSecenekleri[SecilenDilIndex].Lcid))];
        if (secili.Count == 0)
        {
            SihirbazNotu = "En az bir metin kolonu işaretleyin.";
            return;
        }

        QueryResult anahtar = await _calistir(FtsSorgulari.AnahtarIndexSorgusu(sema, tablo), SecilenVeritabani, CancellationToken.None);
        string? anahtarIndex = anahtar.ResultSetler.FirstOrDefault()?.Satirlar.FirstOrDefault()?[0]?.ToString();
        if (anahtarIndex is null)
        {
            // FTS'in ön şartı — dürüstçe söylenir, uydurma script üretilmez.
            SihirbazNotu = "⚠ FULLTEXT INDEX, TEK kolonlu + UNIQUE + NOT NULL bir anahtar index ister "
                + "(tipik PK). Bu tabloda yok — önce uygun bir unique index/PK oluşturun.";
            return;
        }

        string katalog = string.IsNullOrWhiteSpace(SeciliKatalog) ? VarsayilanKatalog : SeciliKatalog!;
        bool yeni = !Kataloglar.Contains(katalog) || katalog == VarsayilanKatalog;
        string script = FtsSorgulari.KurulumScripti(sema, tablo, secili, katalog, anahtarIndex, yeni);
        _sekmeAc($"fts-kurulum-{tablo}", script, SecilenVeritabani); // ÇALIŞTIRMADAN — Güvenli Yazma
        SihirbazNotu = $"Script sekmede açıldı — gözden geçirip Güvenli Yazma açıkken çalıştırın "
            + $"({secili.Count} kolon · {FtsSorgulari.DilSecenekleri[SecilenDilIndex].Ad} · anahtar: {anahtarIndex}).";
    }

    /// <summary>Envanterde sağ tık → silme script'i (o da çalıştırmadan sekmeye).</summary>
    [RelayCommand]
    public void SilmeScriptiUret()
    {
        if (SeciliEnvanter is not { } e)
            return;
        _sekmeAc($"fts-sil-{e.Tablo}", FtsSorgulari.SilmeScripti(e.Sema, e.Tablo), SecilenVeritabani);
    }

    // ── S4: yönetim (doldurma · izleme · katalog · stoplist) ─────────────────

    /// <summary>Envanteri okur; seçili satırı (tablo adıyla) korur — canlı izleme de bunu kullanır.</summary>
    private async Task EnvanterYukleAsync()
    {
        string? secili = SeciliEnvanter?.TamAd;
        QueryResult envanter = await _calistir(FtsSorgulari.EnvanterSorgusu(), SecilenVeritabani, CancellationToken.None);
        if (envanter.Hata is not null)
            return;
        Envanter.Clear();
        foreach (object?[] s in envanter.ResultSetler.FirstOrDefault()?.Satirlar ?? [])
            Envanter.Add(EnvanterSatiri(s));
        SeciliEnvanter = Envanter.FirstOrDefault(e => e.TamAd == secili);
        OnPropertyChanged(nameof(DoldurmaSuruyorMu));
    }

    /// <summary>Eski (6 kolonlu) yanıtlara da dayanıklı — ek kolonlar yoksa varsayılan kalır.</summary>
    private static FtsEnvanterSatiri EnvanterSatiri(object?[] s)
    {
        string M(int i) => i < s.Length ? s[i]?.ToString() ?? "" : "";
        long? L(int i) => i < s.Length && s[i] is not null and not DBNull ? Convert.ToInt64(s[i], System.Globalization.CultureInfo.InvariantCulture) : null;
        DateTime? T(int i) => i < s.Length && s[i] is DateTime t ? t : null;
        return new FtsEnvanterSatiri(
            M(0), M(1), M(2), M(3), M(4), M(5),
            L(6), L(7), L(8), M(9), T(10), T(11), L(12) == 1, M(13),
            L(14) is not 0, (int)(L(15) ?? 0));
    }

    public ObservableCollection<FtsKatalogSatiri> KatalogDetaylari { get; } = [];

    [ObservableProperty] private FtsKatalogSatiri? _seciliKatalogDetay;

    private async Task KataloglarYukleAsync()
    {
        string? secili = SeciliKatalogDetay?.Ad;
        KatalogDetaylari.Clear();
        QueryResult r = await _calistir(FtsSorgulari.KatalogDetaySorgusu(), SecilenVeritabani, CancellationToken.None);
        foreach (object?[] s in r.ResultSetler.FirstOrDefault()?.Satirlar ?? [])
        {
            if (s.Length < 5)
                continue; // beklenmeyen biçim — uydurma satır kurulmaz
            string ad = s[0]?.ToString() ?? "";
            KatalogDetaylari.Add(new FtsKatalogSatiri(
                ad, Convert.ToInt32(s[1] ?? 0) == 1, Convert.ToInt64(s[2] ?? 0L), Convert.ToInt32(s[3] ?? 0),
                s[4]?.ToString() ?? "", Envanter.Count(e => e.Katalog == ad)));
        }
        SeciliKatalogDetay = KatalogDetaylari.FirstOrDefault(k => k.Ad == secili);
    }

    public ObservableCollection<FtsStoplistSatiri> Stoplistler { get; } = [];

    [ObservableProperty] private FtsStoplistSatiri? _seciliStoplist;

    partial void OnSeciliStoplistChanged(FtsStoplistSatiri? value) => _ = StopKelimeleriYukleAsync();

    /// <summary>Stop kelime dili — <see cref="Diller"/> sırasıyla (0 Turkish).</summary>
    [ObservableProperty] private int _stopDilIndex;

    partial void OnStopDilIndexChanged(int value) => _ = StopKelimeleriYukleAsync();

    public ObservableCollection<string> StopKelimeleri { get; } = [];

    [ObservableProperty] private string _stopKelimeOzeti = "";

    private async Task StoplistlerYukleAsync()
    {
        Stoplistler.Clear();
        QueryResult r = await _calistir(FtsSorgulari.StoplistSorgusu(), SecilenVeritabani, CancellationToken.None);
        foreach (object?[] s in r.ResultSetler.FirstOrDefault()?.Satirlar ?? [])
            if (s.Length >= 3 && s[0] is not null)
                Stoplistler.Add(new FtsStoplistSatiri(Convert.ToInt32(s[0]), s[1]?.ToString() ?? "",
                    s[2] is null or DBNull ? null : Convert.ToInt32(s[2])));
        SeciliStoplist = Stoplistler.FirstOrDefault();
    }

    private async Task StopKelimeleriYukleAsync()
    {
        StopKelimeleri.Clear();
        StopKelimeOzeti = "";
        if (SeciliStoplist is not { } sl)
            return;
        (string dil, int lcid) = FtsSorgulari.DilSecenekleri[Math.Clamp(StopDilIndex, 0, FtsSorgulari.DilSecenekleri.Count - 1)];
        QueryResult r = await _calistir(FtsSorgulari.StopKelimeSorgusu(sl.Id, lcid), SecilenVeritabani, CancellationToken.None);
        if (SeciliStoplist != sl)
            return;
        foreach (object?[] s in r.ResultSetler.FirstOrDefault()?.Satirlar ?? [])
            if (s.Length > 0 && s[0]?.ToString() is { Length: > 0 } k)
                StopKelimeleri.Add(k);
        StopKelimeOzeti = $"{StopKelimeleri.Count} kelime · {dil} — bu kelimeler index'e girmez, aramada yok sayılır.";
    }

    /// <summary>Yönetim satırının bilgi/sonuç metni.</summary>
    [ObservableProperty] private string _yonetimNotu = "";

    public IReadOnlyList<string> IzlemeModlari { get; } = ["AUTO", "MANUAL", "OFF"];

    [ObservableProperty] private string _seciliIzleme = "AUTO";

    /// <summary>Artımlı doldurma tabloda timestamp/rowversion kolonu ister.</summary>
    public bool ArtimliMumkun => SeciliEnvanter?.DamgaVar == true;

    /// <summary>Şerit seçimle açılır; salt-okunur kapısı işlemin kendisinde (script düğmeleri yine çalışsın).</summary>
    public bool YonetimAcik => SeciliEnvanter is not null;

    public bool KatalogSecili => SeciliKatalogDetay is not null;

    partial void OnSeciliKatalogDetayChanged(FtsKatalogSatiri? value) => OnPropertyChanged(nameof(KatalogSecili));

    public bool DoldurmaSuruyorMu => Envanter.Any(e => e.DoldurmaSuruyor);

    [RelayCommand] public Task TamDoldurAsync() => DoldurAsync(FtsDoldurma.Tam);

    [RelayCommand] public Task ArtimliDoldurAsync() => DoldurAsync(FtsDoldurma.Artimli);

    [RelayCommand] public Task BekleyenleriIsleAsync() => DoldurAsync(FtsDoldurma.Guncelle);

    private Task DoldurAsync(FtsDoldurma tur)
    {
        if (SeciliEnvanter is not { } e)
            return Task.CompletedTask;
        if (tur == FtsDoldurma.Artimli && !e.DamgaVar)
        {
            YonetimNotu = "Artımlı doldurma tabloda timestamp/rowversion kolonu ister — bu tabloda yok; Tam doldurmayı kullanın.";
            return Task.CompletedTask;
        }
        OnayIstegi soru = tur switch
        {
            FtsDoldurma.Tam => new("Tam doldurma başlatılsın mı?",
                $"{e.TamAd} için TAM doldurma: tüm tablo baştan taranır; büyük tabloda uzun sürer ve sunucuya yük bindirir.", "▶ Başlat"),
            FtsDoldurma.Artimli => new("Artımlı doldurma başlatılsın mı?",
                $"{e.TamAd} için ARTIMLI doldurma: yalnız son doldurmadan beri değişen satırlar işlenir.", "▶ Başlat"),
            _ => new("Bekleyen değişiklikler işlensin mi?",
                $"{e.TamAd} için bekleyen {e.BekleyenMetni} değişiklik index'e yansıtılacak.", "⇪ İşle"),
        };
        string ad = tur switch { FtsDoldurma.Tam => "Tam", FtsDoldurma.Artimli => "Artımlı", _ => "Bekleyenleri işleme" };
        return IslemAsync(soru, FtsSorgulari.DoldurmaSql(e.Sema, e.Tablo, tur), $"▶ {ad} doldurma başladı — durum canlı izleniyor.");
    }

    [RelayCommand]
    public Task DoldurmaDurdurAsync()
        => SeciliEnvanter is { } e
            ? IslemAsync(new OnayIstegi("Doldurma durdurulsun mu?",
                    $"{e.TamAd} için süren doldurma durdurulacak — index yarım dolu kalır.", "■ Durdur", Tehlikeli: true),
                FtsSorgulari.DoldurmaDurdurSql(e.Sema, e.Tablo), "■ Doldurma durduruldu.")
            : Task.CompletedTask;

    [RelayCommand]
    public Task IzlemeUygulaAsync()
    {
        if (SeciliEnvanter is not { } e)
            return Task.CompletedTask;
        string aciklama = SeciliIzleme switch
        {
            "AUTO" => "değişiklikler otomatik index'e yansır",
            "MANUAL" => "değişiklikler birikir, 'Bekleyenleri işle' ile yansır",
            _ => "değişiklik izlenmez; yalnız elle Tam doldurma güncel tutar",
        };
        return IslemAsync(new OnayIstegi($"Değişiklik izleme {SeciliIzleme} yapılsın mı?",
                $"{e.TamAd}: {aciklama}.", "Uygula"),
            FtsSorgulari.IzlemeSql(e.Sema, e.Tablo, SeciliIzleme), $"✔ Değişiklik izleme: {SeciliIzleme}.");
    }

    [RelayCommand]
    public Task EtkinlikDegistirAsync()
    {
        if (SeciliEnvanter is not { } e)
            return Task.CompletedTask;
        bool yeni = !e.Etkin;
        return IslemAsync(
            yeni ? new OnayIstegi("Index etkinleştirilsin mi?", $"{e.TamAd} FULLTEXT INDEX'i yeniden kullanıma açılacak.", "▶ Etkinleştir")
                 : new OnayIstegi("Index devre dışı bırakılsın mı?",
                     $"{e.TamAd}: CONTAINS/FREETEXT bu tabloda hata verir, Veri Arama LIKE'a döner.", "⏸ Devre dışı bırak", Tehlikeli: true),
            FtsSorgulari.EtkinlikSql(e.Sema, e.Tablo, yeni), yeni ? "✔ Index etkinleştirildi." : "⏸ Index devre dışı.");
    }

    [RelayCommand]
    public Task KatalogDuzenleAsync()
        => SeciliKatalogDetay is { } k
            ? IslemAsync(new OnayIstegi("Katalog birleştirilsin mi?",
                    $"{k.Ad} (REORGANIZE): parçalı index'ler toplanır; çevrimiçi ve hafif bir iştir.", "⧉ Birleştir"),
                FtsSorgulari.KatalogDuzenleSql(k.Ad), $"✔ {k.Ad} birleştirildi.")
            : Task.CompletedTask;

    [RelayCommand]
    public Task KatalogVarsayilanYapAsync()
        => SeciliKatalogDetay is { Varsayilan: false } k
            ? IslemAsync(new OnayIstegi("Varsayılan katalog değişsin mi?",
                    $"{k.Ad} varsayılan olacak — katalog belirtilmeden kurulan yeni index'ler buraya girer.", "★ Varsayılan yap"),
                FtsSorgulari.KatalogVarsayilanSql(k.Ad), $"★ {k.Ad} artık varsayılan katalog.")
            : Task.CompletedTask;

    [RelayCommand]
    public void KatalogYenidenKurScriptiUret()
    {
        if (SeciliKatalogDetay is { } k)
            _sekmeAc($"fts-katalog-rebuild-{k.Ad}", FtsSorgulari.KatalogYenidenKurScripti(k.Ad), SecilenVeritabani);
    }

    [RelayCommand]
    public void KatalogSilmeScriptiUret()
    {
        if (SeciliKatalogDetay is { } k)
            _sekmeAc($"fts-katalog-sil-{k.Ad}",
                FtsSorgulari.KatalogSilmeScripti(k.Ad, [.. Envanter.Where(e => e.Katalog == k.Ad).Select(e => e.TamAd)]),
                SecilenVeritabani);
    }

    /// <summary>Doğrudan çalışan yönetim işlemi: salt-okunur kapısı + onay → çalıştır → durumu tazele.</summary>
    private async Task IslemAsync(OnayIstegi soru, string sql, string basari)
    {
        if (_saltOkunur)
        {
            YonetimNotu = "🔒 Salt-okunur bağlantı: Full-Text yönetim işlemleri kapalı.";
            return;
        }
        if (_onayla is null || !_onayla(soru))
            return;
        QueryResult r = await _calistir(sql, SecilenVeritabani, CancellationToken.None);
        if (r.Hata is { } h)
        {
            YonetimNotu = $"⚠ [{h.Numara}] {h.Mesaj}";
            return;
        }
        YonetimNotu = basari;
        await DurumTazeleAsync();
        CanliIzlemeGerekirseBaslat();
    }

    /// <summary>Yalnız envanter + kataloglar (seçimler korunur) — arama/sihirbaz alanlarına dokunmaz.</summary>
    public async Task DurumTazeleAsync()
    {
        await EnvanterYukleAsync();
        await KataloglarYukleAsync();
    }

    // ── canlı izleme: doldurma sürerken 5 sn'de bir durum tazelenir, bitince kendiliğinden durur ──

    /// <summary>Canlı izleme aralığı (sn) — testler kısaltabilsin.</summary>
    public int CanliIzlemeSn { get; set; } = 5;

    [ObservableProperty] private bool _canliIzleniyor;

    private CancellationTokenSource? _izlemeCts;

    private void CanliIzlemeGerekirseBaslat()
    {
        if (CanliIzleniyor || !DoldurmaSuruyorMu)
            return;
        _izlemeCts = new CancellationTokenSource();
        _ = CanliIzlemeAsync(_izlemeCts.Token);
    }

    /// <summary>PeriodicTimer'a jeton VERİLMEZ (iptalde fırlatır — Profiler'daki bilinen tuzak); iptal Dispose ile.</summary>
    private async Task CanliIzlemeAsync(CancellationToken ct)
    {
        CanliIzleniyor = true;
        try
        {
            using var tik = new PeriodicTimer(TimeSpan.FromSeconds(CanliIzlemeSn));
            await using CancellationTokenRegistration kayit = ct.Register(tik.Dispose);
            while (await tik.WaitForNextTickAsync())
            {
                if (ct.IsCancellationRequested)
                    return;
                await DurumTazeleAsync();
                if (!DoldurmaSuruyorMu)
                {
                    YonetimNotu = "✔ Doldurma bitti — index güncel.";
                    return;
                }
            }
        }
        finally
        {
            CanliIzleniyor = false;
        }
    }

    // ── ISekme ────────────────────────────────────────────────────────────────
    public string Baslik => "🔎 FTS";

    public SekmeDurumu Durum => SekmeDurumu.Tamamlandi;

    public Task KapatAsync()
    {
        _izlemeCts?.Cancel();
        return Task.CompletedTask;
    }
}
