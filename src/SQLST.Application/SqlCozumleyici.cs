using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Bir GO batch'i: orijinal metindeki ham aralık + tekrar sayısı (GO n).</summary>
/// <param name="Metin">Ham alt dize — satır hizası korunur ki sunucu hata satırı + <paramref name="BaslangicSatiri"/> - 1 doğru eşlensin.</param>
public sealed record SqlBatch(string Metin, int BaslangicOfseti, int Uzunluk, int Tekrar, int BaslangicSatiri);

/// <summary>İmleçteki statement'ın aralığı (editör seçimi + hata satırı eşlemesi için).</summary>
public sealed record SqlAralik(string Metin, int BaslangicOfseti, int Uzunluk, int BaslangicSatiri);

/// <summary>
/// ScriptDom (MIT) tabanlı T-SQL çözümleme (V2-S3; 07-r2 §5 kuralları):
/// GO batch bölme, imleçteki statement, tam biçimlendirme. Naif split/regex YASAK —
/// string/yorum içindeki GO ayırıcı değildir, ';' opsiyoneldir.
/// </summary>
public static class SqlCozumleyici
{
    private static TSql170Parser YeniParser() => new(initialQuotedIdentifiers: true);

    /// <summary>
    /// GO sınırlarından batch'lere böler (FG-3.11). Token akışı üzerinden: GO yalnız
    /// satırın ilk anlamlı token'ıysa ayırıcıdır (aynı adlı kolon/dize yanıltmaz);
    /// "GO n" tekrarı okunur. GO T-SQL değildir — sunucuya asla gönderilmez.
    /// </summary>
    public static IReadOnlyList<SqlBatch> BatchlereBol(string sql)
    {
        IList<TSqlParserToken> tokenlar = YeniParser().GetTokenStream(new StringReader(sql), out _);

        var batchler = new List<SqlBatch>();
        int bolgeBasi = 0;
        bool satirdaAnlamliVar = false;

        for (int i = 0; i < tokenlar.Count; i++)
        {
            TSqlParserToken t = tokenlar[i];
            if (t.TokenType == TSqlTokenType.EndOfFile)
                break;

            if (t.TokenType == TSqlTokenType.WhiteSpace)
            {
                if (t.Text.Contains('\n'))
                    satirdaAnlamliVar = false;
                continue;
            }

            if (t.TokenType == TSqlTokenType.Go && !satirdaAnlamliVar)
            {
                int tekrar = 1;
                // GO satırının kalanını yut: opsiyonel sayı (GO n) + opsiyonel yorum
                int j = i + 1;
                for (; j < tokenlar.Count; j++)
                {
                    TSqlParserToken sonraki = tokenlar[j];
                    if (sonraki.TokenType == TSqlTokenType.EndOfFile)
                        break;
                    if (sonraki.TokenType == TSqlTokenType.WhiteSpace && sonraki.Text.Contains('\n'))
                        break;
                    if (sonraki.TokenType == TSqlTokenType.Integer)
                        // TryParse + tavan (inceleme 2026-07-30): "GO 3000000000" OverflowException
                        // fırlatıyordu; milyonlarca tekrar da anlamsız — 100k'ya kırpılır.
                        tekrar = int.TryParse(sonraki.Text, System.Globalization.NumberStyles.None,
                            CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 1, 100_000) : 1;
                }

                BatchEkle(sql, batchler, bolgeBasi, t.Offset, tekrar);

                // Yeni batch, GO satırını kapatan '\n'den sonra başlar
                bolgeBasi = j < tokenlar.Count && tokenlar[j].TokenType == TSqlTokenType.WhiteSpace
                    ? tokenlar[j].Offset + tokenlar[j].Text.IndexOf('\n') + 1
                    : sql.Length;
                i = j;
                satirdaAnlamliVar = false;
                continue;
            }

            satirdaAnlamliVar = true;
        }

        BatchEkle(sql, batchler, bolgeBasi, sql.Length, 1);

        // Hiç anlamlı batch yoksa (boş metin) tek boş batch dönme — çağıran zaten boşu eler
        return batchler;
    }

    /// <summary>
    /// Motora göre batch bölme (V4-S5). SQL Server <c>GO</c> ile bölünür; Oracle
    /// <c>;</c> + tek başına <c>/</c> ile. PostgreSQL/MySQL/MongoDB'de bölme YAPILMAZ:
    /// sürücüleri çok-ifadeli metni tek komutta kabul eder ve metni T-SQL çözümleyicisine
    /// sokmak (Mongo'da JSON!) yanlış olurdu — sorgu metni OPAK kalır (08-v3r1 §3).
    /// </summary>
    public static IReadOnlyList<SqlBatch> BatchlereBol(string sql, MotorTuru motor) => motor switch
    {
        MotorTuru.Mssql => BatchlereBol(sql),
        MotorTuru.Oracle => OracleBatchlereBol(sql),
        _ => TekBatch(sql),
    };

    private static IReadOnlyList<SqlBatch> TekBatch(string sql)
        => string.IsNullOrWhiteSpace(sql) ? [] : [new SqlBatch(sql, 0, sql.Length, 1, 1)];

    /// <summary>
    /// Oracle çok-ifadeli script bölme (V4-S5) — SQL*Plus modeli:
    /// <list type="bullet">
    /// <item><c>;</c> sıradan ifadeyi bitirir ve <b>sunucuya gönderilmez</b> (ORA-00933).</item>
    /// <item><b>PL/SQL bloğu içinde <c>;</c> ayırıcı DEĞİLDİR</b> — blok kendi içinde noktalı
    /// virgül kullanır. Blok yalnız <b>tek başına <c>/</c> olan satırla</b> biter; o satır da
    /// sunucuya gönderilmez.</item>
    /// <item>Dize (<c>'…'</c>, <c>q'[…]'</c>), satır yorumu (<c>--</c>) ve blok yorumu
    /// (<c>/* … */</c>) içindeki <c>;</c> ve <c>/</c> ayırıcı sayılmaz — naif split YASAK
    /// (GO bölücüsündeki kuralın aynısı).</item>
    /// </list>
    /// PL/SQL başlangıcı ilk anahtar sözcükten anlaşılır: <c>DECLARE</c>, <c>BEGIN</c> ya da
    /// <c>CREATE [OR REPLACE] [EDITIONABLE] {PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE|LIBRARY}</c>.
    /// <c>CREATE TABLE</c> gibi düz DDL PL/SQL sayılmaz, <c>;</c> ile biter.
    /// </summary>
    public static IReadOnlyList<SqlBatch> OracleBatchlereBol(string sql)
    {
        var batchler = new List<SqlBatch>();
        int bas = 0;
        bool? plsql = null;   // geçerli ifade PL/SQL mi — ilk anlamlı sözcükte belirlenir

        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];

            // Tek başına '/' olan satır: her durumda ifadeyi bitirir (PL/SQL sonlandırıcısı)
            if (c == '/' && TekBasinaSlashSatiri(sql, i, out int slashSonrasi))
            {
                BatchEkle(sql, batchler, bas, i, 1);
                bas = slashSonrasi;
                i = slashSonrasi;
                plsql = null;
                continue;
            }

            // Yorum ve dizeler atlanır (içlerindeki ';' / '/' ayırıcı değildir)
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { i = SatirSonunaAtla(sql, i); continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { i = BlokYorumSonu(sql, i); continue; }
            if (c == '\'') { i = DizeSonu(sql, i); continue; }
            if ((c is 'q' or 'Q') && i + 1 < sql.Length && sql[i + 1] == '\'') { i = AlternatifDizeSonu(sql, i); continue; }

            // İfadenin ilk anlamlı karakteri: PL/SQL mi?
            if (plsql is null && !char.IsWhiteSpace(c))
                plsql = PlsqlBlogunuBaslatir(sql, i);

            if (c == ';' && plsql == false)
            {
                BatchEkle(sql, batchler, bas, i, 1);   // ';' metne DAHİL EDİLMEZ
                bas = i + 1;
                plsql = null;
                i++;
                continue;
            }

            i++;
        }

        BatchEkle(sql, batchler, bas, sql.Length, 1);
        return batchler;
    }

    /// <summary>Satırda '/' dışında yalnız boşluk varsa true; çıkışta satırın SONRASINI verir.</summary>
    private static bool TekBasinaSlashSatiri(string sql, int i, out int satirSonrasi)
    {
        satirSonrasi = i + 1;

        for (int k = i - 1; k >= 0 && sql[k] != '\n'; k--)
        {
            if (!char.IsWhiteSpace(sql[k]))
                return false;
        }

        int j = i + 1;
        while (j < sql.Length && sql[j] != '\n')
        {
            if (!char.IsWhiteSpace(sql[j]))
                return false;
            j++;
        }

        satirSonrasi = j < sql.Length ? j + 1 : sql.Length;
        return true;
    }

    private static int SatirSonunaAtla(string sql, int i)
    {
        int son = sql.IndexOf('\n', i);
        return son < 0 ? sql.Length : son + 1;
    }

    private static int BlokYorumSonu(string sql, int i)
    {
        int son = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return son < 0 ? sql.Length : son + 2;   // kapanmamış yorum: metnin sonuna kadar
    }

    /// <summary>Tek tırnaklı dize; içeride '' kaçışı vardır.</summary>
    private static int DizeSonu(string sql, int i)
    {
        int j = i + 1;
        while (j < sql.Length)
        {
            if (sql[j] == '\'')
            {
                if (j + 1 < sql.Length && sql[j + 1] == '\'') { j += 2; continue; } // '' kaçışı
                return j + 1;
            }
            j++;
        }
        return sql.Length;
    }

    /// <summary>
    /// Oracle alternatif tırnaklama: <c>q'[…]'</c>, <c>q'{…}'</c>, <c>q'(…)'</c>,
    /// <c>q'&lt;…&gt;'</c> ya da herhangi bir ayraç (<c>q'!…!'</c>). İçeride ';' geçebilir.
    /// </summary>
    private static int AlternatifDizeSonu(string sql, int i)
    {
        int acilis = i + 2;                  // q' sonrasındaki ayraç
        if (acilis >= sql.Length)
            return sql.Length;

        char ayrac = sql[acilis];
        char kapanis = ayrac switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => ayrac };

        for (int j = acilis + 1; j < sql.Length - 1; j++)
        {
            if (sql[j] == kapanis && sql[j + 1] == '\'')
                return j + 2;
        }
        return sql.Length;
    }

    private static readonly string[] PlsqlNesneleri =
        ["PROCEDURE", "FUNCTION", "PACKAGE", "TRIGGER", "TYPE", "LIBRARY"];

    /// <summary>İfade PL/SQL bloğu mu — ilk anahtar sözcük(ler)inden karar verilir.</summary>
    private static bool PlsqlBlogunuBaslatir(string sql, int i)
    {
        string ilk = KelimeOku(sql, ref i);
        if (ilk is "DECLARE" or "BEGIN")
            return true;
        if (ilk != "CREATE")
            return false;

        // CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE] <nesne>
        string sonraki = KelimeOku(sql, ref i);
        if (sonraki == "OR")
        {
            KelimeOku(sql, ref i);            // REPLACE
            sonraki = KelimeOku(sql, ref i);
        }
        if (sonraki is "EDITIONABLE" or "NONEDITIONABLE")
            sonraki = KelimeOku(sql, ref i);

        return PlsqlNesneleri.Contains(sonraki);
    }

    /// <summary>Boşluk/yorum atlayıp sonraki sözcüğü BÜYÜK harfle okur; imleci ilerletir.</summary>
    private static string KelimeOku(string sql, ref int i)
    {
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { i = SatirSonunaAtla(sql, i); continue; }
            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { i = BlokYorumSonu(sql, i); continue; }
            break;
        }

        int bas = i;
        while (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_'))
            i++;
        return i > bas ? sql[bas..i].ToUpperInvariant() : "";
    }

    private static void BatchEkle(string sql, List<SqlBatch> batchler, int bas, int son, int tekrar)
    {
        if (son <= bas)
            return;
        string metin = sql[bas..son];
        if (string.IsNullOrWhiteSpace(metin))
            return; // yalnız GO/boşluk — sunucuya gidecek bir şey yok

        int satir = SatirNumarasi(sql, bas);
        batchler.Add(new SqlBatch(metin, bas, son - bas, tekrar, satir));
    }

    private static int SatirNumarasi(string sql, int ofset)
    {
        int satir = 1;
        for (int i = 0; i < ofset && i < sql.Length; i++)
        {
            if (sql[i] == '\n')
                satir++;
        }
        return satir;
    }

    /// <summary>
    /// İmlecin üzerinde durduğu ÜST SEVİYE statement'ı bulur (07-r2 §5: iç içe blokta
    /// en dıştaki batch statement'ı — SSMS davranışı). Ofset iki statement arasındaysa
    /// öncekini seçer; ilkinden öncedeyse ilkini. Söz dizimi hatalı metinde null + mesaj.
    /// </summary>
    public static (SqlAralik? Aralik, string? Hata) ImlectekiStatement(string sql, int ofset)
    {
        if (YeniParser().Parse(new StringReader(sql), out IList<ParseError> hatalar) is not TSqlScript script)
            return (null, "Sorgu çözümlenemedi.");
        if (hatalar.Count > 0)
            return (null, $"Söz dizimi hatası (Satır {hatalar[0].Line}): {hatalar[0].Message}");

        List<TSqlStatement> statementlar = [.. script.Batches.SelectMany(b => b.Statements)];
        if (statementlar.Count == 0)
            return (null, null);

        TSqlStatement secili = statementlar.LastOrDefault(s => s.StartOffset <= ofset) ?? statementlar[0];
        return (new SqlAralik(
            sql.Substring(secili.StartOffset, secili.FragmentLength),
            secili.StartOffset, secili.FragmentLength, secili.StartLine), null);
    }

    /// <summary>
    /// Tam biçimlendirme (FG-3.9): AST'den yeniden üretim — anahtar sözcükler BÜYÜK, girinti 4.
    /// ScriptDom AST'si yorum TAŞIMAZ (üretimde kaybolurlardı). <b>Kullanıcı isteği 2026-07-23:</b>
    /// yorum varken de biçimlendir, yorumlar etkilenmesin. Çözüm: yorum varsa yorumları OLDUĞU GİBİ
    /// koruyan hafif <see cref="SqlBicimleyici"/>'ye düşülür (anahtar sözcüklerden satır böler, dize/
    /// ad/yorum bölgelerine dokunmaz). Yorumsuz metin tam ScriptDom biçimlendirmesi alır. Söz dizimi
    /// hatasında null + mesaj.
    /// </summary>
    public static (string? Sonuc, string? Hata) Bicimlendir(string sql)
    {
        TSql170Parser parser = YeniParser();
        TSqlFragment fragment = parser.Parse(new StringReader(sql), out IList<ParseError> hatalar);
        if (hatalar.Count > 0)
            return (null, $"Söz dizimi hatası (Satır {hatalar[0].Line}): {hatalar[0].Message}");

        bool yorumVar = fragment.ScriptTokenStream.Any(t =>
            t.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment);
        if (yorumVar)
            // ScriptDom yorumu düşürürdü → yorumları koruyan hafif biçimlendirici (yorumlar aynen kalır).
            return (SqlBicimleyici.Bicimlendir(sql), null);

        var uretici = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = true,
            IndentationSize = 4,
            AlignClauseBodies = false,
        });
        uretici.GenerateScript(fragment, out string cikti);
        return (JoinOnBirlestir(cikti.TrimEnd()), null);
    }

    /// <summary>
    /// ScriptDom bir JOIN'i DÖRT satıra dağıtır (join tipi · tablo · ON · her AND/OR ayrı):
    /// <code>
    ///      INNER JOIN dbo.B AS b
    ///      ON a.id = b.aid
    ///         AND a.k = b.k
    /// </code>
    /// Kullanıcı <c>JOIN … ON …</c>'un TEK satırda olmasını istedi (2026-07-21). İki kuralı
    /// birleştiririz: (1) bir satır JOIN/APPLY anahtarıyla bitiyorsa sonraki (tablo) satırını ona
    /// ekle; (2) <c>ON </c> satırını ve onu izleyen <c>AND</c>/<c>OR</c> devam satırlarını da aynı
    /// satıra topla. Sonuç: <c>INNER JOIN dbo.B AS b ON a.id = b.aid AND a.k = b.k</c>. WHERE'in
    /// AND/OR'ları etkilenmez (bir ON'u izlemezler → birleştirilmezler).
    /// </summary>
    private static string JoinOnBirlestir(string cikti)
    {
        string[] satirlar = cikti.Replace("\r\n", "\n").Split('\n');
        var sonuc = new List<string>(satirlar.Length);
        bool onDevami = false; // bir ON (ya da onun AND/OR devamı) satırındayız → sonraki AND/OR de birleşir

        foreach (string satir in satirlar)
        {
            string kirp = satir.TrimStart();
            bool oncekiJoin = sonuc.Count > 0 && JoinAnahtariIleBitiyor(sonuc[^1]);
            bool onSatiri = kirp.StartsWith("ON ", StringComparison.OrdinalIgnoreCase);
            bool devamSatiri = onDevami
                && (kirp.StartsWith("AND ", StringComparison.OrdinalIgnoreCase)
                    || kirp.StartsWith("OR ", StringComparison.OrdinalIgnoreCase));

            if (sonuc.Count > 0 && (oncekiJoin || onSatiri || devamSatiri))
            {
                sonuc[^1] = sonuc[^1].TrimEnd() + " " + kirp;
                onDevami = onSatiri || devamSatiri; // join-anahtarı birleşmesi ON başlatmaz (ON alt satırda)
                continue;
            }

            sonuc.Add(satir);
            onDevami = false;
        }

        return string.Join(Environment.NewLine, sonuc);
    }

    /// <summary>Satır tek başına bir JOIN/APPLY anahtarıyla mı bitiyor (dolayısıyla tablosu alt satırda)?</summary>
    private static bool JoinAnahtariIleBitiyor(string satir)
    {
        string s = satir.TrimEnd();
        return s.EndsWith(" JOIN", StringComparison.OrdinalIgnoreCase)
            || s.Equals("JOIN", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(" APPLY", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tek tablolu SELECT'in kaynağı: [Veritabani.]Sema.Ad (edit modda DML hedefi).</summary>
    public sealed record TekTabloKaynak(string? Veritabani, string Sema, string Ad);

    /// <summary>
    /// Sorgu TEK bir tabloyu okuyan basit bir SELECT mi — öyleyse o tabloyu döner (edit modda
    /// düzenleme hedefi). Kullanıcı bulgusu 2026-07-27: edit tab SABİT tabloya bağlıydı ama kullanıcı
    /// BAŞKA tabloyu sorgulayınca üretilen UPDATE yanlış tabloya gidip 0 satır (çakışma) veriyordu.
    /// SADECE tek NamedTableReference'lı FROM kabul edilir; JOIN / alt sorgu / türev tablo / birden
    /// çok ifade → null (o sonuç güvenle tek tabloya UPDATE edilemez, düzenlenemez sayılır).
    /// Şema belirtilmemişse "dbo" varsayılır (MSSQL); üç parçalı adda veritabanı da döner.
    /// </summary>
    public static TekTabloKaynak? TekTabloSelectKaynagi(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;
        if (YeniParser().Parse(new StringReader(sql), out IList<ParseError> hatalar) is not TSqlScript script
            || hatalar.Count > 0)
            return null;

        List<TSqlStatement> ifadeler = [.. script.Batches.SelectMany(b => b.Statements)];
        if (ifadeler.Count != 1 || ifadeler[0] is not SelectStatement { QueryExpression: QuerySpecification qs })
            return null;

        // FROM tam olarak bir tablo (JOIN/alt sorgu yok) olmalı.
        if (qs.FromClause is not { TableReferences: [NamedTableReference ntr] })
            return null;

        IList<Identifier> parcalar = ntr.SchemaObject.Identifiers;
        return parcalar.Count switch
        {
            1 => new TekTabloKaynak(null, "dbo", parcalar[0].Value),
            2 => new TekTabloKaynak(null, parcalar[0].Value, parcalar[1].Value),
            3 => new TekTabloKaynak(parcalar[0].Value, parcalar[1].Value, parcalar[2].Value),
            _ => null,
        };
    }

    /// <summary>Katlanabilir blok: metindeki [Bas, Son) ofset aralığı (editör folding'i için).</summary>
    public sealed record KatlamaAraligi(int Bas, int Son);

    /// <summary>
    /// Editör kod katlama aralıkları (kullanıcı isteği 2026-07-27: "if, begin gibi tüm alanlarda
    /// end'ine kadar küçültme"). T-SQL'de blok <c>BEGIN…END</c>'dir (IF/WHILE/TRY/CATCH gövdeleri de
    /// bunu kullanır); <c>CASE…END</c> de END ile kapanır. Token akışı üzerinden yığınla eşlenir —
    /// string/yorum içindeki BEGIN/END yanıltmaz (naif metin taraması YASAK, GO kuralının aynısı).
    /// Katlama BEGIN/CASE anahtarının SONUNDAN başlar (anahtar görünür kalır); yalnız BİRDEN ÇOK
    /// satıra yayılan bloklar döner. Ek olarak çok satırlı <c>/* … */</c> yorumları da katlanır.
    /// Söz dizimi hatalı metinde token akışı yine alınır (parser toleranslı) — dengelenemeyen
    /// END'ler sessizce atlanır.
    /// </summary>
    public static IReadOnlyList<KatlamaAraligi> KatlamaAraliklari(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return [];

        IList<TSqlParserToken> tokenlar;
        try { tokenlar = YeniParser().GetTokenStream(new StringReader(sql), out _); }
        catch { return []; }

        var araliklar = new List<KatlamaAraligi>();
        var yigin = new Stack<int>(); // her BEGIN/CASE'in gövde başlangıç ofseti (anahtardan SONRA)

        foreach (TSqlParserToken t in tokenlar)
        {
            switch (t.TokenType)
            {
                case TSqlTokenType.Begin or TSqlTokenType.Case:
                    yigin.Push(t.Offset + t.Text.Length);
                    break;
                case TSqlTokenType.End when yigin.Count > 0:
                    int bas = yigin.Pop();
                    int son = t.Offset + t.Text.Length;
                    if (son > bas && CokSatirli(sql, bas, son))
                        araliklar.Add(new KatlamaAraligi(bas, son));
                    break;
                case TSqlTokenType.MultilineComment when CokSatirli(sql, t.Offset, t.Offset + t.Text.Length):
                    araliklar.Add(new KatlamaAraligi(t.Offset, t.Offset + t.Text.Length));
                    break;
            }
        }

        araliklar.Sort((a, b) => a.Bas.CompareTo(b.Bas)); // FoldingManager artan başlangıç ister
        return araliklar;
    }

    private static bool CokSatirli(string sql, int bas, int son)
    {
        for (int i = bas; i < son && i < sql.Length; i++)
        {
            if (sql[i] == '\n')
                return true;
        }
        return false;
    }

    /// <summary>
    /// WHERE'siz UPDATE/DELETE tespiti (FG-6.2 — her modda çalışan ucuz sigorta).
    /// TOP(n)'li olanlar bilinçli sınırlama sayılır, uyarılmaz. Söz dizimi hatalı
    /// metinde boş döner — analiz edilemeyeni engellemek sunucunun işi değil bizim
    /// işimiz değil; sorgu zaten sunucuda hata alır.
    /// </summary>
    public static IReadOnlyList<string> WheresizDmlBul(string sql)
    {
        if (YeniParser().Parse(new StringReader(sql), out IList<ParseError> hatalar) is not { } fragment
            || hatalar.Count > 0)
            return [];

        var ziyaretci = new WheresizZiyaretci();
        fragment.Accept(ziyaretci);
        return ziyaretci.Bulgular;
    }

    /// <summary>
    /// Script'teki ALTER / CREATE OR ALTER hedeflerini (SP/view/fonksiyon) bulur —
    /// FG-5.5: çalıştırmadan ÖNCE eski tanım yerel tarihçeye yedeklenir (V2-S8).
    /// Şema belirtilmemişse dbo varsayılır; çözümlenemeyen metinde boş liste.
    /// </summary>
    public static IReadOnlyList<(string Sema, string Ad)> AlterHedefleri(string sql)
    {
        if (YeniParser().Parse(new StringReader(sql), out IList<ParseError> hatalar) is not { } fragment
            || hatalar.Count > 0)
            return [];

        var ziyaretci = new AlterZiyaretci();
        fragment.Accept(ziyaretci);
        return ziyaretci.Hedefler;
    }

    private sealed class AlterZiyaretci : TSqlFragmentVisitor
    {
        public List<(string Sema, string Ad)> Hedefler { get; } = [];

        public override void Visit(TSqlFragment n)
        {
            SchemaObjectName? ad = n switch
            {
                AlterProcedureStatement p => p.ProcedureReference.Name,
                CreateOrAlterProcedureStatement p => p.ProcedureReference.Name,
                AlterViewStatement v => v.SchemaObjectName,
                CreateOrAlterViewStatement v => v.SchemaObjectName,
                AlterFunctionStatement f => f.Name,
                CreateOrAlterFunctionStatement f => f.Name,
                _ => null,
            };
            if (ad is not null)
                Hedefler.Add((ad.SchemaIdentifier?.Value ?? "dbo", ad.BaseIdentifier.Value));
        }
    }

    /// <summary>
    /// Sekmede seçili veritabanı DIŞINA yazan sorgu tespiti (kullanıcı isteği 2026-07-17):
    /// script YAZMA içeriyorsa ve üç parçalı ad (db.şema.nesne) ya da USE ile başka
    /// veritabanına dokunuyorsa o veritabanlarını döner — UI onay ister (varsayılan Hayır).
    /// Salt-okuma cross-DB sorguları (sys.* birleşimleri vb.) serbesttir.
    /// Çözümlenemeyen metin engellenmez (sunucu zaten hata verir).
    /// </summary>
    public static IReadOnlyList<string> FarkliVeritabaniYazmasi(string sql, string? seciliVeritabani)
    {
        if (string.IsNullOrWhiteSpace(seciliVeritabani)
            || YeniParser().Parse(new StringReader(sql), out IList<ParseError> hatalar) is not { } fragment
            || hatalar.Count > 0)
            return [];

        var ziyaretci = new FarkliDbZiyaretci();
        fragment.Accept(ziyaretci);
        if (!ziyaretci.YazmaVar)
            return [];

        return [.. ziyaretci.Veritabanlari
            .Where(db => !db.Equals(seciliVeritabani, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private sealed class FarkliDbZiyaretci : TSqlFragmentVisitor
    {
        public bool YazmaVar { get; private set; }
        public List<string> Veritabanlari { get; } = [];

        // ScriptDom deseni: her somut Visit varsayılan olarak bu catch-all'a düşer
        public override void Visit(TSqlFragment n)
        {
            switch (n)
            {
                case DataModificationStatement or TruncateTableStatement or ExecuteStatement:
                    YazmaVar = true;
                    break;
                case TSqlStatement st when st.GetType().Name is { } ad
                    && (ad.StartsWith("Create", StringComparison.Ordinal)
                        || ad.StartsWith("Alter", StringComparison.Ordinal)
                        || ad.StartsWith("Drop", StringComparison.Ordinal)):
                    YazmaVar = true;
                    break;
            }

            if (n is SchemaObjectName { DatabaseIdentifier.Value: { Length: > 0 } db })
                Veritabanlari.Add(db);
            if (n is UseStatement use)
                Veritabanlari.Add(use.DatabaseName.Value);
        }
    }

    private sealed class WheresizZiyaretci : TSqlFragmentVisitor
    {
        public List<string> Bulgular { get; } = [];

        public override void Visit(UpdateSpecification n) => Kontrol(n, "UPDATE", n.TopRowFilter);

        public override void Visit(DeleteSpecification n) => Kontrol(n, "DELETE", n.TopRowFilter);

        private void Kontrol(UpdateDeleteSpecificationBase n, string fiil, TopRowFilter? top)
        {
            if (n.WhereClause is not null || top is not null)
                return;

            string hedef = n.Target is NamedTableReference ntr
                ? string.Join(".", ntr.SchemaObject.Identifiers.Select(i => i.Value))
                : "(hedef)";
            Bulgular.Add($"Satır {n.StartLine}: {fiil} {hedef} — WHERE yok, TÜM satırlar etkilenir");
        }
    }
}
