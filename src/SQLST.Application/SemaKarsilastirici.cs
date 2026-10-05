using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Bir şema farkı satırının yönü: yalnız solda / yalnız sağda / iki tarafta ama değişik.</summary>
public enum SemaDegisim
{
    YalnizSol,
    YalnizSag,
    Degisti,
}

/// <summary>
/// İki CANLI şema arasındaki tek fark satırı (v7-S1). <paramref name="Nesne"/> tabloyu/nesneyi
/// (şema.ad), <paramref name="Kapsam"/> neyin karşılaştırıldığını ("Tablo" · "Kolon: X" · "FK") söyler.
/// </summary>
public sealed record SemaFarkSatiri(string Nesne, string Kapsam, SemaDegisim Tur, string Detay);

/// <summary>
/// İki veritabanının şemasını karşılaştırır (v7-S1 · Veri Karşılaştırma sürümü). SAF mantık:
/// UI/IO yok, iki tarafın nesne + FK listelerini alır, düzleştirilmiş fark satırları üretir.
///
/// <b>Kapsam (S1):</b> (1) nesne VARLIĞI — tablo/view/SP/fonksiyon yalnız-bir-tarafta;
/// (2) ortak TABLOLARda kolon farkı (yalnız-solda / yalnız-sağda / tip·null·PK değişti);
/// (3) ortak tablolarda FK farkı (yalnız-bir-tarafta). Index farkı S1 dışı (ISchemaService
/// index okumuyor — sonraki adım). <b>Aynı motor</b> varsayılır (tip metinleri doğrudan kıyaslanır).
/// </summary>
public static class SemaKarsilastirici
{
    public static IReadOnlyList<SemaFarkSatiri> Karsilastir(
        IReadOnlyList<SemaNesnesi> sol, IReadOnlyList<YabanciAnahtar> solFk,
        IReadOnlyList<SemaNesnesi> sag, IReadOnlyList<YabanciAnahtar> sagFk,
        IReadOnlyList<Indeks>? solIndeks = null, IReadOnlyList<Indeks>? sagIndeks = null)
    {
        var satirlar = new List<SemaFarkSatiri>();
        Dictionary<string, SemaNesnesi> solIdx = Indexle(sol);
        Dictionary<string, SemaNesnesi> sagIdx = Indexle(sag);

        // 1) Nesne varlığı: yalnız kaynakta / yalnız hedefte (tür + tam ad kimliği).
        // Etiket dili "kaynak/hedef" (kullanıcı isteği 2026-07-31): ekran Kaynak DB / Hedef DB diyor.
        foreach ((string anahtar, SemaNesnesi n) in solIdx)
            if (!sagIdx.ContainsKey(anahtar))
                satirlar.Add(new SemaFarkSatiri(n.TamAd, n.Tur.ToString(), SemaDegisim.YalnizSol, "yalnız kaynakta"));
        foreach ((string anahtar, SemaNesnesi n) in sagIdx)
            if (!solIdx.ContainsKey(anahtar))
                satirlar.Add(new SemaFarkSatiri(n.TamAd, n.Tur.ToString(), SemaDegisim.YalnizSag, "yalnız hedefte"));

        // 2) Ortak TABLOLARda kolon + PK farkı.
        foreach ((string anahtar, SemaNesnesi solN) in solIdx)
        {
            if (solN.Tur != SemaNesneTuru.Tablo || !sagIdx.TryGetValue(anahtar, out SemaNesnesi? sagN))
                continue;
            KolonlariKarsilastir(solN, sagN, satirlar);
        }

        // 3) Ortak tablolarda FK farkı.
        FkleriKarsilastir(solIdx, sagIdx, solFk, sagFk, satirlar);

        // 4) Ortak tablolarda index farkı (yapısal imza: benzersizlik + kolonlar).
        IndeksleriKarsilastir(solIdx, sagIdx, solIndeks ?? [], sagIndeks ?? [], satirlar);

        return [.. satirlar
            .OrderBy(s => s.Nesne, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Kapsam, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Nesne kimliği: tür + şema.ad (büyük/küçük harf duyarsız) — SemaFarkAlici ile aynı desen.</summary>
    private static Dictionary<string, SemaNesnesi> Indexle(IReadOnlyList<SemaNesnesi> nesneler)
    {
        var index = new Dictionary<string, SemaNesnesi>(StringComparer.OrdinalIgnoreCase);
        foreach (SemaNesnesi n in nesneler)
            index[$"{n.Tur}|{n.TamAd}"] = n;
        return index;
    }

    private static void KolonlariKarsilastir(SemaNesnesi sol, SemaNesnesi sag, List<SemaFarkSatiri> satirlar)
    {
        var solK = new Dictionary<string, SemaKolonu>(StringComparer.OrdinalIgnoreCase);
        foreach (SemaKolonu k in sol.Kolonlar) solK[k.Ad] = k;
        var sagK = new Dictionary<string, SemaKolonu>(StringComparer.OrdinalIgnoreCase);
        foreach (SemaKolonu k in sag.Kolonlar) sagK[k.Ad] = k;

        foreach ((string ad, SemaKolonu k) in solK)
        {
            if (!sagK.TryGetValue(ad, out SemaKolonu? sagKol))
                satirlar.Add(new SemaFarkSatiri(sol.TamAd, $"Kolon: {ad}", SemaDegisim.YalnizSol, KolonImza(k)));
            else if (KolonImza(k) != KolonImza(sagKol))
                satirlar.Add(new SemaFarkSatiri(sol.TamAd, $"Kolon: {ad}", SemaDegisim.Degisti,
                    $"{KolonImza(k)}  →  {KolonImza(sagKol)}"));
        }
        foreach ((string ad, SemaKolonu k) in sagK)
            if (!solK.ContainsKey(ad))
                satirlar.Add(new SemaFarkSatiri(sol.TamAd, $"Kolon: {ad}", SemaDegisim.YalnizSag, KolonImza(k)));
    }

    /// <summary>Kolon kıyas imzası: tip + null'labilirlik + PK (aynı motor → tip metni doğrudan kıyaslanır).</summary>
    private static string KolonImza(SemaKolonu k)
        => $"{k.Tip}{(k.NullOlabilir ? " NULL" : " NOT NULL")}{(k.PkMi ? " PK" : "")}";

    private static void FkleriKarsilastir(
        Dictionary<string, SemaNesnesi> solIdx, Dictionary<string, SemaNesnesi> sagIdx,
        IReadOnlyList<YabanciAnahtar> solFk, IReadOnlyList<YabanciAnahtar> sagFk, List<SemaFarkSatiri> satirlar)
    {
        var solGrup = FkGrupla(solFk);
        var sagGrup = FkGrupla(sagFk);

        // Yalnız iki tarafta da VAR OLAN tablolar için FK farkı anlamlı (yoksa "yalnız solda tablo" zaten yazıldı).
        foreach (string tablo in solGrup.Keys.Union(sagGrup.Keys, StringComparer.OrdinalIgnoreCase))
        {
            string tabloAnahtar = $"{SemaNesneTuru.Tablo}|{tablo}";
            if (!solIdx.ContainsKey(tabloAnahtar) || !sagIdx.ContainsKey(tabloAnahtar))
                continue; // en az bir tarafta tablo yok → varlık farkı zaten raporlandı

            HashSet<string> solImza = solGrup.TryGetValue(tablo, out var s) ? s : new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> sagImza = sagGrup.TryGetValue(tablo, out var g) ? g : new(StringComparer.OrdinalIgnoreCase);

            foreach (string imza in solImza)
                if (!sagImza.Contains(imza))
                    satirlar.Add(new SemaFarkSatiri(tablo, "FK", SemaDegisim.YalnizSol, imza));
            foreach (string imza in sagImza)
                if (!solImza.Contains(imza))
                    satirlar.Add(new SemaFarkSatiri(tablo, "FK", SemaDegisim.YalnizSag, imza));
        }
    }

    private static void IndeksleriKarsilastir(
        Dictionary<string, SemaNesnesi> solIdx, Dictionary<string, SemaNesnesi> sagIdx,
        IReadOnlyList<Indeks> solIndeks, IReadOnlyList<Indeks> sagIndeks, List<SemaFarkSatiri> satirlar)
    {
        var solGrup = IndeksGrupla(solIndeks);
        var sagGrup = IndeksGrupla(sagIndeks);

        foreach (string tablo in solGrup.Keys.Union(sagGrup.Keys, StringComparer.OrdinalIgnoreCase))
        {
            string tabloAnahtar = $"{SemaNesneTuru.Tablo}|{tablo}";
            if (!solIdx.ContainsKey(tabloAnahtar) || !sagIdx.ContainsKey(tabloAnahtar))
                continue; // en az bir tarafta tablo yok → varlık farkı zaten raporlandı

            HashSet<string> solImza = solGrup.TryGetValue(tablo, out var s) ? s : new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> sagImza = sagGrup.TryGetValue(tablo, out var g) ? g : new(StringComparer.OrdinalIgnoreCase);

            foreach (string imza in solImza)
                if (!sagImza.Contains(imza))
                    satirlar.Add(new SemaFarkSatiri(tablo, "Index", SemaDegisim.YalnizSol, imza));
            foreach (string imza in sagImza)
                if (!solImza.Contains(imza))
                    satirlar.Add(new SemaFarkSatiri(tablo, "Index", SemaDegisim.YalnizSag, imza));
        }
    }

    /// <summary>İndeksleri tabloya göre gruplar; değer = o tablodaki YAPISAL imzalar (ad hariç).</summary>
    private static Dictionary<string, HashSet<string>> IndeksGrupla(IReadOnlyList<Indeks> indeksler)
    {
        var grup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Indeks ix in indeksler)
        {
            if (!grup.TryGetValue(ix.TamTablo, out HashSet<string>? kume))
                grup[ix.TamTablo] = kume = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            kume.Add(IndeksImza(ix));
        }
        return grup;
    }

    /// <summary>FK'leri kaynak tabloya göre gruplar; değer = o tablodaki FK imzaları kümesi.</summary>
    private static Dictionary<string, HashSet<string>> FkGrupla(IReadOnlyList<YabanciAnahtar> fkler)
    {
        var grup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (YabanciAnahtar fk in fkler)
        {
            string tablo = $"{fk.KaynakSema}.{fk.KaynakTablo}";
            if (!grup.TryGetValue(tablo, out HashSet<string>? kume))
                grup[tablo] = kume = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            kume.Add(FkImza(fk));
        }
        return grup;
    }

    /// <summary>FK kıyas imzası: (kaynak kolonlar) → hedefŞema.hedefTablo(hedef kolonlar).
    /// PUBLIC (2026-08-03): SemaEsitleyici fark satırının Detay'ını bu imzayla geri eşler.</summary>
    public static string FkImza(YabanciAnahtar fk)
        => $"({string.Join(", ", fk.KaynakKolonlar)}) → {fk.HedefSema}.{fk.HedefTablo}({string.Join(", ", fk.HedefKolonlar)})";

    /// <summary>İndeks kıyas imzası (ad hariç — yapısal): UNIQUE? + kolon listesi. PUBLIC: eşitleme eşler.</summary>
    public static string IndeksImza(Indeks ix)
        => $"{(ix.Benzersiz ? "UNIQUE " : "")}({string.Join(", ", ix.Kolonlar)})";
}
