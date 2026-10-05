using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// AI'ın ürettiği SQL doğrulama sonucu (v21-S2). <see cref="Dogrulandi"/>=false ise motorun
/// çözümleyicisi elimizde yok (PG/MySQL/Oracle) — sessizce "geçerli" demeyiz, "doğrulanamadı" deriz.
/// </summary>
public sealed record SqlDogrulamaSonucu(bool Dogrulandi, bool Gecerli, string? Hata)
{
    public static readonly SqlDogrulamaSonucu Yapilamadi = new(Dogrulandi: false, Gecerli: false, Hata: null);
    public static SqlDogrulamaSonucu Ok() => new(Dogrulandi: true, Gecerli: true, Hata: null);
    public static SqlDogrulamaSonucu Bozuk(string hata) => new(Dogrulandi: true, Gecerli: false, Hata: hata);
}

/// <summary>
/// 🛡 AI çıktısı SQL doğrulayıcı (v21-S2, POC bulgusu 2026-08-08): yerel model geçersiz T-SQL
/// üretebiliyor (3B <c>LIMIT 5</c>, 7B yanlış konumlu <c>TOP</c>). "Sessiz yanlış yasak" ilkesi
/// AI çıktısına da uygulanır: SQLST'nin ELİNDEKİ gerçek çözümleyicilerle üretilen SQL sınanır.
/// MSSQL: ScriptDom (T-SQL parser). Mongo: JSON geçerliliği. Diğer motorlar: parser yok → "doğrulanamadı".
/// SAF ve testli; ağ/IO yok.
/// </summary>
public static class SqlDogrulayici
{
    public static SqlDogrulamaSonucu Dogrula(string? sql, MotorTuru motor)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return SqlDogrulamaSonucu.Yapilamadi;

        return motor switch
        {
            MotorTuru.Mssql => TsqlDogrula(sql),
            MotorTuru.Mongo => JsonDogrula(sql),
            // PG/MySQL/Oracle: kendi çözümleyicileri elimizde yok — uydurup "geçerli" demeyiz.
            _ => SqlDogrulamaSonucu.Yapilamadi,
        };
    }

    private static SqlDogrulamaSonucu TsqlDogrula(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        parser.Parse(new StringReader(sql), out IList<ParseError> hatalar);
        return hatalar.Count == 0
            ? SqlDogrulamaSonucu.Ok()
            : SqlDogrulamaSonucu.Bozuk($"Satır {hatalar[0].Line}: {hatalar[0].Message}");
    }

    private static SqlDogrulamaSonucu JsonDogrula(string metin)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(metin);
            return SqlDogrulamaSonucu.Ok();
        }
        catch (JsonException ex)
        {
            return SqlDogrulamaSonucu.Bozuk(ex.Message);
        }
    }
}
