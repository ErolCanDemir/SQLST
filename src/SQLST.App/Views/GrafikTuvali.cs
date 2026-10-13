using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>Tuvaldeki tıklanabilir/ipuçlu bölge: çubuk dikdörtgeni, çizgi noktası (8×8) ya da pasta dilimi (geometri).</summary>
public sealed record GrafikVurusu(Rect Alan, Geometry? Sekil, int Kategori, int Seri, double Deger)
{
    public bool Icerir(Point p) => Sekil is not null ? Sekil.FillContains(p) : Alan.Contains(p);
}

/// <summary>
/// 📊 Grafik tuvali (v23-S24 S1, K2 — elle çizim, kütüphane yok): <see cref="GrafikModeli"/>'ni OnRender'da
/// çizer — çubuk · yatay çubuk · çizgi · alan · pasta/halka. Renkler paletten (seri renkleri Ikon* fırçaları,
/// metin/ızgara/kenar fırçaları) — tema değişince <see cref="TemaIzleyiciProperty"/> DynamicResource ile
/// yeniden çözülür ve tuval kendini yeniden çizer. S2: çizim sırasında vuruş bölgeleri toplanır → üzerine
/// gelince ipucu (kategori · seri · değer), tıklayınca <see cref="Tiklandi"/> (panel grid'de satırları seçer);
/// yığılmış çubuk. Eksen/etiket kararları burada; veri kararları (toplama, tavan) SAF çekirdekte.
/// </summary>
public sealed class GrafikTuvali : FrameworkElement
{
    public static readonly string[] SeriAnahtarlari =
        ["IkonMavi", "IkonTeal", "IkonAmber", "IkonMor", "IkonGul", "IkonYesil", "IkonIndigo", "IkonPembe", "IkonSari"];

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(GrafikModeli), typeof(GrafikTuvali),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Palet nöbetçisi: MetinFircasi'na DynamicResource bağı — palet takasında değer değişir → yeniden çizim.</summary>
    public static readonly DependencyProperty TemaIzleyiciProperty = DependencyProperty.Register(
        nameof(TemaIzleyici), typeof(Brush), typeof(GrafikTuvali),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Yazi = new("Segoe UI");
    private static readonly Typeface YaziKalin = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly List<GrafikVurusu> _vuruslar = [];
    private readonly ToolTip _ipucu;
    private GrafikVurusu? _sonVurus;

    /// <summary>Tıklanan çubuk/nokta/dilim — panel bunu grid'de satır seçimine çevirir (S2).</summary>
    public event Action<GrafikVurusu>? Tiklandi;

    public GrafikTuvali()
    {
        SetResourceReference(TemaIzleyiciProperty, "MetinFircasi");
        ClipToBounds = true;
        _ipucu = new ToolTip { PlacementTarget = this, Placement = PlacementMode.Relative, StaysOpen = true };
    }

    public GrafikModeli? Model
    {
        get => (GrafikModeli?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public Brush? TemaIzleyici
    {
        get => (Brush?)GetValue(TemaIzleyiciProperty);
        set => SetValue(TemaIzleyiciProperty, value);
    }

    /// <summary>Son çizimde toplanan vuruş bölgeleri (test ve panel için).</summary>
    public IReadOnlyList<GrafikVurusu> Vuruslar => _vuruslar;

    /// <summary>Son gösterilen ipucu metni (test için).</summary>
    public string? IpucuMetni { get; private set; }

    /// <summary>Noktadaki bölge — çubuk/dilim/nokta; yoksa null.</summary>
    public GrafikVurusu? Vur(Point p)
    {
        for (int i = _vuruslar.Count - 1; i >= 0; i--)
            if (_vuruslar[i].Icerir(p))
                return _vuruslar[i];
        return null;
    }

    /// <summary>Vuruşun ipucu metni: "Kategori · Seri: değer" (pastada yüzde de).</summary>
    public string IpucuMetniUret(GrafikVurusu v)
    {
        GrafikModeli? m = Model;
        if (m is null) return "";
        string kat = m.Kategoriler[v.Kategori];
        string deger = SayiMetni(v.Deger, 0.01);
        if (m.Tur == GrafikTuru.Pasta)
        {
            double toplam = m.Seriler[0].Degerler.Where(d => d is > 0).Sum(d => d!.Value);
            string yuzde = toplam > 0 ? $" (%{(v.Deger / toplam * 100).ToString("0.#", CultureInfo.CurrentCulture)})" : "";
            return $"{kat}: {deger}{yuzde}";
        }
        return m.Seriler.Count > 1 ? $"{kat} · {m.Seriler[v.Seri].Ad}: {deger}" : $"{kat}: {deger}";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        GrafikVurusu? v = Vur(p);
        if (v is null)
        {
            IpucuGizle();
            return;
        }
        Cursor = Cursors.Hand;
        if (!ReferenceEquals(v, _sonVurus))
        {
            _sonVurus = v;
            IpucuMetni = IpucuMetniUret(v);
            _ipucu.Content = IpucuMetni;
        }
        _ipucu.HorizontalOffset = p.X + 14;
        _ipucu.VerticalOffset = p.Y + 18;
        if (!_ipucu.IsOpen) _ipucu.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        IpucuGizle();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Vur(e.GetPosition(this)) is { } v)
            Tiklandi?.Invoke(v);
    }

    private void IpucuGizle()
    {
        _sonVurus = null;
        IpucuMetni = null;
        Cursor = null;
        if (_ipucu.IsOpen) _ipucu.IsOpen = false;
    }

    /// <summary>Seri rengi — uygulama paletinden; test/tasarım ortamında (palet yok) sabit yedek.</summary>
    public static Brush SeriFircasi(int indeks)
    {
        if (indeks < 0) return Brushes.Transparent;
        string anahtar = SeriAnahtarlari[indeks % SeriAnahtarlari.Length];
        return System.Windows.Application.Current?.TryFindResource(anahtar) as Brush ?? Brushes.SteelBlue;
    }

    private Brush F(string anahtar, Color yedek) => TryFindResource(anahtar) as Brush ?? new SolidColorBrush(yedek);

    private FormattedText Metin(string s, double boyut, Brush firca, bool kalin = false)
        => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, kalin ? YaziKalin : Yazi, boyut, firca,
               VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        _vuruslar.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        // Fare olayları için görünmez zemin (şeffaf alan vuruş almaz)
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        Brush soluk = F("SolukMetinFircasi", Colors.Gray);

        if (Model is not { } m || m.BosMu)
        {
            FormattedText bos = Metin(Model?.Uyari ?? "Grafik için X ve Y kolonlarını seçin.", 12.5, soluk);
            bos.MaxTextWidth = w - 40;
            dc.DrawText(bos, new Point((w - bos.Width) / 2, (h - bos.Height) / 2));
            return;
        }

        var f = new Fircalar(F("MetinFircasi", Colors.Black), soluk, F("GridCizgiFircasi", Colors.LightGray), F("KenarFircasi", Colors.Silver));
        switch (m.Tur)
        {
            case GrafikTuru.Pasta: Pasta(dc, m, f, w, h); break;
            case GrafikTuru.YatayCubuk: YatayCubuk(dc, m, f, w, h); break;
            default: Dikey(dc, m, f, w, h); break;
        }
    }

    private readonly record struct Fircalar(Brush Metin, Brush Soluk, Brush Izgara, Brush Kenar);

    // ---- eksen yardımcıları ----

    /// <summary>"Güzel" adım: 1·2·5·10 katları — 5 civarı çizgi.</summary>
    private static double GuzelAdim(double aralik)
    {
        if (aralik <= 0) return 1;
        double kaba = aralik / 5, buyukluk = Math.Pow(10, Math.Floor(Math.Log10(kaba)));
        double norm = kaba / buyukluk;
        return (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * buyukluk;
    }

    private static (double Alt, double Ust, double Adim) Eksen(GrafikModeli m)
    {
        (double min, double max) = m.DegerAraligi();
        if (max == min) max = min + 1;
        double adim = GuzelAdim(max - min);
        return (Math.Floor(min / adim) * adim, Math.Ceiling(max / adim) * adim, adim);
    }

    private static string SayiMetni(double v, double adim)
        => v.ToString(adim >= 1 ? "#,0" : adim >= 0.1 ? "#,0.0" : "#,0.##", CultureInfo.CurrentCulture);

    private static string Kisalt(string s) => s.Length > 18 ? s[..17] + "…" : s;

    // ---- dikey: çubuk · çizgi · alan ----

    private void Dikey(DrawingContext dc, GrafikModeli m, Fircalar f, double w, double h)
    {
        (double alt, double ust, double adim) = Eksen(m);
        int n = m.Kategoriler.Count, s = m.Seriler.Count;
        bool yigin = m.Yigilmis && m.Tur == GrafikTuru.Cubuk;
        bool degerEtiketi = m.Tur == GrafikTuru.Cubuk && n <= 20 && (yigin || n * s <= 40);

        // X etiketleri önce ölçülür: çizgi/alanda ilk/son nokta kenara oturduğundan etiket yarısı kadar pay gerekir.
        double enGenis = 0;
        List<FormattedText> etiketler = [];
        foreach (string k in m.Kategoriler)
        {
            FormattedText t = Metin(Kisalt(k), 11, f.Metin);
            etiketler.Add(t);
            enGenis = Math.Max(enGenis, t.Width);
        }
        bool noktaMerkezli = m.Tur != GrafikTuru.Cubuk;

        double solPay = 0;
        for (double v = alt; v <= ust + adim / 2; v += adim)
            solPay = Math.Max(solPay, Metin(SayiMetni(v, adim), 11, f.Soluk).Width);
        solPay += 12;
        if (noktaMerkezli)
            solPay = Math.Max(solPay, enGenis / 2 + 4);
        double ustPay = degerEtiketi ? 18 : 8, altPay = 22, sagPay = noktaMerkezli ? Math.Max(10, enGenis / 2 + 4) : 10;
        double px = solPay, py = ustPay, pw = w - solPay - sagPay, ph = h - ustPay - altPay;
        if (pw < 20 || ph < 20) return;
        double Y(double v) => py + (ust - v) / (ust - alt) * ph;

        // ızgara + değer etiketleri
        var izgaraKalem = new Pen(f.Izgara, 1);
        for (double v = alt; v <= ust + adim / 2; v += adim)
        {
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(izgaraKalem, new Point(px, y), new Point(px + pw, y));
            FormattedText t = Metin(SayiMetni(v, adim), 11, f.Soluk);
            dc.DrawText(t, new Point(px - 6 - t.Width, y - t.Height / 2));
        }
        double sifir = Math.Round(Y(Math.Clamp(0, alt, ust))) + 0.5;
        dc.DrawLine(new Pen(f.Kenar, 1), new Point(px, sifir), new Point(px + pw, sifir));

        double gw = pw / n;
        if (m.Tur == GrafikTuru.Cubuk)
        {
            double y0 = Y(Math.Clamp(0, alt, ust));
            if (yigin)
            {
                double bw = Math.Max(1, gw * 0.7);
                for (int i = 0; i < n; i++)
                {
                    double x = px + i * gw + gw * 0.15, arti = 0, eksi = 0;
                    for (int j = 0; j < s; j++)
                    {
                        if (m.Seriler[j].Degerler[i] is not { } v || v == 0) continue;
                        double bas = v >= 0 ? arti : eksi, bit = bas + v;
                        var r = new Rect(x, Math.Min(Y(bas), Y(bit)), bw, Math.Abs(Y(bit) - Y(bas)));
                        dc.DrawRectangle(SeriFircasi(m.Seriler[j].Renk), null, r);
                        _vuruslar.Add(new GrafikVurusu(r, null, i, j, v));
                        if (v >= 0) arti = bit; else eksi = bit;
                    }
                    if (degerEtiketi && (arti > 0 || eksi < 0))
                    {
                        FormattedText e = Metin(SayiMetni(arti != 0 ? arti : eksi, 0.01), 10.5, f.Metin);
                        double yTepe = arti != 0 ? Y(arti) - e.Height - 2 : Y(eksi) + 2;
                        dc.DrawText(e, new Point(x + bw / 2 - e.Width / 2, yTepe));
                    }
                }
            }
            else
            {
                double bw = Math.Max(1, gw * 0.7 / s);
                for (int j = 0; j < s; j++)
                {
                    Brush firca = SeriFircasi(m.Seriler[j].Renk);
                    IReadOnlyList<double?> d = m.Seriler[j].Degerler;
                    for (int i = 0; i < n; i++)
                    {
                        if (d[i] is not { } v) continue;
                        double x = px + i * gw + gw * 0.15 + j * bw, y1 = Y(v);
                        var r = new Rect(x, Math.Min(y0, y1), Math.Max(1, bw - (s > 1 ? 1 : 0)), Math.Max(1, Math.Abs(y1 - y0)));
                        dc.DrawRectangle(firca, null, r);
                        _vuruslar.Add(new GrafikVurusu(r, null, i, j, v));
                        if (degerEtiketi)
                        {
                            FormattedText e = Metin(SayiMetni(v, 0.01), 10.5, f.Metin);
                            dc.DrawText(e, new Point(x + bw / 2 - e.Width / 2, v >= 0 ? y1 - e.Height - 2 : y1 + 2));
                        }
                    }
                }
            }
        }
        else
        {
            double X(int i) => n == 1 ? px + pw / 2 : px + i * (pw / (n - 1));
            for (int j = 0; j < s; j++)
            {
                Brush firca = SeriFircasi(m.Seriler[j].Renk);
                IReadOnlyList<double?> d = m.Seriler[j].Degerler;
                var cizgi = new StreamGeometry();
                var alan = new StreamGeometry();
                using (StreamGeometryContext c = cizgi.Open())
                using (StreamGeometryContext a = alan.Open())
                {
                    bool acik = false;
                    for (int i = 0; i <= n; i++)
                    {
                        if (i < n && d[i] is { } v)
                        {
                            var p = new Point(X(i), Y(v));
                            if (!acik)
                            {
                                c.BeginFigure(p, false, false);
                                a.BeginFigure(new Point(p.X, Y(Math.Clamp(0, alt, ust))), true, true);
                                a.LineTo(p, false, false);
                                acik = true;
                            }
                            else
                            {
                                c.LineTo(p, true, false);
                                a.LineTo(p, false, false);
                            }
                            _vuruslar.Add(new GrafikVurusu(new Rect(p.X - 5, p.Y - 5, 10, 10), null, i, j, v));
                        }
                        else if (acik)
                        {
                            a.LineTo(new Point(X(i - 1), Y(Math.Clamp(0, alt, ust))), false, false);
                            acik = false;
                        }
                    }
                }
                if (m.Tur == GrafikTuru.Alan)
                {
                    Brush dolgu = firca.Clone();
                    dolgu.Opacity = 0.25;
                    dc.DrawGeometry(dolgu, null, alan);
                }
                dc.DrawGeometry(null, new Pen(firca, 2) { LineJoin = PenLineJoin.Round }, cizgi);
                if (n <= 60)
                    for (int i = 0; i < n; i++)
                        if (d[i] is { } v)
                            dc.DrawEllipse(firca, null, new Point(X(i), Y(v)), 3, 3);
            }
            gw = n == 1 ? pw : pw / (n - 1);
            px -= gw / 2; // etiketler nokta merkezli
        }

        // X etiketleri: sığmıyorsa her k. etiket
        int atla = Math.Max(1, (int)Math.Ceiling((enGenis + 8) / gw));
        for (int i = 0; i < n; i += atla)
        {
            FormattedText t = etiketler[i];
            dc.DrawText(t, new Point(px + i * gw + gw / 2 - t.Width / 2, py + ph + 4));
        }
    }

    // ---- yatay çubuk ----

    private void YatayCubuk(DrawingContext dc, GrafikModeli m, Fircalar f, double w, double h)
    {
        (double alt, double ust, double adim) = Eksen(m);
        int n = m.Kategoriler.Count, s = m.Seriler.Count;
        bool yigin = m.Yigilmis;
        bool degerEtiketi = n <= 20 && (yigin || n * s <= 40);

        List<FormattedText> etiketler = [.. m.Kategoriler.Select(k => Metin(Kisalt(k), 11, f.Metin))];
        double solPay = Math.Min(170, etiketler.Max(t => t.Width) + 12);
        double px = solPay, py = 6, pw = w - solPay - (degerEtiketi ? 56 : 10), ph = h - 6 - 20;
        if (pw < 20 || ph < 20) return;
        double X(double v) => px + (v - alt) / (ust - alt) * pw;

        var izgaraKalem = new Pen(f.Izgara, 1);
        for (double v = alt; v <= ust + adim / 2; v += adim)
        {
            double x = Math.Round(X(v)) + 0.5;
            dc.DrawLine(izgaraKalem, new Point(x, py), new Point(x, py + ph));
            FormattedText t = Metin(SayiMetni(v, adim), 11, f.Soluk);
            dc.DrawText(t, new Point(x - t.Width / 2, py + ph + 4));
        }
        double x0 = X(Math.Clamp(0, alt, ust));
        dc.DrawLine(new Pen(f.Kenar, 1), new Point(Math.Round(x0) + 0.5, py), new Point(Math.Round(x0) + 0.5, py + ph));

        double gh = ph / n, bh = yigin ? Math.Max(1, gh * 0.7) : Math.Max(1, gh * 0.7 / s);
        int atla = Math.Max(1, (int)Math.Ceiling(14 / gh));
        for (int i = 0; i < n; i++)
        {
            if (i % atla == 0)
            {
                FormattedText t = etiketler[i];
                dc.DrawText(t, new Point(px - 6 - Math.Min(t.Width, solPay - 12), py + i * gh + gh / 2 - t.Height / 2));
            }
            double arti = 0, eksi = 0;
            for (int j = 0; j < s; j++)
            {
                if (m.Seriler[j].Degerler[i] is not { } v) continue;
                double y = py + i * gh + gh * 0.15 + (yigin ? 0 : j * bh);
                double bas = yigin ? (v >= 0 ? arti : eksi) : 0, bit = bas + v;
                var r = new Rect(Math.Min(X(bas), X(bit)), y, Math.Max(1, Math.Abs(X(bit) - X(bas))), Math.Max(1, bh - (s > 1 && !yigin ? 1 : 0)));
                dc.DrawRectangle(SeriFircasi(m.Seriler[j].Renk), null, r);
                _vuruslar.Add(new GrafikVurusu(r, null, i, j, v));
                if (yigin) { if (v >= 0) arti = bit; else eksi = bit; }
                else if (degerEtiketi)
                {
                    FormattedText e = Metin(SayiMetni(v, 0.01), 10.5, f.Metin);
                    dc.DrawText(e, new Point(v >= 0 ? X(v) + 4 : X(v) - e.Width - 4, y + bh / 2 - e.Height / 2));
                }
            }
            if (yigin && degerEtiketi && (arti > 0 || eksi < 0))
            {
                double top = arti != 0 ? arti : eksi;
                FormattedText e = Metin(SayiMetni(top, 0.01), 10.5, f.Metin);
                dc.DrawText(e, new Point(top >= 0 ? X(top) + 4 : X(top) - e.Width - 4, py + i * gh + gh / 2 - e.Height / 2));
            }
        }
    }

    // ---- pasta / halka ----

    private void Pasta(DrawingContext dc, GrafikModeli m, Fircalar f, double w, double h)
    {
        IReadOnlyList<double?> d = m.Seriler[0].Degerler;
        double toplam = d.Where(v => v is > 0).Sum(v => v!.Value);
        var merkez = new Point(w / 2, h / 2);
        double r = Math.Min(w, h) / 2 - 10, ic = r * 0.55;
        if (toplam <= 0 || r < 10)
        {
            FormattedText t = Metin("Pasta için pozitif değer gerekli.", 12.5, f.Soluk);
            dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
            return;
        }

        double aci = -90;
        for (int i = 0; i < d.Count; i++)
        {
            if (d[i] is not { } v || v <= 0) continue;
            double pay = v / toplam, sweep = pay * 360;
            Brush firca = SeriFircasi(i);
            Geometry g;
            if (sweep >= 359.99)
            {
                g = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(merkez, r, r), new EllipseGeometry(merkez, ic, ic));
                dc.DrawGeometry(firca, null, g);
            }
            else
            {
                var sg = new StreamGeometry();
                using (StreamGeometryContext c = sg.Open())
                {
                    Point disBas = Nokta(merkez, r, aci), disBit = Nokta(merkez, r, aci + sweep);
                    Point icBas = Nokta(merkez, ic, aci), icBit = Nokta(merkez, ic, aci + sweep);
                    bool buyuk = sweep > 180;
                    c.BeginFigure(disBas, true, true);
                    c.ArcTo(disBit, new Size(r, r), 0, buyuk, SweepDirection.Clockwise, true, false);
                    c.LineTo(icBit, true, false);
                    c.ArcTo(icBas, new Size(ic, ic), 0, buyuk, SweepDirection.Counterclockwise, true, false);
                }
                g = sg;
                dc.DrawGeometry(firca, new Pen(F("PencereZeminFircasi", Colors.White), 1.5), g);
            }
            _vuruslar.Add(new GrafikVurusu(g.Bounds, g, i, 0, v));
            if (pay >= 0.04)
            {
                FormattedText y = Metin((pay * 100).ToString("0", CultureInfo.CurrentCulture) + "%", 10.5, Brushes.White, true);
                Point p = Nokta(merkez, (r + ic) / 2, aci + sweep / 2);
                dc.DrawText(y, new Point(p.X - y.Width / 2, p.Y - y.Height / 2));
            }
            aci += sweep;
        }

        FormattedText t1 = Metin(SayiMetni(toplam, 0.01), Math.Max(12, ic / 3.2), f.Metin, true);
        FormattedText t2 = Metin("toplam", 10.5, f.Soluk);
        dc.DrawText(t1, new Point(merkez.X - t1.Width / 2, merkez.Y - t1.Height / 2 - 6));
        dc.DrawText(t2, new Point(merkez.X - t2.Width / 2, merkez.Y + t1.Height / 2 - 4));
    }

    private static Point Nokta(Point merkez, double yaricap, double aciDerece)
    {
        double a = aciDerece * Math.PI / 180;
        return new Point(merkez.X + yaricap * Math.Cos(a), merkez.Y + yaricap * Math.Sin(a));
    }
}
