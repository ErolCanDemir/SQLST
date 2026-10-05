using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// Çalıştırılan her sorguyu kaydeden sahte executor. ViewModel testlerinin çoğu
/// "ne döndü" değil <b>"sunucuya NE ve HANGİ KAPSAMDA gitti"</b> sorusunu sorar —
/// bayatlama hataları tam orada görünür.
/// </summary>
public sealed class SahteExecutor : ISqlExecutor
{
    public sealed record Cagri(ConnectionProfile Profil, string Sql, ExecuteOptions Secenekler);

    public List<Cagri> Cagrilar { get; } = [];

    /// <summary>Sıradaki çağrının döneceği sonuç; verilmezse boş başarılı sonuç.</summary>
    public QueryResult? Sonuc { get; set; }

    /// <summary>Doluysa çağrı bu kadar bekler — iptal/durum testleri için.</summary>
    public TimeSpan Gecikme { get; set; }

    public ConnectionProfile? SonProfil => Cagrilar.Count > 0 ? Cagrilar[^1].Profil : null;
    public string? SonVeritabani => Cagrilar.Count > 0 ? Cagrilar[^1].Secenekler.VeritabaniOverride : null;
    public string? SonSql => Cagrilar.Count > 0 ? Cagrilar[^1].Sql : null;

    public async Task<QueryResult> ExecuteAsync(
        ConnectionProfile profil, string sql, ExecuteOptions secenekler, CancellationToken ct)
    {
        Cagrilar.Add(new Cagri(profil, sql, secenekler));

        if (Gecikme > TimeSpan.Zero)
            await Task.Delay(Gecikme, ct);

        return Sonuc ?? BosSonuc();
    }

    public Task<(bool Basarili, string? HataMesaji)> TestConnectionAsync(
        ConnectionProfile profil, CancellationToken ct)
        => Task.FromResult<(bool, string?)>((true, null));

    // NOT: QueryResult.Basarili hesaplanan değil ATANAN bir alandır; kurmayı unutmak
    // sonucu sessizce "başarısız" yapar (ilk yazımda tam bu oldu).
    public static QueryResult BosSonuc() => new()
    {
        Basarili = true,
        ResultSetler = [new ResultSetData { Kolonlar = [], Satirlar = [] }],
    };

    /// <summary>Metin arama sonucu biçimi: sema · ad · tür kodu · tanım.</summary>
    public static QueryResult AramaSonucu(params (string Sema, string Ad, string Tur, string Tanim)[] satirlar)
        => new()
        {
            Basarili = true,
            ResultSetler =
            [
                new ResultSetData
                {
                    Kolonlar =
                    [
                        new KolonBilgisi("sema", "nvarchar"), new KolonBilgisi("ad", "nvarchar"),
                        new KolonBilgisi("tur", "nvarchar"), new KolonBilgisi("tanim", "nvarchar"),
                    ],
                    Satirlar = [.. satirlar.Select(s => new object?[] { s.Sema, s.Ad, s.Tur, s.Tanim })],
                },
            ],
        };
}

/// <summary>Test profilleri.</summary>
public static class Profiller
{
    public static ConnectionProfile Yap(
        MotorTuru motor = MotorTuru.Mssql, string ad = "test", bool saltOkunur = false)
        => new()
        {
            Ad = ad, Motor = motor, Sunucu = "sunucu",
            Kimlik = KimlikTuru.Windows, SaltOkunur = saltOkunur,
        };
}
