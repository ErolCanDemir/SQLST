using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// 📦 "Yeni tabloya aktar" SAF çekirdeği (v23-S32 — kullanıcı 15 Eki: "paket aktarımını tablolar arası
/// SELECT INTO gibi yapalım"). Paket Aktarım'da hedef tablo YOKSA kaynağın kolonlarından oluşturulup
/// doldurulur. İki yol:
///  • <b>Kestirme</b> (aynı sunucu + aynı motor): iş motorun içinde biter — MSSQL <c>SELECT … INTO</c>
///    (kullanıcının sorgusuna ScriptDom'la INTO eklenir; CTE/ORDER BY korunur), PG/MySQL/Oracle
///    <c>CREATE TABLE … AS SELECT</c>. Ağdan veri geçmez. PG'de farklı veritabanı aynı bağlantıdan
///    görülemez → kestirme yok.
///  • <b>Akışlı</b> (farklı sunucu): önce <see cref="CreateTableSql"/> hedefte koşar, sonra mevcut
///    500'lük parti aktarımı (AktarimServisi).
/// KARAR (15 Eki): S1 yalnız AYNI MOTOR — tip çevirisi yapılmaz (çapraz motorda kullanıcı hedefi
/// kendisi oluşturup eşleme kipiyle aktarır). SELECT INTO gibi: yalnız kolonlar + veri; identity/
/// index/default taşınmaz, PK isteğe bağlı. UI/IO yok — birim testli.
/// </summary>
public static class YeniTabloyaAktarim
{
    /// <summary>Aynı motor + aynı sunucu adı (büyük/küçük duyarsız): kestirme yolun ön şartı.</summary>
    public static bool AyniSunucuMu(ConnectionProfile a, ConnectionProfile b)
        => a.Motor == b.Motor
           && string.Equals(a.Sunucu.Trim(), b.Sunucu.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"şema.ad" → (şema, ad); "ad" → (null, ad). Tırnaklı girdi beklenmez (katalog/kullanıcı adı).</summary>
    public static (string? Sema, string Ad) AdAyir(string tablo)
    {
        int nokta = tablo.LastIndexOf('.');
        return nokta < 0 ? (null, tablo.Trim()) : (tablo[..nokta].Trim(), tablo[(nokta + 1)..].Trim());
    }

    /// <summary>
    /// Yazılan ad var olan bir tabloya çarpıyor mu: tam ad ("dbo.Siparis") ya da yalnız ad ("Siparis" →
    /// "dbo.Siparis") eşleşmesi. Dönen = çarpılan tablonun tam adı (null = yeni). Sessiz ezme olmaz.
    /// </summary>
    public static string? VarOlanTablo(string yazilan, IEnumerable<string> tamAdlar)
    {
        string ad = yazilan.Trim();
        if (ad.Length == 0)
            return null;
        foreach (string tam in tamAdlar)
        {
            if (string.Equals(tam, ad, StringComparison.OrdinalIgnoreCase))
                return tam;
            if (!ad.Contains('.') && string.Equals(AdAyir(tam).Ad, ad, StringComparison.OrdinalIgnoreCase))
                return tam;
        }
        return null;
    }

    /// <summary>
    /// Akışlı yolun hedef CREATE TABLE'ı: kolonlar kaynaktan BİREBİR (aynı motor → tip metni aynen),
    /// NULL bilgisi korunur, <paramref name="pkKolonlar"/> verilirse PRIMARY KEY. Identity/default/index yok.
    /// </summary>
    public static (string? Sql, string? Hata) CreateTableSql(
        ILehce lehce, string hedefTablo, IReadOnlyList<SemaKolonu> kolonlar, IReadOnlyList<string> pkKolonlar)
    {
        (string? sema, string ad) = AdAyir(hedefTablo);
        var pk = new HashSet<string>(pkKolonlar, StringComparer.OrdinalIgnoreCase);
        var yeni = new YeniTablo(sema, ad, [.. kolonlar.Select(k =>
            new YeniKolon(k.Ad, k.Tip, NullOlabilir: k.NullOlabilir && !pk.Contains(k.Ad), PkMi: pk.Contains(k.Ad)))]);
        return TabloOlusturucu.Uret(yeni, lehce);
    }

    /// <summary>Kestirme planı: sırayla koşacak ifadeler + hedefin kaynak bağlantıdan görünen tam adı (hata temizliği için).</summary>
    public sealed record KestirmePlani(IReadOnlyList<string> Ifadeler, string HedefTamAd);

    /// <summary>
    /// Kestirme yolun ifadeleri (KAYNAK bağlantıda, kaynak DB bağlamında koşar; hedef tam nitelenir):
    /// [0] SELECT INTO / CREATE TABLE AS, [1] (isteğe bağlı) PK. Null + neden = bu durumda kestirme yok;
    /// çağıran akışlı yola düşer.
    /// </summary>
    public static (KestirmePlani? Plan, string? Neden) KestirmeIfadeleri(
        ILehce lehce, string kaynakSql, string? kaynakDb, string? hedefDb, string hedefTablo,
        IReadOnlyList<string> pkKolonlar)
    {
        string hedefTam = HedefTamAd(lehce, kaynakDb, hedefDb, hedefTablo, out string? neden);
        if (neden is not null)
            return (null, neden);

        string? ilk = lehce.MotorId == "mssql"
            ? SelectIntoEkle(kaynakSql, hedefTam, out neden)
            : $"CREATE TABLE {hedefTam} AS\n{kaynakSql.Trim().TrimEnd(';')}{lehce.IfadeSonu}";
        if (ilk is null)
            return (null, neden);

        var ifadeler = new List<string> { ilk };
        if (pkKolonlar.Count > 0)
        {
            string kisitAdi = lehce.TirnaklaTanimlayici("PK_" + AdAyir(hedefTablo).Ad);
            ifadeler.Add($"ALTER TABLE {hedefTam} ADD CONSTRAINT {kisitAdi} PRIMARY KEY "
                + $"({string.Join(", ", pkKolonlar.Select(lehce.TirnaklaTanimlayici))}){lehce.IfadeSonu}");
        }
        return (new KestirmePlani(ifadeler, hedefTam), null);
    }

    /// <summary>
    /// Hedefin kaynak bağlantıdan görünen tam adı. MSSQL: <c>[Db].[şema].[ad]</c> (şemasızsa <c>[Db]..[ad]</c>);
    /// MySQL: <c>`db`.`ad`</c> (şema = db); Oracle: "db" zaten şemadır → <c>"DB"."AD"</c>; PG: yalnız AYNI
    /// veritabanında (başka DB aynı bağlantıdan görülemez) → <c>"şema"."ad"</c>.
    /// </summary>
    private static string HedefTamAd(ILehce lehce, string? kaynakDb, string? hedefDb, string hedefTablo, out string? neden)
    {
        neden = null;
        (string? sema, string ad) = AdAyir(hedefTablo);
        string q(string s) => lehce.TirnaklaTanimlayici(s);
        bool ayniDb = string.IsNullOrEmpty(hedefDb) || string.Equals(kaynakDb, hedefDb, StringComparison.OrdinalIgnoreCase);
        switch (lehce.MotorId)
        {
            case "mssql":
                if (ayniDb)
                    return sema is null ? q(ad) : lehce.TamAdYaz(sema, ad);
                return $"{q(hedefDb!)}.{(sema is null ? "" : q(sema))}.{q(ad)}";
            case "mysql":
                return ayniDb ? q(ad) : $"{q(hedefDb!)}.{q(ad)}";
            case "oracle":
                return sema is not null ? lehce.TamAdYaz(sema, ad)
                    : ayniDb ? q(ad) : lehce.TamAdYaz(hedefDb!, ad);
            default: // postgres
                if (!ayniDb)
                {
                    neden = "PostgreSQL'de başka bir veritabanı aynı bağlantıdan görülemez — veri SQLST üzerinden akıtılır.";
                    return "";
                }
                return sema is null ? q(ad) : lehce.TamAdYaz(sema, ad);
        }
    }

    /// <summary>
    /// Kullanıcının T-SQL'ine <c>INTO hedef</c> ekler (ScriptDom): tek batch, tek SELECT ifadesi; CTE ve
    /// ORDER BY olduğu gibi kalır (T-SQL'de <c>WITH … SELECT … INTO</c> geçerlidir). Çok ifadeli/
    /// SELECT olmayan/parse edilemeyen metinde null + neden → akışlı yol.
    /// </summary>
    internal static string? SelectIntoEkle(string sql, string hedefTamAd, out string? neden)
    {
        neden = null;
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment = parser.Parse(new StringReader(sql), out IList<ParseError> hatalar);
        if (hatalar.Count > 0)
        {
            neden = $"Sorgu çözümlenemedi (Satır {hatalar[0].Line}): {hatalar[0].Message}";
            return null;
        }
        if (fragment is not TSqlScript { Batches.Count: 1 } script
            || script.Batches[0].Statements.Count != 1
            || script.Batches[0].Statements[0] is not SelectStatement secim)
        {
            neden = "Kestirme yalnız TEK bir SELECT ifadesinde — çok ifadeli metin SQLST üzerinden akıtılır.";
            return null;
        }
        if (secim.Into is not null)
        {
            neden = "Sorguda zaten INTO var.";
            return null;
        }

        // Hedef adı parçalarıyla kurulur: tırnaklı parçalar Identifier olarak eklenir ([Db]..[ad] → boş şema).
        var into = new SchemaObjectName();
        foreach (string parca in Parcala(hedefTamAd))
            into.Identifiers.Add(new Identifier
            {
                Value = parca,
                QuoteType = parca.Length == 0 ? QuoteType.NotQuoted : QuoteType.SquareBracket, // boş şema → [Db]..[ad]
            });
        secim.Into = into;

        var uretici = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            IncludeSemicolons = true,
            AlignClauseBodies = false, // "INTO   [x]" gibi hizalama boşlukları olmasın (okunur script)
        });
        uretici.GenerateScript(secim, out string cikti);
        return cikti.Trim();
    }

    /// <summary>"[Db]..[ad]" → ["Db", "", "ad"] — köşeli parantezleri açar (]] kaçışını çözer).</summary>
    private static IEnumerable<string> Parcala(string tamAd)
    {
        var parcalar = new List<string>();
        var simdiki = new System.Text.StringBuilder();
        bool icinde = false;
        for (int i = 0; i < tamAd.Length; i++)
        {
            char c = tamAd[i];
            if (!icinde && c == '[') { icinde = true; continue; }
            if (icinde && c == ']')
            {
                if (i + 1 < tamAd.Length && tamAd[i + 1] == ']') { simdiki.Append(']'); i++; continue; }
                icinde = false;
                continue;
            }
            if (!icinde && c == '.') { parcalar.Add(simdiki.ToString()); simdiki.Clear(); continue; }
            simdiki.Append(c);
        }
        parcalar.Add(simdiki.ToString());
        return parcalar;
    }
}
