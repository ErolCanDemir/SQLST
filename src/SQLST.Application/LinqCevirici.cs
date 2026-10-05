using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Çevrilemeyen LINQ yapısı — mesaj kullanıcıya AYNEN gösterilir (sessiz yanlış yasak).</summary>
public sealed class LinqCeviriHatasi(string mesaj) : Exception(mesaj);

/// <summary>Çeviri çıktısı: üretilen SQL + (varsa) uyarılar (ör. şemada doğrulanamayan kolon).</summary>
public sealed record LinqCeviriSonucu(string Sql, IReadOnlyList<string> Uyarilar);

/// <summary>
/// 🔁 LINQ → SQL çeviri MOTORU (v20-S11, kullanıcı kararı 2026-08-07: "AI olmadan kendi motorumuzu
/// yazalım"). Roslyn YALNIZ ayrıştırıcıdır (ScriptDom'un C# karşılığı — naif regex yasak, V2-S3
/// emsali); çeviri tamamen burada: metod zinciri + query syntax → ara model → LEHÇELİ SQL.
/// Şema önbelleğinden tablo/kolon eşleme (çoğul ada tolerans) + FK grafından navigation çözümü
/// (m.Musteri.Ad → JOIN; m.Siparisler.Any(...) → EXISTS). Çevrilemeyen yapıda NET hata fırlatır.
/// </summary>
public static class LinqCevirici
{
    // ── giriş ───────────────────────────────────────────────────────────────────────────────

    public static LinqCeviriSonucu Cevir(
        string linq, IReadOnlyList<SemaNesnesi> nesneler, IReadOnlyList<YabanciAnahtar> fkler,
        ILehce lehce)
    {
        // Çok ifadeli metin (v20-S20): "var x = (…); var y = (…); anaSorgu" — SQL→LINQ'in CTE için
        // ürettiği kalıp geri döner: her var tanımı bir CTE olur, ana sorgu onlara başvurur.
        var uyarilar = new List<string>();
        var degiskenler = new HashSet<string>(StringComparer.Ordinal);
        var cteler = new List<(string Ad, string Sql)>();
        List<string> parcalar = ParcalaraBol(linq);

        for (int i = 0; i < parcalar.Count - 1; i++)
        {
            StatementSyntax st = SyntaxFactory.ParseStatement(parcalar[i] + ";");
            if (st.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)
                || st is not LocalDeclarationStatementSyntax
                {
                    Declaration.Variables: [{ Initializer.Value: { } deger } tanim],
                })
                throw new LinqCeviriHatasi(
                    "Çok ifadeli metinde yalnız 'var ad = (sorgu);' tanımları + son satırda ana sorgu beklenir.");
            string ad = tanim.Identifier.Text;
            string parcaSql = KumeCevir(deger, nesneler, fkler, lehce, uyarilar, degiskenler).TrimEnd(';');
            cteler.Add((ad, parcaSql));
            degiskenler.Add(ad);
        }

        ExpressionSyntax ifade = Ayristir(parcalar[^1]);
        string sql = KumeCevir(ifade, nesneler, fkler, lehce, uyarilar, degiskenler);
        if (cteler.Count > 0)
            sql = "WITH " + string.Join(",\n", cteler.Select(c =>
                $"{lehce.TirnaklaTanimlayici(c.Ad)} AS (\n{CteGirinti(c.Sql)}\n)")) + "\n" + sql;
        return new LinqCeviriSonucu(sql, uyarilar);
    }

    private static string CteGirinti(string metin) => "  " + metin.Replace("\n", "\n  ");

    /// <summary>Üst seviye kıyas işleci → SQL karşılığı; kıyas değilse null (v20-S21).</summary>
    private static string? KiyasIslec(SyntaxKind k) => k switch
    {
        SyntaxKind.EqualsExpression => "=",
        SyntaxKind.NotEqualsExpression => "<>",
        SyntaxKind.GreaterThanExpression => ">",
        SyntaxKind.GreaterThanOrEqualExpression => ">=",
        SyntaxKind.LessThanExpression => "<",
        SyntaxKind.LessThanOrEqualExpression => "<=",
        _ => null,
    };

    /// <summary>Sabit soldayken (0 == q.Count()) işlecin yönünü çevirir (taraflar yer değiştirdi).</summary>
    private static string TersIslec(string islec) => islec switch
    {
        ">" => "<", "<" => ">", ">=" => "<=", "<=" => ">=", _ => islec,
    };

    /// <summary>Kıyasın SORGU tarafı: parantezleri soyulmuş metod çağrısı/query syntax; değilse null.</summary>
    private static ExpressionSyntax? SorguTarafi(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p)
            e = p.Expression;
        return e is InvocationExpressionSyntax or QueryExpressionSyntax ? e : null;
    }

    /// <summary>
    /// Metni derinlik-0 ';' sınırlarından böler — dize/karakter sabitlerinin ve parantez/ayraç
    /// içlerinin farkında (naif split yasak: lambda gövdesindeki ';' diye bir şey yok ama dize
    /// içindeki ';' ve iç içe parantezler var). Son parça ana sorgudur.
    /// </summary>
    private static List<string> ParcalaraBol(string metin)
    {
        var parcalar = new List<string>();
        int derinlik = 0, bas = 0;
        bool cift = false, tek = false;
        for (int i = 0; i < metin.Length; i++)
        {
            char c = metin[i];
            if (cift) { if (c == '\\') i++; else if (c == '"') cift = false; continue; }
            if (tek) { if (c == '\\') i++; else if (c == '\'') tek = false; continue; }
            switch (c)
            {
                case '"': cift = true; break;
                case '\'': tek = true; break;
                case '(' or '[' or '{': derinlik++; break;
                case ')' or ']' or '}': derinlik--; break;
                case ';' when derinlik == 0:
                    parcalar.Add(metin[bas..i]);
                    bas = i + 1;
                    break;
            }
        }
        string kalan = metin[bas..].Trim();
        if (kalan.Length > 0)
            parcalar.Add(kalan);
        if (parcalar.Count == 0)
            throw new LinqCeviriHatasi("Çevrilecek bir LINQ ifadesi bulunamadı.");
        return parcalar;
    }

    /// <summary>
    /// Üst seviyede küme metodu (v20-S19 simetri): (q1).Union(q2) → iki sorgu ayrı çevrilip
    /// UNION/UNION ALL/INTERSECT/EXCEPT ile bağlanır — SQL→LINQ yönünün ürettiği kalıp geri döner.
    /// </summary>
    private static string KumeCevir(
        ExpressionSyntax e, IReadOnlyList<SemaNesnesi> nesneler, IReadOnlyList<YabanciAnahtar> fkler,
        ILehce lehce, List<string> uyarilar, IReadOnlySet<string>? kaynakDegiskenleri = null)
    {
        while (e is ParenthesizedExpressionSyntax p)
            e = p.Expression;

        // v20-S21 (saha m.1): "(sorgu).Count() == 0" gibi ÜST SEVİYE skaler sorgu karşılaştırması —
        // sorgu tarafı skaler alt sorguya alınır, kıyas motorun boolean SELECT kalıbına sarılır
        // (Any()'nin EXISTS sarmalıyla aynı aile). Diğer taraf sabit sayı olmalı (net hata).
        if (e is BinaryExpressionSyntax kiyas && KiyasIslec(kiyas.Kind()) is { } kiyasIslec
            && (SorguTarafi(kiyas.Left) ?? SorguTarafi(kiyas.Right)) is { } sorguIfade)
        {
            ExpressionSyntax sabitTaraf = SorguTarafi(kiyas.Left) is not null ? kiyas.Right : kiyas.Left;
            if (sabitTaraf is not LiteralExpressionSyntax sabit
                || !sabit.IsKind(SyntaxKind.NumericLiteralExpression))
                throw new LinqCeviriHatasi(
                    "Sorgu karşılaştırmasının diğer tarafı sabit sayı olmalı (ör. (…).Count() == 0).");
            if (SorguTarafi(kiyas.Left) is null)
                kiyasIslec = TersIslec(kiyasIslec); // sabit soldaysa (0 == q.Count()) işlecin yönü çevrilir

            var bk = new Baglam(nesneler, fkler, lehce, kaynakDegiskenleri ?? new HashSet<string>());
            Sorgu sk = bk.Cozumle(sorguIfade);
            uyarilar.AddRange(bk.Uyarilar);
            if (sk.TekDeger is null)
                throw new LinqCeviriHatasi(
                    "Yalnız skaler sorgu karşılaştırılabilir — zinciri .Count() / .Sum(…) gibi bir toplamla bitirin.");

            string ic = bk.SqlYaz(sk).TrimEnd(';');
            string kiyasSql = $"(\n{CteGirinti(ic)}\n) {kiyasIslec} {sabit.Token.ValueText}";
            return lehce.MotorId switch
            {
                "postgres" or "mysql" => $"SELECT {kiyasSql};",
                "oracle" => $"SELECT CASE WHEN {kiyasSql} THEN 1 ELSE 0 END FROM DUAL;",
                _ => $"SELECT CASE WHEN {kiyasSql} THEN 1 ELSE 0 END;",
            };
        }

        if (e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax uye } cagri
            && uye.Name.Identifier.Text is "Union" or "Concat" or "Intersect" or "Except"
            && cagri.ArgumentList.Arguments.Count == 1)
        {
            string sol = KumeCevir(uye.Expression, nesneler, fkler, lehce, uyarilar, kaynakDegiskenleri).TrimEnd(';');
            string sag = KumeCevir(cagri.ArgumentList.Arguments[0].Expression, nesneler, fkler, lehce, uyarilar, kaynakDegiskenleri).TrimEnd(';');
            string islec = uye.Name.Identifier.Text switch
            {
                "Union" => "UNION",
                "Concat" => "UNION ALL",
                "Intersect" => "INTERSECT",
                _ => "EXCEPT",
            };
            return $"{sol}\n{islec}\n{sag};";
        }

        var b = new Baglam(nesneler, fkler, lehce, kaynakDegiskenleri ?? new HashSet<string>());
        Sorgu s = b.Cozumle(e);
        uyarilar.AddRange(b.Uyarilar);
        return b.SqlYaz(s);
    }

    /// <summary>"var q = …;" kabuğunu soyar, kalan metni C# ifadesi olarak ayrıştırır.</summary>
    private static ExpressionSyntax Ayristir(string metin)
    {
        string t = metin.Trim();
        if (t.EndsWith(';'))
            t = t[..^1].TrimEnd();
        // "var q = ..." / "IQueryable<X> q = ..." kabuğu: '=>' / '==' / '>=' … olmayan ilk '='.
        // Query syntax ("from …") kabuksuzdur; '(', '"', '{' görülünce ifade başlamıştır.
        int krm = 0;
        for (int i = 0; i < t.Length && i < 200 && !t.StartsWith("from ", StringComparison.Ordinal); i++)
        {
            char c = t[i];
            if (c is '(' or '"' or '{') break; // ifade başladı — atama kabuğu yok
            if (c == '<') krm++;
            else if (c == '>') krm--;
            else if (c == '=' && krm == 0
                     && (i + 1 >= t.Length || t[i + 1] is not ('=' or '>'))
                     && (i == 0 || t[i - 1] is not ('=' or '!' or '<' or '>')))
            {
                t = t[(i + 1)..].TrimStart();
                break;
            }
        }

        ExpressionSyntax ifade = SyntaxFactory.ParseExpression(t);
        var hatalar = ifade.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (hatalar.Count > 0)
            throw new LinqCeviriHatasi($"C# ifadesi ayrıştırılamadı: {hatalar[0].GetMessage()}");
        return ifade;
    }

    // ── ara model ───────────────────────────────────────────────────────────────────────────

    /// <summary>Tur: "JOIN" · "LEFT JOIN" (v20-S19: DefaultIfEmpty kalıbı) · "CROSS JOIN" (çoklu from).
    /// Degisken (v20-S20): kaynak tablo değil CTE değişkenidir — şemasız, tırnaklı yalın ad yazılır.
    /// AltSorgu (v22-S1): kaynak TÜRETİLMİŞ TABLO — join'de "(SELECT …) takma" olarak yazılır.</summary>
    private sealed record Katilim(
        string? Sema, string Tablo, string Takma, string Kosul, string Tur = "JOIN", bool Degisken = false,
        string? AltSorgu = null);

    /// <summary>Tek sorgunun ara modeli — SqlYaz bunu okur.</summary>
    private sealed class Sorgu
    {
        public required string Tablo;
        public string? Sema;
        public string Takma = "t";
        public List<Katilim> Katilimlar = [];
        public List<string> Nerede = [];
        public List<string> Gruplama = [];
        public List<string> GrupKosul = [];
        public List<(string Ifade, bool Azalan)> Siralama = [];
        public List<(string Ifade, string? Ad)> Secim = []; // boş → alias.*
        public bool Tekil;
        public int? Al, Atla;
        public string? TekDeger;    // COUNT(*)/SUM(x)… — skaler sorgu
        public bool Varlik;         // Any() → EXISTS kalıbı
        public bool KaynakDegisken; // v20-S20: FROM bir CTE değişkeni (tablo değil)
    }

    /// <summary>Lambda parametresinin bağlandığı şey: satır (alias) ya da GroupBy grubu.</summary>
    private sealed record Deger(string? Takma, Grup? Grubu);

    private sealed record Grup(IReadOnlyList<(string Ifade, string? Ad)> Anahtar, string KaynakTakma);

    // ── çözümleme bağlamı ───────────────────────────────────────────────────────────────────

    private sealed class Baglam(
        IReadOnlyList<SemaNesnesi> nesneler, IReadOnlyList<YabanciAnahtar> fkler, ILehce lehce,
        IReadOnlySet<string> kaynakDegiskenleri)
    {
        public readonly List<string> Uyarilar = [];
        private readonly Dictionary<string, Deger> _ad = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SemaNesnesi> _takmaTablo = new(StringComparer.Ordinal);

        private string K(string ad) => lehce.TirnaklaTanimlayici(ad);
        private string Literal(object? deger) => LiteralYazici.Yaz(deger, lehce.LiteralKurallari);

        // ---- kaynak/tablo eşleme ----

        /// <summary>db.Musteriler / ctx.Musteri / Musteriler → şemadaki tablo (çoğul toleransı).</summary>
        private SemaNesnesi TabloBul(string ad)
        {
            List<SemaNesnesi> es = TabloAra(ad);
            if (es.Count == 1)
                return es[0];
            if (es.Count > 1)
                throw new LinqCeviriHatasi(
                    $"'{ad}' birden çok tabloya uyuyor: {string.Join(", ", es.Select(n => n.TamAd))} — şemalı ad kullanın.");
            throw new LinqCeviriHatasi(
                $"'{ad}' şemada bulunamadı (gezginde yüklü veritabanının tablolarından eşlenir; çoğul ekler denenir).");
        }

        private List<SemaNesnesi> TabloAra(string ad)
        {
            List<SemaNesnesi> tablolar =
                [.. nesneler.Where(n => n.Tur is SemaNesneTuru.Tablo or SemaNesneTuru.View)];

            List<SemaNesnesi> es = [.. tablolar.Where(n => AdEs(n.Ad, ad))];
            if (es.Count == 0)
            {
                foreach (string aday in TekilAdaylari(ad))
                {
                    es = [.. tablolar.Where(n => AdEs(n.Ad, aday))];
                    if (es.Count > 0)
                        break;
                }
            }
            // TABLO önceliği (kullanıcı isteği 2026-08-10): eşleşenlerde tablo varsa VIEW'ları at —
            // aynı adı taşıyan bir view (ör. Kds.Firma) bir tabloyla (Mersis.Firma) çakışma sayılmasın;
            // LINQ context'i tablolardan eşlenir. Hiç tablo eşleşmezse view'lar kalır (view LINQ bozulmasın).
            List<SemaNesnesi> tabloEs = [.. es.Where(n => n.Tur == SemaNesneTuru.Tablo)];
            return tabloEs.Count > 0 ? tabloEs : es;
        }

        /// <summary>İfade bir TABLO KAYNAĞI mı (db.X ya da bağlı olmayan yalın X) — sessiz deneme (v20-S19).
        /// Niteleyici BAĞLI bir lambda değişkeniyse (m.Siparisler) tablo DEĞİLDİR — navigation'a kalır.</summary>
        private SemaNesnesi? TabloDene(ExpressionSyntax e) => e switch
        {
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax kok } m
                when !_ad.ContainsKey(kok.Identifier.Text)
                && TabloAra(m.Name.Identifier.Text) is [{ } tek] => tek,
            IdentifierNameSyntax i when !_ad.ContainsKey(i.Identifier.Text)
                && TabloAra(i.Identifier.Text) is [{ } tek] => tek,
            _ => null,
        };

        /// <summary>db.T ya da db.T.Where(lambda) zinciri → (tablo, süzgeç) — Any/Count/Contains alt sorguları için.</summary>
        private (SemaNesnesi Tablo, LambdaExpressionSyntax? Suzgec)? KaynakZinciri(ExpressionSyntax e)
        {
            if (TabloDene(e) is { } duz)
                return (duz, null);
            if (e is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Where" } w,
                    ArgumentList.Arguments: [{ Expression: LambdaExpressionSyntax suzgec }],
                } && TabloDene(w.Expression) is { } tablo)
                return (tablo, suzgec);
            return null;
        }

        private static bool AdEs(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>"Musteriler"→Musteri, "Orders"→Order, "Cities"→City gibi tekil adaylar.</summary>
        private static IEnumerable<string> TekilAdaylari(string ad)
        {
            foreach (string ek in (string[])["leri", "ları", "lari", "ler", "lar", "iler", "ilar"])
            {
                if (ad.Length > ek.Length && ad.EndsWith(ek, StringComparison.OrdinalIgnoreCase))
                    yield return ad[..^ek.Length];
            }
            if (ad.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
                yield return ad[..^3] + "y";
            if (ad.EndsWith("es", StringComparison.OrdinalIgnoreCase))
                yield return ad[..^2];
            if (ad.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                yield return ad[..^1];
        }

        private bool KolonVar(SemaNesnesi? tablo, string kolon) =>
            tablo is null || tablo.Kolonlar.Count == 0
            || tablo.Kolonlar.Any(k => AdEs(k.Ad, kolon));

        // ---- sorgu çözümleme ----

        public Sorgu Cozumle(ExpressionSyntax e) => e switch
        {
            ParenthesizedExpressionSyntax p => Cozumle(p.Expression),
            QueryExpressionSyntax q => QueryCozumle(q),
            InvocationExpressionSyntax i => MetodCozumle(i),
            MemberAccessExpressionSyntax or IdentifierNameSyntax => KaynakCozumle(e),
            _ => throw new LinqCeviriHatasi($"Sorgu kaynağı anlaşılamadı: {Kisa(e)}"),
        };

        /// <summary>db.Musteriler / Musteriler → yeni Sorgu (takma ad ilk lambda ile netleşir).
        /// Yalın ad bir CTE DEĞİŞKENİYSE (v20-S20) tablo aranmaz — şemasız değişken kaynak olur.</summary>
        private Sorgu KaynakCozumle(ExpressionSyntax e)
        {
            string ad = e switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                IdentifierNameSyntax i => i.Identifier.Text,
                _ => throw new LinqCeviriHatasi($"Sorgu kaynağı anlaşılamadı: {Kisa(e)}"),
            };

            if (e is IdentifierNameSyntax && kaynakDegiskenleri.Contains(ad))
            {
                var d = new Sorgu { Tablo = ad, Sema = null, Takma = $"t{++_takmaSayac}", KaynakDegisken = true };
                _geciciTakma.Add(d.Takma);
                return d; // _takmaTablo kaydı YOK: kolonları bilinmez, adıyla yazılır (KolonVar serbest)
            }

            SemaNesnesi tablo = TabloBul(ad);
            // Geçici takma ad SORGU BAŞINA benzersiz (t1, t2…): Join'de iki kaynak çakışmasın.
            var s = new Sorgu { Tablo = tablo.Ad, Sema = BosNull(tablo.Sema), Takma = $"t{++_takmaSayac}" };
            _geciciTakma.Add(s.Takma);
            _takmaTablo[s.Takma] = tablo;
            return s;
        }

        private int _takmaSayac;
        private readonly HashSet<string> _geciciTakma = [];

        private static string? BosNull(string? s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>Lambda parametresini sorgunun satırına bağlar; İLK bağlanışta takma adı devralır.</summary>
        private void SatiraBagla(Sorgu s, ParameterSyntax p)
        {
            string ad = p.Identifier.Text;
            if (s.Secim.Count == 0 && s.Gruplama.Count == 0 && !_ad.ContainsKey(ad)
                && _geciciTakma.Contains(s.Takma))
            {
                // henüz projeksiyon/grup yok → parametre doğrudan ana satır: takma adı benimse
                _geciciTakma.Remove(s.Takma);
                if (_takmaTablo.Remove(s.Takma, out SemaNesnesi? tb))
                    _takmaTablo[ad] = tb;
                s.Takma = ad;
            }
            _ad[ad] = new Deger(s.Takma, null);
        }

        /// <summary>LEFT JOIN alias'ı → iç anahtar SQL ifadesi: "tb == null" kıyası geçersiz "tb IS NULL"
        /// yerine iç anahtar kolonuna ("t.[FirmaId] IS NULL") çevrilsin diye (v20-S21 saha m.1).</summary>
        private readonly Dictionary<string, string> _solAnahtar = new(StringComparer.Ordinal);

        /// <summary>
        /// join kaynağını çözer ve SQL takma adını belirler. Üç biçim:
        /// (a) yalın tablo → join değişkeninin adı takma olur (v1 davranışı);
        /// (b) tablo.Where(…) → koşullar ON'a AND'lenir (v20-S21 saha m.1; INNER'da da LEFT'te de doğru);
        /// (c) ALT SORGU (grup/projeksiyon/join/Distinct/Take içeren kaynak) → TÜRETİLMİŞ TABLO:
        ///     "JOIN (SELECT …) bdVar ON …" (v22-S1 saha m.1 — önceden net hatayla reddediliyordu).
        /// Türetilmişte kaynağın kendi WHERE'i İÇERİDE kalır (ON'a taşınmaz) ve dışa açılan kolonlar
        /// adlandırılır: "on b.Id equals bdVar" gibi YALIN kullanım tek kolona çözülsün.
        /// </summary>
        private (string Takma, string EkKosul, string? AltSorgu) KatilimKaynagi(
            Sorgu ic, string icAd, IcYedek? yedek = null)
        {
            bool turetilmis = ic.Katilimlar.Count > 0 || ic.Secim.Count > 0 || ic.Gruplama.Count > 0
                || ic.TekDeger is not null || ic.Tekil || ic.Al is not null || ic.Atla is not null;
            if (turetilmis)
                return TuretilmisKaynak(ic, icAd, yedek);

            string takma;
            if (_geciciTakma.Remove(ic.Takma))
            {
                takma = icAd; // bağlanmamış kaynak: join değişkeninin adını benimse (eski davranış)
                if (_takmaTablo.Remove(ic.Takma, out SemaNesnesi? tb))
                    _takmaTablo[takma] = tb;
            }
            else
            {
                takma = ic.Takma; // Where lambda'sının parametre adı — koşullar bu adla yazıldı
            }
            _ad[icAd] = new Deger(takma, null);
            string ek = ic.Nerede.Count == 0 ? "" : " AND " + string.Join(" AND ", ic.Nerede);
            return (takma, ek, null);
        }

        /// <summary>Alt sorgulu join kaynağı → türetilmiş tablo SQL'i + dış takma ad (v22-S1).</summary>
        private (string Takma, string EkKosul, string? AltSorgu) TuretilmisKaynak(
            Sorgu ic, string icAd, IcYedek? yedek)
        {
            if (ic.Varlik)
                throw new LinqCeviriHatasi(
                    "join kaynağı Any() gibi tek bir doğruluk değeri olamaz — küme döndüren bir sorgu verin.");
            if (ic.TekDeger is { } tek) // (nadire) skaler kaynak: kolon adlandırılsın
            {
                ic.Secim = [(tek, "Deger")];
                ic.TekDeger = null;
            }
            if (ic.Siralama.Count > 0 && ic.Al is null)
            {
                ic.Siralama.Clear(); // ORDER BY, TOP'suz alt sorguda anlamsız (MSSQL derlemez bile)
                Uyarilar.Add($"'{icAd}' alt sorgusundaki sıralama atlandı — alt sorguda ORDER BY etkisizdir.");
            }

            _geciciTakma.Remove(ic.Takma);
            if (yedek is { } y)
                YedekUygula(y, ic); // iç navigation JOIN'leri İÇ sorguya, iç adlar geri alınır

            // Dışa açılan kolonlar: seçim listesi adlandırılır (adsız ifadeler k1, k2… alır).
            if (ic.Secim.Count == 0)
            {
                // "select bd" / süzülmüş yalın kaynak: kolonlar iç tablonun kolonlarıdır (t.* yazılır)
                if (_takmaTablo.TryGetValue(ic.Takma, out SemaNesnesi? icTablo))
                    _takmaTablo[icAd] = icTablo;
            }
            else
            {
                var adlar = new List<string>();
                for (int i = 0; i < ic.Secim.Count; i++)
                {
                    (string ifade, string? ad) = ic.Secim[i];
                    string kolonAd = ad ?? SonKolonAdi(ifade) ?? $"k{i + 1}";
                    if (adlar.Any(a => AdEs(a, kolonAd)))
                        kolonAd = $"{kolonAd}{i + 1}"; // türetilmiş tabloda kolon adı tekrarlayamaz
                    ic.Secim[i] = (ifade, kolonAd);
                    adlar.Add(kolonAd);
                }
                _turetilmisKolonlar[icAd] = adlar;
                if (adlar.Count == 1)
                    _turetilmisTekKolon[icAd] = $"{icAd}.{K(adlar[0])}";
            }

            _ad[icAd] = new Deger(icAd, null);
            return (icAd, "", SqlYazIc(ic).TrimEnd(';'));
        }

        /// <summary>"bd.[BirlesmeId]" → BirlesmeId (yalın kolon başvurusu değilse null).</summary>
        private static string? SonKolonAdi(string ifade)
        {
            string p = ifade[(ifade.LastIndexOf('.') + 1)..].Trim('[', ']', '"', '`');
            return p.Length > 0 && !char.IsDigit(p[0]) && p.All(c => char.IsLetterOrDigit(c) || c == '_')
                ? p
                : null;
        }

        /// <summary>Türetilmiş tablonun (alt sorgulu kaynak) dışa açtığı kolon adları — doğrulama için.</summary>
        private readonly Dictionary<string, List<string>> _turetilmisKolonlar = new(StringComparer.Ordinal);

        /// <summary>Tek kolonlu türetilmiş tablonun YALIN kullanımı ("… equals bdVar") → o kolon.</summary>
        private readonly Dictionary<string, string> _turetilmisTekKolon = new(StringComparer.Ordinal);

        /// <summary>Alt sorgu çözümlemesi öncesi bağlam yedeği — iç adlar ve iç navigation JOIN'leri
        /// DIŞ sorguya sızmasın (iç 'bd' dıştaki 'bd'yi gölgelemesin) (v22-S1).</summary>
        internal readonly record struct IcYedek(Dictionary<string, Deger> Adlar, int NavSayisi);

        private IcYedek YedekAl() => new(new Dictionary<string, Deger>(_ad, StringComparer.Ordinal), _sonNav.Count);

        /// <summary>İç çözümlemede biriken navigation JOIN'lerini İÇ sorguya taşır, ad tablosunu geri alır.</summary>
        private void YedekUygula(IcYedek y, Sorgu ic)
        {
            if (_sonNav.Count > y.NavSayisi)
            {
                ic.Katilimlar.AddRange(_sonNav.Skip(y.NavSayisi));
                _sonNav.RemoveRange(y.NavSayisi, _sonNav.Count - y.NavSayisi);
            }
            _ad.Clear();
            foreach (KeyValuePair<string, Deger> kv in y.Adlar)
                _ad[kv.Key] = kv.Value;
        }

        private LambdaExpressionSyntax LambdaAl(ArgumentSyntax a, string metod) =>
            a.Expression as LambdaExpressionSyntax
            ?? throw new LinqCeviriHatasi($"{metod} içinde lambda bekleniyordu: {Kisa(a.Expression)}");

        private static ExpressionSyntax Govde(LambdaExpressionSyntax l) =>
            l.Body as ExpressionSyntax
            ?? throw new LinqCeviriHatasi("Blok gövdeli lambda ({ ... }) çevrilemez — tek ifade kullanın.");

        private static ParameterSyntax Parametre(LambdaExpressionSyntax l) => l switch
        {
            SimpleLambdaExpressionSyntax s => s.Parameter,
            ParenthesizedLambdaExpressionSyntax p when p.ParameterList.Parameters.Count >= 1
                => p.ParameterList.Parameters[0],
            _ => throw new LinqCeviriHatasi("Lambda parametresi okunamadı."),
        };

        /// <summary>Metod zinciri: kaynağı çözümle, metodu uygula (dıştan içe özyineleme).</summary>
        private Sorgu MetodCozumle(InvocationExpressionSyntax cagri)
        {
            if (cagri.Expression is not MemberAccessExpressionSyntax uye)
                throw new LinqCeviriHatasi($"Desteklenmeyen çağrı: {Kisa(cagri)}");

            string metod = uye.Name.Identifier.Text;
            Sorgu s = Cozumle(uye.Expression);
            var argl = cagri.ArgumentList.Arguments;

            switch (metod)
            {
                case "Where":
                {
                    LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                    if (s.Gruplama.Count > 0)
                        _ad[Parametre(l).Identifier.Text] = _ad["$grup"]; // GroupBy sonrası: parametre GRUPTUR
                    else
                        SatiraBagla(s, Parametre(l));
                    KosullariEkle(s.Gruplama.Count > 0 ? s.GrupKosul : s.Nerede, Govde(l));
                    return s;
                }
                case "Select":
                {
                    LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                    ParameterSyntax p = Parametre(l);
                    if (s.Gruplama.Count > 0)
                        _ad[p.Identifier.Text] = _ad["$grup"];
                    else
                        SatiraBagla(s, p);
                    s.Secim = ProjeksiyonYaz(Govde(l));
                    return s;
                }
                case "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending":
                {
                    LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                    if (s.Gruplama.Count > 0)
                        _ad[Parametre(l).Identifier.Text] = _ad["$grup"];
                    else
                        SatiraBagla(s, Parametre(l));
                    if (metod.StartsWith("OrderBy", StringComparison.Ordinal))
                        s.Siralama.Clear();
                    s.Siralama.Add((IfadeYaz(Govde(l)), metod.EndsWith("Descending", StringComparison.Ordinal)));
                    return s;
                }
                case "Take" or "Skip":
                {
                    int n = SabitSayi(argl[0].Expression, metod);
                    if (metod == "Take") s.Al = n; else s.Atla = n;
                    return s;
                }
                case "Distinct":
                    s.Tekil = true;
                    return s;
                case "First" or "FirstOrDefault" or "Single" or "SingleOrDefault":
                {
                    if (argl.Count == 1)
                    {
                        LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                        SatiraBagla(s, Parametre(l));
                        s.Nerede.Add(IfadeYaz(Govde(l)));
                    }
                    s.Al = 1;
                    return s;
                }
                case "Count" or "LongCount":
                {
                    if (argl.Count == 1)
                    {
                        LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                        SatiraBagla(s, Parametre(l));
                        s.Nerede.Add(IfadeYaz(Govde(l)));
                    }
                    s.TekDeger = "COUNT(*)";
                    return s;
                }
                case "Any":
                {
                    if (argl.Count == 1)
                    {
                        LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                        SatiraBagla(s, Parametre(l));
                        s.Nerede.Add(IfadeYaz(Govde(l)));
                    }
                    s.Varlik = true;
                    return s;
                }
                case "Sum" or "Min" or "Max" or "Average":
                {
                    LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                    SatiraBagla(s, Parametre(l));
                    string fn = metod == "Average" ? "AVG" : metod.ToUpperInvariant();
                    s.TekDeger = $"{fn}({IfadeYaz(Govde(l))})";
                    return s;
                }
                case "GroupBy":
                {
                    LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                    SatiraBagla(s, Parametre(l));
                    List<(string, string?)> anahtar = ProjeksiyonYaz(Govde(l));
                    s.Gruplama = [.. anahtar.Select(a => a.Item1)];
                    _ad["$grup"] = new Deger(null, new Grup(anahtar, s.Takma));
                    return s;
                }
                case "Join":
                {
                    return KatilimUygula(s, argl);
                }
                default:
                    throw new LinqCeviriHatasi(
                        $"'{metod}' bu sürümde çevrilmiyor (desteklenenler: Where, Select, OrderBy/ThenBy, "
                        + "GroupBy, Join, Distinct, Take/Skip, First/Single, Count, Any, Sum/Min/Max/Average).");
            }
        }

        /// <summary>.Join(iç, dışAnahtar, içAnahtar, (a,b) => …) → INNER JOIN.</summary>
        private Sorgu KatilimUygula(Sorgu s, SeparatedSyntaxList<ArgumentSyntax> argl)
        {
            if (argl.Count < 4)
                throw new LinqCeviriHatasi("Join için 4 argüman bekleniyordu (iç, dışAnahtar, içAnahtar, sonuç).");

            Sorgu ic = Cozumle(argl[0].Expression);
            if (ic.Katilimlar.Count > 0 || ic.Nerede.Count > 0 || ic.Secim.Count > 0)
                throw new LinqCeviriHatasi("Join'in iç kaynağı yalın bir tablo olmalı (önceden süzülmüş kaynak v1'de yok).");

            LambdaExpressionSyntax disL = LambdaAl(argl[1], "Join");
            LambdaExpressionSyntax icL = LambdaAl(argl[2], "Join");
            LambdaExpressionSyntax sonucL = LambdaAl(argl[3], "Join");

            SatiraBagla(s, Parametre(disL));
            string icTakma = Parametre(icL).Identifier.Text;
            if (_ad.ContainsKey(icTakma) || icTakma == s.Takma)
                icTakma += "2";
            if (_takmaTablo.TryGetValue(ic.Takma, out SemaNesnesi? icTablo))
                _takmaTablo[icTakma] = icTablo; // değişken kaynakta (CTE) tablo bilgisi yoktur
            _ad[Parametre(icL).Identifier.Text] = new Deger(icTakma, null);

            string dis = IfadeYaz(Govde(disL));
            string icAnahtar = IfadeYaz(Govde(icL));
            s.Katilimlar.Add(new Katilim(
                ic.Sema, ic.Tablo, icTakma, $"{dis} = {icAnahtar}", Degisken: ic.KaynakDegisken));

            // sonuç lambda'sı: (a, b) => new { … } — iki parametre de bağlanır
            if (sonucL is ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: { Count: 2 } pl })
            {
                _ad[pl[0].Identifier.Text] = new Deger(s.Takma, null);
                _ad[pl[1].Identifier.Text] = new Deger(icTakma, null);
            }
            s.Secim = ProjeksiyonYaz(Govde(sonucL));
            return s;
        }

        // ---- query syntax (from … in … where … select …) ----

        private Sorgu QueryCozumle(QueryExpressionSyntax q)
        {
            Sorgu s = Cozumle(q.FromClause.Expression);
            string ad = q.FromClause.Identifier.Text;
            _geciciTakma.Remove(s.Takma);
            if (_takmaTablo.Remove(s.Takma, out SemaNesnesi? tb))
                _takmaTablo[ad] = tb;
            s.Takma = ad;
            _ad[ad] = new Deger(ad, null);
            return GovdeCozumle(s, q.Body);
        }

        /// <summary>join…into sonrası LEFT JOIN'e dönmeyi bekleyen grup (v20-S19; Yedek: v22-S1).</summary>
        private (string IntoAd, string JoinAd, Sorgu Ic, ExpressionSyntax Sol, ExpressionSyntax Sag, IcYedek Yedek)?
            _bekleyenSol;

        private Sorgu GovdeCozumle(Sorgu s, QueryBodySyntax govde)
        {
            foreach (QueryClauseSyntax c in govde.Clauses)
            {
                // join…into'dan hemen sonra yalnız DefaultIfEmpty'li from gelebilir (LEFT JOIN kalıbı)
                if (_bekleyenSol is { } bek && c is not FromClauseSyntax)
                    throw new LinqCeviriHatasi(
                        $"join … into {bek.IntoAd} yalnız 'from x in {bek.IntoAd}.DefaultIfEmpty()' ile (LEFT JOIN) çevrilir.");

                switch (c)
                {
                    case WhereClauseSyntax w:
                        KosullariEkle(s.Gruplama.Count > 0 ? s.GrupKosul : s.Nerede, w.Condition);
                        break;
                    case OrderByClauseSyntax o:
                        foreach (OrderingSyntax or1 in o.Orderings)
                            s.Siralama.Add((IfadeYaz(or1.Expression),
                                or1.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword)));
                        break;
                    case JoinClauseSyntax j when j.Into is null:
                    {
                        IcYedek yedek = YedekAl();
                        Sorgu ic = Cozumle(j.InExpression);
                        // v20-S21: kaynak .Where(…) içerebilir — koşullar ON'a AND'lenir (önceden düşüyordu)
                        // v22-S1: kaynak ALT SORGU olabilir — türetilmiş tabloya çevrilir
                        (string icTakma, string ek, string? alt) = KatilimKaynagi(ic, j.Identifier.Text, yedek);
                        string sol = IfadeYaz(j.LeftExpression);
                        string sag = IfadeYaz(j.RightExpression);
                        s.Katilimlar.Add(new Katilim(
                            ic.Sema, ic.Tablo, icTakma, $"{sol} = {sag}{ek}", Degisken: ic.KaynakDegisken,
                            AltSorgu: alt));
                        break;
                    }
                    case JoinClauseSyntax j: // join … into g (v20-S19): LEFT JOIN adayı — from'u bekle
                    {
                        IcYedek yedek = YedekAl();
                        _bekleyenSol = (j.Into!.Identifier.Text, j.Identifier.Text,
                            Cozumle(j.InExpression), j.LeftExpression, j.RightExpression, yedek);
                        break;
                    }
                    case FromClauseSyntax f when _bekleyenSol is { } solJoin:
                    {
                        // from m in g.DefaultIfEmpty() → LEFT JOIN (v20-S19)
                        if (f.Expression is not InvocationExpressionSyntax
                            {
                                Expression: MemberAccessExpressionSyntax
                                {
                                    Name.Identifier.Text: "DefaultIfEmpty",
                                    Expression: IdentifierNameSyntax grupAdi,
                                }
                            } || grupAdi.Identifier.Text != solJoin.IntoAd)
                            throw new LinqCeviriHatasi(
                                $"join … into {solJoin.IntoAd} yalnız 'from x in {solJoin.IntoAd}.DefaultIfEmpty()' ile (LEFT JOIN) çevrilir.");

                        // v20-S21: kaynak .Where(…) içerebilir — koşullar ON'a AND'lenir (LEFT'te
                        // where'e taşımak yanlış olurdu: eşleşmeyen dış satırlar da elenirdi).
                        (string takma, string ek, string? alt) =
                            KatilimKaynagi(solJoin.Ic, f.Identifier.Text, solJoin.Yedek);
                        if (solJoin.JoinAd != f.Identifier.Text) // join değişkeni de aynı satıra bağlanır
                            _ad[solJoin.JoinAd] = new Deger(takma, null);
                        string sol = IfadeYaz(solJoin.Sol);
                        string sag = IfadeYaz(solJoin.Sag);
                        s.Katilimlar.Add(new Katilim(solJoin.Ic.Sema, solJoin.Ic.Tablo, takma,
                            $"{sol} = {sag}{ek}", "LEFT JOIN", solJoin.Ic.KaynakDegisken, alt));
                        _solAnahtar[takma] = sag; // "x == null" kıyası iç anahtar kolonuna gitsin
                        _bekleyenSol = null;
                        break;
                    }
                    case FromClauseSyntax f:
                    {
                        // ikinci from (v20-S19): çapraz birleşim → CROSS JOIN
                        // v22-S1: kaynak süzülmüş ya da alt sorgu olabilir — süzgeç dış WHERE'e taşınır
                        // (CROSS JOIN'de ON yoktur; kartezyen çarpımı süzmek aynı anlama gelir).
                        IcYedek yedek = YedekAl();
                        Sorgu ic = Cozumle(f.Expression);
                        (string ad, string ek, string? alt) = KatilimKaynagi(ic, f.Identifier.Text, yedek);
                        if (ek.Length > 0)
                            s.Nerede.Add(ek[" AND ".Length..]);
                        s.Katilimlar.Add(new Katilim(
                            ic.Sema, ic.Tablo, ad, "", "CROSS JOIN", ic.KaynakDegisken, alt));
                        break;
                    }
                    case LetClauseSyntax:
                        throw new LinqCeviriHatasi("let çevrilmiyor — ifadeyi kullanıldığı yere açın.");
                    default:
                        throw new LinqCeviriHatasi($"Desteklenmeyen sorgu ögesi: {Kisa(c)}");
                }
            }

            if (_bekleyenSol is { } kalan)
                throw new LinqCeviriHatasi(
                    $"join … into {kalan.IntoAd} yalnız 'from x in {kalan.IntoAd}.DefaultIfEmpty()' ile (LEFT JOIN) çevrilir.");

            switch (govde.SelectOrGroup)
            {
                case SelectClauseSyntax sec:
                    s.Secim = ProjeksiyonYaz(sec.Expression);
                    break;
                case GroupClauseSyntax g:
                {
                    List<(string, string?)> anahtar = ProjeksiyonYaz(g.ByExpression);
                    s.Gruplama = [.. anahtar.Select(a => a.Item1)];
                    _ad["$grup"] = new Deger(null, new Grup(anahtar, s.Takma));
                    s.Secim = [];
                    break;
                }
            }

            if (govde.Continuation is { } devam) // group … by … into g select …
            {
                _ad[devam.Identifier.Text] = _ad["$grup"];
                s = GovdeCozumle(s, devam.Body);
            }
            return s;
        }

        // ---- projeksiyon ----

        /// <summary>Select gövdesi → SELECT listesi. new {} → çok kolon; tek ifade → tek kolon; x → *.</summary>
        private List<(string Ifade, string? Ad)> ProjeksiyonYaz(ExpressionSyntax e)
        {
            if (e is IdentifierNameSyntax kimlik && _ad.TryGetValue(kimlik.Identifier.Text, out Deger? d))
            {
                if (d.Grubu is { } grup)
                    return [.. grup.Anahtar];
                return []; // tüm satır → alias.*
            }
            if (e is AnonymousObjectCreationExpressionSyntax anon)
            {
                var liste = new List<(string, string?)>();
                foreach (AnonymousObjectMemberDeclaratorSyntax uye in anon.Initializers)
                {
                    string ifade = IfadeYaz(uye.Expression);
                    string? ad = uye.NameEquals?.Name.Identifier.Text
                        ?? (uye.Expression is MemberAccessExpressionSyntax m ? m.Name.Identifier.Text : null);
                    liste.Add((ifade, ad));
                }
                return liste;
            }
            return [(IfadeYaz(e), null)];
        }

        // ---- ifade çevirisi (lambda gövdeleri) ----

        public string IfadeYaz(ExpressionSyntax e) => e switch
        {
            ParenthesizedExpressionSyntax p => $"({IfadeYaz(p.Expression)})",
            LiteralExpressionSyntax l => LiteralYaz(l),
            IdentifierNameSyntax i => KimlikYaz(i),
            MemberAccessExpressionSyntax m => UyeYaz(m),
            BinaryExpressionSyntax b => IkiliYaz(b),
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } u
                => $"NOT ({KosulYaz(u.Operand)})",
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } u
                => $"-{IfadeYaz(u.Operand)}",
            ConditionalExpressionSyntax k
                => $"CASE WHEN {KosulYaz(k.Condition)} THEN {IfadeYaz(k.WhenTrue)} ELSE {IfadeYaz(k.WhenFalse)} END",
            InvocationExpressionSyntax c => CagriYaz(c),
            CastExpressionSyntax d => CastYaz(d),
            _ => throw new LinqCeviriHatasi($"İfade çevrilemedi: {Kisa(e)}"),
        };

        /// <summary>
        /// KOŞUL bağlamı (WHERE · HAVING · &amp;&amp; · || · NOT · ?:): yalın bit/bool kolon SQL'de tek
        /// başına koşul DEĞİLDİR — MSSQL "WHERE u.[Aktif] AND …" ifadesini derlemez. Bu yüzden yalın
        /// kolon başvurusuna "= 1" eklenir; PostgreSQL'de kolon gerçek boolean olduğu için dokunulmaz
        /// (v22-S1 saha m.1: "u.Aktif &amp;&amp; u.BitisTalepId == null" kalıbı EF'te çok yaygın).
        /// </summary>
        public string KosulYaz(ExpressionSyntax e)
        {
            string sql = IfadeYaz(e);
            return !Motor("postgres") && BoolKolonMu(e) ? $"{sql} = {Literal(true)}" : sql;
        }

        /// <summary>
        /// Üst seviye "&amp;&amp;" zincirini AYRI koşullara böler: SQL yazımı bunları "\n  AND " ile
        /// birleştirdiği için uzun WHERE tek satıra sıkışmaz (v22-S1 saha m.1 — gerçek EF sorgusunda
        /// 1.200 karakterlik tek satır çıkıyordu). Anlam aynı; OR içeren parçalar Parantezle ile korunur.
        /// </summary>
        private void KosullariEkle(List<string> hedef, ExpressionSyntax e)
        {
            while (e is ParenthesizedExpressionSyntax p)
                e = p.Expression;
            if (e is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } b)
            {
                KosullariEkle(hedef, b.Left);
                KosullariEkle(hedef, b.Right);
                return;
            }
            hedef.Add(KosulYaz(e));
        }

        /// <summary>İfade, satıra bağlı YALIN bir kolon başvurusu mu (x.Aktif)? Koşul bağlamında bu bool
        /// kolon demektir (C# başka türlüsüne izin vermez).</summary>
        private bool BoolKolonMu(ExpressionSyntax e) =>
            e is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax kok } m
            && _ad.TryGetValue(kok.Identifier.Text, out Deger? d) && d.Takma is { } takma
            && (_turetilmisKolonlar.ContainsKey(takma)
                || KolonVar(_takmaTablo.GetValueOrDefault(takma), m.Name.Identifier.Text));

        /// <summary>(int)EnumX.Y gibi tür dönüşümü: iç ifade yazılır (enum/parametre/sabitte SQL'de
        /// karşılığı gerekmez). Gerçek bir ifade dönüştürülüyorsa uyarı verilir — CAST söz dizimi
        /// lehçeye göre değişir, sessizce yanlış yazmak yerine kullanıcı bilgilendirilir (v22-S1).</summary>
        private string CastYaz(CastExpressionSyntax d)
        {
            string ic = IfadeYaz(d.Expression);
            bool zararsiz = d.Expression is LiteralExpressionSyntax
                || ic.StartsWith('@') || ic.StartsWith(':');
            if (!zararsiz && _castUyarildi.Add(d.Type.ToString()))
                Uyarilar.Add($"'({d.Type})' dönüşümü SQL'e yazılmadı — gerekiyorsa CAST(… AS …) ekleyin.");
            return ic;
        }

        private readonly HashSet<string> _castUyarildi = new(StringComparer.Ordinal);

        private string LiteralYaz(LiteralExpressionSyntax l) => l.Kind() switch
        {
            SyntaxKind.NullLiteralExpression => "NULL",
            SyntaxKind.TrueLiteralExpression => Literal(true),
            SyntaxKind.FalseLiteralExpression => Literal(false),
            SyntaxKind.StringLiteralExpression or SyntaxKind.CharacterLiteralExpression
                => Literal(l.Token.ValueText),
            SyntaxKind.NumericLiteralExpression => l.Token.ValueText,
            _ => throw new LinqCeviriHatasi($"Sabit çevrilemedi: {l.Token.Text}"),
        };

        private string KimlikYaz(IdentifierNameSyntax i)
        {
            string ad = i.Identifier.Text;
            if (_ad.TryGetValue(ad, out Deger? d))
            {
                if (d.Grubu is { Anahtar.Count: 1 } g)
                    return g.Anahtar[0].Ifade;
                if (d.Takma is { } takma)
                    // Tek kolonlu türetilmiş tablo YALIN kullanıldıysa ("on b.Id equals bdVar") o kolona
                    // çözülür (v22-S1); değilse takma ad tek başına yalnız üye erişiminin solunda anlamlı.
                    return _turetilmisTekKolon.GetValueOrDefault(takma) ?? takma;
            }
            // v20-S21 (saha m.1): dış değişken artık HATA değil — SQL parametresine çevrilir
            // (talepTuruId → @talepTuruId); kullanıcı çalıştırmadan önce değerini verir.
            return Param(ad);
        }

        /// <summary>Sorguya bağlı olmayan (dış) değişken → motorun parametre yazımı + tek seferlik uyarı.</summary>
        private string Param(string ad)
        {
            if (_paramlar.Add(ad))
                Uyarilar.Add($"'{ad}' dış değişkeni {ParamAd(ad)} parametresi olarak yazıldı — "
                    + "çalıştırmadan önce değerini tanımlayın ya da yerine koyun.");
            return ParamAd(ad);
        }

        private string ParamAd(string ad) => (Motor("postgres") || Motor("oracle") ? ":" : "@") + ad;

        private readonly HashSet<string> _paramlar = new(StringComparer.Ordinal);

        /// <summary>x.Kolon · x.Nav.Kolon (FK JOIN) · g.Key · x.Ad.Length · x.Tarih.Year …</summary>
        private string UyeYaz(MemberAccessExpressionSyntax m)
        {
            string uye = m.Name.Identifier.Text;

            // g.Key → grup anahtarı
            if (m.Expression is IdentifierNameSyntax kok && _ad.TryGetValue(kok.Identifier.Text, out Deger? d))
            {
                if (d.Grubu is { } grup)
                {
                    if (uye == "Key")
                        return grup.Anahtar.Count == 1
                            ? grup.Anahtar[0].Ifade
                            : throw new LinqCeviriHatasi("Bileşik grup anahtarında g.Key.Alan biçimini kullanın.");
                    throw new LinqCeviriHatasi($"Grup üzerinde '{uye}' çevrilemedi (Key ve toplama işlevleri desteklenir).");
                }
                if (d.Takma is { } takma)
                {
                    // Türetilmiş tablo (alt sorgulu join kaynağı): kolonlar seçim listesindedir (v22-S1)
                    if (_turetilmisKolonlar.TryGetValue(takma, out List<string>? turKolon))
                    {
                        if (!turKolon.Any(k => AdEs(k, uye)))
                            Uyarilar.Add($"'{uye}', {takma} alt sorgusunun seçim listesinde yok "
                                + $"(orada olanlar: {string.Join(", ", turKolon)}) — adıyla yazıldı.");
                        return $"{takma}.{K(uye)}";
                    }
                    // x.Kolon — kolonu şemayla doğrula; bilinmiyorsa FK navigation dene
                    SemaNesnesi? tablo = _takmaTablo.GetValueOrDefault(takma);
                    if (KolonVar(tablo, uye))
                        return $"{takma}.{K(uye)}";
                    if (tablo is not null && NavigationTakma(tablo, takma, uye) is { } navTakma)
                        return navTakma; // x.Musteri → JOIN'lenen alias (kolon erişimi bir üst çağrıda)
                    Uyarilar.Add($"'{uye}' kolonu {tablo?.TamAd} şemasında görünmüyor — adıyla yazıldı.");
                    return $"{takma}.{K(uye)}";
                }
            }

            // g.Key.Alan — bileşik grup anahtarı alanı
            if (m.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Key" } keyUye
                && keyUye.Expression is IdentifierNameSyntax gKok
                && _ad.TryGetValue(gKok.Identifier.Text, out Deger? gd) && gd.Grubu is { } g2)
            {
                (string Ifade, string? Ad) alan = g2.Anahtar.FirstOrDefault(a => AdEs(a.Ad ?? "", uye));
                return alan.Ifade
                    ?? throw new LinqCeviriHatasi($"Grup anahtarında '{uye}' alanı yok.");
            }

            // DateTime.Now / UtcNow / Today → motorun tarih işlevi (v22-S1)
            if (m.Expression is IdentifierNameSyntax { Identifier.Text: "DateTime" } && !_ad.ContainsKey("DateTime"))
                return SimdiYaz(uye);

            // DIŞ nesne/enum üyesi (v22-S1 saha m.1): aramaModel.SicilNo · (int)EnumTalepDurumu.Tescilli —
            // kök ne satıra bağlı ne tablo → TEK parametre olarak yazılır (@aramaModel_SicilNo). Önceden
            // "Üye çevrilemedi" ile duruyordu; EF sorgularında bu iki kalıp çok yaygın.
            if (DisUyeAdi(m) is { } disAd)
                return Param(disAd);

            // iç ifadeye bağlı üyeler: .Length / .Year / .Month … ya da nav zinciri x.Musteri.Ad
            string ic = IfadeYaz(m.Expression);
            return uye switch
            {
                "Length" => Motor("mssql") ? $"LEN({ic})" : $"LENGTH({ic})",
                "Year" or "Month" or "Day" or "Hour" or "Minute" or "Second" => TarihParca(uye, ic),
                "Date" => Motor("oracle") ? $"TRUNC({ic})" : $"CAST({ic} AS DATE)",
                "Value" => ic, // Nullable<T>.Value — SQL'de fark yok
                _ when _takmaTablo.ContainsKey(ic) => $"{ic}.{K(uye)}", // nav alias'ı üstünden kolon
                _ => throw new LinqCeviriHatasi($"Üye çevrilemedi: {Kisa(m)}"),
            };
        }

        /// <summary>Kökü satıra bağlı OLMAYAN üye zinciri (aramaModel.SicilNo · EnumX.Y · a.b.c) →
        /// parametre adı ("aramaModel_SicilNo"); satıra/tabloya ait ya da tanınan bir tipse null (v22-S1).</summary>
        private string? DisUyeAdi(MemberAccessExpressionSyntax m)
        {
            var parcalar = new List<string>();
            ExpressionSyntax e = m;
            while (e is MemberAccessExpressionSyntax uye)
            {
                parcalar.Insert(0, uye.Name.Identifier.Text);
                e = uye.Expression;
            }
            if (e is not IdentifierNameSyntax kok
                || _ad.ContainsKey(kok.Identifier.Text)
                || TabloDene(m) is not null
                || kok.Identifier.Text is "string" or "String" or "Math" or "EF" or "DateTime")
                return null;
            parcalar.Insert(0, kok.Identifier.Text);
            return string.Join("_", parcalar);
        }

        /// <summary>DateTime.Now / UtcNow / Today → motorun tarih işlevi (v22-S1).</summary>
        private string SimdiYaz(string uye) => (uye, lehce.MotorId) switch
        {
            ("Now", "mssql") => "GETDATE()",
            ("Now", "oracle") => "SYSDATE",
            ("Now", _) => "NOW()",
            ("UtcNow", "mssql") => "GETUTCDATE()",
            ("UtcNow", "mysql") => "UTC_TIMESTAMP()",
            ("UtcNow", "oracle") => "SYS_EXTRACT_UTC(SYSTIMESTAMP)",
            ("UtcNow", _) => "(NOW() AT TIME ZONE 'UTC')",
            ("Today", "mssql") => "CAST(GETDATE() AS DATE)",
            ("Today", "mysql") => "CURDATE()",
            ("Today", "oracle") => "TRUNC(SYSDATE)",
            ("Today", _) => "CURRENT_DATE",
            _ => throw new LinqCeviriHatasi($"DateTime.{uye} çevrilemedi (Now, UtcNow, Today desteklenir)."),
        };

        private bool Motor(string id) => lehce.MotorId == id;

        private string TarihParca(string parca, string ic) => Motor("postgres") || Motor("oracle")
            ? $"EXTRACT({parca.ToUpperInvariant()} FROM {ic})"
            : $"{parca.ToUpperInvariant()}({ic})";

        /// <summary>x.Musteri gibi FK navigation: JOIN ekler, hedefin alias'ını döndürür (yoksa null).</summary>
        private string? NavigationTakma(SemaNesnesi tablo, string takma, string uye)
        {
            foreach (YabanciAnahtar fk in fkler)
            {
                if (!AdEs(fk.KaynakTablo, tablo.Ad))
                    continue;
                bool adUyar = AdEs(fk.HedefTablo, uye)
                    || fk.KaynakKolonlar.Any(k => AdEs(k, uye + "Id") || AdEs(k, uye + "_id"));
                if (!adUyar)
                    continue;

                string yeni = uye.ToLowerInvariant();
                if (_takmaTablo.ContainsKey(yeni))
                    return yeni; // aynı navigation daha önce JOIN'lendi
                SemaNesnesi? hedef = nesneler.FirstOrDefault(
                    n => n.Tur == SemaNesneTuru.Tablo && AdEs(n.Ad, fk.HedefTablo));
                _takmaTablo[yeni] = hedef ?? new SemaNesnesi("", fk.HedefSema, fk.HedefTablo, SemaNesneTuru.Tablo, [], []);
                string kosul = string.Join(" AND ", fk.KaynakKolonlar.Zip(fk.HedefKolonlar,
                    (a, b) => $"{takma}.{K(a)} = {yeni}.{K(b)}"));
                _sonNav.Add(new Katilim(BosNull(fk.HedefSema), fk.HedefTablo, yeni, kosul));
                return yeni;
            }
            return null;
        }

        /// <summary>Navigation JOIN'leri ifade çevirisi SIRASINDA birikir; SqlYaz sorguya işler.</summary>
        private readonly List<Katilim> _sonNav = [];

        // ---- metod çağrıları (string/matematik/koleksiyon) ----

        private string CagriYaz(InvocationExpressionSyntax c)
        {
            if (c.Expression is not MemberAccessExpressionSyntax uye)
                throw new LinqCeviriHatasi($"Çağrı çevrilemedi: {Kisa(c)}");

            string metod = uye.Name.Identifier.Text;
            var argl = c.ArgumentList.Arguments;

            // g.Count() / g.Sum(x => x.Tutar) … — GroupBy grubunun toplama işlevleri
            if (uye.Expression is IdentifierNameSyntax grupKok
                && _ad.TryGetValue(grupKok.Identifier.Text, out Deger? grupDeger)
                && grupDeger.Grubu is { } grubu)
                return GrupToplamaYaz(metod, grubu, argl);

            // EF.Functions.Like(x, desen) → x LIKE desen (v20-S19 — ters yönün ürettiği kalıp geri döner)
            if (metod == "Like" && Kisa(uye.Expression) == "EF.Functions" && argl.Count == 2)
                return $"{IfadeYaz(argl[0].Expression)} LIKE {IfadeYaz(argl[1].Expression)}";

            // db.T[.Where(w)].Any/Count([k]) → EXISTS (…) / (SELECT COUNT(*) …) (v20-S19)
            if (metod is "Any" or "Count" && KaynakZinciri(uye.Expression) is { } kaynak)
                return TabloSorgusuYaz(metod, kaynak, argl);

            // (from … select …).Count() / .Any() / .Sum(x => …) → İLİŞKİLİ skaler alt sorgu (v22-S1
            // saha m.1): dış satıra başvuran (correlated) alt sorgular koşul içinde kullanılabilir.
            if (metod is "Count" or "LongCount" or "Any" or "Sum" or "Min" or "Max" or "Average"
                && AltSorguIfadesi(uye.Expression) is { } altQ)
                return AltSorguToplamaYaz(metod, altQ, argl);

            // db.T[.Where(w)].Select(x => x.Kol).Contains(dış) → dış IN (SELECT kol FROM T …) (v20-S19)
            if (metod == "Contains" && argl.Count == 1
                && uye.Expression is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" } secUye,
                    ArgumentList.Arguments: [{ Expression: LambdaExpressionSyntax secL }],
                }
                && KaynakZinciri(secUye.Expression) is { } secKaynak)
                return InAltSorguYaz(secKaynak, secL, argl[0].Expression);

            // string.IsNullOrEmpty(x) / string.IsNullOrWhiteSpace(x)
            if (uye.Expression is PredefinedTypeSyntax || Kisa(uye.Expression) is "string" or "String")
            {
                string x = IfadeYaz(argl[0].Expression);
                return metod switch
                {
                    "IsNullOrEmpty" => $"({x} IS NULL OR {x} = {Literal("")})",
                    "IsNullOrWhiteSpace" => $"({x} IS NULL OR TRIM({x}) = {Literal("")})",
                    _ => throw new LinqCeviriHatasi($"string.{metod} çevrilemedi."),
                };
            }

            // Math.Abs / Math.Round
            if (Kisa(uye.Expression) is "Math")
            {
                string x = IfadeYaz(argl[0].Expression);
                return metod switch
                {
                    "Abs" => $"ABS({x})",
                    "Round" => argl.Count == 2 ? $"ROUND({x}, {IfadeYaz(argl[1].Expression)})" : $"ROUND({x})",
                    "Floor" => $"FLOOR({x})",
                    "Ceiling" => Motor("mssql") ? $"CEILING({x})" : $"CEIL({x})",
                    _ => throw new LinqCeviriHatasi($"Math.{metod} çevrilemedi."),
                };
            }

            // koleksiyon.Contains(x.Kolon) → IN (…)
            if (metod == "Contains" && DiziElemanlari(uye.Expression) is { } elemanlar)
                return $"{IfadeYaz(argl[0].Expression)} IN ({string.Join(", ", elemanlar)})";

            // dış LİSTE değişkeni (v20-S21 saha m.1): ids.Contains(x.Kol) → x.Kol IN (@ids) — liste
            // içeriği bilinmez, parametre + uyarıyla yazılır (LIKE'a düşüp yanlış çevrilmesin).
            if (metod == "Contains" && argl.Count == 1
                && uye.Expression is IdentifierNameSyntax liste
                && !_ad.ContainsKey(liste.Identifier.Text))
            {
                string arg = IfadeYaz(argl[0].Expression);
                if (_paramlar.Add(liste.Identifier.Text))
                    Uyarilar.Add($"'{liste.Identifier.Text}' liste değişkeni {ParamAd(liste.Identifier.Text)} "
                        + "olarak yazıldı — IN içine değerleri virgülle açın.");
                return $"{arg} IN ({ParamAd(liste.Identifier.Text)})";
            }

            // koleksiyon navigation: m.Siparisler.Any(...) / Count() → EXISTS / alt sorgu
            if (metod is "Any" or "Count" && uye.Expression is MemberAccessExpressionSyntax navUye
                && TersNavigation(navUye) is { } ters)
                return TersNavYaz(metod, ters, argl);

            string hedefIfade = IfadeYaz(uye.Expression);
            return metod switch
            {
                "StartsWith" => LikeYaz(hedefIfade, argl, "{0}%"),
                "EndsWith" => LikeYaz(hedefIfade, argl, "%{0}"),
                "Contains" => LikeYaz(hedefIfade, argl, "%{0}%"),
                "ToUpper" => $"UPPER({hedefIfade})",
                "ToLower" => $"LOWER({hedefIfade})",
                "Trim" => $"TRIM({hedefIfade})",
                "Equals" => $"{hedefIfade} = {IfadeYaz(argl[0].Expression)}",
                "Substring" when argl.Count is 1 or 2 => SubstringYaz(hedefIfade, argl),
                "Replace" => $"REPLACE({hedefIfade}, {IfadeYaz(argl[0].Expression)}, {IfadeYaz(argl[1].Expression)})",
                "ToString" => hedefIfade, // biçim yoksa SQL'de dönüşüme gerek yok
                _ => throw new LinqCeviriHatasi($"'{metod}' metodu çevrilemedi: {Kisa(c)}"),
            };
        }

        private string SubstringYaz(string x, SeparatedSyntaxList<ArgumentSyntax> argl)
        {
            string fn = Motor("mssql") ? "SUBSTRING" : "SUBSTR";
            string bas = $"{IfadeYaz(argl[0].Expression)} + 1"; // C# 0-tabanlı → SQL 1-tabanlı
            if (argl[0].Expression is LiteralExpressionSyntax { Token.Value: int b })
                bas = (b + 1).ToString();
            return argl.Count == 2
                ? $"{fn}({x}, {bas}, {IfadeYaz(argl[1].Expression)})"
                : Motor("mssql") ? $"{fn}({x}, {bas}, LEN({x}))" : $"{fn}({x}, {bas})";
        }

        /// <summary>x.Ad.StartsWith("A") → x.Ad LIKE 'A%' (yalnız SABİT desen — dış değişken hata).</summary>
        private string LikeYaz(string hedef, SeparatedSyntaxList<ArgumentSyntax> argl, string kalip)
        {
            if (argl[0].Expression is not LiteralExpressionSyntax { Token.Value: string deger })
            {
                // Sabit OLMAYAN desen (v22-S1 saha m.1): u.TamUnvan.Contains(aramaModel.Unvan) — EF'te en
                // yaygın arama kalıbı. Motorun metin birleştirmesiyle '%' + ifade + '%' yazılır (önceden
                // net hatayla duruyordu; dış değeri elle yazmak sorguyu parametreliliğinden koparıyordu).
                string parca = IfadeYaz(argl[0].Expression);
                string joker = Literal("%");
                return $"{hedef} LIKE " + kalip switch
                {
                    "{0}%" => MetinBirlestir(parca, joker),
                    "%{0}" => MetinBirlestir(joker, parca),
                    _ => MetinBirlestir(MetinBirlestir(joker, parca), joker),
                };
            }
            if (deger.Contains('%') || deger.Contains('_'))
                Uyarilar.Add($"LIKE deseninde %/_ karakteri var ('{deger}') — SQL'de joker sayılır, gerekiyorsa kaçışlayın.");
            return $"{hedef} LIKE {Literal(string.Format(kalip, deger))}";
        }

        /// <summary>new[] { 1, 2 } / new List&lt;int&gt; { … } sabit dizisi → SQL literalleri (değilse null).</summary>
        private List<string>? DiziElemanlari(ExpressionSyntax e) => e switch
        {
            ImplicitArrayCreationExpressionSyntax a => ElemanYaz(a.Initializer),
            ArrayCreationExpressionSyntax { Initializer: { } init } => ElemanYaz(init),
            ObjectCreationExpressionSyntax { Initializer: { } init } => ElemanYaz(init),
            CollectionExpressionSyntax k => [.. k.Elements.OfType<ExpressionElementSyntax>()
                .Select(el => IfadeYaz(el.Expression))],
            _ => null,
        };

        private List<string> ElemanYaz(InitializerExpressionSyntax init) =>
            [.. init.Expressions.Select(IfadeYaz)];

        /// <summary>Grup toplaması: Count()→COUNT(*), Sum/Min/Max/Average(x=>ifade)→FN(ifade).</summary>
        private string GrupToplamaYaz(string metod, Grup grubu, SeparatedSyntaxList<ArgumentSyntax> argl)
        {
            if (metod is "Count" or "LongCount" && argl.Count == 0)
                return "COUNT(*)";
            if (metod is not ("Sum" or "Min" or "Max" or "Average" or "Count" or "LongCount"))
                throw new LinqCeviriHatasi($"Grup üzerinde '{metod}' çevrilemedi (Count/Sum/Min/Max/Average desteklenir).");

            LambdaExpressionSyntax l = argl[0].Expression as LambdaExpressionSyntax
                ?? throw new LinqCeviriHatasi($"{metod} içinde lambda bekleniyordu.");
            string p = Parametre(l).Identifier.Text;
            Deger? eski = _ad.GetValueOrDefault(p);
            _ad[p] = new Deger(grubu.KaynakTakma, null); // grup elemanı = kaynak satır
            string ic = IfadeYaz(Govde(l));
            if (eski is null) _ad.Remove(p); else _ad[p] = eski;

            if (metod is "Count" or "LongCount") // Count(x => kosul) → koşullu sayım
                return $"SUM(CASE WHEN {ic} THEN 1 ELSE 0 END)";
            string fn = metod == "Average" ? "AVG" : metod.ToUpperInvariant();
            return $"{fn}({ic})";
        }

        /// <summary>Lambda parametresini geçici alias'a bağlar; dönen eylem eski bağı geri yükler.</summary>
        private Action GeciciBagla(string param, string takma, SemaNesnesi tablo)
        {
            Deger? eskiAd = _ad.GetValueOrDefault(param);
            SemaNesnesi? eskiTablo = _takmaTablo.GetValueOrDefault(takma);
            _ad[param] = new Deger(takma, null);
            _takmaTablo[takma] = tablo;
            return () =>
            {
                if (eskiAd is null) _ad.Remove(param); else _ad[param] = eskiAd;
                if (eskiTablo is null) _takmaTablo.Remove(takma); else _takmaTablo[takma] = eskiTablo;
            };
        }

        /// <summary>Parantezleri soyulmuş query-syntax alt sorgusu — değilse null (v22-S1).</summary>
        private static QueryExpressionSyntax? AltSorguIfadesi(ExpressionSyntax e)
        {
            while (e is ParenthesizedExpressionSyntax p)
                e = p.Expression;
            return e as QueryExpressionSyntax;
        }

        /// <summary>
        /// (from … select …).Count() / .Any() / .Sum(x => …) → İLİŞKİLİ (correlated) skaler alt sorgu
        /// (v22-S1 saha m.1). Alt sorgu AYNI bağlamda çözülür — dış satıra başvurular (bd.BirlesmeId ==
        /// b.Id) korunur; iç adlar sonradan geri alınır ki dıştaki aynı adlı takmalar gölgelenmesin.
        /// </summary>
        private string AltSorguToplamaYaz(
            string metod, QueryExpressionSyntax q, SeparatedSyntaxList<ArgumentSyntax> argl)
        {
            IcYedek yedek = YedekAl();
            Sorgu ic = Cozumle(q);

            string? toplamaIfade = null;
            if (argl.Count == 1) // Count/Any(x => koşul) ya da Sum/Min/Max/Average(x => ifade)
            {
                LambdaExpressionSyntax l = LambdaAl(argl[0], metod);
                _ad[Parametre(l).Identifier.Text] = new Deger(ic.Takma, null);
                if (metod is "Count" or "LongCount" or "Any")
                    ic.Nerede.Add(KosulYaz(Govde(l)));
                else
                    toplamaIfade = IfadeYaz(Govde(l));
            }
            else if (metod is not ("Count" or "LongCount" or "Any"))
            {
                toplamaIfade = ic.Secim.Count == 1
                    ? ic.Secim[0].Ifade // (from … select x.Tutar).Sum()
                    : throw new LinqCeviriHatasi(
                        $"{metod}() için alt sorgu tek kolon seçmeli ya da {metod}(x => …) verilmeli.");
            }

            if (ic.Siralama.Count > 0 && ic.Al is null)
                ic.Siralama.Clear(); // toplama sıralamayı yok sayar (MSSQL alt sorguda ORDER BY'ı reddeder)

            YedekUygula(yedek, ic); // iç navigation JOIN'leri İÇ sorguya, iç adlar geri alınır

            if (metod == "Any")
            {
                ic.Secim = [];
                ic.TekDeger = "1";
                return $"EXISTS (\n{Girinti(SqlYazIc(ic).TrimEnd(';'))}\n)";
            }

            // Gruplama/Distinct/Take alt sorguyu SATIR KÜMESİ yapar → sayım bir sarmalda alınır
            if (ic.Gruplama.Count > 0 || ic.Tekil || ic.Al is not null || ic.Atla is not null)
            {
                if (metod is not ("Count" or "LongCount"))
                    throw new LinqCeviriHatasi(
                        $"{metod}(), gruplanmış/Distinct/Take'li alt sorgu üzerinde çevrilemez — alt sorguyu yalınlaştırın.");
                string kume = SqlYazIc(ic).TrimEnd(';');
                return $"(\n{Girinti($"SELECT COUNT(*) FROM (\n{Girinti(kume)}\n) a{++_takmaSayac}")}\n)";
            }

            ic.TekDeger = metod switch
            {
                "Count" or "LongCount" => "COUNT(*)",
                "Average" => $"AVG({toplamaIfade})",
                _ => $"{metod.ToUpperInvariant()}({toplamaIfade})",
            };
            return $"(\n{Girinti(SqlYazIc(ic).TrimEnd(';'))}\n)";
        }

        /// <summary>db.T[.Where(w)].Any/Count([k]) → EXISTS (SELECT 1 …) / (SELECT COUNT(*) …) (v20-S19).</summary>
        private string TabloSorgusuYaz(
            string metod, (SemaNesnesi Tablo, LambdaExpressionSyntax? Suzgec) kaynak,
            SeparatedSyntaxList<ArgumentSyntax> argl)
        {
            LambdaExpressionSyntax? ek = argl.Count == 1
                ? argl[0].Expression as LambdaExpressionSyntax
                    ?? throw new LinqCeviriHatasi($"{metod} içinde lambda bekleniyordu.")
                : null;

            string takma = (kaynak.Suzgec is not null ? Parametre(kaynak.Suzgec).Identifier.Text
                : ek is not null ? Parametre(ek).Identifier.Text : "x").ToLowerInvariant();
            var geriAl = new List<Action>();
            try
            {
                var kosullar = new List<string>();
                if (kaynak.Suzgec is { } w)
                {
                    geriAl.Add(GeciciBagla(Parametre(w).Identifier.Text, takma, kaynak.Tablo));
                    kosullar.Add(IfadeYaz(Govde(w)));
                }
                if (ek is { } k)
                {
                    geriAl.Add(GeciciBagla(Parametre(k).Identifier.Text, takma, kaynak.Tablo));
                    kosullar.Add(IfadeYaz(Govde(k)));
                }

                string tam = lehce.TamAdYaz(kaynak.Tablo.Sema, kaynak.Tablo.Ad);
                string nerede = kosullar.Count == 0 ? "" : $" WHERE {string.Join(" AND ", kosullar)}";
                return metod == "Any"
                    ? $"EXISTS (SELECT 1 FROM {tam} {takma}{nerede})"
                    : $"(SELECT COUNT(*) FROM {tam} {takma}{nerede})";
            }
            finally
            {
                foreach (Action a in geriAl) a();
            }
        }

        /// <summary>db.T[.Where(w)].Select(x => x.Kol).Contains(dış) → dış IN (SELECT kol …) (v20-S19).</summary>
        private string InAltSorguYaz(
            (SemaNesnesi Tablo, LambdaExpressionSyntax? Suzgec) kaynak,
            LambdaExpressionSyntax secim, ExpressionSyntax dis)
        {
            string disSql = IfadeYaz(dis); // dış ifade İÇ bağlamadan ÖNCE çözülür (gölgeleme olmasın)
            string takma = Parametre(secim).Identifier.Text.ToLowerInvariant();
            var geriAl = new List<Action> { GeciciBagla(Parametre(secim).Identifier.Text, takma, kaynak.Tablo) };
            try
            {
                var kosullar = new List<string>();
                if (kaynak.Suzgec is { } w)
                {
                    geriAl.Add(GeciciBagla(Parametre(w).Identifier.Text, takma, kaynak.Tablo));
                    kosullar.Add(IfadeYaz(Govde(w)));
                }
                string kol = IfadeYaz(Govde(secim));
                string tam = lehce.TamAdYaz(kaynak.Tablo.Sema, kaynak.Tablo.Ad);
                string nerede = kosullar.Count == 0 ? "" : $" WHERE {string.Join(" AND ", kosullar)}";
                return $"{disSql} IN (SELECT {kol} FROM {tam} {takma}{nerede})";
            }
            finally
            {
                foreach (Action a in geriAl) a();
            }
        }

        // ---- ters navigation (m.Siparisler.Any / Count) ----

        private sealed record TersNav(string Takma, YabanciAnahtar Fk);

        private TersNav? TersNavigation(MemberAccessExpressionSyntax uye)
        {
            if (uye.Expression is not IdentifierNameSyntax kok
                || !_ad.TryGetValue(kok.Identifier.Text, out Deger? d) || d.Takma is not { } takma
                || _takmaTablo.GetValueOrDefault(takma) is not { } tablo)
                return null;
            if (KolonVar(tablo, uye.Name.Identifier.Text) && tablo.Kolonlar.Count > 0)
                return null; // gerçek kolon — navigation değil

            string ad = uye.Name.Identifier.Text;
            foreach (YabanciAnahtar fk in fkler)
            {
                if (!AdEs(fk.HedefTablo, tablo.Ad))
                    continue;
                if (AdEs(fk.KaynakTablo, ad) || TekilAdaylari(ad).Any(t => AdEs(fk.KaynakTablo, t)))
                    return new TersNav(takma, fk);
            }
            return null;
        }

        private string TersNavYaz(string metod, TersNav ters, SeparatedSyntaxList<ArgumentSyntax> argl)
        {
            YabanciAnahtar fk = ters.Fk;
            string ic = "alt";
            string baglanti = string.Join(" AND ", fk.KaynakKolonlar.Zip(fk.HedefKolonlar,
                (a, b) => $"{ic}.{K(a)} = {ters.Takma}.{K(b)}"));

            string ek = "";
            if (argl.Count == 1)
            {
                LambdaExpressionSyntax l = argl[0].Expression as LambdaExpressionSyntax
                    ?? throw new LinqCeviriHatasi($"{metod} içinde lambda bekleniyordu.");
                string p = Parametre(l).Identifier.Text;
                Deger? eski = _ad.GetValueOrDefault(p);
                _ad[p] = new Deger(ic, null);
                SemaNesnesi? kaynakTablo = nesneler.FirstOrDefault(
                    n => n.Tur == SemaNesneTuru.Tablo && AdEs(n.Ad, fk.KaynakTablo));
                if (kaynakTablo is not null)
                    _takmaTablo[ic] = kaynakTablo;
                ek = $" AND ({IfadeYaz(Govde(l))})";
                if (eski is null) _ad.Remove(p); else _ad[p] = eski;
            }

            string altSorgu =
                $"SELECT 1 FROM {lehce.TamAdYaz(fk.KaynakSema, fk.KaynakTablo)} {ic} WHERE {baglanti}{ek}";
            return metod == "Any"
                ? $"EXISTS ({altSorgu})"
                : $"(SELECT COUNT(*) FROM {lehce.TamAdYaz(fk.KaynakSema, fk.KaynakTablo)} {ic} WHERE {baglanti}{ek})";
        }

        // ---- ikili işleçler ----

        private string IkiliYaz(BinaryExpressionSyntax b)
        {
            bool solNull = b.Left.IsKind(SyntaxKind.NullLiteralExpression);
            bool sagNull = b.Right.IsKind(SyntaxKind.NullLiteralExpression);
            if (solNull || sagNull)
            {
                string taraf = IfadeYaz(solNull ? b.Right : b.Left);
                // LEFT JOIN satırının kendisi null'a kıyaslanıyorsa (tb == null) alias tek başına
                // geçersizdir — iç anahtar kolonuna çevrilir: "t.[FirmaId] IS NULL" (v20-S21 saha m.1).
                if (_solAnahtar.TryGetValue(taraf, out string? icAnahtar))
                    taraf = icAnahtar;
                return b.Kind() switch
                {
                    SyntaxKind.EqualsExpression => $"{taraf} IS NULL",
                    SyntaxKind.NotEqualsExpression => $"{taraf} IS NOT NULL",
                    _ => throw new LinqCeviriHatasi("null yalnız == / != ile karşılaştırılabilir."),
                };
            }

            // Mantıksal bağlaç: taraflar KOŞUL bağlamındadır — yalın bit kolon "= 1" alır (v22-S1)
            if (b.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
                return $"{KosulYaz(b.Left)} "
                    + $"{(b.IsKind(SyntaxKind.LogicalAndExpression) ? "AND" : "OR")} {KosulYaz(b.Right)}";

            string sol = IfadeYaz(b.Left);
            string sag = IfadeYaz(b.Right);
            if (b.IsKind(SyntaxKind.AddExpression) && MetinMi(b))
                return MetinBirlestir(sol, sag); // metin birleştirme motor-özel tam ifade döndürür
            if (b.IsKind(SyntaxKind.CoalesceExpression))
                return $"COALESCE({sol}, {sag})"; // ?? (v20-S19) — COALESCE dört motorda da var

            string islec = b.Kind() switch
            {
                SyntaxKind.EqualsExpression => "=",
                SyntaxKind.NotEqualsExpression => "<>",
                SyntaxKind.GreaterThanExpression => ">",
                SyntaxKind.GreaterThanOrEqualExpression => ">=",
                SyntaxKind.LessThanExpression => "<",
                SyntaxKind.LessThanOrEqualExpression => "<=",
                SyntaxKind.LogicalAndExpression => "AND",
                SyntaxKind.LogicalOrExpression => "OR",
                SyntaxKind.AddExpression => "+",
                SyntaxKind.SubtractExpression => "-",
                SyntaxKind.MultiplyExpression => "*",
                SyntaxKind.DivideExpression => "/",
                SyntaxKind.ModuloExpression => Motor("oracle") ? throw new LinqCeviriHatasi(
                    "Oracle'da % işleci yok — MOD(x, y) elle yazın.") : "%",
                _ => throw new LinqCeviriHatasi($"İşleç çevrilemedi: {b.OperatorToken.Text}"),
            };
            return $"{sol} {islec} {sag}";
        }

        /// <summary>+ metin birleştirme mi? (iki taraftan biri string sabitse).</summary>
        private static bool MetinMi(BinaryExpressionSyntax b) =>
            b.Left.IsKind(SyntaxKind.StringLiteralExpression) || b.Right.IsKind(SyntaxKind.StringLiteralExpression);

        private string MetinBirlestir(string sol, string sag) => lehce.MotorId switch
        {
            "mssql" => $"{sol} + {sag}",
            "mysql" => $"CONCAT({sol}, {sag})",
            _ => $"{sol} || {sag}",
        };

        private static int SabitSayi(ExpressionSyntax e, string metod) =>
            e is LiteralExpressionSyntax { Token.Value: int n }
                ? n
                : throw new LinqCeviriHatasi($"{metod} için sabit sayı bekleniyordu: {Kisa(e)}");

        private static string Kisa(SyntaxNode e)
        {
            string s = e.ToString();
            return s.Length <= 60 ? s : s[..57] + "...";
        }

        // ---- SQL yazımı ────────────────────────────────────────────────────────────────────

        public string SqlYaz(Sorgu s)
        {
            s.Katilimlar.AddRange(_sonNav); // ifade çevirisi sırasında biriken navigation JOIN'leri
            _sonNav.Clear();
            return SqlYazIc(s);
        }

        /// <summary>Sorgu → SQL. Navigation JOIN'lerine DOKUNMAZ: iç sorgu (türetilmiş tablo, skaler
        /// alt sorgu) yazarken dış sorgunun JOIN'leri içeri sızmasın (v22-S1).</summary>
        private string SqlYazIc(Sorgu s)
        {
            var satirlar = new List<string>();
            string secim = s.TekDeger
                ?? (s.Secim.Count == 0
                    ? $"{s.Takma}.*"
                    : string.Join(", ", s.Secim.Select(k =>
                        k.Ad is { } ad && !k.Ifade.EndsWith($".{K(ad)}", StringComparison.Ordinal)
                            ? $"{k.Ifade} AS {K(ad)}"
                            : k.Ifade)));

            string tekil = s.Tekil ? "DISTINCT " : "";
            string tavanBasi = s.Al is { } al && s.Atla is null ? lehce.SatirSinirBasi(al) : "";
            satirlar.Add($"SELECT {tekil}{tavanBasi}{secim}");
            // Değişken (CTE) kaynak şemasız, tırnaklı yalın adla yazılır (v20-S20)
            satirlar.Add($"FROM {(s.KaynakDegisken ? K(s.Tablo) : TamAd(s.Sema, s.Tablo))} {s.Takma}");
            foreach (Katilim k in s.Katilimlar)
            {
                string kaynakAd = k.AltSorgu is { } alt // türetilmiş tablo (v22-S1)
                    ? $"(\n{Girinti(alt)}\n)"
                    : k.Degisken ? K(k.Tablo) : TamAd(k.Sema, k.Tablo);
                satirlar.Add(k.Tur == "CROSS JOIN"
                    ? $"CROSS JOIN {kaynakAd} {k.Takma}"
                    : $"{k.Tur} {kaynakAd} {k.Takma} ON {k.Kosul}");
            }
            if (s.Nerede.Count > 0)
                satirlar.Add($"WHERE {string.Join("\n  AND ", s.Nerede.Select(Parantezle))}");
            if (s.Gruplama.Count > 0)
                satirlar.Add($"GROUP BY {string.Join(", ", s.Gruplama)}");
            if (s.GrupKosul.Count > 0)
                satirlar.Add($"HAVING {string.Join(" AND ", s.GrupKosul.Select(Parantezle))}");
            if (s.Siralama.Count > 0)
                satirlar.Add($"ORDER BY {string.Join(", ", s.Siralama.Select(o => o.Azalan ? $"{o.Ifade} DESC" : o.Ifade))}");

            SayfalamaYaz(s, satirlar);

            string sql = string.Join("\n", satirlar);
            string sonEk = s.Al is { } a2 && s.Atla is null ? lehce.SatirSinirSonu(a2) : "";
            sql += sonEk + ";";

            if (s.Varlik) // Any() → EXISTS sarmalı
            {
                string ic = sql[..^1];
                sql = Motor("oracle")
                    ? $"SELECT CASE WHEN EXISTS (\n{Girinti(ic)}\n) THEN 1 ELSE 0 END FROM DUAL;"
                    : Motor("postgres")
                        ? $"SELECT EXISTS (\n{Girinti(ic)}\n);"
                        : $"SELECT CASE WHEN EXISTS (\n{Girinti(ic)}\n) THEN 1 ELSE 0 END;";
            }
            return sql;
        }

        /// <summary>Skip/Take birlikte: motorun OFFSET söz dizimi (MSSQL/Oracle FETCH, PG/MySQL LIMIT).</summary>
        private void SayfalamaYaz(Sorgu s, List<string> satirlar)
        {
            if (s.Atla is not { } atla)
                return;
            if (s.Siralama.Count == 0)
                throw new LinqCeviriHatasi("Skip için önce OrderBy gerekir (SQL, sırasız OFFSET'e izin vermez).");
            satirlar.Add(lehce.MotorId switch
            {
                "postgres" or "mysql" => s.Al is { } al ? $"LIMIT {al} OFFSET {atla}" : $"OFFSET {atla}",
                _ => s.Al is { } al
                    ? $"OFFSET {atla} ROWS FETCH NEXT {al} ROWS ONLY"
                    : $"OFFSET {atla} ROWS",
            });
        }

        private string TamAd(string? sema, string tablo) =>
            sema is null ? K(tablo) : lehce.TamAdYaz(sema, tablo);

        private static string Parantezle(string kosul) =>
            kosul.Contains(" OR ", StringComparison.Ordinal) ? $"({kosul})" : kosul;

        private static string Girinti(string metin) =>
            "  " + metin.Replace("\n", "\n  ");
    }
}
