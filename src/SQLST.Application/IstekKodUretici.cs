using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Kod üretimi hedef dili (v20-S13, madde 13).</summary>
public enum KodDili { Curl, CSharp, Python }

/// <summary>
/// Bir <see cref="RestIstek"/>'ten paylaşılabilir/çalıştırılabilir kod üretir (v20-S13, madde 13): cURL
/// (<see cref="CurlCozumleyici.Uret"/>'e devreder), C# (<c>HttpClient</c>) ve Python (<c>requests</c>).
/// SAF; birim testli. İstek ZATEN çözülmüş verilir ({{değişken}} yerine değerler) — çağıran
/// <c>DegiskenCozucu</c> ile çözer. String kaçışı C#/Python ortak (<c>\" \\ \n</c>).
/// </summary>
public static class IstekKodUretici
{
    public static string Uret(RestIstek istek, KodDili dil) => dil switch
    {
        KodDili.Curl => CurlCozumleyici.Uret(istek),
        KodDili.CSharp => CSharp(istek),
        KodDili.Python => Python(istek),
        _ => "",
    };

    private static string CSharp(RestIstek i)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using var client = new HttpClient();");
        string url = CurlCozumleyici.UrlBirlestir(i.Url, i.QueryParametreleri);
        sb.AppendLine($"var request = new HttpRequestMessage(HttpMethod.{PascalMetod(i.Metod)}, {Kacir(url)});");

        foreach (RestSatir h in i.Basliklar.Where(h => h.Etkin && !string.IsNullOrWhiteSpace(h.Anahtar)))
            sb.AppendLine($"request.Headers.TryAddWithoutValidation({Kacir(h.Anahtar)}, {Kacir(h.Deger)});");

        if (i.Kimlik is { BearerMi: true } bearer)
            sb.AppendLine($"request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(\"Bearer\", {Kacir(bearer.Parola ?? "")});");
        else if (i.Kimlik is { Dolu: true } basic)
            sb.AppendLine($"request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(\"Basic\", System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes({Kacir($"{basic.KullaniciAdi}:{basic.Parola}")})));");

        if (i.Govde is { Length: > 0 } govde)
            sb.AppendLine($"request.Content = new StringContent({Kacir(govde)}, System.Text.Encoding.UTF8, \"application/json\");");

        sb.AppendLine("var response = await client.SendAsync(request);");
        sb.AppendLine("Console.WriteLine((int)response.StatusCode);");
        sb.AppendLine("Console.WriteLine(await response.Content.ReadAsStringAsync());");
        return sb.ToString();
    }

    private static string Python(RestIstek i)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import requests");
        sb.AppendLine();

        RestSatir[] basliklar = [.. i.Basliklar.Where(h => h.Etkin && !string.IsNullOrWhiteSpace(h.Anahtar))];
        sb.AppendLine(basliklar.Length == 0
            ? "headers = {}"
            : "headers = {" + string.Join(", ", basliklar.Select(h => $"{Kacir(h.Anahtar)}: {Kacir(h.Deger)}")) + "}");

        RestSatir[] paramlar = [.. i.QueryParametreleri.Where(q => q.Etkin && !string.IsNullOrWhiteSpace(q.Anahtar))];
        sb.AppendLine(paramlar.Length == 0
            ? "params = {}"
            : "params = {" + string.Join(", ", paramlar.Select(q => $"{Kacir(q.Anahtar)}: {Kacir(q.Deger)}")) + "}");

        string ekstra = "";
        if (i.Govde is { Length: > 0 } govde)
            ekstra += $", data={Kacir(govde)}";
        if (i.Kimlik is { BearerMi: true } bearer)
            sb.AppendLine($"headers['Authorization'] = 'Bearer ' + {Kacir(bearer.Parola ?? "")}");
        else if (i.Kimlik is { Dolu: true } basic)
            ekstra += $", auth=({Kacir(basic.KullaniciAdi ?? "")}, {Kacir(basic.Parola ?? "")})";

        sb.AppendLine($"resp = requests.request({Kacir(i.Metod.ToString().ToLowerInvariant())}, {Kacir(i.Url)}, headers=headers, params=params{ekstra})");
        sb.AppendLine("print(resp.status_code)");
        sb.AppendLine("print(resp.text)");
        return sb.ToString();
    }

    /// <summary>Enum adını Pascal'a çevirir (GET → Get) — C# HttpMethod.Get için.</summary>
    private static string PascalMetod(HttpMetodu m)
    {
        string s = m.ToString();
        return s[0] + s[1..].ToLowerInvariant();
    }

    /// <summary>C#/Python ortak string kaçışı: çift tırnaklı, <c>\ " \n</c> kaçışlı (\r atılır).</summary>
    private static string Kacir(string? s) =>
        "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
}
