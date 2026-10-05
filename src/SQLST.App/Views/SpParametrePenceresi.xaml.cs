using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// SP parametre giriş penceresi (v16, kullanıcı isteği 2026-07-27: "SSMS'teki gibi bir pencere,
/// orada girilsin, çalıştır deyince girilenlerle bizim ekran açılsın"). Her giriş parametresi için
/// bir satır; değerler <see cref="NesneScriptleyici.ExecDolu"/> ile tip-duyarlı EXEC'e dönüşür.
/// Pencere ÇALIŞTIRMAZ — üretilen metni <see cref="Sonuc"/>'ta verir; çağıran sekmede açıp koşar.
/// </summary>
public partial class SpParametrePenceresi : Window
{
    /// <summary>Grid satırı: parametre kimliği sabit, yalnız Değer düzenlenir.</summary>
    public sealed class Satir : INotifyPropertyChanged
    {
        public required string Ad { get; init; }
        public required string Tip { get; init; }
        public required bool CikisMi { get; init; }
        public string Yon => CikisMi ? "OUTPUT" : "giriş";

        private string _deger = "";
        public string Deger
        {
            get => _deger;
            set { _deger = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Deger))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly SemaNesnesi _sp;
    private readonly ObservableCollection<Satir> _satirlar;

    /// <summary>Üretilen EXEC metni (Çalıştır/Sekmeye al'da dolar; İptal'de null kalır).</summary>
    public string? Sonuc { get; private set; }

    /// <summary>Çalıştır mı istendi (true) yoksa yalnız sekmeye alma mı (false).</summary>
    public bool CalistirIstendi { get; private set; }

    public SpParametrePenceresi(SemaNesnesi sp)
    {
        _sp = sp;
        InitializeComponent();
        Baslik.Text = $"EXEC {sp.TamAd}  ·  {sp.Parametreler.Count} parametre";
        _satirlar = [.. sp.Parametreler.Select(p => new Satir
        {
            Ad = p.Ad, Tip = p.Tip, CikisMi = p.CikisMi,
        })];
        Grid.ItemsSource = _satirlar;
    }

    private string Uret()
    {
        Dictionary<string, string?> degerler = _satirlar
            .Where(s => !s.CikisMi)
            .ToDictionary(s => s.Ad, s => (string?)s.Deger);
        return NesneScriptleyici.ExecDolu(_sp, degerler);
    }

    private void Calistir_Click(object sender, RoutedEventArgs e) => Bitir(calistir: true);

    private void SekmeyeAl_Click(object sender, RoutedEventArgs e) => Bitir(calistir: false);

    private void Bitir(bool calistir)
    {
        Grid.CommitEdit(); // düzenlenen son hücre bağlamaya işlensin
        Sonuc = Uret();
        CalistirIstendi = calistir;
        Kapat(true);
    }

    private void Iptal_Click(object sender, RoutedEventArgs e) => Kapat(false);

    /// <summary>Modal açıldıysa DialogResult ile kapanır; değilse (STA testi) düz Close.</summary>
    private void Kapat(bool sonuc)
    {
        try { DialogResult = sonuc; }
        catch (InvalidOperationException) { Close(); }
    }
}
