using System.Globalization;
using System.Text;

namespace SQLST.Application;

/// <summary>
/// Veritabanı haritasını SAF olarak SVG'ye çeker (v9-S4 dışa aktarma). Vektör çıktı: kart düğümler
/// (rect + başlık + kolon satırları) + FK bağları (bezier + ok). Konumlar dışarıdan gelir (tuvaldeki
/// güncel yerleşim). Tamamen metin — UI/IO yok; test edilebilir. Açık/koyu tema paleti seçilebilir.
/// </summary>
public static class HaritaSvg
{
    private const double Genislik = 220, BaslikYuksekligi = 34, SatirYuksekligi = 24, Pay = 40;

    // Şema şeridi paleti (ekrandaki HaritaDugumGorunumu ile aynı sıralı renkler).
    private static readonly string[] SemaRenkleri =
        ["#26A69A", "#5AA9EE", "#B08BE0", "#E0A94E", "#E07A8A", "#66BB6A"];

    private sealed record Palet(
        string Zemin, string Kart, string Kenar, string Metin, string Soluk, string Baglanti,
        string Baslik, string Pk, string Fk);

    private static readonly Palet Koyu = new("#161a1e", "#232a30", "#333b42", "#e4e6e8", "#8a939c",
        "#8a939c", "#1f262b", "#f2c14e", "#5fc2b5");
    private static readonly Palet Acik = new("#f5f7f9", "#ffffff", "#dae0e5", "#1e262d", "#67727c",
        "#98a2ab", "#f0f2f4", "#b7791f", "#0e8074");

    public static string Uret(HaritaModeli model, IReadOnlyDictionary<string, Nokta> konum, bool koyu = false)
    {
        Palet p = koyu ? Koyu : Acik;

        double Yuk(HaritaDugumu d) => BaslikYuksekligi + d.Kolonlar.Count * SatirYuksekligi;
        Nokta Yer(string tamAd) => konum.TryGetValue(tamAd, out Nokta? n) ? n : new Nokta(0, 0);

        double enX = model.Dugumler.Count == 0 ? 400 : model.Dugumler.Max(d => Yer(d.TamAd).X + Genislik) + Pay;
        double enY = model.Dugumler.Count == 0 ? 300 : model.Dugumler.Max(d => Yer(d.TamAd).Y + Yuk(d)) + Pay;

        var sb = new StringBuilder();
        sb.Append(Inv($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{enX:F0}\" height=\"{enY:F0}\" "))
          .Append(Inv($"viewBox=\"0 0 {enX:F0} {enY:F0}\" font-family=\"Segoe UI, sans-serif\">\n"))
          .Append(Inv($"<rect width=\"{enX:F0}\" height=\"{enY:F0}\" fill=\"{p.Zemin}\"/>\n"));

        // ── Kenarlar (arkada) ──
        foreach (HaritaKenari e in model.Kenarlar)
        {
            if (!konum.ContainsKey(e.KaynakTamAd) || !konum.ContainsKey(e.HedefTamAd))
                continue;
            HaritaDugumu? kd = model.Dugumler.FirstOrDefault(d => d.TamAd == e.KaynakTamAd);
            HaritaDugumu? hd = model.Dugumler.FirstOrDefault(d => d.TamAd == e.HedefTamAd);
            if (kd is null || hd is null)
                continue;

            Nokta kn = Yer(e.KaynakTamAd), hn = Yer(e.HedefTamAd);
            bool hedefSagda = kn.X + Genislik / 2 < hn.X + Genislik / 2;
            double sx = hedefSagda ? kn.X + Genislik : kn.X;
            double tx = hedefSagda ? hn.X : hn.X + Genislik;
            double sy = kn.Y + Yuk(kd) / 2, ty = hn.Y + Yuk(hd) / 2;
            double dx = Math.Max(50, Math.Abs(tx - sx) * 0.4);
            double c1 = hedefSagda ? sx + dx : sx - dx, c2 = hedefSagda ? tx - dx : tx + dx;
            double yon = hedefSagda ? 1 : -1;

            sb.Append(Inv($"<path d=\"M {sx:F1} {sy:F1} C {c1:F1} {sy:F1} {c2:F1} {ty:F1} {tx:F1} {ty:F1}\" "))
              .Append(Inv($"fill=\"none\" stroke=\"{p.Baglanti}\" stroke-width=\"1.6\"/>\n"))
              .Append(Inv($"<path d=\"M {tx - yon * 9:F1} {ty - 5:F1} L {tx:F1} {ty:F1} L {tx - yon * 9:F1} {ty + 5:F1}\" "))
              .Append(Inv($"fill=\"none\" stroke=\"{p.Baglanti}\" stroke-width=\"1.6\"/>\n"));
        }

        // ── Kartlar (önde) ──
        foreach (HaritaDugumu d in model.Dugumler)
        {
            Nokta n = Yer(d.TamAd);
            double yuk = Yuk(d);
            string serit = SemaRenkleri[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(d.Sema) % (uint)SemaRenkleri.Length)];

            sb.Append(Inv($"<g transform=\"translate({n.X:F1},{n.Y:F1})\">\n"))
              .Append(Inv($"<rect width=\"{Genislik:F0}\" height=\"{yuk:F1}\" rx=\"7\" fill=\"{p.Kart}\" stroke=\"{p.Kenar}\"/>\n"))
              .Append(Inv($"<path d=\"M0 7 a7 7 0 0 1 7 -7 h206 a7 7 0 0 1 7 7 v27 h-220 z\" fill=\"{p.Baslik}\"/>\n"))
              .Append(Inv($"<rect width=\"3\" height=\"{yuk:F1}\" rx=\"1.5\" fill=\"{serit}\"/>\n"))
              .Append(Inv($"<circle cx=\"13\" cy=\"17\" r=\"4.5\" fill=\"{serit}\"/>\n"))
              .Append(Inv($"<text x=\"24\" y=\"21\" fill=\"{p.Metin}\" font-size=\"12.5\" font-weight=\"600\">{Kacir(d.Ad)}</text>\n"))
              .Append(Inv($"<text x=\"{Genislik - 8:F0}\" y=\"21\" fill=\"{p.Soluk}\" font-size=\"10.5\" text-anchor=\"end\">{Kacir(d.Sema)}</text>\n"));

            double y = BaslikYuksekligi;
            foreach (HaritaKolonu k in d.Kolonlar)
            {
                double ty2 = y + 16;
                (string glif, string renk) = k.PkMi ? ("🔑", p.Pk) : k.FkMi ? ("🔗", p.Fk) : ("·", p.Soluk);
                sb.Append(Inv($"<text x=\"11\" y=\"{ty2:F0}\" fill=\"{renk}\" font-size=\"11\" text-anchor=\"middle\">{glif}</text>\n"))
                  .Append(Inv($"<text x=\"24\" y=\"{ty2:F0}\" fill=\"{p.Metin}\" font-size=\"12\">{Kacir(k.Ad)}</text>\n"))
                  .Append(Inv($"<text x=\"{Genislik - 10:F0}\" y=\"{ty2:F0}\" fill=\"{p.Soluk}\" font-size=\"10.5\" text-anchor=\"end\">{Kacir(k.Tip)}</text>\n"));
                y += SatirYuksekligi;
            }
            sb.Append("</g>\n");
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string Kacir(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
