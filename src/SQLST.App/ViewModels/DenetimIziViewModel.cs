using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Denetim izi (v10 A — "kim ne iş yapmış"): bu araçtan çalıştırılan sorguların geçmişi + kim + işlem
/// türü + süzgeç. PAYLAŞIMLI bileşen (v10-S4): SQL Server, diğer SQL motorları ve MongoDB yönetim
/// panellerinin hepsi bunu kullanır (tek DataTemplate). Yalnız client-side geçmiştir; motor-bağımsız.
/// </summary>
public sealed partial class DenetimIziViewModel(ISorguGecmisiDeposu gecmis) : ObservableObject
{
    private List<DenetimKaydiGorunumu> _tum = [];

    /// <summary>Süzgeçten geçmiş, gösterilen kayıtlar.</summary>
    public ObservableCollection<DenetimKaydiGorunumu> Kayitlar { get; } = [];

    /// <summary>Serbest metin süzgeci (kullanıcı/tür/db/SQL).</summary>
    [ObservableProperty] private string _suzgec = "";
    /// <summary>Yalnız yazma (INSERT/UPDATE/DELETE/DDL).</summary>
    [ObservableProperty] private bool _yalnizYazma;
    [ObservableProperty] private string _bilgi = "";

    /// <summary>Bu profilin geçmişini okuyup kim + işlem türüyle zenginleştirir, sonra süzer.</summary>
    public async Task YukleAsync(ConnectionProfile profil)
    {
        IReadOnlyList<GecmisKaydi> kayitlar =
            await gecmis.AraAsync(null, limit: 500, profilId: profil.Id, CancellationToken.None);
        _tum = [.. kayitlar.Select(k => new DenetimKaydiGorunumu(k, IslemSiniflayici.Sinifla(k.Sql, profil.Motor)))];
        Suz();
    }

    private void Suz()
    {
        Kayitlar.Clear();
        IEnumerable<DenetimKaydiGorunumu> kaynak = _tum;
        string q = Suzgec.Trim();
        if (q.Length > 0)
            kaynak = kaynak.Where(d => d.Eslesir(q));
        if (YalnizYazma)
            kaynak = kaynak.Where(d => d.Yazma);

        foreach (DenetimKaydiGorunumu d in kaynak)
            Kayitlar.Add(d);

        Bilgi = _tum.Count == 0
            ? "Bu profilde henüz kayıt yok (denetim izi bu araçtan yapılan sorguları gösterir)."
            : $"{Kayitlar.Count} / {_tum.Count} kayıt gösteriliyor.";
    }

    partial void OnSuzgecChanged(string value) => Suz();
    partial void OnYalnizYazmaChanged(bool value) => Suz();
}
