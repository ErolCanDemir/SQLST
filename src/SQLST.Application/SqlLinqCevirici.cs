using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLST.Application;

/// <summary>Ters çeviri çıktısı: üretilen C# LINQ metni + (varsa) uyarılar.</summary>
public sealed record SqlLinqSonucu(string Linq, IReadOnlyList<string> Uyarilar);

/// <summary>
/// 🔁 SQL → LINQ ters çeviri MOTORU (v20-S12): ScriptDom (V2-S3'ten beri bağımlılık) T-SQL'i
/// AST'ye çözer, LINQ metnini BİZ üretiriz — AI yok, deterministik. Çıktı query syntax'tır
/// (çok JOIN'de doğal okunur); Take/Skip/Distinct metod eki olarak gelir. Tam LINQ karşılığı
/// ÜRETİLEMEYEN yapı (LEFT JOIN, UNION, EXISTS, CTE, pencere fonksiyonu…) NET hatayla reddedilir —
/// yaklaşık çeviri yazılmaz (sessiz yanlış yasak). Girdi T-SQL'dir; ortak SELECT alt kümesi
/// diğer motorların sorgularını da kapsar (LIMIT gibi motor-özel ekler ayrıştırma hatası verir).
/// </summary>
public static class SqlLinqCevirici
{
    public static SqlLinqSonucu Cevir(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment parca = parser.Parse(new StringReader(sql), out IList<ParseError> hatalar);
        if (hatalar.Count > 0)
            throw new LinqCeviriHatasi(
                $"SQL ayrıştırılamadı (satır {hatalar[0].Line}): {hatalar[0].Message}");

        List<TSqlStatement> ifadeler = [.. ((TSqlScript)parca).Batches.SelectMany(b => b.Statements)];
        if (ifadeler.Count != 1)
            throw new LinqCeviriHatasi("Tek bir SELECT ifadesi bekleniyordu.");
        if (ifadeler[0] is not SelectStatement secim)
            throw new LinqCeviriHatasi($"Yalnız SELECT çevrilir — {ifadeler[0].GetType().Name} desteklenmiyor.");

        // CTE'ler (v20-S20): her biri "var ad = (…);" ön tanımına çevrilir; sonrakiler öncekilere,
        // ana sorgu hepsine başvurabilir. Özyineleme (kendine başvuru) net hatayla reddedilir.
        var degiskenler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onTanimlar = new List<string>();
        var uyarilar = new List<string>();
        foreach (CommonTableExpression cte in
            secim.WithCtesAndXmlNamespaces?.CommonTableExpressions ?? [])
        {
            string ad = cte.ExpressionName.Value;
            if (cte.Columns.Count > 0)
                throw new LinqCeviriHatasi(
                    $"CTE kolon listesi (WITH {ad} (…) AS) çevrilmiyor — adları SELECT içinde AS ile verin.");
            CeviriParcasi ic = SorguIfadesiCevir(cte.QueryExpression, degiskenler, yasakliAd: ad);
            onTanimlar.AddRange(ic.OnTanimlar);
            onTanimlar.Add($"var {ad} = (\n{Girintile(ic.Govde)});");
            uyarilar.AddRange(ic.Uyarilar);
            degiskenler.Add(ad);
        }

        CeviriParcasi ana = SorguIfadesiCevir(secim.QueryExpression, degiskenler, yasakliAd: null);
        onTanimlar.AddRange(ana.OnTanimlar);
        uyarilar.AddRange(ana.Uyarilar);
        string linq = onTanimlar.Count == 0
            ? ana.Govde
            : string.Join("\n\n", onTanimlar) + "\n\n" + ana.Govde;
        // UNION dallarında aynı uyarı (@TalepNo) tekrarlanır — tekilleştir (v20-S21 saha m.8).
        return new SqlLinqSonucu(linq, [.. uyarilar.Distinct()]);
    }

    /// <summary>Ara çeviri parçası: hoist edilen "var x = (…);" blokları + gövde LINQ'i + uyarılar.</summary>
    private sealed record CeviriParcasi(List<string> OnTanimlar, string Govde, List<string> Uyarilar);

    /// <summary>QuerySpecification → tek Uretici; UNION/INTERSECT/EXCEPT → parçalar ayrı çevrilip
    /// Union/Concat/Intersect/Except ile bağlanır (v20-S18 — küme işleçleri artık çevriliyor).</summary>
    private static CeviriParcasi SorguIfadesiCevir(
        QueryExpression ifade, HashSet<string> degiskenler, string? yasakliAd)
    {
        switch (ifade)
        {
            case QuerySpecification sorgu:
                return new Uretici(degiskenler, yasakliAd).Uret(sorgu);

            case QueryParenthesisExpression p:
                return SorguIfadesiCevir(p.QueryExpression, degiskenler, yasakliAd);

            case BinaryQueryExpression ikili:
            {
                if (ikili.OrderByClause is not null || ikili.OffsetClause is not null)
                    throw new LinqCeviriHatasi(
                        "Küme sorgusunda (UNION/…) ORDER BY çevrilmiyor — sıralamayı LINQ'ta sona ekleyin.");
                CeviriParcasi sol = SorguIfadesiCevir(ikili.FirstQueryExpression, degiskenler, yasakliAd);
                CeviriParcasi sag = SorguIfadesiCevir(ikili.SecondQueryExpression, degiskenler, yasakliAd);
                string metod = (ikili.BinaryQueryExpressionType, ikili.All) switch
                {
                    (BinaryQueryExpressionType.Union, true) => "Concat",   // UNION ALL: tekrarlar kalır
                    (BinaryQueryExpressionType.Union, false) => "Union",
                    (BinaryQueryExpressionType.Intersect, _) => "Intersect",
                    (BinaryQueryExpressionType.Except, _) => "Except",
                    _ => throw new LinqCeviriHatasi($"Küme işleci çevrilemedi: {ikili.BinaryQueryExpressionType}"),
                };
                string linq = $"({Girintile(sol.Govde)}\n).{metod}(\n{Girintile(sag.Govde)}\n)";
                return new CeviriParcasi(
                    [.. sol.OnTanimlar, .. sag.OnTanimlar], linq, [.. sol.Uyarilar, .. sag.Uyarilar]);
            }

            default:
                throw new LinqCeviriHatasi($"Sorgu biçimi çevrilemedi: {ifade.GetType().Name}");
        }
    }

    private static string Girintile(string metin) => "  " + metin.Replace("\n", "\n  ");

    // ── üretim bağlamı ──────────────────────────────────────────────────────────────────────

    private sealed class Uretici(HashSet<string> kaynakDegiskenleri, string? yasakliAd)
    {
        private readonly List<string> _uyarilar = [];
        /// <summary>Türetilmiş tablolardan hoist edilen "var x = (…);" blokları (v20-S20).</summary>
        private readonly List<string> _onTanimlar = [];
        /// <summary>SQL takma adı (küçük) → LINQ aralık değişkeni.</summary>
        private readonly Dictionary<string, string> _takma = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Grup anahtarı ifadeleri (normalize SQL) → LINQ karşılığı (g.Key / g.Key.Alan).</summary>
        private readonly Dictionary<string, string> _grupAnahtar = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>SELECT takma adı → altındaki ifade (ORDER BY GenelCiro gibi başvurular için).</summary>
        private readonly Dictionary<string, ScalarExpression> _secimTakma = new(StringComparer.OrdinalIgnoreCase);
        private bool _grupModu;
        private string? _tekTakma; // tek tablolu sorguda niteliksiz kolonlar buna bağlanır

        public CeviriParcasi Uret(QuerySpecification q)
        {
            // SELECT takma adları ÖNCE toplanır: ORDER BY/HAVING bunlara adla başvurabilir
            foreach (SelectElement se in q.SelectElements)
            {
                if (se is SelectScalarExpression { ColumnName.Value: { } ad } s)
                    _secimTakma[ad] = s.Expression;
            }

            var satirlar = new List<string>();
            KaynaklariYaz(q, satirlar);

            if (q.WhereClause is { } w)
                satirlar.Add($"where {Kosul(w.SearchCondition)}");

            if (q.GroupByClause is { } g)
                GrupYaz(g, satirlar);
            if (q.HavingClause is { } h)
                satirlar.Add($"where {Kosul(h.SearchCondition)}"); // grup modunda: HAVING → into g sonrası where

            if (q.OrderByClause is { } o)
                satirlar.Add("orderby " + string.Join(", ", o.OrderByElements.Select(e =>
                    Ifade(e.Expression) + (e.SortOrder == SortOrder.Descending ? " descending" : ""))));

            satirlar.Add($"select {SecimYaz(q)}");

            string govde = string.Join("\n", satirlar);
            string ekler = Ekler(q);
            string linq = ekler.Length == 0 ? govde : $"({govde}){ekler}";
            return new CeviriParcasi(_onTanimlar, linq, _uyarilar);
        }

        // ---- kaynaklar (FROM + JOIN) ----

        /// <summary>FROM/JOIN kaynağı: tablo (db.Ad) ya da DEĞİŞKEN (CTE/türetilmiş — yalın ad).</summary>
        private sealed record Kaynak(string Ad, string? Alias, bool Degisken, BooleanExpression? On, bool Sol);

        private void KaynaklariYaz(QuerySpecification q, List<string> satirlar)
        {
            if (q.FromClause is not { TableReferences.Count: >= 1 })
                throw new LinqCeviriHatasi("FROM bulunamadı — tablo(lar) olan bir SELECT bekleniyordu.");

            // Virgüllü FROM (v20-S18): her referans ayrı zincirdir; aralar CROSS (çoklu from) olur —
            // eski stil "FROM A a, B b WHERE a.x = b.y" böylece çevrilir (koşul where'e düşer).
            var duz = new List<Kaynak>();
            foreach (TableReference referans in q.FromClause.TableReferences)
                Duzlestir(referans, duz);

            Kaynak ana = duz[0];
            string anaDeg = Kaydet(ana);
            _tekTakma = duz.Count == 1 ? anaDeg : null;
            satirlar.Add($"from {anaDeg} in {KaynakMetni(ana)}");

            foreach (Kaynak k in duz.Skip(1))
            {
                string deg = Kaydet(k);
                if (k.On is null) // virgüllü FROM bacağı → cross join = ikinci from
                {
                    satirlar.Add($"from {deg} in {KaynakMetni(k)}");
                    continue;
                }
                (string solAnahtar, string sagAnahtar, List<BooleanExpression> ekler) = OnAnahtarlari(k.On, deg);

                // v20-S21 saha m.8: ON'daki anahtar-dışı koşullar — YALNIZ iç tabloya değenler join
                // kaynağının .Where'ine (LEFT'te de doğru semantik: iç taraf join'den ÖNCE süzülür);
                // dışa değenler INNER'da where satırına (eşdeğer), LEFT'te net hata (dış satır elenirdi).
                string kaynakMetni = KaynakMetni(k);
                var disKosullar = new List<string>();
                if (ekler.Count > 0)
                {
                    string icSqlTakma = k.Alias ?? k.Ad;
                    var icKosullar = new List<string>();
                    foreach (BooleanExpression ek in ekler)
                    {
                        HashSet<string>? nit = Niteleyiciler(ek);
                        bool yalnizIc = nit is not null
                            && nit.All(n => string.Equals(n, icSqlTakma, StringComparison.OrdinalIgnoreCase));
                        if (yalnizIc)
                            icKosullar.Add(Kosul(ek));
                        else if (!k.Sol)
                            disKosullar.Add(Kosul(ek));
                        else
                            throw new LinqCeviriHatasi(
                                "LEFT JOIN ON'unda dış tabloya değen (ya da niteliksiz) koşul çevrilemiyor — "
                                + "koşulu iç tablo takma adıyla niteleyin ya da WHERE'e taşıyın.");
                    }
                    if (icKosullar.Count > 0)
                        kaynakMetni = $"{kaynakMetni}.Where({deg} => {string.Join(" && ", icKosullar)})";
                }

                if (k.Sol)
                {
                    // LEFT JOIN (v20-S18): LINQ'un kalıbı GroupJoin + DefaultIfEmpty —
                    // eşleşmeyen dış satırda {deg} null gelir (SQL'deki NULL kolonların karşılığı).
                    satirlar.Add($"join {deg} in {kaynakMetni} on {solAnahtar} equals {sagAnahtar} into {deg}Grup");
                    satirlar.Add($"from {deg} in {deg}Grup.DefaultIfEmpty()");
                }
                else
                {
                    satirlar.Add($"join {deg} in {kaynakMetni} on {solAnahtar} equals {sagAnahtar}");
                }
                foreach (string dis in disKosullar)
                    satirlar.Add($"where {dis}");
            }
        }

        private static string KaynakMetni(Kaynak k) => k.Degisken ? k.Ad : $"db.{k.Ad}";

        /// <summary>Sol-derin JOIN ağacını sıraya açar; INNER + LEFT çevrilir, RIGHT/FULL net hatayla
        /// reddedilir. Türetilmiş tablo (v20-S20) "var alias = (…);" olarak hoist edilir.</summary>
        private void Duzlestir(TableReference t, List<Kaynak> hedef)
        {
            switch (t)
            {
                case NamedTableReference n:
                    hedef.Add(KaynakKur(n, null, false));
                    break;
                case QueryDerivedTable qd:
                    hedef.Add(TuretilmisKaynak(qd, null, false));
                    break;
                case QualifiedJoin { QualifiedJoinType: QualifiedJoinType.Inner or QualifiedJoinType.LeftOuter } j:
                {
                    Duzlestir(j.FirstTableReference, hedef);
                    bool sol = j.QualifiedJoinType == QualifiedJoinType.LeftOuter;
                    hedef.Add(j.SecondTableReference switch
                    {
                        NamedTableReference ic => KaynakKur(ic, j.SearchCondition, sol),
                        QueryDerivedTable icQd => TuretilmisKaynak(icQd, j.SearchCondition, sol),
                        _ => throw new LinqCeviriHatasi("JOIN'in sağ tarafı tablo ya da (SELECT …) alias olmalı."),
                    });
                    break;
                }
                case QualifiedJoin { QualifiedJoinType: QualifiedJoinType.RightOuter }:
                    throw new LinqCeviriHatasi(
                        "RIGHT JOIN çevrilmiyor — tablo sırasını değiştirip LEFT JOIN yazın (o çevrilir).");
                case QualifiedJoin j:
                    throw new LinqCeviriHatasi($"{j.QualifiedJoinType} JOIN'in LINQ karşılığı üretilmiyor.");
                default:
                    throw new LinqCeviriHatasi($"FROM ögesi çevrilemedi: {t.GetType().Name}");
            }
        }

        private Kaynak KaynakKur(NamedTableReference n, BooleanExpression? on, bool sol)
        {
            string ad = TabloAd(n);
            if (yasakliAd is not null && string.Equals(ad, yasakliAd, StringComparison.OrdinalIgnoreCase))
                throw new LinqCeviriHatasi($"Özyinelemeli CTE ({ad} kendine başvuruyor) LINQ'a çevrilemez.");
            return new Kaynak(ad, n.Alias?.Value, kaynakDegiskenleri.Contains(ad), on, sol);
        }

        /// <summary>FROM (SELECT …) x → iç sorgu "var xSorgu = (…);" olarak hoist edilir; kaynak o olur
        /// (v20-S20). Değişken adı alias'tan AYRIŞIR — "from x in x" gibi çakışan LINQ üretilmesin.</summary>
        private Kaynak TuretilmisKaynak(QueryDerivedTable qd, BooleanExpression? on, bool sol)
        {
            string alias = qd.Alias?.Value
                ?? throw new LinqCeviriHatasi("Türetilmiş tabloya (FROM (SELECT …)) takma ad verin.");
            string varAd = alias + "Sorgu";
            CeviriParcasi ic = SorguIfadesiCevir(qd.QueryExpression, kaynakDegiskenleri, yasakliAd);
            _onTanimlar.AddRange(ic.OnTanimlar);
            _onTanimlar.Add($"var {varAd} = (\n{Girintile(ic.Govde)});");
            _uyarilar.AddRange(ic.Uyarilar);
            return new Kaynak(varAd, alias, Degisken: true, on, sol);
        }

        private string Kaydet(Kaynak kaynak)
        {
            string sqlTakma = kaynak.Alias ?? kaynak.Ad;
            string deg = (kaynak.Alias ?? kaynak.Ad[..1]).ToLowerInvariant();
            while (_takma.ContainsValue(deg))
                deg += "2";
            _takma[sqlTakma] = deg;
            _degiskenler.Add(deg);
            return deg;
        }

        /// <summary>Alt sorgu yolları (EXISTS/IN/skaler) için eski imza — yalın tabloyu kaynağa sarar.</summary>
        private string Kaydet(NamedTableReference tablo) => Kaydet(KaynakKur(tablo, null, false));

        /// <summary>Ana sorgunun aralık değişkenleri (FROM sırasıyla) — çok tablolu grup elemanı için.</summary>
        private readonly List<string> _degiskenler = [];

        /// <summary>Toplama lambda'sının gövdesinde kolon: tek tabloda x.Kol, çok tabloda x.deg.Kol.</summary>
        private string GrupEleman(ColumnReferenceExpression kol)
        {
            IList<Identifier> par = kol.MultiPartIdentifier.Identifiers;
            if (_degiskenler.Count <= 1)
                return $"x.{par[^1].Value}";
            if (par.Count >= 2)
                return $"x.{DegiskenBul(par[^2].Value)}.{par[^1].Value}";
            throw new LinqCeviriHatasi(
                $"'{par[^1].Value}' kolonunu tablo takma adıyla niteleyin (çok tablolu toplamada bağ kurulamadı).");
        }

        private static string TabloAd(NamedTableReference t) =>
            t.SchemaObject.BaseIdentifier.Value;

        /// <summary>
        /// ON a.X = b.Y [AND …] → equals anahtarları (bileşikte anonim nesne) + ANAHTAR-DIŞI koşullar
        /// (v20-S21 saha m.8): IN / IS NULL / sabit kıyas / iç-tabloya-özel eşitlik artık REDDEDİLMEZ —
        /// "ek" listesinde döner; çağıran iç-tabloya değenleri join kaynağının .Where'ine, dışa
        /// değenleri (INNER'da) where satırına taşır. Anahtar = İKİ TARAFI DA kolon olan ve yalnız
        /// bir tarafı iç tabloya değen eşitlik ("u.Aktif = 1" anahtar değildir).
        /// </summary>
        private (string Sol, string Sag, List<BooleanExpression> Ekler) OnAnahtarlari(
            BooleanExpression on, string icDeg)
        {
            var esitlikler = new List<(string Sol, string Sag)>();
            var ekler = new List<BooleanExpression>();
            OnTopla(on, icDeg, esitlikler, ekler);
            if (esitlikler.Count == 0)
                throw new LinqCeviriHatasi(
                    "JOIN ON'da en az bir tablolar-arası eşitlik (a.X = b.Y) gerekir.");
            if (esitlikler.Count == 1)
                return (esitlikler[0].Sol, esitlikler[0].Sag, ekler);
            return ($"new {{ {string.Join(", ", esitlikler.Select(e => e.Sol))} }}",
                    $"new {{ {string.Join(", ", esitlikler.Select(e => e.Sag))} }}", ekler);
        }

        private void OnTopla(
            BooleanExpression on, string icDeg, List<(string, string)> hedef, List<BooleanExpression> ekler)
        {
            switch (on)
            {
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } ve:
                    OnTopla(ve.FirstExpression, icDeg, hedef, ekler);
                    OnTopla(ve.SecondExpression, icDeg, hedef, ekler);
                    break;
                case BooleanParenthesisExpression p:
                    OnTopla(p.Expression, icDeg, hedef, ekler);
                    break;
                case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } es
                    when es.FirstExpression is ColumnReferenceExpression
                    && es.SecondExpression is ColumnReferenceExpression:
                {
                    string a = Ifade(es.FirstExpression);
                    string b = Ifade(es.SecondExpression);
                    // equals'ın SOLU dış, SAĞI iç kaynak olmalı — gerekirse çevir
                    bool aIc = a.StartsWith(icDeg + ".", StringComparison.Ordinal);
                    bool bIc = b.StartsWith(icDeg + ".", StringComparison.Ordinal);
                    if (aIc == bIc)
                        ekler.Add(es); // iki taraf da iç (u.A = u.B) ya da hiçbiri → anahtar değil
                    else
                        hedef.Add(aIc ? (b, a) : (a, b));
                    break;
                }
                default:
                    ekler.Add(on); // IN / IS NULL / sabit kıyas … (v20-S21: artık hata değil)
                    break;
            }
        }

        /// <summary>ON ek koşulunun değdiği tablo NİTELEYİCİLERİ (a.X → "a"); niteliksiz kolon varsa null (belirsiz).</summary>
        private static HashSet<string>? Niteleyiciler(BooleanExpression e)
        {
            var toplayici = new NiteleyiciToplayici();
            e.Accept(toplayici);
            return toplayici.NiteliksizVar ? null : toplayici.Adlar;
        }

        private sealed class NiteleyiciToplayici : TSqlFragmentVisitor
        {
            public readonly HashSet<string> Adlar = new(StringComparer.OrdinalIgnoreCase);
            public bool NiteliksizVar;

            public override void Visit(ColumnReferenceExpression node)
            {
                IList<Identifier>? par = node.MultiPartIdentifier?.Identifiers;
                if (par is { Count: >= 2 })
                    Adlar.Add(par[^2].Value);
                else if (node.ColumnType == ColumnType.Regular)
                    NiteliksizVar = true;
            }
        }

        // ---- gruplamalar ----

        private void GrupYaz(GroupByClause g, List<string> satirlar)
        {
            var anahtarlar = new List<(string Sql, string Linq, string Ad, bool AdVerildi)>();
            var kullanilan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GroupingSpecification spec in g.GroupingSpecifications)
            {
                if (spec is not ExpressionGroupingSpecification { Expression: { } ifade })
                    throw new LinqCeviriHatasi("GROUP BY yalnız kolon/ifade listesiyle çevrilir (ROLLUP/CUBE yok).");
                string linq = Ifade(ifade);
                string ad = ifade is ColumnReferenceExpression k
                    ? k.MultiPartIdentifier.Identifiers[^1].Value
                    : $"Anahtar{anahtarlar.Count + 1}";
                // Aynı adlı iki anahtar (sv.Ad + st.Ad) anonim nesnede çakışır → takma adla ayrıştır
                bool adVerildi = false;
                if (!kullanilan.Add(ad))
                {
                    int nokta = linq.IndexOf('.');
                    string onek = nokta > 0 ? char.ToUpperInvariant(linq[0]) + linq[1..nokta] : "K";
                    ad = onek + ad;
                    for (int i = 2; !kullanilan.Add(ad); i++)
                        ad = onek + ad + i.ToString(CultureInfo.InvariantCulture);
                    adVerildi = true;
                }
                anahtarlar.Add((Normalize(ifade), linq, ad, adVerildi));
            }

            // Çok tabloda grup elemanı JOIN'Lİ SATIRDIR: new { sv, st, t } — toplamlar x.t.Kol'a gider
            string kaynakDeg = _degiskenler.Count == 1
                ? _degiskenler[0]
                : $"new {{ {string.Join(", ", _degiskenler)} }}";
            string anahtar = anahtarlar.Count == 1
                ? anahtarlar[0].Linq
                : $"new {{ {string.Join(", ", anahtarlar.Select(a => a.AdVerildi ? $"{a.Ad} = {a.Linq}" : a.Linq))} }}";
            satirlar.Add($"group {kaynakDeg} by {anahtar} into g");

            foreach ((string sql, _, string ad, _) in anahtarlar)
                _grupAnahtar[sql] = anahtarlar.Count == 1 ? "g.Key" : $"g.Key.{ad}";
            _grupModu = true;
        }

        /// <summary>Kolon başvurusunu grup anahtarı sözlüğü için normalize eder (a.b → a.b).</summary>
        private static string Normalize(ScalarExpression e) =>
            e is ColumnReferenceExpression k
                ? string.Join(".", k.MultiPartIdentifier.Identifiers.Select(i => i.Value))
                : e.GetType().Name + e.GetHashCode();

        // ---- SELECT listesi ----

        private string SecimYaz(QuerySpecification q)
        {
            var ogeler = new List<(string Ifade, string? Ad)>();
            foreach (SelectElement se in q.SelectElements)
            {
                switch (se)
                {
                    case SelectStarExpression yildiz:
                    {
                        if (_grupModu)
                            throw new LinqCeviriHatasi("GROUP BY ile SELECT * çevrilemez — kolonları açık yazın.");
                        string? nit = yildiz.Qualifier?.Identifiers[^1].Value;
                        string deg = nit is null
                            ? _tekTakma ?? throw new LinqCeviriHatasi(
                                "Çok tablolu sorguda SELECT * çevrilemez — kolonları açık yazın.")
                            : DegiskenBul(nit);
                        if (q.SelectElements.Count == 1)
                            return deg; // select s
                        throw new LinqCeviriHatasi("SELECT listesinde * ile kolonlar karışık — kolonları açık yazın.");
                    }
                    case SelectScalarExpression skaler:
                    {
                        string ifade = Ifade(skaler.Expression);
                        string? ad = skaler.ColumnName?.Value
                            ?? (skaler.Expression is ColumnReferenceExpression k
                                ? k.MultiPartIdentifier.Identifiers[^1].Value
                                : null);
                        ogeler.Add((ifade, ad));
                        break;
                    }
                    default:
                        throw new LinqCeviriHatasi($"SELECT ögesi çevrilemedi: {se.GetType().Name}");
                }
            }

            if (ogeler.Count == 1 && ogeler[0].Ad is { } tekAd
                && ogeler[0].Ifade.EndsWith("." + tekAd, StringComparison.Ordinal))
                return ogeler[0].Ifade; // select s.Kolon

            return "new { " + string.Join(", ", ogeler.Select(o =>
                o.Ad is { } ad && !o.Ifade.EndsWith("." + ad, StringComparison.Ordinal) && o.Ifade != "g.Key." + ad
                    ? $"{ad} = {o.Ifade}"
                    : o.Ifade)) + " }";
        }

        // ---- eklentiler (DISTINCT / TOP / OFFSET-FETCH) ----

        private string Ekler(QuerySpecification q)
        {
            string ek = "";
            if (q.UniqueRowFilter == UniqueRowFilter.Distinct)
                ek += ".Distinct()";
            if (q.OffsetClause is { } of)
            {
                ek += $".Skip({SabitTam(of.OffsetExpression, "OFFSET")})";
                if (of.FetchExpression is { } fe)
                    ek += $".Take({SabitTam(fe, "FETCH")})";
            }
            if (q.TopRowFilter is { } top)
            {
                if (top.Percent || top.WithTies)
                    throw new LinqCeviriHatasi("TOP PERCENT / WITH TIES'ın LINQ karşılığı yok.");
                ek += $".Take({SabitTam(top.Expression, "TOP")})";
            }
            return ek;
        }

        private static string SabitTam(ScalarExpression e, string yer) =>
            e is IntegerLiteral t ? t.Value
            : e is ParenthesisExpression { Expression: IntegerLiteral t2 } ? t2.Value
            : throw new LinqCeviriHatasi($"{yer} için sabit sayı bekleniyordu.");

        // ---- koşullar ----

        private string Kosul(BooleanExpression e) => e switch
        {
            BooleanBinaryExpression b =>
                $"{Kosul(b.FirstExpression)} {(b.BinaryExpressionType == BooleanBinaryExpressionType.And ? "&&" : "||")} {Kosul(b.SecondExpression)}",
            BooleanParenthesisExpression p => $"({Kosul(p.Expression)})",
            BooleanNotExpression { Expression: BooleanParenthesisExpression pIc } => $"!{Kosul(pIc)}",
            BooleanNotExpression n => $"!({Kosul(n.Expression)})",
            BooleanComparisonExpression k => Karsilastirma(k),
            BooleanIsNullExpression bn =>
                $"{Ifade(bn.Expression)} {(bn.IsNot ? "!=" : "==")} null",
            LikePredicate lp => Like(lp),
            InPredicate ip => In(ip),
            ExistsPredicate ex => Exists(ex), // v20-S18: EXISTS → db.T.Any(x => …)
            _ => throw new LinqCeviriHatasi($"Koşul çevrilemedi: {e.GetType().Name}"),
        };

        private string Karsilastirma(BooleanComparisonExpression k)
        {
            string islec = k.ComparisonType switch
            {
                BooleanComparisonType.Equals => "==",
                BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "!=",
                BooleanComparisonType.GreaterThan => ">",
                BooleanComparisonType.GreaterThanOrEqualTo => ">=",
                BooleanComparisonType.LessThan => "<",
                BooleanComparisonType.LessThanOrEqualTo => "<=",
                _ => throw new LinqCeviriHatasi($"Karşılaştırma çevrilemedi: {k.ComparisonType}"),
            };
            return $"{Ifade(k.FirstExpression)} {islec} {Ifade(k.SecondExpression)}";
        }

        private string Like(LikePredicate lp)
        {
            if (lp.EscapeExpression is not null)
                throw new LinqCeviriHatasi("LIKE … ESCAPE v1'de çevrilmiyor.");
            if (lp.SecondExpression is not StringLiteral desen)
                throw new LinqCeviriHatasi("LIKE deseni sabit metin olmalı.");

            string d = desen.Value;
            string hedef = Ifade(lp.FirstExpression);
            bool basJoker = d.StartsWith('%'), sonJoker = d.EndsWith('%');
            string ic = d.Trim('%');
            if (ic.Contains('%') || ic.Contains('_') || d.Contains('['))
            {
                // v20-S18: karışık desen ('A%B', 'x_y', '[..]') StartsWith/EndsWith/Contains'e sığmaz —
                // EF.Functions.Like birebir karşılıktır; EF Core dışı LINQ'ta çalışmayacağı söylenir.
                _uyarilar.Add($"LIKE deseni '{d}' StartsWith/EndsWith/Contains'e sığmıyor — EF.Functions.Like yazıldı (EF Core gerektirir).");
                string efCagri = $"EF.Functions.Like({hedef}, {CSharpMetin(d)})";
                return lp.NotDefined ? $"!{efCagri}" : efCagri;
            }

            string metod = (basJoker, sonJoker) switch
            {
                (true, true) => "Contains",
                (false, true) => "StartsWith",
                (true, false) => "EndsWith",
                (false, false) => null!, // jokersiz LIKE = eşitlik
            };
            string cagri = metod is null
                ? $"{hedef} == {CSharpMetin(ic)}"
                : $"{hedef}.{metod}({CSharpMetin(ic)})";
            return lp.NotDefined ? $"!{(metod is null ? $"({cagri})" : cagri)}" : cagri;
        }

        private string In(InPredicate ip)
        {
            // IN (SELECT kolon FROM T [WHERE …]) → db.T[.Where(…)].Select(x => x.Kol).Contains(dış) (v20-S18)
            if (ip.Subquery is { } altq)
            {
                if (altq.QueryExpression is not QuerySpecification q
                    || q.FromClause is not { TableReferences: [NamedTableReference tablo] }
                    || q.SelectElements is not [SelectScalarExpression { Expression: ColumnReferenceExpression kol }]
                    || q.GroupByClause is not null)
                    throw new LinqCeviriHatasi(
                        "IN alt sorgusu yalnız basit biçimde çevrilir: IN (SELECT kolon FROM Tablo x WHERE …).");

                string disIfade = Ifade(ip.Expression); // dış kolon İÇ kayıttan ÖNCE çözülür (gölgeleme olmasın)
                (string deg, Action birak) = AltKayit(tablo);
                try
                {
                    string kolAd = kol.MultiPartIdentifier.Identifiers[^1].Value;
                    string altCagri = $"{AltKaynakYaz(q, tablo, deg)}.Select({deg} => {deg}.{kolAd}).Contains({disIfade})";
                    return ip.NotDefined ? $"!{altCagri}" : altCagri;
                }
                finally
                {
                    birak();
                }
            }

            string liste = string.Join(", ", ip.Values.Select(Ifade));
            string cagri = $"new[] {{ {liste} }}.Contains({Ifade(ip.Expression)})";
            return ip.NotDefined ? $"!{cagri}" : cagri;
        }

        // ---- skaler ifadeler ----

        private string Ifade(ScalarExpression e) => e switch
        {
            ColumnReferenceExpression k => Kolon(k),
            StringLiteral s => CSharpMetin(s.Value),
            IntegerLiteral t => t.Value,
            NumericLiteral n => n.Value + "m",
            RealLiteral r => r.Value,
            NullLiteral => "null",
            VariableReference v => SqlParametre(v), // @TalepNo → talepNo (v20-S21 saha m.8)
            ParenthesisExpression p => $"({Ifade(p.Expression)})",
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } u => $"-{Ifade(u.Expression)}",
            BinaryExpression b => Ikili(b),
            FunctionCall f => Fonksiyon(f),
            SearchedCaseExpression c => AramaliCase(c),
            SimpleCaseExpression c => BasitCase(c),
            ScalarSubquery alt => AltSorgu(alt),
            CoalesceExpression c => "(" + string.Join(" ?? ", c.Expressions.Select(Ifade)) + ")", // v20-S18
            NullIfExpression n => // NULLIF(a,b) → a == b ? null : a
                $"({Ifade(n.FirstExpression)} == {Ifade(n.SecondExpression)} ? null : {Ifade(n.FirstExpression)})",
            CastCall c => Donusum(c.DataType, Ifade(c.Parameter)),       // v20-S18: CAST(x AS tip)
            ConvertCall c => Donusum(c.DataType, Ifade(c.Parameter)),    // v20-S18: CONVERT(tip, x)
            _ => throw new LinqCeviriHatasi($"İfade çevrilemedi: {e.GetType().Name}"),
        };

        /// <summary>CAST/CONVERT → C# karşılığı: sayısala cast, metne ToString, DATE'e .Date (v20-S18).</summary>
        private static string Donusum(DataTypeReference tip, string ic)
        {
            string ad = tip is SqlDataTypeReference sq
                ? sq.SqlDataTypeOption.ToString().ToUpperInvariant()
                : tip.Name?.BaseIdentifier?.Value.ToUpperInvariant() ?? "?";
            return ad switch
            {
                "INT" => $"(int){ic}",
                "BIGINT" => $"(long){ic}",
                "SMALLINT" => $"(short){ic}",
                "TINYINT" => $"(byte){ic}",
                "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" => $"(decimal){ic}",
                "FLOAT" or "REAL" => $"(double){ic}",
                "BIT" => $"(bool){ic}",
                "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" => $"{ic}.ToString()",
                "DATE" => $"{ic}.Date", // tarih kırpma — SQL'deki en yaygın kullanım
                _ => throw new LinqCeviriHatasi($"CAST/CONVERT hedef tipi çevrilemedi: {ad}"),
            };
        }

        /// <summary>
        /// Alt sorgu tablosunu geçici kaydeder; dönen eylem, kaydı GERİ ALIRKEN aynı adlı DIŞ takma
        /// adı da geri yükler (v20-S18 düzeltmesi: iç "m", dıştaki "m"yi kalıcı siliyordu).
        /// </summary>
        private (string Deg, Action Birak) AltKayit(NamedTableReference tablo)
        {
            string anahtar = tablo.Alias?.Value ?? TabloAd(tablo);
            bool disVardi = _takma.TryGetValue(anahtar, out string? disDeg);
            string deg = Kaydet(tablo);
            return (deg, () =>
            {
                _takma.Remove(anahtar);
                _degiskenler.Remove(deg);
                if (disVardi)
                    _takma[anahtar] = disDeg!;
            });
        }

        /// <summary>Basit alt sorgu kaynağı: "db.T" ya da WHERE'liyse "db.T.Where(x => …)".</summary>
        private string AltKaynakYaz(QuerySpecification q, NamedTableReference tablo, string deg)
        {
            string? kosul = q.WhereClause is { } w ? Kosul(w.SearchCondition) : null;
            return kosul is null
                ? $"db.{TabloAd(tablo)}"
                : $"db.{TabloAd(tablo)}.Where({deg} => {kosul})";
        }

        /// <summary>
        /// Bağıntılı skaler alt sorgu (2026-08-07 + v20-S18 genişlemesi): tek toplamlı
        /// "(SELECT COUNT(*) FROM T x WHERE …)" → "db.T.Count(x => …)"; TEK KOLONLU
        /// "(SELECT TOP (1) x.Kol FROM T x WHERE …)" → "….Select(x => x.Kol).FirstOrDefault()".
        /// Dış kolonlar (sv.Id) aynı bağlamdan çözülür.
        /// </summary>
        private string AltSorgu(ScalarSubquery alt)
        {
            if (alt.QueryExpression is not QuerySpecification q
                || q.SelectElements.Count != 1
                || q.SelectElements[0] is not SelectScalarExpression secim
                || q.FromClause is not { TableReferences: [NamedTableReference tablo] }
                || q.GroupByClause is not null || q.OrderByClause is not null)
                throw new LinqCeviriHatasi(
                    "Alt sorgu yalnız basit biçimde çevrilir: (SELECT COUNT(*)/SUM(k)/TOP (1) kolon FROM Tablo x WHERE …).");

            (string deg, Action birak) = AltKayit(tablo);
            try
            {
                // Tek kolonlu biçim (v20-S18): TOP (1) beklenir; yoksa yine FirstOrDefault + uyarı
                if (secim.Expression is ColumnReferenceExpression tekKol)
                {
                    if (q.TopRowFilter is null)
                        _uyarilar.Add("Skaler alt sorguda TOP (1) yok — LINQ FirstOrDefault ile İLK değeri alır "
                            + "(SQL birden çok satırda hata verirdi).");
                    string kolAd = tekKol.MultiPartIdentifier.Identifiers[^1].Value;
                    return $"{AltKaynakYaz(q, tablo, deg)}.Select({deg} => {deg}.{kolAd}).FirstOrDefault()";
                }

                if (secim.Expression is not FunctionCall f || q.TopRowFilter is not null)
                    throw new LinqCeviriHatasi(
                        "Alt sorgu yalnız basit biçimde çevrilir: (SELECT COUNT(*)/SUM(k)/TOP (1) kolon FROM Tablo x WHERE …).");

                string ad = f.FunctionName.Value.ToUpperInvariant();
                if (ad is not ("COUNT" or "COUNT_BIG" or "SUM" or "MIN" or "MAX" or "AVG"))
                    throw new LinqCeviriHatasi($"Alt sorguda {f.FunctionName.Value}() çevrilemiyor (COUNT/SUM/MIN/MAX/AVG desteklenir).");

                string? kosul = q.WhereClause is { } w ? Kosul(w.SearchCondition) : null;
                bool yildiz = f.Parameters.Count == 0
                    || f.Parameters[0] is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard };

                if (ad.StartsWith("COUNT", StringComparison.Ordinal))
                {
                    if (!yildiz)
                        _uyarilar.Add("Alt sorgudaki COUNT(kolon) null satırları saymaz — LINQ Count() hepsini sayar.");
                    return kosul is null
                        ? $"db.{TabloAd(tablo)}.Count()"
                        : $"db.{TabloAd(tablo)}.Count({deg} => {kosul})";
                }

                if (yildiz || f.Parameters[0] is not ColumnReferenceExpression kol)
                    throw new LinqCeviriHatasi($"Alt sorguda {ad} içinde tek kolon bekleniyordu.");
                string metodAd = ad == "AVG" ? "Average" : Kucuk(ad);
                return $"{AltKaynakYaz(q, tablo, deg)}.{metodAd}({deg} => {deg}.{kol.MultiPartIdentifier.Identifiers[^1].Value})";
            }
            finally
            {
                birak();
            }
        }

        /// <summary>EXISTS (SELECT … FROM T x WHERE …) → db.T.Any(x => …) (v20-S18) — şema gerekmez.</summary>
        private string Exists(ExistsPredicate e)
        {
            if (e.Subquery?.QueryExpression is not QuerySpecification q
                || q.FromClause is not { TableReferences: [NamedTableReference tablo] }
                || q.GroupByClause is not null)
                throw new LinqCeviriHatasi(
                    "EXISTS yalnız basit alt sorguyla çevrilir: EXISTS (SELECT … FROM Tablo x WHERE …).");

            (string deg, Action birak) = AltKayit(tablo);
            try
            {
                string? kosul = q.WhereClause is { } w ? Kosul(w.SearchCondition) : null;
                return kosul is null
                    ? $"db.{TabloAd(tablo)}.Any()"
                    : $"db.{TabloAd(tablo)}.Any({deg} => {kosul})";
            }
            finally
            {
                birak();
            }
        }

        private string Kolon(ColumnReferenceExpression k)
        {
            // grup modunda: anahtar kolonlar g.Key'e gider
            if (_grupModu && _grupAnahtar.TryGetValue(Normalize(k), out string? anahtar))
                return anahtar;

            IList<Identifier> par = k.MultiPartIdentifier.Identifiers;
            if (par.Count >= 2)
                return $"{DegiskenBul(par[^2].Value)}.{par[^1].Value}";

            // ORDER BY GenelCiro gibi SELECT takma adına başvuru: altındaki ifade açılır.
            // Ad genişletme sırasında sözlükten ÇIKARILIR — "SELECT X AS X" kendine dönmesin.
            if (_secimTakma.Remove(par[^1].Value, out ScalarExpression? altIfade))
            {
                string acilim = Ifade(altIfade);
                _secimTakma[par[^1].Value] = altIfade;
                return acilim;
            }

            if (_grupModu || _tekTakma is null)
                throw new LinqCeviriHatasi(
                    $"'{par[^1].Value}' kolonunu tablo takma adıyla niteleyin (çok tablolu/gruplu sorguda bağ kurulamadı).");
            return $"{_tekTakma}.{par[^1].Value}";
        }

        private string DegiskenBul(string sqlTakma) =>
            _takma.TryGetValue(sqlTakma, out string? deg)
                ? deg
                : throw new LinqCeviriHatasi($"'{sqlTakma}' takma adı FROM'da yok.");

        private string Ikili(BinaryExpression b)
        {
            string islec = b.BinaryExpressionType switch
            {
                BinaryExpressionType.Add => "+",
                BinaryExpressionType.Subtract => "-",
                BinaryExpressionType.Multiply => "*",
                BinaryExpressionType.Divide => "/",
                BinaryExpressionType.Modulo => "%",
                _ => throw new LinqCeviriHatasi($"İşleç çevrilemedi: {b.BinaryExpressionType}"),
            };
            return $"{Ifade(b.FirstExpression)} {islec} {Ifade(b.SecondExpression)}";
        }

        private string Fonksiyon(FunctionCall f)
        {
            string ad = f.FunctionName.Value.ToUpperInvariant();

            // toplamalar — yalnız grup modunda anlamlı (SQL de öyle ister)
            if (ad is "COUNT" or "COUNT_BIG" or "SUM" or "MIN" or "MAX" or "AVG")
            {
                if (!_grupModu)
                    throw new LinqCeviriHatasi(
                        $"{ad}() GROUP BY olmadan v1'de çevrilmiyor (tek toplam için LINQ'ta db.Tablo.{(ad == "AVG" ? "Average" : Kucuk(ad))}(…) yazın).");
                // COUNT(*): ScriptDom'da joker kolonlu FunctionCall'dur (parametresiz değil)
                if (f.Parameters.Count == 0
                    || f.Parameters[0] is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
                    return ad.StartsWith("COUNT", StringComparison.Ordinal)
                        ? "g.Count()"
                        : throw new LinqCeviriHatasi($"{ad}(*) çevrilemedi — kolon bekleniyordu.");
                if (f.Parameters.Count == 1 && f.Parameters[0] is ColumnReferenceExpression kol)
                {
                    string eleman = GrupEleman(kol); // tek tabloda x.Kol, çok tabloda x.deg.Kol
                    // COUNT(DISTINCT t.Id) → tekilleştirip say (kullanıcı bulgusu 2026-08-07)
                    if (f.UniqueRowFilter == UniqueRowFilter.Distinct)
                        return ad.StartsWith("COUNT", StringComparison.Ordinal)
                            ? $"g.Select(x => {eleman}).Distinct().Count()"
                            : throw new LinqCeviriHatasi($"{ad}(DISTINCT …) çevrilemiyor — yalnız COUNT(DISTINCT) desteklenir.");
                    string metodAd = ad == "AVG" ? "Average" : Kucuk(ad);
                    return $"g.{metodAd}(x => {eleman})";
                }
                throw new LinqCeviriHatasi($"{ad} içinde tek kolon bekleniyordu.");
            }

            string P(int i) => Ifade(f.Parameters[i]);
            return ad switch
            {
                "UPPER" => $"{P(0)}.ToUpper()",
                "LOWER" => $"{P(0)}.ToLower()",
                "TRIM" => $"{P(0)}.Trim()",
                "LTRIM" => $"{P(0)}.TrimStart()",
                "RTRIM" => $"{P(0)}.TrimEnd()",
                "LEN" or "LENGTH" => $"{P(0)}.Length",
                "YEAR" => $"{P(0)}.Year",
                "MONTH" => $"{P(0)}.Month",
                "DAY" => $"{P(0)}.Day",
                "ABS" => $"Math.Abs({P(0)})",
                "ROUND" => f.Parameters.Count == 2 ? $"Math.Round({P(0)}, {P(1)})" : $"Math.Round({P(0)})",
                "REPLACE" => $"{P(0)}.Replace({P(1)}, {P(2)})",
                "GETDATE" or "SYSDATETIME" or "NOW" or "CURRENT_TIMESTAMP" => GetdateUyar(),
                "GETUTCDATE" or "SYSUTCDATETIME" => GetdateUyar("DateTime.UtcNow"),
                // v20-S18 genişlemesi:
                "ISNULL" => $"({P(0)} ?? {P(1)})",
                "COALESCE" => "(" + string.Join(" ?? ",
                    Enumerable.Range(0, f.Parameters.Count).Select(P)) + ")",
                "SUBSTRING" when f.Parameters.Count == 3 => SubstringYaz(f),
                "DATEADD" when f.Parameters.Count == 3 => DateaddYaz(f),
                "DATEDIFF" when f.Parameters.Count == 3 => DatediffYaz(f),
                _ => throw new LinqCeviriHatasi($"{f.FunctionName.Value}() fonksiyonunun LINQ karşılığı yok."),
            };
        }

        /// <summary>SUBSTRING(x, a, b) → x.Substring(a-1, b) — SQL 1-tabanlı, C# 0-tabanlı.</summary>
        private string SubstringYaz(FunctionCall f)
        {
            string bas = f.Parameters[1] is IntegerLiteral { Value: { } sabit }
                && int.TryParse(sabit, out int b)
                    ? (b - 1).ToString(CultureInfo.InvariantCulture)
                    : $"({Ifade(f.Parameters[1])}) - 1";
            return $"{Ifade(f.Parameters[0])}.Substring({bas}, {Ifade(f.Parameters[2])})";
        }

        /// <summary>Tarih parçası düğüm TİPİNE bakılmaksızın ham token metninden okunur
        /// (ScriptDom "day"/"month"ı sürüme göre farklı düğümlerle verebiliyor).</summary>
        private static string DatePart(FunctionCall f)
        {
            TSqlFragment p = f.Parameters[0];
            if (p.FirstTokenIndex < 0 || p.ScriptTokenStream is null)
                throw new LinqCeviriHatasi("DATEADD/DATEDIFF ilk parametresi tarih parçası olmalı (day/month/…).");
            return string.Concat(
                Enumerable.Range(p.FirstTokenIndex, p.LastTokenIndex - p.FirstTokenIndex + 1)
                    .Select(i => p.ScriptTokenStream[i].Text)).Trim().ToLowerInvariant();
        }

        /// <summary>DATEADD(part, n, x) → x.AddDays(n)/AddMonths(n)/… (v20-S18).</summary>
        private string DateaddYaz(FunctionCall f)
        {
            string metod = DatePart(f) switch
            {
                "day" or "dd" or "d" => "AddDays",
                "month" or "mm" or "m" => "AddMonths",
                "year" or "yy" or "yyyy" => "AddYears",
                "hour" or "hh" => "AddHours",
                "minute" or "mi" or "n" => "AddMinutes",
                "second" or "ss" or "s" => "AddSeconds",
                var p => throw new LinqCeviriHatasi($"DATEADD({p}, …) çevrilemedi (day/month/year/hour/minute/second desteklenir)."),
            };
            return $"{Ifade(f.Parameters[2])}.{metod}({Ifade(f.Parameters[1])})";
        }

        /// <summary>DATEDIFF(part, a, b) → (b - a).Days / (int)(b - a).TotalHours… (v20-S18).
        /// Uyarıyla: SQL sınır SAYAR (23:59→00:01 = 1 gün), TimeSpan tam süre ölçer.</summary>
        private string DatediffYaz(FunctionCall f)
        {
            string fark = $"({Ifade(f.Parameters[2])} - {Ifade(f.Parameters[1])})";
            string sonuc = DatePart(f) switch
            {
                "day" or "dd" or "d" => $"{fark}.Days",
                "hour" or "hh" => $"(int){fark}.TotalHours",
                "minute" or "mi" or "n" => $"(int){fark}.TotalMinutes",
                "second" or "ss" or "s" => $"(int){fark}.TotalSeconds",
                var p => throw new LinqCeviriHatasi($"DATEDIFF({p}, …) çevrilemedi (day/hour/minute/second desteklenir; month/year'ın LINQ karşılığı yok)."),
            };
            _uyarilar.Add("DATEDIFF sınır sayar (23:59→00:01 = 1 gün), LINQ TimeSpan tam süre ölçer — gün sınırında sapabilir.");
            return sonuc;
        }

        private string GetdateUyar(string hedef = "DateTime.Now")
        {
            _uyarilar.Add($"Sunucu saati fonksiyonu → {hedef} yazıldı — LINQ'ta istemci saati, SQL'de sunucu saatidir.");
            return hedef;
        }

        private static string Kucuk(string ad) =>
            char.ToUpperInvariant(ad[0]) + ad[1..].ToLowerInvariant();

        private string AramaliCase(SearchedCaseExpression c)
        {
            string sonuc = c.ElseExpression is { } e ? Ifade(e) : "null";
            foreach (SearchedWhenClause when in c.WhenClauses.Reverse())
                sonuc = $"{Kosul(when.WhenExpression)} ? {Ifade(when.ThenExpression)} : {sonuc}";
            return sonuc;
        }

        private string BasitCase(SimpleCaseExpression c)
        {
            string giris = Ifade(c.InputExpression);
            string sonuc = c.ElseExpression is { } e ? Ifade(e) : "null";
            foreach (SimpleWhenClause when in c.WhenClauses.Reverse())
                sonuc = $"{giris} == {Ifade(when.WhenExpression)} ? {Ifade(when.ThenExpression)} : {sonuc}";
            return sonuc;
        }

        /// <summary>SQL metin sabitini C# string sabitine çevirir (kaçışlama dahil).</summary>
        private static string CSharpMetin(string deger) =>
            "\"" + deger.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>@Param → camelCase C# değişkeni + tek seferlik uyarı (v20-S21 saha m.8) —
        /// önceden "İfade çevrilemedi: VariableReference" ile düşüyordu.</summary>
        private readonly HashSet<string> _sqlParametreleri = new(StringComparer.OrdinalIgnoreCase);

        private string SqlParametre(VariableReference v)
        {
            string ad = v.Name.TrimStart('@');
            string yerel = ad.Length == 0 ? "p" : char.ToLowerInvariant(ad[0]) + ad[1..];
            if (_sqlParametreleri.Add(yerel))
                _uyarilar.Add($"SQL parametresi {v.Name} → '{yerel}' değişkeni olarak yazıldı — "
                    + "LINQ'tan önce değerini tanımlayın.");
            return yerel;
        }
    }
}
