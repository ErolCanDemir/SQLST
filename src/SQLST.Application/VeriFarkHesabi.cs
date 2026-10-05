namespace SQLST.Application;

/// <summary>Bir satır farkının yönü (v7-S2): yalnız solda / yalnız sağda / iki tarafta ama değişik.</summary>
public enum VeriFarkTuru
{
    YalnizSol,
    YalnizSag,
    Farkli,
}

/// <summary>Tek bir satır farkı: anahtar (kolon değerleri birleşik) + yön.</summary>
public sealed record VeriFarkKaydi(string Anahtar, VeriFarkTuru Tur);

/// <summary>
/// İki tarafın (anahtar → satır parmak izi) sözlüklerinden satır bazlı farkı hesaplar (v7-S2 çekirdek).
/// SAF mantık: IO yok. Sunucuda hesaplanmış <c>(anahtar, hash)</c> çiftleri buraya gelir; anahtar
/// EŞLEŞMESİ + hash KIYASI ile fark listesi üretilir. Tüm tabloyu belleğe almak yerine yalnız
/// (anahtar, hash) taşındığından büyük tabloları da kaldırır (S3'te farklı anahtarların tam satırı çekilir).
/// </summary>
public static class VeriFarkHesabi
{
    public static IReadOnlyList<VeriFarkKaydi> Hesapla(
        IReadOnlyDictionary<string, string> sol, IReadOnlyDictionary<string, string> sag)
    {
        var farklar = new List<VeriFarkKaydi>();

        foreach ((string anahtar, string solHash) in sol)
        {
            if (!sag.TryGetValue(anahtar, out string? sagHash))
                farklar.Add(new VeriFarkKaydi(anahtar, VeriFarkTuru.YalnizSol));
            else if (!string.Equals(solHash, sagHash, StringComparison.Ordinal))
                farklar.Add(new VeriFarkKaydi(anahtar, VeriFarkTuru.Farkli));
        }
        foreach (string anahtar in sag.Keys)
            if (!sol.ContainsKey(anahtar))
                farklar.Add(new VeriFarkKaydi(anahtar, VeriFarkTuru.YalnizSag));

        return [.. farklar
            .OrderBy(f => f.Tur)
            .ThenBy(f => f.Anahtar, StringComparer.Ordinal)];
    }

    /// <summary>Bir yöne düşen fark sayısı (özet metni için).</summary>
    public static int Say(IReadOnlyList<VeriFarkKaydi> farklar, VeriFarkTuru tur)
        => farklar.Count(f => f.Tur == tur);
}
