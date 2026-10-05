using System.Data;

namespace SQLST.Application;

/// <summary>
/// Sonuç grid'i kolon araçları (v18 — kullanıcı isteği 2026-07-28): geniş sonuçlarda kolon göster/gizle.
/// SAF mantık (IO yok, UI yok): "tümü boş kolonlar" tespiti. Görünürlük değişimi UI tarafında
/// <c>DataGridColumn.Visibility</c> ile yapılır — hücre binding'ine DOKUNULMAZ (maske modunun yol
/// açtığı "kolon kayboluyor" hatasının aksine, bu güvenli ve verinin kendisini değiştirmez).
/// </summary>
public static class GridKolonAraclari
{
    /// <summary>
    /// Bu result set'te TÜMÜ boş (her satırda NULL ya da boş metin) olan kolonların adları — "boş
    /// kolonları gizle" bunları saklar (ör. çoğu NULL bir Kişi satırında 20 boş kolon ekranı doldurur).
    /// 0 satırlı sette hiçbir kolon "boş" sayılmaz (yanlışlıkla hepsini gizlememek için).
    /// </summary>
    public static IReadOnlyList<string> BosKolonlar(DataTable tablo)
    {
        if (tablo.Rows.Count == 0)
            return [];

        var bos = new List<string>();
        List<DataRow> satirlar = [.. tablo.Rows.Cast<DataRow>()];
        foreach (DataColumn kolon in tablo.Columns)
        {
            if (satirlar.All(r => HucreBos(r[kolon])))
                bos.Add(kolon.ColumnName);
        }

        return bos;
    }

    private static bool HucreBos(object? deger)
        => deger is null or DBNull || (deger is string s && s.Length == 0);
}
