using System.Text.RegularExpressions;

namespace SQLST.Application;

/// <summary>
/// Akıllı sekme adlandırma (V2-S2): çalıştırılan sorgudan "SELECT Musteri",
/// "EXEC spGetOrders" gibi kısa bir başlık türetir. Amaç %100 parse değil,
/// "sorgu3.sql" yerine ne olduğu bir bakışta anlaşılan ad (02-mimari §4.4 ruhu).
/// </summary>
public static partial class SekmeAdlandirici
{
    private const int EnUzunAd = 30;

    /// <summary>Sorgudan başlık türetir; anlamlı bir şey bulamazsa null (mevcut ad korunur).</summary>
    public static string? AdTuret(string sql)
    {
        string temiz = YorumlariSil(sql);

        // Fiil + hedef nesne kalıpları — ilk eşleşen kazanır (ilk statement'ı yansıtır).
        Match m = FiilNesne().Match(temiz);
        if (!m.Success)
            return null;

        string fiil = m.Groups["fiil"].Value.ToUpperInvariant() switch
        {
            "EXECUTE" => "EXEC",
            var f => f,
        };
        string nesne = NesneAdiKisalt(m.Groups["nesne"].Value);
        if (nesne.Length == 0)
            return null;

        string ad = $"{fiil} {nesne}";
        return ad.Length <= EnUzunAd ? ad : ad[..(EnUzunAd - 1)] + "…";
    }

    /// <summary>[dbo].[Musteri] / db.dbo.Musteri → Musteri (son parça, köşeli/tırnak soyulmuş).</summary>
    private static string NesneAdiKisalt(string ham)
    {
        string son = ham.Split('.')[^1].Trim().Trim('[', ']', '"');
        return son;
    }

    private static string YorumlariSil(string sql)
        => SatirYorumu().Replace(BlokYorumu().Replace(sql, " "), " ");

    // Fiil ve hedefi tek desende: SELECT…FROM x | INSERT INTO x | UPDATE x | DELETE FROM x |
    // EXEC x | TRUNCATE TABLE x | CREATE/ALTER <tür> x. Nesne: [köşeli], "tırnaklı", #temp
    // ve nokta ile ayrılmış parçalar.
    [GeneratedRegex(
        """
        \b(?:
            (?<fiil>SELECT)\b(?:(?!\bFROM\b).)*?\bFROM\s+(?<nesne>[\w#\[\]".]+)
          | (?<fiil>INSERT)\s+(?:INTO\s+)?(?<nesne>[\w#\[\]".]+)
          | (?<fiil>UPDATE)\s+(?:TOP\s*\(\d+\)\s*)?(?<nesne>[\w#\[\]".]+)
          | (?<fiil>DELETE)\s+(?:TOP\s*\(\d+\)\s*)?(?:FROM\s+)?(?<nesne>[\w#\[\]".]+)
          | (?<fiil>EXEC|EXECUTE)\s+(?<nesne>[\w#\[\]".]+)
          | (?<fiil>TRUNCATE)\s+TABLE\s+(?<nesne>[\w#\[\]".]+)
          | (?<fiil>CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?:PROCEDURE|PROC|VIEW|FUNCTION|TABLE|TRIGGER|INDEX)\s+(?<nesne>[\w#\[\]".]+)
        )
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline)]
    private static partial Regex FiilNesne();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex SatirYorumu();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlokYorumu();
}
