using System.Data;
using System.Globalization;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Toplu grid işleminin sonucu. <paramref name="Atlananlar"/> boş değilse kullanıcıya
/// NEDEN atlandığı söylenir — sessizce eksik uygulamak en kötü davranıştır.
/// </summary>
public sealed record TopluSonuc(int Uygulanan, int EklenenSatir, IReadOnlyList<string> Atlananlar)
{
    public static readonly TopluSonuc Bos = new(0, 0, []);

    public string Ozet
    {
        get
        {
            var parcalar = new List<string>();
            if (Uygulanan > 0)
                parcalar.Add($"{Uygulanan} hücre değişti");
            if (EklenenSatir > 0)
                parcalar.Add($"{EklenenSatir} yeni satır");
            if (Atlananlar.Count > 0)
                parcalar.Add($"{Atlananlar.Count} atlandı");
            return parcalar.Count == 0 ? "Değişiklik yapılmadı." : string.Join(" · ", parcalar);
        }
    }
}

/// <summary>
/// Edit modunda toplu hücre işlemleri (V5-S5): NULL atama · çoklu satır yapıştırma ·
/// kolon bazlı toplu güncelleme.
///
/// <b>Tasarım:</b> bu sınıf yalnız bekleyen <see cref="DataTable"/>'ı değiştirir. DML üretimi,
/// Show Script önizlemesi ve tek işlemli uygulama zaten var olan raylardır
/// (<c>DmlUretici</c> → <c>DuzenlemeUygulayici</c>) ve <b>hiç değişmez</b> — yani toplu işlemler
/// de satır satır, PK'li WHERE ile ve kullanıcı önizleyip onayladıktan sonra uygulanır.
/// Bu bilinçli bir seçimdir: WHERE'siz tek bir toplu UPDATE üretmek daha kısa olurdu ama
/// edit modunun tüm güvenlik sözü (satır kimliği + iyimser kilit + çakışma tespiti) kaybolurdu.
///
/// <b>Kapsam sınırı:</b> işlemler YALNIZ yüklü satırlara uygulanır (edit modu tabloyu
/// <c>SatirLimiti</c> kadar yükler). Bu, arayüzde açıkça yazılır — kullanıcı "tüm tabloyu
/// güncelledim" sanmamalıdır.
/// </summary>
public static class GridTopluIslem
{
    /// <summary>Panodaki "NULL" metni — kopyalama tarafı NULL hücreleri böyle yazar.</summary>
    public const string NullMetni = "NULL";

    /// <summary>
    /// Seçili hücrelere NULL atar. NULL kabul etmeyen ya da yazılamaz kolonlar atlanır;
    /// sunucuya gidip hata almaktansa burada söylemek daha iyidir.
    /// </summary>
    public static TopluSonuc NullAta(
        DataTable tablo, DuzenlemeMetasi meta, IEnumerable<(int Satir, string Kolon)> hucreler)
    {
        int uygulanan = 0;
        var atlanan = new List<string>();

        foreach ((int satirNo, string kolonAdi) in hucreler)
        {
            if (satirNo < 0 || satirNo >= tablo.Rows.Count || !tablo.Columns.Contains(kolonAdi))
                continue;

            if (Engel(meta, kolonAdi, nullAtaniyor: true) is { } neden)
            {
                Ekle(atlanan, neden);
                continue;
            }

            tablo.Rows[satirNo][kolonAdi] = DBNull.Value;
            uygulanan++;
        }

        return new TopluSonuc(uygulanan, 0, atlanan);
    }

    /// <summary>
    /// Panodan çoklu satır/kolon yapıştırır (sekmeyle ayrılmış — Excel ve kendi kopyalamamızın
    /// biçimi). Yapıştırma başlangıç hücresinden sağa ve aşağı yayılır.
    /// </summary>
    /// <param name="yeniSatirEklenebilir">
    /// Panodaki satırlar tabloda kalanlardan fazlaysa yeni satır eklensin mi. Salt-okunur
    /// (PK'sız) tabloda ve kullanıcı ekleme yapamıyorsa false verilir; fazlalık atlanır.
    /// </param>
    public static TopluSonuc Yapistir(
        DataTable tablo, DuzenlemeMetasi meta, string panoMetni,
        int baslangicSatir, int baslangicKolon, bool yeniSatirEklenebilir)
    {
        string[] satirlar = SatirlaraBol(panoMetni);
        if (satirlar.Length == 0)
            return TopluSonuc.Bos;

        int uygulanan = 0, eklenen = 0;
        var atlanan = new List<string>();

        for (int s = 0; s < satirlar.Length; s++)
        {
            int hedefSatir = baslangicSatir + s;

            if (hedefSatir >= tablo.Rows.Count)
            {
                if (!yeniSatirEklenebilir)
                {
                    Ekle(atlanan, "Tablo sonuna sığmayan satırlar atlandı (yeni satır eklenemiyor).");
                    break;
                }
                tablo.Rows.Add(tablo.NewRow());
                eklenen++;
            }

            string[] hucreler = satirlar[s].Split('\t');
            for (int k = 0; k < hucreler.Length; k++)
            {
                int hedefKolon = baslangicKolon + k;
                if (hedefKolon >= tablo.Columns.Count)
                {
                    Ekle(atlanan, "Tablonun sağına taşan kolonlar atlandı.");
                    break;
                }

                DataColumn kolon = tablo.Columns[hedefKolon];
                string metin = hucreler[k];
                bool nullAtaniyor = metin.Equals(NullMetni, StringComparison.OrdinalIgnoreCase);

                if (Engel(meta, kolon.ColumnName, nullAtaniyor) is { } neden)
                {
                    Ekle(atlanan, neden);
                    continue;
                }

                if (!DegeriCevir(metin, kolon, out object? deger, out string? hata))
                {
                    Ekle(atlanan, hata!);
                    continue;
                }

                tablo.Rows[hedefSatir][hedefKolon] = deger;
                uygulanan++;
            }
        }

        return new TopluSonuc(uygulanan, eklenen, atlanan);
    }

    /// <summary>
    /// Bir kolonun TÜM yüklü satırlarına (ya da verilen satırlara) aynı değeri yazar.
    /// Değer <c>null</c> ise NULL atanır.
    /// </summary>
    public static TopluSonuc KolonaAta(
        DataTable tablo, DuzenlemeMetasi meta, string kolonAdi, string? metinDeger,
        IReadOnlyList<int>? satirlar = null)
    {
        if (!tablo.Columns.Contains(kolonAdi))
            return new TopluSonuc(0, 0, [$"'{kolonAdi}' kolonu yok."]);

        bool nullAtaniyor = metinDeger is null
            || metinDeger.Equals(NullMetni, StringComparison.OrdinalIgnoreCase);

        if (Engel(meta, kolonAdi, nullAtaniyor) is { } neden)
            return new TopluSonuc(0, 0, [neden]);

        DataColumn kolon = tablo.Columns[kolonAdi]!;
        if (!DegeriCevir(nullAtaniyor ? NullMetni : metinDeger!, kolon, out object? deger, out string? hata))
            return new TopluSonuc(0, 0, [hata!]);

        IEnumerable<int> hedefler = satirlar ?? Enumerable.Range(0, tablo.Rows.Count);
        int uygulanan = 0;
        foreach (int satirNo in hedefler)
        {
            if (satirNo < 0 || satirNo >= tablo.Rows.Count)
                continue;
            tablo.Rows[satirNo][kolonAdi] = deger;
            uygulanan++;
        }

        return new TopluSonuc(uygulanan, 0, []);
    }

    /// <summary>Kolon bu işlemi kabul eder mi; etmiyorsa kullanıcıya söylenecek neden.</summary>
    private static string? Engel(DuzenlemeMetasi meta, string kolonAdi, bool nullAtaniyor)
    {
        DuzenlemeKolonu? kolon = meta.Kolonlar
            .FirstOrDefault(k => string.Equals(k.Ad, kolonAdi, StringComparison.OrdinalIgnoreCase));

        if (kolon is null)
            return null;   // üst veride yoksa engelleme — grid zaten göstermezdi

        if (!kolon.Yazilabilir)
            return $"'{kolon.Ad}' sunucu tarafından üretilir (identity/computed/rowversion) — yazılamaz.";

        if (nullAtaniyor && !kolon.NullOlabilir)
            return $"'{kolon.Ad}' NULL kabul etmiyor.";

        return null;
    }

    /// <summary>
    /// Pano metnini kolonun CLR tipine çevirir.
    ///
    /// <b>Kültür:</b> kopyalama tarafı <c>ToString()</c> ile GEÇERLİ kültürde yazar
    /// (tr-TR'de 3,14). Yapıştırma da geçerli kültürle okunur ki gidiş-dönüş bozulmasın;
    /// geçerli kültürde çözülemezse invariant denenir (Excel/İngilizce kaynaktan gelen
    /// "3.14" da çalışsın).
    /// </summary>
    private static bool DegeriCevir(string metin, DataColumn kolon, out object? deger, out string? hata)
    {
        hata = null;

        if (metin.Equals(NullMetni, StringComparison.OrdinalIgnoreCase))
        {
            deger = DBNull.Value;
            return true;
        }

        Type hedef = Nullable.GetUnderlyingType(kolon.DataType) ?? kolon.DataType;

        if (hedef == typeof(string) || hedef == typeof(object))
        {
            deger = metin;
            return true;
        }

        if (hedef == typeof(byte[]))
        {
            try
            {
                deger = Convert.FromHexString(metin.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? metin[2..] : metin);
                return true;
            }
            catch (FormatException)
            {
                deger = null;
                hata = $"'{kolon.ColumnName}' ikili kolon — '{Kisalt(metin)}' onaltılık değer değil.";
                return false;
            }
        }

        // Ondalıklı tipler (kültür bekleyeni, 2026-07-30): binlik ayraç KAPALI parse edilir —
        // tr-TR'de Excel'den gelen "3.14", Convert.ChangeType ile SESSİZCE 314 oluyordu (nokta
        // binlik sayılır, invariant'a hiç düşülmezdi). AllowThousands kapatınca "3.14" tr'de
        // çözülemez → invariant'ta 3,14 olarak DOĞRU okunur (GorselSorguUretici/VeriArayici emsali).
        if (hedef == typeof(double) || hedef == typeof(float) || hedef == typeof(decimal))
        {
            foreach (CultureInfo kultur in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
            {
                if (hedef == typeof(decimal) && decimal.TryParse(metin, NumberStyles.Float, kultur, out decimal m))
                {
                    deger = m;
                    return true;
                }

                if (hedef != typeof(decimal) && double.TryParse(metin, NumberStyles.Float, kultur, out double d))
                {
                    deger = hedef == typeof(float) ? (float)d : d;
                    return true;
                }
            }

            deger = null;
            hata = $"'{kolon.ColumnName}' ({hedef.Name}) için '{Kisalt(metin)}' çevrilemedi.";
            return false;
        }

        foreach (CultureInfo kultur in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            try
            {
                deger = Convert.ChangeType(metin, hedef, kultur);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // sonraki kültürü dene
            }
        }

        deger = null;
        hata = $"'{kolon.ColumnName}' ({hedef.Name}) için '{Kisalt(metin)}' çevrilemedi.";
        return false;
    }

    /// <summary>Satır sonlarını normalleştirir; sondaki boş satır yok sayılır.</summary>
    private static string[] SatirlaraBol(string metin)
    {
        if (string.IsNullOrEmpty(metin))
            return [];

        string[] parcalar = metin.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int son = parcalar.Length;
        while (son > 0 && parcalar[son - 1].Length == 0)
            son--;

        return son == parcalar.Length ? parcalar : parcalar[..son];
    }

    /// <summary>Aynı neden defalarca tekrarlanmasın — 50 satırlık yapıştırmada 50 kez yazılırdı.</summary>
    private static void Ekle(List<string> liste, string neden)
    {
        if (!liste.Contains(neden))
            liste.Add(neden);
    }

    private static string Kisalt(string metin)
        => metin.Length <= 30 ? metin : metin[..30] + "…";
}
