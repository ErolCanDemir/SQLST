using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Taranacak tek hedef: bir koleksiyonun bir alanı.</summary>
public sealed record IzAdayi(string Koleksiyon, string Alan);

/// <summary>
/// v20-S21 m.26 fikir 1 ("bu id nerede geçiyor?"): MongoDB'de yabancı anahtar YOKTUR — SQL'deki
/// Kayıt Haritası'nın karşılığını kurmak için değerin TİPİNE uyan alanlar taranır ($sample alan
/// envanterinden). Bu sınıf saf kısımdır: aday alan seçimi ve sorgu metinleri; koşturma UI'da.
/// </summary>
public static class MongoIdIzi
{
    /// <summary>Aynı anda taranacak azami alan — dev şemada sorgu yağmuru olmasın (UI de söyler).</summary>
    public const int AdayTavani = 250;

    /// <summary>24 haneli onaltılık → ObjectId sayılır (Mongo'nun kanonik id biçimi).</summary>
    public static bool ObjectIdMi(string deger) =>
        deger.Length == 24 && deger.All(Uri.IsHexDigit);

    /// <summary>
    /// Değerin tipine UYAN alanlar: ObjectId'de yalnız objectId alanları, metinde string alanları,
    /// sayıda sayısal alanlar. Envanterde alan birden çok tip görmüşse ("objectId|string") o da
    /// adaydır. Tip envanteri boş koleksiyon (alanları henüz süpürülmemiş) atlanır.
    /// </summary>
    public static IReadOnlyList<IzAdayi> Adaylar(IReadOnlyList<SemaNesnesi> koleksiyonlar, string deger)
    {
        string[] aranan = ObjectIdMi(deger)
            ? ["objectid"]
            : double.TryParse(deger, System.Globalization.NumberStyles.Any,
                              System.Globalization.CultureInfo.InvariantCulture, out _)
                ? ["int32", "int64", "double", "decimal"]
                : ["string"];

        var liste = new List<IzAdayi>();
        foreach (SemaNesnesi k in koleksiyonlar.Where(k => k.Tur == SemaNesneTuru.Koleksiyon))
        {
            foreach (SemaKolonu alan in k.Kolonlar)
            {
                string tip = alan.Tip.ToLowerInvariant();
                if (!aranan.Any(a => tip.Contains(a, StringComparison.Ordinal)))
                    continue;
                liste.Add(new IzAdayi(k.Ad, alan.Ad));
                if (liste.Count >= AdayTavani)
                    return liste;
            }
        }
        return liste;
    }

    /// <summary>Tek adayın eşleşme SAYISI: <c>$match</c> + <c>$count</c> (tek round-trip).</summary>
    public static string SayimSorgusu(IzAdayi aday, string deger)
        => $$"""
            { "aggregate": {{Metin(aday.Koleksiyon)}}, "pipeline": [
                { "$match": { {{Metin(aday.Alan)}}: {{Literal(deger)}} } },
                { "$count": "eşleşen" } ] }
            """;

    /// <summary>Satıra tıklanınca sekmede açılan sorgu — eşleşen belgeler.</summary>
    public static string BulSorgusu(IzAdayi aday, string deger, int limit = 200)
        => $$"""
            { "find": {{Metin(aday.Koleksiyon)}},
              "filter": { {{Metin(aday.Alan)}}: {{Literal(deger)}} },
              "limit": {{limit}} }
            """;

    /// <summary>ObjectId Extended JSON ile yazılır ({"$oid": …}) — sürücü gerçek ObjectId olarak çözer;
    /// sayı çıplak, gerisi tırnaklı metin.</summary>
    private static string Literal(string deger)
    {
        if (ObjectIdMi(deger))
            return $$"""{ "$oid": {{Metin(deger)}} }""";
        return double.TryParse(deger, System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture, out _)
            ? deger
            : Metin(deger);
    }

    /// <summary>JSON metin literali (kaçışlar JsonSerializer'a bırakılır — elle tırnak kaçırma yok).</summary>
    private static string Metin(string deger) => JsonSerializer.Serialize(deger);
}
