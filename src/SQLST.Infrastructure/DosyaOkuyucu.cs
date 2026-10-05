using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Excel/TXT Import dosya okuyucusu (v13-S1). İki biçim:
///  • <b>TXT/CSV</b> — <see cref="MetinTabloAyristirici"/> ile AKIŞLI (dev dosya belleğe alınmaz);
///    kodlama çağırandan (UTF-8 varsayılan, Windows-1254 desteklenir — CodePages sağlayıcısı
///    burada kaydedilir), BOM varsa otomatik kazanır.
///  • <b>Excel</b> — ExcelDataReader (MIT): .xlsx + eski .xls; hücreler TİPLİ gelir
///    (double/DateTime/bool/string) — tip tahmini bu yüzden Excel'de daha isabetlidir.
/// Önizleme <see cref="EnCokOnizlemeSatiri"/> ile sınırlıdır (ekran donmasın); aktarım (S3)
/// akış metotlarını sınırsız kullanır. İlk satır başlıksa kolon adları oradan, değilse Kolon1…N.
/// </summary>
public static class DosyaOkuyucu
{
    /// <summary>Önizlemede gösterilen en çok satır (ekran + tip tahmini örneklemi).</summary>
    public const int EnCokOnizlemeSatiri = 200;

    static DosyaOkuyucu()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // 1254 + eski .xls için

    /// <summary>Desteklenen metin kodlamaları (ekrandaki combo bu listeyi gösterir).</summary>
    public static IReadOnlyList<string> Kodlamalar => ["UTF-8", "Windows-1254 (Türkçe ANSI)"];

    public static Encoding KodlamaCoz(string? ad) =>
        ad is not null && ad.StartsWith("Windows-1254", StringComparison.OrdinalIgnoreCase)
            ? Encoding.GetEncoding(1254)
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Uzantıya göre Excel mi (xlsx/xlsm/xls) — ekran dosya seçiminde ayracı gizler.</summary>
    public static bool ExcelMi(string yol) =>
        Path.GetExtension(yol).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xls";

    // ---- TXT/CSV ----

    /// <summary>
    /// CSV kolon ayracını OTOMATİK algılar (kullanıcı kuralı 2026-07-26: "CSV için ayraca gerek
    /// yok"): ilk birkaç KB'de tırnak DIŞI aday sayımı — TR Excel'in ';' CSV'si de, standart ','
    /// CSV'si de sorusuz açılır. Hiç aday görülmezse ';' varsayılır (tek kolonlu dosya).
    /// </summary>
    public static char AyracAlgila(string yol, Encoding kodlama)
    {
        using StreamReader okuyucu = new(yol, kodlama, detectEncodingFromByteOrderMarks: true);
        char[] tampon = new char[8192];
        int n = okuyucu.Read(tampon, 0, tampon.Length);
        var sayim = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0, ['|'] = 0 };
        bool tirnakta = false;
        for (int i = 0; i < n; i++)
        {
            char k = tampon[i];
            if (k == '"')
                tirnakta = !tirnakta;
            else if (!tirnakta && sayim.ContainsKey(k))
                sayim[k]++;
        }

        (char ayrac, int adet) = sayim.OrderByDescending(s => s.Value).Select(s => (s.Key, s.Value)).First();
        return adet == 0 ? ';' : ayrac;
    }

    /// <summary>TXT önizlemesi: kolon envanteri (tip tahminli) + ilk N satır.
    /// <paramref name="satirAyraci"/> null = CRLF/LF otomatik; doluysa EK satır sonudur.</summary>
    public static DosyaOnizleme MetinOnizle(
        string yol, char ayrac, Encoding kodlama, bool ilkSatirBaslik, CultureInfo kultur,
        char? satirAyraci = null)
    {
        using StreamReader okuyucu = new(yol, kodlama, detectEncodingFromByteOrderMarks: true);
        IReadOnlyList<string>? adlar = null;
        var satirlar = new List<object?[]>();
        bool kesildi = false;

        foreach (string[] alanlar in MetinTabloAyristirici.Ayristir(okuyucu, ayrac, satirAyraci))
        {
            if (adlar is null)
            {
                adlar = ilkSatirBaslik
                    ? KolonAdlariNormalle(alanlar)
                    : [.. Enumerable.Range(1, alanlar.Length).Select(i => $"Kolon{i}")];
                if (ilkSatirBaslik)
                    continue;
            }

            if (satirlar.Count >= EnCokOnizlemeSatiri)
            {
                kesildi = true;
                break;
            }

            satirlar.Add([.. alanlar.Select(FormulOnekiSoy)]);
        }

        adlar ??= [];
        return new DosyaOnizleme(KolonTipiTahminci.Tahmin(adlar, satirlar, kultur), satirlar, kesildi);
    }

    /// <summary>
    /// Excel'in metin öneki geri alınır (inceleme kalanı 2026-08-03): CSV dışa aktarma formül
    /// enjeksiyonuna karşı '=…' hücrelerine <c>'</c> öneki koyar (CsvYazici.FormulKoru). Aynı dosya
    /// geri İÇE aktarılırsa önek veri sanılırdı — Excel'in kendi semantiğiyle soyulur: yalnız
    /// <c>'</c> + formül karakteri (=, @, +, -) dizilişinde. Gerçek <c>'=…</c> verisi Excel'de de
    /// aynı kayba uğrar; bu bilinçli gidiş-dönüş onarımıdır.
    /// </summary>
    private static string FormulOnekiSoy(string alan)
        => alan.Length >= 2 && alan[0] == '\'' && alan[1] is '=' or '@' or '+' or '-'
            ? alan[1..] : alan;

    /// <summary>TXT'nin TAMAMINI akışla verir (S3 aktarımı) — başlık satırı atlanmış hâlde.</summary>
    public static IEnumerable<object?[]> MetinAkis(
        string yol, char ayrac, Encoding kodlama, bool ilkSatirBaslik, char? satirAyraci = null)
    {
        using StreamReader okuyucu = new(yol, kodlama, detectEncodingFromByteOrderMarks: true);
        bool ilk = true;
        foreach (string[] alanlar in MetinTabloAyristirici.Ayristir(okuyucu, ayrac, satirAyraci))
        {
            if (ilk && ilkSatirBaslik)
            {
                ilk = false;
                continue;
            }

            ilk = false;
            yield return [.. alanlar.Select(FormulOnekiSoy)]; // önizlemeyle AYNI onarım (tutarlılık)
        }
    }

    // ---- Excel ----

    /// <summary>Dosyadaki sayfa adları (ekranda sayfa seçimi).</summary>
    public static IReadOnlyList<string> ExcelSayfalari(string yol)
    {
        using FileStream akis = File.OpenRead(yol);
        using IExcelDataReader okuyucu = ExcelReaderFactory.CreateReader(akis);
        var adlar = new List<string>();
        do
        {
            adlar.Add(okuyucu.Name);
        }
        while (okuyucu.NextResult());
        return adlar;
    }

    /// <summary>Excel önizlemesi: seçili sayfadan kolon envanteri + ilk N satır.</summary>
    public static DosyaOnizleme ExcelOnizle(
        string yol, string? sayfa, bool ilkSatirBaslik, CultureInfo kultur)
    {
        IReadOnlyList<string>? adlar = null;
        var satirlar = new List<object?[]>();
        bool kesildi = false;

        foreach (object?[] degerler in ExcelAkis(yol, sayfa, ilkSatirBaslik: false))
        {
            if (adlar is null)
            {
                adlar = ilkSatirBaslik
                    ? KolonAdlariNormalle([.. degerler.Select(d => Convert.ToString(d, kultur) ?? "")])
                    : [.. Enumerable.Range(1, degerler.Length).Select(i => $"Kolon{i}")];
                if (ilkSatirBaslik)
                    continue;
            }

            if (satirlar.Count >= EnCokOnizlemeSatiri)
            {
                kesildi = true;
                break;
            }

            satirlar.Add(degerler);
        }

        adlar ??= [];
        return new DosyaOnizleme(KolonTipiTahminci.Tahmin(adlar, satirlar, kultur), satirlar, kesildi);
    }

    /// <summary>Excel sayfasının TAMAMINI akışla verir (S3) — hücreler tipli (double/DateTime/…).</summary>
    public static IEnumerable<object?[]> ExcelAkis(string yol, string? sayfa, bool ilkSatirBaslik)
    {
        using FileStream akis = File.OpenRead(yol);
        using IExcelDataReader okuyucu = ExcelReaderFactory.CreateReader(akis);

        // Sayfa seçimi: ad eşleşene dek ilerle (null = ilk sayfa).
        while (sayfa is not null && !okuyucu.Name.Equals(sayfa, StringComparison.OrdinalIgnoreCase))
        {
            if (!okuyucu.NextResult())
                throw new InvalidOperationException($"'{sayfa}' adlı sayfa dosyada yok.");
        }

        bool ilk = true;
        while (okuyucu.Read())
        {
            if (ilk && ilkSatirBaslik)
            {
                ilk = false;
                continue;
            }

            ilk = false;
            object?[] satir = new object?[okuyucu.FieldCount];
            for (int i = 0; i < okuyucu.FieldCount; i++)
            {
                object deger = okuyucu.GetValue(i);
                satir[i] = deger is DBNull ? null : deger;
            }

            yield return satir;
        }
    }

    /// <summary>Başlık satırından kolon adları: boş ad Kolon{N}; yinelenen ada _2, _3… eklenir
    /// (eşleme gridi ve CREATE adları tekil olmalı).</summary>
    private static IReadOnlyList<string> KolonAdlariNormalle(IReadOnlyList<string> ham)
    {
        var gorulen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var adlar = new List<string>(ham.Count);
        for (int i = 0; i < ham.Count; i++)
        {
            string ad = ham[i].Trim();
            if (ad.Length == 0)
                ad = $"Kolon{i + 1}";
            if (gorulen.TryGetValue(ad, out int sayi))
            {
                gorulen[ad] = sayi + 1;
                ad = $"{ad}_{sayi + 1}";
            }

            gorulen.TryAdd(ad, 1);
            adlar.Add(ad);
        }

        return adlar;
    }
}
