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
}
