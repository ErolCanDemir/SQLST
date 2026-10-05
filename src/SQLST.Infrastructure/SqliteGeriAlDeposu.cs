using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Geri Al paketlerinin yerel deposu (V15-S3, BF-1; şema YerelDepo göç 12). Budama her
/// eklemede uygulanır — BF-1 kararları: yaş > <see cref="SaklamaGunu"/> silinir; toplam satır
/// verisi <see cref="ToplamBaytSiniri"/>'nı aşarsa en eskiden başlanarak sınıra inilir.
/// </summary>
public sealed class SqliteGeriAlDeposu(YerelDepo depo) : IGeriAlDeposu
{
    public const int SaklamaGunu = 30;
    public const long ToplamBaytSiniri = 500L * 1024 * 1024;

    public async Task<long> EkleAsync(GeriAlPaketi paket, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO GeriAlPaketi
                (TarihUtc, Sunucu, Veritabani, Tablo, Fiil, SqlMetni, SatirSayisi, KolonlarJson, PkJson, SatirlarJson)
            VALUES ($tarih, $sunucu, $db, $tablo, $fiil, $sql, $sayi, $kolonlar, $pk, $satirlar);
            SELECT last_insert_rowid();
            """;
        komut.Parameters.AddWithValue("$tarih", paket.TarihUtc.ToString("o", CultureInfo.InvariantCulture));
        komut.Parameters.AddWithValue("$sunucu", paket.Sunucu);
        komut.Parameters.AddWithValue("$db", paket.Veritabani);
        komut.Parameters.AddWithValue("$tablo", paket.Tablo);
        komut.Parameters.AddWithValue("$fiil", paket.Fiil);
        komut.Parameters.AddWithValue("$sql", paket.SqlMetni);
        komut.Parameters.AddWithValue("$sayi", paket.SatirSayisi);
        komut.Parameters.AddWithValue("$kolonlar", paket.KolonlarJson);
        komut.Parameters.AddWithValue("$pk", paket.PkJson);
        komut.Parameters.AddWithValue("$satirlar", paket.SatirlarJson);
        long id = Convert.ToInt64(await komut.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

        await BudaAsync(baglanti, ct);
        return id;
    }

    private static async Task BudaAsync(SqliteConnection baglanti, CancellationToken ct)
    {
        await using SqliteCommand buda = baglanti.CreateCommand();
        buda.CommandText = """
            DELETE FROM GeriAlPaketi WHERE TarihUtc < $esik;
            DELETE FROM GeriAlPaketi WHERE Id IN (
                SELECT Id FROM (
                    SELECT Id, SUM(LENGTH(SatirlarJson)) OVER (ORDER BY Id DESC) AS toplam
                    FROM GeriAlPaketi
                ) WHERE toplam > $sinir);
            """;
        buda.Parameters.AddWithValue("$esik",
            DateTime.UtcNow.AddDays(-SaklamaGunu).ToString("o", CultureInfo.InvariantCulture));
        buda.Parameters.AddWithValue("$sinir", ToplamBaytSiniri);
        await buda.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<GeriAlPaketi>> ListeleAsync(int limit = 200, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            SELECT Id, TarihUtc, Sunucu, Veritabani, Tablo, Fiil, SqlMetni, SatirSayisi, KolonlarJson, PkJson
            FROM GeriAlPaketi ORDER BY Id DESC LIMIT $limit;
            """;
        komut.Parameters.AddWithValue("$limit", limit);

        var liste = new List<GeriAlPaketi>();
        await using SqliteDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        while (await okuyucu.ReadAsync(ct))
            liste.Add(Oku(okuyucu, satirlarJson: "")); // liste hafif — satır verisi GetirAsync'te
        return liste;
    }

    public async Task<GeriAlPaketi?> GetirAsync(long id, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            SELECT Id, TarihUtc, Sunucu, Veritabani, Tablo, Fiil, SqlMetni, SatirSayisi, KolonlarJson, PkJson, SatirlarJson
            FROM GeriAlPaketi WHERE Id = $id;
            """;
        komut.Parameters.AddWithValue("$id", id);

        await using SqliteDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        return await okuyucu.ReadAsync(ct) ? Oku(okuyucu, okuyucu.GetString(10)) : null;
    }

    public async Task SilAsync(long id, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = "DELETE FROM GeriAlPaketi WHERE Id = $id;";
        komut.Parameters.AddWithValue("$id", id);
        await komut.ExecuteNonQueryAsync(ct);
    }

    private static GeriAlPaketi Oku(SqliteDataReader okuyucu, string satirlarJson) => new()
    {
        Id = okuyucu.GetInt64(0),
        TarihUtc = DateTime.Parse(okuyucu.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Sunucu = okuyucu.GetString(2),
        Veritabani = okuyucu.GetString(3),
        Tablo = okuyucu.GetString(4),
        Fiil = okuyucu.GetString(5),
        SqlMetni = okuyucu.GetString(6),
        SatirSayisi = okuyucu.GetInt32(7),
        KolonlarJson = okuyucu.GetString(8),
        PkJson = okuyucu.GetString(9),
        SatirlarJson = satirlarJson,
    };
}
