using System.Text.RegularExpressions;
using SQLST.Contracts;

namespace SQLST.Application;

/// <param name="Oncelik">Büyük olan listede önce gelir (snippet > kolon > nesne > anahtar sözcük).</param>
/// <param name="Snippet">
/// Doluysa öneri bir kod parçasıdır: editöre <see cref="Contracts.Snippet.Govde"/> girer ve
/// imleç <c>$0</c> işaretine taşınır — düz metin değiştirmesi YETMEZ (V5-S4).
/// </param>
/// <param name="Ekle">
/// Editöre GİRİLECEK metin, gösterilen/süzülen <paramref name="Metin"/>'den farklıysa. Nesne
/// önerilerinde ad sade süzülür ("Firma") ama <c>şema.ad</c> girilir ("Mersis.Firma") — kullanıcı
/// isteği 2026-07-21: sorgular şema-nitelikli yazılsın. Null ise <paramref name="Metin"/> girilir.
/// </param>
public sealed record TamamlamaOnerisi(
    string Metin, string? Aciklama, double Oncelik, Snippet? Snippet = null, string? Ekle = null);

/// <summary>
/// #9 (2026-07-29): bir SP/fonksiyon çağrısının parametre imzası. Editör imleç bir çağrının argüman
/// bölgesindeyken yüzen ipuçta gösterilir (AvalonEdit InsightWindow). UI'sız/saf — App yalnız gösterir.
/// </summary>
public sealed record ParametreImzasi(string NesneAd, IReadOnlyList<SemaParametresi> Parametreler);

/// <summary>
/// IntelliSense beyni (V2-S3, FG-3.8): metin + imleç + şema önbelleğinden öneri üretir.
/// UI'sız ve saf — AvalonEdit CompletionWindow yalnız gösterir (07-r2 §1 kararı).
/// Bağlam: "x." → x bir takma ad/tablo ise kolonları, şema ise nesneleri;
/// düz sözcükte nesne adları + temel anahtar sözcükler.
/// </summary>
public static partial class OtoTamamlama
{
    /// <summary>Şema önerisinin açıklama etiketi. TamamlamaVerisi bunu görüp şemaya özel glif/renk
    /// verir (nesnelerle aynı öncelikte olsalar da tabloyla karışmasınlar).</summary>
    public const string SemaEtiketi = "Şema";

    private static readonly string[] AnahtarSozcukler =
    [
        "SELECT", "FROM", "WHERE", "ORDER BY", "GROUP BY", "HAVING", "TOP", "DISTINCT",
        "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "ON",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "MERGE",
        "EXEC", "DECLARE", "BEGIN", "END", "IF", "ELSE", "WHILE", "RETURN",
        "CASE", "WHEN", "THEN", "IS NULL", "IS NOT NULL", "NOT", "AND", "OR", "IN",
        "LIKE", "BETWEEN", "EXISTS", "UNION ALL", "WITH", "AS",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "GETDATE", "CAST", "CONVERT", "COALESCE",
    ];

    /// <summary>Yüklem bağlamında (WHERE/ON/AND/OR/HAVING) anlamlı sözcükler (#4, kullanıcı isteği
    /// 2026-07-29: "bağlama göre süz"). Mantık/karşılaştırma + ifade fonksiyonları + WHERE'i sürdüren
    /// cümle sözcükleri. SELECT/INSERT/CREATE/FROM gibi cümle başlatıcılar burada GÜRÜLTÜ — gizlenir.</summary>
    private static readonly string[] YuklemSozcukleri =
    [
        "AND", "OR", "NOT", "IN", "LIKE", "BETWEEN", "EXISTS", "IS NULL", "IS NOT NULL",
        "CASE", "WHEN", "THEN", "AS",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "GETDATE", "CAST", "CONVERT", "COALESCE",
        "ORDER BY", "GROUP BY", "HAVING",
    ];

    /// <summary>
    /// Önerileri ve tamamlanacak sözcüğün başlangıç ofsetini döner (CompletionWindow.StartOffset).
    /// Önbellek yoksa yalnız anahtar sözcükler önerilir; boş liste = pencere açma.
    /// </summary>
    /// <param name="motor">
    /// Öneri kümesini kısıtlar (V5-S4). <see cref="AnahtarSozcukler"/> T-SQL/SQL sözcükleridir;
    /// MongoDB'de önerilmezler — kullanıcının seçtiği anda çalışmayan metin girerdi.
    /// <b>Bu, V5-S4'te düzeltilen mevcut bir kusurdu:</b> liste motordan bağımsız olduğu için
    /// <c>TOP</c>/<c>GETDATE</c> Mongo sekmesinde de öneriliyordu.
    /// </param>
    /// <param name="snippetler">
    /// O motorda geçerli kod parçaları (depo zaten motora göre süzer). Kısayollar en üstte
    /// çıksın diye önceliği en yüksektir.
    /// </param>
    /// <summary>Sözcüğün yazıldığı yer: FROM/JOIN → Tablo; WHERE/ON/AND/OR/HAVING → Yuklem (yalnız
    /// kolon/alias, tablo/şema DEĞİL); SELECT/GROUP BY/ORDER BY/SET → Kolon (kolon + tablo/şema da);
    /// hiçbiri → Genel. Kolon ve Yuklem ortak "kolon referans bağlamı"dır (alias+kapsam kolonu).</summary>
    private enum Baglam { Genel, Tablo, Kolon, Yuklem }

    public static IReadOnlyList<TamamlamaOnerisi> Oner(
        string metin, int ofset, SemaOnbellegi? onbellek, out int kelimeBasi,
        MotorTuru motor = MotorTuru.Mssql, IReadOnlyList<Snippet>? snippetler = null)
    {
        kelimeBasi = KelimeBasiBul(metin, ofset);

        // MongoDB (C7, 2026-07-25): sorgu SQL değil find/aggregate JSON'udur — SQL bağlam
        // çözümü (FROM/WHERE, '.' üyeleri, '-- yorum') burada anlamsız. Belge-farkında öneri
        // SAF MongoBulTamamlama.EditorOner'dan gelir (kök anahtarlar → koleksiyon → alan/operatör);
        // snippet'ler yine en üstte (depo Mongo'ya göre süzülmüş verir).
        if (motor == MotorTuru.Mongo)
        {
            MongoTamamlamaSonucu? mongo = MongoBulTamamlama.EditorOner(
                metin, ofset, onbellek?.Nesneler ?? []);
            var mongoOneriler = new List<TamamlamaOnerisi>();
            foreach (Snippet snippet in snippetler ?? [])
                mongoOneriler.Add(new TamamlamaOnerisi(snippet.Kisayol, snippet.Aciklama, 4, snippet));
            if (mongo is not null)
            {
                kelimeBasi = mongo.ParcaBas;
                foreach (MongoOneri o in mongo.Oneriler)
                    mongoOneriler.Add(new TamamlamaOnerisi(o.Goster, o.Aciklama, 3, Ekle: o.Ekle));
            }
            return mongoOneriler;
        }

        // Yorum ya da string literali içinde ÖNERİ YOK (kullanıcı isteği 2026-07-21): "-- not"
        // satırında ya da '...' içinde tablo/kolon önerisi anlamsız ve dikkat dağıtıcıydı.
        // TAM metne göre bakılır — yorum/dize ifade sınırını (boş satır) aşabilir.
        if (YorumYaDaMetinIcinde(metin, ofset))
            return [];

        // Bağlam/tablo/alias taraması yalnız İMLEÇTEKİ ifadeyi kapsamalı (kullanıcı bulgusu 2026-07-28):
        // tek tablolu bir SELECT'te bile editördeki DİĞER ifadelerin FROM/JOIN tabloları öneriye
        // düşüp WHERE'i karıştırıyordu. İfade sınırı ';' / tek başına GO / BOŞ satır'dır (canlı yarım
        // metinde çalışır; tam ScriptDom ayrıştırıcısı yazılırken geçersiz metinde null döner).
        // 'kelimeBasi' MUTLAK kalır (CompletionWindow.StartOffset editör metnine göredir); içeride
        // yalnız ifadeye göreli 'ykelimeBasi' kullanılır (sınır sözcük-dışı karakter → kelime bölünmez).
        (int ifadeBas, int ifadeSon) = SqlIfadeAralik(metin, ofset);
        string ifade = metin[ifadeBas..ifadeSon];
        int ykelimeBasi = Math.Max(0, kelimeBasi - ifadeBas);

        // "x." üyeleri: sözcüğün hemen öncesi nokta mı?
        if (ykelimeBasi > 0 && ifade[ykelimeBasi - 1] == '.')
        {
            string niteleyici = NiteleyiciOku(ifade, ykelimeBasi - 1);
            return niteleyici.Length == 0 || onbellek is null
                ? []
                : UyeOnerileri(ifade, niteleyici, onbellek);
        }

        var oneriler = new List<TamamlamaOnerisi>();

        // Snippet'ler en üstte: kullanıcı kısayolu bilerek yazar, aramaz.
        foreach (Snippet snippet in snippetler ?? [])
            oneriler.Add(new TamamlamaOnerisi(snippet.Kisayol, snippet.Aciklama, 4, snippet));

        // @parametreler (kullanıcı bulgusu 2026-07-31: "@ ile parametreler öneri olarak gelmiyor"):
        // BELGEDE geçen @tanımlayıcılar (DECLARE'ler + kullanımlar) önerilir — SP gövdesi düzenlerken
        // parametre adları elde. TÜM metinden toplanır (DECLARE ifade sınırının dışında kalabilir);
        // yazılmakta olan sözcüğün kendisi (tek geçiş) önerilmez. Yüksek öncelik: '@' yazan parametre arar.
        string mevcutKelime = metin[kelimeBasi..Math.Min(ofset, metin.Length)];
        var parametreSayaci = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match pm in ParametreDeseni().Matches(metin))
            parametreSayaci[pm.Value] = parametreSayaci.GetValueOrDefault(pm.Value) + 1;
        foreach ((string p, int adet) in parametreSayaci)
            if (adet > 1 || !p.Equals(mevcutKelime, StringComparison.OrdinalIgnoreCase))
                oneriler.Add(new TamamlamaOnerisi(p, "parametre", 3.8));

        Baglam baglam = BaglamBul(ifade, ykelimeBasi);

        // Kolon referans bağlamı (Kolon + Yuklem): buralarda önce TAKMA ADLAR (kullanıcı isteği
        // 2026-07-28: "ON/WHERE'de tanımladığım 'k'yı öner") — kolonların hemen üstünde (3.5). Şemadan
        // BAĞIMSIZ (sorgu metninden gelir) → önbellek yüklenmemişse bile çıkar.
        bool kolonBaglami = baglam is Baglam.Kolon or Baglam.Yuklem;
        if (kolonBaglami)
            foreach (TamamlamaOnerisi a in KapsamTakmaAdlari(ifade))
                oneriler.Add(a);

        if (onbellek is not null)
        {
            // Kapsamdaki tabloların kolonları (kullanıcı isteği: "WHERE'den sonra kolon adları gelmiyor").
            if (kolonBaglami)
                foreach (TamamlamaOnerisi k in KapsamKolonlari(ifade, onbellek))
                    oneriler.Add(k);

            // #7 (kullanıcı isteği 2026-07-29): "⋯ tüm kolonlar" yalnız Kolon (SELECT/GROUP BY/ORDER BY)
            // bağlamında — Yuklem'de (WHERE) tüm kolonları açmak anlamsız. Seçilince kolon listesi genişler.
            if (baglam == Baglam.Kolon)
                foreach (TamamlamaOnerisi t in KapsamTumKolonOnerileri(ifade, onbellek))
                    oneriler.Add(t);

            // #8 (kullanıcı isteği 2026-07-29): JOIN … ON bağlamında FK'dan eşitlik önerisi
            // ("a.XId = b.Id") — listenin EN ÜSTÜNDE ilk öneri (öncelik 4.0), otomatik YAZILMAZ; Enter/
            // Tab ile alınır. Yalnız imleç ON'un ardındaki yüklemde ve kapsamda 2+ tablo + FK varsa.
            if (baglam == Baglam.Yuklem && EnYakinBaglamKelimesi(ifade, ykelimeBasi) == "ON")
                foreach (TamamlamaOnerisi f in FkJoinOnerileri(ifade, onbellek))
                    oneriler.Add(f);

            // Şema + tüm-tablo listesi: YÜKLEM (WHERE/ON/AND/OR/HAVING) DIŞINDA (kullanıcı isteği
            // 2026-07-28: "WHERE'den sonra sadece kolonlar"). Yüklemde yüzlerce tablo, kapsam
            // kolonlarını bastırıyordu. SELECT/GROUP BY/ORDER BY, FROM/JOIN ve Genel'de gelmeye devam eder.
            if (baglam != Baglam.Yuklem)
            {
                // Şema adları (kullanıcı isteği 2026-07-22: "şemalar otomatik tamamlamaya çıkmıyor").
                // Seçip "." yazınca o şemanın nesneleri (UyeOnerileri) gelir. Nesnelerle AYNI öncelikte
                // ama TamamlamaVerisi ayrı boyar.
                foreach (string sema in onbellek.Nesneler
                             .Select(n => n.Sema)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                    oneriler.Add(new TamamlamaOnerisi(sema, SemaEtiketi, 2));

                // Tablolar/nesneler: ad sade süzülür ama ŞEMA.AD girilir (kullanıcı isteği: sorgular
                // şema-nitelikli yazılsın → karışıklık azalır).
                foreach (SemaNesnesi nesne in onbellek.Nesneler)
                    oneriler.Add(new TamamlamaOnerisi(nesne.Ad, $"{nesne.Tur} — {nesne.TamAd}", 2, Ekle: nesne.TamAd));
            }
        }

        if (motor != MotorTuru.Mongo)
        {
            // #4 (kullanıcı isteği 2026-07-29): yüklem bağlamında (WHERE/ON/AND/OR/HAVING) yalnız
            // mantık/ifade sözcükleri — orada SELECT/INSERT/CREATE önermek gürültüydü. Diğer bağlamlarda
            // tam liste kalır (SELECT'ten sonra FROM, FROM'dan sonra JOIN yazılır → başlatıcılar gerekli).
            string[] sozcukler = baglam == Baglam.Yuklem ? YuklemSozcukleri : AnahtarSozcukler;
            foreach (string sozcuk in sozcukler)
                oneriler.Add(new TamamlamaOnerisi(sozcuk, null, 1));
        }

        return oneriler;
    }

    /// <summary>
    /// #3 (kullanıcı isteği 2026-07-29): imleç bir cümle sözcüğünün ardındaki boşlukta mı — öneri
    /// penceresi KENDİLİĞİNDEN açılmalı mı? FROM/JOIN/WHERE/ON/AND/OR/HAVING/SET/BY/INTO/APPLY/SELECT/
    /// UPDATE sonrası (ya da virgül sonrası) tablo/kolon yazılır → yazmadan öneri düşsün. App katmanı
    /// yalnız YENİ boşluk yazıldığında çağırır; burada imlecin boşluk ardında olduğu doğrulanır.
    /// Yorum/dize içinde tetiklemez (Oner zaten boş dönerdi; erken çıkalım — pencere hiç açılmasın).
    /// </summary>
    public static bool OtoAcilmali(string metin, int ofset)
    {
        if (ofset <= 0 || ofset > metin.Length) return false;
        if (!char.IsWhiteSpace(metin[ofset - 1])) return false;   // imleç boşluğun hemen ardında
        if (YorumYaDaMetinIcinde(metin, ofset)) return false;
        // İmleç metnin sonundaysa (en yaygın: sona yazarken) alt-dize KOPYASI alma — büyük belgede bedava kazanç.
        string bas = ofset == metin.Length ? metin : metin[..ofset];
        return OtoTetikDeseni().IsMatch(bas);
    }

    /// <summary>
    /// #9 (2026-07-29): imleç bir SP/fonksiyon çağrısının argüman bölgesindeyse, çağrılan nesnenin
    /// parametre imzasını döner; değilse null. İki desen: fonksiyon "fn(" (imleci saran kapanmamış
    /// parantez) ve "EXEC şema.sp &lt;args&gt;" (parantezsiz). Yalnız İMLEÇTEKİ ifadeye bakar; yorum/
    /// dize içinde ya da önbellek yoksa null. Nesne SP/Fonksiyon değilse (ya da bulunamazsa) null.
    /// </summary>
    public static ParametreImzasi? ParametreImzasiBul(string metin, int ofset, SemaOnbellegi? onbellek)
    {
        if (onbellek is null || ofset <= 0 || ofset > metin.Length)
            return null;
        if (YorumYaDaMetinIcinde(metin, ofset))
            return null;

        (int ifadeBas, _) = SqlIfadeAralik(metin, ofset);
        string ifade = metin[ifadeBas..Math.Min(ofset, metin.Length)];

        // 1) Fonksiyon çağrısı: imleç kapanmamış '(' içinde mi? '(' öncesi tanımlayıcı = fonksiyon/SP.
        if (KapanmamisParantezNesnesi(ifade) is { } fnAd
            && NesneParametreli(onbellek, fnAd) is { } fn)
            return new ParametreImzasi(fn.TamAd, fn.Parametreler);

        // 2) EXEC şema.sp <args>: imleçten önceki son EXEC <ad> (parantezsiz SP çağrısı).
        Match m = ExecDeseni().Match(ifade);
        if (m.Success && NesneParametreli(onbellek, m.Groups["ad"].Value) is { } sp)
            return new ParametreImzasi(sp.TamAd, sp.Parametreler);

        return null;
    }

    /// <summary>Metnin sonundan geriye giderek imleci saran kapanmamış '(' bulur; hemen öncesindeki
    /// tanımlayıcıyı (fonksiyon/SP adı) döner. Dengeli parantezler atlanır; yoksa ya da ad boşsa null.</summary>
    private static string? KapanmamisParantezNesnesi(string metin)
    {
        int derinlik = 0;
        for (int i = metin.Length - 1; i >= 0; i--)
        {
            char c = metin[i];
            if (c == ')')
                derinlik++;
            else if (c == '(')
            {
                if (derinlik > 0) { derinlik--; continue; }
                int j = i - 1;
                while (j >= 0 && char.IsWhiteSpace(metin[j])) j--;
                int son = j + 1, bas = son;
                while (bas > 0 && (char.IsLetterOrDigit(metin[bas - 1]) || metin[bas - 1] is '_' or '[' or ']' or '.' or '#'))
                    bas--;
                return son > bas ? metin[bas..son] : null;
            }
        }
        return null;
    }

    /// <summary>SP ya da fonksiyonu ada göre bulur (parametre imzası için — tablo/view değil).</summary>
    private static SemaNesnesi? NesneParametreli(SemaOnbellegi onbellek, string ad)
    {
        string sade = ad.Split('.')[^1].Trim('[', ']');
        return onbellek.Nesneler.FirstOrDefault(n =>
            n.Ad.Equals(sade, StringComparison.OrdinalIgnoreCase)
            && n.Tur is SemaNesneTuru.StoredProcedure or SemaNesneTuru.Fonksiyon);
    }

    /// <summary>
    /// İmleçten önceki en yakın cümle anahtar sözcüğüne göre bağlamı bulur. FROM/JOIN/INTO/UPDATE/
    /// APPLY → Tablo; WHERE/ON/AND/OR/HAVING → Yuklem (yalnız kolon/alias); SET/SELECT/GROUP BY/
    /// ORDER BY → Kolon; hiçbiri → Genel.
    /// </summary>
    private static Baglam BaglamBul(string metin, int kelimeBasi)
    {
        Match m = SonBaglamKelimesi().Match(metin[..kelimeBasi]);
        if (!m.Success)
            return Baglam.Genel;
        string kw = WhitespaceDeseni().Replace(m.Value, " ").ToUpperInvariant();
        if (kw is "FROM" or "INTO" or "APPLY" or "UPDATE" || kw.EndsWith("JOIN", StringComparison.Ordinal))
            return Baglam.Tablo;
        // Yüklem (WHERE ve devamı): boolean koşul yazılır → yalnız kolon/alias, tablo/şema DEĞİL
        // (kullanıcı isteği 2026-07-28). AND/OR bir WHERE'i sürdürür; ON join yüklemi; HAVING grup yüklemi.
        if (kw is "WHERE" or "ON" or "AND" or "OR" or "HAVING")
            return Baglam.Yuklem;
        return Baglam.Kolon;
    }

    /// <summary>
    /// İmleçteki "ifade"nin kaba metin aralığı [Bas, Son) (kullanıcı bulgusu 2026-07-28). Sınır:
    /// <c>;</c>, tek başına <c>GO</c>/<c>GO n</c> satırı ya da BOŞ satır — kullanıcı sorgu defterinde
    /// ifadeleri boş satırla ayırır. CANLI (yazılırken, geçersiz) metinde çalışır; <see cref="SqlCozumleyici"/>
    /// tam ayrıştırıcısı yarım metinde null döneceği için burada kullanılmaz. Yorum/dize İÇİ ayrı ele
    /// alınmaz (çağıran <c>YorumYaDaMetinIcinde</c>'yi TAM metne uygular); bu yalnız tablo/alias
    /// taramasının kapsamını daraltır. Çok satırlı JOIN'de (içinde boş satır yok) tüm ifade tek parçadır.
    /// </summary>
    internal static (int Bas, int Son) SqlIfadeAralik(string metin, int ofset)
    {
        ofset = Math.Clamp(ofset, 0, metin.Length);
        int n = metin.Length;

        // İmleç satırının başı; yukarı doğru ilk SINIR satırının altındaki satırın başına kadar.
        int bas = SatirBasi(metin, ofset);
        while (bas > 0)
        {
            int oncekiSon = bas - 1;                       // önceki satırın '\n'i
            int oncekiBas = SatirBasi(metin, oncekiSon);
            if (SinirSatiriMi(metin, oncekiBas, oncekiSon)) break;
            bas = oncekiBas;
        }

        // İmleç satırının sonu; aşağı doğru ilk SINIR satırının üstündeki satırın sonuna kadar.
        int son = SatirSonu(metin, ofset);
        while (son < n)
        {
            int sonrakiBas = son + 1;                      // '\n' sonrası
            int sonrakiSon = SatirSonu(metin, sonrakiBas);
            if (SinirSatiriMi(metin, sonrakiBas, sonrakiSon)) break;
            son = sonrakiSon;
        }

        // Aynı satırda birden çok ifade: ';' penceresini daha da daraltır (yalnız [bas, son) içinde).
        if (ofset > 0)
        {
            int p = metin.LastIndexOf(';', ofset - 1);
            if (p >= bas) bas = p + 1;
        }
        if (ofset < n)
        {
            int p = metin.IndexOf(';', ofset);
            if (p >= 0 && p < son) son = p;
        }

        return (bas, Math.Max(bas, son));
    }

    private static int SatirBasi(string metin, int ofset)
    {
        int i = Math.Clamp(ofset, 0, metin.Length);
        while (i > 0 && metin[i - 1] != '\n') i--;
        return i;
    }

    private static int SatirSonu(string metin, int ofset)
    {
        int i = Math.Clamp(ofset, 0, metin.Length);
        while (i < metin.Length && metin[i] != '\n') i++;
        return i;
    }

    /// <summary>[bas, son) satırı bir ifade sınırı mı: yalnız boşluk (boş satır) ya da tek başına GO / GO n.</summary>
    private static bool SinirSatiriMi(string metin, int bas, int son)
    {
        string satir = metin[bas..Math.Min(son, metin.Length)].Trim();
        if (satir.Length == 0) return true;
        if (satir.Equals("GO", StringComparison.OrdinalIgnoreCase)) return true;
        return satir.StartsWith("GO ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(satir.AsSpan(3).Trim(), out _);
    }

    /// <summary>
    /// Kapsamdaki (FROM/JOIN/…) tabloları niteleyicisiyle döner: takma ad varsa o, yoksa tablo adı.
    /// Her FROM/JOIN oluşumu ayrı satırdır — self-join'de (FROM T a JOIN T b) a ve b ayrı ayrı gelir,
    /// böylece #6 nitelendirmesi (a.Id/b.Id) self-join'de de doğru çalışır.
    /// </summary>
    private static List<(SemaNesnesi Tablo, string Niteleyici)> KapsamTablolar(string metin, SemaOnbellegi onbellek)
    {
        var sonuc = new List<(SemaNesnesi, string)>();
        foreach (Match m in KapsamHedefDeseni().Matches(metin))
        {
            if (NesneBul(onbellek, m.Groups["nesne"].Value) is not { } tablo)
                continue;
            string niteleyici = m.Groups["ad"].Success ? m.Groups["ad"].Value : tablo.Ad;
            sonuc.Add((tablo, niteleyici));
        }
        return sonuc;
    }

    /// <summary>
    /// Kapsamdaki tabloların kolonlarını önerir. Kolon adı TEK tabloda ise sade ("Ad"); BİRDEN ÇOK
    /// tabloda ise (JOIN/self-join) her tablo için NİTELENMİŞ önerilir ("a.Id", "b.Id") — kullanıcı
    /// isteği 2026-07-29 (#6): "join'de aynı adlı kolon hangi tablonunki karışıyor". Belirsiz kolonda
    /// sade ad HİÇ verilmez (hangi tablonunki belli değil); tekil kolonda nitelendirmeye gerek yok.
    /// Metin sade "Ad" kalır (kullanıcı "Id" yazınca süzülür); nitelenmiş metin yalnız <c>Ekle</c>'dedir.
    /// </summary>
    private static IReadOnlyList<TamamlamaOnerisi> KapsamKolonlari(string metin, SemaOnbellegi onbellek)
    {
        List<(SemaNesnesi Tablo, string Niteleyici)> tablolar = KapsamTablolar(metin, onbellek);

        // Kolon adı kaç tablo oluşumunda görülüyor → belirsiz mi?
        var sayac = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((SemaNesnesi tablo, _) in tablolar)
            foreach (SemaKolonu k in tablo.Kolonlar)
                sayac[k.Ad] = sayac.GetValueOrDefault(k.Ad) + 1;

        var sonuc = new List<TamamlamaOnerisi>();
        var tekilGorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((SemaNesnesi tablo, string niteleyici) in tablolar)
            foreach (SemaKolonu k in tablo.Kolonlar)
            {
                string tip = $"{k.Tip}{(k.PkMi ? " · PK" : "")}";
                if (sayac[k.Ad] > 1)
                    sonuc.Add(new TamamlamaOnerisi(
                        k.Ad, $"{niteleyici}.{k.Ad} · {tablo.Ad} · {tip}", 3, Ekle: $"{niteleyici}.{k.Ad}"));
                else if (tekilGorulen.Add(k.Ad))
                    sonuc.Add(new TamamlamaOnerisi(k.Ad, $"{tablo.Ad} · {tip}", 3));
            }
        return sonuc;
    }

    /// <summary>
    /// #7 (kullanıcı isteği 2026-07-29): SELECT/kolon bağlamında "⋯ tüm kolonlar" önerisi — seçilince
    /// FROM tablosunun tüm kolonları virgülle açılır ("*" yerine kolon kolon). Kapsamdaki her tablo için
    /// bir öneri; JOIN'de kolonlar niteleyiciyle yazılır ("a.Id, a.Ad"). Öncelik yüksek (3.7) → listenin
    /// üstünde. Kolon adlarıyla süzülmez (Metin "⋯" ile başlar) — bilerek "hepsini seç" kestirmesi.
    /// </summary>
    private static IReadOnlyList<TamamlamaOnerisi> KapsamTumKolonOnerileri(string metin, SemaOnbellegi onbellek)
    {
        List<(SemaNesnesi Tablo, string Niteleyici)> tablolar = KapsamTablolar(metin, onbellek);
        bool nitele = tablolar.Count > 1;   // JOIN → kolonları niteleyiciyle yaz (a.Id, b.Tutar)
        var sonuc = new List<TamamlamaOnerisi>();
        foreach ((SemaNesnesi tablo, string niteleyici) in tablolar)
        {
            if (tablo.Kolonlar.Count == 0)
                continue;
            string liste = string.Join(", ", tablo.Kolonlar.Select(k => nitele ? $"{niteleyici}.{k.Ad}" : k.Ad));
            string onizleme = liste.Length > 40 ? liste[..40] + "…" : liste;
            string metinBasi = nitele ? $"⋯ {niteleyici}.* tüm kolonlar" : "⋯ tüm kolonlar";
            sonuc.Add(new TamamlamaOnerisi(metinBasi, $"{tablo.Ad} → {onizleme}", 3.7, Ekle: liste));
        }
        return sonuc;
    }

    /// <summary>İmleçten önceki EN YAKIN cümle sözcüğünü (tek boşlukla normalize, BÜYÜK) döner; yoksa "".
    /// #8'de "ON" ile "WHERE"i ayırt etmek için (BaglamBul ikisini de Yuklem sayar).</summary>
    private static string EnYakinBaglamKelimesi(string metin, int kelimeBasi)
    {
        Match m = SonBaglamKelimesi().Match(metin[..kelimeBasi]);
        return m.Success ? WhitespaceDeseni().Replace(m.Value, " ").ToUpperInvariant() : "";
    }

    /// <summary>
    /// #8 (2026-07-29): kapsamda 2+ tablo varsa aralarındaki FK'lardan JOIN eşitliğini ("s.MusteriId =
    /// m.Id") hazır öneri üretir (öncelik 4.0 → ON'da ilk sırada, otomatik yazılmaz). Bileşik FK'da
    /// kolonlar AND'le birleşir. Niteleyici = sorgudaki takma ad (yoksa tablo adı). Aynı niteleyiciye
    /// (self-join aynı örnek) FK üretilmez; yinelenen eşitlik metni tekilleşir.
    /// </summary>
    private static IReadOnlyList<TamamlamaOnerisi> FkJoinOnerileri(string metin, SemaOnbellegi onbellek)
    {
        if (onbellek.YabanciAnahtarlar.Count == 0)
            return [];
        List<(SemaNesnesi Tablo, string Niteleyici)> tablolar = KapsamTablolar(metin, onbellek);
        if (tablolar.Count < 2)
            return [];

        var sonuc = new List<TamamlamaOnerisi>();
        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (YabanciAnahtar fk in onbellek.YabanciAnahtarlar)
        {
            if (fk.KaynakKolonlar.Count == 0 || fk.KaynakKolonlar.Count != fk.HedefKolonlar.Count)
                continue;
            foreach ((SemaNesnesi kt, string kn) in tablolar)
            {
                if (!TabloEslesir(kt, fk.KaynakSema, fk.KaynakTablo))
                    continue;
                foreach ((SemaNesnesi ht, string hn) in tablolar)
                {
                    if (!TabloEslesir(ht, fk.HedefSema, fk.HedefTablo)
                        || kn.Equals(hn, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string esitlik = string.Join(" AND ", fk.KaynakKolonlar.Zip(
                        fk.HedefKolonlar, (k, h) => $"{kn}.{k} = {hn}.{h}"));
                    if (gorulen.Add(esitlik))
                        // Öncelik 4.5: snippet dahil her şeyin üstünde → ON'da KESİN ilk öneri (kullanıcı isteği).
                        sonuc.Add(new TamamlamaOnerisi(esitlik, $"FK: {kt.Ad} → {ht.Ad}", 4.5, Ekle: esitlik));
                }
            }
        }
        return sonuc;
    }

    /// <summary>Kapsam tablosu bir FK ucuyla (şema+ad) eşleşir mi? FK şeması boşsa yalnız ad bakılır.</summary>
    private static bool TabloEslesir(SemaNesnesi n, string sema, string tablo)
        => n.Ad.Equals(tablo, StringComparison.OrdinalIgnoreCase)
           && (sema.Length == 0 || n.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sorgudaki FROM/JOIN takma adlarını (ad → hedef nesne) öneri olarak döner. Kullanıcının açıkça
    /// tanımladığı adlardır; yalnız sorgu METNİNE bakılır, şema önbelleği GEREKMEZ (bu yüzden şema
    /// yüklenmeden de ON/WHERE'de "k" önerilebilir). Öncelik 3.5: kolonların (3) hemen üstünde çıkar,
    /// çünkü ON/WHERE'de genelde önce "alias." yazılır. Aynı ad birden çok kez tanımlıysa tekilleşir.
    /// </summary>
    private static IReadOnlyList<TamamlamaOnerisi> KapsamTakmaAdlari(string metin)
    {
        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sonuc = new List<TamamlamaOnerisi>();
        foreach (Match m in TakmaAdDeseni().Matches(metin))
        {
            string ad = m.Groups["ad"].Value;
            if (gorulen.Add(ad))
                sonuc.Add(new TamamlamaOnerisi(ad, $"takma ad → {m.Groups["nesne"].Value}", 3.5));
        }
        return sonuc;
    }

    /// <summary>İmleç bir yorumun (-- ya da /* */) ya da string literalinin ('…') içinde mi?</summary>
    private static bool YorumYaDaMetinIcinde(string metin, int ofset)
    {
        int n = Math.Min(ofset, metin.Length);
        bool str = false;
        int blok = 0; // T-SQL blok yorumu İÇ İÇE olabilir (inceleme 2026-07-30) — derinlik sayılır
        for (int i = 0; i < n; i++)
        {
            char c = metin[i];
            char nx = i + 1 < n ? metin[i + 1] : '\0';
            if (blok > 0)
            {
                if (c == '*' && nx == '/') { blok--; i++; }
                else if (c == '/' && nx == '*') { blok++; i++; }
                continue;
            }
            if (str)
            {
                if (c == '\'') { if (nx == '\'') i++; else str = false; } // '' kaçış
                continue;
            }
            if (c == '-' && nx == '-')
            {
                int nl = metin.IndexOf('\n', i);
                if (nl < 0 || nl >= n) return true; // imleç bu satırdaki --'den sonra
                i = nl;
            }
            else if (c == '/' && nx == '*') { blok = 1; i++; }
            else if (c == '[')
            {
                // Köşeli tanımlayıcı içindeki ' string sanılmasın (inceleme 2026-07-30: [O'Brien]
                // belgede sonraki TÜM önerileri kapatıyordu) — ]'a kadar atla.
                int kapa = metin.IndexOf(']', i + 1);
                if (kapa < 0 || kapa >= n) return false; // açık kalan köşeli — yorum/dize değil
                i = kapa;
            }
            else if (c == '\'') { str = true; }
        }
        return str || blok > 0;
    }

    /// <summary>
    /// "alias." → o tablonun kolonları; "şema." → o şemanın nesneleri; "Tablo." → kolonlar.
    /// <b>ŞEMA, doğrudan TABLO'dan ÖNCE denenir</b> (kullanıcı isteği 2026-07-21: "ortak" hem şema
    /// hem tablo olabilir; <c>ortak.</c> yazınca ŞEMADAKİ tablolar gelmeli, tablo kolonları değil).
    /// Takma ad (FROM'da açıkça tanımlı) her ikisinden önce gelir.
    /// </summary>
    private static IReadOnlyList<TamamlamaOnerisi> UyeOnerileri(
        string metin, string niteleyici, SemaOnbellegi onbellek)
    {
        // 1) Takma ad: "FROM dbo.Musteri m" → m.  (Açık niyet — önce bu.)
        if (TakmaAdCoz(metin, niteleyici) is { } nesneAdi && NesneBul(onbellek, nesneAdi) is { } takmaHedef)
            return Kolonlar(takmaHedef);

        // 2) Şema adı: "ortak." → o şemanın nesneleri (tablo/view). Tablodan ÖNCE — şema.tablo kuralı.
        List<TamamlamaOnerisi> semadakiler = [.. onbellek.Nesneler
            .Where(n => n.Sema.Equals(niteleyici, StringComparison.OrdinalIgnoreCase))
            .Select(n => new TamamlamaOnerisi(n.Ad, n.Tur.ToString(), 2))];
        if (semadakiler.Count > 0)
            return semadakiler;

        // 3) Doğrudan tablo/view adı: "Musteri." → kolonları.
        return NesneBul(onbellek, niteleyici) is { } hedef ? Kolonlar(hedef) : [];
    }

    private static IReadOnlyList<TamamlamaOnerisi> Kolonlar(SemaNesnesi nesne)
        => [.. nesne.Kolonlar.Select(k => new TamamlamaOnerisi(k.Ad, $"{k.Tip}{(k.PkMi ? " · PK" : "")}", 3))];

    /// <summary>Metindeki "FROM <nesne> [AS] <takmaAd>" eşleşmelerinde takma adı nesne adına çözer.</summary>
    private static string? TakmaAdCoz(string metin, string takmaAd)
    {
        foreach (Match m in TakmaAdDeseni().Matches(metin))
        {
            if (m.Groups["ad"].Value.Equals(takmaAd, StringComparison.OrdinalIgnoreCase))
                return m.Groups["nesne"].Value;
        }
        return null;
    }

    private static SemaNesnesi? NesneBul(SemaOnbellegi onbellek, string ad)
    {
        // Şema niteleyicisi ARTIK dikkate alınır (inceleme 2026-07-30): "arsiv.Siparis s" yazana
        // eskiden ada göre İLK bulunan (ör. dbo.Siparis) tablonun kolonları öneriliyordu.
        string[] parcalar = ad.Split('.');
        string sade = parcalar[^1].Trim('[', ']');
        string? sema = parcalar.Length >= 2 ? parcalar[^2].Trim('[', ']') : null;
        return onbellek.Nesneler.FirstOrDefault(n =>
            n.Ad.Equals(sade, StringComparison.OrdinalIgnoreCase)
            && (sema is null || n.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase))
            && n.Tur is SemaNesneTuru.Tablo or SemaNesneTuru.View);
    }

    private static int KelimeBasiBul(string metin, int ofset)
    {
        int i = Math.Min(ofset, metin.Length);
        while (i > 0 && (char.IsLetterOrDigit(metin[i - 1]) || metin[i - 1] is '_' or '@' or '#'))
            i--;
        return i;
    }

    /// <summary>Noktanın solundaki niteleyiciyi okur: "m." → m, "[dbo]." → dbo, "a.b." → b.</summary>
    private static string NiteleyiciOku(string metin, int noktaOfseti)
    {
        int son = noktaOfseti;
        int bas = son;
        while (bas > 0 && (char.IsLetterOrDigit(metin[bas - 1]) || metin[bas - 1] is '_' or '[' or ']' or '#'))
            bas--;
        return metin[bas..son].Trim('[', ']');
    }

    // AS opsiyonel; nesne [köşeli]/şemalı/#temp olabilir; takma ad düz sözcük.
    // ON/WHERE gibi anahtar sözcükleri takma ad sanmamak için negatif liste.
    [GeneratedRegex(
        @"\b(?:FROM|JOIN|UPDATE|INTO|APPLY)\s+(?<nesne>[\w\[\]\.#]+)(?:\s+AS)?\s+(?!(?:ON|WHERE|SET|AS|JOIN|INNER|LEFT|RIGHT|FULL|CROSS|GROUP|ORDER|HAVING|UNION|SELECT|WITH|VALUES|OUTPUT)\b)(?<ad>\w+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TakmaAdDeseni();

    // İmleçten önceki EN YAKIN cümle anahtar sözcüğü (RightToLeft = sondan ilk eşleşme).
    [GeneratedRegex(
        @"\b(?:FROM|INNER\s+JOIN|LEFT\s+JOIN|RIGHT\s+JOIN|FULL\s+JOIN|CROSS\s+JOIN|JOIN|INTO|APPLY|UPDATE|WHERE|ON|AND|OR|HAVING|SET|SELECT|GROUP\s+BY|ORDER\s+BY)\b",
        RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
    private static partial Regex SonBaglamKelimesi();

    // Kapsamdaki her FROM/JOIN/INTO/UPDATE/APPLY hedefi + (varsa) takma adı. Her oluşum ayrı eşleşir
    // (self-join'de a ve b ayrı). Alias negatif listesi TakmaAdDeseni ile aynı mantık: bir sonraki
    // anahtar sözcüğü (ON/WHERE/JOIN/AND/OR…) takma ad sanma. AS opsiyonel; alias'ın kendisi opsiyonel.
    [GeneratedRegex(
        @"\b(?:FROM|JOIN|INTO|UPDATE|APPLY)\s+(?<nesne>[\w\[\]\.#]+)(?:\s+(?:AS\s+)?(?!(?:ON|WHERE|SET|JOIN|INNER|LEFT|RIGHT|FULL|CROSS|GROUP|ORDER|HAVING|UNION|SELECT|WITH|VALUES|OUTPUT|AND|OR)\b)(?<ad>\w+))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex KapsamHedefDeseni();

    // #3 oto-tetik: cümle sözcüğü (ya da virgül) + boşluk(lar) + imleç ($). Çok-sözcüklü JOIN'de son
    // token JOIN; GROUP/ORDER BY'da son token BY. '=' / operatör sonrası tetiklemez (mid-ifade).
    // RightToLeft ŞART (stres bulgusu 2026-07-30): desen $-demirli ama soldan-sağa motor 2MB metnin
    // TAMAMINI her boşlukta tarıyordu (~60 ms/tuş). RTL motor SONDAN dener → sabit maliyet (~0 ms).
    [GeneratedRegex(
        @"(?:\b(?:FROM|JOIN|ON|WHERE|AND|OR|HAVING|SET|INTO|APPLY|SELECT|UPDATE|BY)|,)\s+$",
        RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
    private static partial Regex OtoTetikDeseni();

    // @parametre/@değişken adları (2026-07-31): @@ sistem değişkenleri hariç (@'lı tek geçiş).
    [GeneratedRegex(@"(?<!@)@[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex ParametreDeseni();

    // #9: EXEC/EXECUTE <şema.sp> — imleçten önceki SON çağrı (RightToLeft). Parantezsiz SP çağrısı.
    [GeneratedRegex(@"\bEXEC(?:UTE)?\s+(?<ad>[\w\[\]\.#]+)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
    private static partial Regex ExecDeseni();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceDeseni();
}
