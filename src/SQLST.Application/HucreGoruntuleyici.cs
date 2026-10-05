using System.Text.Encodings.Web;
using System.Text.Json;

namespace SQLST.Application;

/// <summary>
/// JSON/uzun hücre görüntüleyici çekirdeği (V2-S6, FG-4.7): hücre değerini tam
/// metne çevirir; içerik geçerli JSON ise girintili biçimler.
/// </summary>
public static class HucreGoruntuleyici
{
    private static readonly JsonSerializerOptions GirintiliAyar = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static (string Metin, bool JsonMu) Bicimlendir(object? deger, string? sqlTip = null)
    {
        string ham = SonucBicimleyici.HucreMetni(deger, sqlTip); // v23-S13: kültürsüz ham metin
        string kirpik = ham.TrimStart();
        if (kirpik.Length == 0 || (kirpik[0] != '{' && kirpik[0] != '['))
            return (ham, false);

        try
        {
            using JsonDocument belge = JsonDocument.Parse(ham);
            return (JsonSerializer.Serialize(belge.RootElement, GirintiliAyar), true);
        }
        catch (JsonException)
        {
            return (ham, false); // { ile başlayan ama JSON olmayan metin — olduğu gibi
        }
    }
}
