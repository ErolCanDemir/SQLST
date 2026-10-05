using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Nesne script'leri (S5). Saf metin işlemleri — UI'sız test edilir.
/// Nesne adları daima <see cref="SemaNesnesi.TamAdKoseli"/> ile parantezlenir.
/// </summary>
public static class NesneScriptleyici
{
    /// <summary>
    /// Gövdedeki ilk anlamlı anahtar sözcük CREATE ise ALTER'a çevirir (FG-5.1).
    /// Baştaki yorum/boşluklara duyarlıdır; gövdenin geri kalanına dokunmaz.
    /// CREATE bulunamazsa metin olduğu gibi döner (çağıran karar verir).
    /// </summary>
    public static string AlterEDonustur(string tanim)
    {
        int i = IlkTokenIndeksi(tanim);
        if (i < 0 || !tanim.AsSpan(i).StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
            return tanim;

        int son = i + "CREATE".Length;
        if (son < tanim.Length && (char.IsLetterOrDigit(tanim[son]) || tanim[son] == '_'))
            return tanim; // "CREATEX" gibi — anahtar sözcük değil

        return string.Concat(tanim.AsSpan(0, i), "ALTER", tanim.AsSpan(son));
    }

    /// <summary>
    /// Tablo/view için hazır SELECT (FG-2.6). NOLOCK gömülmez — o ayrı bir seçenektir (FG-6.4).
    /// Koleksiyonda (V3-S2) SQL değil Mongo find JSON'u üretilir — sorgu metni opaktır,
    /// motor yorumlar (08-v3r1 §3).
    /// </summary>
    public static string SelectScripti(SemaNesnesi nesne, int n)
        => nesne.Tur == SemaNesneTuru.Koleksiyon
            ? $$"""{ "find": "{{nesne.Ad.Replace("\\", "\\\\").Replace("\"", "\\\"")}}", "limit": {{n}} }"""
            : $"SELECT TOP {n} * FROM {nesne.TamAdKoseli};";

    /// <summary>
    /// SP için parametreleri yorumlu EXEC iskeleti (FG-5.6):
    /// değerler NULL bırakılır, tip ve OUTPUT bilgisi satır sonunda yorum olarak durur.
    /// </summary>
    public static string ExecIskeleti(SemaNesnesi sp)
    {
        if (sp.Parametreler.Count == 0)
            return $"EXEC {sp.TamAdKoseli};";

        var sb = new StringBuilder();
        sb.AppendLine($"EXEC {sp.TamAdKoseli}");

        int hiza = sp.Parametreler.Max(p => p.Ad.Length);
        for (int i = 0; i < sp.Parametreler.Count; i++)
        {
            SemaParametresi p = sp.Parametreler[i];
            bool sonuncu = i == sp.Parametreler.Count - 1;
            string deger = p.CikisMi ? "NULL OUTPUT" : "NULL";
            sb.AppendLine($"     {p.Ad.PadRight(hiza)} = {deger}{(sonuncu ? ";" : ",")}   -- {p.Tip}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Tırnak İSTEMEYEN (sayısal/mantıksal) T-SQL tipleri — gerisi N'…' ile kaçışlanır.</summary>
    private static readonly HashSet<string> TirnaksizTipler = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint", "bit",
        "decimal", "numeric", "float", "real", "money", "smallmoney",
    };

    /// <summary>
    /// Parametre değerleri DOLDURULMUŞ EXEC üretir (kullanıcı isteği 2026-07-27: SSMS gibi bir
    /// parametre penceresi → değerleri gir → çalıştır). <paramref name="degerler"/> parametre adı →
    /// kullanıcının girdiği metin; boş/null verilen giriş parametresi <c>NULL</c> olur. Değerler
    /// tip-duyarlı sarılır (sayısal tırnaksız, gerisi N'…' — <c>'</c> kaçışlı). OUTPUT parametreleri
    /// için değişken bildirilir, <c>OUTPUT</c> ile geçilir ve sonda <c>SELECT</c> ile geri okunur.
    /// Üretilen metin ÇALIŞTIRILMAZ — sekmede açılır, kullanıcı görür ve F5 der.
    /// </summary>
    public static string ExecDolu(SemaNesnesi sp, IReadOnlyDictionary<string, string?> degerler)
    {
        if (sp.Parametreler.Count == 0)
            return $"EXEC {sp.TamAdKoseli};";

        var oncesi = new StringBuilder();   // OUTPUT değişken bildirimleri
        var sonrasi = new StringBuilder();  // OUTPUT değişkenlerini gösteren SELECT
        var satirlar = new List<string>();
        var cikislar = new List<string>();
        int hiza = sp.Parametreler.Max(p => p.Ad.Length);

        foreach (SemaParametresi p in sp.Parametreler)
        {
            if (p.CikisMi)
            {
                string degisken = "@out_" + p.Ad.TrimStart('@');
                oncesi.AppendLine($"DECLARE {degisken} {p.Tip};");
                satirlar.Add($"     {p.Ad.PadRight(hiza)} = {degisken} OUTPUT");
                cikislar.Add($"{degisken} AS [{p.Ad.TrimStart('@')}]");
            }
            else
            {
                degerler.TryGetValue(p.Ad, out string? girilen);
                satirlar.Add($"     {p.Ad.PadRight(hiza)} = {Literal(girilen, p.Tip)}");
            }
        }

        var sb = new StringBuilder();
        if (oncesi.Length > 0)
            sb.Append(oncesi).AppendLine();
        sb.AppendLine($"EXEC {sp.TamAdKoseli}");
        sb.AppendLine(string.Join(",\n", satirlar) + ";");
        if (cikislar.Count > 0)
            sb.AppendLine().Append("SELECT ").Append(string.Join(", ", cikislar)).Append(';');
        return sb.ToString().TrimEnd();
    }

    /// <summary>Kullanıcı metni → tip-duyarlı T-SQL literali. Boş → NULL; sayısal tırnaksız; gerisi N'…'.</summary>
    private static string Literal(string? deger, string tip)
    {
        if (string.IsNullOrWhiteSpace(deger))
            return "NULL";
        string temelTip = tip.Split('(')[0].Trim(); // "nvarchar(50)" → "nvarchar"
        return TirnaksizTipler.Contains(temelTip) ? deger.Trim() : "N'" + deger.Replace("'", "''") + "'";
    }

    /// <summary>Kolon adlarını virgüllü liste olarak verir (FG-2.6 "kolonları kopyala").</summary>
    public static string KolonListesi(SemaNesnesi nesne)
        => string.Join(", ", nesne.Kolonlar.Select(k => k.Ad));

    /// <summary>
    /// Script as DROP + CREATE (FG-5.3): IF EXISTS'li DROP + GO + orijinal tanım.
    /// GO şart — CREATE PROC/VIEW/FN batch'in ilk statement'ı olmalı (07-r2 §5).
    /// </summary>
    public static string DropVeCreate(SemaNesnesi nesne, string tanim)
    {
        string tur = nesne.Tur switch
        {
            SemaNesneTuru.StoredProcedure => "PROCEDURE",
            SemaNesneTuru.View => "VIEW",
            SemaNesneTuru.Fonksiyon => "FUNCTION",
            _ => "TABLE",
        };
        return $"""
            DROP {tur} IF EXISTS {nesne.TamAdKoseli};
            GO
            {tanim.TrimStart()}
            """;
    }

    /// <summary>
    /// Tablo için CREATE TABLE script'i (FG-5.4): kolonlar + PK + nonclustered index'ler.
    /// IDENTITY(1,1) varsayımı yorumda belirtilir (seed/artış meta'da taşınmıyor).
    /// </summary>
    public static string CreateTableScripti(DuzenlemeMetasi meta, IReadOnlyList<MevcutIndex> indexler)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {meta.TamAdKoseli} (");

        var satirlar = new List<string>();
        foreach (DuzenlemeKolonu k in meta.Kolonlar)
        {
            string satir = $"    {k.AdKoseli} {k.GosterimTipi}"
                + (k.IdentityMi ? " IDENTITY(1,1)" : "")
                + (k.NullOlabilir ? " NULL" : " NOT NULL");
            if (k.IdentityMi)
                satir += " -- IDENTITY seed/artış (1,1) varsayıldı";
            satirlar.Add(satir);
        }

        if (meta.PkKolonlari.Count > 0)
        {
            satirlar.Add($"    CONSTRAINT [PK_{meta.Tablo.Replace("]", "]]")}] PRIMARY KEY "
                + $"({string.Join(", ", meta.PkKolonlari.Select(k => k.AdKoseli))})");
        }

        sb.AppendLine(string.Join("," + Environment.NewLine, satirlar));
        sb.AppendLine(");");

        foreach (MevcutIndex ix in indexler.Where(i =>
            i.Tablo.Equals(meta.TamAdKoseli, StringComparison.OrdinalIgnoreCase) && !i.PkMi))
        {
            string dahil = ix.IncludeKolonlar.Count > 0
                ? $" INCLUDE ({string.Join(", ", ix.IncludeKolonlar.Select(Koseli))})"
                : "";
            sb.AppendLine($"CREATE {(ix.UniqueMi ? "UNIQUE " : "")}NONCLUSTERED INDEX [{ix.Ad.Replace("]", "]]")}]");
            sb.AppendLine($"ON {meta.TamAdKoseli} ({string.Join(", ", ix.AnahtarKolonlar.Select(Koseli))}){dahil};");
        }

        return sb.ToString().TrimEnd();

        static string Koseli(string ad) => $"[{ad.Replace("]", "]]")}]";
    }

    /// <summary>
    /// #11 (kullanıcı isteği 2026-07-29): tablo için ÇOK SATIRLI, tip-farkında örnek INSERT.
    /// identity/computed/rowversion kolonları ATLANIR (<see cref="DuzenlemeKolonu.Yazilabilir"/> —
    /// bunlar sunucu malıdır, INSERT'e girmez). Değerler tipe göre çalıştırmaya yakın örneklerle dolar
    /// (metin <c>''</c>, sayı/bit <c>0</c>, tarih <c>GETDATE()</c>, guid <c>NEWID()</c>); kullanıcı
    /// yerlerine gerçek değerleri yazar. <paramref name="satirSayisi"/> hizalı VALUES satırı üretilir —
    /// tek satır isteyen fazlasını siler, daha çok isteyen son satırı kopyalar. Çalıştırılmaz.
    /// </summary>
    public static string InsertSablonu(DuzenlemeMetasi meta, int satirSayisi, ILehce lehce)
    {
        string tamAd = lehce.TamAdYaz(meta.Sema, meta.Tablo);
        List<DuzenlemeKolonu> kolonlar = [.. meta.Kolonlar.Where(k => k.Yazilabilir)];
        if (kolonlar.Count == 0)
            return $"-- {tamAd}: INSERT'e girebilecek kolon yok (hepsi identity/computed/rowversion).";

        string kolonListesi = string.Join(", ", kolonlar.Select(k => lehce.TirnaklaTanimlayici(k.Ad)));
        string satir = "    (" + string.Join(", ", kolonlar.Select(k => OrnekDeger(k.SysTip, lehce.MotorId))) + ")";

        var sb = new StringBuilder();
        sb.AppendLine($"INSERT INTO {tamAd} ({kolonListesi})");
        sb.AppendLine("VALUES");
        sb.Append(string.Join("," + Environment.NewLine, Enumerable.Repeat(satir, Math.Max(1, satirSayisi))));
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>v20-S21 saha m.29 ("INSERT örneğinin UPDATE eşi"): yazılabilir, anahtar-olmayan
    /// kolonlar SET satırlarında tip-farkında örnek değerlerle; WHERE PK üzerinden kurulur.
    /// PK yoksa güvenli varsayılan <c>WHERE 1 = 0</c> + uyarı — filtresiz UPDATE kazası şablondan
    /// çıkmaz (FG-6.2 sigortası ayrıca var ama şablon da güvenli doğar). Çalıştırılmaz.</summary>
    public static string UpdateSablonu(DuzenlemeMetasi meta, ILehce lehce)
    {
        string tamAd = lehce.TamAdYaz(meta.Sema, meta.Tablo);
        List<DuzenlemeKolonu> setler = [.. meta.Kolonlar.Where(k => k.Yazilabilir && !k.PkMi)];
        List<DuzenlemeKolonu> anahtarlar = [.. meta.Kolonlar.Where(k => k.PkMi)];
        if (setler.Count == 0)
            return $"-- {tamAd}: UPDATE edilebilecek kolon yok (hepsi anahtar/identity/computed/rowversion).";

        var sb = new StringBuilder();
        sb.AppendLine($"UPDATE {tamAd}");
        sb.AppendLine("SET");
        sb.AppendLine(string.Join("," + Environment.NewLine, setler.Select(k =>
            $"    {lehce.TirnaklaTanimlayici(k.Ad)} = {OrnekDeger(k.SysTip, lehce.MotorId)}")));
        sb.Append(anahtarlar.Count == 0
            ? "WHERE 1 = 0; -- ⚠ tabloda PK yok — koşulu KENDİN yaz (filtresiz UPDATE tüm tabloyu değiştirir)"
            : "WHERE " + string.Join(" AND ", anahtarlar.Select(k =>
                $"{lehce.TirnaklaTanimlayici(k.Ad)} = {OrnekDeger(k.SysTip, lehce.MotorId)}")) + ";");
        return sb.ToString();
    }

    /// <summary>
    /// Tipe göre çalıştırmaya yakın örnek değer (#11; özellik eşitliği 2026-08-03 — motor bazlı):
    /// sayısal/bit <c>0</c>, tarih/saat/guid motorun KENDİ fonksiyonu (GETDATE·now·NOW·SYSDATE /
    /// NEWID·gen_random_uuid·UUID; PG'de time kolonuna now() örtük İNMEZ → CURRENT_TIME/CURRENT_DATE),
    /// metin/xml/json <c>''</c>, PG boolean <c>FALSE</c>, gerisi <c>NULL</c>.
    /// Oracle notu (madde 3): DATE zaman da taşır → SYSDATE; TIMESTAMP → SYSTIMESTAMP; <c>''</c>
    /// Oracle'da NULL demektir — şablon değeri kullanıcı zaten değiştirir.
    /// </summary>
    private static string OrnekDeger(string sysTip, string motorId)
    {
        string t = sysTip.Split('(')[0].Trim().ToLowerInvariant();
        bool pg = motorId == "postgres";
        bool my = motorId == "mysql";
        bool ora = motorId == "oracle";
        return t switch
        {
            "int" or "bigint" or "smallint" or "tinyint" or "bit"
                or "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney"
                or "integer" or "int2" or "int4" or "int8" or "double precision" or "float4" or "float8"
                or "mediumint" or "double"
                or "number" or "binary_float" or "binary_double" => "0",
            "boolean" or "bool" => pg ? "FALSE" : "0",
            "date" => pg ? "CURRENT_DATE" : my ? "CURDATE()" : ora ? "SYSDATE" : "GETDATE()",
            "time" => pg ? "CURRENT_TIME" : my ? "CURTIME()" : "GETDATE()",
            "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset"
                or "timestamp" or "timestamptz" or "timestamp without time zone" or "timestamp with time zone"
                => pg ? "now()" : my ? "NOW()" : ora ? "SYSTIMESTAMP" : "GETDATE()",
            "uniqueidentifier" or "uuid" => pg ? "gen_random_uuid()" : my ? "UUID()" : "NEWID()",
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml"
                or "character varying" or "character" or "bpchar" or "citext" or "json" or "jsonb"
                or "tinytext" or "mediumtext" or "longtext"
                or "varchar2" or "nvarchar2" => "''",
            _ => "NULL",
        };
    }

    /// <summary>
    /// Şema kopyalama (2026-07-31): FK'yı ALTER TABLE ADD CONSTRAINT olarak yazar. Kaynak FK ADI
    /// modelde taşınmaz (<see cref="YabanciAnahtar"/> yalnız uçları tutar) — deterministik ad üretilir
    /// (bilinçli v1 sınırı). Bileşik anahtarda kolonlar sıralı listelenir.
    /// </summary>
    public static string FkScripti(YabanciAnahtar fk, int sira)
    {
        static string K(string a) => $"[{a.Replace("]", "]]")}]";
        string ad = $"FK_{fk.KaynakTablo}_{fk.HedefTablo}_{sira}".Replace("]", "]]");
        return $"ALTER TABLE {K(fk.KaynakSema)}.{K(fk.KaynakTablo)} ADD CONSTRAINT [{ad}] "
             + $"FOREIGN KEY ({string.Join(", ", fk.KaynakKolonlar.Select(K))}) "
             + $"REFERENCES {K(fk.HedefSema)}.{K(fk.HedefTablo)} ({string.Join(", ", fk.HedefKolonlar.Select(K))});";
    }

    /// <summary>Boşluk ve (iç içe olabilen) yorumları atlayarak ilk anlamlı karakterin indeksini verir.</summary>
    private static int IlkTokenIndeksi(string metin)
    {
        int i = 0;
        while (i < metin.Length)
        {
            char c = metin[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < metin.Length && metin[i + 1] == '-')
            {
                int satirSonu = metin.IndexOf('\n', i);
                if (satirSonu < 0)
                    return -1;
                i = satirSonu + 1;
                continue;
            }

            if (c == '/' && i + 1 < metin.Length && metin[i + 1] == '*')
            {
                int derinlik = 1;
                i += 2;
                while (i < metin.Length && derinlik > 0)
                {
                    if (metin[i] == '/' && i + 1 < metin.Length && metin[i + 1] == '*')
                    {
                        derinlik++;
                        i += 2;
                    }
                    else if (metin[i] == '*' && i + 1 < metin.Length && metin[i + 1] == '/')
                    {
                        derinlik--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                continue;
            }

            return i;
        }

        return -1;
    }
}
