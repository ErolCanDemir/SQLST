using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Execution plan sekmesi (V5-S1). Salt görüntüleyicidir: planı ALMAK sorgu sekmesinin işi
/// (oturum orada), bu sınıf yalnız çözümlenmiş <see cref="SorguPlani"/>'yi gösterir.
///
/// Kapsam notu (kullanıcı, 2026-07-18): <b>Yönetim Paneli'nden ayrıdır.</b> Panel sunucu
/// genelini DMV'lerden inceler; bu sekme ŞU sorgunun planını gösterir. Tek temas noktası
/// eksik index önerisinin script'ini sorgu sekmesinde açmaktır — panel değişmez.
/// </summary>
public partial class PlanSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly SorguPlani _plan;

    public PlanSekmesiViewModel(SorguPlani plan, string sekmeAdi)
    {
        _plan = plan;
        SekmeAdi = sekmeAdi;

        foreach (IfadePlani ifade in plan.Ifadeler)
        {
            // Komut ifade görünümünde durur: ItemsControl içinde DataContext eksik index
            // olduğundan, komuta ItemsControl'ün DataContext'i üzerinden erişilir.
            Ifadeler.Add(new PlanIfadeGorunumu(ifade) { ScriptiAc = s => ScriptiSekmedeAc?.Invoke(s) });
        }

        SeciliIfade = Ifadeler.FirstOrDefault();
    }

    public string SekmeAdi { get; }

    /// <summary>Çözümlenmiş plan — 🤖 Asistan "Planı yorumlat" (v11-S5b) metinleştirmek için okur.</summary>
    public SorguPlani Plan => _plan;

    public string Baslik => _plan.Gercek
        ? $"📊 Execution plan · {SekmeAdi}"
        : $"📈 Tahmini execution plan · {SekmeAdi}";

    public SekmeDurumu Durum => SekmeDurumu.Tamamlandi;

    /// <summary>
    /// Planın nasıl alındığını dürüstçe söyler — kullanıcı neye baktığını bilmeli.
    /// Tahmin ÜRETMEYEN motorda (MongoDB) "tahminle karşılaştırabilirsiniz" demek yanlıştı:
    /// karşılaştıracak tahmin yok (A1/B1 bulgusu, 2026-07-19).
    ///
    /// Planın tahminî olmasının İKİ sebebi vardır ve ayırt edilir (2026-07-20): motor
    /// ölçümlü plan veremiyor olabilir, ya da ifade yazma/DDL olduğu için araç sorguyu
    /// bilerek çalıştırmamıştır. MSSQL'de ikincisine "bu motor ölçümlü plan vermiyor"
    /// demek düpedüz yanlış olurdu — SQL Server verir, biz istemedik.
    /// </summary>
    public string Aciklama => _plan.YazmaOlduguIcinCalistirilmadi
        ? "Bu bir YAZMA/DDL ifadesi olduğu için sorgu ÇALIŞTIRILMADI — yalnız derlendi ve "
          + "veri DEĞİŞMEDİ. Satır sayıları optimizer TAHMİNİDİR. Ölçümlü plan yalnız okuma "
          + "sorgularında alınır: onu elde etmenin tek yolu sorguyu gerçekten çalıştırmaktır."
        : !_plan.Gercek
        ? "Bu motor ölçümlü plan vermiyor — sorgu ÇALIŞTIRILMADI, yalnız derlendi. "
          + "Satır sayıları optimizer TAHMİNİDİR."
        : TahminUretiliyor
            ? "Sorgu çalıştırıldı; satır sayıları ÖLÇÜLDÜ ve optimizer'ın tahminiyle karşılaştırılabilir."
            : "Sorgu çalıştırıldı; satır sayıları ÖLÇÜLDÜ. Bu motorun kardinalite TAHMİNİ "
              + "yoktur, bu yüzden tahmin/gerçek karşılaştırması yapılmaz — bakılacak sayı "
              + "'incelenen belge' sayısıdır.";

    /// <summary>Motor kardinalite tahmini üretiyor mu (Mongo üretmez).</summary>
    private bool TahminUretiliyor => _plan.TumDugumler.Any(d => d.TahminVar);

    public bool GercekMi => _plan.Gercek;

    public ObservableCollection<PlanIfadeGorunumu> Ifadeler { get; } = [];

    [ObservableProperty] private PlanIfadeGorunumu? _seciliIfade;

    // Uyarilar/Sapmalar bir kez hesaplanır. Önce hesaplanan özelliklerdi ve her binding
    // erişiminde ağaç baştan dolaşılıyordu (UyariVar → Uyarilar → tam dolaşım); XAML dördünü
    // birden bağladığı için tek plan açılışında ~6 dolaşım oluyordu. Plan ağacı küçük olduğu
    // için ölçülebilir bir yavaşlık değildi ama bedava düzeltme (A1/B1, 2026-07-19).
    private IReadOnlyList<string>? _uyarilar;
    private IReadOnlyList<string>? _sapmalar;

    /// <summary>Plandaki tüm uyarılar tek yerde — kullanıcı ağacı tarayarak aramasın.</summary>
    public IReadOnlyList<string> Uyarilar => _uyarilar ??=
        [.. _plan.TumDugumler.SelectMany(d => d.Uyarilar.Select(u => $"{d.Islem}: {u}")).Distinct()];

    public bool UyariVar => Uyarilar.Count > 0;

    /// <summary>Tahmin/gerçek sapması olan operatörler — planın en işe yarar sinyali.</summary>
    public IReadOnlyList<string> Sapmalar => _sapmalar ??=
        [.. _plan.TumDugumler.Where(d => d.SapmaVar).Select(d =>
            $"{d.Islem}{(d.Ayrinti is null ? "" : $" ({d.Ayrinti})")}: tahmin {d.TahminiSatir:N0}, gerçek {d.GercekSatir:N0}")];

    public bool SapmaVar => Sapmalar.Count > 0;

    /// <summary>View bağlar: eksik index script'ini YENİ SORGU SEKMESİNDE açar (asla çalıştırmaz).</summary>
    public Action<string>? ScriptiSekmedeAc { get; set; }

    public Task KapatAsync() => Task.CompletedTask;   // kalıcı kaynak tutmaz
}

/// <summary>Tek bir ifadenin planı — batch birden çok ifade içerebilir.</summary>
public sealed partial class PlanIfadeGorunumu(IfadePlani ifade) : ObservableObject
{
    /// <summary>Plan sekmesi bağlar: script'i yeni sorgu sekmesinde açar.</summary>
    public Action<string>? ScriptiAc { get; init; }

    [RelayCommand]
    private void EksikIndexAc(PlanEksikIndexi? oneri)
    {
        if (oneri is not null)
            ScriptiAc?.Invoke(oneri.Script());
    }

    public string Baslik => Kisalt(ifade.IfadeMetni);

    public string ToplamMaliyet => $"maliyet {ifade.ToplamMaliyet:N4}";

    public IReadOnlyList<PlanDugumu> Kokler => ifade.Kok is null ? [] : [ifade.Kok];

    public IReadOnlyList<PlanEksikIndexi> EksikIndexler => ifade.EksikIndexler;

    public bool EksikIndexVar => EksikIndexler.Count > 0;

    private static string Kisalt(string metin)
    {
        string tek = string.Join(' ', metin.Split('\n', '\r').Select(s => s.Trim()).Where(s => s.Length > 0));
        return tek.Length <= 90 ? tek : tek[..90] + "…";
    }
}
