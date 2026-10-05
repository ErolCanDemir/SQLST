using System.Data.Common;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Sekme başına kalıcı bağlantı oturumu (V2-S1, FG-1.7). Bağlantıyı canlı tutar;
/// SET seçenekleri / #temp tablolar / kullanıcının açtığı transaction sekme boyunca korunur.
///
/// 07-r2 §2 kuralları uygulanır:
/// - Bağlantı HAVUZSUZ (Pooling=false) — açık transaction'lı bağlantı pool'a sızmaz.
/// - Komutlar SemaphoreSlim ile serileştirilir (bağlantı nesnesi thread-safe değil).
/// - Kapanışta açık transaction explicit ROLLBACK edilir (Dispose tek başına geri almaz).
///
/// V3-S1 / Faz 0: gövde soyut <see cref="DbConnection"/> ile çalışır; motora özgü
/// ifadeler (USE / XACT_STATE / ROLLBACK) <see cref="ILehce"/>'den gelir.
/// </summary>
public sealed class DbOturum : IDbOturum
{
    private readonly ILehce _lehce;
    private readonly SemaphoreSlim _kilit = new(1, 1);
    private DbConnection? _baglanti;
    private string? _aktifVeritabani;
    private bool _atildi;

    internal DbOturum(ConnectionProfile profil, ILehce lehce)
    {
        Profil = profil;
        _lehce = lehce;
    }

    public ConnectionProfile Profil { get; }

    public async Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_atildi, this);
        // ConfigureAwait(false): tüm DB işi UI thread'inden UZAKTA yürür (WPF donma düzeltmesi
        // — bkz. SonucOkuyucu). İlk gerçek I/O yield'inden sonra iş thread havuzunda kalır.
        await _kilit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DbConnection baglanti;
            try
            {
                baglanti = await BaglantiHazirlaAsync(opts.VeritabaniOverride, ct).ConfigureAwait(false);
                await VeritabaniSecAsync(baglanti, opts.VeritabaniOverride, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // İptal, sorgu okumadan önce (bağlantı açılışı/USE) tetiklendi → yine iptal sonucu.
                return new QueryResult { IptalEdildi = true, Mesajlar = ["Sorgu kullanıcı tarafından iptal edildi."] };
            }
            catch (DbException) when (ct.IsCancellationRequested)
            {
                return new QueryResult { IptalEdildi = true, Mesajlar = ["Sorgu kullanıcı tarafından iptal edildi."] };
            }
            catch (DbException ex)
            {
                return new QueryResult { Hata = _lehce.HataYorumla(ex) };
            }

            return await SonucOkuyucu.OkuAsync(baglanti, _lehce, sql, opts, Profil.KomutTimeoutSn, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _kilit.Release();
        }
    }

    public async Task<long> AkisAsync(
        string sql, ExecuteOptions opts,
        Action<IReadOnlyList<string>> baslikYaz, Action<object?[]> satirYaz, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_atildi, this);
        await _kilit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DbConnection baglanti = await BaglantiHazirlaAsync(opts.VeritabaniOverride, ct).ConfigureAwait(false);
            await VeritabaniSecAsync(baglanti, opts.VeritabaniOverride, ct).ConfigureAwait(false);
            return await SonucOkuyucu.AkisAsync(
                baglanti, _lehce, sql, Profil.KomutTimeoutSn, baslikYaz, satirYaz, ct).ConfigureAwait(false);
        }
        finally
        {
            _kilit.Release();
        }
    }

    public async Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_atildi, this);
        await _kilit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_baglanti is not { State: System.Data.ConnectionState.Open })
                return IslemDurumu.Yok;
            return await _lehce.IslemDurumuAsync(_baglanti, ct).ConfigureAwait(false);
        }
        finally
        {
            _kilit.Release();
        }
    }

    /// <summary>
    /// Açık bağlantıyı döndürür; yoksa/koptuysa açar.
    /// <paramref name="hedefVeritabani"/>: motor açık bağlantıda veritabanı değiştiremiyorsa
    /// (PostgreSQL — Faz 1 bulgusu) bağlantı doğrudan o veritabanına açılır; hedef değişmişse
    /// bağlantı tazelenir. MSSQL'de bu dal hiç çalışmaz, davranış v2 ile aynıdır.
    /// </summary>
    private async Task<DbConnection> BaglantiHazirlaAsync(string? hedefVeritabani, CancellationToken ct)
    {
        bool dbDegisti = !_lehce.AcikBaglantidaVeritabaniDegisir
            && !string.IsNullOrWhiteSpace(hedefVeritabani)
            && !string.Equals(hedefVeritabani, _aktifVeritabani, StringComparison.OrdinalIgnoreCase);

        if (_baglanti is { State: System.Data.ConnectionState.Open } && !dbDegisti)
            return _baglanti;

        if (_baglanti is not null)
            await _baglanti.DisposeAsync().ConfigureAwait(false); // koptu/bozuldu ya da hedef db değişti → yenile

        // Bağlantı dizesine gömülü veritabanı: yalnız USE desteklemeyen motorlarda anlamlı.
        string? dizedekiDb = _lehce.AcikBaglantidaVeritabaniDegisir ? null : hedefVeritabani;
        _aktifVeritabani = dizedekiDb;
        _baglanti = _lehce.BaglantiOlustur(Profil, dizedekiDb, havuz: false);
        await _baglanti.OpenAsync(ct).ConfigureAwait(false);
        return _baglanti;
    }

    /// <summary>Hedef veritabanı değiştiyse motorun kendi ifadesiyle geçer (açık ad kaçırılır).</summary>
    private async Task VeritabaniSecAsync(DbConnection baglanti, string? veritabani, CancellationToken ct)
    {
        if (!_lehce.AcikBaglantidaVeritabaniDegisir // bağlantı zaten hedef veritabanına açıldı
            || string.IsNullOrWhiteSpace(veritabani)
            || string.Equals(veritabani, _aktifVeritabani, StringComparison.OrdinalIgnoreCase))
            return;

        await using DbCommand komut = baglanti.CreateCommand();
        komut.CommandText = _lehce.VeritabaniSecSql(veritabani);
        await komut.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _aktifVeritabani = veritabani;
    }

    public async ValueTask DisposeAsync()
    {
        if (_atildi)
            return;
        _atildi = true;

        try
        {
            if (_baglanti is { State: System.Data.ConnectionState.Open } baglanti)
            {
                // 07-r2 §2: Dispose açık transaction'ı geri ALMAZ → explicit ROLLBACK.
                try
                {
                    if (await _lehce.IslemDurumuAsync(baglanti, CancellationToken.None).ConfigureAwait(false) != IslemDurumu.Yok)
                    {
                        await using DbCommand geriAl = baglanti.CreateCommand();
                        geriAl.CommandText = _lehce.GeriAlSql();
                        await geriAl.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
                catch (DbException) { /* kapanışta en iyi çaba — yut */ }
            }
        }
        finally
        {
            if (_baglanti is not null)
                await _baglanti.DisposeAsync().ConfigureAwait(false);
            _kilit.Dispose();
        }
    }
}

/// <summary>Profil başına kalıcı oturum üretir; lehçe profilin motorundan çözülür (V3-S1).</summary>
public sealed class OturumFabrikasi : IOturumFabrikasi
{
    private readonly ILehceSaglayici _lehceler;

    /// <summary>Varsayılan kurulum: SQL Server lehçesi (tek motor — testler/eski çağıranlar).</summary>
    public OturumFabrikasi(ISecretProtector protector) : this(new MssqlLehcesi(protector)) { }

    /// <summary>Tek lehçeli kurulum (testler).</summary>
    public OturumFabrikasi(ILehce lehce) : this(new SabitLehceSaglayici(lehce)) { }

    /// <summary>Çoklu motor kurulumu (V3-S1).</summary>
    public OturumFabrikasi(ILehceSaglayici lehceler) => _lehceler = lehceler;

    public IDbOturum Olustur(ConnectionProfile profil) => new DbOturum(profil, _lehceler.Getir(profil.Motor));
}
