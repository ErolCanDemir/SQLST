using System.Globalization;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// 🔎 Veri Arama çekirdeği (kullanıcı onayı 2026-08-03, öneri 1): verilen DEĞERİ tablonun uygun
/// tipli kolonlarında arayan sorguyu üretir. SAF ve testli; koşum pencerededir (tablo tablo,
/// ilerlemeli, durdurulabilir). "Kod Arama"dan farkı: nesne TANIMI değil tablo VERİSİ taranır.
/// v1: tam eşitlik · 100 satır/tablo · MSSQL. Özellik eşitliği (2026-08-03 devamı): ILehce ile
/// motor-parametrik — tırnaklama/limit/literal lehçeden gelir, PG/MySQL de destekli.
/// Değer sayıya çözülüyorsa sayısal+metin kolonlar, değilse yalnız metin kolonları taranır
/// (AllowThousands bilerek KAPALI — "3,14"→314 dersi).
/// </summary>
public static class VeriArayici
{
    // Tip adları BİRLEŞİK kümedir (dört motor): bir ad hangi motorda geçerse geçsin aynı sınıftadır
    // ("text" hepsinde metin, "real" hepsinde sayısal) — MotorId dallanması gerekmez.
    // Oracle CLOB/NCLOB BİLEREK dışarıda: '=' kıyası ORA-00932 verir (LOB eşitlikle aranamaz).
    private static readonly HashSet<string> MetinTipleri = new(StringComparer.OrdinalIgnoreCase)
    {
        // MSSQL
        "char", "varchar", "nchar", "nvarchar", "text", "ntext",
        // PostgreSQL (information_schema/udt adları)
        "character varying", "character", "bpchar", "citext",
        // MySQL/MariaDB
        "tinytext", "mediumtext", "longtext",
        // Oracle (madde 3, 2026-08-03)
        "varchar2", "nvarchar2",
    };

    private static readonly HashSet<string> SayisalTipler = new(StringComparer.OrdinalIgnoreCase)
    {
        // MSSQL
        "int", "bigint", "smallint", "tinyint",
        "decimal", "numeric", "money", "smallmoney", "float", "real",
        // PostgreSQL
        "integer", "int2", "int4", "int8", "double precision", "float4", "float8", "serial", "bigserial",
        // MySQL/MariaDB
        "mediumint", "double",
        // Oracle (madde 3, 2026-08-03)
        "number", "binary_float", "binary_double",
    };

    /// <summary>
    /// Tablo için arama sorgusu; uygun kolon yoksa null. İlk kolon "Tablo" etiketidir.
    ///
    /// <b>S3 — FTS entegrasyonu (v23, K3 kararı; yalnız MSSQL çağıranı doldurur):</b>
    /// <paramref name="ftsKolonlar"/> verilirse o metin kolonları <c>=</c> yerine TEK bir
    /// <c>CONTAINS</c> koşulunda aranır — hem kelime index'inden gider (LIKE/'=' tam taramasına
    /// girmeden) hem de <c>=</c>'in ASLA bulamayacağı "uzun metnin İÇİNDE geçiyor" eşleşmelerini
    /// yakalar (nvarchar(max) açıklama/not kolonları). FTS'siz metin kolonları <c>=</c> ile,
    /// sayısallar aynı — davranış birleşimi tek sorguda OR'lanır.
    /// </summary>
    public static string? SorguUret(
        SemaNesnesi tablo, string deger, ILehce lehce, int tavan = 100,
        IReadOnlyCollection<string>? ftsKolonlar = null)
    {
        bool sayiMi = decimal.TryParse(deger, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal sayi);
        LiteralKurallari kurallar = lehce.LiteralKurallari;

        var kosullar = new List<string>();
        var ftsHedefler = new List<string>();
        foreach (SemaKolonu k in tablo.Kolonlar)
        {
            string tip = k.Tip.Split('(')[0].Trim();

            if (MetinTipleri.Contains(tip))
            {
                if (ftsKolonlar?.Contains(k.Ad) == true)
                    ftsHedefler.Add(lehce.TirnaklaTanimlayici(k.Ad));
                else
                    kosullar.Add($"{lehce.TirnaklaTanimlayici(k.Ad)} = {LiteralYazici.Yaz(deger, kurallar)}");
            }
            else if (SayisalTipler.Contains(tip) && sayiMi)
            {
                kosullar.Add($"{lehce.TirnaklaTanimlayici(k.Ad)} = {sayi.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (ftsHedefler.Count > 0)
        {
            // Deyim/çift tırnak FTS kaçışı + tek tırnak SQL kaçışı (FtsSorgulari ile aynı kural).
            string terim = "\"" + deger.Replace("\"", "\"\"") + "\"";
            string hedef = ftsHedefler.Count == 1 ? ftsHedefler[0] : $"({string.Join(", ", ftsHedefler)})";
            kosullar.Insert(0, $"CONTAINS({hedef}, N'{terim.Replace("'", "''")}')");
        }

        if (kosullar.Count == 0)
            return null;
        string etiket = LiteralYazici.Yaz($"{tablo.Sema}.{tablo.Ad}", kurallar);
        return $"SELECT {lehce.SatirSinirBasi(tavan)}{etiket} AS {lehce.TirnaklaTanimlayici("Tablo")}, * "
             + $"FROM {lehce.TamAdYaz(tablo.Sema, tablo.Ad)} "
             + $"WHERE {string.Join(" OR ", kosullar)}{lehce.SatirSinirSonu(tavan)};";
    }
}
