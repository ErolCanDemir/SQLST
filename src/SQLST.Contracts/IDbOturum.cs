namespace SQLST.Contracts;

/// <summary>Oturumdaki açık transaction durumu (SQL Server XACT_STATE karşılığı).</summary>
public enum IslemDurumu
{
    /// <summary>Açık transaction yok.</summary>
    Yok,
    /// <summary>Açık ve commit edilebilir (XACT_STATE = 1).</summary>
    Acik,
    /// <summary>Açık ama commit edilemez — yalnız ROLLBACK (XACT_STATE = -1, "doomed").</summary>
    Mahkum,
}

/// <summary>
/// Bir sekmenin kalıcı bağlantı oturumu (V2-S1, FG-1.7). Bağlantıyı canlı tutar —
/// böylece SET seçenekleri, #temp tablolar ve (V2-S4) açık transaction sekme yaşam
/// süresince korunur; her sorgu bağlantıyı yeniden açmaz.
///
/// Kritik (07-r2 §2): açık transaction'lı bağlantı asla pool'a döndürülmez; kapanışta
/// açık transaction explicit ROLLBACK edilir (Dispose tek başına geri almaz).
/// Komutlar oturum içinde serileştirilir (bağlantı nesnesi thread-safe değildir).
/// </summary>
public interface IDbOturum : IAsyncDisposable
{
    ConnectionProfile Profil { get; }

    /// <summary>Oturumun kalıcı bağlantısında sorguyu çalıştırır (bağlantı açık kalır).</summary>
    Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct);

    /// <summary>Sunucudaki açık transaction durumu (COMMIT öncesi kontrol için — V2-S4).</summary>
    Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default);

    /// <summary>
    /// Sorgunun İLK sonuç kümesini SATIR SATIR AKITIR — belleğe TÜMÜNÜ almadan (v6, "Tümünü
    /// dışa aktar"). 100.000'lik grid sınırı GÖRÜNTÜLEME içindir; toplu aktarma bu sınıra
    /// takılmadan, sabit bellekle çalışır. <paramref name="baslikYaz"/> kolon adlarıyla BİR KEZ,
    /// <paramref name="satirYaz"/> her satır için çağrılır; çağrı içi işleme SENKRON olmalı
    /// (dizi tampon olarak yeniden kullanılır). Dönen değer yazılan satır sayısıdır.
    ///
    /// Aynı oturumda koşar (izolasyon/#temp bağlantıda kalıcı — grid'le tutarlı). Satır SINIRI
    /// YOKTUR: reader sonuna kadar okunur.
    ///
    /// Varsayılan gövde "desteklenmiyor" fırlatır: gerçek oturumlar (SQL/Mongo) kendi
    /// uygulamalarıyla ezer; bu özelliği kullanmayan sade test sahteleri uygulamak zorunda
    /// kalmaz.
    /// </summary>
    Task<long> AkisAsync(
        string sql, ExecuteOptions opts,
        Action<IReadOnlyList<string>> baslikYaz, Action<object?[]> satirYaz, CancellationToken ct)
        => throw new NotSupportedException("Bu oturum toplu dışa aktarmayı desteklemiyor.");
}

/// <summary>Profil başına kalıcı oturum üretir (sekme kapanınca oturum Dispose edilir).</summary>
public interface IOturumFabrikasi
{
    IDbOturum Olustur(ConnectionProfile profil);
}
