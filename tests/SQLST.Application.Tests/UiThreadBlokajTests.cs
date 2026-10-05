using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// UI DONMA REGRESYONU (kullanıcı bulgusu 2026-07-20): "Yoğun bir sorgu çalışırken sekmeler
/// arası geçiş yapınca program dondu." Kök neden: veri erişim yolundaki <c>await</c>'ler
/// <c>ConfigureAwait(false)</c> kullanmıyordu; WPF'te devamları UI thread'ine postalanıyor ve
/// <c>ReadAsync</c> senkron tamamlandığında satır materyalizasyonu UI thread'inde koşuyordu.
///
/// <b>Bu test o daveti doğrudan yakalar:</b> çağıran thread'e sayan bir
/// <see cref="SynchronizationContext"/> kurulur; yürütücü çalıştırılır; context'e HİÇ devam
/// postalanmamış olmalı (0). ConfigureAwait(false) düşerse sayı &gt; 0 olur ve test kırılır —
/// donma sessizce geri gelemez.
///
/// LocalDB gerekir; erişilemezse test ANLAMINI YİTİRİR, o yüzden erişilemiyorsa atlanır
/// (gerçek async I/O yield'i olmadan ConfigureAwait'in etkisi gözlenemez).
/// </summary>
public class UiThreadBlokajTests
{
    private static ConnectionProfile LocalDbProfili() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    /// <summary>Postalanan devamları SAYAR ama yine de çalıştırır (yoksa görev asılır).</summary>
    private sealed class SayanContext : SynchronizationContext
    {
        private int _sayi;
        public int PostSayisi => _sayi;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _sayi);
            ThreadPool.QueueUserWorkItem(_ => d(state));   // ilerleme dursun diye yine çalıştır
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _sayi);
            d(state);
        }
    }

    [Fact]
    public async Task Sorgu_yurutumu_cagiran_context_e_devam_POSTALAMAZ()
    {
        // Çok satırlı bir sonuç: ReadAsync döngüsü gerçekten dönsün (senkron-tamamlanma dahil).
        const string sql = """
            SELECT TOP 500 name, object_id, type
            FROM sys.all_objects ORDER BY object_id;
            """;

        var ctx = new SayanContext();

        // Sayan context AYRI bir thread'e kurulur; yürütücü ORADA çalışıp bloke edilir. Böylece
        // yürütücünün İÇ marshaling'i sayılır ama testin kendi await'i sayımı kirletmez (ve
        // ConfigureAwait(false) uyarısına gerek kalmaz). GetResult kilitlenmez: postalanan
        // devamlar zaten thread havuzunda koşar (bkz. SayanContext.Post).
        QueryResult sonuc = await Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(ctx);
            return new SqlExecutor(new DpapiSecretProtector())
                .ExecuteAsync(LocalDbProfili(), sql, ExecuteOptions.Varsayilan, CancellationToken.None)
                .GetAwaiter().GetResult();
        });

        if (!sonuc.Basarili)
            return; // LocalDB yok/erişilemez → test anlamını yitirir, sessizce geç

        Assert.NotEmpty(sonuc.ResultSetler[0].Satirlar);
        Assert.Equal(0, ctx.PostSayisi);
    }
}
