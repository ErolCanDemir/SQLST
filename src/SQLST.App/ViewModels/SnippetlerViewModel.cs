using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Snippet yönetimi (V5-S4). Kod parçaları editörde kısayolla genişler; burada eklenir,
/// düzenlenir, silinir.
///
/// <b>Motor alanı zorunlu bir tasarım kararıdır:</b> "(her motorda)" seçeneği yalnız
/// gerçekten motordan bağımsız metinler içindir. T-SQL kalıbı her motorda işaretlenirse
/// Mongo sekmesinde önerilir ve kullanıcı seçtiğinde çalışmayan bir metin girer — çoklu
/// motor kuralının ihlali. Ekranda bu uyarı yazılıdır.
/// </summary>
public partial class SnippetlerViewModel : ObservableObject
{
    private readonly ISnippetDeposu _depo;
    private readonly MotorTuru? _aktifMotor;

    /// <param name="aktifMotor">
    /// Bağlı olunan motor (kullanıcı isteği 2026-07-19: <i>"kod parçaları da giriş yapılan
    /// motora göre listelensin"</i>). Liste bu motorun kalıpları + motorsuz ("her motorda")
    /// olanlarla sınırlanır — editördeki öneri listesiyle AYNI küme, böylece yönetim
    /// ekranında görünen şey ile editörde çıkan şey birbirini tutar.
    /// <c>null</c> = bağlantı yok → tümü listelenir (yönetim yine de yapılabilsin).
    /// </param>
    public SnippetlerViewModel(ISnippetDeposu depo, MotorTuru? aktifMotor = null)
    {
        _depo = depo;
        _aktifMotor = aktifMotor;
        _motor = aktifMotor;      // yeni kalıp varsayılan olarak bu motora yazılır
        Motorlar = MotorSecenekleriKur(aktifMotor);
    }

    public ObservableCollection<Snippet> Snippetler { get; } = [];

    /// <summary>
    /// Listenin neyle sınırlı olduğu ekranda YAZAR — kullanıcı "kaydettiğim kalıp nerede?"
    /// diye aramasın. (Başka motorun kalıbı gizlenmiştir, silinmemiştir.)
    /// </summary>
    public string Kapsam => _aktifMotor is { } motor
        ? $"Gösterilen: {motor} kalıpları + \"(her motorda)\" işaretli olanlar. "
          + "Başka motorların kalıpları gizli — o motora bağlanınca görünür. "
          + $"Yeni kalıp yalnız {motor} ya da \"(her motorda)\" olarak kaydedilebilir."
        : "Bağlantı yok — TÜM kalıplar listeleniyor.";

    /// <summary>
    /// ComboBox öğesi. <b>Sarmalayıcı tip ŞART:</b> WPF ComboBox <c>null</c> bir öğeyi
    /// SEÇİLİ TUTAMAZ — seçimi "boş" sayıp temizler, kullanıcı "(her motorda)"yı seçemez
    /// (kullanıcı bulgusu 2026-07-19). Bağlantı ekranı da aynı nedenle
    /// <c>MotorSecenegi</c> kullanıyor.
    /// </summary>
    public sealed record MotorSecimi(MotorTuru? Deger, string Ad)
    {
        public override string ToString() => Ad;
    }

    /// <summary>
    /// Motor seçenekleri. <b>Yalnız BAĞLI OLUNAN motor ve "(her motorda)" sunulur</b>
    /// (kullanıcı isteği 2026-07-19: <i>"SQL ile bağlandıysam ya her motor ya da SQL'i
    /// seçebileyim, Mongo ve diğer motorlara kod parçası kaydedemeyeyim"</i>).
    ///
    /// Gerekçe: liste zaten bağlı motorun kalıplarını gösteriyor. Başka bir motora kayıt
    /// yapılabilseydi kalıp kaydedildiği anda listeden kaybolurdu — kullanıcı silindi
    /// sanardı. Seçeneği hiç sunmamak, kaydettikten sonra uyarı göstermekten iyidir.
    ///
    /// Bağlantı yokken tümü sunulur: yönetim o hâlde de yapılabilmeli.
    /// </summary>
    public IReadOnlyList<MotorSecimi> Motorlar { get; }

    private static IReadOnlyList<MotorSecimi> MotorSecenekleriKur(MotorTuru? aktif)
    {
        var liste = new List<MotorSecimi> { new(null, "(her motorda)") };
        if (aktif is { } motor)
            liste.Add(new MotorSecimi(motor, motor.ToString()));
        else
            liste.AddRange(Enum.GetValues<MotorTuru>().Select(m => new MotorSecimi(m, m.ToString())));
        return liste;
    }

    /// <summary>
    /// ComboBox'ın bağlandığı seçim — ASLA null olmaz, böylece "(her motorda)" da seçilebilir.
    /// <see cref="Motor"/> tek doğruluk kaynağı olarak kalır; bu yalnız görünüm köprüsüdür.
    /// </summary>
    public MotorSecimi SeciliMotorSecimi
    {
        get => Motorlar.FirstOrDefault(m => m.Deger == Motor) ?? Motorlar[0];
        set => Motor = value?.Deger;
    }

    [ObservableProperty] private Snippet? _secili;
    [ObservableProperty] private string _kisayol = "";
    [ObservableProperty] private string _baslik = "";
    [ObservableProperty] private string _govde = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SeciliMotorSecimi))] private MotorTuru? _motor;
    [ObservableProperty] private string _bilgi = "";

    /// <summary>Liste değiştiğinde ana pencere snippet önbelleğini tazelesin.</summary>
    public Func<Task>? DegistiBildir { get; set; }

    partial void OnSeciliChanged(Snippet? value)
    {
        if (value is null)
            return;

        Kisayol = value.Kisayol;
        Baslik = value.Baslik;
        Govde = value.Govde;
        Motor = value.Motor;
    }

    public async Task YukleAsync()
    {
        Snippetler.Clear();

        // Bağlıysak O MOTORUN kümesi (editördeki öneri listesiyle aynı); bağlantı yoksa tümü.
        IReadOnlyList<Snippet> liste = _aktifMotor is { } motor
            ? await _depo.ListeleAsync(motor)
            : await _depo.TumunuListeleAsync();

        foreach (Snippet s in liste)
            Snippetler.Add(s);
    }

    [RelayCommand]
    private void Yeni()
    {
        Secili = null;
        Kisayol = "";
        Baslik = "";
        Govde = "";
        Motor = _aktifMotor;      // varsayılan: bağlı olunan motor (liste zaten onu gösteriyor)
        Bilgi = "Yeni kalıp: kısayol, başlık ve gövdeyi doldurup Kaydet'e basın. "
              + $"Gövdedeki {Snippet.ImlecIsareti} imlecin nereye gideceğini belirler.";
    }

    [RelayCommand]
    private async Task KaydetAsync()
    {
        if (string.IsNullOrWhiteSpace(Kisayol) || string.IsNullOrWhiteSpace(Govde))
        {
            Bilgi = "Kısayol ve gövde boş olamaz.";
            return;
        }

        try
        {
            if (Secili is { } mevcut)
            {
                await _depo.GuncelleAsync(mevcut with
                {
                    Kisayol = Kisayol.Trim(),
                    Baslik = Baslik.Trim(),
                    Govde = Govde,
                    Motor = Motor,
                });
                Bilgi = $"'{Kisayol}' güncellendi.";
            }
            else
            {
                await _depo.EkleAsync(new Snippet(
                    0, Kisayol.Trim(), Baslik.Trim(), Govde, Motor, Yerlesik: false));
                Bilgi = $"'{Kisayol}' eklendi.";
            }

            await YukleAsync();
            if (DegistiBildir is { } bildir)
                await bildir();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // Tekil index: aynı motorda aynı kısayol iki kez olamaz. Kullanıcıya ham
            // SQLite metni yerine ne yapması gerektiği söylenir.
            Bilgi = ex.SqliteErrorCode == 19
                ? $"'{Kisayol}' bu motorda zaten var — başka bir kısayol seçin."
                : $"Kaydedilemedi: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SilAsync()
    {
        if (Secili is not { } secili)
        {
            Bilgi = "Önce listeden bir kalıp seçin.";
            return;
        }

        await _depo.SilAsync(secili.Id);
        Bilgi = $"'{secili.Kisayol}' silindi.";
        Yeni();
        await YukleAsync();
        if (DegistiBildir is { } bildir)
            await bildir();
    }
}
