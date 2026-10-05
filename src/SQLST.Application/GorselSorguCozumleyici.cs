using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLST.Application;

/// <summary>
/// Script → Görsel (v6-S5): bir <c>SELECT … JOIN … WHERE</c> T-SQL sorgusunu AYRIŞTIRIP görsel
/// tasarımcı modeline (<see cref="CozumlenmisSorgu"/>) çevirir. ScriptDom (T-SQL) tabanlıdır —
/// bu yüzden yalnız SQL Server sorgularında çalışır (diğer motorlarda çağıran gizler/uyarır).
///
/// <b>Kapsam:</b> FROM tabloları + JOIN'ler (INNER/LEFT/RIGHT/FULL) + basit WHERE koşulları
/// (karşılaştırma / LIKE / IS [NOT] NULL) + nitelenmiş SELECT kolonları. Alt sorgu, türetilmiş
/// tablo, fonksiyon, BETWEEN/IN gibi ayrıştırılamayan parçalar <see cref="CozumlenmisSorgu.Uyarilar"/>'a
/// yazılır ve atlanır — sorgu yine de mümkün olduğunca tuvale çıkar (kısmi de olsa değerlidir).
/// </summary>
public static class GorselSorguCozumleyici
{
    /// <summary>Ayrıştırır; başarısızsa null döner ve <paramref name="hata"/> doldurulur.</summary>
    public static CozumlenmisSorgu? Coz(string sql, out string hata)
    {
        hata = "";
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment kok = parser.Parse(new StringReader(sql), out IList<ParseError> hatalar);
        if (hatalar.Count > 0)
        {
            hata = $"SQL ayrıştırılamadı: {hatalar[0].Message}";
            return null;
        }

        SelectStatement? secim = IlkSelect(kok);
        if (secim?.QueryExpression is not QuerySpecification qs || qs.FromClause is null)
        {
            hata = "Yalnız tek bir SELECT sorgusu görsele çevrilebilir (alt sorgu/UNION değil).";
            return null;
        }

        var tablolar = new List<CozumlenmisTablo>();
        var joinler = new List<CozumlenmisJoin>();
        var uyarilar = new List<string>();

        foreach (TableReference tr in qs.FromClause.TableReferences)
            FromTopla(tr, tablolar, joinler, uyarilar);

        if (tablolar.Count == 0)
        {
            hata = "FROM'da adlandırılmış bir tablo bulunamadı (alt sorgu/fonksiyon olabilir).";
            return null;
        }

        var kosullar = new List<CozumlenmisKosul>();
        if (qs.WhereClause is { SearchCondition: { } koşul })
            WhereTopla(koşul, ilkVeya: false, kosullar, uyarilar);

        var secimler = new List<CozumlenmisSecim>();
        SelectTopla(qs, secimler, uyarilar);

        SorguCumleleriniUyar(qs, uyarilar);

        return new CozumlenmisSorgu(tablolar, joinler, kosullar, secimler, uyarilar);
    }

    /// <summary>
    /// Görsel model YALNIZCA düz <c>SELECT + JOIN + WHERE</c> tutar; sorgu-düzeyi cümleler
    /// (DISTINCT/TOP/GROUP BY/HAVING/ORDER BY) temsil EDİLEMEZ. Bunları sessizce düşürmek
    /// görsel sorgunun orijinalden FARKLI sonuç vermesine yol açar (kullanıcı bulgusu 2026-07-21:
    /// karmaşık sorgu görsele çevrildiğinde sonuç değişiyordu) → her biri açıkça uyarılır.
    /// </summary>
    private static void SorguCumleleriniUyar(QuerySpecification qs, List<string> uyarilar)
    {
        if (qs.UniqueRowFilter == UniqueRowFilter.Distinct)
            uyarilar.Add("DISTINCT atlandı — görsel sorgu tekrarlı satırları da getirir.");
        if (qs.TopRowFilter is not null)
            uyarilar.Add("TOP atlandı — görsel sorgu satır sayısını sınırlamaz.");
        if (qs.GroupByClause is not null)
            uyarilar.Add("GROUP BY atlandı — görsel sorgu gruplamaz/özetlemez (COUNT/SUM vb. yok olur).");
        if (qs.HavingClause is not null)
            uyarilar.Add("HAVING atlandı.");
        if (qs.OrderByClause is not null)
            uyarilar.Add("ORDER BY atlandı — sıralama korunmaz.");
    }

    private static SelectStatement? IlkSelect(TSqlFragment kok)
    {
        var bulucu = new SelectBulucu();
        kok.Accept(bulucu);
        return bulucu.Secim;
    }

    private sealed class SelectBulucu : TSqlFragmentVisitor
    {
        public SelectStatement? Secim { get; private set; }
        public override void Visit(SelectStatement node) => Secim ??= node;
    }

    // ── FROM + JOIN ─────────────────────────────────────────────────────────

    private static void FromTopla(
        TableReference tr, List<CozumlenmisTablo> tablolar, List<CozumlenmisJoin> joinler, List<string> uyarilar)
    {
        switch (tr)
        {
            case NamedTableReference n:
                tablolar.Add(TabloYap(n));
                break;

            case QualifiedJoin q:
                FromTopla(q.FirstTableReference, tablolar, joinler, uyarilar);
                FromTopla(q.SecondTableReference, tablolar, joinler, uyarilar);
                JoinYap(q, joinler, uyarilar);
                break;

            case JoinParenthesisTableReference jp:
                FromTopla(jp.Join, tablolar, joinler, uyarilar);
                break;

            case UnqualifiedJoin uj: // CROSS JOIN / CROSS APPLY
                FromTopla(uj.FirstTableReference, tablolar, joinler, uyarilar);
                FromTopla(uj.SecondTableReference, tablolar, joinler, uyarilar);
                break; // bağ bilgisi yok → yalnız tablolar (üretici CROSS JOIN ile köprüler)

            default:
                uyarilar.Add($"FROM öğesi çevrilemedi (alt sorgu/fonksiyon): {tr.GetType().Name}");
                break;
        }
    }

    private static CozumlenmisTablo TabloYap(NamedTableReference n)
    {
        IList<Identifier> parcalar = n.SchemaObject.Identifiers;
        string ad = parcalar[^1].Value;
        string? sema = parcalar.Count >= 2 ? parcalar[^2].Value : null;
        string takma = n.Alias?.Value ?? ad;
        return new CozumlenmisTablo(sema, ad, takma);
    }

    private static void JoinYap(QualifiedJoin q, List<CozumlenmisJoin> joinler, List<string> uyarilar)
    {
        JoinTuru tur = q.QualifiedJoinType switch
        {
            QualifiedJoinType.Inner => JoinTuru.Inner,
            QualifiedJoinType.LeftOuter => JoinTuru.Left,
            QualifiedJoinType.RightOuter => JoinTuru.Right,
            QualifiedJoinType.FullOuter => JoinTuru.Full,
            _ => JoinTuru.Inner,
        };

        var ciftler = new List<(string SolTakma, string SolKolon, string SagTakma, string SagKolon)>();
        OnTopla(q.SearchCondition, ciftler, uyarilar);
        if (ciftler.Count == 0)
        {
            // ON'u kolon eşitliğine indirgenemeyen (ör. alt sorgu) bir JOIN görsel modele giremez;
            // tablo bağsız kalır → üretici onu CROSS JOIN (KARTEZYEN) ile köprüler. Bu, sonucu
            // tümüyle değiştirir → açıkça uyar (kullanıcı bulgusu 2026-07-21).
            uyarilar.Add("Bir JOIN'in ON'u kolon eşitliği değil (alt sorgu/karmaşık) → tablo bağsız "
                + "kaldı, CROSS JOIN (kartezyen çarpım) olur — sonuç orijinalden çok farklı olur.");
            return;
        }

        string solTakma = ciftler[0].SolTakma, sagTakma = ciftler[0].SagTakma;
        var kolonlar = ciftler
            .Where(c => c.SolTakma == solTakma && c.SagTakma == sagTakma)
            .Select(c => new GorselKolonEsi(c.SolKolon, c.SagKolon))
            .ToList();
        joinler.Add(new CozumlenmisJoin(solTakma, sagTakma, tur, kolonlar));
    }

    /// <summary>ON koşulunu (AND'li) kolon-eşitliği çiftlerine indirger.</summary>
    private static void OnTopla(
        BooleanExpression? e, List<(string, string, string, string)> ciftler, List<string> uyarilar)
    {
        switch (e)
        {
            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } b:
                OnTopla(b.FirstExpression, ciftler, uyarilar);
                OnTopla(b.SecondExpression, ciftler, uyarilar);
                break;
            case BooleanParenthesisExpression p:
                OnTopla(p.Expression, ciftler, uyarilar);
                break;
            case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } c
                when Kolon(c.FirstExpression) is { } sol && Kolon(c.SecondExpression) is { } sag:
                ciftler.Add((sol.Takma, sol.Kolon, sag.Takma, sag.Kolon));
                break;
            default:
                if (e is not null)
                    uyarilar.Add("JOIN ON'unda kolon-eşitliği olmayan bir koşul atlandı.");
                break;
        }
    }

    // ── WHERE ───────────────────────────────────────────────────────────────

    private static void WhereTopla(
        BooleanExpression e, bool ilkVeya, List<CozumlenmisKosul> kosullar, List<string> uyarilar)
    {
        switch (e)
        {
            case BooleanBinaryExpression b:
                WhereTopla(b.FirstExpression, ilkVeya, kosullar, uyarilar);
                WhereTopla(b.SecondExpression, b.BinaryExpressionType == BooleanBinaryExpressionType.Or, kosullar, uyarilar);
                break;
            case BooleanParenthesisExpression p:
                WhereTopla(p.Expression, ilkVeya, kosullar, uyarilar);
                break;
            default:
                if (YaprakKosul(e, ilkVeya) is { } k)
                    kosullar.Add(k);
                else
                    uyarilar.Add("WHERE'de çevrilemeyen bir koşul atlandı (fonksiyon/BETWEEN/IN/alt sorgu).");
                break;
        }
    }

    private static CozumlenmisKosul? YaprakKosul(BooleanExpression e, bool veya) => e switch
    {
        BooleanComparisonExpression c when Kolon(c.FirstExpression) is { } k && Deger(c.SecondExpression) is { } d
            => new CozumlenmisKosul(k.Takma, k.Kolon, KarsilastirmaOp(c.ComparisonType), d, veya),

        BooleanIsNullExpression n when Kolon(n.Expression) is { } k
            => new CozumlenmisKosul(k.Takma, k.Kolon, n.IsNot ? KosulOperatoru.DoluDegil : KosulOperatoru.Bos, "", veya),

        LikePredicate { NotDefined: false } l when Kolon(l.FirstExpression) is { } k
            && l.SecondExpression is StringLiteral sl
            => LikeKosul(k.Takma, k.Kolon, sl.Value, veya),

        _ => null,
    };

    private static CozumlenmisKosul LikeKosul(string takma, string kolon, string desen, bool veya)
    {
        bool bas = desen.StartsWith('%'), son = desen.EndsWith('%');
        (KosulOperatoru op, string deger) = (bas, son) switch
        {
            (true, true) => (KosulOperatoru.Icerir, desen.Trim('%')),
            (true, false) => (KosulOperatoru.Biter, desen[1..]),
            (false, true) => (KosulOperatoru.Baslar, desen[..^1]),
            _ => (KosulOperatoru.Esit, desen), // joker yok → eşitlik gibi (LIKE ama sabit)
        };
        return new CozumlenmisKosul(takma, kolon, op, deger, veya);
    }

    private static KosulOperatoru KarsilastirmaOp(BooleanComparisonType t) => t switch
    {
        BooleanComparisonType.Equals => KosulOperatoru.Esit,
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => KosulOperatoru.Esitsiz,
        BooleanComparisonType.GreaterThan => KosulOperatoru.Buyuk,
        BooleanComparisonType.LessThan => KosulOperatoru.Kucuk,
        BooleanComparisonType.GreaterThanOrEqualTo => KosulOperatoru.BuyukEsit,
        BooleanComparisonType.LessThanOrEqualTo => KosulOperatoru.KucukEsit,
        _ => KosulOperatoru.Esit,
    };

    // ── SELECT kolonları ────────────────────────────────────────────────────

    private static void SelectTopla(QuerySpecification qs, List<CozumlenmisSecim> secimler, List<string> uyarilar)
    {
        foreach (SelectElement se in qs.SelectElements)
        {
            switch (se)
            {
                case SelectStarExpression:
                    return; // SELECT * → hiçbir kolon işaretlenmez (varsayılan)
                case SelectScalarExpression { Expression: ColumnReferenceExpression cr } when Kolon(cr) is { } k:
                    secimler.Add(new CozumlenmisSecim(k.Takma, k.Kolon));
                    break;
                default:
                    uyarilar.Add("Bir SELECT öğesi kolon değil (ifade/fonksiyon) — kolon seçimine yansıtılamadı.");
                    break;
            }
        }
    }

    // ── Ortak yardımcılar ───────────────────────────────────────────────────

    /// <summary>Kolon referansını (takma, kolon)'a indirger; nitelenmemişse takma boş kalır.</summary>
    private static (string Takma, string Kolon)? Kolon(ScalarExpression? e)
    {
        if (e is not ColumnReferenceExpression cr || cr.MultiPartIdentifier is not { } m || m.Count == 0)
            return null;
        string kolon = m.Identifiers[^1].Value;
        string takma = m.Count >= 2 ? m.Identifiers[^2].Value : "";
        return (takma, kolon);
    }

    /// <summary>Sağ taraftaki literali METİN değere indirger (string/sayı); değilse null.</summary>
    private static string? Deger(ScalarExpression? e) => e switch
    {
        StringLiteral s => s.Value,
        IntegerLiteral i => i.Value,
        NumericLiteral n => n.Value,
        RealLiteral r => r.Value,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: Literal l } => "-" + l.Value,
        _ => null,
    };
}
