using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Geri Al paketinden TERS DML üretir (V15-S4, BF-1): UPDATE paketi → PK ile eşleşen eski
/// değerleri geri yazan UPDATE'ler; DELETE paketi → silinen satırları yeniden ekleyen INSERT'ler.
/// SALT METİN üretir — çalıştırmaz; kullanıcı sekmede görür, Güvenli Yazma raylarında koşar.
///
/// PK ŞARTI (BF-1 kararı): ters UPDATE satırı PK ile hedefler. PK yoksa üretim REDDEDİLİR
/// (eski hal yalnız görüntülenir) — PK'sız tuple eşleşmesi yanlış satırı güncelleyebilirdi.
/// DELETE paketinde PK gerekmez (INSERT tüm kolonları yazar).
/// </summary>
public static class TersDmlUretici
{
    /// <summary>Tırnak İSTEMEYEN (sayısal/mantıksal) SQL tipleri — gerisi N'…' ile kaçışlanır.</summary>
    private static readonly HashSet<string> TirnaksizTipler = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint", "bit",
        "decimal", "numeric", "float", "real", "money", "smallmoney",
    };

    /// <summary>Paket hücresi Base64 taşır (GeriAlSerilestirici.HucreYaz) → 0x… ikili literali üretilir.</summary>
    private static readonly HashSet<string> IkiliTipler = new(StringComparer.OrdinalIgnoreCase)
    {
        "binary", "varbinary", "image", "rowversion", "timestamp",
    };

    /// <summary>DateTime "O" biçiminde (7 hane) saklanır; düz datetime'a N'…' ataması bu hassasiyette
    /// PATLAR ("Conversion failed") — CAST(datetime2) köprüsüyle her tarih tipine örtük iner.</summary>
    private static readonly HashSet<string> TarihTipler = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime", "datetime2", "smalldatetime", "date",
    };

    public static (string? Script, string? Hata) Uret(GeriAlPaketi paket)
    {
        IReadOnlyList<GeriAlSerilestirici.KolonTanimi> kolonlar = GeriAlSerilestirici.KolonlariOku(paket);
        IReadOnlyList<string?[]> satirlar = GeriAlSerilestirici.SatirlariOku(paket);
        if (kolonlar.Count == 0 || satirlar.Count == 0)
            return (null, "Bu pakette geri yazılacak satır yok.");

        return paket.Fiil.ToUpperInvariant() switch
        {
            "DELETE" => (InsertUret(paket.Tablo, kolonlar, satirlar), null),
            "UPDATE" => UpdateUret(paket, kolonlar, satirlar),
            _ => (null, $"'{paket.Fiil}' paketinden ters DML üretilemez."),
        };
    }

    private static string InsertUret(
        string tablo, IReadOnlyList<GeriAlSerilestirici.KolonTanimi> kolonlar, IReadOnlyList<string?[]> satirlar)
    {
        string kolonListesi = string.Join(", ", kolonlar.Select(k => $"[{k.Ad}]"));
        var sb = new StringBuilder();
        sb.AppendLine($"-- ⏪ Geri Al: {satirlar.Count} silinen satır yeniden ekleniyor ({tablo}).");
        sb.AppendLine("-- İnceleyip Güvenli Yazma ile çalıştırın. IDENTITY kolonu varsa SET IDENTITY_INSERT gerekebilir.");
        foreach (string?[] satir in satirlar)
        {
            string degerler = string.Join(", ", kolonlar.Select((k, i) => Literal(satir[i], k.Tip)));
            sb.AppendLine($"INSERT INTO {tablo} ({kolonListesi}) VALUES ({degerler});");
        }
        return sb.ToString().TrimEnd();
    }

    private static (string?, string?) UpdateUret(
        GeriAlPaketi paket, IReadOnlyList<GeriAlSerilestirici.KolonTanimi> kolonlar, IReadOnlyList<string?[]> satirlar)
    {
        IReadOnlyList<string> pk = GeriAlSerilestirici.PkOku(paket);
        if (pk.Count == 0)
            return (null, "Bu tabloda birincil anahtar yok — ters UPDATE güvenle üretilemez "
                + "(hangi satırın güncelleneceği belirsiz). Eski değerleri yukarıdaki tablodan "
                + "elle kullanabilirsiniz.");

        var pkKume = new HashSet<string>(pk, StringComparer.OrdinalIgnoreCase);
        int[] pkIndeks = [.. pk.Select(a => KolonIndeksi(kolonlar, a))];
        if (pkIndeks.Any(i => i < 0))
            return (null, "Birincil anahtar kolonları pakette bulunamadı — ters UPDATE üretilemez.");

        var sb = new StringBuilder();
        sb.AppendLine($"-- ⏪ Geri Al: {satirlar.Count} satır eski değerlerine döndürülüyor ({paket.Tablo}).");
        sb.AppendLine("-- İnceleyip Güvenli Yazma ile çalıştırın.");
        foreach (string?[] satir in satirlar)
        {
            string set = string.Join(", ", kolonlar
                .Select((k, i) => (k, i))
                .Where(x => !pkKume.Contains(x.k.Ad))
                .Select(x => $"[{x.k.Ad}] = {Literal(satir[x.i], x.k.Tip)}"));
            string where = string.Join(" AND ", pk.Select((a, j) =>
                $"[{a}] = {Literal(satir[pkIndeks[j]], kolonlar[pkIndeks[j]].Tip)}"));

            // SET boş olabilir (tabloda PK dışı kolon yoksa) — o satırın zaten geri yazacak bir şeyi yok.
            if (set.Length == 0)
                continue;
            sb.AppendLine($"UPDATE {paket.Tablo} SET {set} WHERE {where};");
        }
        return (sb.ToString().TrimEnd(), null);
    }

    private static int KolonIndeksi(IReadOnlyList<GeriAlSerilestirici.KolonTanimi> kolonlar, string ad)
    {
        for (int i = 0; i < kolonlar.Count; i++)
        {
            if (string.Equals(kolonlar[i].Ad, ad, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Kültürden bağımsız string temsili → T-SQL literali. NULL → NULL; sayısal tırnaksız;
    /// ikili 0x…; tarih CAST köprüsü; gerisi N'…' (inceleme 2026-07-30 bekleyeni: binary/datetime).</summary>
    private static string Literal(string? deger, string tip)
    {
        if (deger is null)
            return "NULL";
        if (TirnaksizTipler.Contains(tip))
            return deger; // COUNT/decimal/bit — invariant metin zaten geçerli literal

        if (IkiliTipler.Contains(tip))
        {
            try
            {
                byte[] ham = Convert.FromBase64String(deger);
                return ham.Length == 0 ? "0x" : "0x" + Convert.ToHexString(ham);
            }
            catch (FormatException)
            {
                // Base64 değilse (eski/elle bozulmuş paket) metin olarak bırakmak sessiz veri
                // bozar — script'e açık uyarı düşür.
                return $"NULL /* ⚠ ikili değer çözülemedi: {deger.Replace("*/", "*\\/")} */";
            }
        }

        string kacisli = "N'" + deger.Replace("'", "''") + "'";
        // ISO-8601 'T' biçimi DATEFORMAT'tan bağımsızdır; datetime2 köprüsü 7 haneli kesri taşır
        // ve hedef kolona (datetime/smalldatetime/date) örtük dönüşümle iner.
        return TarihTipler.Contains(tip) ? $"CAST({kacisli} AS datetime2)" : kacisli;
    }
}
