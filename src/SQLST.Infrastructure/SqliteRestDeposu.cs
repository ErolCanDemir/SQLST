using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// REST istemcisinin kalıcı deposu (v20-S8): istek geçmişi (en yeni üstte, budamalı) + ortamlar +
/// adlı kayıtlı istekler. SOAP deposunun (<see cref="SqliteSoapDeposu"/>) kardeşi; şema YerelDepo göç 14.
/// Ortam DEĞİŞKENLERİ (token gibi sırlar içerebildiğinden) tek blob olarak JSON'lanıp <see cref="ISecretProtector"/>
/// (DPAPI) ile ŞİFRELİ saklanır — diskte düz durmaz.
/// </summary>
public sealed class SqliteRestDeposu(YerelDepo depo, ISecretProtector protector) : IRestDeposu
{
    public async Task GecmisEkleAsync(RestGecmisKaydi kayit, int enCok = 200, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO RestGecmisi (ZamanUtc, Metod, Url, Durum, SureMs)
            VALUES ($zaman, $metod, $url, $durum, $sure);
            DELETE FROM RestGecmisi
            WHERE Id NOT IN (SELECT Id FROM RestGecmisi ORDER BY Id DESC LIMIT $enCok);
            """;
        komut.Parameters.AddWithValue("$zaman", kayit.ZamanUtc.ToString("o", CultureInfo.InvariantCulture));
        komut.Parameters.AddWithValue("$metod", kayit.Metod);
        komut.Parameters.AddWithValue("$url", kayit.Url);
        komut.Parameters.AddWithValue("$durum", kayit.Durum);
        komut.Parameters.AddWithValue("$sure", kayit.SureMs);
        komut.Parameters.AddWithValue("$enCok", enCok);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RestGecmisKaydi>> GecmisAsync(int enCok = 200, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = "SELECT Id, ZamanUtc, Metod, Url, Durum, SureMs FROM RestGecmisi ORDER BY Id DESC LIMIT $enCok;";
        komut.Parameters.AddWithValue("$enCok", enCok);

        var liste = new List<RestGecmisKaydi>();
        await using SqliteDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        while (await okuyucu.ReadAsync(ct))
            liste.Add(new RestGecmisKaydi(
                okuyucu.GetInt64(0),
                DateTime.Parse(okuyucu.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                okuyucu.GetString(2), okuyucu.GetString(3), okuyucu.GetInt32(4), okuyucu.GetInt64(5)));
        return liste;
    }

    public async Task OrtamKaydetAsync(RestOrtam ortam, CancellationToken ct = default)
    {
        string blob = protector.Sifrele(JsonSerializer.Serialize(ortam.Degiskenler));
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO RestOrtam (Ad, DegiskenlerSifreli) VALUES ($ad, $blob)
            ON CONFLICT(Ad) DO UPDATE SET DegiskenlerSifreli = $blob;
            """;
        komut.Parameters.AddWithValue("$ad", ortam.Ad);
        komut.Parameters.AddWithValue("$blob", blob);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RestOrtam>> OrtamlarAsync(CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = "SELECT Ad, DegiskenlerSifreli FROM RestOrtam ORDER BY Ad COLLATE NOCASE;";

        var liste = new List<RestOrtam>();
        await using SqliteDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        while (await okuyucu.ReadAsync(ct))
        {
            IReadOnlyList<RestSatir> degiskenler = [];
            try
            {
                degiskenler = JsonSerializer.Deserialize<List<RestSatir>>(protector.Coz(okuyucu.GetString(1))) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                // Blob çözülemedi/bozuk (ör. başka Windows kullanıcısı) — o ortamı boş değişkenle sun, düşme.
            }
            liste.Add(new RestOrtam(okuyucu.GetString(0), degiskenler));
        }
        return liste;
    }

    public async Task IstekKaydetAsync(RestKayitliIstek istek, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO RestKayitliIstek (Ad, Metod, Url, Govde) VALUES ($ad, $metod, $url, $govde)
            ON CONFLICT(Ad) DO UPDATE SET Metod = $metod, Url = $url, Govde = $govde;
            """;
        komut.Parameters.AddWithValue("$ad", istek.Ad);
        komut.Parameters.AddWithValue("$metod", istek.Metod);
        komut.Parameters.AddWithValue("$url", istek.Url);
        komut.Parameters.AddWithValue("$govde", (object?)istek.Govde ?? DBNull.Value);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task IstekSilAsync(string ad, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = "DELETE FROM RestKayitliIstek WHERE Ad = $ad;";
        komut.Parameters.AddWithValue("$ad", ad);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RestKayitliIstek>> KayitliIsteklerAsync(CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = "SELECT Ad, Metod, Url, Govde FROM RestKayitliIstek ORDER BY Ad COLLATE NOCASE;";

        var liste = new List<RestKayitliIstek>();
        await using SqliteDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        while (await okuyucu.ReadAsync(ct))
            liste.Add(new RestKayitliIstek(
                okuyucu.GetString(0), okuyucu.GetString(1), okuyucu.GetString(2),
                okuyucu.IsDBNull(3) ? null : okuyucu.GetString(3)));
        return liste;
    }
}
