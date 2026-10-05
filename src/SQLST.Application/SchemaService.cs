using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Şema okuyucu (FG-2.5): katalog SQL'ini ve tip/tür yorumunu <see cref="ILehce"/>'den alır,
/// sonucu KANONİK biçimde (motordan bağımsız kolon sırası) ayrıştırır. Böylece şema okuma
/// motor-nötrdür — yeni motor eklemek yeni bir lehçe yazmaktır, bu sınıfa dokunmamaktır.
///
/// V3-S1 / Faz 1: v2'de buradaki inline sys.* SQL'i MssqlLehcesi'ne taşındı; ayrıştırma
/// ve şekil aynı kaldı (SchemaServiceTests değişmedi — davranış korundu).
/// </summary>
public sealed class SchemaService : ISchemaService
{
    private readonly ISqlExecutor _executor;
    private readonly ILehceSaglayici _lehceler;

    /// <summary>Tek lehçeli kurulum (testler): her profil bu lehçeyle okunur.</summary>
    public SchemaService(ISqlExecutor executor, ILehce lehce)
        : this(executor, new SabitTekLehce(lehce)) { }

    /// <summary>Çoklu motor kurulumu (V3-S1): lehçe her çağrıda profilin motorundan çözülür.</summary>
    public SchemaService(ISqlExecutor executor, ILehceSaglayici lehceler)
    {
        _executor = executor;
        _lehceler = lehceler;
    }

    private sealed class SabitTekLehce(ILehce lehce) : ILehceSaglayici
    {
        public ILehce Getir(MotorTuru motor) => lehce;
    }

    public async Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct)
    {
        ILehce lehce = _lehceler.Getir(profil.Motor);
        QueryResult sonuc = await _executor.ExecuteAsync(profil, lehce.VeritabanlariSorgusu, ExecuteOptions.Varsayilan, ct);

        if (sonuc.IptalEdildi)
            throw new OperationCanceledException(ct);
        if (!sonuc.Basarili || sonuc.ResultSetler.Count == 0)
            throw new InvalidOperationException($"Veritabanları listelenemedi: {sonuc.Hata?.Mesaj ?? "beklenmeyen sonuç"}");

        return [.. sonuc.ResultSetler[0].Satirlar.Select(r =>
            new VeritabaniBilgisi((string)r[0]!, Convert.ToInt32(r[1]) == 1))];
    }

    public async Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
    {
        ILehce lehce = _lehceler.Getir(profil.Motor);

        // Lehçenin sorguları sırayla çalıştırılır, sonuç kümeleri SIRAYLA birleştirilir —
        // MSSQL/PG tek batch'te 4 küme döndürür (tek round-trip), Oracle 4 ayrı sorgu verir.
        var setler = new List<ResultSetData>();
        foreach (string sorgu in lehce.SemaSorgulari)
        {
            QueryResult parca = await _executor.ExecuteAsync(
                profil, sorgu,
                new ExecuteOptions { SatirSiniri = 1_000_000, VeritabaniOverride = veritabani, TumKumeler = true }, ct);

            if (parca.IptalEdildi)
                throw new OperationCanceledException(ct);
            if (!parca.Basarili)
                throw new InvalidOperationException($"Şema okunamadı: {parca.Hata?.Mesaj ?? "beklenmeyen sonuç"}");
            setler.AddRange(parca.ResultSetler);
        }

        if (setler.Count < 4)
            throw new InvalidOperationException("Şema okunamadı: beklenmeyen sonuç (4 küme gelmedi).");

        QueryResult sonuc = new() { Basarili = true, ResultSetler = setler };
        string dbAdi = (string)sonuc.ResultSetler[0].Satirlar[0][0]!;

        // [2] kolonlar → (sema.ad) anahtarıyla grupla
        var kolonlar = new Dictionary<string, List<SemaKolonu>>(StringComparer.OrdinalIgnoreCase);
        foreach (object?[] r in sonuc.ResultSetler[2].Satirlar)
        {
            string anahtar = $"{r[0]}.{r[1]}";
            if (!kolonlar.TryGetValue(anahtar, out List<SemaKolonu>? liste))
                kolonlar[anahtar] = liste = [];
            liste.Add(new SemaKolonu(
                Ad: (string)r[2]!,
                Tip: lehce.TipYaz((string)r[3]!, Convert.ToInt32(r[4]), Convert.ToInt32(r[5]), Convert.ToInt32(r[6])),
                NullOlabilir: Convert.ToBoolean(r[7]), // truthy: bool (MSSQL/PG) veya 1/0 (MySQL)
                PkMi: Convert.ToInt32(r[8]) == 1));
        }

        // [3] parametreler → (sema.ad) anahtarıyla grupla
        var parametreler = new Dictionary<string, List<SemaParametresi>>(StringComparer.OrdinalIgnoreCase);
        foreach (object?[] r in sonuc.ResultSetler[3].Satirlar)
        {
            string anahtar = $"{r[0]}.{r[1]}";
            if (!parametreler.TryGetValue(anahtar, out List<SemaParametresi>? liste))
                parametreler[anahtar] = liste = [];
            liste.Add(new SemaParametresi(
                Ad: (string)r[2]!,
                Tip: lehce.TipYaz((string)r[3]!, Convert.ToInt32(r[4]), Convert.ToInt32(r[5]), Convert.ToInt32(r[6])),
                CikisMi: Convert.ToBoolean(r[7]))); // truthy: bool veya 1/0
        }

        // [1] nesneler
        var nesneler = new List<SemaNesnesi>();
        foreach (object?[] r in sonuc.ResultSetler[1].Satirlar)
        {
            string sema = (string)r[0]!, ad = (string)r[1]!;
            SemaNesneTuru tur = lehce.TurCevir((string)r[2]!);
            string anahtar = $"{sema}.{ad}";
            nesneler.Add(new SemaNesnesi(
                Veritabani: dbAdi,
                Sema: sema,
                Ad: ad,
                Tur: tur,
                Kolonlar: kolonlar.TryGetValue(anahtar, out List<SemaKolonu>? k) ? k : [],
                Parametreler: parametreler.TryGetValue(anahtar, out List<SemaParametresi>? p) ? p : []));
        }

        return new SemaOnbellegi { Nesneler = nesneler, YuklenmeZamaniUtc = DateTime.UtcNow };
    }

    public async Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
    {
        ILehce lehce = _lehceler.Getir(profil.Motor);
        QueryResult sonuc = await _executor.ExecuteAsync(
            profil, lehce.TanimSorgusu(nesne), new ExecuteOptions { VeritabaniOverride = nesne.Veritabani, TumKumeler = true }, ct);

        if (sonuc.IptalEdildi)
            throw new OperationCanceledException(ct);
        if (!sonuc.Basarili || sonuc.ResultSetler.Count == 0)
            throw new InvalidOperationException($"Tanım okunamadı: {sonuc.Hata?.Mesaj ?? "beklenmeyen sonuç"}");

        // Kanonik sözleşme: bir VEYA çok satır, ilk kolon tanım parçası (Oracle ALL_SOURCE
        // satır-satır verir, MSSQL/PG tek satır). String olmayan (CLOB sarmalayıcı vb.)
        // ToString ile alınır; hiç parça yoksa tanım yok (WITH ENCRYPTION / bulunamadı).
        var parcalar = sonuc.ResultSetler[0].Satirlar
            .Select(r => r.Length > 0 ? r[0] : null)
            .Where(v => v is not (null or DBNull))
            .Select(v => v as string ?? v!.ToString() ?? "")
            .ToList();
        return parcalar.Count == 0 ? null : string.Concat(parcalar);
    }

    public async Task<DuzenlemeMetasi> DuzenlemeMetaAsync(ConnectionProfile profil, SemaNesnesi tablo, CancellationToken ct)
    {
        ILehce lehce = _lehceler.Getir(profil.Motor);
        QueryResult sonuc = await _executor.ExecuteAsync(
            profil, lehce.DuzenlemeMetaSorgusu(tablo), new ExecuteOptions { VeritabaniOverride = tablo.Veritabani, TumKumeler = true }, ct);

        if (sonuc.IptalEdildi)
            throw new OperationCanceledException(ct);
        if (!sonuc.Basarili || sonuc.ResultSetler.Count == 0 || sonuc.ResultSetler[0].Satirlar.Count == 0)
            throw new InvalidOperationException($"{tablo.TamAd}: kolon üst verisi okunamadı ({sonuc.Hata?.Mesaj ?? "tablo bulunamadı"}).");

        var kolonlar = new List<DuzenlemeKolonu>();
        foreach (object?[] r in sonuc.ResultSetler[0].Satirlar)
        {
            string sysTip = (string)r[1]!;
            int maxLength = Convert.ToInt32(r[2]);
            kolonlar.Add(new DuzenlemeKolonu(
                Ad: (string)r[0]!,
                SysTip: sysTip,
                GosterimTipi: lehce.TipYaz(sysTip, maxLength, Convert.ToInt32(r[3]), Convert.ToInt32(r[4])),
                // truthy: bool (MSSQL/PG) veya 1/0 (MySQL sayısal, Oracle NUMBER) — V4-S1
                NullOlabilir: Convert.ToBoolean(r[5]),
                IdentityMi: Convert.ToBoolean(r[6]),
                ComputedMi: Convert.ToBoolean(r[7]),
                RowversionMi: Convert.ToInt32(r[8]) == 1,
                PkMi: Convert.ToInt32(r[9]) == 1,
                KiyasGuvenliMi: lehce.KiyasGuvenliMi(sysTip, maxLength)));
        }

        // DECLARED PK YOKSA IDENTITY'ye düş (kullanıcı bulgusu 2026-07-27: "sen listelenen satırların
        // PK'sını biliyorsun zaten"): PK constraint'i tanımlı olmayan ama IDENTITY sütunlu tablolar
        // (ör. Yonetim.KullaniciRolleri.Id) düzenlenemiyordu. IDENTITY pratikte benzersizdir ve satır
        // kimliği olarak güvenlidir; TEK identity varsa onu anahtar sayıp düzenlemeyi açarız. Çoklu
        // identity ya da identity yoksa davranış değişmez (salt-okunur — kimlik gerçekten belirsizdir).
        if (!kolonlar.Any(k => k.PkMi))
        {
            List<DuzenlemeKolonu> identiteler = [.. kolonlar.Where(k => k.IdentityMi)];
            if (identiteler.Count == 1)
            {
                int i = kolonlar.IndexOf(identiteler[0]);
                kolonlar[i] = kolonlar[i] with { PkMi = true };
            }
        }

        return new DuzenlemeMetasi(tablo.Veritabani, tablo.Sema, tablo.Ad, kolonlar);
    }

    public async Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(
        ConnectionProfile profil, string veritabani, CancellationToken ct)
    {
        ILehce lehce = _lehceler.Getir(profil.Motor);
        QueryResult sonuc = await _executor.ExecuteAsync(
            profil, lehce.YabanciAnahtarSorgusu,
            new ExecuteOptions { SatirSiniri = 1_000_000, VeritabaniOverride = veritabani, TumKumeler = true }, ct);

        if (sonuc.IptalEdildi)
            throw new OperationCanceledException(ct);
        if (!sonuc.Basarili)
            throw new InvalidOperationException($"Yabancı anahtarlar okunamadı: {sonuc.Hata?.Mesaj ?? "beklenmeyen sonuç"}");
        if (sonuc.ResultSetler.Count == 0)
            return [];

        // Kanonik satır-başına-kolon: [0] fk-kimliği, [1] sıra, [2..4] kaynak, [5..7] hedef,
        // [8] OPSİYONEL kısıt adı (v19-S4 — hedefteki fazla FK'nın DROP'u için; eski/eksik
        // sorgular 8 kolonla da çalışır). Aynı fk-kimliğinin satırları bir bileşik anahtardır;
        // SIRAYA göre birleştirilir (STRING_AGG yok → gruplama burada).
        var gruplar = new Dictionary<string, (string KS, string KT, string HS, string HT, string? Ad,
            List<(int Sira, string Kaynak, string Hedef)> Kolonlar)>(StringComparer.Ordinal);
        var sira = new List<string>();

        foreach (object?[] r in sonuc.ResultSetler[0].Satirlar)
        {
            string fkId = r[0]?.ToString() ?? "";
            if (!gruplar.TryGetValue(fkId, out var g))
            {
                g = ((string)r[2]!, (string)r[3]!, (string)r[5]!, (string)r[6]!,
                    r.Length > 8 ? r[8]?.ToString() : null, []);
                gruplar[fkId] = g;
                sira.Add(fkId);
            }
            g.Kolonlar.Add((Convert.ToInt32(r[1]), (string)r[4]!, (string)r[7]!));
        }

        return [.. sira.Select(id =>
        {
            var g = gruplar[id];
            var sirali = g.Kolonlar.OrderBy(k => k.Sira).ToList();
            return new YabanciAnahtar(
                g.KS, g.KT, [.. sirali.Select(k => k.Kaynak)],
                g.HS, g.HT, [.. sirali.Select(k => k.Hedef)], g.Ad);
        })];
    }

    public async Task<IReadOnlyList<Indeks>> IndekslerAsync(
        ConnectionProfile profil, string veritabani, CancellationToken ct)
    {
        ILehce lehce = _lehceler.Getir(profil.Motor);
        if (string.IsNullOrEmpty(lehce.IndeksSorgusu))
            return []; // bu motorda index okuma yok

        QueryResult sonuc = await _executor.ExecuteAsync(
            profil, lehce.IndeksSorgusu,
            new ExecuteOptions { SatirSiniri = 1_000_000, VeritabaniOverride = veritabani, TumKumeler = true }, ct);

        if (sonuc.IptalEdildi)
            throw new OperationCanceledException(ct);
        if (!sonuc.Basarili)
            throw new InvalidOperationException($"İndeksler okunamadı: {sonuc.Hata?.Mesaj ?? "beklenmeyen sonuç"}");
        if (sonuc.ResultSetler.Count == 0)
            return [];

        // Kanonik: [0] sema, [1] tablo, [2] indeks, [3] benzersiz truthy, [4] sıra, [5] kolon.
        // Aynı (sema.tablo.indeks) satırları tek indeksin kolonlarıdır; sıraya göre birleşir
        // (STRING_AGG yok → gruplama burada).
        var gruplar = new Dictionary<string, (string S, string T, string Ad, bool U,
            List<(int Sira, string Kolon)> Kolonlar)>(StringComparer.Ordinal);
        var sira = new List<string>();

        foreach (object?[] r in sonuc.ResultSetler[0].Satirlar)
        {
            string anahtar = $"{r[0]}.{r[1]}.{r[2]}";
            if (!gruplar.TryGetValue(anahtar, out var g))
            {
                g = ((string)r[0]!, (string)r[1]!, (string)r[2]!, Convert.ToInt32(r[3]) != 0, []);
                gruplar[anahtar] = g;
                sira.Add(anahtar);
            }
            g.Kolonlar.Add((Convert.ToInt32(r[4]), (string)r[5]!));
        }

        return [.. sira.Select(k =>
        {
            var g = gruplar[k];
            return new Indeks(g.S, g.T, g.Ad, g.U, [.. g.Kolonlar.OrderBy(c => c.Sira).Select(c => c.Kolon)]);
        })];
    }
}
