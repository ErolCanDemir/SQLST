using System.Windows;
using System.Windows.Controls;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// 🔗 Bağımlılık Ağacı (kullanıcı isteği 2026-08-03): sağ tık → nesnenin iki yönlü bağımlılıkları
/// AĞAÇ olarak. Her düğüm tembel yüklenir — açıldıkça aynı yönde derinleşir (SP → SP → tablo
/// zinciri). Sorgu üretimi SAF <see cref="BagimlilikGezgini.YonSorgusu"/>'nda; koşum köprüyle
/// (30 sn timeout'lu LogAnalizSorgusuAsync yolu — MSSQL'e özgü, UI MotorMssqlMu ile kapılı).
/// </summary>
public partial class BagimlilikAgaciPenceresi : Window
{
    private readonly Func<string, Task<QueryResult>> _calistir; // sql → sonuç (DB pencere ömrünce sabit)
    private readonly Action<string, string, string?> _sekmeAcVeCalistir;
    private readonly string _veritabani;
    private readonly string _kokTamAd;
    private readonly string _kokTur;
    private readonly string _motorId; // madde 2 (2026-08-03): "mssql" | "postgres" — sorgular motora göre

    /// <summary>Düğüm durumu: tam ad + tür + yön + atalar (döngü tespiti) + tembel yükleme bayrağı.</summary>
    private sealed class DugumBilgi
    {
        public required string TamAd { get; init; }
        public required string Tur { get; init; }
        public required bool Kullananlar { get; init; }
        public required IReadOnlySet<string> Atalar { get; init; }
        public bool Yuklendi { get; set; }
    }

    public BagimlilikAgaciPenceresi(
        SemaNesnesi nesne, string veritabani,
        Func<string, Task<QueryResult>> calistir,
        Action<string, string, string?> sekmeAcVeCalistir,
        string motorId = "mssql")
    {
        _calistir = calistir;
        _sekmeAcVeCalistir = sekmeAcVeCalistir;
        _veritabani = veritabani;
        _kokTamAd = nesne.TamAd;
        _kokTur = nesne.Tur.ToString();
        _motorId = motorId;
        InitializeComponent();
        Baslik.Text = $"{nesne.TamAd}  ·  {_kokTur}  ·  {veritabani}";
        if (motorId == "postgres")
            Not.Text += " Not: PostgreSQL'de view bağımlılıkları kesindir; fonksiyonlar gövde"
                + " metni eşleşmesiyle bulunur (~ işaretli, yaklaşık).";
        if (motorId == "mysql")
            Not.Text += " Not: MySQL'de view bağımlılıkları VIEW_TABLE_USAGE'dan gelir (8.0.13+"
                + " gerekir; MariaDB'de yoktur — düğümde hata görünür); rutinler gövde metni"
                + " eşleşmesiyle bulunur (~ işaretli, yaklaşık).";

        Agac.Items.Add(YonKokuKur("⬅ Bunu KULLANANLAR (ALTER'dan kim etkilenir?)", kullananlar: true));
        Agac.Items.Add(YonKokuKur("➡ Bunun KULLANDIKLARI (neye bağımlı?)", kullananlar: false));
        ((TreeViewItem)Agac.Items[0]).IsExpanded = true; // ilk seviye hemen gelsin
    }

    private TreeViewItem YonKokuKur(string baslik, bool kullananlar)
    {
        var kok = new TreeViewItem
        {
            Header = baslik,
            FontWeight = FontWeights.SemiBold,
            Tag = new DugumBilgi
            {
                TamAd = _kokTamAd, Tur = _kokTur, Kullananlar = kullananlar,
                Atalar = new HashSet<string>([_kokTamAd], StringComparer.OrdinalIgnoreCase),
            },
        };
        kok.Items.Add(new TreeViewItem { Header = "yükleniyor…", IsEnabled = false });
        kok.Expanded += Dugum_Acildi;
        return kok;
    }

    private TreeViewItem DugumKur(string tamAd, string tur, bool kullananlar, IReadOnlySet<string> atalar)
    {
        bool dongu = atalar.Contains(tamAd);
        var dugum = new TreeViewItem
        {
            Header = $"{tamAd}  ·  {tur}{(dongu ? "  ↻ döngü" : "")}",
            FontWeight = FontWeights.Normal,
        };
        if (dongu)
            return dugum; // zinciri burada kes — sonsuz açılım olmaz

        var atalarla = new HashSet<string>(atalar, StringComparer.OrdinalIgnoreCase) { tamAd };
        dugum.Tag = new DugumBilgi { TamAd = tamAd, Tur = tur, Kullananlar = kullananlar, Atalar = atalarla };
        dugum.Items.Add(new TreeViewItem { Header = "yükleniyor…", IsEnabled = false });
        dugum.Expanded += Dugum_Acildi;
        dugum.MouseDoubleClick += (_, e) =>
        {
            // Çift tık: o düğümün İKİ yönlü script'i sekmede (mevcut grid deneyimi korunur).
            _sekmeAcVeCalistir($"bagimlilik-{tamAd}", BagimlilikGezgini.ScriptUret(tamAd, tur, _motorId), _veritabani);
            e.Handled = true; // üst düğümlere köpürüp onların script'ini de açmasın
        };
        return dugum;
    }

    private async void Dugum_Acildi(object sender, RoutedEventArgs e)
    {
        // Expanded üst öğelere köpürür — yalnız gerçekten açılan düğüm yüklensin.
        if (sender is not TreeViewItem dugum || !ReferenceEquals(e.OriginalSource, dugum))
            return;
        if (dugum.Tag is not DugumBilgi bilgi || bilgi.Yuklendi)
            return;

        bilgi.Yuklendi = true;
        dugum.Items.Clear();
        QueryResult sonuc = await _calistir(BagimlilikGezgini.YonSorgusu(bilgi.TamAd, bilgi.Kullananlar, _motorId));

        if (sonuc.Hata is not null)
        {
            bilgi.Yuklendi = false; // tekrar açılınca yeniden denesin
            dugum.Items.Add(new TreeViewItem { Header = $"⚠ {sonuc.Hata.Mesaj}", IsEnabled = false });
            return;
        }

        IReadOnlyList<object?[]> satirlar = sonuc.ResultSetler.Count > 0 ? sonuc.ResultSetler[0].Satirlar : [];
        if (satirlar.Count == 0)
        {
            dugum.Items.Add(new TreeViewItem { Header = "(bağımlılık yok)", IsEnabled = false });
            return;
        }

        foreach (object?[] satir in satirlar)
        {
            string tamAd = satir.Length > 0 ? satir[0]?.ToString() ?? "?" : "?";
            string tur = satir.Length > 1 ? satir[1]?.ToString() ?? "?" : "?";
            dugum.Items.Add(DugumKur(tamAd, tur, bilgi.Kullananlar, bilgi.Atalar));
        }
    }

    private void ScriptAc_Click(object sender, RoutedEventArgs e)
        => _sekmeAcVeCalistir($"bagimlilik-{_kokTamAd}",
            BagimlilikGezgini.ScriptUret(_kokTamAd, _kokTur, _motorId), _veritabani);
}
