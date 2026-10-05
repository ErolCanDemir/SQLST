using System.Data;
using System.IO;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// CSV dışa aktarma (FG-4.3). RFC 4180 uyumlu kaçışlama; Excel'in Türkçe
/// karakterleri doğru açması için UTF-8 BOM ile yazar.
/// </summary>
public static class CsvYazici
{
    /// <summary>
    /// Sorgunun TÜM satırlarını (grid sınırına takılmadan) AKIŞLA dosyaya yazar (v6 "Tümünü
    /// dışa aktar"). Belleğe tümünü almaz: <see cref="IDbOturum.AkisAsync"/> satır satır okur,
    /// biz satır satır yazarız → 10 satır da 10 milyon da sabit bellekle. Dönen değer yazılan
    /// satır sayısıdır. <paramref name="ilerleme"/> her ~10.000 satırda bir bildirilir.
    /// </summary>
    public static async Task<long> AkislaDosyayaYazAsync(
        IDbOturum oturum, string sql, ExecuteOptions opts, string dosyaYolu,
        char ayirici = ';', IProgress<long>? ilerleme = null, CancellationToken ct = default)
    {
        // UTF-8 BOM → Excel Türkçe'yi doğru açar. AutoFlush kapalı: StreamWriter kendi tamponuyla
        // toplu disk yazar (milyonlarca satırda I/O çağrısını azaltır).
        await using var akis = new StreamWriter(
            dosyaYolu, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = false };

        long sayi = 0, sonBildirim = 0;
        long yazilan = await oturum.AkisAsync(sql, opts,
            baslik => akis.WriteLine(string.Join(ayirici, baslik.Select(k => Kacir(k, ayirici)))),
            satir =>
            {
                akis.WriteLine(string.Join(ayirici, satir.Select(d =>
                    d is null or DBNull ? "" : Kacir(SonucBicimleyici.HucreMetni(d), ayirici))));
                if (++sayi - sonBildirim >= 10_000)
                {
                    sonBildirim = sayi;
                    ilerleme?.Report(sayi); // ara ilerleme (UI'a marshallanır)
                }
            },
            ct);

        await akis.FlushAsync(ct);
        ilerleme?.Report(yazilan); // son toplam
        return yazilan;
    }

    public static string Metin(DataTable tablo, char ayirici = ';')
    {
        // Türkçe bölgesel ayarda Excel CSV ayracı ';' bekler; varsayılan bilinçli ';'
        var sb = new StringBuilder();

        sb.AppendLine(string.Join(ayirici,
            tablo.Columns.Cast<DataColumn>().Select(k => Kacir(k.ColumnName, ayirici))));

        DataColumn[] kolonlar = [.. tablo.Columns.Cast<DataColumn>()];
        foreach (DataRow satir in tablo.Rows)
        {
            // v23-S13: değer kolonun SQL tipiyle HAM yazılır (kültürsüz — "2026-10-05 14:23:11.123").
            sb.AppendLine(string.Join(ayirici, kolonlar.Select(k =>
                satir[k] is DBNull ? "" : Kacir(HamDeger.Metin(satir, k), ayirici))));
        }

        return sb.ToString();
    }

    public static async Task DosyayaYazAsync(DataTable tablo, string dosyaYolu, CancellationToken ct = default)
        => await File.WriteAllTextAsync(dosyaYolu, Metin(tablo), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);

    /// <summary>Ayırıcı, tırnak veya satır sonu içeren değerleri çift tırnağa alır; içteki tırnakları ikiler.</summary>
    private static string Kacir(string deger, char ayirici)
    {
        deger = FormulKoru(deger);
        bool gerekli = deger.Contains(ayirici) || deger.Contains('"')
                    || deger.Contains('\n') || deger.Contains('\r');
        return gerekli ? $"\"{deger.Replace("\"", "\"\"")}\"" : deger;
    }

    /// <summary>
    /// Formül enjeksiyonu koruması (inceleme 2026-07-30 bekleyeni, dengeli çözüm): Excel, CSV
    /// hücresi =, @, +, - ile başlıyorsa FORMÜL olarak çalıştırır ("=cmd|…" DDE saldırısı).
    /// Kural: '='/'@' ile başlayan HER değere, '+'/'-' ile başlayıp SAYI OLMAYAN değere Excel'in
    /// kendi "metin" öneki (') konur. Sayılar ("-5", "+3,14") dokunulmadan kalır — gidiş-dönüş
    /// (dışa aktar → içe aktar) verinin asıl gövdesinde bozulmaz; yalnız formüle benzeyen metin
    /// hücresi görünür bir ' kazanır (sessiz bozulmaya bilinçli tercih; XLSX yolu zaten tipli
    /// olduğundan bu korumaya ihtiyaç duymaz).
    /// </summary>
    private static string FormulKoru(string deger)
    {
        if (deger.Length == 0)
            return deger;

        char ilk = deger[0];
        if (ilk is not ('=' or '@' or '+' or '-'))
            return deger;
        if (ilk is '+' or '-'
            && (double.TryParse(deger, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out _)
                || double.TryParse(deger, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            return deger; // negatif/işaretli SAYI zararsızdır ve aynen kalmalı
        }

        return "'" + deger;
    }
}
