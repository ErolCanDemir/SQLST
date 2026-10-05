using System.Data;

namespace SQLST.Application;

public enum FarkTuru
{
    YalnizA,
    YalnizB,
    Farkli,
    Ayni
}

/// <summary>
/// Sonuç diff'i çekirdeği (V2-S9, Ö4): iki DataTable'ı kullanıcı seçimi anahtar
/// kolon(lar)la karşılaştırır — yalnız A'da / yalnız B'de / ortak-ama-farklı.
/// Bellek içi çalışır (satır sınırı zaten FOG-6 ile bağlı). Saf ve UI'sız.
/// </summary>
public static class SonucKarsilastirici
{
    /// <param name="Anahtar">Anahtar kolon değerlerinin insan-okur birleşimi.</param>
    /// <param name="FarkliKolonlar">Tur=Farkli ise değeri uyuşmayan ortak kolonlar.</param>
    public sealed record FarkSatiri(
        FarkTuru Tur, string Anahtar, DataRow? A, DataRow? B, IReadOnlyList<string> FarkliKolonlar);

    public sealed record KarsilastirmaSonucu(
        IReadOnlyList<FarkSatiri> Satirlar,
        IReadOnlyList<string> OrtakKolonlar,
        int YalnizAAdet, int YalnizBAdet, int FarkliAdet, int AyniAdet)
    {
        public string Ozet =>
            $"yalnız A: {YalnizAAdet}  ·  yalnız B: {YalnizBAdet}  ·  farklı: {FarkliAdet}  ·  aynı: {AyniAdet}";
    }

    /// <summary>
    /// Karşılaştırır; anahtar bir tarafta TEKİL değilse dürüstçe hata döner
    /// (07-r2 §7: anahtar seçimi kullanıcıya — belirsiz eşleşme uydurulmaz).
    /// </summary>
    public static (KarsilastirmaSonucu? Sonuc, string? Hata) Karsilastir(
        DataTable a, DataTable b, IReadOnlyList<string> anahtarKolonlar, bool aynilariDahilEt = false)
    {
        if (anahtarKolonlar.Count == 0)
            return (null, "En az bir anahtar kolon seçin.");

        List<string> ortak = [.. a.Columns.Cast<DataColumn>().Select(k => k.ColumnName)
            .Intersect(b.Columns.Cast<DataColumn>().Select(k => k.ColumnName), StringComparer.OrdinalIgnoreCase)];
        foreach (string anahtar in anahtarKolonlar)
        {
            if (!ortak.Contains(anahtar, StringComparer.OrdinalIgnoreCase))
                return (null, $"Anahtar kolon iki tarafta da bulunmalı: {anahtar}");
        }

        List<string> kiyasKolonlari = [.. ortak.Except(anahtarKolonlar, StringComparer.OrdinalIgnoreCase)];

        (Dictionary<string, DataRow>? aIndex, string? aHata) = Indexle(a, anahtarKolonlar, "A");
        if (aIndex is null)
            return (null, aHata);
        (Dictionary<string, DataRow>? bIndex, string? bHata) = Indexle(b, anahtarKolonlar, "B");
        if (bIndex is null)
            return (null, bHata);

        var satirlar = new List<FarkSatiri>();
        int yalnizA = 0, yalnizB = 0, farkli = 0, ayni = 0;

        foreach ((string anahtar, DataRow satirA) in aIndex)
        {
            if (!bIndex.TryGetValue(anahtar, out DataRow? satirB))
            {
                yalnizA++;
                satirlar.Add(new FarkSatiri(FarkTuru.YalnizA, anahtar, satirA, null, []));
                continue;
            }

            List<string> farklar = [.. kiyasKolonlari.Where(k => !HucreEsit(satirA[k], satirB[k]))];
            if (farklar.Count > 0)
            {
                farkli++;
                satirlar.Add(new FarkSatiri(FarkTuru.Farkli, anahtar, satirA, satirB, farklar));
            }
            else
            {
                ayni++;
                if (aynilariDahilEt)
                    satirlar.Add(new FarkSatiri(FarkTuru.Ayni, anahtar, satirA, satirB, []));
            }
        }

        foreach ((string anahtar, DataRow satirB) in bIndex)
        {
            if (!aIndex.ContainsKey(anahtar))
            {
                yalnizB++;
                satirlar.Add(new FarkSatiri(FarkTuru.YalnizB, anahtar, null, satirB, []));
            }
        }

        return (new KarsilastirmaSonucu(satirlar, ortak, yalnizA, yalnizB, farkli, ayni), null);
    }

    /// <summary>Fark grid'i (renkli tek grid — 06-r1 Ö4): Durum + anahtar + kolonlar; farklı hücre "a → b".</summary>
    public static DataTable GridTablosuKur(
        KarsilastirmaSonucu sonuc, IReadOnlyList<string> anahtarKolonlar)
    {
        var tablo = new DataTable();
        tablo.Columns.Add("Durum", typeof(string));
        tablo.Columns.Add("Anahtar", typeof(string));
        List<string> digerleri = [.. sonuc.OrtakKolonlar.Except(anahtarKolonlar, StringComparer.OrdinalIgnoreCase)];
        // Veri kolonu "Durum"/"Anahtar" adındaysa (TR şemalarda yaygın) DataTable DuplicateNameException
        // fırlatıyordu (inceleme 2026-07-30, KRİTİK) — TabloyaCevir'deki tekilleştirme deseniyle çöz.
        var kullanilan = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Durum", "Anahtar" };
        foreach (string k in digerleri)
        {
            string aday = k;
            for (int i = 2; !kullanilan.Add(aday); i++)
                aday = $"{k}_{i}";
            tablo.Columns.Add(aday, typeof(string));
        }

        foreach (FarkSatiri f in sonuc.Satirlar)
        {
            var satir = new object?[2 + digerleri.Count];
            satir[0] = f.Tur switch
            {
                FarkTuru.YalnizA => "⬅ yalnız A",
                FarkTuru.YalnizB => "➡ yalnız B",
                FarkTuru.Farkli => "≠ farklı",
                _ => "= aynı",
            };
            satir[1] = f.Anahtar;
            for (int i = 0; i < digerleri.Count; i++)
            {
                string k = digerleri[i];
                satir[2 + i] = f.Tur switch
                {
                    FarkTuru.YalnizA => Metin(f.A![k]),
                    FarkTuru.YalnizB => Metin(f.B![k]),
                    _ when f.FarkliKolonlar.Contains(k, StringComparer.OrdinalIgnoreCase)
                        => $"{Metin(f.A![k])}  →  {Metin(f.B![k])}",
                    _ => Metin(f.A![k]),
                };
            }
            tablo.Rows.Add(satir);
        }
        return tablo;
    }

    private static (Dictionary<string, DataRow>? Index, string? Hata) Indexle(
        DataTable tablo, IReadOnlyList<string> anahtarKolonlar, string taraf)
    {
        var index = new Dictionary<string, DataRow>(StringComparer.Ordinal);
        foreach (DataRow satir in tablo.Rows)
        {
            string anahtar = string.Join(" ‖ ", anahtarKolonlar.Select(k => Metin(satir[k])));
            if (!index.TryAdd(anahtar, satir))
                return (null, $"Anahtar {taraf} tarafında tekil değil (ör. {anahtar}) — daha ayırt edici kolon(lar) seçin.");
        }
        return (index, null);
    }

    private static bool HucreEsit(object? a, object? b)
        => Metin(a) == Metin(b); // tip farkları görünümden okunur — metin kıyası belirlenimci

    private static string Metin(object? deger) => SonucBicimleyici.HucreMetni(deger);
}
