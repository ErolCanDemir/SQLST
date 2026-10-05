using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// SOAP istemcisinin kalıcı deposu (v14-S3): istek geçmişi (en yeni üstte, budamalı) + ortam
/// profilleri (Ad birincil anahtar — aynı ada kaydetmek güncellemedir). Sorgu geçmişi
/// deseninin (SqliteSorguGecmisiDeposu) SOAP karşılığıdır; şema YerelDepo göç 11'de.
/// </summary>
public sealed class SqliteSoapDeposu(YerelDepo depo) : ISoapDeposu
{
    public async Task GecmisEkleAsync(SoapGecmisKaydi kayit, int enCok = 200, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO SoapGecmisi (ZamanUtc, Adres, Aksiyon, Zarf, HttpDurum, SureMs, FaultMu)
            VALUES ($zaman, $adres, $aksiyon, $zarf, $durum, $sure, $fault);
            DELETE FROM SoapGecmisi
            WHERE Id NOT IN (SELECT Id FROM SoapGecmisi ORDER BY Id DESC LIMIT $enCok);
            """;
        komut.Parameters.AddWithValue("$zaman", kayit.ZamanUtc.ToString("o", CultureInfo.InvariantCulture));
        komut.Parameters.AddWithValue("$adres", kayit.Adres);
        komut.Parameters.AddWithValue("$aksiyon", kayit.Aksiyon);
        komut.Parameters.AddWithValue("$zarf", kayit.Zarf);
        komut.Parameters.AddWithValue("$durum", kayit.HttpDurum);
        komut.Parameters.AddWithValue("$sure", kayit.SureMs);
        komut.Parameters.AddWithValue("$fault", kayit.FaultMu ? 1 : 0);
        komut.Parameters.AddWithValue("$enCok", enCok);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SoapGecmisKaydi>> GecmisAsync(int enCok = 200, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            SELECT Id, ZamanUtc, Adres, Aksiyon, Zarf, HttpDurum, SureMs, FaultMu
            FROM SoapGecmisi ORDER BY Id DESC LIMIT $enCok;
            """;
        komut.Parameters.AddWithValue("$enCok", enCok);

        var liste = new List<SoapGecmisKaydi>();
        await using SqliteDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        while (await okuyucu.ReadAsync(ct))
        {
            liste.Add(new SoapGecmisKaydi(
                okuyucu.GetInt64(0),
                DateTime.Parse(okuyucu.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                okuyucu.GetString(2),
                okuyucu.GetString(3),
                okuyucu.GetString(4),
                okuyucu.GetInt32(5),
                okuyucu.GetInt64(6),
                okuyucu.GetInt32(7) == 1));
        }

        return liste;
    }

    public async Task OrtamKaydetAsync(SoapOrtamKaydi ortam, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO SoapOrtam (Ad, WsdlUrl, Adres, KullaniciAdi, ParolaSifreli)
            VALUES ($ad, $wsdl, $adres, $kullanici, $parola)
            ON CONFLICT(Ad) DO UPDATE SET
                WsdlUrl = $wsdl, Adres = $adres, KullaniciAdi = $kullanici, ParolaSifreli = $parola;
            """;
        komut.Parameters.AddWithValue("$ad", ortam.Ad);
        komut.Parameters.AddWithValue("$wsdl", ortam.WsdlUrl);
        komut.Parameters.AddWithValue("$adres", ortam.Adres);
        komut.Parameters.AddWithValue("$kullanici", (object?)ortam.KullaniciAdi ?? DBNull.Value);
        komut.Parameters.AddWithValue("$parola", (object?)ortam.ParolaSifreli ?? DBNull.Value);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SoapOrtamKaydi>> OrtamlarAsync(CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText =
            "SELECT Ad, WsdlUrl, Adres, KullaniciAdi, ParolaSifreli FROM SoapOrtam ORDER BY Ad COLLATE NOCASE;";

        var liste = new List<SoapOrtamKaydi>();
        await using SqliteDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        while (await okuyucu.ReadAsync(ct))
            liste.Add(new SoapOrtamKaydi(
                okuyucu.GetString(0), okuyucu.GetString(1), okuyucu.GetString(2),
                okuyucu.IsDBNull(3) ? null : okuyucu.GetString(3),
                okuyucu.IsDBNull(4) ? null : okuyucu.GetString(4)));
        return liste;
    }
}
