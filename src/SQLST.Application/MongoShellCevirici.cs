using System.Text;
using System.Text.Json;

namespace SQLST.Application;

/// <summary>
/// v20-S21 m.26 fikir 9 ("shell komutu yapıştır" — cURL yapıştır'ın Mongo karşılığı): Compass'tan,
/// meslektaştan ya da internetten gelen <c>db.koleksiyon.find({…}).sort({…}).limit(10)</c> metnini
/// SQLST'nin çalıştırdığı biçime çevirir. İki iş yapar: (a) zinciri parçalar (find/aggregate/
/// findOne/countDocuments + sort/limit/skip/projection), (b) shell'in GEVŞEK JSON'unu katı JSON'a
/// çevirir — tırnaksız anahtar, tek tırnak, <c>ObjectId(…)</c>, <c>ISODate(…)</c>, <c>/regex/i</c>.
/// Hiçbir şey çalıştırmaz; çıktı kullanıcının gözü önünde sekmede açılır.
/// </summary>
public static class MongoShellCevirici
{
    public static (string? Json, string? Hata) Cevir(string shell)
    {
        if (string.IsNullOrWhiteSpace(shell))
            return (null, "Panoda çevrilecek bir shell komutu yok.");

        string metin = shell.Trim().TrimEnd(';').Trim();
        if (!metin.StartsWith("db.", StringComparison.OrdinalIgnoreCase))
            return (null, "Beklenen biçim: db.koleksiyon.find({…}) — metin 'db.' ile başlamıyor.");

        metin = GetCollectionAc(metin);

        int ilkParantez = metin.IndexOf('(');
        if (ilkParantez < 0)
            return (null, "Shell komutu ayrıştırılamadı (metot çağrısı yok).");

        string[] basParcalar = metin[3..ilkParantez].Split('.');
        if (basParcalar.Length < 2)
            return (null, "Koleksiyon ya da metot adı okunamadı (db.koleksiyon.metot bekleniyor).");

        string koleksiyon = string.Join('.', basParcalar[..^1]);
        var zincir = new List<(string Metot, string Arg)>();
        int i = ilkParantez;
        string metot = basParcalar[^1];

        while (true)
        {
            (string? arg, int sonrasi) = ArgumanOku(metin, i);
            if (arg is null)
                return (null, $"'{metot}(' çağrısının parantezi kapanmıyor.");
            zincir.Add((metot, arg));

            int j = sonrasi;
            while (j < metin.Length && char.IsWhiteSpace(metin[j]))
                j++;
            if (j >= metin.Length)
                break;
            if (metin[j] != '.')
                return (null, "Zincirde beklenmeyen metin var — yalnız .sort() .limit() .skip() zincirlenebilir.");

            int p = metin.IndexOf('(', j);
            if (p < 0)
                return (null, "Zincirdeki metot çağrısı eksik.");
            metot = metin[(j + 1)..p].Trim();
            i = p;
        }

        return YapiKur(koleksiyon, zincir);
    }

    /// <summary>
    /// <c>db.getCollection("ad")</c> / <c>db.getCollection('ad')</c> → <c>db.ad</c> (v22-S3 saha
    /// turu-3 m.4). MongoDB Compass'ın kendi "Copy" düğmesi sorguyu BU biçimde verir; eski
    /// ayrıştırıcı "db." ile "(" arasında nokta göremediği için en yaygın yapıştırmayı reddediyordu.
    /// Ad tırnaklıysa nokta içerebilir (log.2026) — normalleştirme bunu bozmaz, çünkü koleksiyon adı
    /// aşağıda "son parça hariç hepsi" olarak birleştirilir.
    /// </summary>
    private static string GetCollectionAc(string metin)
    {
        const string onek = "db.getCollection(";
        if (!metin.StartsWith(onek, StringComparison.OrdinalIgnoreCase))
            return metin;

        int kapanis = metin.IndexOf(')', onek.Length);
        if (kapanis < 0)
            return metin; // parantez kapanmıyor — asıl ayrıştırıcı net hatayı versin

        string ad = metin[onek.Length..kapanis].Trim().Trim('"', '\'').Trim();
        return ad.Length == 0 || ad.Contains('(') ? metin : $"db.{ad}{metin[(kapanis + 1)..]}";
    }

    private static (string? Json, string? Hata) YapiKur(string koleksiyon, List<(string Metot, string Arg)> zincir)
    {
        (string ilkMetot, string ilkArg) = zincir[0];
        var parcalar = new List<string>();
        string kok;

        switch (ilkMetot.ToLowerInvariant())
        {
            case "find":
            case "findone":
            {
                kok = "find";
                // find({filtre}, {projeksiyon}) — ikinci argüman varsa projection'dır.
                (string? filtre, string? projeksiyon) = IkiyeBol(ilkArg);
                parcalar.Add($"\"filter\": {Json(filtre ?? "{}")}");
                if (projeksiyon is not null)
                    parcalar.Add($"\"projection\": {Json(projeksiyon)}");
                if (ilkMetot.Equals("findOne", StringComparison.OrdinalIgnoreCase))
                    parcalar.Add("\"limit\": 1");
                break;
            }
            case "aggregate":
                kok = "aggregate";
                parcalar.Add($"\"pipeline\": {Json(ilkArg.Trim().Length == 0 ? "[]" : ilkArg)}");
                break;
            case "count":
            case "countdocuments":
                // Sayım aggregate'e çevrilir: SQLST find/aggregate biçimini çalıştırır.
                return (Bicimle("aggregate", koleksiyon,
                    [$"\"pipeline\": [ {{ \"$match\": {Json(ilkArg.Trim().Length == 0 ? "{}" : ilkArg)} }}, {{ \"$count\": \"adet\" }} ]"]), null);
            case "distinct":
                return (null, "distinct() henüz çevrilmiyor — aggregate($group) ile yazabilirsiniz.");
            default:
                return (null, $"'{ilkMetot}()' çevrilmiyor — find, findOne, aggregate ve countDocuments desteklenir.");
        }

        foreach ((string metot, string arg) in zincir.Skip(1))
        {
            switch (metot.ToLowerInvariant())
            {
                case "sort":
                    parcalar.Add($"\"sort\": {Json(arg)}");
                    break;
                case "limit":
                case "skip":
                    if (!long.TryParse(arg.Trim(), out long n))
                        return (null, $"{metot}() sayı bekliyor: {arg}");
                    parcalar.Add($"\"{metot.ToLowerInvariant()}\": {n}");
                    break;
                case "projection":
                    parcalar.Add($"\"projection\": {Json(arg)}");
                    break;
                case "pretty":
                case "toarray":
                    break; // görüntüleme çağrıları — sorguyu etkilemez, sessizce atlanır
                default:
                    return (null, $"Zincirdeki '{metot}()' çevrilmiyor (sort/limit/skip/projection desteklenir).");
            }
        }

        return (Bicimle(kok, koleksiyon, parcalar), null);
    }

    private static string Bicimle(string kok, string koleksiyon, IReadOnlyList<string> parcalar)
        => $"{{ \"{kok}\": {JsonSerializer.Serialize(koleksiyon)},\n  {string.Join(",\n  ", parcalar)} }}";

    /// <summary>Açık parantezden başlayarak dengeli argümanı okur (string içindeki parantezler sayılmaz).</summary>
    private static (string? Arg, int Sonrasi) ArgumanOku(string metin, int acilis)
    {
        int derinlik = 0;
        char tirnak = '\0';
        for (int i = acilis; i < metin.Length; i++)
        {
            char c = metin[i];
            if (tirnak != '\0')
            {
                if (c == '\\')
                    i++;
                else if (c == tirnak)
                    tirnak = '\0';
                continue;
            }
            if (c is '"' or '\'')
                tirnak = c;
            else if (c is '(' or '[' or '{')
                derinlik++;
            else if (c is ')' or ']' or '}')
            {
                derinlik--;
                if (derinlik == 0)
                    return (metin[(acilis + 1)..i], i + 1);
            }
        }
        return (null, metin.Length);
    }

    /// <summary>find(…) argümanını (filtre, projeksiyon) olarak ayırır — üst düzey virgülden böler.</summary>
    private static (string? Ilk, string? Ikinci) IkiyeBol(string arg)
    {
        int derinlik = 0;
        char tirnak = '\0';
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (tirnak != '\0')
            {
                if (c == '\\')
                    i++;
                else if (c == tirnak)
                    tirnak = '\0';
                continue;
            }
            if (c is '"' or '\'')
                tirnak = c;
            else if (c is '(' or '[' or '{')
                derinlik++;
            else if (c is ')' or ']' or '}')
                derinlik--;
            else if (c == ',' && derinlik == 0)
                return (arg[..i], arg[(i + 1)..]);
        }
        return (arg.Trim().Length == 0 ? null : arg, null);
    }

    /// <summary>
    /// Shell'in gevşek JSON'unu katı JSON'a çevirir: tırnaksız anahtarlar tırnaklanır, tek tırnaklı
    /// metinler çift tırnağa döner, <c>ObjectId/ISODate/NumberLong/NumberDecimal/new Date</c>
    /// Extended JSON'a, <c>/desen/i</c> ise <c>$regex</c>'e çevrilir.
    /// </summary>
    public static string Json(string gevsek)
    {
        var sb = new StringBuilder(gevsek.Length + 16);
        for (int i = 0; i < gevsek.Length; i++)
        {
            char c = gevsek[i];

            if (c is '"' or '\'')
            {
                i = MetniYaz(gevsek, i, sb);
                continue;
            }
            if (c == '/' && RegexBasiMi(sb))
            {
                i = RegexYaz(gevsek, i, sb);
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int son = i;
                while (son + 1 < gevsek.Length && (char.IsLetterOrDigit(gevsek[son + 1]) || gevsek[son + 1] is '_' or '$' or '.'))
                    son++;
                string kelime = gevsek[i..(son + 1)];
                i = KelimeYaz(gevsek, kelime, son, sb);
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static int MetniYaz(string kaynak, int bas, StringBuilder sb)
    {
        char tirnak = kaynak[bas];
        var icerik = new StringBuilder();
        int i = bas + 1;
        for (; i < kaynak.Length; i++)
        {
            if (kaynak[i] == '\\' && i + 1 < kaynak.Length)
            {
                icerik.Append(kaynak[i]).Append(kaynak[i + 1]);
                i++;
                continue;
            }
            if (kaynak[i] == tirnak)
                break;
            icerik.Append(kaynak[i]);
        }
        // Tek tırnaklı metinde çift tırnak kaçırılmalı (aksi hâlde JSON bozulur).
        sb.Append('"').Append(tirnak == '\'' ? icerik.ToString().Replace("\"", "\\\"") : icerik.ToString()).Append('"');
        return i;
    }

    private static int KelimeYaz(string kaynak, string kelime, int son, StringBuilder sb)
    {
        int sonraki = son + 1;
        while (sonraki < kaynak.Length && char.IsWhiteSpace(kaynak[sonraki]))
            sonraki++;

        // Sarmalayıcı çağrılar: ObjectId("…"), ISODate("…"), new Date("…"), NumberLong(…)
        if (sonraki < kaynak.Length && kaynak[sonraki] == '(')
        {
            (string? arg, int sonrasi) = ArgumanOku(kaynak, sonraki);
            string ic = (arg ?? "").Trim().Trim('"', '\'');
            string ad = kelime.Equals("new", StringComparison.Ordinal) ? "Date" : kelime;
            sb.Append(ad.ToLowerInvariant() switch
            {
                "objectid" => $$"""{ "$oid": {{JsonSerializer.Serialize(ic)}} }""",
                "isodate" or "date" => $$"""{ "$date": {{JsonSerializer.Serialize(ic)}} }""",
                "numberlong" or "numberint" => ic,
                "numberdecimal" => $$"""{ "$numberDecimal": {{JsonSerializer.Serialize(ic)}} }""",
                _ => JsonSerializer.Serialize(ic),
            });
            return sonrasi - 1;
        }

        // "new" tek başına (new Date(…)) — sonraki kelime işlenecek
        if (kelime.Equals("new", StringComparison.Ordinal))
            return son;

        // JSON sabitleri olduğu gibi; gerisi anahtar/metin → tırnaklanır
        sb.Append(kelime is "true" or "false" or "null" ? kelime : JsonSerializer.Serialize(kelime));
        return son;
    }

    private static int RegexYaz(string kaynak, int bas, StringBuilder sb)
    {
        int i = bas + 1;
        var desen = new StringBuilder();
        for (; i < kaynak.Length && kaynak[i] != '/'; i++)
        {
            if (kaynak[i] == '\\' && i + 1 < kaynak.Length)
            {
                desen.Append(kaynak[i]).Append(kaynak[i + 1]);
                i++;
                continue;
            }
            desen.Append(kaynak[i]);
        }
        int bayrakBas = i + 1;
        int j = bayrakBas;
        while (j < kaynak.Length && char.IsLetter(kaynak[j]))
            j++;
        string bayraklar = kaynak[bayrakBas..j];

        sb.Append($$"""{ "$regex": {{JsonSerializer.Serialize(desen.ToString())}}""");
        if (bayraklar.Length > 0)
            sb.Append($$""", "$options": {{JsonSerializer.Serialize(bayraklar)}}""");
        sb.Append(" }");
        return j - 1;
    }

    /// <summary>'/' bölme değil regex başlangıcı mı — değerin beklendiği yerde (: , [ { sonrası) regexdir.</summary>
    private static bool RegexBasiMi(StringBuilder sb)
    {
        for (int i = sb.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(sb[i]))
                continue;
            return sb[i] is ':' or ',' or '[' or '{' or '(';
        }
        return true;
    }
}
