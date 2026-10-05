namespace SQLST.Application;

/// <summary>Mahalle görünümündeki bir küme kartı: ad + üyeler (TamAd) + öne çıkan (en bağlantılı) tablolar.</summary>
public sealed record HaritaKumesi(string Ad, IReadOnlyList<string> Uyeler, IReadOnlyList<string> OneCikanlar);

/// <summary>İki küme arasındaki toplu ilişki: aradaki FK sayısı (kalın tek çizgi + rozet).</summary>
public sealed record HaritaKumeKenari(int Kaynak, int Hedef, int FkSayisi);

/// <summary>Mahalle görünümünün tamamı: kümeler + aralarındaki toplu bağlar.</summary>
public sealed record HaritaKumeHaritasi(
    IReadOnlyList<HaritaKumesi> Kumeler, IReadOnlyList<HaritaKumeKenari> Kenarlar);

/// <summary>
/// Harita "Odak + Bağlam" SAF mantığı (v11-öncesi #1, kullanıcı onayı 2026-07-25). Büyük şemada
/// "hepsini çiz, yerleşimi iyileştir" yerine "hepsini birden HİÇ çizme": açılışta tablolar FK
/// bağlantısına göre KÜMELERE (mahalle kartları) toplanır; kümeye dalınca yalnız o kümenin
/// tabloları, bir tabloya odaklanınca yalnız komşuluğu çizilir. Tümü DETERMİNİSTİK (rastgelelik
/// yok; eşitlikler ada göre kırılır) — aynı şema hep aynı haritayı verir, test edilebilir.
/// </summary>
public static class HaritaKumeleme
{
    /// <summary>
    /// Kümeleri kurar: en yüksek dereceli atanmamış düğüm ÇEKİRDEK olur, komşuluktan (derece sırasıyla)
    /// en fazla <paramref name="hedefBoy"/> üye toplanır; bağı olmayan tablolar tek "Bağımsız tablolar"
    /// kümesine gider. Küme adı: üyeler tek şemadaysa şema adı, değilse "çekirdek çevresi".
    /// </summary>
    public static HaritaKumeHaritasi Kur(HaritaModeli model, int hedefBoy = 12)
    {
        Dictionary<string, List<string>> komsuluk = Komsuluk(model);
        Dictionary<string, int> derece = komsuluk.ToDictionary(
            k => k.Key, k => k.Value.Count, StringComparer.OrdinalIgnoreCase);

        var atanmamis = new HashSet<string>(
            model.Dugumler.Select(d => d.TamAd), StringComparer.OrdinalIgnoreCase);

        // Bağımsızlar (derece 0) en sona, tek kümede.
        List<string> bagimsizlar = [.. atanmamis
            .Where(a => derece.GetValueOrDefault(a) == 0)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)];
        atanmamis.ExceptWith(bagimsizlar);

        // 1) Üye listeleri: çekirdekten BFS ile hedefBoy'a kadar topla.
        var uyeListeleri = new List<List<string>>();
        while (atanmamis.Count > 0)
        {
            string cekirdek = atanmamis
                .OrderByDescending(a => derece.GetValueOrDefault(a))
                .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
                .First();

            var uyeler = new List<string> { cekirdek };
            atanmamis.Remove(cekirdek);
            var kuyruk = new Queue<string>([cekirdek]);
            while (kuyruk.Count > 0 && uyeler.Count < hedefBoy)
            {
                string bas = kuyruk.Dequeue();
                foreach (string komsu in komsuluk.GetValueOrDefault(bas, [])
                    .Where(atanmamis.Contains)
                    .OrderByDescending(k => derece.GetValueOrDefault(k))
                    .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    if (uyeler.Count >= hedefBoy)
                        break;
                    uyeler.Add(komsu);
                    atanmamis.Remove(komsu);
                    kuyruk.Enqueue(komsu);
                }
            }
            uyeListeleri.Add(uyeler);
        }

        // 2) Artakalan TEKLİ kümeler (kapasite taşması: komşuları başka kümeye dolmuş uydular)
        //    hedefBoy'luk parçalar hâlinde birleştirilir — kart enflasyonu olmasın.
        List<List<string>> tekliler = [.. uyeListeleri.Where(u => u.Count == 1)];
        if (tekliler.Count > 1)
        {
            uyeListeleri.RemoveAll(u => u.Count == 1);
            List<string> hepsi = [.. tekliler.Select(t => t[0]).OrderBy(a => a, StringComparer.OrdinalIgnoreCase)];
            for (int i = 0; i < hepsi.Count; i += hedefBoy)
                uyeListeleri.Add([.. hepsi.Skip(i).Take(hedefBoy)]);
        }

        // 3) Küme kayıtları + indeks.
        var kumeler = new List<HaritaKumesi>();
        var kumeIndeksi = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (List<string> uyeler in uyeListeleri)
        {
            string cekirdek = uyeler
                .OrderByDescending(u => derece.GetValueOrDefault(u))
                .ThenBy(u => u, StringComparer.OrdinalIgnoreCase)
                .First();
            foreach (string u in uyeler)
                kumeIndeksi[u] = kumeler.Count;
            kumeler.Add(KumeYap(model, uyeler, derece, cekirdek));
        }

        if (bagimsizlar.Count > 0)
        {
            foreach (string u in bagimsizlar)
                kumeIndeksi[u] = kumeler.Count;
            kumeler.Add(new HaritaKumesi("Bağımsız tablolar", bagimsizlar, [.. bagimsizlar.Take(3)]));
        }

        // Kümeler arası toplu bağlar: farklı kümelere düşen her FK kenarı sayılır.
        var sayilar = new Dictionary<(int, int), int>();
        foreach (HaritaKenari e in model.Kenarlar)
        {
            if (!kumeIndeksi.TryGetValue(e.KaynakTamAd, out int a)
                || !kumeIndeksi.TryGetValue(e.HedefTamAd, out int b) || a == b)
                continue;
            (int, int) c = a < b ? (a, b) : (b, a);
            sayilar[c] = sayilar.GetValueOrDefault(c) + 1;
        }

        return new HaritaKumeHaritasi(kumeler,
            [.. sayilar.OrderBy(s => s.Key).Select(s => new HaritaKumeKenari(s.Key.Item1, s.Key.Item2, s.Value))]);
    }

    private static HaritaKumesi KumeYap(
        HaritaModeli model, List<string> uyeler, Dictionary<string, int> derece, string cekirdek)
    {
        List<string> oneCikan = [.. uyeler
            .OrderByDescending(u => derece.GetValueOrDefault(u))
            .ThenBy(u => u, StringComparer.OrdinalIgnoreCase)
            .Take(3)];

        var semalar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uyeKumesi = new HashSet<string>(uyeler, StringComparer.OrdinalIgnoreCase);
        foreach (HaritaDugumu d in model.Dugumler)
            if (uyeKumesi.Contains(d.TamAd))
                semalar.Add(d.Sema);

        string cekirdekAd = cekirdek.Contains('.') ? cekirdek[(cekirdek.IndexOf('.') + 1)..] : cekirdek;
        string ad = semalar.Count == 1 && !string.IsNullOrEmpty(semalar.First())
            ? $"{semalar.First()} · {cekirdekAd}"
            : $"{cekirdekAd} çevresi";
        return new HaritaKumesi(ad, uyeler, oneCikan);
    }

    /// <summary>Küme kartlarının ızgara yerleşimi (kart ~240×132; kareye yakın dizilim).</summary>
    public static IReadOnlyList<Nokta> KumeYerlesimi(int kumeSayisi, double bosluk = 90)
    {
        const double G = 240, Y = 132;
        int sutun = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(kumeSayisi)));
        return [.. Enumerable.Range(0, kumeSayisi).Select(i =>
            new Nokta(60 + i % sutun * (G + bosluk), 60 + i / sutun * (Y + bosluk)))];
    }

    /// <summary>Odak komşuluğu: merkez + <paramref name="derinlik"/> adım BFS (merkez dahil, ada göre kararlı).</summary>
    public static IReadOnlyList<string> Komsular(HaritaModeli model, string merkez, int derinlik)
    {
        Dictionary<string, List<string>> komsuluk = Komsuluk(model);
        var gorunur = new List<string> { merkez };
        var kume = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { merkez };
        var sinir = new List<string> { merkez };
        for (int d = 0; d < derinlik; d++)
        {
            List<string> yeni = [.. sinir
                .SelectMany(s => komsuluk.GetValueOrDefault(s, []))
                .Where(k => !kume.Contains(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
            foreach (string y in yeni)
                kume.Add(y);
            gorunur.AddRange(yeni);
            sinir = yeni;
        }
        return gorunur;
    }

    /// <summary>
    /// Radyal odak yerleşimi: merkez ortada, 1. halka çevresinde, 2. halka daha dışta. Halka yarıçapı
    /// kart sayısıyla büyür (kartlar çakışmasın); açılar ada göre kararlı dağıtılır.
    /// </summary>
    public static IReadOnlyDictionary<string, Nokta> Radyal(
        HaritaModeli model, string merkez, IReadOnlyList<string> gorunur)
    {
        Dictionary<string, List<string>> komsuluk = Komsuluk(model);
        var sonuc = new Dictionary<string, Nokta>(StringComparer.OrdinalIgnoreCase);
        const double MerkezX = 900, MerkezY = 560;
        sonuc[merkez] = new Nokta(MerkezX - HaritaYerlesim.DugumGenislik / 2, MerkezY - 80);

        // Halkalar: merkezden BFS mesafesine göre.
        var mesafe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [merkez] = 0 };
        var kuyruk = new Queue<string>([merkez]);
        var gorunurKume = new HashSet<string>(gorunur, StringComparer.OrdinalIgnoreCase);
        while (kuyruk.Count > 0)
        {
            string bas = kuyruk.Dequeue();
            foreach (string k in komsuluk.GetValueOrDefault(bas, []).Where(gorunurKume.Contains))
                if (!mesafe.ContainsKey(k))
                {
                    mesafe[k] = mesafe[bas] + 1;
                    kuyruk.Enqueue(k);
                }
        }

        foreach (IGrouping<int, string> halka in gorunur
            .Where(g => !g.Equals(merkez, StringComparison.OrdinalIgnoreCase))
            .GroupBy(g => mesafe.GetValueOrDefault(g, 1)))
        {
            List<string> uyeler = [.. halka.OrderBy(u => u, StringComparer.OrdinalIgnoreCase)];
            // Çakışmasın: çevre uzunluğu kart başına ~300px versin; alt sınır halka başına büyür.
            double r = Math.Max(300 + (halka.Key - 1) * 260, uyeler.Count * 300 / (2 * Math.PI));
            for (int i = 0; i < uyeler.Count; i++)
            {
                double aci = -Math.PI / 2 + 2 * Math.PI * i / uyeler.Count;
                sonuc[uyeler[i]] = new Nokta(
                    MerkezX + r * Math.Cos(aci) - HaritaYerlesim.DugumGenislik / 2,
                    MerkezY + r * Math.Sin(aci) - 60);
            }
        }
        return sonuc;
    }

    private static Dictionary<string, List<string>> Komsuluk(HaritaModeli model)
    {
        var komsuluk = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (HaritaDugumu d in model.Dugumler)
            komsuluk[d.TamAd] = [];
        foreach (HaritaKenari e in model.Kenarlar)
        {
            if (e.KaynakTamAd.Equals(e.HedefTamAd, StringComparison.OrdinalIgnoreCase))
                continue; // öz-FK komşuluk üretmez
            if (komsuluk.TryGetValue(e.KaynakTamAd, out List<string>? a)
                && !a.Contains(e.HedefTamAd, StringComparer.OrdinalIgnoreCase))
                a.Add(e.HedefTamAd);
            if (komsuluk.TryGetValue(e.HedefTamAd, out List<string>? b)
                && !b.Contains(e.KaynakTamAd, StringComparer.OrdinalIgnoreCase))
                b.Add(e.KaynakTamAd);
        }
        return komsuluk;
    }
}
