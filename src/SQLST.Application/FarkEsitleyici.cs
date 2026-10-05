using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// BF-3 (2026-07-27): tam eşitleme akışının tek giriş noktası — Excel satırları ile hedef tablonun
/// mevcut satırlarını <see cref="ExcelFarkKarsilastirici"/> ile kıyaslar, sonra
/// <see cref="FarkDmlUretici"/> ile motor-doğru INSERT/UPDATE/DELETE üretir. İki tarafın OKUNMASI
/// çağırana aittir (Excel: DosyaOkuyucu · tablo: FarkOkumaServisi); burası SAF birleştirmedir —
/// UI, önce iki tarafı okur, sonra bunu çağırıp <see cref="EsitlemeSonucu"/>'nu (sayımlar + üretilen
/// script) alır ve script'i Güvenli Yazma sekmesinde açar.
/// </summary>
public static class FarkEsitleyici
{
    /// <param name="Fark">Üç kova (YENİ/DEĞİŞEN/SİLİNMİŞ) — UI önizleme gridleri bunu gösterir.</param>
    /// <param name="Komutlar">Güvenli Yazma'ya gidecek DELETE→UPDATE→INSERT dizisi.</param>
    /// <param name="Onizleme">Komutların satır-satır metni ("Show Script").</param>
    public sealed record EsitlemeSonucu(
        TabloFarki Fark, IReadOnlyList<string> Komutlar, string Onizleme)
    {
        public int YeniSayisi => Fark.Yeniler.Count;
        public int DegisenSayisi => Fark.Degisenler.Count;
        public int SilinenSayisi => Fark.Silinenler.Count;
        public bool BosMu => Komutlar.Count == 0;
    }

    public static EsitlemeSonucu Hesapla(
        ILehce lehce, string sema, string tablo,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> excelSatirlar,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> tabloSatirlar,
        IReadOnlyList<string> anahtarKolonlar,
        IReadOnlyList<string> karsilastirilanKolonlar,
        bool silmeDahil)
    {
        TabloFarki fark = ExcelFarkKarsilastirici.Karsilastir(
            excelSatirlar, tabloSatirlar, anahtarKolonlar, karsilastirilanKolonlar);
        IReadOnlyList<string> komutlar = FarkDmlUretici.Uret(
            lehce, sema, tablo, anahtarKolonlar, fark, silmeDahil);
        return new EsitlemeSonucu(fark, komutlar, FarkDmlUretici.Onizle(komutlar));
    }
}
