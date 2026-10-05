using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Bir aranan ad için kolon adayı (aynı ad birden çok tabloda olabilir — kullanıcı seçer).</summary>
public sealed record SpKolonAdayi(SemaNesnesi Tablo, SemaKolonu Kolon)
{
    public string Gosterim => $"{Tablo.TamAd}.{Kolon.Ad} ({Kolon.Tip})";
}

/// <summary>FK grafında bulunan bir JOIN adımı: Yeni tablo, mevcut tabloya bu FK ile bağlanır.</summary>
public sealed record SpJoinAdimi(SemaNesnesi YeniTablo, YabanciAnahtar Fk, bool YeniTabloKaynakMi)
{
    /// <summary>Bağ, tanımlı FK'dan değil AD KURALINDAN (XId → X.Id) çıkarıldıysa true (v19-S16) —
    /// SQL'de varsayılan LEFT JOIN + uyarı yorumu olarak yazılır.</summary>
    public bool AdKuralindan => Fk.Ad == SpSihirbazi.AdKuraliEtiketi;

    /// <summary>Kullanıcının seçtiği JOIN türü (v19-S19: "INNER JOIN"/"LEFT JOIN"). Null → otomatik
    /// (ad kuralından ise LEFT, gerçek FK ise INNER). UI adım 2'de her bağın türünü değiştirebilir.</summary>
    public string? TurZorla { get; init; }

    /// <summary>Yazılacak JOIN anahtar sözcüğü (zorlama yoksa otomatik).</summary>
    public string JoinTuru => TurZorla ?? (AdKuralindan ? "LEFT JOIN" : "INNER JOIN");
}

/// <summary>SP girişi (v19-S16): parametre adı + eşleşen kolon — eşleşme YOKSA null
/// (parametre yine konur, koşulu kullanıcı elle bağlar).</summary>
public sealed record SpGirisi(string Param, SpKolonAdayi? Kolon);

/// <summary>SP çıkışı (v19-S16): istenen ad + eşleşen kolon — eşleşme YOKSA null
/// (kullanıcı isteği 2026-08-04: "karşılığı yoksa boş olarak yine koysun" → NULL AS kolon).
/// v19-S19: <see cref="Toplama"/> toplama fonksiyonu (COUNT/SUM/MAX/MIN/AVG) — doluysa SELECT'te
/// <c>SUM(...)</c> olur ve toplamasız çıkışlar GROUP BY'a girer.</summary>
public sealed record SpCikisi(string Ad, SpKolonAdayi? Kolon, string? Toplama = null);

/// <summary>
/// SP'ye eklenecek WHERE koşulu (v19-S18, kullanıcı isteği 2026-08-04: "gittiği tabloları seçsin
/// kolonu seçsin şartları girsin hepsi where arkasına eklensin"). Değer, kolon tipine göre
/// tırnaklanır (metin türleri) ya da ham yazılır; IS NULL / IS NOT NULL değersizdir.
/// </summary>
/// <summary><see cref="Baglac"/> (v19-S19): koşulun bir öncekine AND mi OR ile mi bağlanacağı
/// ("AND"/"OR"; ilk koşulda yok sayılır).</summary>
public sealed record SpEkKosul(SemaNesnesi Tablo, SemaKolonu Kolon, string Operator, string? Deger, string Baglac = "AND");

/// <summary>
/// SP başında IF ile parametre kontrolü (v19-S18, kullanıcı isteği 2026-08-04: "girdi parametresi
/// 0 olamaz bunu da kontrol etsin if ile"). <see cref="Operator"/>+<see cref="Deger"/> GEÇERSİZ
/// koşuludur — doğruysa RAISERROR + RETURN. Mesaj boşsa otomatik üretilir.
/// </summary>
public sealed record SpKural(string Param, string Operator, string? Deger, string? Mesaj);

/// <summary>ORDER BY öğesi (v19-S19): sıralanacak kolon + yön (azalan mı).</summary>
public sealed record SpSiralama(SemaNesnesi Tablo, SemaKolonu Kolon, bool Azalan);

/// <summary>
/// SP sorgu-şekli seçenekleri (v19-S19): CREATE OR ALTER, TOP (n), DISTINCT, ORDER BY listesi.
/// Hepsi opsiyonel — verilmezse eski davranış (CREATE PROCEDURE, sınırsız, sırasız).
/// </summary>
public sealed record SpSecenekleri(
    bool CreateOrAlter = false, int? Top = null, bool Distinct = false,
    IReadOnlyList<SpSiralama>? Siralamalar = null);

/// <summary>
/// 🪄 SP Sihirbazı çekirdeği (kullanıcı fikri 2026-07-31: "girişlerim/çıkışlarım bunlar — şemayı
/// analiz et, SP'yi hazırla"). SAF ve testli: kolon eşleştirme (tam ad önce, sonra kısmi) +
/// FK grafında yol bulma (BFS — kökten diğer tablolara en kısa bağ) + tek SELECT'li CREATE
/// PROCEDURE üretimi (girişler WHERE'de eşitlik). Üretilen metin DAİMA taslaktır — editörde açılır,
/// çalıştırma kullanıcıdadır. v1 MSSQL (T-SQL) hedefler.
/// </summary>
public static class SpSihirbazi
{
    /// <summary>Ad kuralından çıkarılan sanal FK'ların işareti (gerçek kısıt adı değildir).</summary>
    public const string AdKuraliEtiketi = "≈ad-kuralı";

    /// <summary>
    /// v19-S16 (kullanıcı isteği 2026-08-04: "x tablomda Y Id var — Y'ye gideceğini kendisinin
    /// bilmesi lazım"): gerçek veritabanlarında FK çoğu zaman TANIMLI DEĞİLDİR; bu yüzden ad
    /// kuralından SANAL ilişkiler çıkarılır — <c>XId</c> biçimli her kolon, adı <c>X</c> olan
    /// tablonun PK'sına bağlanır (tanımlı FK'sı zaten olan kolon atlanır; kendine bağ atlanır).
    /// Dönenler <see cref="AdKuraliEtiketi"/> ile işaretlidir → SQL'de LEFT JOIN + uyarı yorumu.
    /// </summary>
    public static IReadOnlyList<YabanciAnahtar> AdKuralindanIliskiler(SemaOnbellegi onbellek)
    {
        var fkliKolonlar = new HashSet<string>(
            onbellek.YabanciAnahtarlar.SelectMany(f =>
                f.KaynakKolonlar.Select(k => $"{f.KaynakSema}.{f.KaynakTablo}.{k}".ToUpperInvariant())),
            StringComparer.Ordinal);
        List<SemaNesnesi> tablolar = [.. onbellek.Nesneler.Where(n => n.Tur == SemaNesneTuru.Tablo)];
        ILookup<string, SemaNesnesi> adaGore = tablolar.ToLookup(t => t.Ad, StringComparer.OrdinalIgnoreCase);

        var sonuc = new List<YabanciAnahtar>();
        foreach (SemaNesnesi t in tablolar)
            foreach (SemaKolonu k in t.Kolonlar)
            {
                if (k.PkMi || k.Ad.Length <= 2 || !k.Ad.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (fkliKolonlar.Contains($"{t.Sema}.{t.Ad}.{k.Ad}".ToUpperInvariant()))
                    continue; // gerçek FK zaten var — çıkarıma gerek yok

                foreach (SemaNesnesi hedef in adaGore[k.Ad[..^2]])
                {
                    if (ReferenceEquals(hedef, t))
                        continue; // kendine bağ (UstKisiId gibi) — takma ad çakışmasın diye üretilmez
                    SemaKolonu? pk = hedef.Kolonlar.FirstOrDefault(x => x.PkMi)
                        ?? hedef.Kolonlar.FirstOrDefault(x => x.Ad.Equals("Id", StringComparison.OrdinalIgnoreCase))
                        ?? hedef.Kolonlar.FirstOrDefault(x => x.Ad.Equals(k.Ad, StringComparison.OrdinalIgnoreCase));
                    if (pk is not null)
                        sonuc.Add(new YabanciAnahtar(t.Sema, t.Ad, [k.Ad], hedef.Sema, hedef.Ad, [pk.Ad], AdKuraliEtiketi));
                }
            }
        return sonuc;
    }

    /// <summary>
    /// Aranan adı tüm tablo kolonlarında bulur: TAM eşleşmeler önce, yoksa KISMİ (içeren).
    /// v19-S21 (kullanıcı isteği 2026-08-04: "sadece Id yazınca çok tablo geliyor"): ad NİTELENMİŞSE
    /// (<c>Tablo.Kolon</c> ya da <c>Sema.Tablo.Kolon</c>) yalnız o tablonun o kolonu döner — belirsizlik
    /// biter. Nitelenmiş ama bulunamazsa boş döner (UI "eşleşme yok" gösterir; uydurma tablo seçmez).
    /// </summary>
    public static IReadOnlyList<SpKolonAdayi> KolonAra(SemaOnbellegi onbellek, string ad)
    {
        string aranan = ad.TrimStart('@').Trim();
        if (aranan.Length == 0)
            return [];

        List<SemaNesnesi> tablolar = [.. onbellek.Nesneler.Where(n => n.Tur == SemaNesneTuru.Tablo)];

        // Nitelenmiş ad: son parça kolon, ondan önceki tablo, (varsa) daha öncesi şema.
        string[] parcalar = aranan.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parcalar.Length >= 2)
        {
            string kolonAd = parcalar[^1], tabloAd = parcalar[^2];
            string? semaAd = parcalar.Length >= 3 ? parcalar[^3] : null;
            var nitelenmis = new List<SpKolonAdayi>();
            foreach (SemaNesnesi t in tablolar)
            {
                if (!t.Ad.Equals(tabloAd, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (semaAd is not null && !t.Sema.Equals(semaAd, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (SemaKolonu k in t.Kolonlar)
                    if (k.Ad.Equals(kolonAd, StringComparison.OrdinalIgnoreCase))
                        nitelenmis.Add(new SpKolonAdayi(t, k));
            }
            return nitelenmis;
        }

        var tam = new List<SpKolonAdayi>();
        var kismi = new List<SpKolonAdayi>();
        foreach (SemaNesnesi t in tablolar)
            foreach (SemaKolonu k in t.Kolonlar)
            {
                if (k.Ad.Equals(aranan, StringComparison.OrdinalIgnoreCase))
                    tam.Add(new SpKolonAdayi(t, k));
                else if (k.Ad.Contains(aranan, StringComparison.OrdinalIgnoreCase))
                    kismi.Add(new SpKolonAdayi(t, k));
            }

        return tam.Count > 0 ? tam : kismi;
    }

    /// <summary>
    /// JOIN adım sırası geçerli mi (v19-S21 — kullanıcı yolu elle sıralarken): her adımın bağlandığı
    /// "mevcut" tablo, kökten ya da KENDİNDEN ÖNCEKİ bir adımın tablosundan gelmelidir. Bozuk sıra
    /// (bir join, henüz kapsamda olmayan tabloya bağlanıyor) SQL'i geçersiz kılar — UI taşımayı reddeder.
    /// </summary>
    public static bool JoinSirasiGecerli(SemaNesnesi kok, IReadOnlyList<SpJoinAdimi> joinler)
    {
        var kapsam = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { kok.TamAd };
        foreach (SpJoinAdimi j in joinler)
        {
            string mevcut = j.YeniTabloKaynakMi
                ? $"{j.Fk.HedefSema}.{j.Fk.HedefTablo}"
                : $"{j.Fk.KaynakSema}.{j.Fk.KaynakTablo}";
            if (!kapsam.Contains(mevcut))
                return false;
            kapsam.Add(j.YeniTablo.TamAd);
        }
        return true;
    }

    /// <summary>
    /// Seçili tabloları FK grafında bağlar: kökten (ilk tablo) BFS ile her hedef tabloya en kısa yol;
    /// yol kenarlarının birleşimi JOIN adımlarıdır. Bağlanamayan tablo adı <paramref name="baglanamayan"/>'a
    /// yazılır (kullanıcı Görsel Sorgu'da elle bağlayabilir).
    /// </summary>
    public static IReadOnlyList<SpJoinAdimi> YolBul(
        IReadOnlyList<SemaNesnesi> tablolar, IReadOnlyList<YabanciAnahtar> fkler, List<string> baglanamayan)
    {
        var adimlar = new List<SpJoinAdimi>();
        if (tablolar.Count <= 1)
            return adimlar;

        static string Anah(string sema, string tablo) => $"{sema}.{tablo}".ToUpperInvariant();
        var dugumler = tablolar.ToDictionary(t => Anah(t.Sema, t.Ad), t => t, StringComparer.Ordinal);

        // Komşuluk: FK kenarları çift yönlü gezilir (kaynak↔hedef).
        var komsular = new Dictionary<string, List<(string Komsu, YabanciAnahtar Fk, bool KomsuKaynakMi)>>(StringComparer.Ordinal);
        void Ekle(string a, string b, YabanciAnahtar fk, bool bKaynakMi)
        {
            if (!komsular.TryGetValue(a, out var liste))
                komsular[a] = liste = [];
            liste.Add((b, fk, bKaynakMi));
        }
        foreach (YabanciAnahtar fk in fkler)
        {
            string k = Anah(fk.KaynakSema, fk.KaynakTablo), h = Anah(fk.HedefSema, fk.HedefTablo);
            Ekle(k, h, fk, false); // kaynaktan hedefe gidersek yeni tablo (hedef) FK'nın HEDEF ucudur
            Ekle(h, k, fk, true);  // hedeften kaynağa gidersek yeni tablo FK'nın KAYNAK ucudur
        }

        var dahil = new HashSet<string>(StringComparer.Ordinal) { Anah(tablolar[0].Sema, tablolar[0].Ad) };
        foreach (SemaNesnesi hedef in tablolar.Skip(1))
        {
            string hedefAnah = Anah(hedef.Sema, hedef.Ad);
            if (dahil.Contains(hedefAnah))
                continue;

            // BFS: dahil kümesinden hedefe en kısa yol (ara tablolar şemadan bilinmese de FK uçlarından tanınır).
            var geldi = new Dictionary<string, (string Onceki, YabanciAnahtar Fk, bool YeniKaynakMi)>(StringComparer.Ordinal);
            var kuyruk = new Queue<string>(dahil);
            bool bulundu = false;
            while (kuyruk.Count > 0 && !bulundu)
            {
                string su = kuyruk.Dequeue();
                if (!komsular.TryGetValue(su, out var liste))
                    continue;
                foreach ((string komsu, YabanciAnahtar fk, bool komsuKaynakMi) in liste)
                {
                    if (dahil.Contains(komsu) || geldi.ContainsKey(komsu))
                        continue;
                    geldi[komsu] = (su, fk, komsuKaynakMi);
                    if (komsu == hedefAnah) { bulundu = true; break; }
                    kuyruk.Enqueue(komsu);
                }
            }

            if (!bulundu)
            {
                baglanamayan.Add(hedef.TamAd);
                continue;
            }

            // Yolu geri sar; adımları köke yakından hedefe doğru sırala ve dahil kümesine kat.
            var yol = new List<(string Dugum, YabanciAnahtar Fk, bool YeniKaynakMi)>();
            for (string d = hedefAnah; !dahil.Contains(d); d = geldi[d].Onceki)
                yol.Add((d, geldi[d].Fk, geldi[d].YeniKaynakMi));
            yol.Reverse();
            foreach ((string dugum, YabanciAnahtar fk, bool yeniKaynakMi) in yol)
            {
                SemaNesnesi yeni = dugumler.TryGetValue(dugum, out SemaNesnesi? bilinen)
                    ? bilinen
                    : AraTablo(dugum, fk, yeniKaynakMi); // yolda geçen ARA tablo (istenmese de JOIN'e girer)
                adimlar.Add(new SpJoinAdimi(yeni, fk, yeniKaynakMi));
                dahil.Add(dugum);
            }
        }

        return adimlar;
    }

    /// <summary>Yolda geçen ama seçilmeyen ara tablo — FK ucundan kimliklendirilir (kolonsuz temsil).</summary>
    private static SemaNesnesi AraTablo(string anahtar, YabanciAnahtar fk, bool kaynakMi)
        => kaynakMi
            ? new SemaNesnesi("", fk.KaynakSema, fk.KaynakTablo, SemaNesneTuru.Tablo, [], [])
            : new SemaNesnesi("", fk.HedefSema, fk.HedefTablo, SemaNesneTuru.Tablo, [], []);

    /// <summary>
    /// Zengin CREATE PROCEDURE taslağı (v19-S16, kullanıcı isteği 2026-08-04 — "basit değil çok
    /// satırlı"): başta analiz özeti, girişler parametre (eşleşen tip / eşleşmeyen için TODO),
    /// çıkışlar SELECT'te (eşleşmeyen → <c>NULL AS [ad]</c>, kullanıcı: "karşılığı yoksa boş olarak
    /// yine koysun"), JOIN'ler FK yolundan — ad-kuralından çıkarılanlar <b>LEFT JOIN</b> + uyarı,
    /// gerçek FK'lar INNER JOIN. TASLAKTIR — editörde açılır, çalıştırma kullanıcıdadır.
    /// </summary>
    public static string SpUret(
        string spAdi, IReadOnlyList<SpGirisi> girisler,
        IReadOnlyList<SpCikisi> cikislar, IReadOnlyList<SpJoinAdimi> joinler,
        IReadOnlyList<SpEkKosul>? ekKosullar = null, IReadOnlyList<SpKural>? kurallar = null,
        SpSecenekleri? secenekler = null)
    {
        ekKosullar ??= [];
        kurallar ??= [];
        secenekler ??= new SpSecenekleri();
        IReadOnlyList<SpSiralama> siralamalar = secenekler.Siralamalar ?? [];
        static string K(string a) => $"[{a.Replace("]", "]]")}]";
        static string P(string p) => p.TrimStart('@').Trim();

        // Takma adlar: kök + join sırasına göre t1..tN (yalnız EŞLEŞEN kolonların tablosu FROM'a girer).
        var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string AliasAl(SemaNesnesi t)
        {
            string anah = $"{t.Sema}.{t.Ad}";
            if (!alias.TryGetValue(anah, out string? a))
                alias[anah] = a = $"t{alias.Count + 1}";
            return a;
        }

        SemaNesnesi? kok = girisler.FirstOrDefault(g => g.Kolon is not null)?.Kolon?.Tablo
            ?? cikislar.FirstOrDefault(c => c.Kolon is not null)?.Kolon?.Tablo;
        var sb = new System.Text.StringBuilder();

        // ── Başlık + analiz özeti (çok satırlı taslağın "büyük/güzel" tarafı) ──
        sb.AppendLine("-- ============================================================");
        sb.AppendLine($"-- 🪄 SP Sihirbazı taslağı — {DateTime.Now:dd.MM.yyyy}");
        sb.AppendLine("-- Şema analizinden üretildi. GÖZDEN GEÇİRİN: JOIN yolu ve tip eşlemeleri");
        sb.AppendLine("-- şemadan çıkarıldı; iş kuralları ve ek koşullar size aittir.");
        sb.AppendLine("-- ------------------------------------------------------------");
        sb.AppendLine("-- Girişler:");
        foreach (SpGirisi g in girisler)
            sb.AppendLine(g.Kolon is { } gk
                ? $"--   • @{P(g.Param)} → {gk.Tablo.TamAd}.{gk.Kolon.Ad} ({gk.Kolon.Tip})"
                : $"--   • @{P(g.Param)} → ⚠ eşleşmedi (parametre kondu, koşulu elle bağlayın)");
        sb.AppendLine("-- Çıkışlar:");
        foreach (SpCikisi c in cikislar)
            sb.AppendLine(c.Kolon is { } ck
                ? $"--   • {c.Ad} → {ck.Tablo.TamAd}.{ck.Kolon.Ad} ({ck.Kolon.Tip})"
                : $"--   • {c.Ad} → ⚠ hiçbir tabloda yok (NULL kolon olarak eklendi)");
        if (joinler.Any(j => j.AdKuralindan))
            sb.AppendLine("-- ⚠ Bazı JOIN'ler AD KURALINDAN (XId→X) çıkarıldı — LEFT JOIN + gözden geçir.");
        sb.AppendLine("-- ============================================================");

        // ── CREATE [OR ALTER] PROCEDURE başlığı + parametreler (v19-S19) ──
        sb.AppendLine($"{(secenekler.CreateOrAlter ? "CREATE OR ALTER PROCEDURE" : "CREATE PROCEDURE")} {K("dbo")}.{K(spAdi)}");
        if (girisler.Count > 0)
            sb.AppendLine(string.Join(",\n", girisler.Select(g => g.Kolon is { } gk
                ? $"    @{P(g.Param)} {gk.Kolon.Tip}"
                : $"    @{P(g.Param)} nvarchar(200)   -- ⚠ eşleşmedi: tipi/koşulu elle ayarlayın")));
        sb.AppendLine("AS");
        sb.AppendLine("BEGIN");
        sb.AppendLine("    SET NOCOUNT ON;");
        sb.AppendLine();

        // ── Parametre kuralları (v19-S18): IF <geçersiz koşul> → RAISERROR + RETURN ──
        if (kurallar.Count > 0)
        {
            sb.AppendLine("    -- Parametre kuralları (geçersiz girişte hata verip çıkar):");
            foreach (SpKural kural in kurallar)
            {
                string kosul = kural.Operator is "IS NULL" or "IS NOT NULL"
                    ? $"@{P(kural.Param)} {kural.Operator}"
                    : $"@{P(kural.Param)} {kural.Operator} {(kural.Deger ?? "").Trim()}";
                string mesaj = string.IsNullOrWhiteSpace(kural.Mesaj)
                    ? $"{P(kural.Param)} degeri gecersiz."
                    : kural.Mesaj.Trim();
                sb.AppendLine($"    IF {kosul}");
                sb.AppendLine("    BEGIN");
                sb.AppendLine($"        RAISERROR(N'{mesaj.Replace("'", "''")}', 16, 1);");
                sb.AppendLine("        RETURN;");
                sb.AppendLine("    END");
            }
            sb.AppendLine();
        }

        if (kok is null)
        {
            // Hiçbir kolon eşleşmedi — iskelet + kullanıcı doldurur (crash yerine kullanışlı taslak).
            sb.AppendLine("    -- ⚠ Hiçbir çıkış/giriş kolonu şemada eşleşmedi.");
            sb.AppendLine("    -- FROM ve SELECT'i elle doldurun; aşağıdaki NULL kolonlar yer tutucudur.");
            sb.AppendLine("    SELECT");
            sb.AppendLine(string.Join(",\n", cikislar.Select(c => $"        NULL AS {K(c.Ad)}")));
            sb.AppendLine("    -- FROM <tablo> t1");
            sb.AppendLine("END");
            return sb.ToString();
        }

        AliasAl(kok);
        // Join tablolarının alias'ları FROM/JOIN yazımından ÖNCE atanmalı (SELECT onları kullanır).
        foreach (SpJoinAdimi j in joinler)
            AliasAl(j.YeniTablo);
        string AliasBul(SemaNesnesi t) => alias.TryGetValue($"{t.Sema}.{t.Ad}", out string? a) ? a : AliasAl(t);

        // ── SELECT [DISTINCT] [TOP (n)]: eşleşen → alias.[kolon]/TOPLAMA(...); eşleşmeyen → NULL AS (v19-S19) ──
        bool toplamaVar = cikislar.Any(c => c.Kolon is not null && !string.IsNullOrWhiteSpace(c.Toplama));
        string selectBas = "    SELECT"
            + (secenekler.Distinct ? " DISTINCT" : "")
            + (secenekler.Top is { } top ? $" TOP ({top})" : "");
        sb.AppendLine(selectBas);
        sb.AppendLine(string.Join(",\n", cikislar.Select(c =>
        {
            if (c.Kolon is not { } ck)
                return $"        NULL AS {K(c.Ad)}   -- ⚠ eşleşen kolon yok";
            string kolon = $"{AliasBul(ck.Tablo)}.{K(ck.Kolon.Ad)}";
            return string.IsNullOrWhiteSpace(c.Toplama)
                ? $"        {kolon} AS {K(c.Ad)}"
                : $"        {c.Toplama.Trim().ToUpperInvariant()}({kolon}) AS {K(c.Ad)}";
        })));

        // ── FROM + JOIN'ler (tür kullanıcı seçimine göre; yoksa otomatik) ──
        sb.AppendLine($"    FROM {K(kok.Sema)}.{K(kok.Ad)} {AliasBul(kok)}");
        foreach (SpJoinAdimi j in joinler)
        {
            string yeniA = AliasBul(j.YeniTablo);
            // FK uçları: yeni tablo kaynaksa yeni.KaynakKolon = mevcut.HedefKolon; değilse tersi.
            string mevcutA = j.YeniTabloKaynakMi
                ? alias[$"{j.Fk.HedefSema}.{j.Fk.HedefTablo}"]
                : alias[$"{j.Fk.KaynakSema}.{j.Fk.KaynakTablo}"];
            IEnumerable<string> kosullar = j.Fk.KaynakKolonlar.Zip(j.Fk.HedefKolonlar, (kk, hk) =>
                j.YeniTabloKaynakMi
                    ? $"{yeniA}.{K(kk)} = {mevcutA}.{K(hk)}"
                    : $"{yeniA}.{K(hk)} = {mevcutA}.{K(kk)}");
            string not = j.AdKuralindan && j.TurZorla is null ? "   -- ⚠ ad kuralından (XId→X), doğrulayın" : "";
            sb.AppendLine($"    {j.JoinTuru} {K(j.YeniTablo.Sema)}.{K(j.YeniTablo.Ad)} {yeniA} ON {string.Join(" AND ", kosullar)}{not}");
        }

        // ── WHERE: eşleşen girişler eşitlik + kullanıcı EK KOŞULLARI (baglaç AND/OR, v19-S19) ──
        var eslesen = girisler.Where(g => g.Kolon is not null).ToList();
        var eslesmeyen = girisler.Where(g => g.Kolon is null).ToList();
        var whereParcalari = new List<(string Baglac, string Sql)>();
        foreach (SpGirisi g in eslesen)
            whereParcalari.Add(("AND", $"{AliasBul(g.Kolon!.Tablo)}.{K(g.Kolon!.Kolon.Ad)} = @{P(g.Param)}"));
        foreach (SpEkKosul ek in ekKosullar)
        {
            // Koşulun tablosu FROM/JOIN'de değilse (matched değil) → yorumla, sessizce düşürme.
            if (!alias.TryGetValue($"{ek.Tablo.Sema}.{ek.Tablo.Ad}", out string? ekA))
            {
                sb.AppendLine($"    -- ⚠ {ek.Tablo.TamAd}.{ek.Kolon.Ad}: bu tablo sorguda yok, koşul atlandı");
                continue;
            }
            whereParcalari.Add((ek.Baglac is "OR" ? "OR" : "AND", EkKosulSql(ekA, ek)));
        }
        if (whereParcalari.Count > 0)
        {
            sb.AppendLine($"    WHERE {whereParcalari[0].Sql}");
            for (int i = 1; i < whereParcalari.Count; i++)
                sb.AppendLine($"      {whereParcalari[i].Baglac} {whereParcalari[i].Sql}");
        }
        foreach (SpGirisi g in eslesmeyen)
            sb.AppendLine($"    -- AND <tablo>.<kolon> = @{P(g.Param)}   -- ⚠ eşleşen kolon yok, elle bağlayın");

        // ── GROUP BY: toplama varsa toplamasız EŞLEŞEN çıkışlar gruplanır (v19-S19) ──
        if (toplamaVar)
        {
            var gruplananlar = cikislar
                .Where(c => c.Kolon is not null && string.IsNullOrWhiteSpace(c.Toplama))
                .Select(c => $"{AliasBul(c.Kolon!.Tablo)}.{K(c.Kolon!.Kolon.Ad)}")
                .Distinct()
                .ToList();
            if (gruplananlar.Count > 0)
                sb.AppendLine("    GROUP BY " + string.Join(", ", gruplananlar));
        }

        // ── ORDER BY (v19-S19) ──
        if (siralamalar.Count > 0)
            sb.AppendLine("    ORDER BY " + string.Join(", ", siralamalar.Select(s =>
                $"{AliasBul(s.Tablo)}.{K(s.Kolon.Ad)}{(s.Azalan ? " DESC" : "")}")));

        sb.AppendLine("END");
        return sb.ToString();
    }

    /// <summary>Ek WHERE koşulunu SQL'e çevirir (v19-S18): metin kolonlarda değer tırnaklanır,
    /// IS NULL/IS NOT NULL değersizdir, IN parantezlenir.</summary>
    private static string EkKosulSql(string alias, SpEkKosul ek)
    {
        static string K(string a) => $"[{a.Replace("]", "]]")}]";
        string sol = $"{alias}.{K(ek.Kolon.Ad)}";
        if (ek.Operator is "IS NULL" or "IS NOT NULL")
            return $"{sol} {ek.Operator}";

        string ham = (ek.Deger ?? "").Trim();
        if (ek.Operator == "IN")
            return $"{sol} IN ({ham.TrimStart('(').TrimEnd(')')})"; // parantezi biz koyarız

        return $"{sol} {ek.Operator} {DegerTirnakla(ek.Kolon.Tip, ham)}";
    }

    /// <summary>Değeri kolon tipine göre biçimler: metin türü → N'...' (zaten tırnaklı/parametre/
    /// boş değilse); sayısal/tarih/bit → ham (kullanıcı zaten uygun yazar, taslak gözden geçirilir).</summary>
    private static string DegerTirnakla(string kolonTipi, string ham)
    {
        bool metin = kolonTipi.Contains("char", StringComparison.OrdinalIgnoreCase)
            || kolonTipi.Contains("text", StringComparison.OrdinalIgnoreCase);
        if (!metin || ham.Length == 0)
            return ham.Length == 0 ? "''" : ham;
        if (ham.StartsWith('@') || ham.StartsWith("N'", StringComparison.Ordinal) || ham.StartsWith('\''))
            return ham; // zaten parametre ya da tırnaklı
        return $"N'{ham.Replace("'", "''")}'";
    }
}
