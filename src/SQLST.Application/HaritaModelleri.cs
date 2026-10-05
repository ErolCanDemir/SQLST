using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Haritadaki bir tablo kartının tek kolon satırı (v9-S1). PK/FK rozetleri buradan gelir.</summary>
public sealed record HaritaKolonu(string Ad, string Tip, bool PkMi, bool FkMi);

/// <summary>Haritada bir tablo/view düğümü (kart). Kolonlar sırayla; başlıkta şema + ad.</summary>
public sealed record HaritaDugumu(string Sema, string Ad, IReadOnlyList<HaritaKolonu> Kolonlar)
{
    public string TamAd => string.IsNullOrEmpty(Sema) ? Ad : $"{Sema}.{Ad}";
}

/// <summary>Bir FK bağının kaynak↔hedef kolon çifti (bileşik anahtarda sıralı, aynı uzunlukta).</summary>
public sealed record HaritaKenarKolonu(string KaynakKolon, string HedefKolon);

/// <summary>
/// İki düğüm arasındaki FK bağı (v9-S1). Bir FOREIGN KEY = TEK kenar; bileşik anahtar birden çok
/// kolon çiftiyle tek kenarda toplanır (UI kaynağı FK kolonun satırına, hedefi PK satırına bağlar).
/// </summary>
public sealed record HaritaKenari(
    string KaynakTamAd, string HedefTamAd, IReadOnlyList<HaritaKenarKolonu> Kolonlar);

/// <summary>Veritabanı haritasının SAF modeli (v9-S1): düğümler + FK kenarları. Konum YOK — yerleşim
/// ayrı (<see cref="HaritaYerlesim"/>); render UI'da. Girdi <see cref="SemaOnbellegi"/> + FK listesidir.</summary>
public sealed record HaritaModeli(IReadOnlyList<HaritaDugumu> Dugumler, IReadOnlyList<HaritaKenari> Kenarlar);

/// <summary>
/// Şema nesneleri + yabancı anahtar listesinden harita modelini kuran SAF üretici (v9-S1). Düğüm =
/// tablo/view; kenar = FK (yalnız haritadaki iki tabloyu bağlayanlar — dış/eksik tabloya FK çizilmez).
/// Kolonların <see cref="HaritaKolonu.FkMi"/> bayrağı, o kolonun herhangi bir FK'nin KAYNAĞI olup
/// olmadığından gelir. UI/IO yok — tümüyle testlenebilir.
/// </summary>
public static class HaritaKurucu
{
    public static HaritaModeli Kur(
        IReadOnlyList<SemaNesnesi> nesneler, IReadOnlyList<YabanciAnahtar> fkler)
    {
        // Düğümler: tablo + view + MongoDB koleksiyonu (SP/fonksiyon ER haritasında yer almaz).
        // Koleksiyonlarda FK yoktur → bağsız kart (v9-S5).
        List<SemaNesnesi> dugumNesneleri =
            [.. nesneler.Where(n => n.Tur is SemaNesneTuru.Tablo or SemaNesneTuru.View or SemaNesneTuru.Koleksiyon)];

        // Hangi (şema.tablo.kolon) bir FK'nin KAYNAK kolonu? → kolon FkMi bayrağı için.
        var fkKaynak = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (YabanciAnahtar fk in fkler)
            foreach (string k in fk.KaynakKolonlar)
                fkKaynak.Add($"{fk.KaynakSema}.{fk.KaynakTablo}.{k}");

        List<HaritaDugumu> dugumler =
        [
            .. dugumNesneleri.Select(n => new HaritaDugumu(n.Sema, n.Ad,
                [.. n.Kolonlar.Select(c => new HaritaKolonu(
                    c.Ad, c.Tip, c.PkMi, fkKaynak.Contains($"{n.Sema}.{n.Ad}.{c.Ad}")))]))
        ];

        var haritada = new HashSet<string>(dugumler.Select(d => d.TamAd), StringComparer.OrdinalIgnoreCase);

        var kenarlar = new List<HaritaKenari>();
        foreach (YabanciAnahtar fk in fkler)
        {
            string kaynak = $"{fk.KaynakSema}.{fk.KaynakTablo}";
            string hedef = $"{fk.HedefSema}.{fk.HedefTablo}";
            if (!haritada.Contains(kaynak) || !haritada.Contains(hedef))
                continue; // haritada olmayan tabloya FK → kenar çizilmez

            int n = Math.Min(fk.KaynakKolonlar.Count, fk.HedefKolonlar.Count);
            List<HaritaKenarKolonu> kolonlar =
                [.. Enumerable.Range(0, n).Select(i =>
                    new HaritaKenarKolonu(fk.KaynakKolonlar[i], fk.HedefKolonlar[i]))];
            kenarlar.Add(new HaritaKenari(kaynak, hedef, kolonlar));
        }

        return new HaritaModeli(dugumler, kenarlar);
    }
}
