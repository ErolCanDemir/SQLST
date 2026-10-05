using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// 🔀 Veri Eşitleme script üreticisi (madde 4, kullanıcı onayı 2026-08-03: "listelenen satırları
/// seçip eşitle — birden fazla ya da tamamı"). Karşılaştırma sonucundaki fark satırlarından hedefi
/// KAYNAĞA eşitleyen DML üretir: yalnız kaynakta → INSERT · farklı → UPDATE · yalnız hedefte →
/// DELETE. SALT METİN üretir, ÇALIŞTIRMAZ — hedef ayrı bir bağlantı olabileceğinden script hedefte
/// koşulmak üzere gösterilir (Güvenli Yazma önerilir). SAF ve testli; sıra DELETE → UPDATE → INSERT.
/// </summary>
public static class VeriEsitleyici
{
    /// <summary>
    /// Eşitlenecek tek fark. <paramref name="AnahtarDegerleri"/> PK kolon→değer (WHERE için);
    /// INSERT/UPDATE'te <paramref name="Kolonlar"/>+<paramref name="KaynakSatir"/> kaynaktaki tam
    /// satırı taşır (aynı dizilim). DELETE'te kaynak satır gerekmez.
    /// </summary>
    public sealed record Kayit(
        VeriFarkTuru Tur,
        IReadOnlyDictionary<string, object?> AnahtarDegerleri,
        IReadOnlyList<string>? Kolonlar = null,
        object?[]? KaynakSatir = null);

    public static string ScriptUret(
        ILehce lehce, SemaNesnesi tablo, string hedefOzeti,
        IReadOnlyList<string> anahtarKolonlar, IReadOnlyList<Kayit> kayitlar)
    {
        LiteralKurallari lk = lehce.LiteralKurallari;
        string tamAd = string.IsNullOrEmpty(tablo.Sema)
            ? lehce.TirnaklaTanimlayici(tablo.Ad)
            : lehce.TamAdYaz(tablo.Sema, tablo.Ad);

        string Kosul(IReadOnlyDictionary<string, object?> anahtar)
            => string.Join(" AND ", anahtarKolonlar.Select(k =>
                anahtar.TryGetValue(k, out object? d) && d is not null and not DBNull
                    ? $"{lehce.TirnaklaTanimlayici(k)} = {LiteralYazici.Yaz(d, lk)}"
                    : $"{lehce.TirnaklaTanimlayici(k)} IS NULL"));

        var silmeler = new List<string>();
        var guncellemeler = new List<string>();
        var eklemeler = new List<string>();
        var atlananlar = new List<string>();

        foreach (Kayit kayit in kayitlar)
        {
            switch (kayit.Tur)
            {
                case VeriFarkTuru.YalnizSag:
                    silmeler.Add($"DELETE FROM {tamAd} WHERE {Kosul(kayit.AnahtarDegerleri)};");
                    break;

                case VeriFarkTuru.Farkli when kayit.Kolonlar is { Count: > 0 } && kayit.KaynakSatir is not null:
                {
                    var set = new List<string>();
                    for (int i = 0; i < kayit.Kolonlar.Count && i < kayit.KaynakSatir.Length; i++)
                    {
                        if (anahtarKolonlar.Contains(kayit.Kolonlar[i], StringComparer.OrdinalIgnoreCase))
                            continue; // anahtar zaten eşit — WHERE'de
                        set.Add($"{lehce.TirnaklaTanimlayici(kayit.Kolonlar[i])} = {LiteralYazici.Yaz(kayit.KaynakSatir[i], lk)}");
                    }
                    if (set.Count > 0)
                        guncellemeler.Add($"UPDATE {tamAd} SET {string.Join(", ", set)} WHERE {Kosul(kayit.AnahtarDegerleri)};");
                    break;
                }

                case VeriFarkTuru.YalnizSol when kayit.Kolonlar is { Count: > 0 } && kayit.KaynakSatir is not null:
                {
                    string kolonListesi = string.Join(", ", kayit.Kolonlar.Select(lehce.TirnaklaTanimlayici));
                    string degerler = string.Join(", ",
                        kayit.KaynakSatir.Take(kayit.Kolonlar.Count).Select(d => LiteralYazici.Yaz(d, lk)));
                    eklemeler.Add($"INSERT INTO {tamAd} ({kolonListesi}) VALUES ({degerler});");
                    break;
                }

                default:
                    // Kaynak satırı çekilememiş INSERT/UPDATE — sessiz eksik script yerine açık not.
                    atlananlar.Add($"-- ⚠ atlandı ({kayit.Tur}): {Kosul(kayit.AnahtarDegerleri)} — kaynak satır okunamadı.");
                    break;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"-- 🔀 Veri Eşitleme · tablo: {tablo.TamAd} · yön: KAYNAK → HEDEF");
        sb.AppendLine($"-- HEDEF: {hedefOzeti} — script'i HEDEF bağlantıda çalıştırın (burada ÇALIŞTIRILMADI).");
        sb.AppendLine($"-- {silmeler.Count} silme · {guncellemeler.Count} güncelleme · {eklemeler.Count} ekleme"
            + (atlananlar.Count > 0 ? $" · ⚠ {atlananlar.Count} atlanan" : ""));
        sb.AppendLine("-- Sıra: DELETE → UPDATE → INSERT. FK bağları ya da kimlik (identity/serial) kolonu varsa");
        sb.AppendLine("-- sırayı/kimlik eklemeyi gözden geçirin. Güvenli Yazma AÇIKKEN koşmanız önerilir.");
        sb.AppendLine();
        foreach (string s in atlananlar) sb.AppendLine(s);
        foreach (string s in silmeler) sb.AppendLine(s);
        foreach (string s in guncellemeler) sb.AppendLine(s);
        foreach (string s in eklemeler) sb.AppendLine(s);
        return sb.ToString().TrimEnd();
    }
}
