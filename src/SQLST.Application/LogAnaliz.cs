using System.Text.RegularExpressions;

namespace SQLST.Application;

/// <summary>Ayrıştırılmış tek log kaydı (başlık satırı): zaman + seviye + mesaj.</summary>
public sealed record LogKaydi(string Zaman, string Seviye, string Mesaj);

/// <summary>Aynı imzaya düşen kayıtların özeti: kaç kez, örnek mesaj, son görülme.</summary>
public sealed record LogGrup(string Imza, int Sayi, string Seviye, string OrnekMesaj, string SonZaman);

/// <summary>
/// Serilog LOG DOSYASI analizi (SAF). NOT (2026-07-23): Log Analizi PENCERESİ artık bunu değil,
/// veritabanı tablolarını analiz eden <see cref="LogTabloAnaliz"/>'i kullanır (kullanıcının asıl
/// istediği exception-log TABLOLARIYDI — ilk yorum yanlıştı). Dosya ayrıştırma UI'sız saf motor
/// olarak durur; <see cref="Imza"/> normalizasyonu tablo analizinde de ortak kullanılır.
/// </summary>
public static partial class LogAnaliz
{
    // Serilog File sink varsayılan başlığı: "2026-07-23 14:30:00.123 +03:00 [ERR] mesaj"
    [GeneratedRegex(@"^(?<z>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})[.\d]* [+\-]\d{2}:\d{2} \[(?<s>\w+)\] (?<m>.*)$")]
    private static partial Regex BaslikDeseni();

    /// <summary>Log satırlarını kayıtlara ayrıştırır. Devam satırları (exception yığını) başlığa aittir,
    /// imza için başlık mesajı kullanılır. Zaman sırası korunur.</summary>
    public static IReadOnlyList<LogKaydi> Ayristir(IEnumerable<string> satirlar)
    {
        var sonuc = new List<LogKaydi>();
        foreach (string satir in satirlar)
        {
            Match m = BaslikDeseni().Match(satir);
            if (m.Success)
                sonuc.Add(new LogKaydi(m.Groups["z"].Value, m.Groups["s"].Value, m.Groups["m"].Value.Trim()));
            // başlık değilse (exception devamı) — atlanır; imza başlık mesajından üretilir
        }
        return sonuc;
    }

    /// <summary>Değişken kısımları eleyerek imza üretir: GUID → &lt;id&gt;, 'x'/[x]/"x" → &lt;x&gt;, sayı → #.
    /// Böylece "Tablo [A] yok" ile "Tablo [B] yok" aynı gruba düşer.</summary>
    public static string Imza(string mesaj)
    {
        string s = GuidDeseni().Replace(mesaj, "<id>");
        s = TirnakliDeseni().Replace(s, "<x>");
        s = KoseliDeseni().Replace(s, "<x>");
        s = CiftTirnakliDeseni().Replace(s, "<x>");
        s = SayiDeseni().Replace(s, "#");
        return WhitespaceDeseni().Replace(s, " ").Trim();
    }

    // Değişken span yer tutucusu (LIKE'ta '%', regex'te '.*' olur). Yalnız HARF içerir — meta-karakter
    // kaçışından ve regex kaçışından etkilenmez; gerçek log metninde görülmesi olanaksız denecek kadar nadir.
    private const string DesenYer = "zzSQLSTVARzz";

    /// <summary>
    /// <see cref="Imza"/>'nın SQL LIKE ikizi (v20-S3): mesajı bir <b>LIKE kalıbına</b> çevirir — aynı
    /// değişken span'lar (GUID / 'x' / [x] / "x" / sayı) tek <c>%</c> olur, sabit iskelet korunur.
    /// Böylece "sample'da 342×" yerine <c>WHERE msg LIKE kalıp</c> ile TÜM tabloda GERÇEK sayım alınır;
    /// aynı kalıp detay (ham satırlar) ve trend için de kullanılır. Kaçış karakteri <c>!</c>'tir
    /// (ters bölü DEĞİL — MySQL string-literal ters-bölüyü ayrıca işler, dosya yollarında tuzak olurdu);
    /// yalnız LIKE meta-karakterleri (<c>% _ !</c>) kaçırılır. Sorguya gömen taraf tek tırnağı ikizler
    /// ve <c>ESCAPE '!'</c> ekler. NOT: kalıp <b>yaklaşıktır</b> (over-match olabilir) — "gerçek toplam"
    /// bilinçli olarak "bu desene uyan toplam"dır.
    /// </summary>
    public static string LikeDeseni(string mesaj)
    {
        string s = DegiskenleriYerTutucuYap(mesaj);
        // LIKE meta-karakter kaçışı (kaçış = '!'): önce '!' kendini, sonra % ve _.
        s = s.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
        s = s.Replace(DesenYer, "%");
        return CokluYuzdeDeseni().Replace(s, "%"); // ardışık joker daralt: "%%" → "%"
    }

    /// <summary><see cref="LikeDeseni"/>'nin Mongo ikizi: mesajı bir <b>regex</b> kalıbına çevirir —
    /// sabit parçalar regex-kaçışlı, değişken span'lar <c>.*</c>. Bağlayıcı değil (contains); çağıran
    /// <c>$regex</c> ile kullanır. Ardışık <c>.*</c> daraltılır.</summary>
    public static string RegexDeseni(string mesaj)
    {
        string s = DegiskenleriYerTutucuYap(mesaj);
        s = RegexKacisDeseni().Replace(s, "\\$0");     // regex meta-karakterlerini kaçır (yer tutucu harf → etkilenmez)
        s = s.Replace(DesenYer, ".*");
        return CokluYildizDeseni().Replace(s, ".*");   // ardışık ".*" daralt
    }

    /// <summary>Değişken span'ları (GUID/'x'/[x]/"x"/sayı) tek yer tutucuya indirger + boşlukları toplar.
    /// LikeDeseni ve RegexDeseni'nin ortak ön adımı (Imza ile aynı desenler).</summary>
    private static string DegiskenleriYerTutucuYap(string mesaj)
    {
        string s = GuidDeseni().Replace(mesaj, DesenYer);
        s = TirnakliDeseni().Replace(s, DesenYer);
        s = KoseliDeseni().Replace(s, DesenYer);
        s = CiftTirnakliDeseni().Replace(s, DesenYer);
        s = SayiDeseni().Replace(s, DesenYer);
        return WhitespaceDeseni().Replace(s, " ").Trim();
    }

    /// <summary>
    /// İmzaya göre gruplayıp en çok tekrarlayanları döner (azalan sayı, sonra en yeni). Varsayılan
    /// yalnız HATA/ölümcül kayıtlar (patlama tespiti için asıl istenen). En fazla <paramref name="enFazla"/>.
    /// </summary>
    public static IReadOnlyList<LogGrup> EnCokTekrarlayan(
        IReadOnlyList<LogKaydi> kayitlar, int enFazla = 20, bool yalnizHata = true)
    {
        IEnumerable<LogKaydi> kaynak = yalnizHata
            ? kayitlar.Where(k => k.Seviye is "ERR" or "FTL" or "Error" or "Fatal")
            : kayitlar;

        return [.. kaynak
            .GroupBy(k => Imza(k.Mesaj))
            .Select(g => new LogGrup(
                Imza: g.Key,
                Sayi: g.Count(),
                Seviye: g.First().Seviye,
                OrnekMesaj: g.First().Mesaj,
                SonZaman: g.Max(k => k.Zaman) ?? ""))
            .OrderByDescending(g => g.Sayi)
            .ThenByDescending(g => g.SonZaman)
            .Take(enFazla)];
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}")]
    private static partial Regex GuidDeseni();
    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex TirnakliDeseni();
    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex KoseliDeseni();
    [GeneratedRegex(@"""[^""]*""")]
    private static partial Regex CiftTirnakliDeseni();
    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex SayiDeseni();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceDeseni();
    [GeneratedRegex("%+")]
    private static partial Regex CokluYuzdeDeseni();
    [GeneratedRegex(@"(?:\.\*)+")]
    private static partial Regex CokluYildizDeseni();
    [GeneratedRegex(@"[.^$*+?()\[\]{}|\\]")]
    private static partial Regex RegexKacisDeseni();
}
