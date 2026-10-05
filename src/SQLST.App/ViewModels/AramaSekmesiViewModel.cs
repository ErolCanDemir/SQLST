using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.ViewModels;

/// <summary>
/// Sunucuda metin arama sekmesi (V5-S2). Aramayı BU sekme yürütür — plan sekmesinin aksine
/// hazır bir sonuç almaz, çünkü kullanıcı aynı sekmede arka arkaya arama yapar.
///
/// <b>Kapsam (kullanıcı kararı, 19 Tem 2026):</b> yalnız SEÇİLİ veritabanı ve yalnız nesne
/// TANIMLARI (view/SP/fonksiyon gövdeleri). Tablo VERİSİ içinde arama yoktur:
/// <c>LIKE '%x%'</c> baştan joker olduğundan hiçbir index kullanılamaz, her tablonun her metin
/// kolonu baştan sona taranırdı — milyonlarca kayıtta uygulamayı kilitlerdi.
///
/// <b>Mongo:</b> bu sekme açılmaz. Koleksiyonların tanım metni yoktur
/// (<c>MongoSchemaService.TanimGetirAsync</c> null döner) ve Mongo <see cref="ILehce"/>
/// uygulamaz. Çoklu-motor kuralı gereği yarı çalışan bir arama sunmak yerine o motorda gizlenir.
/// </summary>
public partial class AramaSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly ISqlExecutor _executor;
    private readonly Func<ConnectionProfile?> _profilGetir;
    private readonly Func<MotorTuru, ILehce> _lehceGetir;

    private CancellationTokenSource? _iptal;

    /// <summary>
    /// Profil ve lehçe SAKLANMAZ, her aramada yeniden okunur: sekme yeniden kullanıldığından
    /// (aynı anda tek arama sekmesi) yakalanan bir profil bağlantı değişince bayatlardı.
    /// </summary>
    public AramaSekmesiViewModel(
        ISqlExecutor executor,
        Func<ConnectionProfile?> profilGetir,
        Func<MotorTuru, ILehce> lehceGetir,
        ObservableCollection<string> veritabaniAdlari,
        string? secilenVeritabani,
        MongoAramaDelegesi? mongoArayici = null)
    {
        _executor = executor;
        _profilGetir = profilGetir;
        _lehceGetir = lehceGetir;
        _mongoArayici = mongoArayici;
        VeritabaniAdlari = veritabaniAdlari;
        _secilenVeritabani = secilenVeritabani;
    }

    public ObservableCollection<string> VeritabaniAdlari { get; }

    /// <summary>
    /// Aramanın hedef veritabanı. Sekmenin KENDİ seçicisidir (teşhis sekmelerindeki desen):
    /// kullanıcı bulgusu 2026-07-19 — sekme açılışta yakaladığı veritabanında aramaya devam
    /// ediyordu, ağaçtan başka bir veritabanı seçmek arama kapsamını değiştirmiyordu.
    /// </summary>
    [ObservableProperty] private string? _secilenVeritabani;

    partial void OnSecilenVeritabaniChanged(string? value)
    {
        OnPropertyChanged(nameof(Baslik));
        OnPropertyChanged(nameof(Kapsam));

        // Kapsam değişti → ekrandaki sonuçlar artık başka bir veritabanına ait. Bayat sonuç
        // göstermek yanlış cevaptan beterdir: kullanıcı doğru veritabanına baktığını sanır.
        Sonuclar.Clear();
        SeciliSonuc = null;
        Arandi = false;
        Hata = null;
        OnPropertyChanged(nameof(Ozet));
    }

    // "Metin arama"→"Kod arama" (kullanıcı isteği 2026-08-03) — VERİ arayan yeni özellikten ayrışsın.
    public string Baslik => $"🔍 Kod arama · {SecilenVeritabani ?? _profilGetir()?.Ad}";

    public SekmeDurumu Durum => Araniyor ? SekmeDurumu.Calisiyor : SekmeDurumu.Tamamlandi;

    /// <summary>
    /// Hangi kapsamda arandığı ekranda YAZAR — kullanıcı bulunamayanı yanlış yorumlamasın.
    /// Metin MOTORA GÖRE değişir: MongoDB'de aranan şeyler farklıdır (B5/A6) ve bunu
    /// gizlemek "aradım, yok" diyen kullanıcıyı yanıltırdı.
    /// </summary>
    public string Kapsam
    {
        get
        {
            string yer = SecilenVeritabani ?? "(bağlantının varsayılan veritabanı)";
            return _profilGetir()?.Motor == MotorTuru.Mongo
                ? $"Aranan yer: {yer} — view TANIMLARI (viewOn + pipeline) ve INDEX tanımları. "
                  + "Belge verisi ve koleksiyon adları taranmaz."
                : $"Aranan yer: {yer} — view, stored procedure ve fonksiyon TANIMLARI. "
                  + "Tablo verisi taranmaz.";
        }
    }

    [ObservableProperty] private string _aranan = "";

    [ObservableProperty] private bool _buyukKucukDuyarli;

    [ObservableProperty] private string? _hata;

    public ObservableCollection<AramaSonucu> Sonuclar { get; } = [];

    [ObservableProperty] private AramaSonucu? _seciliSonuc;

    [ObservableProperty] private AramaEslesmesi? _seciliEslesme;

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

    /// <summary>Arama yapıldı mı — "sonuç yok" ile "henüz aranmadı" ayrımı için.</summary>
    [ObservableProperty] private bool _arandi;

    /// <summary>
    /// Sonuç tavanına takıldı mı (B4/A5). Sunucu en çok <see cref="ILehce.AramaTavani"/>
    /// nesne döndürür; tam o kadar geldiyse daha fazlası olma İHTİMALİ vardır.
    /// </summary>
    [ObservableProperty] private bool _tavanaTakildi;

    public string Ozet
    {
        get
        {
            if (!Arandi)
                return "";
            if (Sonuclar.Count == 0)
                return "Eşleşme bulunamadı.";

            string temel = $"{Sonuclar.Count} nesne, toplam "
                         + $"{Sonuclar.Sum(s => s.EslesmeSayisi)} satırda eşleşti.";

            // Sessizce kırpmak yanlış cevaptan beterdir: kullanıcı eksik listeyi TAM sanır.
            return TavanaTakildi
                ? temel + $" ⚠ Yalnız ilk {ILehce.AramaTavani} nesne gösteriliyor — "
                        + "aramayı daraltın (daha uzun/özgül bir metin)."
                : temel;
        }
    }

    /// <summary>Seçili sonucun tanımını yeni sorgu sekmesinde, eşleşen satırda açar.</summary>
    public Action<string, string, int>? TanimiSekmedeAc { get; set; }

    private bool AramaMumkun => !Araniyor;

    [RelayCommand(CanExecute = nameof(AramaMumkun))]
    private async Task AraAsync()
    {
        string aranan = Aranan.Trim();
        if (aranan.Length == 0)
            return;

        // Profil her aramada TAZE okunur; kullanıcı arada bağlantı değiştirmiş olabilir.
        if (_profilGetir() is not { } profil)
        {
            Hata = "Bağlantı yok.";
            return;
        }

        // Önceki arama sürüyorsa iptal et: kullanıcı Enter'a üst üste basabilir.
        if (_iptal is not null)
            await _iptal.CancelAsync();
        _iptal?.Dispose();
        _iptal = new CancellationTokenSource();
        CancellationToken ct = _iptal.Token;

        Araniyor = true;
        Hata = null;
        Sonuclar.Clear();
        SeciliSonuc = null;

        try
        {
            // MongoDB ayrı ailedir (ILehce uygulamaz) → kendi arama yolu (B5/A6)
            IReadOnlyList<AramaSonucu> bulunan = profil.Motor == MotorTuru.Mongo
                ? await MongoAraAsync(profil, aranan, ct)
                : await SqlAraAsync(profil, aranan, ct);

            if (ct.IsCancellationRequested || Hata is not null)
                return;

            foreach (AramaSonucu s in bulunan)
                Sonuclar.Add(s);

            Arandi = true;
            SeciliSonuc = Sonuclar.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            // kullanıcı yeni arama başlattı — sessiz
        }
        finally
        {
            Araniyor = false;
            OnPropertyChanged(nameof(Ozet));
        }
    }

    /// <summary>SQL ailesi: süzme SUNUCUDA yapılır, ağdan yalnız eşleşenler geçer.</summary>
    private async Task<IReadOnlyList<AramaSonucu>> SqlAraAsync(
        ConnectionProfile profil, string aranan, CancellationToken ct)
    {
        ILehce lehce = _lehceGetir(profil.Motor);
        var secenekler = new ExecuteOptions { VeritabaniOverride = SecilenVeritabani };

        QueryResult sonuc = await _executor.ExecuteAsync(
            profil, lehce.MetinAramaSorgusu(aranan), secenekler, ct);

        if (!sonuc.Basarili)
        {
            Hata = sonuc.Hata?.Mesaj ?? "Arama başarısız.";
            return [];
        }

        // Tavan ölçütü SUNUCUDAN DÖNEN satır sayısıdır, listeye eklenen sonuç sayısı değil:
        // Cozumle şifreli/eşleşmeyen tanımları eleyebilir, o zaman liste tavanın altında
        // kalır ama sunucu yine de kırpmış olabilir (B4/A5).
        int donenNesne = sonuc.ResultSetler.Count > 0 ? sonuc.ResultSetler[0].Satirlar.Count : 0;
        TavanaTakildi = donenNesne >= ILehce.AramaTavani;

        return MetinArayici.Cozumle(sonuc, lehce, aranan, BuyukKucukDuyarli);
    }

    /// <summary>
    /// MongoDB ailesi (B5/A6): view <c>pipeline</c>'ları ve index tanımları taranır.
    /// Süzme burada İSTEMCİDEDİR — Mongo üst verisinde sunucu tarafı metin süzgeci yoktur;
    /// çekilen şey belge VERİSİ değil şema üst verisi olduğundan kabul edilebilir.
    /// </summary>
    private async Task<IReadOnlyList<AramaSonucu>> MongoAraAsync(
        ConnectionProfile profil, string aranan, CancellationToken ct)
    {
        if (_mongoArayici is not { } ara)
        {
            Hata = "MongoDB araması bu kurulumda hazır değil.";
            return [];
        }

        IReadOnlyList<MongoMetinArayici.Bulgu> bulgular =
            await ara(profil, SecilenVeritabani ?? "", aranan, BuyukKucukDuyarli, ct);

        TavanaTakildi = bulgular.Count >= ILehce.AramaTavani;

        return [.. bulgular
            .Select(b => new AramaSonucu(
                Sema: b.Sema, Ad: b.Ad, Tur: b.Tur, Tanim: b.Tanim,
                Eslesmeler: MetinArayici.Eslesmeler(b.Tanim, aranan, BuyukKucukDuyarli)))
            .Where(s => s.Eslesmeler.Count > 0)
            .OrderByDescending(s => s.EslesmeSayisi)
            .ThenBy(s => s.TamAd)];
    }

    /// <summary>
    /// Mongo arama köprüsü — Infrastructure'a doğrudan bağımlılık kurmamak için delege
    /// olarak enjekte edilir (App katmanı zaten Infrastructure'ı görüyor ama ViewModel'i
    /// test edilebilir tutmak için sözleşme delege üzerinden).
    /// </summary>
    public delegate Task<IReadOnlyList<MongoMetinArayici.Bulgu>> MongoAramaDelegesi(
        ConnectionProfile profil, string veritabani, string aranan, bool duyarli, CancellationToken ct);

    private readonly MongoAramaDelegesi? _mongoArayici;

    /// <summary>
    /// Eşleşmeye çift tıklanınca tanımı açar. Tanım <see cref="AramaSonucu.Tanim"/> içinde
    /// SAKLI olduğundan sunucuya YENİDEN gidilmez.
    /// </summary>
    [RelayCommand]
    private void EslesmeyeGit(AramaEslesmesi? eslesme)
    {
        if (SeciliSonuc is { } s)
            TanimiSekmedeAc?.Invoke(s.TamAd, s.Tanim, eslesme?.Satir ?? 1);
    }

    public Task KapatAsync()
    {
        IptalYardimcisi.ArkaPlandaIptal(_iptal, birak: true); // m.15: Cancel UI'da bloklayabilir
        _iptal = null;
        return Task.CompletedTask;
    }
}
