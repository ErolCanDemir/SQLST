using Microsoft.Data.Sqlite;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Snippet'lerin yerel SQLite deposu (V5-S4). Motor sütunu <see cref="MotorTuru"/> adıyla
/// (metin) saklanır — sayısal enum değeri saklansaydı enum'a araya yeni motor eklendiğinde
/// eski satırlar sessizce başka motora kayardı.
/// </summary>
public sealed class SqliteSnippetDeposu : ISnippetDeposu
{
    private readonly YerelDepo _depo;

    public SqliteSnippetDeposu(YerelDepo depo) => _depo = depo;

    public async Task<IReadOnlyList<Snippet>> ListeleAsync(MotorTuru motor, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        // O motora özel olanlar + motorsuz (her motorda geçerli) olanlar.
        komut.CommandText = """
            SELECT Id, Kisayol, Baslik, Govde, Motor, Yerlesik FROM Snippet
            WHERE Motor IS NULL OR Motor = $m
            ORDER BY Kisayol;
            """;
        komut.Parameters.AddWithValue("$m", motor.ToString());
        return await OkuAsync(komut, ct);
    }

    public async Task<IReadOnlyList<Snippet>> TumunuListeleAsync(CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            SELECT Id, Kisayol, Baslik, Govde, Motor, Yerlesik FROM Snippet
            ORDER BY IFNULL(Motor, ''), Kisayol;
            """;
        return await OkuAsync(komut, ct);
    }

    public async Task<long> EkleAsync(Snippet snippet, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO Snippet (Kisayol, Baslik, Govde, Motor, Yerlesik)
            VALUES ($k, $b, $g, $m, $y);
            SELECT last_insert_rowid();
            """;
        Parametreler(komut, snippet);
        return Convert.ToInt64(await komut.ExecuteScalarAsync(ct));
    }

    public async Task GuncelleAsync(Snippet snippet, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            UPDATE Snippet SET Kisayol = $k, Baslik = $b, Govde = $g, Motor = $m, Yerlesik = $y
            WHERE Id = $id;
            """;
        Parametreler(komut, snippet);
        komut.Parameters.AddWithValue("$id", snippet.Id);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task SilAsync(long id, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = "DELETE FROM Snippet WHERE Id = $id;";
        komut.Parameters.AddWithValue("$id", id);
        await komut.ExecuteNonQueryAsync(ct);
    }

    private static void Parametreler(SqliteCommand komut, Snippet s)
    {
        komut.Parameters.AddWithValue("$k", s.Kisayol);
        komut.Parameters.AddWithValue("$b", s.Baslik);
        komut.Parameters.AddWithValue("$g", s.Govde);
        komut.Parameters.AddWithValue("$m", s.Motor?.ToString() ?? (object)DBNull.Value);
        komut.Parameters.AddWithValue("$y", s.Yerlesik ? 1 : 0);
    }

    private static async Task<IReadOnlyList<Snippet>> OkuAsync(SqliteCommand komut, CancellationToken ct)
    {
        var liste = new List<Snippet>();
        await using SqliteDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        while (await okuyucu.ReadAsync(ct))
        {
            liste.Add(new Snippet(
                Id: okuyucu.GetInt64(0),
                Kisayol: okuyucu.GetString(1),
                Baslik: okuyucu.GetString(2),
                Govde: okuyucu.GetString(3),
                Motor: MotorCoz(okuyucu.IsDBNull(4) ? null : okuyucu.GetString(4)),
                Yerlesik: okuyucu.GetInt32(5) != 0));
        }
        return liste;
    }

    /// <summary>
    /// Bilinmeyen motor adı satırı DÜŞÜRMEZ, "her motorda" (null) sayılır: eski bir sürümde
    /// yazılmış ya da elle bozulmuş bir satır yüzünden tüm snippet listesi kaybolmamalı.
    /// </summary>
    private static MotorTuru? MotorCoz(string? ad)
        => Enum.TryParse(ad, out MotorTuru motor) ? motor : null;
}
