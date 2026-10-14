using System.Data;
using SQLST.App.ViewModels;
using SQLST.Application;

namespace SQLST.App.Tests;

/// <summary>📊 Grafik paneli VM'i (v23-S25 davranışları, pencereye taşınınca STA testinden ayrıldı): açıklamadan seri gizle
/// (renk kalıcı), yığılmış seçilebilirlik + eksen, tablo değişince ayar korunur.</summary>
public class GrafikPaneliVmTests
{
    private static DataTable Tablo(bool bursa = false)
    {
        var t = new DataTable();
        t.Columns.Add("Sehir", typeof(string)); t.Columns.Add("Yil", typeof(int)); t.Columns.Add("Tutar", typeof(int));
        t.Rows.Add("İstanbul", 2026, 485); t.Rows.Add("İstanbul", 2025, 420); t.Rows.Add("Ankara", 2026, 335); t.Rows.Add("Ankara", 2025, 310);
        if (bursa) t.Rows.Add("Bursa", 2026, 210);
        return t;
    }

    [Fact]
    public void Seri_gizle_yigilmis_tablo_degisince_ayar_korunur()
    {
        var g = new GrafikPaneliViewModel(Tablo());
        g.YSecimleri.First(y => y.Ad == "Yil").Secili = false;
        g.SeriKolon = "Yil";
        Assert.Equal(2, g.Model!.Seriler.Count);

        g.AciklamaDegistir("2026");
        Assert.Single(g.Model!.Seriler);
        Assert.Equal(1, g.Model.Seriler[0].Renk);
        Assert.True(g.Aciklama.Single(a => a.Ad == "2026").Gizli);
        g.AciklamaDegistir("2026");
        Assert.Equal(2, g.Model!.Seriler.Count);

        Assert.True(g.YigilmisSecilebilir);
        g.Yigilmis = true;
        Assert.Equal(905d, g.Model!.DegerAraligi().EnBuyuk);
        g.Tur = GrafikTuru.Cizgi;
        Assert.False(g.YigilmisSecilebilir);
        g.Tur = GrafikTuru.Cubuk;

        g.TabloyuDegistir(Tablo(bursa: true));
        Assert.Equal("Yil", g.SeriKolon);
        Assert.True(g.Yigilmis);
        Assert.Equal(["İstanbul", "Ankara", "Bursa"], g.Model!.Kategoriler);
    }
}
