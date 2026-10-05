using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// 🔗 Kolon bağları çekirdeği (v20-S10, kullanıcı fikri 2026-08-07: "kolona sağ tık →
/// tüm bağlantılarını göster"). SAF ve testli: bir kolonun FK grafındaki TÜM bağlarını çıkarır —
/// ▲ giden (bu kolon hangi tabloyu işaret ediyor) + ▼ gelen (bu kolonu kim işaret ediyor).
/// FK grafı şema önbelleğinden gelir (v6-S2, motor-nötr) — ağ gitmez; Mongo'da FK yoktur.
/// </summary>
public static class KolonBaglari
{
    /// <summary>
    /// Tek bağ. <paramref name="Giden"/>=true → tıklanan kolon FK'nın KAYNAK bacağındadır (hedefi
    /// işaret eder); false → HEDEF bacağındadır (kaynak tablo bu kolonu işaret eder). Bileşik FK'da
    /// tıklanan kolonun karşı bacaktaki eşleniği <paramref name="KarsiKolon"/>'dur (indeks eşleşmesi).
    /// </summary>
    public sealed record Bag(bool Giden, YabanciAnahtar Fk, string KarsiKolon)
    {
        /// <summary>Liste satırı: "▲ dbo.Siparis(MusteriId) → dbo.Musteri(Id) · FK_Siparis_Musteri".</summary>
        public string Gosterim =>
            $"{(Giden ? "▲" : "▼")} {Taraf(Fk.KaynakSema, Fk.KaynakTablo, Fk.KaynakKolonlar)}"
            + $" → {Taraf(Fk.HedefSema, Fk.HedefTablo, Fk.HedefKolonlar)}"
            + (string.IsNullOrEmpty(Fk.Ad) ? "" : $"  ·  {Fk.Ad}");

        private static string Taraf(string sema, string tablo, IReadOnlyList<string> kolonlar) =>
            $"{(string.IsNullOrEmpty(sema) ? "" : sema + ".")}{tablo}({string.Join(", ", kolonlar)})";
    }

    /// <summary>
    /// Kolonun tüm bağlarını bulur: önce ▲ gidenler, sonra ▼ gelenler. Tanımlayıcı kıyası
    /// büyük/küçük duyarsızdır (SQL kimliği); bileşik FK'da kolon hangi indeksteyse karşı
    /// bacağın aynı indeksi eşlenik sayılır.
    /// </summary>
    public static IReadOnlyList<Bag> Bul(
        string? sema, string tablo, string kolon, IReadOnlyList<YabanciAnahtar> fkler)
    {
        var baglar = new List<Bag>();
        foreach (YabanciAnahtar fk in fkler)
        {
            if (TabloEs(fk.KaynakSema, fk.KaynakTablo, sema, tablo)
                && Indeks(fk.KaynakKolonlar, kolon) is int gi && gi < fk.HedefKolonlar.Count)
                baglar.Add(new Bag(Giden: true, fk, fk.HedefKolonlar[gi]));

            if (TabloEs(fk.HedefSema, fk.HedefTablo, sema, tablo)
                && Indeks(fk.HedefKolonlar, kolon) is int hi && hi < fk.KaynakKolonlar.Count)
                baglar.Add(new Bag(Giden: false, fk, fk.KaynakKolonlar[hi]));
        }
        return [.. baglar.OrderByDescending(b => b.Giden)];
    }

    /// <summary>
    /// Bağı ortaya çıkaran JOIN sorgusu: kaynak k ⋈ hedef h, ilk <paramref name="tavan"/> satır.
    /// Sınır sözdizimi lehçeden gelir (TOP / LIMIT / FETCH) — motor-parametrik.
    /// </summary>
    public static string JoinSorgusu(Bag bag, ILehce lehce, int tavan = 100)
    {
        YabanciAnahtar fk = bag.Fk;
        string on = string.Join(" AND ", fk.KaynakKolonlar.Zip(fk.HedefKolonlar,
            (a, b) => $"k.{lehce.TirnaklaTanimlayici(a)} = h.{lehce.TirnaklaTanimlayici(b)}"));
        return $"SELECT {lehce.SatirSinirBasi(tavan)}* FROM {lehce.TamAdYaz(fk.KaynakSema, fk.KaynakTablo)} k"
             + $" JOIN {lehce.TamAdYaz(fk.HedefSema, fk.HedefTablo)} h ON {on}{lehce.SatirSinirSonu(tavan)};";
    }

    private static bool TabloEs(string fkSema, string fkTablo, string? sema, string tablo) =>
        string.Equals(fkTablo, tablo, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(sema) || string.IsNullOrEmpty(fkSema)
            || string.Equals(fkSema, sema, StringComparison.OrdinalIgnoreCase));

    private static int? Indeks(IReadOnlyList<string> kolonlar, string kolon)
    {
        for (int i = 0; i < kolonlar.Count; i++)
        {
            if (string.Equals(kolonlar[i], kolon, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return null;
    }
}
