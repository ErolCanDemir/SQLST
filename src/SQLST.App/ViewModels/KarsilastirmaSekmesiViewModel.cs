using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Grid satırı: bir şema farkının kullanıcıya dönük gösterimi (yön simgesi + metinler).</summary>
public sealed record SemaFarkGorunumu(string Yon, string Nesne, string Kapsam, string Detay)
{
    /// <summary>☑ eşitleme seçimi (v19-S12, canlı test 2026-08-04: "satırları nasıl seçeceğiz,
    /// seçim kolonu koyalım") — grid'deki onay kutusu kolonu buna bağlanır.</summary>
    public bool Secili { get; set; }
}

/// <summary>Grid satırı: bir satır (veri) farkının gösterimi (yön + anahtar + kolon detayı).</summary>
public sealed record VeriFarkGorunumu(string Yon, string Anahtar, string Detay)
{
    /// <summary>☑ eşitleme seçimi (v19-S12) — grid'deki onay kutusu kolonu buna bağlanır.</summary>
    public bool Secili { get; set; }
}

/// <summary>
/// Karşılaştırma sekmesi (v7-S1 · "Veri Karşılaştırma" sürümü). Sol taraf AKTİF profildir; sağ
/// taraf kullanıcının ELLE girdiği bir bağlantıdır (bağlantı ekranındaki gibi: sunucu · kimlik türü ·
/// SQL ise kullanıcı/parola). <b>Sağ motor SOLA SABİTTİR</b> — yalnız aynı motor karşılaştırılır
/// (kullanıcı kararı 2026-07-21). Kayıtlı profiller burada listelenmez.
///
/// İki alt sekme hedeflenir: <b>Şema</b> (bu dilimde çalışır — tablo/kolon/PK/FK farkı) ve
/// <b>Veri</b> (S2'de gelecek). MongoDB'de bu sekme HİÇ açılmaz. Kalıcı bağlantı tutmaz; her
/// işlemde şemayı taze okur (ISchemaService stateless).
/// </summary>
public sealed partial class KarsilastirmaSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly ISchemaService _schemaService;
    private readonly ISecretProtector _protector;
    private readonly ISqlExecutor _executor;
    private readonly ILehceSaglayici _lehceler;
    private readonly Func<ConnectionProfile?> _aktifProfil;

    /// <summary>Sol (kaynak) DB'nin şema nesneleri — Veri sekmesinde tablo listesi + PK için tutulur.</summary>
    private IReadOnlyList<SemaNesnesi> _solSema = [];

    public KarsilastirmaSekmesiViewModel(
        ISchemaService schemaService, ISecretProtector protector, ISqlExecutor executor,
        ILehceSaglayici lehceler, Func<ConnectionProfile?> aktifProfil)
    {
        _schemaService = schemaService;
        _protector = protector;
        _executor = executor;
        _lehceler = lehceler;
        _aktifProfil = aktifProfil;
    }

    public string Baslik => "🔀 Karşılaştır";
    public SekmeDurumu Durum => Calisiyor ? SekmeDurumu.Calisiyor : SekmeDurumu.Tamamlandi;

    public ObservableCollection<string> SolVeritabanlari { get; } = [];
    public ObservableCollection<string> SagVeritabanlari { get; } = [];
    public IReadOnlyList<KimlikTuru> KimlikTurleri { get; } = [KimlikTuru.Windows, KimlikTuru.Sql];

    /// <summary>Şema fark satırları (Şema alt sekmesi gridini besler).</summary>
    public ObservableCollection<SemaFarkGorunumu> Farklar { get; } = [];

    /// <summary>Veri sekmesi: kıyaslanabilir tablolar (sol şemadan) + satır fark satırları.</summary>
    public ObservableCollection<string> VeriTablolari { get; } = [];
    public ObservableCollection<VeriFarkGorunumu> VeriFarklari { get; } = [];

    public string SolBaslik => _aktifProfil() is { } p ? $"◀ {p.Ad} ({p.Sunucu})" : "◀ (bağlı profil yok)";

    /// <summary>Sağ taraf motoru SOLA sabit — kullanıcıya "aynı motor: X" olarak gösterilir.</summary>
    public string SagMotorMetni => $"▶ aynı motor: {MotorAdi(_aktifProfil()?.Motor ?? MotorTuru.Mssql)}";

    [ObservableProperty] private string? _solDb;

    // Sağ bağlantı girişleri (bağlantı ekranındaki gibi).
    [ObservableProperty] private string _sagSunucu = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SagSqlKimlikMi))] private KimlikTuru _sagKimlik = KimlikTuru.Windows;
    [ObservableProperty] private string _sagKullanici = "";
    [ObservableProperty] private string _sagParola = "";
    [ObservableProperty] private string? _sagDb;

    public bool SagSqlKimlikMi => SagKimlik == KimlikTuru.Sql;

    [ObservableProperty] private string _bilgi =
        "Kaynak = aktif profil. Hedef sunucuyu/kimliği girip \"DB'leri getir\", sonra \"Karşılaştır\".";

    // Veri sekmesi (S2): kaynak DB'nin tablosu + WHERE + PK anahtarıyla satır bazlı kıyas.
    [ObservableProperty] private string? _veriTablo;
    [ObservableProperty] private string _veriWhere = "";
    [ObservableProperty] private string _veriAnahtar = "(tablo seçin)";
    [ObservableProperty] private string _veriBilgi =
        "Kaynak DB'nin tablosunu seçin, gerekiyorsa WHERE yazın, \"Veri karşılaştır\". (PK gerekir.)";

    /// <summary>Bir karşılaştırma/bağlanma sürüyor mu — belirgin gösterge + buton kilidi için (kullanıcı isteği 2026-07-21).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Durum))]
    [NotifyPropertyChangedFor(nameof(Bosta))]
    private bool _calisiyor;

    /// <summary>Çalışmıyor mu (butonların IsEnabled'ı buna bağlanır — çalışırken kilitli).</summary>
    public bool Bosta => !Calisiyor;

    /// <summary>Sekme açılınca sol (aktif profil) veritabanları yüklenir.</summary>
    [RelayCommand]
    public async Task YukleAsync()
    {
        if (_aktifProfil() is not { } aktif)
            return;
        await VeritabanlariDoldurAsync(aktif, SolVeritabanlari);
        SolDb = SolVeritabanlari.FirstOrDefault();
    }

    /// <summary>Girilen sağ bağlantıya gidip veritabanlarını listeler (kullanıcı sonra birini seçer).</summary>
    [RelayCommand]
    public async Task SagBaglanAsync()
    {
        SagVeritabanlari.Clear();
        SagDb = null;
        if (SagProfilKur() is not { } sag)
        {
            Bilgi = "⚠ Sağ sunucu adı gerekli (SQL kimliğinde kullanıcı adı da).";
            return;
        }
        Calisiyor = true;
        Bilgi = $"{sag.Sunucu} bağlanılıyor…";
        await VeritabanlariDoldurAsync(sag, SagVeritabanlari);
        SagDb = SagVeritabanlari.FirstOrDefault();
        if (SagVeritabanlari.Count > 0)
            Bilgi = $"{SagVeritabanlari.Count} veritabanı geldi. Sağ DB'yi seçip \"Karşılaştır\".";
        Calisiyor = false;
    }

    /// <summary>Girişlerden sağ bağlantı profili kurar; motor SOLA sabit. Eksik girişte null.</summary>
    private ConnectionProfile? SagProfilKur()
    {
        if (_aktifProfil() is not { } sol || string.IsNullOrWhiteSpace(SagSunucu))
            return null;
        if (SagSqlKimlikMi && string.IsNullOrWhiteSpace(SagKullanici))
            return null;

        return new ConnectionProfile
        {
            Ad = $"(sağ) {SagSunucu.Trim()}",
            Motor = sol.Motor,                    // AYNI MOTOR — kullanıcı seçemez, kısıt otomatik sağlanır
            Sunucu = SagSunucu.Trim(),
            Kimlik = SagKimlik,
            KullaniciAdi = SagSqlKimlikMi ? SagKullanici.Trim() : null,
            ParolaSifreli = SagSqlKimlikMi && SagParola.Length > 0 ? _protector.Sifrele(SagParola) : null,
        };
    }

    private async Task VeritabanlariDoldurAsync(ConnectionProfile profil, ObservableCollection<string> hedef)
    {
        hedef.Clear();
        try
        {
            foreach (VeritabaniBilgisi vb in await _schemaService.VeritabanlariAsync(profil, CancellationToken.None))
                hedef.Add(vb.Ad);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Bilgi = $"⚠ {profil.Sunucu} veritabanları okunamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task KarsilastirAsync()
    {
        Farklar.Clear();
        _semaEsitleme = null; // bayat bağlamdan şema eşitlemesi üretilmesin

        if (_aktifProfil() is not { } sol)
        {
            Bilgi = "⚠ Sol (aktif) profil yok.";
            return;
        }
        if (SagProfilKur() is not { } sag)
        {
            Bilgi = "⚠ Sağ bağlantıyı girin (sunucu · kimlik).";
            return;
        }
        if (SolDb is not { } solDb || SagDb is not { } sagDb)
        {
            Bilgi = "⚠ İki taraf için de veritabanı seçin (sağ için önce \"DB'leri getir\").";
            return;
        }
        if (string.Equals(sol.Sunucu, sag.Sunucu, StringComparison.OrdinalIgnoreCase)
            && string.Equals(solDb, sagDb, StringComparison.OrdinalIgnoreCase))
        {
            Bilgi = "⚠ Sol ve sağ aynı sunucu+veritabanı — kıyaslanacak fark yok.";
            return;
        }

        Calisiyor = true;
        Bilgi = $"Karşılaştırılıyor: [{solDb}] ↔ {sag.Sunucu}[{sagDb}]…";
        try
        {
            SemaOnbellegi solSema = await _schemaService.YukleAsync(sol, solDb, CancellationToken.None);
            SemaOnbellegi sagSema = await _schemaService.YukleAsync(sag, sagDb, CancellationToken.None);
            IReadOnlyList<YabanciAnahtar> solFk = await _schemaService.YabanciAnahtarlarAsync(sol, solDb, CancellationToken.None);
            IReadOnlyList<YabanciAnahtar> sagFk = await _schemaService.YabanciAnahtarlarAsync(sag, sagDb, CancellationToken.None);
            IReadOnlyList<Indeks> solIx = await _schemaService.IndekslerAsync(sol, solDb, CancellationToken.None);
            IReadOnlyList<Indeks> sagIx = await _schemaService.IndekslerAsync(sag, sagDb, CancellationToken.None);

            IReadOnlyList<SemaFarkSatiri> farklar = SemaKarsilastirici.Karsilastir(
                solSema.Nesneler, solFk, sagSema.Nesneler, sagFk, solIx, sagIx);

            // v19-S4: GÖVDE kıyası — iki tarafta da var olan view/SP/fonksiyonların tanımları
            // çekilip normalize kıyaslanır; farklıysa "Degisti · gövde farklı" satırı eklenir.
            // Üst sınır 200 nesne (büyük DB'de kıyası kilitlememek için — aşım Bilgi'de söylenir).
            (farklar, string govdeNotu) = await GovdeFarklariEkleAsync(
                sol, solDb, sag, sagDb, solSema.Nesneler, sagSema.Nesneler, farklar);

            foreach (SemaFarkSatiri f in farklar)
                Farklar.Add(new SemaFarkGorunumu(YonMetni(f.Tur), f.Nesne, f.Kapsam, f.Detay));

            // 🔀 Şema eşitleme bağlamı (2026-08-03): fark satırları GÖRÜNÜMLE AYNI SIRADA saklanır —
            // "Seçilenleri/Tümünü eşitle" indeks eşleşmesiyle yapısal kayda döner.
            _semaEsitleme = new SemaEsitlemeBaglami(
                _lehceler.Getir(sol.Motor), sol, solDb, sag, sagDb, farklar,
                solSema.Nesneler, sagSema.Nesneler, solFk, sagFk, solIx, sagIx);

            Bilgi = (farklar.Count == 0
                ? $"Fark yok — [{solDb}] ile {sag.Sunucu}[{sagDb}] şeması eş."
                : $"{farklar.Count} fark: yalnız kaynakta {Say(farklar, SemaDegisim.YalnizSol)} · "
                  + $"yalnız hedefte {Say(farklar, SemaDegisim.YalnizSag)} · değişti {Say(farklar, SemaDegisim.Degisti)}.")
                + govdeNotu;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Bilgi = $"⚠ Karşılaştırılamadı: {ex.Message}";
        }
        finally
        {
            Calisiyor = false;
        }
    }

    // ── Veri karşılaştırma (S2) ──────────────────────────────────────────────

    /// <summary>Kaynak DB değişince o DB'nin şeması okunur; Veri sekmesi tablo listesi dolar.</summary>
    partial void OnSolDbChanged(string? value) => _ = SolSemaYukleAsync(value);

    private async Task SolSemaYukleAsync(string? db)
    {
        VeriTablolari.Clear();
        VeriTablo = null;
        _solSema = [];
        if (_aktifProfil() is not { } sol || db is null)
            return;
        try
        {
            SemaOnbellegi onbellek = await _schemaService.YukleAsync(sol, db, CancellationToken.None);
            _solSema = onbellek.Nesneler;
            foreach (SemaNesnesi t in _solSema
                .Where(n => n.Tur == SemaNesneTuru.Tablo)
                .OrderBy(n => n.TamAd, StringComparer.OrdinalIgnoreCase))
                VeriTablolari.Add(t.TamAd);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            VeriBilgi = $"⚠ {db} tabloları okunamadı: {ex.Message}";
        }
    }

    /// <summary>Tablo seçilince anahtar (PK) kolonları gösterilir; PK yoksa kıyaslanamayacağı söylenir.</summary>
    partial void OnVeriTabloChanged(string? value)
    {
        VeriFarklari.Clear();
        if (SeciliTabloNesnesi(value) is not { } t)
        {
            VeriAnahtar = "(tablo seçin)";
            return;
        }
        IReadOnlyList<string> pk = AnahtarKolonlar(t);
        VeriAnahtar = pk.Count > 0
            ? $"Anahtar (PK): {string.Join(", ", pk)}"
            : "⚠ Bu tabloda PK yok — satır bazında kıyaslanamaz.";
    }

    [RelayCommand]
    public async Task VeriKarsilastirAsync()
    {
        VeriFarklari.Clear();
        _esitleme = null; // bayat bağlamdan eşitleme üretilmesin (yeni kıyas dolduracak)

        if (_aktifProfil() is not { } sol)
        {
            VeriBilgi = "⚠ Sol (kaynak) profil yok.";
            return;
        }
        if (SagProfilKur() is not { } sag)
        {
            VeriBilgi = "⚠ Hedef bağlantıyı girin (sunucu · kimlik).";
            return;
        }
        if (SolDb is not { } solDb || SagDb is not { } sagDb)
        {
            VeriBilgi = "⚠ İki taraf için de veritabanı seçin (hedef için \"DB'leri getir\").";
            return;
        }
        if (SeciliTabloNesnesi(VeriTablo) is not { } tablo)
        {
            VeriBilgi = "⚠ Kıyaslanacak bir tablo seçin.";
            return;
        }
        IReadOnlyList<string> anahtar = AnahtarKolonlar(tablo);
        if (anahtar.Count == 0)
        {
            VeriBilgi = "⚠ Seçili tabloda PK yok — satır bazında kıyaslanamaz.";
            return;
        }

        ILehce lehce = _lehceler.Getir(sol.Motor);
        string? where = string.IsNullOrWhiteSpace(VeriWhere) ? null : VeriWhere.Trim();
        IReadOnlyList<string> tumKolonlar = [.. tablo.Kolonlar.Select(k => k.Ad)];
        if (lehce.SatirHashSorgusu(tablo.Sema, tablo.Ad, anahtar, tumKolonlar, where) is not { } sql)
        {
            VeriBilgi = $"⚠ {MotorAdi(sol.Motor)} motorunda veri karşılaştırma henüz yok.";
            return;
        }

        Calisiyor = true;
        VeriBilgi = $"Veri karşılaştırılıyor: {tablo.TamAd} [{solDb}] ↔ {sag.Sunucu}[{sagDb}]…";
        try
        {
            Dictionary<string, HashSatir> solH = await HashSozluguKurAsync(sol, solDb, sql, anahtar.Count);
            Dictionary<string, HashSatir> sagH = await HashSozluguKurAsync(sag, sagDb, sql, anahtar.Count);

            IReadOnlyList<VeriFarkKaydi> farklar = VeriFarkHesabi.Hesapla(
                solH.ToDictionary(p => p.Key, p => p.Value.Hash),
                sagH.ToDictionary(p => p.Key, p => p.Value.Hash));

            // Detay (borç 3): FARKLI satırlarda hangi kolon değişti — ilk 200 anahtarın tam satırı
            // iki taraftan KEYli çekilip kolon kolon kıyaslanır (sunucu-tarafı hash'te kolon detayı yok).
            _farkliKolonMetni.Clear();
            _detayHatasi = false;
            var farkliAnahtar = farklar.Where(f => f.Tur == VeriFarkTuru.Farkli).Select(f => f.Anahtar).Take(200).ToHashSet();
            _detayDenenenler = farkliAnahtar; // detayı DENENEN anahtarlar (ilk 200) — mesaj için ayırt et
            await FarkliKolonlariGetirAsync(lehce, tablo, anahtar, sol, solDb, sag, sagDb, farkliAnahtar, solH, sagH);

            // Yalnız TEK taraftaki satırların DEĞER DETAYI (kullanıcı isteği 2026-07-31: "(satır yalnız
            // kaynakta) yazan yerde satıra ait detay verelim") — ilgili taraftan tam satır çekilir.
            await TekTarafDetayGetirAsync(lehce, tablo, anahtar, sol, solDb, solH,
                farklar.Where(f => f.Tur == VeriFarkTuru.YalnizSol).Select(f => f.Anahtar));
            await TekTarafDetayGetirAsync(lehce, tablo, anahtar, sag, sagDb, sagH,
                farklar.Where(f => f.Tur == VeriFarkTuru.YalnizSag).Select(f => f.Anahtar));

            foreach (VeriFarkKaydi f in farklar)
                VeriFarklari.Add(new VeriFarkGorunumu(VeriYonMetni(f.Tur), f.Anahtar, DetayMetni(f)));

            // 🔀 Eşitleme bağlamı (madde 4): fark listesi + ham anahtarlar + iki taraf saklanır ki
            // "Seçilenleri/Tümünü eşitle" script'i sonradan sunucuya gitmeden kurulabilsin
            // (INSERT/UPDATE için kaynak tam satırı yalnız eşitleme anında çekilir).
            _esitleme = new EsitlemeBaglami(sol, sag, solDb, sagDb, tablo, anahtar, lehce, farklar, solH, sagH);

            VeriBilgi = farklar.Count == 0
                ? $"Fark yok — {solH.Count} satır eş."
                : $"{farklar.Count} fark: yalnız kaynakta {VeriFarkHesabi.Say(farklar, VeriFarkTuru.YalnizSol)} · "
                  + $"yalnız hedefte {VeriFarkHesabi.Say(farklar, VeriFarkTuru.YalnizSag)} · "
                  + $"farklı {VeriFarkHesabi.Say(farklar, VeriFarkTuru.Farkli)}. (kaynak {solH.Count} · hedef {sagH.Count} satır)";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            VeriBilgi = $"⚠ Karşılaştırılamadı: {ex.Message}";
        }
        finally
        {
            Calisiyor = false;
        }
    }

    /// <summary>Bir satırın parmak izi + ham anahtar değerleri (detay fetch'i için).</summary>
    private sealed record HashSatir(string Hash, object?[] Anahtar);

    // ── 🔀 Veri Eşitleme (madde 4, kullanıcı onayı 2026-08-03) ───────────────
    // Yöntem kullanıcı tarifi: fark satırlarından BİRDEN FAZLASINI seçip "Seçilenleri eşitle"
    // ya da "Tümünü eşitle". Yön hep KAYNAK → HEDEF; script üretilir, ÇALIŞTIRILMAZ (hedef ayrı
    // bağlantı olabilir — pencere gösterir, kullanıcı hedefte Güvenli Yazma ile koşar).

    /// <summary>
    /// v19-S4: ortak gövdeli nesnelerin (view/SP/fonksiyon) tanımlarını iki taraftan çekip
    /// normalize kıyaslar; farklıysa listeye "Degisti · gövde farklı" ekler. Tanım okunamayan
    /// nesne SESSİZCE eş sayılmaz — kıyas dışı kaldığı Bilgi'ye yansır.
    /// </summary>
    private async Task<(IReadOnlyList<SemaFarkSatiri> Farklar, string Not)> GovdeFarklariEkleAsync(
        ConnectionProfile sol, string solDb, ConnectionProfile sag, string sagDb,
        IReadOnlyList<SemaNesnesi> solNesneler, IReadOnlyList<SemaNesnesi> sagNesneler,
        IReadOnlyList<SemaFarkSatiri> farklar)
    {
        static bool Govdeli(SemaNesnesi n) => n.Tur is SemaNesneTuru.View
            or SemaNesneTuru.StoredProcedure or SemaNesneTuru.Fonksiyon;

        var sagIdx = sagNesneler.Where(Govdeli)
            .ToDictionary(n => $"{n.Tur}|{n.TamAd}", n => n, StringComparer.OrdinalIgnoreCase);
        List<(SemaNesnesi Sol, SemaNesnesi Sag)> ortaklar = [.. solNesneler.Where(Govdeli)
            .Select(n => (Sol: n, Anahtar: $"{n.Tur}|{n.TamAd}"))
            .Where(x => sagIdx.ContainsKey(x.Anahtar))
            .Select(x => (x.Sol, sagIdx[x.Anahtar]))];

        const int Tavan = 200;
        int kiyaslanamayan = 0;
        var ek = new List<SemaFarkSatiri>();
        foreach ((SemaNesnesi solN, SemaNesnesi sagN) in ortaklar.Take(Tavan))
        {
            try
            {
                string? solTanim = await _schemaService.TanimGetirAsync(sol, solN, CancellationToken.None);
                string? sagTanim = await _schemaService.TanimGetirAsync(sag, sagN, CancellationToken.None);
                if (solTanim is null || sagTanim is null)
                { kiyaslanamayan++; continue; }
                if (!string.Equals(GovdeNormalle(solTanim), GovdeNormalle(sagTanim), StringComparison.Ordinal))
                    ek.Add(new SemaFarkSatiri(solN.TamAd, solN.Tur.ToString(), SemaDegisim.Degisti, "gövde farklı"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                kiyaslanamayan++;
            }
        }

        string not = ortaklar.Count > Tavan
            ? $" ℹ Gövde kıyası ilk {Tavan} ortak nesneyle sınırlandı ({ortaklar.Count} ortak var)."
            : kiyaslanamayan > 0
                ? $" ℹ {kiyaslanamayan} nesnenin tanımı okunamadı — gövde kıyası dışında kaldılar."
                : "";

        return (ek.Count == 0 ? farklar : [.. farklar, .. ek], not);
    }

    /// <summary>Gövde kıyas normalizasyonu: satır sonları birleşik, satır sonu boşlukları kırpık.</summary>
    private static string GovdeNormalle(string tanim)
        => string.Join('\n', tanim.Replace("\r\n", "\n").Split('\n').Select(s => s.TrimEnd())).Trim();

    /// <summary>Şema eşitleme bağlamı: fark satırları görünümle aynı sırada + iki tarafın nesneleri
    /// (+v2: FK/indeks listeleri ve gövde çekimi için sol profil/DB).</summary>
    private sealed record SemaEsitlemeBaglami(
        ILehce Lehce, ConnectionProfile Sol, string SolDb, ConnectionProfile Sag, string SagDb,
        IReadOnlyList<SemaFarkSatiri> Farklar,
        IReadOnlyList<SemaNesnesi> SolNesneler, IReadOnlyList<SemaNesnesi> SagNesneler,
        IReadOnlyList<YabanciAnahtar> SolFk, IReadOnlyList<YabanciAnahtar> SagFk,
        IReadOnlyList<Indeks> SolIx, IReadOnlyList<Indeks> SagIx);

    private SemaEsitlemeBaglami? _semaEsitleme;

    /// <summary>Şema fark listesinin TAMAMI için eşitleme script'i.</summary>
    public Task TumSemayiEsitleAsync() => SemaEsitleAsync([.. Farklar]);

    /// <summary>Seçilen şema fark satırları için eşitleme script'i (çoklu seçim — veri eşitlemeyle aynı
    /// yöntem). v2: yalnız-kaynakta view/SP/fonksiyon gövdeleri TanimGetir ile çekilip script'e girer.</summary>
    public async Task SemaEsitleAsync(IReadOnlyList<SemaFarkGorunumu> secilen)
    {
        if (_semaEsitleme is not { } b)
        {
            Bilgi = "⚠ Önce \"Karşılaştır\" çalıştırın — eşitleme fark listesinden üretilir.";
            return;
        }
        if (secilen.Count == 0)
        {
            Bilgi = "⚠ ☑ kolonundan eşitlenecek satırları işaretleyin ya da \"Tümünü eşitle\" deyin.";
            return;
        }

        // Görünüm kayıtları fark listesiyle AYNI SIRADA eklendi — indeks eşleşmesi kesin kimliktir.
        List<SemaFarkSatiri> farklar = [.. secilen
            .Select(g => Farklar.IndexOf(g))
            .Where(i => i >= 0 && i < b.Farklar.Count)
            .Select(i => b.Farklar[i])];
        if (farklar.Count == 0)
        {
            Bilgi = "⚠ Seçim fark listesiyle eşleşmedi — karşılaştırmayı yenileyin.";
            return;
        }

        Calisiyor = true;
        try
        {
            // v2: yalnız-kaynakta VE (v19-S4) gövdesi DEĞİŞEN nesnelerin tanımları KAYNAKTAN çekilir.
            var govdeler = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SemaFarkSatiri f in farklar)
            {
                if (f.Tur is not (SemaDegisim.YalnizSol or SemaDegisim.Degisti)
                    || f.Kapsam is not (nameof(SemaNesneTuru.View) or nameof(SemaNesneTuru.StoredProcedure)
                        or nameof(SemaNesneTuru.Fonksiyon)))
                    continue;
                SemaNesnesi? nesne = b.SolNesneler.FirstOrDefault(n =>
                    n.TamAd.Equals(f.Nesne, StringComparison.OrdinalIgnoreCase) && n.Tur.ToString() == f.Kapsam);
                if (nesne is null)
                    continue;
                try
                {
                    if (await _schemaService.TanimGetirAsync(b.Sol, nesne, CancellationToken.None) is { } tanim)
                        govdeler[f.Nesne] = tanim;
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
                {
                    // tanım okunamadı — üretici notla düşer (sessiz eksik script olmaz)
                }
            }

            string script = SemaEsitleyici.ScriptUret(
                b.Lehce, $"{b.Sag.Sunucu} [{b.SagDb}]", farklar, b.SolNesneler, b.SagNesneler,
                b.SolFk, b.SolIx, b.SagIx, govdeler, b.SagFk);
            ScriptGoster?.Invoke($"🔀 Şema eşitleme — {b.Sag.Sunucu}[{b.SagDb}]", script);
            Bilgi = $"Şema eşitleme script'i üretildi ({farklar.Count} fark) — HEDEFTE çalıştırın (Güvenli Yazma önerilir).";
        }
        finally
        {
            Calisiyor = false;
        }
    }

    /// <summary>Son karşılaştırmanın eşitleme için gereken tüm bağlamı (yeni kıyasta tazelenir).</summary>
    private sealed record EsitlemeBaglami(
        ConnectionProfile Sol, ConnectionProfile Sag, string SolDb, string SagDb,
        SemaNesnesi Tablo, IReadOnlyList<string> AnahtarKolonlar, ILehce Lehce,
        IReadOnlyList<VeriFarkKaydi> Farklar,
        Dictionary<string, HashSatir> SolH, Dictionary<string, HashSatir> SagH);

    private EsitlemeBaglami? _esitleme;

    /// <summary>Pencere köprüsü: üretilen eşitleme script'ini gösterir (başlık, script).</summary>
    public Action<string, string>? ScriptGoster { get; set; }

    /// <summary>Fark listesinin TAMAMI için eşitleme script'i (kullanıcı: "ya da tamamını eşitle").</summary>
    public Task TumunuEsitleAsync() => EsitleAsync([.. VeriFarklari]);

    /// <summary>Seçilen fark satırları için eşitleme script'i üretir (çoklu seçim destekli).</summary>
    public async Task EsitleAsync(IReadOnlyList<VeriFarkGorunumu> secilen)
    {
        if (_esitleme is not { } b)
        {
            VeriBilgi = "⚠ Önce \"Veri karşılaştır\" çalıştırın — eşitleme fark listesinden üretilir.";
            return;
        }
        if (secilen.Count == 0)
        {
            VeriBilgi = "⚠ ☑ kolonundan eşitlenecek satırları işaretleyin ya da \"Tümünü eşitle\" deyin.";
            return;
        }

        var secilenAnahtar = new HashSet<string>(secilen.Select(s => s.Anahtar), StringComparer.Ordinal);
        List<VeriFarkKaydi> farklar = [.. b.Farklar.Where(f => secilenAnahtar.Contains(f.Anahtar))];
        if (farklar.Count == 0)
        {
            VeriBilgi = "⚠ Seçim fark listesiyle eşleşmedi — listeyi yenileyip tekrar deneyin.";
            return;
        }

        Calisiyor = true;
        VeriBilgi = $"Eşitleme script'i üretiliyor ({farklar.Count} fark)…";
        try
        {
            // INSERT/UPDATE kaynak TAM satır ister — yalnız gerekenler tek KEYli sorguyla çekilir.
            IReadOnlyList<string> kaynakGereken = [.. farklar
                .Where(f => f.Tur is VeriFarkTuru.YalnizSol or VeriFarkTuru.Farkli)
                .Select(f => f.Anahtar)];
            IReadOnlyList<string> solKol = [];
            Dictionary<string, object?[]> solSatir = [];
            if (kaynakGereken.Count > 0)
            {
                IReadOnlyList<object?[]> anahtarlar = [.. kaynakGereken.Select(a => b.SolH[a].Anahtar)];
                (solKol, solSatir) = await TamSatirGetirAsync(
                    b.Lehce, b.Tablo, b.AnahtarKolonlar, b.Sol, b.SolDb, anahtarlar);
            }

            Dictionary<string, object?> AnahtarSozlugu(HashSatir satir)
            {
                var sozluk = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < b.AnahtarKolonlar.Count && i < satir.Anahtar.Length; i++)
                    sozluk[b.AnahtarKolonlar[i]] = satir.Anahtar[i];
                return sozluk;
            }

            var kayitlar = new List<VeriEsitleyici.Kayit>();
            foreach (VeriFarkKaydi f in farklar)
            {
                if (f.Tur == VeriFarkTuru.YalnizSag)
                {
                    kayitlar.Add(new VeriEsitleyici.Kayit(f.Tur, AnahtarSozlugu(b.SagH[f.Anahtar])));
                    continue;
                }

                solSatir.TryGetValue(f.Anahtar, out object?[]? kaynak);
                kayitlar.Add(new VeriEsitleyici.Kayit(
                    f.Tur, AnahtarSozlugu(b.SolH[f.Anahtar]),
                    kaynak is null ? null : solKol, kaynak));
            }

            string script = VeriEsitleyici.ScriptUret(
                b.Lehce, b.Tablo, $"{b.Sag.Sunucu} [{b.SagDb}]", b.AnahtarKolonlar, kayitlar);
            ScriptGoster?.Invoke($"🔀 Eşitleme — {b.Tablo.TamAd} → {b.Sag.Sunucu}[{b.SagDb}]", script);
            VeriBilgi = $"Eşitleme script'i üretildi ({farklar.Count} fark) — HEDEFTE çalıştırın (Güvenli Yazma önerilir).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            VeriBilgi = $"⚠ Eşitleme script'i üretilemedi: {ex.Message}";
        }
        finally
        {
            Calisiyor = false;
        }
    }

    /// <summary>Hash sorgusunu bir tarafta çalıştırıp (anahtar-metni → hash + ham anahtar) sözlüğü kurar.</summary>
    private async Task<Dictionary<string, HashSatir>> HashSozluguKurAsync(
        ConnectionProfile profil, string db, string sql, int anahtarSayisi)
    {
        QueryResult sonuc = await _executor.ExecuteAsync(profil, sql,
            new ExecuteOptions { SatirSiniri = 1_000_000, VeritabaniOverride = db }, CancellationToken.None);
        if (sonuc.IptalEdildi)
            throw new OperationCanceledException();
        if (!sonuc.Basarili)
            throw new InvalidOperationException(sonuc.Hata?.Mesaj ?? "sorgu başarısız");

        var sozluk = new Dictionary<string, HashSatir>(StringComparer.Ordinal);
        if (sonuc.ResultSetler.Count == 0)
            return sozluk;
        foreach (object?[] r in sonuc.ResultSetler[0].Satirlar)
        {
            object?[] anahtarDeger = r.Take(anahtarSayisi).ToArray();
            string anahtar = string.Join("␟", anahtarDeger.Select(AnahtarMetni));
            sozluk[anahtar] = new HashSatir(AnahtarMetni(r[anahtarSayisi]), anahtarDeger); // son kolon = __hash
        }
        return sozluk;
    }

    /// <summary>
    /// FARKLI anahtarların tam satırlarını iki taraftan KEYli çekip kolon kolon kıyaslar; her farklı
    /// anahtar için "kolon: kaynakDeğer → hedefDeğer" listesini üretir. Fetch başarısızsa (ör. çok
    /// karmaşık WHERE) detay boş kalır — özet fark yine gösterilir (detay bonus, bloklamaz).
    /// </summary>
    private async Task FarkliKolonlariGetirAsync(
        ILehce lehce, SemaNesnesi tablo, IReadOnlyList<string> anahtarKolonlar,
        ConnectionProfile sol, string solDb, ConnectionProfile sag, string sagDb,
        HashSet<string> farkliAnahtar, Dictionary<string, HashSatir> solH, Dictionary<string, HashSatir> sagH)
    {
        if (farkliAnahtar.Count == 0)
            return;

        IReadOnlyList<object?[]> anahtarlar = [.. farkliAnahtar.Select(a => solH[a].Anahtar)];
        try
        {
            (IReadOnlyList<string> solKol, Dictionary<string, object?[]> solSatir) =
                await TamSatirGetirAsync(lehce, tablo, anahtarKolonlar, sol, solDb, anahtarlar);
            (IReadOnlyList<string> sagKol, Dictionary<string, object?[]> sagSatir) =
                await TamSatirGetirAsync(lehce, tablo, anahtarKolonlar, sag, sagDb, anahtarlar);

            foreach (string a in farkliAnahtar)
            {
                if (!solSatir.TryGetValue(a, out object?[]? sr) || !sagSatir.TryGetValue(a, out object?[]? gr))
                    continue;
                var farkliKolonlar = new List<string>();
                int n = Math.Min(solKol.Count, Math.Min(sr.Length, gr.Length));
                for (int i = 0; i < n; i++)
                {
                    if (anahtarKolonlar.Contains(solKol[i], StringComparer.OrdinalIgnoreCase))
                        continue; // anahtar kolonları eşit
                    if (!string.Equals(AnahtarMetni(sr[i]), AnahtarMetni(gr[i]), StringComparison.Ordinal))
                        farkliKolonlar.Add($"{solKol[i]}: {AnahtarMetni(sr[i])} → {AnahtarMetni(gr[i])}");
                }
                _farkliKolonMetni[a] = string.Join(" · ", farkliKolonlar);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            _detayHatasi = true; // detay çekilemedi (bonus) — özet fark bozulmaz; mesaj bunu söyler
        }
    }

    private readonly Dictionary<string, string> _farkliKolonMetni = new(StringComparer.Ordinal);

    /// <summary>
    /// Yalnız TEK tarafta olan satırların değer dökümü (kullanıcı isteği 2026-07-31): ilgili taraftan
    /// tam satır çekilir, "Kolon: değer" listesi <see cref="_farkliKolonMetni"/>'ne yazılır (DetayMetni
    /// okur). Sınırlar: ilk 200 anahtar · ilk 10 kolon · değer başına 60 karakter (grid satırı taşmasın).
    /// Hata bloklamaz — detay bonustur, özet fark yine görünür.
    /// </summary>
    private async Task TekTarafDetayGetirAsync(
        ILehce lehce, SemaNesnesi tablo, IReadOnlyList<string> anahtarKolonlar,
        ConnectionProfile profil, string db, Dictionary<string, HashSatir> taraf, IEnumerable<string> kume)
    {
        IReadOnlyList<string> secilen = [.. kume.Take(200)];
        if (secilen.Count == 0)
            return;
        try
        {
            IReadOnlyList<object?[]> anahtarlar = [.. secilen.Select(a => taraf[a].Anahtar)];
            (IReadOnlyList<string> kol, Dictionary<string, object?[]> satirlar) =
                await TamSatirGetirAsync(lehce, tablo, anahtarKolonlar, profil, db, anahtarlar);

            static string Kisa(string s) => s.Length <= 60 ? s : s[..60] + "…";
            var istenen = new HashSet<string>(secilen, StringComparer.Ordinal);
            foreach ((string a, object?[] r) in satirlar)
            {
                if (!istenen.Contains(a))
                    continue; // sorgu fazlasını getirdiyse İSTENMEYEN anahtara yazma (≠ satırın detayını ezme)
                var parcalar = new List<string>();
                bool kirpildi = false;
                for (int i = 0; i < Math.Min(kol.Count, r.Length); i++)
                {
                    if (anahtarKolonlar.Contains(kol[i], StringComparer.OrdinalIgnoreCase))
                        continue; // anahtar zaten kendi kolonunda görünür
                    if (parcalar.Count >= 10) { kirpildi = true; break; }
                    parcalar.Add($"{kol[i]}: {Kisa(AnahtarMetni(r[i]))}");
                }
                _farkliKolonMetni[a] = string.Join(" · ", parcalar) + (kirpildi ? " · …" : "");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // bonus detay — sessiz; kısa "(satır yalnız …)" metni kalır
        }
    }

    /// <summary>Kolon detayı DENENEN (ilk 200) farklı anahtarlar — mesajı "sınır ötesi" durumundan ayırır.</summary>
    private HashSet<string> _detayDenenenler = [];

    /// <summary>Detay fetch'i (tam satır çekme) hata verdi mi — mesaj bunu ayırt eder.</summary>
    private bool _detayHatasi;

    /// <summary>Verilen anahtarların TAM satırlarını (SELECT *) tek KEYli sorguyla çeker; (kolon adları, key→satır).</summary>
    private async Task<(IReadOnlyList<string> Kolonlar, Dictionary<string, object?[]> Satirlar)> TamSatirGetirAsync(
        ILehce lehce, SemaNesnesi tablo, IReadOnlyList<string> anahtarKolonlar,
        ConnectionProfile profil, string db, IReadOnlyList<object?[]> anahtarlar)
    {
        string tam = string.IsNullOrEmpty(tablo.Sema)
            ? lehce.TirnaklaTanimlayici(tablo.Ad) : lehce.TamAdYaz(tablo.Sema, tablo.Ad);
        LiteralKurallari lk = lehce.LiteralKurallari;
        IEnumerable<string> kosullar = anahtarlar.Select(vals =>
            "(" + string.Join(" AND ", anahtarKolonlar.Select((k, i) =>
                $"{lehce.TirnaklaTanimlayici(k)} = {LiteralYazici.Yaz(vals[i], lk)}")) + ")");
        string sql = $"SELECT * FROM {tam} WHERE {string.Join(" OR ", kosullar)}";

        QueryResult sonuc = await _executor.ExecuteAsync(profil, sql,
            new ExecuteOptions { SatirSiniri = 1_000_000, VeritabaniOverride = db }, CancellationToken.None);
        if (!sonuc.Basarili || sonuc.ResultSetler.Count == 0)
            throw new InvalidOperationException(sonuc.Hata?.Mesaj ?? "detay okunamadı");

        ResultSetData rs = sonuc.ResultSetler[0];
        IReadOnlyList<string> kolonlar = [.. rs.Kolonlar.Select(k => k.Ad)];
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < kolonlar.Count; i++) idx[kolonlar[i]] = i;
        int[] anahtarIndeks = [.. anahtarKolonlar.Select(k => idx.TryGetValue(k, out int p) ? p : -1)];

        var satirlar = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        foreach (object?[] r in rs.Satirlar)
        {
            string key = string.Join("␟", anahtarIndeks.Select(p => p >= 0 ? AnahtarMetni(r[p]) : ""));
            satirlar[key] = r;
        }
        return (kolonlar, satirlar);
    }

    /// <summary>
    /// Bir fark satırının detayı. "Farklı" satırda kolon detayı yoksa NEDENİNİ söyler (kullanıcı sorusu
    /// 2026-07-23: "bazı karşılaştırmalarda kolon kaydı yok, neden"): (a) detay ilk 200 farkla sınırlı;
    /// (b) tam satır çekilemedi; (c) hash farklı ama kolon değerleri string olarak eşit görünüyor
    /// (tip/temsil farkı — ör. datetime hassasiyeti, ondalık biçimi, boşluk/trailing).
    /// </summary>
    private string DetayMetni(VeriFarkKaydi f)
    {
        switch (f.Tur)
        {
            // Tek-taraf satırında DEĞERLER de gösterilir (kullanıcı isteği 2026-07-31) — detay
            // çekilemediyse (hata/sınır) eski kısa metin kalır.
            case VeriFarkTuru.YalnizSol:
                return _farkliKolonMetni.TryGetValue(f.Anahtar, out string? sd) && sd.Length > 0
                    ? $"satır yalnız kaynakta · {sd}" : "(satır yalnız kaynakta)";
            case VeriFarkTuru.YalnizSag:
                return _farkliKolonMetni.TryGetValue(f.Anahtar, out string? hd) && hd.Length > 0
                    ? $"satır yalnız hedefte · {hd}" : "(satır yalnız hedefte)";
        }
        if (_farkliKolonMetni.TryGetValue(f.Anahtar, out string? d) && d.Length > 0)
            return d;
        if (!_detayDenenenler.Contains(f.Anahtar))
            return "(kolon detayı ilk 200 farkta gösterilir)";
        if (_detayHatasi)
            return "(kolon detayı alınamadı)";
        return "(satır farklı ama kolon değerleri eşit görünüyor — tip/temsil farkı olabilir)";
    }

    private static string AnahtarMetni(object? v)
        => v is null ? "∅" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private SemaNesnesi? SeciliTabloNesnesi(string? tamAd)
        => tamAd is null ? null : _solSema.FirstOrDefault(n => n.Tur == SemaNesneTuru.Tablo && n.TamAd == tamAd);

    private static IReadOnlyList<string> AnahtarKolonlar(SemaNesnesi t)
        => [.. t.Kolonlar.Where(k => k.PkMi).Select(k => k.Ad)];

    // "solda/sağda" → "kaynakta/hedefte" (kullanıcı isteği 2026-07-31): ekran Kaynak DB / Hedef DB
    // diye konuşuyor — yön etiketi de aynı dili konuşsun, kafa karıştırmasın.
    private static string VeriYonMetni(VeriFarkTuru t) => t switch
    {
        VeriFarkTuru.YalnizSol => "◀ yalnız kaynakta",
        VeriFarkTuru.YalnizSag => "▶ yalnız hedefte",
        _ => "≠ farklı",
    };

    private static int Say(IReadOnlyList<SemaFarkSatiri> f, SemaDegisim t) => f.Count(s => s.Tur == t);

    private static string YonMetni(SemaDegisim t) => t switch
    {
        SemaDegisim.YalnizSol => "◀ yalnız kaynakta",
        SemaDegisim.YalnizSag => "▶ yalnız hedefte",
        _ => "≠ değişti",
    };

    private static string MotorAdi(MotorTuru m) => m switch
    {
        MotorTuru.Mssql => "SQL Server",
        MotorTuru.Postgres => "PostgreSQL",
        MotorTuru.MySql => "MySQL / MariaDB",
        MotorTuru.Oracle => "Oracle",
        _ => m.ToString(),
    };

    public Task KapatAsync()
    {
        // Bellek (inceleme 2026-07-30 bekleyeni): fark listeleri on binlerce satır tutabilir;
        // sekmeye sarkan referans kalsa da veri burada bırakılır.
        Farklar.Clear();
        VeriFarklari.Clear();
        return Task.CompletedTask;
    }
}
