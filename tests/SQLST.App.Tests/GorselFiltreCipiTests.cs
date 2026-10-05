using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// WHERE filtre çipleri (v11-öncesi #2, 2026-07-25): huni popover'ının düştüğü KosulEkle yolu,
/// çip şeridi (sıra + ilk-çip bayrağı + metin), ✕ ile silme, VE↔VEYA anahtarı ve koşulların
/// SQL üretimine yansıması. Popover'ın kendisi görsel (kod-üretimli Popup) — burada VM sözleşmesi test edilir.
/// </summary>
public class GorselFiltreCipiTests
{
    private static readonly SemaNesnesi Musteri = new(
        "Db", "dbo", "Musteri", SemaNesneTuru.Tablo,
        [new("Id", "int", false, true), new("Ad", "nvarchar(100)", true, false),
         new("Aktif", "bit", false, false), new("Kayit", "datetime2", false, false)],
        []);

    private static GorselSorguSekmesiViewModel Vm()
        => new(() => new MssqlLehcesi(new DpapiSecretProtector()));

    [Fact]
    public void Kosul_ekle_cip_uretir_rozet_ve_sql_yansir()
    {
        GorselSorguSekmesiViewModel vm = Vm();
        vm.TabloEkle(Musteri, 0, 0);
        GorselSorguKutusu kutu = vm.Kutular[0];

        vm.KosulEkle(kutu, "Ad", KosulOperatoru.Icerir, "veli");
        vm.KosulEkle(kutu, "Aktif", KosulOperatoru.Esit, "1");

        Assert.Equal(2, vm.Cipler.Count);
        Assert.True(vm.CiplerVar);
        Assert.True(vm.Cipler[0].IlkMi);
        Assert.False(vm.Cipler[1].IlkMi);
        Assert.Equal("dbo.Musteri.Ad içerir veli", vm.Cipler[0].Metin);
        Assert.True(kutu.KosulVar); // kutu rozeti de dolar

        string? sql = vm.ScriptiUret();
        Assert.NotNull(sql);
        Assert.Contains("WHERE", sql!);
        Assert.Contains("LIKE", sql!);   // içerir → LIKE
        Assert.Contains("Aktif", sql!);
    }

    [Fact]
    public void Cip_sil_kosulu_kutudan_dusurur()
    {
        GorselSorguSekmesiViewModel vm = Vm();
        vm.TabloEkle(Musteri, 0, 0);
        vm.KosulEkle(vm.Kutular[0], "Ad", KosulOperatoru.Esit, "x");

        vm.CipSil(vm.Cipler[0]);

        Assert.Empty(vm.Cipler);
        Assert.False(vm.CiplerVar);
        Assert.Empty(vm.Kutular[0].Kosullar);
        Assert.False(vm.Kutular[0].KosulVar);
    }

    [Fact]
    public void Baglac_anahtari_ve_veya_dondurur()
    {
        GorselSorguSekmesiViewModel vm = Vm();
        vm.TabloEkle(Musteri, 0, 0);
        vm.KosulEkle(vm.Kutular[0], "Ad", KosulOperatoru.Esit, "x");
        vm.KosulEkle(vm.Kutular[0], "Aktif", KosulOperatoru.Esit, "1");

        Assert.Equal("VE", vm.Cipler[1].Baglac);
        vm.CipBaglacDegistir(vm.Cipler[1]);
        Assert.Equal("VEYA", vm.Cipler[1].Baglac);

        string? sql = vm.ScriptiUret();
        Assert.NotNull(sql);
        Assert.Contains("OR", sql!);
    }

    [Fact]
    public void Deger_istemeyen_operator_cip_metninde_deger_gostermez()
    {
        GorselSorguSekmesiViewModel vm = Vm();
        vm.TabloEkle(Musteri, 0, 0);
        vm.KosulEkle(vm.Kutular[0], "Ad", KosulOperatoru.Bos, "");

        Assert.Equal("dbo.Musteri.Ad boş", vm.Cipler[0].Metin);
        string? sql = vm.ScriptiUret();
        Assert.Contains("IS NULL", sql!);
    }

    [Fact]
    public void Kutu_silinince_cipleri_de_gider()
    {
        GorselSorguSekmesiViewModel vm = Vm();
        vm.TabloEkle(Musteri, 0, 0);
        vm.KosulEkle(vm.Kutular[0], "Ad", KosulOperatoru.Esit, "x");
        Assert.Single(vm.Cipler);

        vm.KutuSil(vm.Kutular[0]);
        Assert.Empty(vm.Cipler);
    }
}
