using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// 🔍 Profiler SEKMESİ (v23-S1 — araştırma: docs/09-profiler-arastirma.md; kullanıcı kararları
/// K1-K4): sunucudaki TÜM uygulamaların sorgu trafiğini CANLI izler. SSMS Profiler deprecated
/// olduğundan altyapı <b>Extended Events</b>: Başlat oturumu kurup açar
/// (<see cref="ProfilerSorgulari"/> DDL'leri), akış <b>ring_buffer</b>'ın aralıklı okunmasıyla
/// beslenir (K2 — 2 sn'de bir; sunucuya yük bindirmeyen tek SELECT). Durdur/kapat oturumu
/// sunucudan SİLER — arkamızda oturum bırakmayız.
///
/// YALNIZ MSSQL (K1): düğme diğer motorlarda görünmez (yarım özellik yok kuralı).
/// Bellek sigortası: ekranda en çok <see cref="EnCokSatir"/> olay tutulur (m.8/m.9 dersleri) —
/// eskiler baştan düşer; tamamı gerekiyorsa CSV'ye kaydedilir (K3).
/// </summary>
public sealed partial class ProfilerSekmesiViewModel : ObservableObject, ISekme
{
    [ObservableProperty] private bool _sabit;

    /// <summary>Ekranda tutulan en çok olay — 4 MB ring buffer zaten ~binlerle sınırlı; grid
    /// sanallaştırması sağlıklı kalsın diye üstü baştan düşer (durum satırı bunu söyler).</summary>
    public const int EnCokSatir = 5_000;

    /// <summary>ring_buffer okuma aralığı (sn) — MAX_DISPATCH_LATENCY=3 sn ile uyumlu.</summary>
    private const int OkumaAraligiSn = 2;

    private readonly Func<string, CancellationToken, Task<QueryResult>> _calistir;
    private CancellationTokenSource? _cts;
    private DateTime? _sonZaman;      // artımlı okuma eşiği (RingBufferCoz'a gider)
    private long _dusenSatir;         // tavandan düşen olay sayısı (dürüst durum satırı)

    /// <summary>Sunucuda BİZİM kurduğumuz oturum duruyor mu (v23-S6): Başlat'ta CREATE'ten hemen
    /// önce kalkar (CREATE olur da START düşerse oturum yine sunucudadır — kapanışta silinsin),
    /// temizlik bitince iner. Kapanış/bağlantı değişimi temizliği YALNIZ bayrak kalkıkken koşar —
    /// önceki koşul (_tum.Count > 0) çoktan silinmiş oturum için de sunucuya gidiyordu.</summary>
    private bool _oturumSunucuda;

    /// <summary>Ana liste (tavanlı) — imza OLAY GELİRKEN arka planda bir kez hesaplanır (v23-S2):
    /// her tikte 5.000 satırı yeniden regex'lemek yerine önbellek (LogAnaliz.Imza ~6 regex/metin).</summary>
    private readonly List<(ProfilerOlayi Olay, string Imza)> _tum = [];

    /// <summary>İmza → grup satırı (v23-S2). Gruplar EKRAN TAVANINDAN BAĞIMSIZ kümülatiftir:
    /// ham satır tavandan düşse de sayaçlar akışın başından beri saymaya devam eder.</summary>
    private readonly Dictionary<string, ProfilerGrubu> _gruplar = new(StringComparer.Ordinal);

    /// <summary>🤖 Asistan köprüsü (v23-S4) — null ise AI düğmesi hiç görünmez (LogAnaliz deseni).</summary>
    private readonly Func<string, Task<AsistanCevabi>>? _asistanSor;

    public ProfilerSekmesiViewModel(
        IReadOnlyList<string> veritabanlari,
        Func<string, CancellationToken, Task<QueryResult>> calistir,
        Func<string, Task<AsistanCevabi>>? asistanSor = null)
    {
        _calistir = calistir;
        _asistanSor = asistanSor;
        VeritabaniSecenekleri.Add(TumuEtiketi);
        foreach (string db in veritabanlari)
            VeritabaniSecenekleri.Add(db);
        _secilenVeritabani = TumuEtiketi;
    }

    private const string TumuEtiketi = "(tümü)";

    // ── Kurulum seçimleri ─────────────────────────────────────────────────────
    /// <summary>Şablonlar — sıra <see cref="ProfilerSablonu"/> enum sırasıyla eşleşir.</summary>
    public IReadOnlyList<string> Sablonlar { get; } =
        ["Standart (Batch + RPC)", "Yavaş sorgular (eşik üstü)", "Hatalar (önem ≥ 11)",
         "Deadlock (kilitlenme)"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsikGorunur))]
    private int _secilenSablonIndex;

    public bool EsikGorunur => (ProfilerSablonu)SecilenSablonIndex == ProfilerSablonu.YavasSorgular;

    /// <summary>Yavaş sorgu eşiği (ms) — sunucu tarafında süzülür (µs çevirisi çekirdekte).</summary>
    [ObservableProperty] private int _esikMs = 1000;

    public System.Collections.ObjectModel.ObservableCollection<string> VeritabaniSecenekleri { get; } = [];

    [ObservableProperty] private string _secilenVeritabani;

    // ── Akış durumu ───────────────────────────────────────────────────────────
    public System.Collections.ObjectModel.ObservableCollection<ProfilerOlayi> Olaylar { get; } = [];

    /// <summary>🧠 Grup görünümü satırları (v23-S2) — en çok tekrarlayan üstte, canlı güncellenir.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProfilerGrubu> Gruplar { get; } = [];

    /// <summary>🧠 Grupla anahtarı (v23-S2): açıkken grid, aynı sorgunun parametre VARYANTLARINI
    /// (LogAnaliz.Imza — Log Analizi'yle aynı motor) tek satırda sayaçla gösterir. Ham akış
    /// arkada birikmeye devam eder — kapatınca kaldığın yerdesin.</summary>
    [ObservableProperty] private bool _gruplaAcik;

    partial void OnGruplaAcikChanged(bool value)
    {
        TamMetinGuncelle();
        if (value)
            GruplariSirala(); // açılışta güncel sıralı görünüm
    }

    [ObservableProperty] private ProfilerOlayi? _seciliOlay;

    partial void OnSeciliOlayChanged(ProfilerOlayi? value) => TamMetinGuncelle();

    [ObservableProperty] private ProfilerGrubu? _seciliGrup;

    partial void OnSeciliGrupChanged(ProfilerGrubu? value) => TamMetinGuncelle();

    /// <summary>Alt paneldeki tam metin — aktif görünümün seçimini izler (olay ↔ grup).</summary>
    [ObservableProperty] private string? _tamMetin;

    /// <summary>🕸 Seçili satır bir kilitlenme raporu mu (v23-S3) — "Şemayı aç" düğmesinin kapısı.</summary>
    public bool DeadlockSecili
        => (GruplaAcik ? SeciliGrup?.Olay : SeciliOlay?.Olay) == "Deadlock";

    /// <summary>⧉ Seçili satır sorgu sekmesinde açılabilir mi (v23-S5 köprüsü): Batch/RPC (ve grup
    /// örneği) açılır; Deadlock XML'i ve Hata mesajı editörde anlamsız — düğme gizlenir.</summary>
    public bool SekmedeAcilabilir
        => TamMetin is { Length: > 0 }
            && (GruplaAcik ? SeciliGrup?.Olay : SeciliOlay?.Olay) is "Batch" or "RPC";

    /// <summary>Sekmede açarken hedef veritabanı — olayın yakalandığı DB (grupta örnek olayın DB'si
    /// akışta yok; null → aktif profil varsayılanı).</summary>
    public string? SekmeVeritabani => GruplaAcik ? null : SeciliOlay?.Veritabani;

    /// <summary>Şema penceresi başlığı için seçili satırın zamanı.</summary>
    public DateTime SeciliZaman
        => (GruplaAcik ? SeciliGrup?.SonZaman : SeciliOlay?.Zaman) ?? DateTime.Now;

    private void TamMetinGuncelle()
    {
        TamMetin = GruplaAcik ? SeciliGrup?.OrnekMetin : SeciliOlay?.Metin;
        OnPropertyChanged(nameof(DeadlockSecili));
        OnPropertyChanged(nameof(SekmedeAcilabilir));
    }

    [ObservableProperty] private string _sonucSuz = "";

    partial void OnSonucSuzChanged(string value) => SuzUygula();

    [ObservableProperty] private string _ozet =
        "Şablonu seçip “▶ İzlemeyi başlat”a basın — sunucudaki TÜM uygulamaların trafiği akar. "
        + "(Sunucuda ALTER ANY EVENT SESSION yetkisi gerekir.)";

    /// <summary>⏸ ToggleButton'a doğrudan bağlanır: işaretliyken okuma durur, sunucu oturumu
    /// AKMAYA DEVAM eder — sürdürünce ring buffer'da birikenler tek seferde gelir.</summary>
    [ObservableProperty] private bool _duraklatildi;

    partial void OnDuraklatildiChanged(bool value)
    {
        if (Calisiyor)
            Ozet = value
                ? "⏸ Duraklatıldı — oturum sunucuda akmaya devam ediyor; sürdürünce birikenler gelir."
                : "İzleniyor…";
    }

    private bool _calisiyor;
    public bool Calisiyor
    {
        get => _calisiyor;
        private set
        {
            if (SetProperty(ref _calisiyor, value))
            {
                OnPropertyChanged(nameof(Bosta));
                OnPropertyChanged(nameof(Durum));
                BaslatCommand.NotifyCanExecuteChanged();
                DurdurCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool Bosta => !Calisiyor;

    // ── Komutlar ──────────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(Bosta))]
    private async Task BaslatAsync()
    {
        var sablon = (ProfilerSablonu)SecilenSablonIndex;
        string? db = SecilenVeritabani == TumuEtiketi ? null : SecilenVeritabani;

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        Calisiyor = true;
        Duraklatildi = false;
        Ozet = "Oturum kuruluyor…";
        try
        {
            // Eski oturum kalmış olabilir (çökme/ikinci kopya) → önce temizle, sonra kur + başlat.
            foreach (string sql in (string[])
                     [ProfilerSorgulari.OturumSil(),
                      ProfilerSorgulari.OturumOlustur(sablon, EsikMs, db),
                      ProfilerSorgulari.OturumBaslat()])
            {
                if (sql.Contains("CREATE EVENT SESSION"))
                    _oturumSunucuda = true; // CREATE denendiği andan itibaren sunucuda iz olabilir
                QueryResult r = await _calistir(sql, ct);
                if (r.Hata is { } hata)
                {
                    Ozet = $"⚠ Oturum kurulamadı: {hata.Mesaj}\n"
                        + "İzleme, sunucuda ALTER ANY EVENT SESSION yetkisi ister — "
                        + "yetkiniz yoksa DBA'den isteyin (sysadmin'de vardır).";
                    Calisiyor = false;
                    if (_oturumSunucuda)
                        _ = OturumuTemizleAsync(); // CREATE olup START düşmüş olabilir — iz bırakma (best-effort)
                    return;
                }
            }

            _sonZaman = null;
            _dusenSatir = 0;
            _tum.Clear();
            Olaylar.Clear();
            _gruplar.Clear();
            Gruplar.Clear();
            Ozet = "İzleniyor… (olaylar 2-3 sn içinde akmaya başlar)";
            await DonguAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Durdur — sessiz; temizlik Durdur/KapatAsync'te.
        }
        finally
        {
            Calisiyor = false;
        }
    }

    /// <summary>Okuma döngüsü: her tikte ring_buffer'ı okur, YENİ olayları akışa ekler.
    /// PeriodicTimer'a jeton VERİLMEZ (iptalde fırlatır — bilinen tuzak); iptal Dispose ile.</summary>
    private async Task DonguAsync(CancellationToken ct)
    {
        using var tik = new PeriodicTimer(TimeSpan.FromSeconds(OkumaAraligiSn));
        await using CancellationTokenRegistration kayit = ct.Register(tik.Dispose);
        while (await tik.WaitForNextTickAsync())
        {
            if (ct.IsCancellationRequested)
                return;
            if (Duraklatildi)
                continue; // oturum sunucuda akmaya devam eder; sürdürünce birikenler gelir

            QueryResult r = await _calistir(ProfilerSorgulari.RingBufferOku(), ct);
            if (r.Hata is { } hata)
            {
                Ozet = $"⚠ Okuma hatası: {hata.Mesaj} (izleme sürüyor — sonraki denemede düzelir)";
                continue;
            }
            string? xml = r.ResultSetler.Count > 0 && r.ResultSetler[0].Satirlar.Count > 0
                ? r.ResultSetler[0].Satirlar[0][0]?.ToString()
                : null;

            // Çözüm + İMZA hesabı arka planda: 4 MB XML ve regex'ler UI thread'ini takmasın
            // (v20-S14 dersi; imza olay başına BİR kez hesaplanır — v23-S2 önbelleği).
            // v23-S6 (canlı tanı 28 Eyl): kendi gürültümüz (polling SELECT'i, oturum DDL'leri,
            // havuzun sp_reset_connection'ı) BURADA elenir — dış trafiksiz 10 sn'de 8 "olay"
            // üretip akışı kendi kendine besliyordu. Eşik yine HAM listenin sonundan alınır ki
            // elenen olaylar sonraki tikte yeniden çözülmesin.
            DateTime? esik = _sonZaman;
            (IReadOnlyList<(ProfilerOlayi Olay, string Imza)> Yeni, DateTime? Son) cozum =
                await Task.Run(() =>
                {
                    IReadOnlyList<ProfilerOlayi> ham = ProfilerSorgulari.RingBufferCoz(xml, esik);
                    return ((IReadOnlyList<(ProfilerOlayi, string)>)
                        [.. ham.Where(o => !KendiGurultusuMu(o))
                            .Select(o => (o, LogAnaliz.Imza(o.Metin ?? "")))],
                        ham.Count > 0 ? ham[^1].Zaman : (DateTime?)null);
                }, ct);
            if (cozum.Son is { } son)
                _sonZaman = son;
            if (cozum.Yeni.Count == 0)
                continue;

            // v23-S6 (canlı tanı 28 Eyl — "donuyor" kök nedeni): eski SuzUygula() her tikte
            // 5.000 satırı Clear + yeniden Add ediyordu → 2 sn'de bir 200-500 ms UI takılması,
            // üstelik seçim, kaydırma konumu ve tam-metin paneli SIFIRLANIYORDU. Artık yalnız
            // YENİ olaylar süzgeçten geçirilip SONA eklenir; tavan üstü baştan tek tek düşer.
            string suz = SonucSuz.Trim();
            foreach ((ProfilerOlayi Olay, string Imza) c in cozum.Yeni)
            {
                _tum.Add(c);
                GrupGuncelle(c.Olay, c.Imza);
                if (SuzgectenGecer(c.Olay, suz))
                    Olaylar.Add(c.Olay);
            }
            while (_tum.Count > EnCokSatir)
            {
                _tum.RemoveAt(0);
                _dusenSatir++;
            }
            while (Olaylar.Count > EnCokSatir)
                Olaylar.RemoveAt(0);
            if (GruplaAcik)
                GruplariSirala();
            Ozet = $"İzleniyor — {_tum.Count:N0} olay · {_gruplar.Count:N0} grup"
                + (_dusenSatir > 0 ? $" (en eski {_dusenSatir:N0} düştü — tavan {EnCokSatir:N0}; gruplar saymaya devam eder)" : "")
                + $" · son: {_sonZaman:HH:mm:ss}";
        }
    }

    [RelayCommand(CanExecute = nameof(Calisiyor))]
    private async Task DurdurAsync()
    {
        _cts?.Cancel();
        Calisiyor = false;
        await OturumuTemizleAsync();
        Ozet = $"■ Durduruldu — {_tum.Count:N0} olay ekranda (oturum sunucudan silindi). "
            + "Kaydetmek için ⧉ Kopyala / ⬇ CSV.";
    }

    /// <summary>STOP + DROP (best-effort, iptalsiz): sunucuda oturum bırakmamak temizlik borcudur;
    /// hata yutulur — kapanışta kullanıcıya gösterecek ekran kalmamış olabilir.</summary>
    private async Task OturumuTemizleAsync()
    {
        try
        {
            await _calistir(ProfilerSorgulari.OturumDurdur(), CancellationToken.None);
            await _calistir(ProfilerSorgulari.OturumSil(), CancellationToken.None);
            _oturumSunucuda = false;
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
        {
            // bağlantı kapanmış olabilir — sunucu tarafında oturum admin tarafından görülebilir/silinebilir (adı sabit)
        }
    }

    [RelayCommand]
    private void Temizle()
    {
        _tum.Clear();
        Olaylar.Clear();
        _gruplar.Clear();
        Gruplar.Clear();
        _dusenSatir = 0;
        if (!Calisiyor)
            Ozet = "Temizlendi.";
    }

    /// <summary>Arama kutusu değişince TAM yeniden kurulum (kullanıcı eylemi — kabul edilir maliyet).
    /// Canlı tik ise SuzgectenGecer ile yalnız YENİ olayları ekler (v23-S6 — donma düzeltmesi).</summary>
    private void SuzUygula()
    {
        string q = SonucSuz.Trim();
        Olaylar.Clear();
        foreach ((ProfilerOlayi o, _) in _tum)
        {
            if (SuzgectenGecer(o, q))
                Olaylar.Add(o);
        }
    }

    private static bool SuzgectenGecer(ProfilerOlayi o, string q)
        => q.Length == 0
            || (o.Metin?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (o.Uygulama?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (o.Veritabani?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (o.Kullanici?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Kendi izleme mekanizmamızın ürettiği trafik (v23-S6 — canlı tanı: dış trafiksiz
    /// 10 sn'de 8 olay): oturum adını taşıyan DDL/polling sorguları + SQLST havuzunun bağlantı
    /// sıfırlaması akışa YAZILMAZ. Kullanıcının kendi sorgu sekmesi trafiği görünmeye devam eder
    /// (yalnız metni oturum adını içeren sorgular elenir — bilinçli dar süzgeç).</summary>
    private static bool KendiGurultusuMu(ProfilerOlayi o)
    {
        string m = o.Metin ?? "";
        if (m.Contains(ProfilerSorgulari.OturumAdi, StringComparison.Ordinal))
            return true;
        return o.Uygulama == "SQLST"
            && m.TrimEnd().EndsWith("sp_reset_connection", StringComparison.OrdinalIgnoreCase);
    }

    // ── 🧠 İmza gruplama (v23-S2) ─────────────────────────────────────────────
    /// <summary>Bir olayı grubuna işler: varsa sayaçlar YERİNDE güncellenir (grid canlı yenilenir,
    /// seçim kaybolmaz), yoksa yeni grup açılır. Hata olayları da mesaj imzasıyla gruplanır.</summary>
    private void GrupGuncelle(ProfilerOlayi olay, string imza)
    {
        if (_gruplar.TryGetValue(imza, out ProfilerGrubu? grup))
        {
            grup.Isle(olay);
            return;
        }
        var yeniGrup = new ProfilerGrubu(imza, olay);
        _gruplar[imza] = yeniGrup;
        Gruplar.Add(yeniGrup);
    }

    /// <summary>Grupları "en çok tekrarlayan üstte" tutar. Tam yeniden kurmak seçimi düşürürdü —
    /// yalnız yerinden oynaması gerekenler Move ile taşınır (tipik akışta birkaç grup).</summary>
    private void GruplariSirala()
    {
        List<ProfilerGrubu> hedef = [.. _gruplar.Values
            .OrderByDescending(g => g.Kez)
            .ThenByDescending(g => g.MaksMs)];
        for (int i = 0; i < hedef.Count; i++)
        {
            int mevcut = Gruplar.IndexOf(hedef[i]);
            if (mevcut != i && mevcut >= 0)
                Gruplar.Move(mevcut, i);
        }
    }

    // ── 🤖 AI yorumu (v23-S4) ─────────────────────────────────────────────────
    public bool AiGorunur => _asistanSor is not null;

    [ObservableProperty] private string _aiYorum = "";

    [ObservableProperty] private bool _aiYorumAcik;

    /// <summary>Seçili olayı/grubu/kilitlenmeyi asistana yorumlatır: muhtemel neden + somut öneri.
    /// Modele ham deadlock XML'i DEĞİL çözümlenmiş özet gider (istem şişmez, model şaşmaz).</summary>
    [RelayCommand]
    private async Task AiYorumlaAsync()
    {
        if (_asistanSor is null)
            return;
        string? ozet = OlayOzetiKur();
        if (ozet is null)
        {
            Ozet = "Yorumlatmak için akıştan bir satır seçin.";
            return;
        }
        AiYorumAcik = true;
        AiYorum = "🤖 Model düşünüyor…";
        AsistanCevabi cevap = await _asistanSor(AsistanIstemleri.ProfilerYorumla(ozet));
        AiYorum = cevap.Metin;
    }

    /// <summary>Aktif seçimden modele gidecek özet metni kurar; seçim yoksa null.</summary>
    private string? OlayOzetiKur()
    {
        if (GruplaAcik && SeciliGrup is { } g)
            return $"GRUP — aynı sorgunun {g.Kez} varyantı ({g.Olay}): ortalama {g.OrtMs} ms · "
                + $"en yüksek {g.MaksMs} ms · toplam CPU {g.ToplamCpuMs} ms · "
                + $"ilk {g.IlkZaman:HH:mm:ss} · son {g.SonZaman:HH:mm:ss}\n"
                + $"ÖRNEK SORGU:\n{Kirp(g.OrnekMetin)}";

        if (SeciliOlay is not { } o)
            return null;
        if (o.Olay == "Deadlock")
            return DeadlockOzeti(o.Metin);
        return $"{o.Olay} · süre {o.SureMs} ms · CPU {o.CpuMs} ms · okuma {o.Reads} · "
            + $"yazma {o.Writes} · satır {o.SatirSayisi} · veritabanı {o.Veritabani} · "
            + $"uygulama {o.Uygulama} · kullanıcı {o.Kullanici}\n"
            + $"{(o.Olay == "Hata" ? "HATA" : "SORGU")}:\n{Kirp(o.Metin ?? "")}";
    }

    /// <summary>Kilitlenmeyi çözümleyip metin özetler; XML çözülmezse ham metnin başı gider.</summary>
    private static string DeadlockOzeti(string? xml)
    {
        DeadlockGrafigi? grafik = DeadlockCozumleyici.Coz(xml);
        if (grafik is null)
            return "KİLİTLENME (deadlock) — ham rapor:\n" + Kirp(xml ?? "");
        var sb = new System.Text.StringBuilder("KİLİTLENME (deadlock):\n");
        foreach (DeadlockSureci s in grafik.Surecler)
            sb.AppendLine($"- Süreç SPID {s.Spid} ({s.Uygulama} · {s.Kullanici})"
                + (s.KurbanMi ? " [KURBAN]" : "") + $": {Kirp(s.Sorgu ?? "", 400)}");
        foreach (DeadlockKaynagi k in grafik.Kaynaklar)
            sb.AppendLine($"- Kaynak {k.Tur} {k.Ad}: sahip "
                + string.Join(",", k.Sahipler.Select(x => $"{x.SurecId}({x.Kip})"))
                + " · bekleyen "
                + string.Join(",", k.Bekleyenler.Select(x => $"{x.SurecId}({x.Kip})")));
        return sb.ToString().TrimEnd();
    }

    private static string Kirp(string s, int en = 2000)
        => s.Length <= en ? s : s[..en] + " …(kırpıldı)";

    // ── Dışa aktarma (K3: CSV + pano; pencere I/O'yu yapar — LogAnaliz deseni) ─
    public bool SonucVar => GruplaAcik ? Gruplar.Count > 0 : Olaylar.Count > 0;

    public void DurumBildir(string mesaj) => Ozet = mesaj;

    /// <summary>Aktif GÖRÜNÜMÜ dışa verir: grup modunda grup satırları, değilse ham akış.</summary>
    public string SatirMetni(char ayrac, bool csv = false)
        => GruplaAcik ? GrupSatirMetni(ayrac, csv) : OlaySatirMetni(ayrac, csv);

    private string GrupSatirMetni(char ayrac, bool csv)
    {
        var sb = new System.Text.StringBuilder();
        string[] baslik = ["Kez", "Ort ms", "Maks ms", "Toplam CPU ms", "Ilk", "Son", "Olay", "Ornek metin"];
        sb.AppendLine(string.Join(ayrac, csv ? baslik.Select(h => CsvAlan(h, ayrac)) : baslik));
        foreach (ProfilerGrubu g in Gruplar)
        {
            string[] h =
            [
                g.Kez.ToString(), g.OrtMs.ToString(), g.MaksMs.ToString(), g.ToplamCpuMs.ToString(),
                g.IlkZaman.ToString("HH:mm:ss.fff"), g.SonZaman.ToString("HH:mm:ss.fff"),
                g.Olay, CsvTekSatir(g.OrnekMetin),
            ];
            sb.AppendLine(string.Join(ayrac, csv ? h.Select(x => CsvAlan(x, ayrac)) : h));
        }
        return sb.ToString();
    }

    private string OlaySatirMetni(char ayrac, bool csv)
    {
        var sb = new System.Text.StringBuilder();
        string[] baslik = ["Zaman", "Olay", "Sure ms", "CPU ms", "Reads", "Writes", "Satir",
            "Veritabani", "Uygulama", "Kullanici", "Makine", "SPID", "Metin"];
        sb.AppendLine(string.Join(ayrac, csv ? baslik.Select(h => CsvAlan(h, ayrac)) : baslik));
        foreach (ProfilerOlayi o in Olaylar)
        {
            string[] h =
            [
                o.Zaman.ToString("dd.MM.yyyy HH:mm:ss.fff"), o.Olay, o.SureMs.ToString(),
                o.CpuMs.ToString(), o.Reads.ToString(), o.Writes.ToString(), o.SatirSayisi.ToString(),
                o.Veritabani ?? "", o.Uygulama ?? "", o.Kullanici ?? "", o.Makine ?? "",
                o.Spid.ToString(), CsvTekSatir(o.Metin ?? ""),
            ];
            sb.AppendLine(string.Join(ayrac, csv ? h.Select(x => CsvAlan(x, ayrac)) : h));
        }
        return sb.ToString();
    }

    private static string CsvAlan(string s, char ayrac)
        => s.Contains(ayrac) || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string CsvTekSatir(string s)
        => s.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ").Replace("\r", " ⏎ ").Replace('\t', ' ');

    // ── ISekme ────────────────────────────────────────────────────────────────
    public string Baslik => "🔍 Profiler";

    public SekmeDurumu Durum => Calisiyor ? SekmeDurumu.Calisiyor : SekmeDurumu.Tamamlandi;

    /// <summary>Sekme kapanışı / bağlantı değişimi. v23-S6 (canlı tanı 28 Eyl): MainViewModel
    /// bağlantı değişiminde bunu SIRAYLA bekler — eski sunucu koptuysa STOP+DROP, bağlantı açma
    /// süresi + 30 sn komut tavanına kadar asılıp yeni bağlantıyı DONMUŞ gösteriyordu. Temizlik
    /// artık 5 sn'le sınırlı beklenir; yetişemezse arkada sürer (best-effort — oturum adı sabit,
    /// bir sonraki Başlat da IF EXISTS ile siler).</summary>
    public async Task KapatAsync()
    {
        _cts?.Cancel();
        if (_oturumSunucuda)
        {
            Task temizlik = OturumuTemizleAsync();
            await Task.WhenAny(temizlik, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        Calisiyor = false;
    }
}

/// <summary>
/// 🧠 Grup satırı (v23-S2): bir imzanın canlı sayaçları. ObservableObject — olay geldikçe
/// sayaçlar YERİNDE artar, grid satırı kendini günceller (satır değiştirilmez → seçim düşmez).
/// </summary>
public sealed partial class ProfilerGrubu : ObservableObject
{
    private long _toplamSureMs;

    public ProfilerGrubu(string imza, ProfilerOlayi ilk)
    {
        Imza = imza;
        OrnekMetin = ilk.Metin ?? "";
        Olay = ilk.Olay;
        IlkZaman = ilk.Zaman;
        _sonZaman = ilk.Zaman;
        Isle(ilk);
    }

    public string Imza { get; }

    /// <summary>Grubun İLK olayının metni — Log Analizi'ndeki "örnek mesaj" sözleşmesi.</summary>
    public string OrnekMetin { get; }

    public string Olay { get; }

    public DateTime IlkZaman { get; }

    [ObservableProperty] private int _kez;
    [ObservableProperty] private long _ortMs;
    [ObservableProperty] private long _maksMs;
    [ObservableProperty] private long _toplamCpuMs;
    [ObservableProperty] private DateTime _sonZaman;

    /// <summary>Yeni olayı sayaçlara işler (ortalama toplamdan türetilir — kayan hata yok).</summary>
    public void Isle(ProfilerOlayi o)
    {
        Kez++;
        _toplamSureMs += o.SureMs;
        OrtMs = _toplamSureMs / Kez;
        if (o.SureMs > MaksMs)
            MaksMs = o.SureMs;
        ToplamCpuMs += o.CpuMs;
        SonZaman = o.Zaman;
    }
}
