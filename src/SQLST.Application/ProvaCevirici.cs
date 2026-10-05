using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLST.Application;

/// <summary>
/// Prova planı (V15-S1, BF-2): DML'in kendisi değil, etkisini GÖSTEREN salt-SELECT çifti.
/// <paramref name="SayimSql"/> tek satır tek kolon (COUNT_BIG) döner; <paramref name="OrnekSql"/>
/// etkilenecek satırlardan ilk 10'u getirir. İkisi tek gidişte (iki result set) koşulabilir.
/// </summary>
public sealed record ProvaPlani(
    string Fiil,                          // "UPDATE" | "DELETE"
    string Hedef,                         // görünen hedef (dbo.Musteri ya da alias'ın çözüldüğü metin)
    string SayimSql,
    string OrnekSql,
    IReadOnlyList<string> SetKolonlari,   // UPDATE'te SET edilen kolonlar (DELETE'te boş)
    bool WhereYok,                        // WHERE yok — "TÜM tablo etkilenecek" kırmızı uyarısı
    string? TopNotu);                     // sorguda TOP(n) varsa: sayım eşleşen TÜMÜNÜ sayar, DML en fazla n'i etkiler

/// <summary>
/// 🔍 Sorgu provası çevirici (BF-2 — kullanıcı: "her update'ten önce select yapıp bakıyoruz",
/// bu alışkanlığın otomasyonu). Tek ifadeli UPDATE/DELETE'i ScriptDom ile çözer ve WHERE/FROM
/// parçalarını ORİJİNAL metinden keserek (alias/join/hint bozulmadan) salt-SELECT'lere çevirir.
/// Hiçbir şey çalıştırmaz — çalıştıran katman yalnız ürettiği SELECT'leri koşar.
/// MVP sınırı: yalnız T-SQL; CTE'li/INSERT/MERGE desteklenmez (mesajla söylenir).
/// </summary>
public static class ProvaCevirici
{
    public static (ProvaPlani? Plan, string? Hata) Cevir(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (null, "Prova alınacak sorgu yok.");

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        if (parser.Parse(new StringReader(sql), out IList<ParseError> hatalar) is not TSqlScript script
            || hatalar.Count > 0)
            return (null, hatalar.Count > 0
                ? $"Söz dizimi hatası (Satır {hatalar[0].Line}): {hatalar[0].Message}"
                : "Sorgu çözümlenemedi.");

        List<TSqlStatement> ifadeler = [.. script.Batches.SelectMany(b => b.Statements)];
        if (ifadeler.Count == 0)
            return (null, "Prova alınacak sorgu yok.");
        if (ifadeler.Count > 1)
            return (null, $"Prova TEK ifadeyle çalışır (metinde {ifadeler.Count} ifade var) — "
                + "provasını istediğiniz UPDATE/DELETE'i seçili hale getirip tekrar deneyin.");

        return ifadeler[0] switch
        {
            UpdateStatement { WithCtesAndXmlNamespaces: not null } or DeleteStatement { WithCtesAndXmlNamespaces: not null }
                => (null, "CTE'li (WITH …) sorguların provası henüz desteklenmiyor."),
            UpdateStatement u => Kur(sql, "UPDATE", u.UpdateSpecification,
                [.. u.UpdateSpecification.SetClauses.OfType<AssignmentSetClause>()
                    .Where(s => s.Column is not null && s.NewValue is not null)
                    .Select(s => (Ad: s.Column!.MultiPartIdentifier.Identifiers[^1].Value,
                                  Ifade: Metin(sql, s.NewValue!)))]),
            DeleteStatement d => Kur(sql, "DELETE", d.DeleteSpecification, []),
            InsertStatement => (null, "INSERT'in provası yok — ekleyeceği veri sorgunun kendisidir; "
                + "SELECT kaynağını ayrıca çalıştırıp bakabilirsiniz."),
            _ => (null, "Prova yalnız UPDATE ve DELETE için çalışır."),
        };
    }

    private static (ProvaPlani?, string?) Kur(
        string sql, string fiil, UpdateDeleteSpecificationBase spec, List<(string Ad, string Ifade)> setler)
    {
        // Hedef ve kaynak: FROM varsa (UPDATE m … FROM Musteri m JOIN …) kaynak FROM'un kendisidir,
        // hedefin adı/alias'ı örnek SELECT'te "hedef.*" nitelemesi olur. FROM yoksa hedef tek kaynaktır.
        string hedefMetni = Metin(sql, spec.Target);
        string? fromMetni = spec.FromClause is null ? null : Metin(sql, spec.FromClause);
        string whereMetni = spec.WhereClause is null ? "" : " " + Metin(sql, spec.WhereClause);

        string kaynak = fromMetni ?? $"FROM {hedefMetni}";
        string ornekKolon = fromMetni is null ? "*" : $"{hedefMetni}.*";

        // v20-S21 saha m.28 ("prova olacak hali göstermeli"): UPDATE provasının örnek SELECT'i
        // artık mevcut kolonların YANINA, SET ifadelerini hesaplanmış kolon olarak koyar —
        // "[Ad → olacak]" başlığıyla; kullanıcı şimdiki/olacak değerleri yan yana karşılaştırır.
        // İfade metni orijinalden kesildiği için alias/fonksiyon/değişken aynen korunur.
        string olacaklar = setler.Count == 0
            ? ""
            : ", " + string.Join(", ", setler.Select(s =>
                $"{s.Ifade} AS [{s.Ad.Replace("]", "]]")} → olacak]"));

        string sayim = $"SELECT COUNT_BIG(*) AS [etkilenecek satır] {kaynak}{whereMetni};";
        string ornek = $"SELECT TOP (10) {ornekKolon}{olacaklar} {kaynak}{whereMetni};";
        List<string> setKolonlari = [.. setler.Select(s => s.Ad)];

        string? topNotu = spec.TopRowFilter is { } top
            ? $"Sorguda TOP {Metin(sql, top.Expression)} var — sayım eşleşen TÜM satırları sayar, "
              + $"{fiil} en fazla o kadarını etkiler."
            : null;

        return (new ProvaPlani(
            fiil, hedefMetni, sayim, ornek, setKolonlari,
            WhereYok: spec.WhereClause is null, topNotu), null);
    }

    /// <summary>Parçayı ORİJİNAL metinden keser — ScriptDom yeniden üretimi alias/hint/biçim bozabilirdi.</summary>
    private static string Metin(string sql, TSqlFragment parca)
        => sql.Substring(parca.StartOffset, parca.FragmentLength);
}
