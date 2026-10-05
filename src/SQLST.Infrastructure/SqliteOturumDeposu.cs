using Microsoft.Data.Sqlite;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>Yerel SQLite'ta oturum (açık sekmeler) deposu (V2-S2, FG-3.6). Kaydet = sil + yaz.</summary>
public sealed class SqliteOturumDeposu : IOturumDeposu
{
    private readonly YerelDepo _depo;

    public SqliteOturumDeposu(YerelDepo depo) => _depo = depo;

    public async Task KaydetAsync(
        IReadOnlyList<OturumSekmeKaydi> sekmeler, Guid? profilId = null, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteTransaction tran = (SqliteTransaction)await baglanti.BeginTransactionAsync(ct);
        object profil = (object?)profilId?.ToString() ?? DBNull.Value;

        // Yalnız BU profilin kayıtları değişir — diğer profillerin çalışma alanı korunur.
        await using (SqliteCommand sil = baglanti.CreateCommand())
        {
            sil.Transaction = tran;
            sil.CommandText = "DELETE FROM OturumSekmesi WHERE ProfilId IS $profil;";
            sil.Parameters.AddWithValue("$profil", profil);
            await sil.ExecuteNonQueryAsync(ct);
        }

        for (int i = 0; i < sekmeler.Count; i++)
        {
            await using SqliteCommand ekle = baglanti.CreateCommand();
            ekle.Transaction = tran;
            ekle.CommandText = """
                INSERT INTO OturumSekmesi (ProfilId, Sira, Baslik, Veritabani, Sql, SeciliMi, OtomatikAd)
                VALUES ($profil, $sira, $baslik, $vt, $sql, $secili, $oto);
                """;
            ekle.Parameters.AddWithValue("$profil", profil);
            ekle.Parameters.AddWithValue("$sira", i);
            ekle.Parameters.AddWithValue("$baslik", sekmeler[i].Baslik);
            ekle.Parameters.AddWithValue("$vt", (object?)sekmeler[i].Veritabani ?? DBNull.Value);
            ekle.Parameters.AddWithValue("$sql", sekmeler[i].Sql);
            ekle.Parameters.AddWithValue("$secili", sekmeler[i].SeciliMi ? 1 : 0);
            ekle.Parameters.AddWithValue("$oto", sekmeler[i].OtomatikAd ? 1 : 0);
            await ekle.ExecuteNonQueryAsync(ct);
        }

        await tran.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<OturumSekmeKaydi>> YukleAsync(
        Guid? profilId = null, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            SELECT Baslik, Veritabani, Sql, SeciliMi, OtomatikAd
            FROM OturumSekmesi WHERE ProfilId IS $profil ORDER BY Sira;
            """;
        komut.Parameters.AddWithValue("$profil", (object?)profilId?.ToString() ?? DBNull.Value);

        var sekmeler = new List<OturumSekmeKaydi>();
        await using SqliteDataReader oku = await komut.ExecuteReaderAsync(ct);
        while (await oku.ReadAsync(ct))
        {
            sekmeler.Add(new OturumSekmeKaydi
            {
                Baslik = oku.GetString(0),
                Veritabani = oku.IsDBNull(1) ? null : oku.GetString(1),
                Sql = oku.GetString(2),
                SeciliMi = oku.GetInt32(3) != 0,
                OtomatikAd = oku.GetInt32(4) != 0,
            });
        }
        return sekmeler;
    }
}
