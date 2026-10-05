using System.Data;
using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Tek result set'in UI paketi: tablo + tekilleştirilmiş kolon adı → SQL tipi haritası (FG-4.10 tooltip'leri).</summary>
public sealed record SonucSeti(DataTable Tablo, IReadOnlyDictionary<string, string> KolonTipleri)
{
    public int SatirSayisi => Tablo.Rows.Count;

    /// <summary>v22-S3: tablo kurulurken bellek tavanı yüzünden KESİLDİ mi (çağıran bandda söyler).</summary>
    public bool Kesildi { get; init; }
}

/// <summary>
/// QueryResult'ı UI'ın tüketeceği biçime çevirir: result set → DataTable,
/// mesajlar/hata → Messages metni ("Msg N, Satır X: …" — 03-ui-tasarim §3.2).
/// </summary>
public static class SonucBicimleyici
{
    /// <summary>
    /// Adsız kolonun (ör. <c>SELECT COUNT(*)</c>) yer tutucusu.
    ///
    /// ⚠ PARANTEZLE BAŞLAYAMAZ (v22-S4 saha turu-4 m.9 — kullanıcı: "bir tablonun count'u
    /// gelmiyor"; grid "1 satır" diyor ama hücre BOŞ). Eski değer <c>"(adsız)"</c> idi: WPF
    /// <c>PropertyPath</c> ayrıştırıcısı '(' ile BAŞLAYAN yolu <i>eklenmiş özellik</i>
    /// (<c>(Grid.Row)</c>) sanar; indexer biçiminde (<c>[(adsız)]</c>) verilse bile bağlama
    /// sessizce düşer ve hücre boş görünür. ÖLÇÜLDÜ: aynı kurgu ile <c>ort süre (ms)</c> ve
    /// <c>Tutar $</c> SORUNSUZ bağlanıyor — kıran tek şey adın parantezle BAŞLAMASI, yani
    /// kusur kolon adlarında değil bizim yer tutucumuzdaydı.
    /// </summary>
    public const string AdsizKolon = "adsız";

    /// <summary>
    /// Kolon adlarını tekilleştirerek (boş → adsız, çakışan → ad_2) DataTable + tip haritası kurar.
    ///
    /// <b>v22-S3 (saha turu-3; kullanıcı: "mantığımızı MSSQL'deki gibi yapalım, gereksiz yüklerden
    /// arındıralım"):</b> kolonlar artık <c>typeof(object)</c> DEĞİL, okuyucunun bildirdiği GERÇEK CLR
    /// tipiyle kurulur. DataTable tipli kolonda değerleri KUTULAMADAN tutar (Int32Storage/DateTimeStorage);
    /// object kolonda her sayı/tarih için ayrı kutu nesnesi yaşar. ÖLÇÜM (200.000 satır × 10 kolon —
    /// 4 sayı + 1 tarih + 5 metin): object kolonlarla satır başına <b>595 bayt</b>, gerçek tiplerle
    /// <b>457 bayt</b> (%23 az; 1 GB tepede 1,8M yerine 2,3M satır). Yan kazanç: sıralama artık metin
    /// değil TİP sırasına göre. Edit modu bu yaklaşımı zaten kullanıyordu (kanıtlı).
    ///
    /// <paramref name="durmaliMi"/> her 4.096 satırda bir sorulur; true dönerse yükleme KESİLİR
    /// (<see cref="SonucSeti.Kesildi"/> ile bildirilir). Koruma eskiden yalnız OKUMA fazındaydı —
    /// tablo kurulumu korumasızdı ve tepe belleği okuma bittikten SONRA ikiye katlıyordu.
    /// </summary>
    public static SonucSeti TabloyaCevir(ResultSetData set, Func<bool>? durmaliMi = null)
    {
        var tipler = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kullanilan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adlar = new List<string>(set.Kolonlar.Count);

        foreach (KolonBilgisi kolon in set.Kolonlar)
        {
            string ad = string.IsNullOrWhiteSpace(kolon.Ad) ? AdsizKolon : kolon.Ad;
            string aday = ad;
            for (int i = 2; !kullanilan.Add(aday); i++)
                aday = $"{ad}_{i}";
            adlar.Add(aday);
            tipler[aday] = HamDeger.GorunumTipi(kolon.TipAdi, kolon.Olcek); // v23-S13: datetime2(3)
        }

        try
        {
            (DataTable tipli, bool kesildi) = Doldur(set, adlar, tipliKolonlar: true, durmaliMi);
            TipleriIsle(tipli, tipler);
            return new SonucSeti(tipli, tipler) { Kesildi = kesildi };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or FormatException)
        {
            // ŞEMASIZ kaynak (MongoDB): kolon tipi İLK belgeden çıkarılır, sonraki belgede aynı alan
            // başka tipte olabilir → tipli kolon değeri reddeder. O sette object kolonlara düşülür:
            // gösterim HER ZAMAN çalışır, yalnız o setin bellek kazancı kaybolur (izinli bölge:
            // dar tipli dönüşüm sınırı — AGENTS §4.6).
            (DataTable serbest, bool kesildi) = Doldur(set, adlar, tipliKolonlar: false, durmaliMi);
            TipleriIsle(serbest, tipler);
            return new SonucSeti(serbest, tipler) { Kesildi = kesildi };
        }
    }

    /// <summary>v23-S13: SQL tipini KOLONUN ÜSTÜNE yazar (<see cref="HamDeger.TipAnahtari"/>) —
    /// tabloyu alan her yüzey (CSV/pano/rapor) ham metni tipe göre üretir, ayrı harita taşımaz.</summary>
    public static void TipleriIsle(DataTable tablo, IReadOnlyDictionary<string, string> tipler)
    {
        foreach (DataColumn k in tablo.Columns)
            if (tipler.TryGetValue(k.ColumnName, out string? tip))
                k.ExtendedProperties[HamDeger.TipAnahtari] = tip;
    }

    private static (DataTable Tablo, bool Kesildi) Doldur(
        ResultSetData set, List<string> adlar, bool tipliKolonlar, Func<bool>? durmaliMi)
    {
        var tablo = new DataTable();
        for (int i = 0; i < adlar.Count; i++)
        {
            Type tip = tipliKolonlar ? set.Kolonlar[i].ClrTip ?? typeof(object) : typeof(object);
            tablo.Columns.Add(adlar[i], tip);
        }

        bool kesildi = false;
        int sayac = 0;
        tablo.BeginLoadData();
        foreach (object?[] satir in set.Satirlar)
        {
            if (durmaliMi is not null && (++sayac & 4095) == 0 && durmaliMi())
            {
                kesildi = true;
                break;
            }
            tablo.Rows.Add(satir);
        }
        tablo.EndLoadData();
        return (tablo, kesildi);
    }

    /// <summary>
    /// Panoya kopyalama metni (FG-4.2): sekmeyle ayrılmış (Excel'e yapıştırılabilir);
    /// satır seçimi verilmezse tüm tablo. NULL hücreler "NULL" yazılır.
    /// </summary>
    public static string PanoMetni(DataTable tablo, IReadOnlyList<DataRow>? satirlar, bool basliklarla)
    {
        var sb = new StringBuilder();

        if (basliklarla)
            sb.AppendLine(string.Join('\t', tablo.Columns.Cast<DataColumn>().Select(k => k.ColumnName)));

        DataColumn[] kolonlar = [.. tablo.Columns.Cast<DataColumn>()];
        foreach (DataRow satir in satirlar ?? tablo.Rows.Cast<DataRow>().ToList())
            sb.AppendLine(string.Join('\t', kolonlar.Select(k => HamDeger.Metin(satir, k))));

        return sb.ToString();
    }

    /// <summary>Hücrenin HAM pano/dışa-aktarma metni (satır sonları KORUNUR). v23-S13: eskiden
    /// <c>ToString()</c> iş parçacığı kültürüyle (tr-TR) "05.10.2026" / "1250,75" üretiyordu —
    /// artık <see cref="HamDeger"/>: kültürsüz, SSMS biçimi. Tip biliniyorsa verin.</summary>
    public static string HucreMetni(object? deger, string? sqlTip = null) => HamDeger.Metin(deger, sqlTip);

    public static string MesajlariBirlestir(QueryResult sonuc)
    {
        var sb = new StringBuilder();

        if (sonuc.Hata is { } hata)
            sb.AppendLine($"Msg {hata.Numara}, Satır {hata.Satir}: {hata.Mesaj}");

        foreach (string mesaj in sonuc.Mesajlar)
            sb.AppendLine(mesaj);

        if (sonuc.BellekSiniriAsildi)
            sb.AppendLine($"İlk {sonuc.ToplamSatir} satır gösteriliyor — bellek sınırına ulaşıldı (geniş satırlar); tümü için \"Tümünü dışa aktar\" (FG-4.5).");
        else if (sonuc.SatirSiniriAsildi)
            sb.AppendLine($"İlk {sonuc.ToplamSatir} satır gösteriliyor — satır sınırına ulaşıldı (FG-4.5).");

        if (sb.Length == 0 && sonuc.Basarili)
            sb.AppendLine("Komut başarıyla tamamlandı.");

        return sb.ToString().TrimEnd();
    }

    public static string SureFormatla(TimeSpan sure)
        => $"{(int)sure.TotalHours:00}:{sure.Minutes:00}:{sure.Seconds:00}.{sure.Milliseconds / 10:00}";
}
