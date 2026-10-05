namespace SQLST.Application;

/// <summary>AI cevaplarından JSON ayıklama yardımcıları (v20-S13) — SAF; yerel model önsöz/çit eklese bile çözülebilsin diye.</summary>
internal static class JsonMetinAraci
{
    /// <summary>
    /// Metindeki İLK dengeli <c>{ … }</c> nesnesini döndürür (AI açıklama ya da <c>```json</c> çiti eklemiş
    /// olabilir); string içi tırnak/kaçış gözetilir (gövdedeki <c>}</c> bloğu erken kapatmaz). Yoksa null.
    /// </summary>
    public static string? IlkDengeliNesne(string metin)
    {
        int basla = metin.IndexOf('{');
        if (basla < 0)
            return null;

        int derinlik = 0;
        bool tirnakta = false, kacis = false;
        for (int i = basla; i < metin.Length; i++)
        {
            char c = metin[i];
            if (kacis) { kacis = false; continue; }
            if (c == '\\') { kacis = true; continue; }
            if (c == '"') { tirnakta = !tirnakta; continue; }
            if (tirnakta) continue;

            if (c == '{')
                derinlik++;
            else if (c == '}' && --derinlik == 0)
                return metin[basla..(i + 1)];
        }
        return null; // kapanmayan blok
    }
}
