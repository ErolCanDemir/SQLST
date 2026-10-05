using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// v20-S21 m.26 fikir 1: "bu id nerede geçiyor?" — değeri, tip uyumlu TÜM koleksiyon alanlarında
/// arar (Mongo'da FK yok). Adaylar SIRAYLA taranır (sunucu boğulmasın, ⏹ Durdur her an keser);
/// eşleşme bulunan satır anında listeye düşer — tarama bitmeden sonuç görünür.
/// Satıra çift tık → o koleksiyonun find'ı yeni sekmede açılır.
/// </summary>
public partial class MongoIdIziPenceresi : Window
{
    public sealed class Satir
    {
        public required string Koleksiyon { get; init; }
        public required string Alan { get; init; }
        public required long Eslesen { get; init; }
        public string EslesenMetni => Eslesen.ToString("N0");
    }

    private readonly ObservableCollection<Satir> _satirlar = [];
    private readonly string _deger;

    /// <summary>
    /// Aday alanlar ARTIK hazır gelmez (v22-S3 saha turu-3 m.3): pencere açıldıktan sonra burada
    /// üretilir — bkz. <see cref="TaraAsync"/>. null döner = koleksiyon envanteri okunamadı.
    /// </summary>
    private readonly Func<CancellationToken, Task<IReadOnlyList<IzAdayi>?>> _adaylariGetir;
    private IReadOnlyList<IzAdayi> _adaylar = [];
    private readonly Func<string, CancellationToken, Task<long?>> _sayimGetir;
    private readonly Action<string, string> _sekmedeAc; // (başlık, sorgu)
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _indexAlanlari;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// ⚡ v22-S1 saha turu-2 m.12 (kullanıcı: "büyük satırlı veritabanında çok yavaş"): adaylar artık
    /// TEK TEK değil, bu boyda GRUPLAR hâlinde eşzamanlı taranır. ÖLÇÜM (yerel MongoDB, 600.000 belge,
    /// index'siz alan): 8 aday sırayla <b>4.260 ms</b> ↔ paralel <b>654 ms</b> (6,5×). Tavan bilerek
    /// küçük: sunucuyu boğmadan gecikmeyi (ağ + tarama) örtüşülmüş tutar.
    /// </summary>
    private const int EszamanliTavan = 6;

    /// <summary>
    /// Tarama NESLİ (v22-S1 saha turu-2 m.11; v20-S21 m.2/m.15b'deki desenin aynısı). Neden gerekli:
    /// ⏹ Durdur yalnız iptal İSTEĞİ gönderir; Mongo sayımı sunucuda sürer ve await geri dönene dek
    /// (komut tavanı 60 sn) döngü parkta kalırdı — ekranda "Durduruluyor…" asılı kalıyordu (kullanıcı
    /// bulgusu). Durdur nesli ARTIRIR: UI ANINDA sonlanır, geciken sayımın devamı bayat nesilden
    /// geldiği için ne listeye satır ekler ne de yazı yazar.
    /// </summary>
    private int _nesil;

    // Özet Durdur'dan da yazılabilsin diye sayaçlar alan (döngü yerel değişkeni değil).
    private int _tarandi;
    private int _bulunan;
    private readonly System.Diagnostics.Stopwatch _kronometre = new();

    public MongoIdIziPenceresi(
        string deger,
        Func<CancellationToken, Task<IReadOnlyList<IzAdayi>?>> adaylariGetir,
        Func<string, CancellationToken, Task<long?>> sayimGetir,
        Action<string, string> sekmedeAc,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? indexAlanlari = null)
    {
        InitializeComponent();
        _deger = deger;
        _adaylariGetir = adaylariGetir;
        _sayimGetir = sayimGetir;
        _sekmedeAc = sekmedeAc;
        _indexAlanlari = indexAlanlari;

        DegerKutusu.Text = deger;
        Sonuclar.ItemsSource = _satirlar;
        Loaded += async (_, _) => await TaraAsync();
        Closed += (_, _) => IptalYardimcisi.ArkaPlandaIptal(_cts, birak: true);
    }

    private async Task TaraAsync()
    {
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        int nesil = ++_nesil;
        _tarandi = 0;
        _bulunan = 0;
        _kronometre.Restart();

        // 🚪 v22-S3 saha turu-3 m.3 (kullanıcı: "sağ tıklayınca açılmıyor… sonradan açıldı çok geç;
        // ekranı açıp aramayı öyle yapması lazım"): koleksiyon envanteri ESKİDEN pencere AÇILMADAN
        // ÖNCE, MainWindow'da bekleniyordu — büyük veritabanında saniyelerce EKRANDA HİÇBİR ŞEY
        // olmuyor, özellik bozuk sanılıyordu. Envanter artık pencere EKRANDAYKEN okunur ve ⏹ Durdur
        // bu evrede de keser (eskiden iptal edilemeyen bir bekleyişti).
        Ilerleme.Text = "Koleksiyon envanteri okunuyor… (ilk açılışta uzun sürer)";
        IReadOnlyList<IzAdayi>? adaylar = await _adaylariGetir(ct);
        if (ct.IsCancellationRequested || nesil != _nesil)
            return; // ⏹ Durdur envanter beklenirken basıldı — ekran ZATEN sonlandı
        _adaylar = adaylar ?? [];

        if (adaylar is null)
        {
            Ilerleme.Text = "";
            Ozet.Text = "Koleksiyon envanteri okunamadı (bağlantı yok ya da erişim reddedildi).";
            DurdurDugmesi.IsEnabled = false;
            return;
        }

        if (_adaylar.Count == 0)
        {
            Ilerleme.Text = "";
            Ozet.Text = "Bu değerin tipine uyan alan bulunamadı — koleksiyonların alan envanteri "
                      + "henüz süpürülmemiş olabilir (ağaçta koleksiyonları bir kez açın).";
            DurdurDugmesi.IsEnabled = false;
            return;
        }

        // ⚡ m.12 (1/2): index'li adaylar ÖNE alınır — ölçümde index'li sayım 2 ms, index'siz 493 ms
        // (600k belge). Böylece cevap saniyeler yerine ANINDA görünür; yavaş kısım sonra sürer.
        Ilerleme.Text = "Aday alanların index'leri okunuyor…";
        (IReadOnlyList<IzAdayi> sira, int indexliSayi) = await SirayaKoyAsync(ct);
        if (ct.IsCancellationRequested || nesil != _nesil)
            return;

        // ⚡ m.12 (2/2): adaylar 6'lı gruplar hâlinde EŞZAMANLI taranır (8 aday: 4.260 ms → 654 ms).
        for (int i = 0; i < sira.Count; i += EszamanliTavan)
        {
            if (ct.IsCancellationRequested || nesil != _nesil)
                return; // Durdur ekranı ZATEN sonlandırdı — bayat döngü hiçbir şey yazmaz
            IzAdayi[] parti = [.. sira.Skip(i).Take(EszamanliTavan)];
            Ilerleme.Text = $"Taranıyor… {_tarandi}/{sira.Count} · {parti[0].Koleksiyon}.{parti[0].Alan}"
                + (parti.Length > 1 ? $" (+{parti.Length - 1} eşzamanlı)" : "")
                + (i >= indexliSayi && indexliSayi > 0 ? "  ⏳ index'siz alanlar — bu kısım yavaş" : "");

            long?[] sayilar = await Task.WhenAll(
                parti.Select(a => _sayimGetir(MongoIdIzi.SayimSorgusu(a, _deger), ct)));
            if (nesil != _nesil)
                return; // sayımlar Durdur'dan SONRA döndü — satır eklenmez, yazı yazılmaz

            for (int j = 0; j < parti.Length; j++)
            {
                _tarandi++;
                if (sayilar[j] is > 0)
                {
                    _satirlar.Add(new Satir
                    {
                        Koleksiyon = parti[j].Koleksiyon, Alan = parti[j].Alan, Eslesen = sayilar[j]!.Value,
                    });
                    _bulunan++;
                }
            }
        }

        Sonlandir("Tarama bitti.");
    }

    /// <summary>
    /// Adayları index'liler ÖNDE olacak biçimde sıralar (v22-S1 m.12) ve index'li sayısını döner.
    /// Index bilgisi koleksiyon başına BİR kez sorulur (listIndexes ucuz); köprü yoksa ya da
    /// okunamazsa sıra DEĞİŞMEZ — özellik hızlanmaz ama bozulmaz.
    /// </summary>
    private async Task<(IReadOnlyList<IzAdayi> Sira, int IndexliSayi)> SirayaKoyAsync(CancellationToken ct)
    {
        if (_indexAlanlari is null)
            return (_adaylar, 0);

        var onbellek = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var indexli = new List<IzAdayi>();
        var digerleri = new List<IzAdayi>();
        foreach (IzAdayi aday in _adaylar)
        {
            if (ct.IsCancellationRequested)
                break;
            if (!onbellek.TryGetValue(aday.Koleksiyon, out IReadOnlyList<string>? alanlar))
                onbellek[aday.Koleksiyon] = alanlar = await _indexAlanlari(aday.Koleksiyon, ct);
            bool var_ = alanlar.Any(a => string.Equals(a, aday.Alan, StringComparison.OrdinalIgnoreCase));
            (var_ ? indexli : digerleri).Add(aday);
        }
        return ([.. indexli, .. digerleri], indexli.Count);
    }

    /// <summary>Tarama sonunu YAZAR (bitiş ya da Durdur) — düğmeyi kapatır, özeti basar.</summary>
    private void Sonlandir(string durum)
    {
        _kronometre.Stop();
        DurdurDugmesi.IsEnabled = false;
        Ilerleme.Text = durum;
        Ozet.Text = $"{_tarandi}/{_adaylar.Count} aday alan tarandı · {_bulunan} koleksiyonda bulundu · "
                  + $"{_kronometre.Elapsed.TotalSeconds:N1} sn"
                  + (_adaylar.Count >= MongoIdIzi.AdayTavani
                      ? $"  ⚠ aday sayısı {MongoIdIzi.AdayTavani} tavanında kesildi — sonuç eksik olabilir."
                      : "")
                  + "   |   satıra ÇİFT TIK → o koleksiyonun find'ı yeni sekmede açılır";
    }

    /// <summary>
    /// ⏹ Durdur — ANINDA sonlandırır (v22-S1 m.11). Nesil artar → geciken sayımın devamı ekrana
    /// yazamaz; iptal isteği havuzda gönderilir (UI'yı bloklamasın, m.15) ve CTS bırakılır.
    /// Eldeki satırlar EKRANDA KALIR: kullanıcı yarım sonuçla çalışabilsin.
    /// </summary>
    private void Durdur_Click(object sender, RoutedEventArgs e)
    {
        if (!DurdurDugmesi.IsEnabled)
            return;
        _nesil++;
        IptalYardimcisi.ArkaPlandaIptal(_cts, birak: true);
        _cts = null;
        Sonlandir("Durduruldu.");
    }

    private void Sonuc_CiftTik(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Sonuclar.SelectedItem is not Satir satir)
            return;
        _sekmedeAc($"🔎 {satir.Koleksiyon}",
            MongoIdIzi.BulSorgusu(new IzAdayi(satir.Koleksiyon, satir.Alan), _deger));
    }
}
