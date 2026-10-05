using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Motor → lehçe kayıt defteri (V3-S1 motor seçimi). Lehçeler durumsuz olduğundan
/// motor başına tek örnek yeterlidir; kayıt burada, seçim profildedir.
/// </summary>
public sealed class LehceSaglayici : ILehceSaglayici
{
    private readonly IReadOnlyDictionary<MotorTuru, ILehce> _lehceler;

    public LehceSaglayici(ISecretProtector protector) => _lehceler = new Dictionary<MotorTuru, ILehce>
    {
        [MotorTuru.Mssql] = new MssqlLehcesi(protector),
        [MotorTuru.Postgres] = new PostgresLehcesi(protector),
        [MotorTuru.MySql] = new MySqlLehcesi(protector),
        [MotorTuru.Oracle] = new OracleLehcesi(protector),
    };

    public ILehce Getir(MotorTuru motor) => _lehceler.TryGetValue(motor, out ILehce? lehce)
        ? lehce
        : throw new NotSupportedException($"Motor için lehçe kayıtlı değil: {motor}");
}

/// <summary>
/// Tek lehçeyi her motora veren adaptör — mevcut tek-motor kurucuları (testler dahil)
/// kırmadan sağlayıcı desenine köprü.
/// </summary>
public sealed class SabitLehceSaglayici : ILehceSaglayici
{
    private readonly ILehce _lehce;

    public SabitLehceSaglayici(ILehce lehce) => _lehce = lehce;

    public ILehce Getir(MotorTuru motor) => _lehce;
}
