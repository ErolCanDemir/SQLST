using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// cURL ↔ <see cref="RestIstek"/> köprüsü (REST İstemcisi v20-S8, Katman 2): bir <c>curl</c> komutunu
/// yapıştırıp isteğe çevirme (<see cref="Coz"/>) ve isteği paylaşılabilir cURL komutuna dökme
/// (<see cref="Uret"/>). SAF (ağ yok). Kabuk-tokenizasyonu tek/çift tırnak + <c>\</c> kaçış + satır-sonu
/// devamını (<c>\</c>+satırsonu) çözer. Tanınan bayraklar: <c>-X/--request · -H/--header · -d/--data*
/// · -u/--user · -G/--get · --url</c>; bilinmeyen bayraklar atlanır (best-effort — cURL'ün tüm yüzeyi değil).
/// </summary>
public static class CurlCozumleyici
{
    /// <summary>cURL komutunu <see cref="RestIstek"/>'e çevirir; URL bulunamazsa null.</summary>
    public static RestIstek? Coz(string? curl)
    {
        List<string> tok = Tokenlar(curl ?? "");
        int i = 0;
        if (i < tok.Count && tok[i].Equals("curl", StringComparison.OrdinalIgnoreCase))
            i++;

        HttpMetodu? metod = null;
        string? url = null;
        SoapKimlik? kimlik = null;
        bool getIleData = false;
        var basliklar = new List<RestSatir>();
        var datalar = new List<string>();

        for (; i < tok.Count; i++)
        {
            string a = tok[i];
            switch (a)
            {
                case "-X" or "--request":
                    if (++i < tok.Count) metod = MetodCoz(tok[i]);
                    break;
                case "-H" or "--header":
                    if (++i < tok.Count && BaslikBol(tok[i]) is { } b) basliklar.Add(b);
                    break;
                case "-d" or "--data" or "--data-raw" or "--data-ascii" or "--data-binary" or "--data-urlencode":
                    if (++i < tok.Count) datalar.Add(tok[i]);
                    break;
                case "-u" or "--user":
                    if (++i < tok.Count) kimlik = KimlikBol(tok[i]);
                    break;
                case "-G" or "--get":
                    getIleData = true;
                    break;
                case "--url":
                    if (++i < tok.Count) url = tok[i];
                    break;
                default:
                    if (!a.StartsWith('-') && url is null)
                        url = a; // ilk bayrak-olmayan argüman = URL
                    // bilinmeyen bayrak → atla (değer almadığını varsay)
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
            return null;

        string? govde = datalar.Count > 0 ? string.Join("&", datalar) : null;
        var query = new List<RestSatir>();
        if (getIleData && govde is not null)
        {
            foreach (RestSatir s in QueryBol(govde)) query.Add(s);
            govde = null;
        }
        metod ??= govde is not null ? HttpMetodu.POST : HttpMetodu.GET; // -d varsa POST (cURL kuralı)
        return new RestIstek(metod.Value, url!, query, basliklar, govde, kimlik);
    }

    /// <summary>İsteği tek satırlık, paylaşılabilir bir cURL komutuna döker.</summary>
    public static string Uret(RestIstek istek)
    {
        var sb = new StringBuilder("curl");
        if (istek.Metod != HttpMetodu.GET)
            sb.Append(" -X ").Append(istek.Metod);

        sb.Append(' ').Append(Tirnak(UrlBirlestir(istek.Url, istek.QueryParametreleri)));

        foreach (RestSatir h in istek.Basliklar.Where(h => h.Etkin && !string.IsNullOrWhiteSpace(h.Anahtar)))
            sb.Append(" -H ").Append(Tirnak($"{h.Anahtar}: {h.Deger}"));

        if (istek.Kimlik is { BearerMi: true } bearer)
            sb.Append(" -H ").Append(Tirnak($"Authorization: Bearer {bearer.Parola}"));
        else if (istek.Kimlik is { Dolu: true } basic)
            sb.Append(" -u ").Append(Tirnak($"{basic.KullaniciAdi}:{basic.Parola}"));

        if (!string.IsNullOrEmpty(istek.Govde))
            sb.Append(" -d ").Append(Tirnak(istek.Govde));

        return sb.ToString();
    }

    /// <summary>URL'e etkin query parametrelerini ekler (URL'de zaten <c>?</c> varsa <c>&amp;</c> ile birleştirir).</summary>
    public static string UrlBirlestir(string url, IReadOnlyList<RestSatir> query)
    {
        List<RestSatir> etkin = [.. query.Where(q => q.Etkin && !string.IsNullOrWhiteSpace(q.Anahtar))];
        if (etkin.Count == 0)
            return url;
        string ek = string.Join("&", etkin.Select(q => $"{q.Anahtar}={q.Deger}"));
        return url + (url.Contains('?') ? "&" : "?") + ek;
    }

    private static HttpMetodu MetodCoz(string s)
        => Enum.TryParse(s.Trim(), ignoreCase: true, out HttpMetodu m) ? m : HttpMetodu.GET;

    private static RestSatir? BaslikBol(string ham)
    {
        int i = ham.IndexOf(':');
        if (i < 0)
            return null;
        return new RestSatir(ham[..i].Trim(), ham[(i + 1)..].Trim());
    }

    /// <summary>"kullanıcı:parola" → Basic <see cref="SoapKimlik"/> (parola olmasa da olur).</summary>
    private static SoapKimlik KimlikBol(string ham)
    {
        int i = ham.IndexOf(':');
        return i < 0 ? new SoapKimlik(ham, null) : new SoapKimlik(ham[..i], ham[(i + 1)..]);
    }

    private static IEnumerable<RestSatir> QueryBol(string ham)
    {
        foreach (string parca in ham.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = parca.IndexOf('=');
            yield return i < 0 ? new RestSatir(parca, "") : new RestSatir(parca[..i], parca[(i + 1)..]);
        }
    }

    /// <summary>Tek tırnakla sarar (kabuk-güvenli); içteki tek tırnak <c>'\''</c> ile kaçırılır.</summary>
    private static string Tirnak(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Kabuk tokenizasyonu: tek/çift tırnak, <c>\</c> kaçış, <c>\</c>+satırsonu devamı.</summary>
    private static List<string> Tokenlar(string s)
    {
        var t = new List<string>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(s[i]))
                i++;
            if (i >= n)
                break;

            var sb = new StringBuilder();
            while (i < n && !char.IsWhiteSpace(s[i]))
            {
                char c = s[i];
                if (c == '\\' && i + 1 < n && (s[i + 1] == '\n' || s[i + 1] == '\r')) { i += 2; continue; } // satır devamı
                if (c == '\\' && i + 1 < n) { sb.Append(s[i + 1]); i += 2; continue; }                      // kaçış
                if (c == '\'')
                {
                    i++;
                    while (i < n && s[i] != '\'') sb.Append(s[i++]);
                    if (i < n) i++;
                    continue;
                }
                if (c == '"')
                {
                    i++;
                    while (i < n && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < n) { sb.Append(s[i + 1]); i += 2; }
                        else sb.Append(s[i++]);
                    }
                    if (i < n) i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            t.Add(sb.ToString());
        }
        return t;
    }
}
