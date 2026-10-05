using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;

namespace SQLST.App.ViewModels;

/// <summary>
/// Görsel Sorgu WHERE filtre ÇİPİ (v11-öncesi #2, kullanıcı onayı 2026-07-25): tuvalin üstündeki
/// şeritte görünen tek koşul — "Tablo.Kolon op değer" + kaldır (✕) + bir öncekiyle VE/VEYA anahtarı.
/// Model hâlâ kutunun <see cref="GorselSorguKutusu.Kosullar"/> koleksiyonudur (SQL üretici değişmedi);
/// çip yalnız düzleştirilmiş GÖRÜNÜMdür. Eylemler sekme VM'ine delege edilir (tazeleme orada).
/// </summary>
public sealed partial class GorselFiltreCipi : ObservableObject
{
    private readonly GorselSorguSekmesiViewModel _sekme;

    public GorselFiltreCipi(
        GorselSorguSekmesiViewModel sekme, GorselSorguKutusu kutu, GorselKosulGorunumu kosul, bool ilkMi)
    {
        _sekme = sekme;
        Kutu = kutu;
        Kosul = kosul;
        IlkMi = ilkMi;
    }

    public GorselSorguKutusu Kutu { get; }
    public GorselKosulGorunumu Kosul { get; }

    /// <summary>Şeritteki İLK çip — bağlaç anahtarı gösterilmez (üretimde de yok sayılır).</summary>
    public bool IlkMi { get; }

    /// <summary>Çip metni: "dbo.Musteri.Ad içerir 'A'" gibi kısa okunur biçim.</summary>
    public string Metin
    {
        get
        {
            string op = Kosul.Operator switch
            {
                KosulOperatoru.Esit => "=",
                KosulOperatoru.Esitsiz => "≠",
                KosulOperatoru.Buyuk => ">",
                KosulOperatoru.Kucuk => "<",
                KosulOperatoru.BuyukEsit => "≥",
                KosulOperatoru.KucukEsit => "≤",
                KosulOperatoru.Icerir => "içerir",
                KosulOperatoru.Baslar => "ile başlar",
                KosulOperatoru.Biter => "ile biter",
                KosulOperatoru.Bos => "boş",
                KosulOperatoru.DoluDegil => "dolu",
                _ => "?",
            };
            string deger = Kosul.DegerGerekli ? $" {Kosul.Deger}" : "";
            return $"{Kutu.Nesne.TamAd}.{Kosul.Kolon} {op}{deger}";
        }
    }

    /// <summary>Bağlaç anahtarının etiketi (tıklanınca VE↔VEYA döner).</summary>
    public string Baglac => Kosul.VeyaMi ? "VEYA" : "VE";

    [RelayCommand]
    private void Sil() => _sekme.CipSil(this);

    [RelayCommand]
    private void BaglacDegistir() => _sekme.CipBaglacDegistir(this);
}
