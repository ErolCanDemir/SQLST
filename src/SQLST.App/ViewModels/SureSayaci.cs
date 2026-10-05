using System.Windows.Threading;

namespace SQLST.App.ViewModels;

/// <summary>
/// Saniyede bir tetiklenen küçük sayaç (v22-S4 saha turu-4 m.6). Süresiz koşan bir işin ekranda
/// "dondu mu, sürüyor mu" ayrımını yapabilmesi için: <c>using</c> kapsamı bitince durur.
///
/// Neden gerekli: log analizi bilerek süresiz çalışır (v20-S6, ⏹ Durdur ile kesilir). Kullanıcı
/// "analiz çalışamıyor" dediğinde ekranda değişmeyen tek bir "okunuyor…" satırı vardı; ne kadar
/// beklendiği, işin sürüp sürmediği görünmüyordu.
/// </summary>
internal sealed class SureSayaci : IDisposable
{
    private readonly DispatcherTimer _zamanlayici;

    private SureSayaci(DispatcherTimer zamanlayici) => _zamanlayici = zamanlayici;

    /// <summary>Saniyede bir <paramref name="tik"/> çağırır (UI iş parçacığında).</summary>
    public static SureSayaci Baslat(Action tik)
    {
        var zamanlayici = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        zamanlayici.Tick += (_, _) => tik();
        zamanlayici.Start();
        return new SureSayaci(zamanlayici);
    }

    public void Dispose() => _zamanlayici.Stop();
}
