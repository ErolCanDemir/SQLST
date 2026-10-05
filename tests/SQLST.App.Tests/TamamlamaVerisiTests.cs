using SQLST.App.Views;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// Otomatik tamamlama satırının tür etiketi (v6 sonrası UI iyileştirmesi 2026-07-20). Etiket
/// önceliğe göre kodlanır: 4=parça · 3=kolon(tip) · 2=nesne(Tür) · 1=anahtar. Görünüm (simge/renk/
/// hizalama) gözle doğrulanır; burada yalnız SAF etiket eşlemesi sabitlenir (WPF örneklemez).
/// </summary>
public class TamamlamaVerisiTests
{
    private static string Etiket(TamamlamaOnerisi o) => new TamamlamaVerisi(o).TurEtiketi;

    [Fact]
    public void Nesne_onerisinde_tur_aciklamanin_ilk_parcasi()
        => Assert.Equal("Tablo", Etiket(new TamamlamaOnerisi("Musteri", "Tablo — db.satis.Musteri", 2)));

    [Fact]
    public void Kolon_onerisinde_tip_bilgisi_gosterilir()
        => Assert.Equal("int · PK", Etiket(new TamamlamaOnerisi("Id", "int · PK", 3)));

    [Fact]
    public void Anahtar_sozcukte_sabit_etiket()
        => Assert.Equal("anahtar", Etiket(new TamamlamaOnerisi("SELECT", null, 1)));

    [Fact]
    public void Snippette_parca_etiketi()
    {
        var snippet = new Snippet(1, "sel", "SELECT iskeleti", "SELECT * FROM ", MotorTuru.Mssql, false);
        Assert.Equal("parça", Etiket(new TamamlamaOnerisi("sel", "SELECT iskeleti", 4, snippet)));
    }
}
