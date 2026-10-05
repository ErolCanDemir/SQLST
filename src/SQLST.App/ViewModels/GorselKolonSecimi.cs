using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Görsel Sorgu Tasarımcısı'nda kutu içindeki bir kolonun SELECT işareti (v6-S4). İşaretli
/// kolonlar SELECT listesine girer; hiçbiri işaretli değilse üretici <c>SELECT *</c> yazar.
/// Ayrıca kutunun kolon listesinin gösterimini (🔑/tip) de taşır.
/// </summary>
public sealed partial class GorselKolonSecimi : ObservableObject
{
    public GorselKolonSecimi(SemaKolonu kolon) => Kolon = kolon;

    public SemaKolonu Kolon { get; }
    public string Ad => Kolon.Ad;
    public string Gosterim => Kolon.Gosterim;

    [ObservableProperty] private bool _secili;
}
