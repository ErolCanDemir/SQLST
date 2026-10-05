using System.Data;

namespace SQLST.Application;

/// <summary>
/// Auto-refresh iki koşu arasındaki fark özeti (V5-S4).
/// </summary>
/// <param name="Eklenen">Yeni koşuda fazladan olan satır sayısı.</param>
/// <param name="Silinen">Önceki koşuda olup yenide olmayan satır sayısı.</param>
/// <param name="Degisen">Aynı konumdaki hücre değeri değişen satır sayısı.</param>
/// <param name="DegisenSatirlar">Değişen satırların yeni tablodaki 0 tabanlı indeksleri.</param>
/// <param name="SekilDegisti">Kolon kümesi değiştiği için satır karşılaştırması yapılamadı.</param>
public sealed record YenilemeFarki(
    int Eklenen,
    int Silinen,
    int Degisen,
    IReadOnlySet<int> DegisenSatirlar,
    bool SekilDegisti)
{
    public bool FarkVar => Eklenen > 0 || Silinen > 0 || Degisen > 0 || SekilDegisti;

    public string Ozet => SekilDegisti
        ? "Kolonlar değişti — satır karşılaştırması yapılamadı."
        : !FarkVar
            ? "Değişiklik yok."
            : string.Join(" · ", new[]
            {
                Eklenen > 0 ? $"+{Eklenen} satır" : null,
                Silinen > 0 ? $"−{Silinen} satır" : null,
                Degisen > 0 ? $"{Degisen} satır değişti" : null,
            }.Where(p => p is not null));
}

/// <summary>
/// Auto-refresh için KONUMSAL fark bulucu (V5-S4).
///
/// <b>Neden mevcut <see cref="SonucKarsilastirici"/> kullanılmadı:</b> o, kullanıcının
/// seçtiği ANAHTAR KOLONLARI ister ve anahtar tekil değilse hata döner. Auto-refresh'te
/// kullanıcıya anahtar sorulmaz — sorgu her şey olabilir (toplulaştırma, sıralamasız
/// SELECT, tek satırlık sayaç). Bu yüzden burada satırlar SIRAYA göre eşlenir.
///
/// <b>Dürüst sınır:</b> sorguda <c>ORDER BY</c> yoksa sunucu satır sırasını garanti etmez;
/// sıra değişirse fark "değişmiş" görünebilir. Bu, yanlış bir hesap değil, sırasız sorgunun
/// doğasıdır — kullanıcıya arayüzde söylenir.
/// </summary>
public static class OtoYenileFarki
{
    public static YenilemeFarki Karsilastir(DataTable? onceki, DataTable yeni)
    {
        if (onceki is null)
            return new YenilemeFarki(0, 0, 0, new HashSet<int>(), SekilDegisti: false);

        if (!KolonlarAyni(onceki, yeni))
            return new YenilemeFarki(0, 0, 0, new HashSet<int>(), SekilDegisti: true);

        int ortak = Math.Min(onceki.Rows.Count, yeni.Rows.Count);
        var degisenler = new HashSet<int>();

        for (int satir = 0; satir < ortak; satir++)
        {
            if (SatirFarkli(onceki.Rows[satir], yeni.Rows[satir], yeni.Columns.Count))
                degisenler.Add(satir);
        }

        return new YenilemeFarki(
            Eklenen: Math.Max(0, yeni.Rows.Count - onceki.Rows.Count),
            Silinen: Math.Max(0, onceki.Rows.Count - yeni.Rows.Count),
            Degisen: degisenler.Count,
            DegisenSatirlar: degisenler,
            SekilDegisti: false);
    }

    private static bool KolonlarAyni(DataTable a, DataTable b)
    {
        if (a.Columns.Count != b.Columns.Count)
            return false;

        for (int i = 0; i < a.Columns.Count; i++)
        {
            if (!string.Equals(a.Columns[i].ColumnName, b.Columns[i].ColumnName, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Hücreler METİN olarak kıyaslanır — gridde görünen değer budur ve
    /// <see cref="SonucKarsilastirici"/> de aynı yolu izler (tip/kültür tuzağı yok).
    /// </summary>
    private static bool SatirFarkli(DataRow a, DataRow b, int kolonSayisi)
    {
        for (int k = 0; k < kolonSayisi; k++)
        {
            if (!string.Equals(SonucBicimleyici.HucreMetni(a[k]), SonucBicimleyici.HucreMetni(b[k]),
                    StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
