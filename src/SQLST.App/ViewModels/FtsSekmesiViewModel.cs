using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Envanter satırı: FTS index'li bir tablonun özeti (salt görüntü).</summary>
public sealed record FtsEnvanterSatiri(
    string Sema, string Tablo, string Katalog, string Kolonlar, string Izleme, string Doldurma)
{
    public string TamAd => $"{Sema}.{Tablo}";
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

    public FtsSekmesiViewModel(
        IReadOnlyList<string> veritabanlari, string? aktifDb,
        Func<string?, Task<SemaOnbellegi?>> onbellekGetir,
        Func<string, string?, CancellationToken, Task<QueryResult>> calistir,
        Action<string, string, string?> sekmeAc,
        Action<string, string, string?> sekmeAcVeCalistir)
    {
        _onbellekGetir = onbellekGetir;
        _calistir = calistir;
        _sekmeAc = sekmeAc;
        _sekmeAcVeCalistir = sekmeAcVeCalistir;
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
        if (value is null)
            return;
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

            QueryResult envanter = await _calistir(FtsSorgulari.EnvanterSorgusu(), SecilenVeritabani, CancellationToken.None);
            foreach (object?[] s in envanter.ResultSetler.FirstOrDefault()?.Satirlar ?? [])
            {
                var satir = new FtsEnvanterSatiri(
                    s[0]?.ToString() ?? "", s[1]?.ToString() ?? "", s[2]?.ToString() ?? "",
                    s[3]?.ToString() ?? "", s[4]?.ToString() ?? "", s[5]?.ToString() ?? "");
                Envanter.Add(satir);
                AramaTablolari.Add(satir.TamAd);
            }

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
            await SihirbazTablolariYukleAsync();
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

    // ── ISekme ────────────────────────────────────────────────────────────────
    public string Baslik => "🔎 FTS";

    public SekmeDurumu Durum => SekmeDurumu.Tamamlandi;

    public Task KapatAsync() => Task.CompletedTask;
}
