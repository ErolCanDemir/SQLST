using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Görsel Sorgu Tasarımcısı'nda bir WHERE koşulu satırı (v6-S3): Kolon · Operatör · Değer +
/// bir öncekiyle AND/OR bağlacı. <see cref="KosulOperatoru.Bos"/>/<see cref="KosulOperatoru.DoluDegil"/>
/// değer istemez (<see cref="DegerGerekli"/> false → değer kutusu pasifleşir).
/// </summary>
public sealed partial class GorselKosulGorunumu : ObservableObject
{
    [ObservableProperty] private string? _kolon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DegerGerekli))]
    private KosulOperatoru _operator = KosulOperatoru.Esit;

    [ObservableProperty] private string _deger = "";

    /// <summary>Bir önceki koşula OR (true) mı AND (false) mı ile bağlanır (ilk koşulda yok sayılır).</summary>
    [ObservableProperty] private bool _veyaMi;

    /// <summary>Değer alanı gerekli mi — IS NULL / IS NOT NULL değer istemez.</summary>
    public bool DegerGerekli => Operator is not (KosulOperatoru.Bos or KosulOperatoru.DoluDegil);

    /// <summary>Üretime dahil edilecek kadar dolu mu (kolon seçili + gerekiyorsa değer var).</summary>
    public bool Dolu => !string.IsNullOrEmpty(Kolon) && (!DegerGerekli || Deger.Length > 0);

    /// <summary>Üretime hazır koşul; dolu değilse null.</summary>
    public GorselKosul? Yap(SemaNesnesi tablo)
        => Dolu ? new GorselKosul(tablo, Kolon!, Operator, Deger, VeyaMi) : null;
}
