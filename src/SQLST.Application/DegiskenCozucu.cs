using System.Text.RegularExpressions;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// <c>{{değişken}}</c> çözümleyici (REST İstemcisi v20-S8): URL/başlık/gövdedeki <c>{{ad}}</c> kalıplarını
/// seçili ortamın değişkenleriyle değiştirir. Tanımsız değişken OLDUĞU GİBİ bırakılır (kullanıcı eksik
/// tanımı görsün, sessiz boşluk olmasın). Tek geçişlidir — değişkenin değeri başka bir <c>{{}}</c> içerse
/// bile yeniden çözülmez (döngü/şaşırtma koruması). Postman'in ortam değişkeni davranışının sade eşi.
/// </summary>
public static partial class DegiskenCozucu
{
    [GeneratedRegex(@"\{\{\s*([^{}\s]+)\s*\}\}")]
    private static partial Regex Desen();

    /// <summary>Metindeki tüm <c>{{ad}}</c>'ları sözlükten çözer; tanımsızları olduğu gibi bırakır.</summary>
    public static string Coz(string? metin, IReadOnlyDictionary<string, string> degiskenler)
    {
        if (string.IsNullOrEmpty(metin))
            return metin ?? "";
        return Desen().Replace(metin, m =>
            degiskenler.TryGetValue(m.Groups[1].Value, out string? deger) ? deger : m.Value);
    }

    /// <summary>Metinde geçen değişken ADLARI (tekrarsız, görülme sırasıyla) — "hangileri tanımsız" için.</summary>
    public static IReadOnlyList<string> AdlariCikar(string? metin)
    {
        if (string.IsNullOrEmpty(metin))
            return [];
        var gorulen = new HashSet<string>(StringComparer.Ordinal);
        var sonuc = new List<string>();
        foreach (Match m in Desen().Matches(metin))
            if (gorulen.Add(m.Groups[1].Value))
                sonuc.Add(m.Groups[1].Value);
        return sonuc;
    }

    /// <summary>Metindeki değişkenlerden sözlükte BULUNMAYANLAR (uyarı göstermek için); hepsi varsa boş.</summary>
    public static IReadOnlyList<string> TanimsizOlanlar(string? metin, IReadOnlyDictionary<string, string> degiskenler)
        => [.. AdlariCikar(metin).Where(ad => !degiskenler.ContainsKey(ad))];

    /// <summary>
    /// Bir <see cref="RestIstek"/>'in tüm alanlarındaki <c>{{değişken}}</c>'leri çözer (URL · query
    /// anahtar/değer · başlık değeri · gövde · kimlik) — App katmanı bunu servise vermeden önce çağırır,
    /// böylece <see cref="SQLST.Contracts.RestIstek"/> ağa çözülmüş gider. Başlık ANAHTARLARI çözülmez
    /// (ad genelde sabittir), değerler çözülür.
    /// </summary>
    public static RestIstek CozIstek(RestIstek istek, IReadOnlyDictionary<string, string> degiskenler)
        => istek with
        {
            Url = Coz(istek.Url, degiskenler),
            QueryParametreleri = [.. istek.QueryParametreleri.Select(q =>
                q with { Anahtar = Coz(q.Anahtar, degiskenler), Deger = Coz(q.Deger, degiskenler) })],
            Basliklar = [.. istek.Basliklar.Select(h => h with { Deger = Coz(h.Deger, degiskenler) })],
            Govde = istek.Govde is null ? null : Coz(istek.Govde, degiskenler),
            Kimlik = istek.Kimlik is { } k
                ? new SoapKimlik(CozN(k.KullaniciAdi, degiskenler), CozN(k.Parola, degiskenler))
                : null,
        };

    private static string? CozN(string? s, IReadOnlyDictionary<string, string> d) => s is null ? null : Coz(s, d);
}
