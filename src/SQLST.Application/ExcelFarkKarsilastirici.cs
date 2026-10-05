using System.Globalization;

namespace SQLST.Application;

/// <summary>Fark satiri: kolon adi -> deger (kaynak ya da hedef satirin kolon eslemeli gorunumu).</summary>
public sealed record FarkSatiri(IReadOnlyDictionary<string, object?> Degerler);

/// <summary>DEGISEN satir: anahtar ayni, en az bir kolon farkli. Eski->yeni ve tam yeni satir tasinir.</summary>
public sealed record DegisenSatir(
    IReadOnlyDictionary<string, object?> Anahtar,
    IReadOnlyDictionary<string, (object? Eski, object? Yeni)> Degisenler,
    IReadOnlyDictionary<string, object?> YeniSatir);

/// <summary>
/// Excel/tablo farki (BF-3): uc kova. YENI = Excel'de var tabloda yok -> INSERT · DEGISEN = anahtar
/// eslesti kolon farkli -> UPDATE · SILINMIS = tabloda var Excel'de yok -> DELETE (tam esitleme).
/// </summary>
public sealed record TabloFarki(
    IReadOnlyList<FarkSatiri> Yeniler,
    IReadOnlyList<DegisenSatir> Degisenler,
    IReadOnlyList<FarkSatiri> Silinenler);

/// <summary>
/// Excel (kaynak) ile hedef tablo satirlarini ANAHTAR kolonlara gore esleyip farki uc kovaya ayirir
/// (BF-3, kullanici tasarimi 2026-07-27: "Ice Aktar / Guncelle" -> fark ekrani; Guncelle'de Excel'de
/// olmayan satir otomatik SILINMIS kovasina duser = tam esitleme). SAF: veritabanina dokunmaz, DML
/// uretmez -- cagiran (Show Script + Guvenli Yazma) uretir. Deger karsilastirmasi KULTURDEN BAGIMSIZ
/// metin temsili uzerinden yapilir (Excel'in double/DateTime'i ile tablonun decimal/date'i "1250,75"
/// vs "1250.75" ya da olcek farki yuzunden YANLIS "degisti" sayilmasin diye normallestirilir).
/// </summary>
public static class ExcelFarkKarsilastirici
{
    public static TabloFarki Karsilastir(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> kaynak,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> hedef,
        IReadOnlyList<string> anahtarKolonlar,
        IReadOnlyList<string> karsilastirilanKolonlar)
    {
        if (anahtarKolonlar.Count == 0)
            throw new ArgumentException("En az bir anahtar kolon gerekir.", nameof(anahtarKolonlar));

        // Hedefi anahtara gore indeksle (ayni anahtardan ilki tutulur; tabloda yinelenen anahtar
        // beklenmez ama patlatmayiz -- ilki baz alinir).
        var hedefIndeks = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        foreach (IReadOnlyDictionary<string, object?> satir in hedef)
            hedefIndeks.TryAdd(AnahtarMetni(satir, anahtarKolonlar), satir);

        var yeniler = new List<FarkSatiri>();
        var degisenler = new List<DegisenSatir>();
        var kaynaktaGorulen = new HashSet<string>();

        foreach (IReadOnlyDictionary<string, object?> ksatir in kaynak)
        {
            string anahtar = AnahtarMetni(ksatir, anahtarKolonlar);
            kaynaktaGorulen.Add(anahtar);

            if (!hedefIndeks.TryGetValue(anahtar, out IReadOnlyDictionary<string, object?>? hsatir))
            {
                yeniler.Add(new FarkSatiri(ksatir)); // tabloda yok -> INSERT
                continue;
            }

            var degisiklik = new Dictionary<string, (object?, object?)>();
            foreach (string kolon in karsilastirilanKolonlar)
            {
                object? eski = hsatir.GetValueOrDefault(kolon);
                object? yeni = ksatir.GetValueOrDefault(kolon);
                if (!Esit(eski, yeni))
                    degisiklik[kolon] = (eski, yeni);
            }
            if (degisiklik.Count > 0)
                degisenler.Add(new DegisenSatir(AnahtarSozluk(ksatir, anahtarKolonlar), degisiklik, ksatir));
        }

        // Tabloda olup Excel'de olmayan -> tam esitlemede SILINMIS.
        var silinenler = new List<FarkSatiri>();
        foreach (IReadOnlyDictionary<string, object?> hsatir in hedef)
        {
            if (!kaynaktaGorulen.Contains(AnahtarMetni(hsatir, anahtarKolonlar)))
                silinenler.Add(new FarkSatiri(hsatir));
        }

        return new TabloFarki(yeniler, degisenler, silinenler);
    }

    /// <summary>
    /// Kaynakta (Excel) AYNI anahtardan birden fazla satır — kaç satır yinelenen anahtara sahip
    /// (kod inceleme 2026-07-27). Tam eşitlemede yinelenen anahtar, YENİ kovada çift INSERT (PK ihlali
    /// → tüm işlem geri alınır) ya da çift UPDATE (son kazanır, sessiz) demektir; UI önceden uyarır.
    /// </summary>
    public static int YinelenenAnahtarSayisi(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> kaynak, IReadOnlyList<string> anahtarKolonlar)
    {
        var gorulen = new HashSet<string>();
        int yinelenen = 0;
        foreach (IReadOnlyDictionary<string, object?> satir in kaynak)
            if (!gorulen.Add(AnahtarMetni(satir, anahtarKolonlar)))
                yinelenen++;
        return yinelenen;
    }

    private static Dictionary<string, object?> AnahtarSozluk(
        IReadOnlyDictionary<string, object?> satir, IReadOnlyList<string> anahtarKolonlar)
        => anahtarKolonlar.ToDictionary(k => k, k => satir.GetValueOrDefault(k));

    /// <summary>
    /// Anahtar kolonların normalize değerlerini AYIRICIYLA birleştirir (eşleme kimliği). Ayırıcı ŞART
    /// (kod inceleme 2026-07-27): ayırıcısız (2026,5) ile (202,65) ikisi de "20265" olup ÇAKIŞIR →
    /// yanlış eşleşme, sessiz yanlış eşitleme. U+001F (Birim Ayırıcı): DB anahtar kolonlarında
    /// (sayı/kısa metin/tarih) gerçekleşmesi olanaksız kontrol karakteri.
    /// </summary>
    private static string AnahtarMetni(IReadOnlyDictionary<string, object?> satir, IReadOnlyList<string> anahtarKolonlar)
        => string.Join((char)0x1F, anahtarKolonlar.Select(k => Normalize(satir.GetValueOrDefault(k))));

    /// <summary>Iki hucre kulturden bagimsiz temsille esit mi (Excel double vs DB decimal vb.).</summary>
    private static bool Esit(object? a, object? b) => Normalize(a) == Normalize(b);

    /// <summary>Hucre -> kulturden bagimsiz karsilastirma anahtari. NULL, bos metinden AYRIdir.</summary>
    private static string Normalize(object? deger)
    {
        switch (deger)
        {
            case null or DBNull:
                return "\0NULL"; // NULL'a ozel isaret -- "" (bos metin) ile karismasin
            case DateTime t:
                return t.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset t:
                return t.ToString("O", CultureInfo.InvariantCulture);
            case bool b:
                return b ? "1" : "0";
            // char(n)/nchar(n) SONDAN BOŞLUKLA doldurur ("TR" -> "TR        "); dosya tarafı Trim'li
            // gelir. SONDAKİ boşluğu at (kod inceleme 2026-07-27): yoksa her eşitlemede sahte "değişti"
            // + DB'nin yeniden doldurması yüzünden ASLA yakınsamayan churn olur. SQL'in kendi metin
            // eşitliği de sondaki boşluğu yok sayar ('TR' = 'TR   '), bu yüzden bu DOĞRU davranış.
            case string s:
                return s.TrimEnd();
        }

        // Sayisal tipleri TEK bicime indir: Excel double'i (1250) ile DB decimal'i (1250.00)
        // ayni sayilsin (olcek/gosterim farki YANLIS "degisti" uretmesin). Sondaki sifirlar atilir.
        if (SayiMi(deger, out decimal m))
            return m.ToString("0.############################", CultureInfo.InvariantCulture);

        return deger is IFormattable f
            ? f.ToString(null, CultureInfo.InvariantCulture)
            : deger.ToString() ?? "";
    }

    private static bool SayiMi(object deger, out decimal m)
    {
        m = 0;
        try
        {
            switch (deger)
            {
                case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                    m = Convert.ToDecimal(deger, CultureInfo.InvariantCulture);
                    return true;
                case double d:
                    m = (decimal)d;
                    return true;
                case float ff:
                    m = (decimal)ff;
                    return true;
                default:
                    return false;
            }
        }
        catch (OverflowException)
        {
            return false; // decimal araligi disi (cok buyuk double) -> metin karsilastirmasina duser
        }
    }
}
