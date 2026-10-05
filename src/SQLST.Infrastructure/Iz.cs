using System.Diagnostics;

namespace SQLST.Infrastructure;

/// <summary>
/// 🧭 FAZ İZİ — çökmeden sağ çıkan tek satırlık kayıt (v22-S4, çok ajanlı çökme denetimi).
///
/// NEDEN SERİLOG DEĞİL: dört turdur çökmenin nerede olduğunu bilmiyoruz. Denetim, uçuş
/// kaydedicisinin sahada yalnız <b>dört</b> markörü olduğunu gösterdi (sorgu başladı · okuma bitti ·
/// grid hazır · hata) ve aralarındaki her şey — okuma döngüsü, tablo kurulumu, WPF kolon üretimi,
/// measure/arrange — <b>işaretsiz</b>. Dahası uygulamanın TEMİZ kapanışı ile SERT ölümü logda
/// birebir aynı görünüyor: iki durumda da dosya "Grid hazır" ile bitiyor.
///
/// Serilog bu işi göremez, çünkü:
/// <list type="bullet">
///   <item>OOM anında loglamanın kendisi tahsis ister (mesaj şablonu, kuyruk, tampon);</item>
///   <item>sert ölümde (StackOverflow / native / FailFast) süreç hiçbir yönetilen kod çalıştıramaz.</item>
/// </list>
/// Buradaki yazım BİLEREK ilkel: sabit dosya adı, üzerine yaz, tek satır, ek tahsis yok. Dosya
/// büyümez; çökme sonrası SON YAZILAN faz orada durur.
///
/// Okuma: <c>%APPDATA%\SQLST\logs\faz.txt</c>. Son satır "temiz-cikis" ise o oturum ÇÖKME DEĞİLDİR.
/// </summary>
public static class Iz
{
    private static readonly object Kilit = new();
    private static string? _yol;

    /// <summary>Faz izinin dosya yolu (tanılama ekranı/paket betiği bunu gösterir).</summary>
    public static string Yol => _yol ??= Path.Combine(UygulamaVeriYolu.GunlukKlasoru, "faz.txt");

    /// <summary>
    /// Bulunulan fazı KAYDEDER (öncekinin üzerine). <paramref name="ek"/> faz içi sayaç içindir
    /// (ör. okunan satır). Hata YUTULUR: iz bir kolaylıktır, uygulamayı asla düşürmez.
    /// </summary>
    public static void Faz(string ad, long ek = -1)
    {
        try
        {
            // GC.GetTotalMemory(false) koleksiyon TETİKLEMEZ; WorkingSet64 yönetilmeyen büyümeyi de
            // gören tek ucuz ölçüdür (WPF render/native sızıntısı yalnız orada görünür).
            long yigin = GC.GetTotalMemory(false) >> 20;
            long ozel = Environment.WorkingSet >> 20;
            string satir = $"{DateTime.Now:HH:mm:ss.fff} pid={Environment.ProcessId} {ad}"
                + (ek >= 0 ? $" n={ek}" : "")
                + $" yigin={yigin}MB ozel={ozel}MB gen2={GC.CollectionCount(2)}";
            lock (Kilit)
                File.WriteAllText(Yol, satir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                    or System.Security.SecurityException)
        {
            // izlenebilirlik uğruna uygulama riske atılmaz
        }
    }

    /// <summary>
    /// Temiz kapanış damgası — bu satır varsa oturum çökmemiştir. Çökme ile normal çıkışın logda
    /// ayırt edilememesi, denetimin bulduğu en can sıkıcı körlüktü.
    /// </summary>
    public static void TemizCikis() => Faz("temiz-cikis");

    /// <summary>
    /// 💓 DONMA GÖZLEMCİSİ — nöbetçi izlediği thread'in ÜSTÜNDE yaşamamalı.
    ///
    /// Mevcut donma nöbetçisi <c>DispatcherTimer</c> ile UI thread'inde koşuyor: arayüz bloke
    /// olduğunda kuyruk pompalanmadığı için tik ATMAZ. Yani "donma uyarısı yok" ifadesi
    /// "donma yoktu" demek değil — tam da donma anında susuyor. Burada gözlemci havuz thread'inde:
    /// UI yalnız kalbi damgalar, gözlem dışarıdan yapılır.
    /// </summary>
    public sealed class DonmaGozlemcisi : IDisposable
    {
        private readonly Timer _zamanlayici;
        private long _kalp = Environment.TickCount64;
        private readonly int _esikMs;

        public DonmaGozlemcisi(int esikMs = 2_000)
        {
            _esikMs = esikMs;
            _zamanlayici = new Timer(_ => Bak(), null, 1_000, 1_000);
        }

        /// <summary>UI thread'inden çağrılır — tek yazım, kilit yok.</summary>
        public void Kalp() => Volatile.Write(ref _kalp, Environment.TickCount64);

        private void Bak()
        {
            long gecen = Environment.TickCount64 - Volatile.Read(ref _kalp);
            if (gecen < _esikMs)
                return;
            try
            {
                using Process p = Process.GetCurrentProcess();
                File.WriteAllText(
                    Path.Combine(UygulamaVeriYolu.GunlukKlasoru, "donma.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} UI {gecen / 1000.0:F1} sn blokta · "
                    + $"yigin={GC.GetTotalMemory(false) >> 20}MB · "
                    + $"ozel={p.PrivateMemorySize64 >> 20}MB · gen2={GC.CollectionCount(2)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or InvalidOperationException)
            {
                // gözlem uğruna uygulama riske atılmaz
            }
        }

        public void Dispose() => _zamanlayici.Dispose();
    }
}
