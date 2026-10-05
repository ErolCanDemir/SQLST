using System.Windows;
using System.Windows.Threading;

namespace SQLST.App.Tests;

/// <summary>
/// STA pencere testlerinin PAYLAŞTIĞI tek dispatcher/Application (2026-07-25). WPF Application
/// AppDomain'de tektir ve bir dispatcher thread'ine bağlıdır; her test sınıfı kendi STA thread'ini
/// açarsa (a) Application ölü thread'e bağlanır, (b) xUnit sınıfları PARALEL koşunca iki sınıf aynı
/// anda Application kurmaya çalışıp "birden fazla örnek" hatası alır (tam süit koşusunda görüldü).
/// Tüm STA testleri buradan geçer: kurulum kilitli, işler Invoke ile sıralanır.
/// </summary>
internal static class StaOrtak
{
    private static Dispatcher? _sta;
    private static readonly object _kilit = new();

    public static Dispatcher Sta()
    {
        lock (_kilit)
        {
            if (_sta is null)
            {
                using var hazir = new ManualResetEventSlim();
                var t = new Thread(() =>
                {
                    System.Windows.Application app =
                        System.Windows.Application.Current ?? new System.Windows.Application();
                    // Pencere kapanınca Application KAPANMASIN — sonraki test aynı örneği kullanır.
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _sta = Dispatcher.CurrentDispatcher;
                    hazir.Set();
                    Dispatcher.Run();
                })
                { IsBackground = true };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                hazir.Wait();
            }
            return _sta!;
        }
    }

    /// <summary>Palet/Tema sözlüğünü Application kaynaklarına bir kez ekler (dispatcher thread'inde çağrılır).</summary>
    public static void Birlestir(string dosya)
    {
        System.Windows.Application app = System.Windows.Application.Current!;
        var uri = new Uri($"pack://application:,,,/SQLST.App;component/{dosya}", UriKind.Absolute);
        if (!app.Resources.MergedDictionaries.Any(d => d.Source == uri))
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }

    /// <summary>Dispatcher'ı belirtilen süre pompalar — Loaded/async void sürekliliklerini işler.</summary>
    public static void Pump(TimeSpan sure)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(sure, DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>
    /// <paramref name="kosul"/> sağlanana KADAR pompalar (en çok <paramref name="azami"/>).
    ///
    /// ⚠ NEDEN VAR (24 Ağu 2026): STA testleri arkalarındaki <c>Task.Run</c>/Loaded işini SABİT
    /// süreli <see cref="Pump"/> ile bekliyordu (300/600 ms). İzole koşuda yetiyor; TAM SÜİTTE
    /// makine meşgulken yetmiyor → grid kolonsuz kalıyor, satır görseli 0 çıkıyor ve test
    /// "süitte kırmızı, tek başına yeşil" oluyordu (iki ayrı testte gözlendi). Sabit süre
    /// makinenin o anki yüküne bahis oynamaktır; koşula bakmak deterministiktir.
    ///
    /// Koşul olarak ASSERT EDİLEN DEĞERİ değil, işin BİTTİĞİNİ gösteren sinyali verin
    /// (ör. "kolonlar kuruldu"), yoksa test kendi beklentisini bekleyip tautoloji olur.
    /// Süre dolarsa sessizce döner — asıl hatayı çağıranın assert'i versin (daha okunur mesaj).
    /// </summary>
    public static void PumpUntil(Func<bool> kosul, TimeSpan? azami = null)
    {
        TimeSpan sinir = azami ?? TimeSpan.FromSeconds(20);
        var kron = System.Diagnostics.Stopwatch.StartNew();
        while (kron.Elapsed < sinir && !kosul())
            Pump(TimeSpan.FromMilliseconds(50));
    }
}
