using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;

namespace SQLST.App.ViewModels;

/// <summary>Bir result set'in grid paketi: tablo görünümü + başlık + tip haritası (03 §3.5).</summary>
public sealed partial class SonucSetiGorunumu : ObservableObject
{
    public required SonucSeti Set { get; init; }
    public required string Baslik { get; init; }
    /// <summary>Tek result set'te başlık gizlenir; çokluda "Sonuç N (M satır)" görünür.</summary>
    public required bool BaslikGorunur { get; init; }

    /// <summary>Seçili hücrelerin özeti (V2-S6): COUNT/SUM/AVG/MIN/MAX — Excel durum çubuğu deseni.</summary>
    [ObservableProperty] private string _secimIstatistigi = "";

    public DataTable Tablo => Set.Tablo;
    public DataView Gorunum => Set.Tablo.DefaultView;
    public IReadOnlyDictionary<string, string> KolonTipleri => Set.KolonTipleri;

    // ---- 📊 Grafik paneli (v23-S24 S1) — set başına bir panel, YALNIZ kullanıcı açınca (K8) ----

    /// <summary>Panel görünür mü. Kendiliğinden hiç true olmaz; yalnız <see cref="GrafikAc"/> (düğme) ve yeniden
    /// çalıştırmada önceki set'ten taşıma (<see cref="GrafikDurumunuTasi"/>) açar.</summary>
    [ObservableProperty] private bool _grafikAcik;

    /// <summary>Panel VM'i — ilk açılışta kurulur, kapatınca kalır (tekrar açınca seçimler yerinde).</summary>
    [ObservableProperty] private GrafikPaneliViewModel? _grafik;

    /// <summary>Panel genişliği (px) — soldaki tutamaçla ayarlanır, yeniden çalıştırmada korunur.</summary>
    [ObservableProperty] private double _grafikGenisligi = 520;

    public void GrafikAc(GrafikAyari? ayar = null)
    {
        if (Grafik is null)
            Grafik = new GrafikPaneliViewModel(Tablo, ayar);
        else if (ayar is not null)
            Grafik.TabloyuDegistir(Tablo, ayar);
        GrafikAcik = true;
    }

    public void GrafikKapat() => GrafikAcik = false;

    /// <summary>Yeniden çalıştırma/oto-yenile: önceki set'te açık panel yeni set'te de açılır, aynı seçimlerle
    /// (kolonlar değiştiyse panel yeniden tahmin eder). Kapalıysa kapalı kalır — K8.</summary>
    public static void GrafikDurumunuTasi(IReadOnlyList<SonucSetiGorunumu> onceki, IReadOnlyList<SonucSetiGorunumu> yeni)
    {
        for (int i = 0; i < Math.Min(onceki.Count, yeni.Count); i++)
        {
            if (!onceki[i].GrafikAcik || onceki[i].Grafik is not { } g)
                continue;
            yeni[i].GrafikGenisligi = onceki[i].GrafikGenisligi;
            yeni[i].GrafikAc(g.Ayar);
        }
    }
}
