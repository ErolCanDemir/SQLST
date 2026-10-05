using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Yerel SQLite'ta anahtar-değer ayar deposu (V2-S1). Değerler invariant serileşir.
/// V3: ayarlar PROFİL kapsamlı (kullanıcı kuralı 2026-07-18). Satır anahtarı
/// (ProfilId, Anahtar); ProfilId '' = GENEL. Okuma önce profile, yoksa GENEL'e bakar —
/// böylece v2'den gelen tercihler tüm profillerde varsayılan olarak sürer, profil kendi
/// değerini yazdığı anda yalnız kendisi için ezer.
/// </summary>
public sealed class SqliteAyarDeposu : IAyarDeposu
{
    private readonly YerelDepo _depo;

    public SqliteAyarDeposu(YerelDepo depo) => _depo = depo;

    private static string Kapsam(Guid? profilId) => profilId?.ToString() ?? "";

    public async Task<string?> OkuAsync(string anahtar, Guid? profilId = null, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        // Profil değeri varsa o, yoksa GENEL değer (ProfilId = '') — sıralama bunu sağlar.
        komut.CommandText = """
            SELECT Deger FROM Ayar
            WHERE Anahtar = $a AND ProfilId IN ($p, '')
            ORDER BY CASE WHEN ProfilId = $p THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        komut.Parameters.AddWithValue("$a", anahtar);
        komut.Parameters.AddWithValue("$p", Kapsam(profilId));
        object? deger = await komut.ExecuteScalarAsync(ct);
        return deger as string;
    }

    public async Task YazAsync(string anahtar, string deger, Guid? profilId = null, CancellationToken ct = default)
    {
        await using SqliteConnection baglanti = _depo.BaglantiAc();
        await using SqliteCommand komut = baglanti.CreateCommand();
        komut.CommandText = """
            INSERT INTO Ayar (ProfilId, Anahtar, Deger) VALUES ($p, $a, $d)
            ON CONFLICT(ProfilId, Anahtar) DO UPDATE SET Deger = excluded.Deger;
            """;
        komut.Parameters.AddWithValue("$p", Kapsam(profilId));
        komut.Parameters.AddWithValue("$a", anahtar);
        komut.Parameters.AddWithValue("$d", deger);
        await komut.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> IntOkuAsync(
        string anahtar, int varsayilan, Guid? profilId = null, CancellationToken ct = default)
    {
        string? ham = await OkuAsync(anahtar, profilId, ct);
        return int.TryParse(ham, NumberStyles.Integer, CultureInfo.InvariantCulture, out int deger)
            ? deger
            : varsayilan;
    }

    public async Task<bool> BoolOkuAsync(
        string anahtar, bool varsayilan, Guid? profilId = null, CancellationToken ct = default)
    {
        string? ham = await OkuAsync(anahtar, profilId, ct);
        return ham is null ? varsayilan : ham == "1" || string.Equals(ham, "true", StringComparison.OrdinalIgnoreCase);
    }
}
