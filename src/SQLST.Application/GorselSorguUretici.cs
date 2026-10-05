using System.Globalization;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Görsel Sorgu Tasarımcısı'nın (v6) SQL üreten çekirdeği — WPF'siz, saf mantık.
///
/// <b>S1:</b> yalnız <c>SELECT * FROM …</c> (bağsız tablolar CROSS JOIN).
/// <b>S2:</b> tablolar arası <b>JOIN</b> (INNER/LEFT/RIGHT/FULL) + ON eşleşmesi. WHERE S3.
///
/// <b>Tırnaklama motora devredilir</b> (<see cref="ILehce.TirnaklaTanimlayici"/>): MSSQL
/// <c>[ad]</c>, PostgreSQL/Oracle <c>"ad"</c>, MySQL <c>`ad`</c>. Kolonlar tam nitelenir
/// (<c>şema.tablo.kolon</c>) — takma ad üretilmez (S1 tekilleştirmesi self-join'i zaten
/// engeller, ambiguity çıkmaz).
///
/// <b>FROM zinciri algoritması:</b> ilk tablo FROM çapasıdır. Bir ucu tanıtılmış, diğeri
/// tanıtılmamış bir bağ bulundukça yeni tablo JOIN edilir (gerekirse LEFT↔RIGHT çevrilerek).
/// Hiçbir bağla ulaşılamayan tablo <c>CROSS JOIN</c> ile köprülenir — böylece kopuk
/// bileşenler de dürüstçe (Kartezyen) birleşir, sessizce kaybolmaz.
/// </summary>
public static class GorselSorguUretici
{
    public static string Uret(
        ILehce lehce, IReadOnlyList<SemaNesnesi> tablolar,
        IReadOnlyList<GorselJoin>? joinler = null, IReadOnlyList<GorselKosul>? kosullar = null,
        IReadOnlyList<GorselKolonAlani>? secilenler = null)
    {
        if (tablolar.Count == 0)
            return "";

        var sb = new StringBuilder();

        // SELECT listesi (S4): kolon seçilmişse yalnız onlar (tam nitelenmiş); yoksa *.
        if (secilenler is { Count: > 0 })
        {
            sb.Append("SELECT ");
            sb.Append(string.Join(", ", secilenler.Select(s =>
                $"{TamAd(lehce, s.Tablo)}.{lehce.TirnaklaTanimlayici(s.Kolon)}")));
            sb.Append("\nFROM ");
        }
        else
        {
            sb.Append("SELECT *\nFROM ");
        }

        sb.Append(TamAd(lehce, tablolar[0]));

        var tanitilan = new HashSet<string>(StringComparer.Ordinal) { tablolar[0].TamAd };
        var kalan = new List<GorselJoin>(joinler ?? []);

        // Tüm tablolar FROM zincirine girene dek: önce bağla (JOIN), olmazsa köprüyle (CROSS JOIN).
        while (tanitilan.Count < tablolar.Count)
        {
            int uygulanan = -1;
            for (int i = 0; i < kalan.Count; i++)
            {
                GorselJoin j = kalan[i];
                bool solVar = tanitilan.Contains(j.Sol.TamAd);
                bool sagVar = tanitilan.Contains(j.Sag.TamAd);

                if (solVar == sagVar)
                    continue; // ikisi de var (döngü/mükerrer → yok say) ya da ikisi de yok (henüz ulaşılamaz)

                // Yeni taraf her zaman "Sag" olsun: Sol tanıtılmışsa doğrudan, değilse ters çevir.
                GorselJoin yaz = sagVar ? Ters(j) : j;
                JoinYaz(sb, lehce, yaz);
                tanitilan.Add(yaz.Sag.TamAd);
                uygulanan = i;
                break;
            }

            if (uygulanan >= 0)
            {
                kalan.RemoveAt(uygulanan);
                continue;
            }

            // Bağla ulaşılamadı → sıradaki tanıtılmamış tabloyu CROSS JOIN ile köprüle.
            SemaNesnesi kopru = tablolar.First(t => !tanitilan.Contains(t.TamAd));
            sb.Append("\n    CROSS JOIN ").Append(TamAd(lehce, kopru));
            tanitilan.Add(kopru.TamAd);
        }

        // WHERE (S3): koşullar sırayla; her biri bir önceki'ye AND/OR ile bağlanır (düz — SQL'in
        // AND-önce-OR önceliğiyle değerlendirilir; gruplama S3 sonrası bir konudur).
        if (kosullar is { Count: > 0 })
        {
            sb.Append("\nWHERE ");
            for (int i = 0; i < kosullar.Count; i++)
            {
                if (i > 0)
                    sb.Append(kosullar[i].VeyaMi ? "\n   OR " : "\n  AND ");
                KosulYaz(sb, lehce, kosullar[i]);
            }
        }

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>Tek bir WHERE koşulu: <c>tablo.kolon OP değer</c> (LIKE/IS NULL dahil).</summary>
    private static void KosulYaz(StringBuilder sb, ILehce lehce, GorselKosul k)
    {
        string kolonRef = $"{TamAd(lehce, k.Tablo)}.{lehce.TirnaklaTanimlayici(k.Kolon)}";

        switch (k.Operator)
        {
            case KosulOperatoru.Bos:
                sb.Append(kolonRef).Append(" IS NULL");
                return;
            case KosulOperatoru.DoluDegil:
                sb.Append(kolonRef).Append(" IS NOT NULL");
                return;
            case KosulOperatoru.Icerir:
                sb.Append(kolonRef).Append(" LIKE ").Append(LiteralYazici.Yaz($"%{k.Deger}%", lehce.LiteralKurallari));
                return;
            case KosulOperatoru.Baslar:
                sb.Append(kolonRef).Append(" LIKE ").Append(LiteralYazici.Yaz($"{k.Deger}%", lehce.LiteralKurallari));
                return;
            case KosulOperatoru.Biter:
                sb.Append(kolonRef).Append(" LIKE ").Append(LiteralYazici.Yaz($"%{k.Deger}", lehce.LiteralKurallari));
                return;
        }

        string op = k.Operator switch
        {
            KosulOperatoru.Esit => "=",
            KosulOperatoru.Esitsiz => "<>",
            KosulOperatoru.Buyuk => ">",
            KosulOperatoru.Kucuk => "<",
            KosulOperatoru.BuyukEsit => ">=",
            KosulOperatoru.KucukEsit => "<=",
            _ => "=",
        };
        sb.Append(kolonRef).Append(' ').Append(op).Append(' ').Append(DegerYaz(k.Deger, lehce.LiteralKurallari));
    }

    /// <summary>
    /// Kullanıcı metnini güvenli SQL değerine çevirir: sayıya çözülüyorsa TIRNAKSIZ (invariant),
    /// değilse motor tırnağıyla METİN literali (kaçışlı). Böylece <c>= 5</c> ve <c>= 'Ali'</c>
    /// doğru ayrışır; asla ham enjeksiyon olmaz (metin daima kaçışlanır).
    /// </summary>
    private static string DegerYaz(string metin, LiteralKurallari kurallar)
    {
        if (long.TryParse(metin, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            return l.ToString(CultureInfo.InvariantCulture);
        // AllowThousands KAPALI (inceleme 2026-07-30): NumberStyles.Number "3,14"ü binlik ayraçlı
        // 314'e çözüyordu → sessiz yanlış WHERE. Virgüllü/belirsiz giriş metin literaline düşer.
        if (decimal.TryParse(metin, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
            return d.ToString(CultureInfo.InvariantCulture);
        return LiteralYazici.Yaz(metin, kurallar);
    }

    /// <summary>Bir join'i yazar: <c>&lt;TÜR&gt; JOIN sag [ON sol.k = sag.k AND …]</c>.</summary>
    private static void JoinYaz(StringBuilder sb, ILehce lehce, GorselJoin j)
    {
        sb.Append("\n    ").Append(JoinKelimesi(j.Tur)).Append(' ').Append(TamAd(lehce, j.Sag));

        if (j.Tur == JoinTuru.Cross || j.Kolonlar.Count == 0)
            return; // CROSS ya da eşleşme yok → ON yazılmaz

        sb.Append(" ON ");
        for (int i = 0; i < j.Kolonlar.Count; i++)
        {
            if (i > 0)
                sb.Append(" AND ");
            GorselKolonEsi k = j.Kolonlar[i];
            sb.Append(TamAd(lehce, j.Sol)).Append('.').Append(lehce.TirnaklaTanimlayici(k.SolKolon));
            sb.Append(" = ");
            sb.Append(TamAd(lehce, j.Sag)).Append('.').Append(lehce.TirnaklaTanimlayici(k.SagKolon));
        }
    }

    private static string JoinKelimesi(JoinTuru tur) => tur switch
    {
        JoinTuru.Inner => "INNER JOIN",
        JoinTuru.Left => "LEFT JOIN",
        JoinTuru.Right => "RIGHT JOIN",
        JoinTuru.Full => "FULL OUTER JOIN",
        JoinTuru.Cross => "CROSS JOIN",
        _ => "INNER JOIN",
    };

    /// <summary>
    /// Bağı ters çevirir: yeni taraf Sag olsun diye Sol↔Sag, LEFT↔RIGHT ve kolon çiftleri
    /// takas edilir (INNER/FULL/CROSS yönsüzdür, tür değişmez).
    /// </summary>
    private static GorselJoin Ters(GorselJoin j) => new(
        j.Sag, j.Sol,
        j.Tur switch { JoinTuru.Left => JoinTuru.Right, JoinTuru.Right => JoinTuru.Left, var t => t },
        [.. j.Kolonlar.Select(k => new GorselKolonEsi(k.SagKolon, k.SolKolon))]);

    /// <summary>Şema.ad — her ikisi de motor tırnağıyla. Şema boşsa yalnız ad.</summary>
    private static string TamAd(ILehce lehce, SemaNesnesi tablo)
        => string.IsNullOrEmpty(tablo.Sema)
            ? lehce.TirnaklaTanimlayici(tablo.Ad)
            : $"{lehce.TirnaklaTanimlayici(tablo.Sema)}.{lehce.TirnaklaTanimlayici(tablo.Ad)}";
}
