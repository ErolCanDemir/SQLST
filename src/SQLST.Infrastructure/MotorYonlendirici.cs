using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Aile yönlendiricileri (V3 Faz 3 — 08-v3r1 §3): üst sözleşme TEK, altta iki aile.
/// Profilin motoruna göre SQL ailesine (ADO.NET + ILehce) ya da Mongo ailesine gider;
/// çağıranlar (VM/servisler) aile ayrımını hiç görmez.
/// </summary>
public sealed class MotorYonlendiriciExecutor : ISqlExecutor
{
    private readonly ISqlExecutor _sql;
    private readonly ISqlExecutor _mongo;

    public MotorYonlendiriciExecutor(ISqlExecutor sql, ISqlExecutor mongo)
    {
        _sql = sql;
        _mongo = mongo;
    }

    private ISqlExecutor Sec(ConnectionProfile profil)
        => profil.Motor == MotorTuru.Mongo ? _mongo : _sql;

    public Task<QueryResult> ExecuteAsync(ConnectionProfile profil, string sql, ExecuteOptions opts, CancellationToken ct)
        => Sec(profil).ExecuteAsync(profil, sql, opts, ct);

    public Task<(bool Basarili, string? HataMesaji)> TestConnectionAsync(ConnectionProfile profil, CancellationToken ct)
        => Sec(profil).TestConnectionAsync(profil, ct);
}

public sealed class MotorYonlendiriciOturumFabrikasi : IOturumFabrikasi
{
    private readonly IOturumFabrikasi _sql;
    private readonly IOturumFabrikasi _mongo;

    public MotorYonlendiriciOturumFabrikasi(IOturumFabrikasi sql, IOturumFabrikasi mongo)
    {
        _sql = sql;
        _mongo = mongo;
    }

    public IDbOturum Olustur(ConnectionProfile profil)
        => (profil.Motor == MotorTuru.Mongo ? _mongo : _sql).Olustur(profil);
}

public sealed class MotorYonlendiriciSchemaService : ISchemaService
{
    private readonly ISchemaService _sql;
    private readonly ISchemaService _mongo;

    public MotorYonlendiriciSchemaService(ISchemaService sql, ISchemaService mongo)
    {
        _sql = sql;
        _mongo = mongo;
    }

    private ISchemaService Sec(ConnectionProfile profil)
        => profil.Motor == MotorTuru.Mongo ? _mongo : _sql;

    public Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct)
        => Sec(profil).VeritabanlariAsync(profil, ct);

    public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
        => Sec(profil).YukleAsync(profil, veritabani, ct);

    public Task<SemaOnbellegi> AdlariYukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
        => Sec(profil).AdlariYukleAsync(profil, veritabani, ct);

    public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
        => Sec(profil).TanimGetirAsync(profil, nesne, ct);

    public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(ConnectionProfile profil, SemaNesnesi tablo, CancellationToken ct)
        => Sec(profil).DuzenlemeMetaAsync(profil, tablo, ct);

    public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
        => Sec(profil).YabanciAnahtarlarAsync(profil, veritabani, ct);

    public Task<IReadOnlyList<Indeks>> IndekslerAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
        => Sec(profil).IndekslerAsync(profil, veritabani, ct);
}
