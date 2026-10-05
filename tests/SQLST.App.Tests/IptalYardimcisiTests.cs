using System.Diagnostics;

namespace SQLST.App.Tests;

/// <summary>
/// v20-S21 saha m.15 (2026-08-14) — "Durdur butonları işe yaramıyor + pencere Not Responding":
/// <c>CancellationTokenSource.Cancel()</c> kayıtlı geri çağrıları ÇAĞIRAN thread'de senkron koşturur;
/// SqlClient'ın kaydı Attention paketini ağa senkron yazar → UI thread'inden çağrılan Cancel canlı
/// sunucuda pencereyi donduruyordu. <see cref="IptalYardimcisi"/> iptali havuza taşır — bu sınıf
/// çağıranın ASLA bloklanmadığını ve kenar durumların (bırakılmış CTS, fırlatan geri çağrı)
/// sessizce yutulduğunu sabitler.
/// </summary>
public class IptalYardimcisiTests
{
    [Fact]
    public void Bloklayan_geri_cagri_cagirani_bekletmez()
    {
        using var cts = new CancellationTokenSource();
        using var bitti = new ManualResetEventSlim();
        cts.Token.Register(() => { Thread.Sleep(1500); bitti.Set(); }); // SqlCommand.Cancel taklidi

        var kronometre = Stopwatch.StartNew();
        IptalYardimcisi.ArkaPlandaIptal(cts);
        kronometre.Stop();

        // Çağıran (UI thread'i temsilen) ANINDA dönmeli — geri çağrının 1,5 sn'sini beklememeli.
        Assert.True(kronometre.ElapsedMilliseconds < 500,
            $"Çağıran {kronometre.ElapsedMilliseconds} ms bekledi — iptal UI'yı bloklar demektir");

        Assert.True(bitti.Wait(TimeSpan.FromSeconds(5)), "İptal arka planda hiç koşmadı");
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Birakilmis_cts_sessiz_gecilir()
    {
        var cts = new CancellationTokenSource();
        cts.Dispose(); // koşu bitmiş, CTS bırakılmış senaryosu

        IptalYardimcisi.ArkaPlandaIptal(cts); // fırlatmamalı (ODE havuzda yutulur)
        await Task.Delay(300);                // havuz görevi koşsun — süreç ayakta kalmalı
    }

    [Fact]
    public void Null_cts_noop()
        => IptalYardimcisi.ArkaPlandaIptal(null);

    [Fact]
    public async Task Firlatan_geri_cagri_yutulur_iptal_yine_islenir()
    {
        using var cts = new CancellationTokenSource();
        cts.Token.Register(() => throw new InvalidOperationException("kasıtlı — geri çağrı patladı"));

        IptalYardimcisi.ArkaPlandaIptal(cts);

        // AggregateException havuzda yutulur (loglanır); iptal bayrağı yine kalkar.
        for (int i = 0; i < 50 && !cts.IsCancellationRequested; i++)
            await Task.Delay(100);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Birak_istenirse_iptalden_sonra_dispose_edilir()
    {
        var cts = new CancellationTokenSource();

        IptalYardimcisi.ArkaPlandaIptal(cts, birak: true);

        // Dispose arka planda, iptal TAMAMLANDIKTAN sonra gelir — Token erişimi ODE atmaya başlamalı.
        bool birakildi = false;
        for (int i = 0; i < 50 && !birakildi; i++)
        {
            try { _ = cts.Token; await Task.Delay(100); }
            catch (ObjectDisposedException) { birakildi = true; }
        }
        Assert.True(birakildi, "CTS arka planda Dispose edilmedi");
    }
}
