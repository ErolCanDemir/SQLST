using System.Net;
using System.Text;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// REST gönderim (v20-S8): değişken çözümünün isteğe uygulanması (App) + <see cref="RestIstemcisi"/>'nin
/// HttpListener'lı KAPALI DEVRE entegrasyonu (gerçek sunucu gerekmez; localhost → proxy'siz yol).
/// </summary>
public class RestIstemcisiTests
{
    [Fact]
    public void CozIstek_url_query_baslik_govde_ve_kimlikte_degiskeni_cozer()
    {
        var d = new Dictionary<string, string> { ["baseUrl"] = "https://api.x", ["token"] = "TOK", ["il"] = "Ankara" };
        var istek = new RestIstek(HttpMetodu.POST, "{{baseUrl}}/firmalar",
            [new RestSatir("il", "{{il}}")],
            [new RestSatir("X-Trace", "{{token}}")],
            """{"q":"{{il}}"}""",
            new SoapKimlik(null, "{{token}}"));

        RestIstek c = DegiskenCozucu.CozIstek(istek, d);

        Assert.Equal("https://api.x/firmalar", c.Url);
        Assert.Equal("Ankara", c.QueryParametreleri[0].Deger);
        Assert.Equal("TOK", c.Basliklar[0].Deger);
        Assert.Equal("""{"q":"Ankara"}""", c.Govde);
        Assert.Equal("TOK", c.Kimlik!.Parola);
    }

    [Fact]
    public async Task Kapali_devre_get_bearer_query_ve_post_govde()
    {
        int port = Random.Shared.Next(21000, 45000);
        string kok = $"http://localhost:{port}/";
        using var sunucu = new HttpListener();
        sunucu.Prefixes.Add(kok);
        sunucu.Start();

        string? gelenMetod = null, gelenYol = null, gelenAuth = null, gelenGovde = null;
        var sunucuGorevi = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                HttpListenerContext ctx = await sunucu.GetContextAsync();
                gelenMetod = ctx.Request.HttpMethod;
                gelenYol = ctx.Request.Url!.PathAndQuery;
                gelenAuth = ctx.Request.Headers["Authorization"];
                using (var okuyucu = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    gelenGovde = await okuyucu.ReadToEndAsync();

                bool post = ctx.Request.HttpMethod == "POST";
                ctx.Response.StatusCode = post ? 201 : 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                byte[] veri = Encoding.UTF8.GetBytes(post ? """{"id":9}""" : """{"firmalar":[]}""");
                await ctx.Response.OutputStream.WriteAsync(veri);
                ctx.Response.Close();
            }
        });

        var istemci = new RestIstemcisi();

        // GET + Bearer + query parametresi
        RestCevap g = await istemci.GonderAsync(
            new RestIstek(HttpMetodu.GET, kok + "firmalar",
                [new RestSatir("il", "Ankara")], [], null, new SoapKimlik(null, "TOK")),
            CancellationToken.None);
        Assert.Equal(200, g.Durum);
        Assert.Contains("firmalar", g.Govde, StringComparison.Ordinal);
        Assert.Equal("GET", gelenMetod);
        Assert.Contains("il=Ankara", gelenYol!, StringComparison.Ordinal);
        Assert.Equal("Bearer TOK", gelenAuth);
        Assert.True(g.Sure > TimeSpan.Zero);
        Assert.Contains(g.Basliklar, b => b.Anahtar.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));

        // POST + JSON gövde
        RestCevap p = await istemci.GonderAsync(
            new RestIstek(HttpMetodu.POST, kok + "firmalar",
                [], [new RestSatir("Content-Type", "application/json")], """{"ad":"LST"}""", null),
            CancellationToken.None);
        Assert.Equal(201, p.Durum);
        Assert.Equal("POST", gelenMetod);
        Assert.Equal("""{"ad":"LST"}""", gelenGovde);
        Assert.Equal("application/json", p.IcerikTipi?.Split(';')[0]);

        sunucu.Stop();
        await Task.WhenAny(sunucuGorevi, Task.Delay(1000));
    }
}
