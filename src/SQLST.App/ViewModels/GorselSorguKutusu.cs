using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Görsel Sorgu Tasarımcısı'nda tuvale konmuş bir tablo kutusu. Şema nesnesini sarar;
/// <see cref="X"/>/<see cref="Y"/> tuvaldeki konumdur ve sürüklenince güncellenir.
///
/// <b>S3:</b> kutuya çift tık → bu tablonun WHERE koşulları (<see cref="Kosullar"/>).
/// Kolonlar hem gösterim hem koşul/kolon-seçimi için kullanılır.
/// </summary>
public sealed partial class GorselSorguKutusu : ObservableObject
{
    public GorselSorguKutusu(SemaNesnesi nesne, double x, double y)
    {
        Nesne = nesne;
        _x = x;
        _y = y;
        KolonSecimleri = [.. nesne.Kolonlar.Select(k => new GorselKolonSecimi(k))];
    }

    public SemaNesnesi Nesne { get; }

    public string Baslik => Nesne.TamAd;

    public IReadOnlyList<SemaKolonu> Kolonlar => Nesne.Kolonlar;

    /// <summary>SELECT işaretli kolonlar (v6-S4). Hiçbiri işaretli değilse üretici <c>SELECT *</c> yazar.</summary>
    public ObservableCollection<GorselKolonSecimi> KolonSecimleri { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    /// <summary>Bu tablonun WHERE koşulları (v6-S3) — çift tıkla açılan düzenleyicide girilir.</summary>
    public ObservableCollection<GorselKosulGorunumu> Kosullar { get; } = [];

    /// <summary>Kutuda gösterilen şart rozeti: "⧩ 2" (dolu koşul sayısı) — yoksa boş.</summary>
    public string KosulRozeti => KosulVar ? $"⧩ {Kosullar.Count(k => k.Dolu)}" : "";

    public bool KosulVar => Kosullar.Any(k => k.Dolu);

    /// <summary>Koşul düzenleyici kapanınca rozet/görünürlük tazelenir.</summary>
    public void KosullariTazele()
    {
        OnPropertyChanged(nameof(KosulRozeti));
        OnPropertyChanged(nameof(KosulVar));
    }
}
