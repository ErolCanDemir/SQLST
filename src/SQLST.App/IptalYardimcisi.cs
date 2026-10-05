namespace SQLST.App;

/// <summary>
/// ⏹ Durdur yollarının ORTAK iptali (v20-S21 saha m.15). <see cref="CancellationTokenSource.Cancel()"/>
/// kayıtlı geri çağrıları ÇAĞIRAN thread'de senkron koşturur; SqlClient'ın kaydı
/// <c>SqlCommand.Cancel</c>'a iner ve Attention paketini AĞA SENKRON yazar (bağlantı kilidini de
/// bekler). UI thread'inden çağrılınca canlı sunucuda büyük tarama sırasında pencere "Yanıt
/// vermiyor"a düşüyor, kullanıcı "Durdur çalışmıyor" görüyordu (MERSIS 15sn+ LIKE bulgusu —
/// testler yakalamadı çünkü <c>CancelAfter</c> timer thread'inde ateşler, UI'da değil).
/// Bu yardımcı iptali HAVUZ thread'ine taşır: tıklama anında döner, ağ maliyeti arka planda ödenir.
/// </summary>
internal static class IptalYardimcisi
{
    /// <param name="cts">İptal edilecek kaynak; null ise sessiz no-op.</param>
    /// <param name="birak">true → iptalden sonra CTS arka planda Dispose edilir. Çağıran alanını
    /// HEMEN null'lamalı/yenilemeli ve eski örneğe bir daha dokunmamalı (sahiplik havuza geçer).</param>
    public static void ArkaPlandaIptal(CancellationTokenSource? cts, bool birak = false)
    {
        if (cts is null)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Koşu bu arada bitmiş, CTS bırakılmış — iptal edilecek şey kalmadı.
            }
            catch (AggregateException ex)
            {
                Serilog.Log.Warning(ex, "İptal geri çağrısı istisna fırlattı");
            }
            finally
            {
                if (birak)
                    cts.Dispose(); // Dispose idempotenttir — yarışta ikinci çağrı zararsız
            }
        });
    }
}
