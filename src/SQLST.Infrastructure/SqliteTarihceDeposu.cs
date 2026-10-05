using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Yerel SQLite'ta nesne tarihçesi (V2-S8, Ö2). Aynı hash art arda sürüm açmaz.
/// V3: kayıtlar PROFİL kapsamlı (kullanıcı kuralı 2026-07-18) — aynı sunucuya bakan iki
/// profil birbirinin sürüm zincirini görmez ve bozmaz.
/// </summary>
public sealed class SqliteTarihceDeposu : ITarihceDeposu
{
    private readonly YerelDepo _depo;

    public SqliteTarihceDeposu(YerelDepo depo) => _depo = depo;

    public async Task<bool> EkleAsync(TarihceKaydi kayit, CancellationToken ct = default)
    {
        TarihceKaydi? son = await SonAsync(
            kayit.ProfilId, kayit.Sunucu, kayit.Veritabani, kayit.Sema, kayit.Ad, ct);
        if (son is not null && son.IcerikHash == kayit.IcerikHash)
            return false; // içerik değişmedi — sürüm gürültüsü yapma

        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO NesneTarihcesi (ProfilId, Sunucu, Veritabani, Sema, Ad, IcerikHash, Tanim, GorulmeUtc, Kaynak)
            VALUES ($profil, $sunucu, $vt, $sema, $ad, $hash, $tanim, $zaman, $kaynak);
            """;
        komut.Parameters.AddWithValue("$profil", (object?)kayit.ProfilId?.ToString() ?? DBNull.Value);
        komut.Parameters.AddWithValue("$sunucu", kayit.Sunucu);
        komut.Parameters.AddWithValue("$vt", kayit.Veritabani);
        komut.Parameters.AddWithValue("$sema", kayit.Sema);
        komut.Parameters.AddWithValue("$ad", kayit.Ad);
        komut.Parameters.AddWithValue("$hash", kayit.IcerikHash);
        komut.Parameters.AddWithValue("$tanim", kayit.Tanim);
        komut.Parameters.AddWithValue("$zaman", kayit.GorulmeUtc.ToString("o", CultureInfo.InvariantCulture));
        komut.Parameters.AddWithValue("$kaynak", kayit.Kaynak);
        await komut.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<TarihceKaydi>> ListeAsync(
        Guid? profilId, string sunucu, string veritabani, string sema, string ad,
        int limit = 50, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = NesneSorgusu(baglanti, profilId, sunucu, veritabani, sema, ad, limit);
        var kayitlar = new List<TarihceKaydi>();
        await using SqliteDataReader oku = await komut.ExecuteReaderAsync(ct);
        while (await oku.ReadAsync(ct))
            kayitlar.Add(Oku(oku));
        return kayitlar;
    }

    public async Task<TarihceKaydi?> SonAsync(
        Guid? profilId, string sunucu, string veritabani, string sema, string ad, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = NesneSorgusu(baglanti, profilId, sunucu, veritabani, sema, ad, limit: 1);
        await using SqliteDataReader oku = await komut.ExecuteReaderAsync(ct);
        return await oku.ReadAsync(ct) ? Oku(oku) : null;
    }

    private static SqliteCommand NesneSorgusu(
        SqliteConnection baglanti, Guid? profilId,
        string sunucu, string veritabani, string sema, string ad, int limit)
    {
        SqliteCommand komut = baglanti.CreateCommand();
        // ProfilId IS $profil: null da doğru eşleşir (v2'den kalan profilsiz kayıtlar kendi kovasında).
        komut.CommandText = """
            SELECT Id, ProfilId, Sunucu, Veritabani, Sema, Ad, IcerikHash, Tanim, GorulmeUtc, Kaynak
            FROM NesneTarihcesi
            WHERE ProfilId IS $profil
              AND Sunucu = $sunucu AND Veritabani = $vt AND Sema = $sema AND Ad = $ad
            ORDER BY Id DESC LIMIT $limit;
            """;
        komut.Parameters.AddWithValue("$profil", (object?)profilId?.ToString() ?? DBNull.Value);
        komut.Parameters.AddWithValue("$sunucu", sunucu);
        komut.Parameters.AddWithValue("$vt", veritabani);
        komut.Parameters.AddWithValue("$sema", sema);
        komut.Parameters.AddWithValue("$ad", ad);
        komut.Parameters.AddWithValue("$limit", limit);
        return komut;
    }

    private static TarihceKaydi Oku(SqliteDataReader oku) => new()
    {
        Id = oku.GetInt64(0),
        ProfilId = oku.IsDBNull(1) ? null : Guid.Parse(oku.GetString(1)),
        Sunucu = oku.GetString(2),
        Veritabani = oku.GetString(3),
        Sema = oku.GetString(4),
        Ad = oku.GetString(5),
        IcerikHash = oku.GetString(6),
        Tanim = oku.GetString(7),
        GorulmeUtc = DateTime.Parse(oku.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Kaynak = oku.GetString(9),
    };
}
