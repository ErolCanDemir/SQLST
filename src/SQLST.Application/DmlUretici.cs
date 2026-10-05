using System.Data;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Edit modu DML üreticisi (V2-S5, FG-4.9): DataTable'ın satır durumlarından
/// (Added/Modified/Deleted) INSERT/UPDATE/DELETE script'leri üretir — 07-r2 §4:
/// satır kimliği PK (+ varsa sürüm kolonu) ile; sürüm kolonu yoksa DEĞİŞEN kolonların
/// eski değerleri iyimser kıyas olarak WHERE'e eklenir (kırılgan tipler hariç);
/// NULL kıyası daima IS NULL; identity/computed/sürüm kolonu SET/INSERT'e girmez.
/// Sıra: DELETE → UPDATE → INSERT (silinen PK yeniden eklenebilsin).
///
/// V4-S1: motor bazlıdır — tırnaklama, literal biçimi, ifade sonlandırıcı ve
/// "hepsi varsayılan" satır ekleme <see cref="ILehce"/>'den gelir. Üretilen metin
/// SQL Server, PostgreSQL, MySQL/MariaDB ve Oracle için ayrı ayrı doğru olmalıdır.
/// </summary>
public static class DmlUretici
{
    public static IReadOnlyList<string> Uret(ILehce lehce, DuzenlemeMetasi meta, DataTable tablo)
    {
        if (!meta.DuzenlenebilirMi)
            throw new InvalidOperationException($"{meta.TamAd}: PK yok — düzenleme kapalı (07-r2 §4).");

        string tamAd = lehce.TamAdYaz(meta.Sema, meta.Tablo);
        string son = lehce.IfadeSonu;

        var silmeler = new List<string>();
        var guncellemeler = new List<string>();
        var eklemeler = new List<string>();

        foreach (DataRow satir in tablo.Rows)
        {
            switch (satir.RowState)
            {
                case DataRowState.Deleted:
                    silmeler.Add($"DELETE FROM {tamAd} WHERE {Kimlik(lehce, meta, satir)}{son}");
                    break;
                case DataRowState.Modified when Guncelleme(lehce, meta, satir, tamAd) is { } g:
                    guncellemeler.Add(g + son);
                    break;
                case DataRowState.Added:
                    eklemeler.Add(Ekleme(lehce, meta, satir, tamAd) + son);
                    break;
            }
        }

        return [.. silmeler, .. guncellemeler, .. eklemeler];
    }

    /// <summary>Önizleme metni ("Show Script") — komutlar satır satır.</summary>
    public static string Onizle(IReadOnlyList<string> komutlar)
        => string.Join(Environment.NewLine, komutlar);

    private static string? Guncelleme(ILehce lehce, DuzenlemeMetasi meta, DataRow satir, string tamAd)
    {
        List<DuzenlemeKolonu> degisenler = [.. meta.Kolonlar.Where(k =>
            k.Yazilabilir && satir.Table.Columns.Contains(k.Ad)
            && !DegerEsit(satir[k.Ad, DataRowVersion.Original], satir[k.Ad, DataRowVersion.Current]))];
        if (degisenler.Count == 0)
            return null; // gerçek değişiklik yok (ör. aynı değer yeniden yazıldı)

        var sb = new StringBuilder();
        sb.Append($"UPDATE {tamAd} SET ");
        sb.AppendJoin(", ", degisenler.Select(k =>
            $"{Ad(lehce, k)} = {Literal(lehce, satir[k.Ad, DataRowVersion.Current])}"));
        sb.Append($" WHERE {Kimlik(lehce, meta, satir)}");

        // Sürüm kolonu yoksa: değişen kolonların ESKİ değerleri iyimser kıyas (07-r2 §4);
        // kırılgan tipler (float/max/xml…) kıyasa girmez, PK yine de kimliği taşır.
        if (meta.Rowversion is null)
        {
            foreach (DuzenlemeKolonu k in degisenler.Where(k => k.KiyasGuvenliMi))
                sb.Append($" AND {EskiKiyas(lehce, k, satir[k.Ad, DataRowVersion.Original])}");
        }

        return sb.ToString();
    }

    private static string Ekleme(ILehce lehce, DuzenlemeMetasi meta, DataRow satir, string tamAd)
    {
        // DBNull bırakılan kolonlar INSERT'e girmez — sunucu DEFAULT'u işlesin;
        // NOT NULL + default'suz kolon sunucudan net hata alır (dürüst davranış).
        List<DuzenlemeKolonu> dolular = [.. meta.Kolonlar.Where(k =>
            k.Yazilabilir && satir.Table.Columns.Contains(k.Ad)
            && satir[k.Ad] is not DBNull)];
        if (dolular.Count == 0)
            return lehce.BosSatirEkleSql(tamAd); // Oracle'da NotSupportedException — açık mesaj

        return $"INSERT INTO {tamAd} ("
             + string.Join(", ", dolular.Select(k => Ad(lehce, k)))
             + ") VALUES ("
             + string.Join(", ", dolular.Select(k => Literal(lehce, satir[k.Ad])))
             + ")";
    }

    /// <summary>Satır kimliği: PK kolonları (+ varsa sürüm kolonu) — hep ESKİ (Original) değerlerle.</summary>
    private static string Kimlik(ILehce lehce, DuzenlemeMetasi meta, DataRow satir)
    {
        DataRowVersion surum = satir.RowState == DataRowState.Added
            ? DataRowVersion.Current
            : DataRowVersion.Original;

        IEnumerable<string> parcalar = meta.PkKolonlari
            .Select(k => EskiKiyas(lehce, k, satir[k.Ad, surum]));

        if (meta.Rowversion is { } rv && satir.Table.Columns.Contains(rv.Ad))
            parcalar = parcalar.Append(EskiKiyas(lehce, rv, satir[rv.Ad, surum]));

        return string.Join(" AND ", parcalar);
    }

    /// <summary>Kıyas parçası: NULL daima IS NULL ('= NULL' sessizce 0 satır — 07-r2 §4).</summary>
    private static string EskiKiyas(ILehce lehce, DuzenlemeKolonu kolon, object? deger)
        => deger is null or DBNull
            ? $"{Ad(lehce, kolon)} IS NULL"
            : $"{Ad(lehce, kolon)} = {Literal(lehce, deger)}";

    private static string Ad(ILehce lehce, DuzenlemeKolonu kolon)
        => lehce.TirnaklaTanimlayici(kolon.Ad);

    private static string Literal(ILehce lehce, object? deger)
        => LiteralYazici.Yaz(deger, lehce.LiteralKurallari);

    private static bool DegerEsit(object? a, object? b)
    {
        if (a is DBNull) a = null;
        if (b is DBNull) b = null;
        if (a is byte[] ba && b is byte[] bb)
            return ba.AsSpan().SequenceEqual(bb);
        return Equals(a, b);
    }
}
