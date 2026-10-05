using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Yerel SQLite'ta sorgu geçmişi (V2-S2, FG-3.7). Zaman UTC "o" biçiminde saklanır.
/// V3: kayıtlar bağlantı PROFİLİNE bağlıdır — her profil kendi geçmişini görür ve
/// budama profil başına işler (kullanıcı isteği 2026-07-18).
/// </summary>
public sealed class SqliteSorguGecmisiDeposu : ISorguGecmisiDeposu
{
    private readonly YerelDepo _depo;

    public SqliteSorguGecmisiDeposu(YerelDepo depo) => _depo = depo;

    public async Task EkleAsync(GecmisKaydi kayit, int enCok = 1000, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        // Budama profil bazlı: yoğun kullanılan bir profil, başka profilin geçmişini süpürmez.
        komut.CommandText = """
            INSERT INTO SorguGecmisi (ProfilId, Sunucu, Veritabani, Sql, BaslangicUtc, SureMs, SatirSayisi, Durum, HataMesaji, Kullanici, SekmeAdi)
            VALUES ($profil, $sunucu, $vt, $sql, $baslangic, $sure, $satir, $durum, $hata, $kullanici, $sekmeAdi);
            DELETE FROM SorguGecmisi
            WHERE Id NOT IN (
                SELECT Id FROM SorguGecmisi
                WHERE ProfilId IS $profil
                ORDER BY Id DESC LIMIT $enCok)
              AND ProfilId IS $profil;
            """;
        komut.Parameters.AddWithValue("$profil", (object?)kayit.ProfilId?.ToString() ?? DBNull.Value);
        komut.Parameters.AddWithValue("$sunucu", kayit.Sunucu);
        komut.Parameters.AddWithValue("$vt", (object?)kayit.Veritabani ?? DBNull.Value);
        komut.Parameters.AddWithValue("$kullanici", (object?)kayit.Kullanici ?? DBNull.Value);
        komut.Parameters.AddWithValue("$sekmeAdi", (object?)kayit.SekmeAdi ?? DBNull.Value);
        komut.Parameters.AddWithValue("$sql", kayit.Sql);
        komut.Parameters.AddWithValue("$baslangic", kayit.BaslangicUtc.ToString("o", CultureInfo.InvariantCulture));
        komut.Parameters.AddWithValue("$sure", kayit.SureMs);
        komut.Parameters.AddWithValue("$satir", kayit.SatirSayisi);
        komut.Parameters.AddWithValue("$durum", kayit.Durum.ToString());
        komut.Parameters.AddWithValue("$hata", (object?)kayit.HataMesaji ?? DBNull.Value);
        komut.Parameters.AddWithValue("$enCok", enCok);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<GecmisKaydi>> AraAsync(
        string? metin, int limit = 200, Guid? profilId = null, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        // ProfilId IS $profil: profil KAPSAMI zorunlu — "null = tüm profiller" gibi tehlikeli
        // bir kaçış yok (V3 denetimi). null geçilirse yalnız profilsiz (v2) kayıtlar döner.
        komut.CommandText = """
            SELECT Id, ProfilId, Sunucu, Veritabani, Sql, BaslangicUtc, SureMs, SatirSayisi, Durum, HataMesaji, Kullanici, SekmeAdi
            FROM SorguGecmisi
            WHERE ($metin IS NULL OR Sql LIKE '%' || $metin || '%' COLLATE NOCASE)
              AND ProfilId IS $profil
            ORDER BY Id DESC LIMIT $limit;
            """;
        komut.Parameters.AddWithValue("$metin",
            string.IsNullOrWhiteSpace(metin) ? DBNull.Value : metin.Trim());
        komut.Parameters.AddWithValue("$profil", (object?)profilId?.ToString() ?? DBNull.Value);
        komut.Parameters.AddWithValue("$limit", limit);

        var kayitlar = new List<GecmisKaydi>();
        await using SqliteDataReader oku = await komut.ExecuteReaderAsync(ct);
        while (await oku.ReadAsync(ct))
        {
            kayitlar.Add(new GecmisKaydi
            {
                Id = oku.GetInt64(0),
                ProfilId = oku.IsDBNull(1) ? null : Guid.Parse(oku.GetString(1)),
                Sunucu = oku.GetString(2),
                Veritabani = oku.IsDBNull(3) ? null : oku.GetString(3),
                Sql = oku.GetString(4),
                BaslangicUtc = DateTime.Parse(oku.GetString(5), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                SureMs = oku.GetInt32(6),
                SatirSayisi = oku.GetInt32(7),
                Durum = Enum.TryParse(oku.GetString(8), out GecmisDurumu durum) ? durum : GecmisDurumu.Hata,
                HataMesaji = oku.IsDBNull(9) ? null : oku.GetString(9),
                Kullanici = oku.IsDBNull(10) ? null : oku.GetString(10),
                SekmeAdi = oku.IsDBNull(11) ? null : oku.GetString(11),
            });
        }
        return kayitlar;
    }

    public async Task TemizleAsync(Guid? profilId = null, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        // Yalnız verilen profilin kayıtları silinir (null → profilsiz v2 kayıtları).
        komut.CommandText = "DELETE FROM SorguGecmisi WHERE ProfilId IS $profil;";
        komut.Parameters.AddWithValue("$profil", (object?)profilId?.ToString() ?? DBNull.Value);
        await komut.ExecuteNonQueryAsync(ct);
    }
}
