using System.Globalization;
using System.Windows;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// Mongo "find yardımcısı" (v8): Compass tarzı alanlardan (Filter/Sort/Project/Limit/Skip)
/// çalıştırılabilir find JSON'u üretir (<see cref="MongoBulYazici"/>) ve yeni sorgu sekmesinde açar.
/// SQL profillerinde açılmaz — çağıran yalnız MongoDB'de gösterir.
///
/// Otomatik doldurma (v11-öncesi #3, 2026-07-25): koleksiyon adı ve Filter/Sort/Project kutularında
/// Compass tarzı öneri — koleksiyonun ALAN ENVANTERİ şema önbelleğinden gelir (ağ yok), Sort'ta
/// alan sonrası 1/-1, Filter'da $ ile operatör önerilir. Mantık SAF <see cref="MongoBulTamamlama"/>.
/// </summary>
public partial class MongoBulPenceresi : Window
{
    private readonly Action<string, string> _sekmeyeAc;
    private IReadOnlyList<string> _koleksiyonAdlari = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _alanSozlugu = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// TEK koleksiyonun alanlarını on-demand getirir (v22-S3 saha turu-3 m.5). Mongo'da koleksiyon
    /// LİSTESİ bilerek alansız gelir (liste hızlı açılsın diye), bu yüzden alanlar seçilen
    /// koleksiyon için ayrıca sorulmak zorunda.
    /// </summary>
    private readonly Func<string, Task<IReadOnlyList<string>>>? _alanlariGetir;
    private string _alanIstegi = ""; // yarış bekçisi: son istenen koleksiyon

    public MongoBulPenceresi(
        Action<string, string> sekmeyeAc,
        string? mevcutSorgu = null,
        Func<Task<IReadOnlyList<SemaNesnesi>>>? koleksiyonlariGetir = null,
        Func<string, Task<IReadOnlyList<string>>>? alanlariGetir = null)
    {
        _alanlariGetir = alanlariGetir;
        _sekmeyeAc = sekmeyeAc;
        InitializeComponent();
        OnDoldur(mevcutSorgu);

        // Canlı normalleştirme (v12-S6, kullanıcı isteği: "sorttaki json işini tüm alanlara"):
        // JSON kutusundan odak çıkınca içerik güzelleştirilir ({} sarma + girinti — Sort'taki
        // görünüm), bozuksa hata ÜRET'İ BEKLEMEDEN alt banda düşer.
        FilterKutusu.LostFocus += (_, _) => KutuyuGuzellestir(FilterKutusu, "filter");
        SortKutusu.LostFocus += (_, _) => KutuyuGuzellestir(SortKutusu, "sort");
        ProjectKutusu.LostFocus += (_, _) => KutuyuGuzellestir(ProjectKutusu, "projection");

        // Adaptörler: koleksiyon kutusu düz ad; JSON kutuları bağlam çözümlü (anahtar=alan, değer=1/-1…)
        _ = new TamamlamaAdaptoru(KoleksiyonKutusu,
            (metin, _) => MongoBulTamamlama.KoleksiyonOner(metin.Trim(), _koleksiyonAdlari));
        _ = new TamamlamaAdaptoru(FilterKutusu,
            (metin, caret) => MongoBulTamamlama.Oner(MongoKutu.Filter, metin, caret, SeciliAlanlar()));
        _ = new TamamlamaAdaptoru(SortKutusu,
            (metin, caret) => MongoBulTamamlama.Oner(MongoKutu.Sort, metin, caret, SeciliAlanlar()));
        _ = new TamamlamaAdaptoru(ProjectKutusu,
            (metin, caret) => MongoBulTamamlama.Oner(MongoKutu.Project, metin, caret, SeciliAlanlar()));

        if (koleksiyonlariGetir is not null)
        {
            // async void Loaded sınırı: şema yüklenemezse öneri sessizce kapalı kalır — pencere çalışır.
            Loaded += async (_, _) =>
            {
                try
                {
                    IReadOnlyList<SemaNesnesi> nesneler = await koleksiyonlariGetir();
                    _koleksiyonAdlari = [.. nesneler.Select(n => n.Ad)];
                    // Mongo'da bu liste ALANSIZ gelir (bilerek); boş girdi yazılmaz ki m.5'teki
                    // on-demand keşif "zaten biliyorum" sanıp atlamasın.
                    foreach (SemaNesnesi n in nesneler.Where(n => n.Kolonlar.Count > 0))
                        _alanSozlugu[n.Ad] = [.. n.Kolonlar.Select(k => k.Ad)];
                }
                catch (Exception)
                {
                    // öneri istenirken pencerenin kendisi düşmesin — doldurma özelliği pasif kalır
                }
                await AlanlariTazeleAsync(); // ön-dolu koleksiyon varsa alanları hemen getir
            };
        }
        else
        {
            Loaded += async (_, _) => await AlanlariTazeleAsync();
        }

        // Koleksiyon değişince alan envanteri tazelenir — Filter/Sort/Project önerileri buna dayanır.
        KoleksiyonKutusu.TextChanged += async (_, _) => await AlanlariTazeleAsync();
    }

    /// <summary>Seçili koleksiyonun alanları; koleksiyon tanınmıyorsa boş (öneri yalnız operatör/değer).
    /// Filter/Sort/Project tamamlamasının BESLENDİĞİ küme — testler doğrudan bunu denetler.</summary>
    internal IReadOnlyList<string> SeciliAlanlar()
        => _alanSozlugu.TryGetValue(KoleksiyonKutusu.Text?.Trim() ?? "", out IReadOnlyList<string>? alanlar)
            ? alanlar : [];

    /// <summary>
    /// m.5 (kullanıcı: "Find ekranında tabloyu seçince sort/filter alanlarına girerken koleksiyon
    /// ALANLARINI da getirsin — şu an alanları değil alanlardan SONRASINI getiriyor"): kutulardaki
    /// alan önerisi <see cref="SeciliAlanlar"/>'a dayanır; Mongo'da o sözlük BOŞ kaldığı için
    /// anahtar konumunda hiç öneri çıkmıyor, yalnız değer konumundaki 1/-1 (Sort) ve $operatörler
    /// (Filter) görünüyordu — "alanlardan sonrası" tam olarak buydu. Alanlar artık seçilen
    /// koleksiyon için bir kez getirilip sözlüğe yazılır.
    /// </summary>
    private async Task AlanlariTazeleAsync()
    {
        string ad = KoleksiyonKutusu.Text?.Trim() ?? "";
        _alanIstegi = ad;

        if (ad.Length == 0)
        {
            AlanNotu.Text = "";
            return;
        }
        if (_alanSozlugu.TryGetValue(ad, out IReadOnlyList<string>? bilinen))
        {
            AlanNotu.Text = $"{bilinen.Count} alan";
            return;
        }
        // Adı henüz tam yazılmamışsa (kullanıcı yazmaya devam ediyor) sunucuya gitme.
        if (_alanlariGetir is null || !_koleksiyonAdlari.Contains(ad, StringComparer.OrdinalIgnoreCase))
        {
            AlanNotu.Text = "";
            return;
        }

        AlanNotu.Text = "alanlar yükleniyor…";
        IReadOnlyList<string> alanlar;
        try
        {
            alanlar = await _alanlariGetir(ad);
        }
        catch (Exception)
        {
            alanlar = []; // öneri bir kolaylıktır — okunamazsa ekran çalışmaya devam eder
        }

        if (!string.Equals(_alanIstegi, ad, StringComparison.OrdinalIgnoreCase))
            return; // kullanıcı bu arada başka koleksiyona geçti — bayat yanıt yazmaz

        if (alanlar.Count > 0)
            _alanSozlugu[ad] = alanlar;
        AlanNotu.Text = alanlar.Count > 0 ? $"{alanlar.Count} alan" : "⚠ alan okunamadı";
    }

    /// <summary>Aktif sekmedeki metin bir <c>find</c> belgesiyse alanları ondan doldurur (borç kapanışı:
    /// geri-ayrıştırma). Değilse sessizce boş kalır — kullanıcı sıfırdan da kurabilir.</summary>
    private void OnDoldur(string? mevcutSorgu)
    {
        if (MongoBulYazici.Ayristir(mevcutSorgu) is not { Istek: { } istek })
            return;

        KoleksiyonKutusu.Text = istek.Koleksiyon;
        FilterKutusu.Text = istek.Filtre ?? "";
        SortKutusu.Text = istek.Sirala ?? "";
        ProjectKutusu.Text = istek.Yansit ?? "";
        LimitKutusu.Text = istek.Limit?.ToString(CultureInfo.InvariantCulture) ?? "";
        SkipKutusu.Text = istek.Atla?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private void Uret_Click(object sender, RoutedEventArgs e)
    {
        var istek = new MongoBulIstegi(
            KoleksiyonKutusu.Text?.Trim() ?? "",
            Bosuysa(FilterKutusu.Text), Bosuysa(SortKutusu.Text), Bosuysa(ProjectKutusu.Text),
            Tamsayi(LimitKutusu.Text), Tamsayi(SkipKutusu.Text));

        (string? json, string? hata) = MongoBulYazici.Uret(istek);
        if (json is null)
        {
            HataMetni.Text = $"⚠ {hata}";
            return;
        }

        _sekmeyeAc($"{istek.Koleksiyon}.json", json);
        Close();
    }

    /// <summary>Odak çıkışında kutuyu Sort görünümüne normaller; hatayı anında gösterir/temizler.</summary>
    private void KutuyuGuzellestir(System.Windows.Controls.TextBox kutu, string ad)
    {
        (string? guzel, string? hata) = MongoBulYazici.KutuGuzellestir(kutu.Text);
        if (hata is not null)
        {
            HataMetni.Text = $"⚠ {ad} geçerli bir JSON nesnesi ({{…}}) olmalı: {hata}";
            return;
        }

        if (guzel is not null)
            kutu.Text = guzel;
        HataMetni.Text = "";
    }

    private void Iptal_Click(object sender, RoutedEventArgs e) => Close();

    private static string? Bosuysa(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int? Tamsayi(string? s)
        => int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
}
