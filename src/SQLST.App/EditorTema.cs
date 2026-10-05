using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace SQLST.App;

/// <summary>
/// Editör sözdizimi renklendirmesi (SQLST kendi T-SQL şeması, 2026-07-22). AvalonEdit'in gömülü
/// "TSQL" tanımı hem eksikti (NOLOCK/PARTITION/OVER/window fonksiyonları anahtar sözcük değildi)
/// hem de SAYILARI hiç boyamıyordu (kullanıcı isteği: "int sayı girince rengi farklı olsun").
/// Bu yüzden kapsamlı kendi şemamızı kuruyoruz. xshd fırçaları DynamicResource takasına
/// girmediğinden koyu tema için renkleri metinsel olarak koyu-zemin tonlarına çeviririz
/// (gömülü tema köprüsü).
///
/// 🎨 SSMS paritesi (kullanıcı 2026-09-23: "SSMS gibi güzel bir tasarıma ulaşamadık, editör
/// renkleri aynı değil"): açık tema artık SSMS'in varsayılan editör paleti — anahtar sözcük SAF
/// MAVİ ve KALINSIZ (eski hâl #1D4ED8 kalındı; SSMS hiçbir şeyi kalın yazmaz, "karışık" görünümün
/// bir parçası da buydu), 'metin' KIRMIZI, yorum yeşil, yerleşik fonksiyonlar MACENTA (ayrı grup —
/// eskiden anahtar sözcük mavisindeydi), operatörler GRİ (eskiden boyanmıyordu). Tek bilinçli
/// sapma: SAYILAR turuncu kalır — SSMS sayıyı siyah bırakır ama ayrı renk kullanıcının kendi
/// isteğiydi (2026-07-24 "int sayı girince rengi farklı olsun"); istenirse tek satırla siyaha döner.
/// </summary>
public static class EditorTema
{
    private const string IsikAd = "TSQL-SQLST";
    private const string KoyuAd = "TSQL-SQLST-Koyu";
    private static bool _kayitli;

    private const string XmlIsikAd = "XML-SQLST";
    private const string XmlKoyuAd = "XML-SQLST-Koyu";

    private const string JsonIsikAd = "JSON-SQLST";
    private const string JsonKoyuAd = "JSON-SQLST-Koyu";

    /// <summary>Aktif temaya uygun tanım. Şema yüklenemezse (beklenmez) gömülü "TSQL"e düşer.</summary>
    public static IHighlightingDefinition? Tanim(bool koyu)
    {
        Kaydet();
        return HighlightingManager.Instance.GetDefinition(koyu ? KoyuAd : IsikAd)
            ?? HighlightingManager.Instance.GetDefinition("TSQL");
    }

    /// <summary>
    /// Aktif temaya uygun XML tanımı (SOAP #13, 2026-07-29): ETİKET (mavi) · öznitelik adı (mor) ·
    /// öznitelik değeri (kırmızı) · yorum (yeşil) boyanır; ETİKET DIŞI İÇERİK (kullanıcının girdiği
    /// değerler) editörün ön planına bırakılır — çağıran onu "değer rengi"ne (amber) boyar → etiket ile
    /// değer gözle ayrışır. Renkler iki temada da okunur. Yüklenemezse gömülü "XML"e düşer.
    /// </summary>
    public static IHighlightingDefinition? XmlTanim(bool koyu)
    {
        Kaydet();
        return HighlightingManager.Instance.GetDefinition(koyu ? XmlKoyuAd : XmlIsikAd)
            ?? HighlightingManager.Instance.GetDefinition("XML");
    }

    /// <summary>
    /// Aktif temaya uygun JSON tanımı (REST İstemcisi v20-S8): ANAHTAR (mavi, kalın) · string değer (yeşil)
    /// · sayı (turuncu) · true/false/null (mor). Anahtar, string değerden ':' ileri-bakışıyla ayrılır.
    /// İki temada da okunur; yüklenemezse gömülü "XML"e (renksiz-yakın) düşer.
    /// </summary>
    public static IHighlightingDefinition? JsonTanim(bool koyu)
    {
        Kaydet();
        return HighlightingManager.Instance.GetDefinition(koyu ? JsonKoyuAd : JsonIsikAd)
            ?? HighlightingManager.Instance.GetDefinition("XML");
    }

    private static void Kaydet()
    {
        if (_kayitli)
            return;
        _kayitli = true;

        // Açık tema doğrudan (SSMS varsayılan paleti); koyu tema aynı şemanın renkleri VS/SSMS koyu
        // temasının karşılıklarına çevrilmiş hâli (saf mavi/kırmızı koyu zeminde okunmaz).
        Yukle(IsikAd, Xshd, [".sql"]);
        Yukle(KoyuAd, Xshd
            .Replace("name=\"TSQL-SQLST\"", "name=\"TSQL-SQLST-Koyu\"") // iç ad da anahtarla aynı olsun
            .Replace("#008000", "#6A9955")   // yorum
            .Replace("#FF0000", "#D69D85")   // 'metin' (VS koyu string tonu)
            .Replace("#EA580C", "#FB923C")   // sayı (koyu temada aydınlık turuncu)
            .Replace("#0000FF", "#569CD6")   // anahtar sözcük (VS koyu keyword mavisi)
            .Replace("#FF00FF", "#C586C0")   // yerleşik fonksiyon (koyu zeminde yumuşak macenta)
            .Replace("#808080", "#9CA3AF"), [".sql"]);  // operatör (koyu zeminde okunur gri)

        // XML (SOAP #13, 2026-07-29): etiket/öznitelik/yorum boyalı; içerik editör ön planına.
        // Koyu tema renkleri aynı köprüyle aydınlık tonlara çevrilir.
        Yukle(XmlIsikAd, XmlXshd, [".xml"]);
        Yukle(XmlKoyuAd, XmlXshd
            .Replace("name=\"XML-SQLST\"", "name=\"XML-SQLST-Koyu\"")
            .Replace("#008000", "#6A9955")   // yorum
            .Replace("#767676", "#9CA3AF")   // CDATA
            .Replace("#1D4ED8", "#60A5FA")   // etiket (mavi → koyuda aydınlık)
            .Replace("#C81E1E", "#F08A8A")   // öznitelik adı (kırmızı → koyuda aydınlık)
            .Replace("#2563EB", "#7AA2F7")   // öznitelik değeri (mavi → koyuda aydınlık)
            .Replace("#B8860B", "#DCDCAA"), [".xml"]); // entity

        // JSON (REST v20-S8): anahtar/string/sayı/bool boyalı; koyu tema aynı köprüyle aydınlık tonlara.
        Yukle(JsonIsikAd, JsonXshd, [".json"]);
        Yukle(JsonKoyuAd, JsonXshd
            .Replace("name=\"JSON-SQLST\"", "name=\"JSON-SQLST-Koyu\"")
            .Replace("#0369A1", "#7DD3FC")   // anahtar (mavi → koyuda aydınlık)
            .Replace("#15803D", "#86EFAC")   // string değer (yeşil → koyuda aydınlık)
            .Replace("#B45309", "#FBBF24")   // sayı (turuncu → koyuda aydınlık)
            .Replace("#7C3AED", "#C4B5FD"), [".json"]); // true/false/null (mor → koyuda aydınlık)
    }

    /// <summary>Tek bir xshd metnini yükleyip verilen adla kaydeder. Parse hatası olursa sessizce
    /// atlar (renklendirmesiz düz metin, çökmekten iyidir) — dar kapsamlı IO/parse adaptörü.</summary>
    private static void Yukle(string ad, string xshd, string[] uzantilar)
    {
        try
        {
            using var xml = XmlReader.Create(new StringReader(xshd));
            IHighlightingDefinition tanim = HighlightingLoader.Load(xml, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(ad, uzantilar, tanim);
        }
        catch (Exception e) when (e is XmlException or HighlightingDefinitionInvalidException)
        {
            // şema bozuksa kaydı atla; Tanim()/XmlTanim() gömülü tanıma düşer (renksiz ama çökmez)
        }
    }

    // Açık tema = SSMS varsayılan paleti (kullanıcı 2026-09-23: "editör renkleri SSMS'le aynı
    // değil"; koyu türev Kaydet()'te üretilir):
    //   Anahtar sözcük #0000FF (saf mavi, KALINSIZ — SSMS hiçbir şeyi kalın yazmaz) ·
    //   'metin' #FF0000 · yorum #008000 · yerleşik fonksiyon #FF00FF (macenta) · operatör #808080 ·
    //   Sayı #EA580C — SSMS'ten TEK bilinçli sapma: SSMS sayıyı siyah bırakır, ayrı renk
    //   kullanıcının kendi isteğiydi (2026-07-24 "int sayı girince rengi farklı olsun").
    // (Önceki hâl — 2026-07-24 "mavisi daha vurgulu" isteğiyle #1D4ED8 KALIN idi; 2026-09-23
    //  SSMS-parite isteği onu geçersiz kıldı.)
    // Sözcük listesi bilerek GENİŞ: cümlecikler + join + window + mantık + DDL + programlama +
    // tablo ipuçları (NOLOCK…) + veri tipleri; yerleşik fonksiyonlar AYRI (macenta) grupta.
    private const string Xshd = """
        <SyntaxDefinition name="TSQL-SQLST" extensions=".sql"
                          xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment"   foreground="#008000" />
          <Color name="String"    foreground="#FF0000" />
          <Color name="Number"    foreground="#EA580C" />
          <Color name="Keyword"   foreground="#0000FF" />
          <Color name="Fonksiyon" foreground="#FF00FF" />
          <Color name="Operator"  foreground="#808080" />

          <RuleSet ignoreCase="true">
            <Span color="Comment" begin="--" />
            <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
            <Span color="String"  multiline="true" begin="'" end="'" />

            <!-- SAYILAR: tam sayi, ondalik, 0x onaltilik. Kelime icindeki rakam (Kolon1) haric.
                 Regex TEK satir ve bosluksuz: IgnorePatternWhitespace olsun olmasin dogru calissin. -->
            <Rule color="Number">\b0[xX][0-9a-fA-F]+\b|\b\d+(\.\d+)?([eE][+\-]?\d+)?\b</Rule>

            <!-- OPERATÖRLER (SSMS grisi). Cift tire yorumunu ve /* blogunu yukaridaki span'ler
                 bu kuraldan ONCE geldigi icin yutar; tek tire, bolu, yildiz operatör kalir.
                 (DIKKAT: XML yorumu icinde cift tire yazilamaz - ilk deneme tam bu yuzden
                 XmlException'la sessizce gomulu TSQL'e dustu, testler yakaladi.) -->
            <Rule color="Operator">[+\-*/%=!~^&amp;|&lt;&gt;]+</Rule>

            <Keywords color="Keyword">
              <!-- Cümlecikler / sorgu -->
              <Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>GROUP</Word><Word>BY</Word>
              <Word>HAVING</Word><Word>ORDER</Word><Word>ASC</Word><Word>DESC</Word><Word>DISTINCT</Word>
              <Word>TOP</Word><Word>PERCENT</Word><Word>TIES</Word><Word>AS</Word><Word>INTO</Word>
              <Word>INSERT</Word><Word>UPDATE</Word><Word>DELETE</Word><Word>MERGE</Word><Word>USING</Word>
              <Word>MATCHED</Word><Word>VALUES</Word><Word>SET</Word><Word>OUTPUT</Word><Word>WITH</Word>
              <Word>UNION</Word><Word>ALL</Word><Word>EXCEPT</Word><Word>INTERSECT</Word>
              <Word>OFFSET</Word><Word>FETCH</Word><Word>FIRST</Word><Word>NEXT</Word><Word>ONLY</Word>
              <Word>PIVOT</Word><Word>UNPIVOT</Word><Word>FOR</Word><Word>JSON</Word><Word>PATH</Word>
              <!-- Join -->
              <Word>JOIN</Word><Word>INNER</Word><Word>LEFT</Word><Word>RIGHT</Word><Word>FULL</Word>
              <Word>OUTER</Word><Word>CROSS</Word><Word>APPLY</Word><Word>ON</Word><Word>PIVOT</Word>
              <!-- Window / analitik (çerçeve sözcükleri; RANK/LAG gibi FONKSİYONLAR macenta grupta) -->
              <Word>OVER</Word><Word>PARTITION</Word><Word>RANGE</Word><Word>ROWS</Word>
              <Word>UNBOUNDED</Word><Word>PRECEDING</Word><Word>FOLLOWING</Word><Word>CURRENT</Word>
              <Word>ROW</Word>
              <!-- Mantık / koşul -->
              <Word>AND</Word><Word>OR</Word><Word>NOT</Word><Word>IN</Word><Word>EXISTS</Word>
              <Word>BETWEEN</Word><Word>LIKE</Word><Word>IS</Word><Word>NULL</Word><Word>ANY</Word>
              <Word>SOME</Word><Word>CASE</Word><Word>WHEN</Word><Word>THEN</Word><Word>ELSE</Word>
              <Word>END</Word><Word>IIF</Word><Word>COLLATE</Word><Word>ESCAPE</Word>
              <!-- Dönüşüm / DDL (CAST ANSI sözcüğü olarak SSMS'te MAVİDİR; CONVERT ailesi macenta grupta) -->
              <Word>CAST</Word><Word>CREATE</Word><Word>ALTER</Word><Word>DROP</Word><Word>TRUNCATE</Word>
              <Word>ADD</Word><Word>COLUMN</Word><Word>TABLE</Word><Word>VIEW</Word><Word>INDEX</Word>
              <Word>PROCEDURE</Word><Word>PROC</Word><Word>FUNCTION</Word><Word>TRIGGER</Word>
              <Word>SCHEMA</Word><Word>DATABASE</Word><Word>SEQUENCE</Word><Word>TYPE</Word>
              <Word>CONSTRAINT</Word><Word>PRIMARY</Word><Word>KEY</Word><Word>FOREIGN</Word>
              <Word>REFERENCES</Word><Word>DEFAULT</Word><Word>CHECK</Word><Word>UNIQUE</Word>
              <Word>CLUSTERED</Word><Word>NONCLUSTERED</Word><Word>IDENTITY</Word><Word>COMPUTED</Word>
              <Word>PERSISTED</Word><Word>FILESTREAM</Word><Word>CASCADE</Word>
              <!-- Programlama / akış -->
              <Word>DECLARE</Word><Word>EXEC</Word><Word>EXECUTE</Word><Word>GO</Word><Word>BEGIN</Word>
              <Word>IF</Word><Word>WHILE</Word><Word>BREAK</Word><Word>CONTINUE</Word><Word>RETURN</Word>
              <Word>GOTO</Word><Word>WAITFOR</Word><Word>THROW</Word><Word>TRY</Word><Word>CATCH</Word>
              <Word>RAISERROR</Word><Word>PRINT</Word><Word>CURSOR</Word><Word>OPEN</Word><Word>CLOSE</Word>
              <Word>DEALLOCATE</Word><Word>TRANSACTION</Word><Word>TRAN</Word><Word>COMMIT</Word>
              <Word>ROLLBACK</Word><Word>SAVE</Word>
              <!-- Tablo ipuçları / seçenekler (NOLOCK ve arkadaşları) -->
              <Word>NOLOCK</Word><Word>READPAST</Word><Word>ROWLOCK</Word><Word>PAGLOCK</Word>
              <Word>TABLOCK</Word><Word>TABLOCKX</Word><Word>UPDLOCK</Word><Word>XLOCK</Word>
              <Word>HOLDLOCK</Word><Word>READCOMMITTED</Word><Word>READUNCOMMITTED</Word>
              <Word>REPEATABLEREAD</Word><Word>SERIALIZABLE</Word><Word>SNAPSHOT</Word>
              <Word>FORCESEEK</Word><Word>FORCESCAN</Word><Word>RECOMPILE</Word><Word>OPTIMIZE</Word>
              <Word>MAXDOP</Word><Word>FAST</Word><Word>OPTION</Word><Word>FILLFACTOR</Word>
              <!-- Veri tipleri -->
              <Word>BIT</Word><Word>TINYINT</Word><Word>SMALLINT</Word><Word>INT</Word><Word>BIGINT</Word>
              <Word>DECIMAL</Word><Word>NUMERIC</Word><Word>MONEY</Word><Word>SMALLMONEY</Word>
              <Word>FLOAT</Word><Word>REAL</Word><Word>DATE</Word><Word>DATETIME</Word><Word>DATETIME2</Word>
              <Word>SMALLDATETIME</Word><Word>DATETIMEOFFSET</Word><Word>TIME</Word><Word>CHAR</Word>
              <Word>VARCHAR</Word><Word>TEXT</Word><Word>NCHAR</Word><Word>NVARCHAR</Word><Word>NTEXT</Word>
              <Word>BINARY</Word><Word>VARBINARY</Word><Word>IMAGE</Word><Word>UNIQUEIDENTIFIER</Word>
              <Word>SQL_VARIANT</Word><Word>XML</Word><Word>ROWVERSION</Word><Word>TIMESTAMP</Word>
              <Word>HIERARCHYID</Word><Word>GEOGRAPHY</Word><Word>GEOMETRY</Word>
              <!-- ANSI koşul sözcükleri (SSMS'te MAVİ kalır; ISNULL ise sistem fonksiyonu → macenta) -->
              <Word>COALESCE</Word><Word>NULLIF</Word>
            </Keywords>

            <!-- YERLEŞİK FONKSİYONLAR (SSMS "SQL system function" macentası) — eskiden anahtar
                 sözcük mavisindeydiler; SSMS bunları ayrı boyar, parite için ayrıldılar. -->
            <Keywords color="Fonksiyon">
              <!-- Toplama / analitik -->
              <Word>COUNT</Word><Word>COUNT_BIG</Word><Word>SUM</Word><Word>AVG</Word><Word>MIN</Word>
              <Word>MAX</Word><Word>STDEV</Word><Word>STDEVP</Word><Word>VAR</Word><Word>VARP</Word>
              <Word>GROUPING</Word><Word>STRING_AGG</Word>
              <!-- Window / sıralama fonksiyonları -->
              <Word>ROW_NUMBER</Word><Word>RANK</Word><Word>DENSE_RANK</Word><Word>NTILE</Word>
              <Word>LAG</Word><Word>LEAD</Word><Word>FIRST_VALUE</Word><Word>LAST_VALUE</Word>
              <Word>CUME_DIST</Word><Word>PERCENT_RANK</Word><Word>PERCENTILE_CONT</Word>
              <Word>PERCENTILE_DISC</Word>
              <!-- Dönüşüm -->
              <Word>CONVERT</Word><Word>TRY_CAST</Word><Word>TRY_CONVERT</Word><Word>PARSE</Word>
              <!-- Tarih / metin / matematik / sistem -->
              <Word>ISNULL</Word><Word>GETDATE</Word><Word>GETUTCDATE</Word><Word>SYSDATETIME</Word>
              <Word>DATEADD</Word><Word>DATEDIFF</Word><Word>DATEPART</Word><Word>DATENAME</Word>
              <Word>YEAR</Word><Word>MONTH</Word><Word>DAY</Word><Word>FORMAT</Word><Word>LEN</Word>
              <Word>SUBSTRING</Word><Word>CHARINDEX</Word><Word>PATINDEX</Word><Word>REPLACE</Word>
              <Word>STUFF</Word><Word>CONCAT</Word><Word>CONCAT_WS</Word><Word>UPPER</Word><Word>LOWER</Word>
              <Word>LTRIM</Word><Word>RTRIM</Word><Word>TRIM</Word><Word>REVERSE</Word><Word>REPLICATE</Word>
              <Word>ABS</Word><Word>CEILING</Word><Word>FLOOR</Word><Word>ROUND</Word><Word>POWER</Word>
              <Word>SQRT</Word><Word>NEWID</Word><Word>ISNUMERIC</Word><Word>ISDATE</Word>
              <Word>OBJECT_ID</Word><Word>SCOPE_IDENTITY</Word><Word>ROW_COUNT</Word>
            </Keywords>
          </RuleSet>
        </SyntaxDefinition>
        """;

    // XML (SOAP #13): ETİKET (mavi) span içerik-dışını sarar → etiket adı + '<' '>' '/' mavi olur;
    // öznitelik adı (mor) + değeri (kırmızı) tag içinde; yorum yeşil; CDATA gri; entity altın.
    // Etiketler DIŞINDAKİ metin (kullanıcının girdiği DEĞERLER) hiçbir span'e girmez → editör ön
    // planına (çağıran "değer rengi"ne = amber boyar) düşer. Böylece etiket ve değer gözle ayrışır.
    private const string XmlXshd = """
        <SyntaxDefinition name="XML-SQLST" extensions=".xml"
                          xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment"    foreground="#008000" />
          <Color name="Cdata"      foreground="#767676" />
          <Color name="Tag"        foreground="#1D4ED8" fontWeight="bold" />
          <Color name="AttrName"   foreground="#C81E1E" />
          <Color name="AttrValue"  foreground="#2563EB" />
          <Color name="Entity"     foreground="#B8860B" />

          <RuleSet>
            <Span color="Comment" multiline="true" begin="&lt;!--" end="--&gt;" />
            <Span color="Cdata"   multiline="true" begin="&lt;!\[CDATA\[" end="]]&gt;" />
            <Span color="Tag"     multiline="true" begin="&lt;" end="&gt;">
              <RuleSet>
                <Span color="AttrValue" multiline="true" begin="&quot;" end="&quot;" />
                <Span color="AttrValue" multiline="true" begin="'" end="'" />
                <Rule color="AttrName">[\d\w_\-\.:]+(?=\s*=)</Rule>
              </RuleSet>
            </Span>
            <Rule color="Entity">&amp;[\w\#]+;</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    // JSON (REST v20-S8): ANAHTAR = tırnak içi + ardından ':' (ileri-bakış) → mavi/kalın; kalan string'ler
    // (değerler) yeşil; sayı turuncu; true/false/null mor. Koyu tema Kaydet()'te renk takasıyla üretilir.
    private const string JsonXshd = """
        <SyntaxDefinition name="JSON-SQLST" extensions=".json"
                          xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Key"    foreground="#0369A1" fontWeight="bold" />
          <Color name="String" foreground="#15803D" />
          <Color name="Number" foreground="#B45309" />
          <Color name="Bool"   foreground="#7C3AED" fontWeight="bold" />

          <RuleSet>
            <!-- Anahtar: tırnaklı ad + ardından ':' — string DEĞER'den önce gelir (öncelik). -->
            <Rule color="Key">"(\\.|[^"\\])*"(?=\s*:)</Rule>
            <!-- String değer -->
            <Span color="String" multiline="true" begin="&quot;" end="&quot;">
              <RuleSet><Span begin="\\" end="." /></RuleSet>
            </Span>
            <Keywords color="Bool"><Word>true</Word><Word>false</Word><Word>null</Word></Keywords>
            <Rule color="Number">\b-?\d+(\.\d+)?([eE][+\-]?\d+)?\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
