using System.Data;
using System.Globalization;

namespace SQLST.Application;

/// <summary>Grafik türleri (v23-S24, K3): nokta/dağılım kullanıcı kararıyla kapsam dışı.</summary>
public enum GrafikTuru { Cubuk, YatayCubuk, Cizgi, Alan, Pasta }

/// <summary>Aynı X birden fazla satırda geçiyorsa değerler nasıl birleşir (K4). "Yok" = satır sırasıyla, birleştirmeden.</summary>
public enum GrafikToplama { Yok, Toplam, Ortalama, Adet, EnKucuk, EnBuyuk }

/// <summary>Panelin seçimleri: X kolonu, Y kolonları (çoklu), isteğe bağlı seri kolonu (pivot), toplama, tür.</summary>
public sealed record GrafikAyari(
    GrafikTuru Tur, string? XKolon, IReadOnlyList<string> YKolonlar, string? SeriKolon, GrafikToplama Toplama,
    bool Yigilmis = false);

/// <summary>Çizilecek bir seri: ad + kategori başına değer (NULL = nokta yok; çizgide kopuk). <paramref name="Renk"/>
/// serinin KALICI renk sırası — bir seri gizlenince diğerlerinin rengi kaymaz (S2).</summary>
public sealed record GrafikSerisi(string Ad, IReadOnlyList<double?> Degerler, int Renk = 0);

/// <summary>Tuvalin çizdiği hazır model — eşleme/toplama/tavan burada bitmiştir, tuval yalnız çizer.</summary>
public sealed record GrafikModeli(
    GrafikTuru Tur,
    IReadOnlyList<string> Kategoriler,
    IReadOnlyList<GrafikSerisi> Seriler,
    int ToplamKategori,
    string? Uyari)
{
    /// <summary>S2: çubuklar üst üste (yalnız çubuk/yatay, çoklu seride anlamlı).</summary>
    public bool Yigilmis { get; init; }

    public static GrafikModeli Bos(GrafikTuru tur, string uyari) => new(tur, [], [], 0, uyari);

    public bool BosMu => Kategoriler.Count == 0 || Seriler.Count == 0;

    public int NoktaSayisi => Seriler.Sum(s => s.Degerler.Count(d => d.HasValue));

    /// <summary>Değer ekseninin alt/üst sınırı: 0 her zaman içeride (çubuk sıfırdan başlar). Yığılmışta kategori
    /// başına pozitifler ve negatifler ayrı toplanır (eksen yığının tepesini görür).</summary>
    public (double EnKucuk, double EnBuyuk) DegerAraligi()
    {
        double min = 0, max = 0;
        if (Yigilmis && Tur is GrafikTuru.Cubuk or GrafikTuru.YatayCubuk)
        {
            for (int i = 0; i < Kategoriler.Count; i++)
            {
                double arti = 0, eksi = 0;
                foreach (GrafikSerisi s in Seriler)
                    if (s.Degerler[i] is { } v)
                    {
                        if (v >= 0) arti += v; else eksi += v;
                    }
                if (arti > max) max = arti;
                if (eksi < min) min = eksi;
            }
            return (min, max);
        }
        foreach (GrafikSerisi s in Seriler)
            foreach (double? d in s.Degerler)
            {
                if (d is not { } v) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        return (min, max);
    }
}

/// <summary>
/// 📊 Sonuç tablosundan grafik modeli (v23-S24 S1 — docs/12-grafik-arastirma.md). SAF: DataTable girer,
/// çizime hazır kategoriler + seriler çıkar. Toplama (GROUP BY yazmadan), seri kolonu (uzun → geniş pivot),
/// otomatik tahmin ve "dürüst tavan" (200 kategori / 50.000 nokta — K5) burada; WPF yalnız çizer.
/// </summary>
public static class GrafikHazirlayici
{
    public const int KategoriTavani = 200;
    public const int NoktaTavani = 50_000;
    public const string BosKategori = "(boş)";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool SayisalMi(DataColumn k) => Type.GetTypeCode(k.DataType) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
        or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    public static bool TarihMi(DataColumn k) => k.DataType == typeof(DateTime) || k.DataType == typeof(DateTimeOffset);

    /// <summary>X ya da seri olabilecek kolon: ikili (varbinary) ve nesne tipleri hariç her şey.</summary>
    public static bool KategoriOlabilirMi(DataColumn k) => k.DataType != typeof(byte[]) && k.DataType != typeof(object);

    public static IReadOnlyList<string> SayisalKolonlar(DataTable t)
        => [.. t.Columns.Cast<DataColumn>().Where(SayisalMi).Select(c => c.ColumnName)];

    public static IReadOnlyList<string> KategoriKolonlari(DataTable t)
        => [.. t.Columns.Cast<DataColumn>().Where(KategoriOlabilirMi).Select(c => c.ColumnName)];

    /// <summary>
    /// Otomatik tahmin (K4 — panel açılınca DOLU gelsin): ilk metin/tarih kolonu X, ilk 3 sayısal kolon Y;
    /// X tekrar ediyorsa Toplam, tarih ise çizgi. Sayısal kolon yoksa kategori sayımı (Adet). Paneli AÇMAZ (K8).
    /// </summary>
    public static GrafikAyari Tahmin(DataTable t)
    {
        List<DataColumn> kolonlar = [.. t.Columns.Cast<DataColumn>()];
        DataColumn? x = kolonlar.FirstOrDefault(c => KategoriOlabilirMi(c) && !SayisalMi(c))
                     ?? kolonlar.FirstOrDefault(KategoriOlabilirMi);
        List<string> yler = [.. kolonlar.Where(c => SayisalMi(c) && c != x).Take(3).Select(c => c.ColumnName)];
        if (x is null)
            return new GrafikAyari(GrafikTuru.Cubuk, null, yler, null, GrafikToplama.Yok);

        GrafikToplama toplama = yler.Count == 0 ? GrafikToplama.Adet
            : XTekrarEdiyorMu(t, x) ? GrafikToplama.Toplam : GrafikToplama.Yok;
        GrafikTuru tur = TarihMi(x) ? GrafikTuru.Cizgi : GrafikTuru.Cubuk;
        return new GrafikAyari(tur, x.ColumnName, yler, null, toplama);
    }

    private static bool XTekrarEdiyorMu(DataTable t, DataColumn x)
    {
        var gorulen = new HashSet<string>();
        int bak = Math.Min(t.Rows.Count, 20_000);
        for (int i = 0; i < bak; i++)
            if (!gorulen.Add(KategoriMetni(t.Rows[i][x])))
                return true;
        return false;
    }

    /// <summary>Kategori etiketi: NULL → "(boş)", tarih → ISO (saat yalnız sıfır değilse), sayı/metin invariant.</summary>
    public static string KategoriMetni(object? deger) => deger switch
    {
        null or DBNull => BosKategori,
        DateTime t => t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", Inv) : t.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        string s => s.Length == 0 ? BosKategori : s,
        _ => HamDeger.Metin(deger),
    };

    private static double? Sayi(object? deger)
    {
        if (deger is null or DBNull) return null;
        try { return Convert.ToDouble(deger, Inv); }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) { return null; }
    }

    /// <summary>
    /// Ayarı tabloya uygular. <paramref name="ekranGenisligi"/> çizgi/alan indirgemesinde kova sayısını belirler
    /// (0 → 1000 kova). Ayar tabloyla uyuşmuyorsa (kolon yok) boş model + açıklayıcı uyarı döner — sessiz boş grafik YOK.
    /// </summary>
    public static GrafikModeli Kur(DataTable t, GrafikAyari a, int ekranGenisligi = 0)
    {
        if (a.XKolon is null || !t.Columns.Contains(a.XKolon))
            return GrafikModeli.Bos(a.Tur, "X (kategori) kolonu seçin.");
        DataColumn x = t.Columns[a.XKolon]!;

        DataColumn? seri = a.SeriKolon is not null && t.Columns.Contains(a.SeriKolon) ? t.Columns[a.SeriKolon] : null;
        List<DataColumn> yler = [.. a.YKolonlar.Where(t.Columns.Contains).Select(ad => t.Columns[ad]!)];
        bool sayim = a.Toplama == GrafikToplama.Adet;
        if (yler.Count == 0 && !sayim)
            return GrafikModeli.Bos(a.Tur, "Y (değer) kolonu seçin — ya da Toplama'yı 'Adet' yapın.");
        if (seri is not null && yler.Count > 1)
            yler = [yler[0]]; // seri kolonu varken Y tek: seriler o kolonun değerleridir (K4)
        if (t.Rows.Count == 0)
            return GrafikModeli.Bos(a.Tur, "Sonuç boş — çizilecek satır yok.");

        // Kategori sırası = ilk görülme (ORDER BY'a saygı). Toplama Yok + seri yok → her satır ayrı nokta.
        bool satirBasina = a.Toplama == GrafikToplama.Yok && seri is null;
        List<string> kategoriler = [];
        var kategoriIndeksi = new Dictionary<string, int>();
        List<string> seriAdlari = [];
        var seriIndeksi = new Dictionary<string, int>();
        // [seri][kategori] → biriktirici
        List<List<Biriktirici?>> hucreler = [];

        if (seri is null)
        {
            foreach (DataColumn y in yler)
                SeriEkle(y.ColumnName);
            if (yler.Count == 0)
                SeriEkle("Adet");
        }

        foreach (DataRow satir in t.Rows)
        {
            string kat = KategoriMetni(satir[x]);
            int ki;
            if (satirBasina)
            {
                ki = kategoriler.Count;
                kategoriler.Add(kat);
                foreach (List<Biriktirici?> s in hucreler) s.Add(null);
            }
            else if (!kategoriIndeksi.TryGetValue(kat, out ki))
            {
                ki = kategoriler.Count;
                kategoriIndeksi[kat] = ki;
                kategoriler.Add(kat);
                foreach (List<Biriktirici?> s in hucreler) s.Add(null);
            }

            if (seri is not null)
            {
                string sAd = KategoriMetni(satir[seri]);
                if (!seriIndeksi.TryGetValue(sAd, out int si))
                    si = SeriEkle(sAd);
                Biriktir(si, ki, sayim ? 1d : Sayi(satir[yler[0]]));
            }
            else if (yler.Count == 0)
            {
                Biriktir(0, ki, 1d);
            }
            else
            {
                for (int si = 0; si < yler.Count; si++)
                    Biriktir(si, ki, sayim ? 1d : Sayi(satir[yler[si]]));
            }
        }

        int toplamKategori = kategoriler.Count;
        string? uyari = null;
        List<GrafikSerisi> seriler = [];
        for (int si = 0; si < seriAdlari.Count; si++)
            seriler.Add(new GrafikSerisi(seriAdlari[si], [.. hucreler[si].Select(b => b?.Sonuc(a.Toplama))], si));

        // Pasta tek seri çizer; fazla seri varsa ilkini alır ve söyler.
        if (a.Tur == GrafikTuru.Pasta && seriler.Count > 1)
        {
            uyari = $"Pasta tek seri çizer — '{seriler[0].Ad}' gösteriliyor.";
            seriler = [seriler[0]];
        }

        // K5 — dürüst tavanlar
        if (a.Tur is GrafikTuru.Cizgi or GrafikTuru.Alan)
        {
            if (kategoriler.Count > NoktaTavani)
                (kategoriler, seriler, uyari) = Indirge(kategoriler, seriler, ekranGenisligi);
        }
        else if (kategoriler.Count > KategoriTavani)
        {
            int fazla = kategoriler.Count - KategoriTavani;
            if (a.Tur == GrafikTuru.Pasta)
            {
                // Pastada kalan dilimler "Diğer"de toplanır — yüzde doğru kalsın.
                double? diger = seriler[0].Degerler.Skip(KategoriTavani).Sum();
                kategoriler = [.. kategoriler.Take(KategoriTavani), $"Diğer ({fazla:N0} kategori)"];
                seriler = [new GrafikSerisi(seriler[0].Ad, [.. seriler[0].Degerler.Take(KategoriTavani), diger])];
                uyari = $"{toplamKategori:N0} kategori — ilk {KategoriTavani} dilim ayrı, kalan {fazla:N0} kategori 'Diğer'de toplandı.";
            }
            else
            {
                kategoriler = [.. kategoriler.Take(KategoriTavani)];
                seriler = [.. seriler.Select(s => s with { Degerler = [.. s.Degerler.Take(KategoriTavani)] })];
                uyari = $"{toplamKategori:N0} kategori — ilk {KategoriTavani}'ü çizildi, +{fazla:N0} diğer gösterilmiyor. "
                      + "Daraltmak için sorguya TOP/WHERE ekleyin ya da Toplama seçin; tam veri grid'de.";
            }
        }

        return new GrafikModeli(a.Tur, kategoriler, seriler, toplamKategori, uyari)
        {
            Yigilmis = a.Yigilmis && a.Tur is GrafikTuru.Cubuk or GrafikTuru.YatayCubuk && seriler.Count > 1,
        };

        int SeriEkle(string ad)
        {
            int si = seriAdlari.Count;
            seriIndeksi[ad] = si;
            seriAdlari.Add(ad);
            hucreler.Add([.. Enumerable.Repeat<Biriktirici?>(null, kategoriler.Count)]);
            return si;
        }

        void Biriktir(int si, int ki, double? deger)
        {
            if (deger is not { } v) return;
            Biriktirici? b = hucreler[si][ki];
            if (b is null) hucreler[si][ki] = b = new Biriktirici();
            b.Ekle(v);
        }
    }

    /// <summary>
    /// Çizgi/alan için ekran-kova indirgemesi: her kovada en küçük ve en büyük nokta kalır (tepe/çukur kaybolmaz),
    /// etiket kovanın ilk kategorisi. 50.000 üstü nokta için; LTTB gerekmez.
    /// </summary>
    private static (List<string>, List<GrafikSerisi>, string) Indirge(
        List<string> kategoriler, List<GrafikSerisi> seriler, int ekranGenisligi)
    {
        int kova = Math.Max(100, (ekranGenisligi > 0 ? ekranGenisligi : 2000) / 2);
        int n = kategoriler.Count;
        var secilen = new SortedSet<int>();
        for (int k = 0; k < kova; k++)
        {
            int bas = (int)((long)k * n / kova), bit = (int)((long)(k + 1) * n / kova);
            if (bit <= bas) continue;
            foreach (GrafikSerisi s in seriler)
            {
                int iMin = -1, iMax = -1;
                double vMin = double.MaxValue, vMax = double.MinValue;
                for (int i = bas; i < bit; i++)
                {
                    if (s.Degerler[i] is not { } v) continue;
                    if (v < vMin) { vMin = v; iMin = i; }
                    if (v > vMax) { vMax = v; iMax = i; }
                }
                if (iMin >= 0) secilen.Add(iMin);
                if (iMax >= 0) secilen.Add(iMax);
            }
            secilen.Add(bas);
        }
        List<int> indeksler = [.. secilen];
        return (
            [.. indeksler.Select(i => kategoriler[i])],
            [.. seriler.Select(s => s with { Degerler = [.. indeksler.Select(i => s.Degerler[i])] })],
            $"{n:N0} nokta — ekran genişliğine indirgendi ({indeksler.Count:N0} nokta; her kovada en küçük/en büyük korunur). Tam veri grid'de.");
    }

    private sealed class Biriktirici
    {
        private double _ilk, _toplam, _min = double.MaxValue, _max = double.MinValue;
        private int _adet;

        public void Ekle(double v)
        {
            if (_adet == 0) _ilk = v;
            _adet++;
            _toplam += v;
            if (v < _min) _min = v;
            if (v > _max) _max = v;
        }

        public double? Sonuc(GrafikToplama t) => _adet == 0 ? null : t switch
        {
            GrafikToplama.Toplam => _toplam,
            GrafikToplama.Ortalama => _toplam / _adet,
            GrafikToplama.Adet => _adet,
            GrafikToplama.EnKucuk => _min,
            GrafikToplama.EnBuyuk => _max,
            _ => _ilk, // Yok: seri kolonu varken aynı (x, seri) tekrar ederse ilk değer kalır
        };
    }
}
