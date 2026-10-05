using System.Text.Json;
using System.Text.RegularExpressions;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// "Tüm satırları etkileyen yazma" sigortası (FG-6.2) — MOTORA GÖRE (V3 denetimi 2026-07-18).
///
/// Eskiden bu kontrol yalnız T-SQL ayrıştırıcısıyla (ScriptDom) yapılıyordu ve ayrıştırma
/// başarısız olunca sessizce BOŞ dönüyordu. Sonuç: PostgreSQL/MySQL/Oracle'da uyarı hiç
/// çıkmıyordu; MongoDB'de ise boş filtreli <c>delete</c> koleksiyonun tamamını uyarısız
/// siliyordu. Artık her motorun kendi kontrolü var:
///   • MSSQL   → ScriptDom (tam ayrıştırma); ayrıştırılamazsa metin tabanlı yedeğe düşer
///   • PG/MySQL/Oracle → metin tabanlı sigorta (ucuz ama gerçek)
///   • Mongo   → JSON komutunda boş filtre (q/filter) araması
/// Amaç %100 doğruluk değil, kaza sigortası (02-mimari §4.4).
/// </summary>
public static class YazmaSigortasi
{
    public static IReadOnlyList<string> WheresizYazmalar(string metin, MotorTuru motor) => motor switch
    {
        MotorTuru.Mongo => MongoBosFiltreler(metin),
        MotorTuru.Mssql => MssqlVeYedek(metin),
        _ => MetinTabanli(metin),
    };

    private static IReadOnlyList<string> MssqlVeYedek(string sql)
    {
        IReadOnlyList<string> ayristirilmis = SqlCozumleyici.WheresizDmlBul(sql);
        if (ayristirilmis.Count > 0)
            return ayristirilmis;

        // ScriptDom bir yerde takıldıysa (yeni sözdizimi, kısmi metin) sessiz kalmayalım.
        return MetinTabanli(sql);
    }

    // UPDATE <hedef> SET … / DELETE [FROM] <hedef> … — aynı ifadede WHERE yoksa yakalar.
    private static readonly Regex GuncelleSil = new(
        @"(?<fiil>\bUPDATE\b|\bDELETE\b)(?<govde>.*?)(?=;|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WhereVar = new(@"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Yorumları ve string literalleri eleyip ifade ifade bakar (SQL ailesi ortak yedeği).</summary>
    private static IReadOnlyList<string> MetinTabanli(string sql)
    {
        string temiz = Temizle(sql);
        var bulgular = new List<string>();
        foreach (Match m in GuncelleSil.Matches(temiz))
        {
            string govde = m.Groups["govde"].Value;
            if (WhereVar.IsMatch(govde))
                continue;

            string fiil = m.Groups["fiil"].Value.ToUpperInvariant();
            string ozet = (fiil + govde).Trim();
            if (ozet.Length > 120)
                ozet = ozet[..120] + "…";
            bulgular.Add(ozet);
        }
        return bulgular;
    }

    /// <summary>
    /// Yorum ve metin sabitlerini ayıklar — plan yolundaki temkinli yazma denetimi de kullanır
    /// (<see cref="QueryService.PlanIcinYazmaSayilir"/>), böylece <c>'DELETE'</c> gibi bir metin
    /// sabiti ya da yorum satırı yanlış alarm üretmez.
    /// </summary>
    public static string YorumVeMetinleriAyikla(string sql) => Temizle(sql);

    /// <summary>-- ve /* */ yorumlarını, '…' literallerini boşlukla değiştirir (yanlış eşleşme olmasın).</summary>
    private static string Temizle(string sql)
    {
        string s = Regex.Replace(sql, @"--[^\n]*", " ");
        s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"'([^']|'')*'", "''");
        return s;
    }

    /// <summary>
    /// Mongo: { "delete": "k", "deletes": [ { "q": {}, "limit": 0 } ] } → boş q TÜM belgeleri siler.
    /// update için { "q": {}, "multi": true } aynı anlama gelir.
    /// </summary>
    private static IReadOnlyList<string> MongoBosFiltreler(string metin)
    {
        var bulgular = new List<string>();
        try
        {
            using JsonDocument belge = JsonDocument.Parse(
                metin, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement kok = belge.RootElement;
            if (kok.ValueKind != JsonValueKind.Object)
                return bulgular;

            Denetle(kok, "delete", "deletes", "silme", bulgular);
            Denetle(kok, "update", "updates", "güncelleme", bulgular);

            // findAndModify tek belge etkiler (limit 1) — tüm koleksiyon riski yok.
        }
        catch (JsonException)
        {
            // Geçersiz JSON zaten çalıştırılamaz; sigorta sessiz kalır.
        }
        return bulgular;

        static void Denetle(
            JsonElement kok, string komut, string dizi, string etiket, List<string> bulgular)
        {
            if (!kok.TryGetProperty(komut, out JsonElement hedef) || hedef.ValueKind != JsonValueKind.String)
                return;
            if (!kok.TryGetProperty(dizi, out JsonElement islemler) || islemler.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement islem in islemler.EnumerateArray())
            {
                if (!islem.TryGetProperty("q", out JsonElement filtre))
                    continue;
                bool bos = filtre.ValueKind == JsonValueKind.Object && !filtre.EnumerateObject().Any();
                if (bos)
                    bulgular.Add($"{etiket}: '{hedef.GetString()}' koleksiyonunda filtre BOŞ ({{}}) — tüm belgeler etkilenir");
            }
        }
    }
}
