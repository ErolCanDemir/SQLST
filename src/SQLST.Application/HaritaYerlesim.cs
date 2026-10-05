namespace SQLST.Application;

/// <summary>Tuval koordinatı (v9-S1). Sol-üst köşe (0,0); +x sağ, +y aşağı.</summary>
public sealed record Nokta(double X, double Y);

/// <summary>
/// Harita düğümlerinin SAF yerleşimi (v9-S1). İki strateji: <see cref="Izgara"/> (deterministik ilk
/// dizilim) ve <see cref="KuvvetYonlu"/> (bağlı tablolar yaklaşır, bağsızlar itilir — "otomatik
/// yerleştir"). İkisi de DETERMİNİSTİKtir (rastgelelik yok; kuvvet-yönlü ızgaradan tohumlanır) —
/// böylece aynı şema hep aynı haritayı verir ve test edilebilir. Çıktı: TamAd → konum.
/// </summary>
public static class HaritaYerlesim
{
    /// <summary>Kart genişliği (yerleşim aralığı için sabit ölçü; gerçek render UI'da).</summary>
    public const double DugumGenislik = 220;

    /// <summary>Kart yüksekliği tahmini: başlık + kolon satırları.</summary>
    public static double DugumYukseklik(HaritaDugumu d) => 40 + d.Kolonlar.Count * 26;

    /// <summary>Düğümleri kareye yakın bir ızgaraya dizer (giriş sırasına göre — deterministik).</summary>
    public static IReadOnlyDictionary<string, Nokta> Izgara(HaritaModeli model, double bosluk = 64)
    {
        var sonuc = new Dictionary<string, Nokta>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<HaritaDugumu> d = model.Dugumler;
        if (d.Count == 0)
            return sonuc;

        int sutun = (int)Math.Ceiling(Math.Sqrt(d.Count));
        double hucreG = DugumGenislik + bosluk;
        double hucreY = d.Max(DugumYukseklik) + bosluk;
        for (int i = 0; i < d.Count; i++)
            sonuc[d[i].TamAd] = new Nokta(i % sutun * hucreG, i / sutun * hucreY);
        return sonuc;
    }

    /// <summary>
    /// Kuvvet-yönlü (Fruchterman–Reingold türevi) yerleşim. Her düğüm çifti birbirini iter; FK ile
    /// bağlı çiftler çeker; hareket her adımda soğuyan bir sıcaklıkla sınırlanır. Izgaradan tohumlanır
    /// → rastgelelik yok, sonuç tekrarlanabilir. <paramref name="adim"/> yineleme sayısı.
    /// </summary>
    public static IReadOnlyDictionary<string, Nokta> KuvvetYonlu(HaritaModeli model, int adim = 320)
    {
        IReadOnlyList<HaritaDugumu> d = model.Dugumler;
        if (d.Count == 0)
            return new Dictionary<string, Nokta>(StringComparer.OrdinalIgnoreCase);
        if (d.Count == 1)
            return new Dictionary<string, Nokta>(StringComparer.OrdinalIgnoreCase) { [d[0].TamAd] = new Nokta(0, 0) };

        IReadOnlyDictionary<string, Nokta> tohum = Izgara(model);
        var pos = d.ToDictionary(n => n.TamAd,
            n => new[] { tohum[n.TamAd].X, tohum[n.TamAd].Y }, StringComparer.OrdinalIgnoreCase);

        double k = DugumGenislik + 120;                    // ideal komşuluk mesafesi
        double sicaklik = k * 3;                           // adım başına en çok hareket
        List<HaritaKenari> kenarlar =
            [.. model.Kenarlar.Where(e => pos.ContainsKey(e.KaynakTamAd) && pos.ContainsKey(e.HedefTamAd))];

        for (int it = 0; it < adim; it++)
        {
            var itme = d.ToDictionary(n => n.TamAd, _ => new double[2], StringComparer.OrdinalIgnoreCase);

            // İtme: her düğüm çifti (O(n²) — tipik şema için yeterli; UI adım sayısını kısabilir)
            for (int i = 0; i < d.Count; i++)
                for (int j = i + 1; j < d.Count; j++)
                {
                    double[] a = pos[d[i].TamAd], b = pos[d[j].TamAd];
                    double dx = a[0] - b[0], dy = a[1] - b[1];
                    double uzk = Math.Max(0.01, Math.Sqrt(dx * dx + dy * dy));
                    double kuvvet = k * k / uzk, ux = dx / uzk, uy = dy / uzk;
                    itme[d[i].TamAd][0] += ux * kuvvet; itme[d[i].TamAd][1] += uy * kuvvet;
                    itme[d[j].TamAd][0] -= ux * kuvvet; itme[d[j].TamAd][1] -= uy * kuvvet;
                }

            // Çekme: FK ile bağlı çiftler
            foreach (HaritaKenari e in kenarlar)
            {
                double[] a = pos[e.KaynakTamAd], b = pos[e.HedefTamAd];
                double dx = a[0] - b[0], dy = a[1] - b[1];
                double uzk = Math.Max(0.01, Math.Sqrt(dx * dx + dy * dy));
                double kuvvet = uzk * uzk / k, ux = dx / uzk, uy = dy / uzk;
                itme[e.KaynakTamAd][0] -= ux * kuvvet; itme[e.KaynakTamAd][1] -= uy * kuvvet;
                itme[e.HedefTamAd][0] += ux * kuvvet; itme[e.HedefTamAd][1] += uy * kuvvet;
            }

            // Uygula (sıcaklıkla sınırlı)
            foreach (HaritaDugumu n in d)
            {
                double[] dp = itme[n.TamAd];
                double boy = Math.Max(0.01, Math.Sqrt(dp[0] * dp[0] + dp[1] * dp[1]));
                double sinir = Math.Min(boy, sicaklik);
                pos[n.TamAd][0] += dp[0] / boy * sinir;
                pos[n.TamAd][1] += dp[1] / boy * sinir;
            }
            sicaklik = Math.Max(k * 0.05, sicaklik * 0.97); // soğuma
        }

        // Sol-üst köşeyi (0,0)'a çek — negatif koordinat kalmasın
        double minX = pos.Values.Min(p => p[0]), minY = pos.Values.Min(p => p[1]);
        return pos.ToDictionary(kv => kv.Key,
            kv => new Nokta(kv.Value[0] - minX, kv.Value[1] - minY), StringComparer.OrdinalIgnoreCase);
    }
}
