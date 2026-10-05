using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// v20-S21 saha m.17 ("kolon listesinde FK göstergesi yok"): şema önbelleği kurulurken FK
/// listesini kolonlara İŞLER — kaynak kolon 🔗 + hedef tablo etiketi alır (ağaç PK 🔑 ile
/// aynı satır biçimi). Saf ve yan etkisizdir: FK'sı olmayan nesneler AYNI referansla döner
/// (büyük şemada gereksiz kopya yok). Şema sorgularına kolon başına FK alanı eklemek yerine
/// burada birleştirme seçildi: FK'lar zaten önbellekle birlikte paralel yükleniyor (v2b #8)
/// ve dört SQL motorunun sorgusuna tek tek dokunmak gerekmiyor.
/// </summary>
public static class FkIsaretleyici
{
    /// <summary>Yalnız 🔗 işareti konur, hedef ağaca yazılmaz (kullanıcı 2026-08-14) — bağların
    /// tamamı sağ tık → 🔗 Kolon Bağları penceresinde (v20-S10) zaten listelenir.</summary>
    public static IReadOnlyList<SemaNesnesi> Isaretle(
        IReadOnlyList<SemaNesnesi> nesneler, IReadOnlyList<YabanciAnahtar> fkler)
    {
        if (fkler.Count == 0 || nesneler.Count == 0)
            return nesneler;

        // (şema.tablo, kolon) kümesi — ad karşılaştırmaları SQL kimliği gibi büyük/küçük
        // duyarsız (Türkçe I tuzağına düşmemek için Invariant tabanlı).
        var fkKolonlari = new HashSet<(string Tablo, string Kolon)>();
        foreach (YabanciAnahtar fk in fkler)
            foreach (string kolon in fk.KaynakKolonlar)
                fkKolonlari.Add((Anahtar($"{fk.KaynakSema}.{fk.KaynakTablo}"), Anahtar(kolon)));

        var sonuc = new List<SemaNesnesi>(nesneler.Count);
        foreach (SemaNesnesi nesne in nesneler)
        {
            if (nesne.Tur != SemaNesneTuru.Tablo || nesne.Kolonlar.Count == 0)
            {
                sonuc.Add(nesne);
                continue;
            }

            string tabloAnahtari = Anahtar(nesne.TamAd);
            SemaKolonu[]? yeniKolonlar = null; // yalnız gerçekten FK'lı tabloda kopyalanır
            for (int i = 0; i < nesne.Kolonlar.Count; i++)
            {
                SemaKolonu k = nesne.Kolonlar[i];
                if (!fkKolonlari.Contains((tabloAnahtari, Anahtar(k.Ad))))
                    continue;
                yeniKolonlar ??= [.. nesne.Kolonlar];
                yeniKolonlar[i] = k with { FkMi = true };
            }

            sonuc.Add(yeniKolonlar is null ? nesne : nesne with { Kolonlar = yeniKolonlar });
        }
        return sonuc;
    }

    private static string Anahtar(string ad) => ad.ToUpperInvariant();
}
