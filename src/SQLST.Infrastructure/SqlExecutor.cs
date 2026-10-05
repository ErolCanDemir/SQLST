using System.Data.Common;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Stateless (tek-atış) yürütücü (02-mimari §4.1): şema okuma, tanım getirme, bağlantı
/// testi gibi kısa işler için havuzlu bağlantı açar-kapatır. Sekme sorguları ise kalıcı
/// <see cref="DbOturum"/> kullanır (V2-S1).
///
/// V3-S1 / Faz 0: bağlantı üretimi ve istisna yorumu <see cref="ILehce"/>'ye devredildi;
/// gövde soyut <see cref="DbConnection"/> ile çalışır (motor-nötr).
/// </summary>
public sealed class SqlExecutor : ISqlExecutor
{
    private readonly ILehceSaglayici _lehceler;

    /// <summary>Varsayılan kurulum: SQL Server lehçesi (tek motor — testler/eski çağıranlar).</summary>
    public SqlExecutor(ISecretProtector protector) : this(new MssqlLehcesi(protector)) { }

    /// <summary>Tek lehçeli kurulum (testler): her profil bu lehçeyle çalışır.</summary>
    public SqlExecutor(ILehce lehce) : this(new SabitLehceSaglayici(lehce)) { }

    /// <summary>Çoklu motor kurulumu (V3-S1): lehçe her çağrıda profilin motorundan çözülür.</summary>
    public SqlExecutor(ILehceSaglayici lehceler) => _lehceler = lehceler;

    public async Task<QueryResult> ExecuteAsync(
        ConnectionProfile profil, string sql, ExecuteOptions opts, CancellationToken ct)
    {
        ILehce lehce = _lehceler.Getir(profil.Motor);
        DbConnection? baglanti = null;
        try
        {
            baglanti = lehce.BaglantiOlustur(profil, opts.VeritabaniOverride, havuz: true);
            // ConfigureAwait(false): şema/panel okumaları da UI thread'ini bloke etmesin
            // (WPF donma düzeltmesi — bkz. SonucOkuyucu).
            await baglanti.OpenAsync(ct).ConfigureAwait(false);
            return await SonucOkuyucu.OkuAsync(baglanti, lehce, sql, opts, profil.KomutTimeoutSn, ct)
                .ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // Bağlantı açılışında (OpenAsync) oluşan hata — okuma katmanına ulaşmadan.
            return new QueryResult { Hata = lehce.HataYorumla(ex) };
        }
        finally
        {
            if (baglanti is not null)
                await baglanti.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<(bool Basarili, string? HataMesaji)> TestConnectionAsync(
        ConnectionProfile profil, CancellationToken ct)
    {
        try
        {
            ILehce lehce = _lehceler.Getir(profil.Motor);
            await using DbConnection baglanti = lehce.BaglantiOlustur(profil, veritabaniOverride: null, havuz: true);
            await baglanti.OpenAsync(ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return (false, ex.Message);
        }
    }
}
