using System.Xml.Linq;

namespace SQLST.Application;

/// <summary>
/// 🔐 WS-Security UsernameToken üreticisi (v19-S2, 2026-08-03). Zarfın <c>Header</c>'ına
/// OASIS UsernameToken bloğu (Username + PasswordText + Nonce + Created) enjekte eder.
/// SAF ve testli — SQLST felsefesi gereği başlık istek anında gizlice değil, DÜĞMEYLE zarfa
/// yazılır: kullanıcı ne gönderdiğini editörde GÖRÜR ve düzenleyebilir. Nonce/zaman dışarıdan
/// gelir (testlenebilirlik); PasswordText v1 kapsamıdır (PasswordDigest kuyruk borcu değil —
/// ihtiyaç doğarsa istenir).
/// </summary>
public static class WsSecurityUretici
{
    private static readonly XNamespace Wsse =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    private static readonly XNamespace Wsu =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    private const string PasswordText =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";

    private const string Base64Binary =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    /// <summary>
    /// Zarfa UsernameToken ekler; başarıda yeni zarf metni, sorunda kullanıcıya söylenecek hata
    /// döner. Var olan <c>Header</c> korunur (yoksa oluşturulur); zarfta ZATEN wsse:Security varsa
    /// dokunulmaz — kullanıcının elle yazdığı başlık ezilmez.
    /// </summary>
    public static (string? Zarf, string? Hata) UsernameTokenEkle(
        string zarf, string kullanici, string parola, string nonceBase64, DateTime utcSimdi)
    {
        if (string.IsNullOrWhiteSpace(kullanici))
            return (null, "WS-Security için kullanıcı adı gerekli (kimlik satırındaki kutudan alınır).");

        XDocument belge;
        try
        {
            belge = XDocument.Parse(zarf);
        }
        catch (System.Xml.XmlException ex)
        {
            return (null, $"Zarf XML olarak çözümlenemedi: {ex.Message}");
        }

        XElement kok = belge.Root!;
        if (kok.Name.LocalName != "Envelope")
            return (null, "Zarfın kökü SOAP Envelope değil — WS-Security başlığı eklenemedi.");

        XNamespace soap = kok.Name.Namespace;
        XElement? header = kok.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
        if (header is not null
            && header.Descendants().Any(e => e.Name.LocalName == "Security"))
            return (null, "Zarfta zaten bir Security başlığı var — üzerine yazılmadı (elle düzenleyin).");

        if (header is null)
        {
            header = new XElement(soap + "Header");
            kok.AddFirst(header);
        }

        header.Add(new XElement(Wsse + "Security",
            new XAttribute(XNamespace.Xmlns + "wsse", Wsse),
            new XAttribute(XNamespace.Xmlns + "wsu", Wsu),
            new XAttribute(soap + "mustUnderstand", "1"),
            new XElement(Wsse + "UsernameToken",
                new XElement(Wsse + "Username", kullanici),
                new XElement(Wsse + "Password", new XAttribute("Type", PasswordText), parola),
                new XElement(Wsse + "Nonce", new XAttribute("EncodingType", Base64Binary), nonceBase64),
                new XElement(Wsu + "Created",
                    utcSimdi.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)))));

        // ToString tüm belgeyi tutarlı girintiler — zarf editörde okunaklı görünür.
        return (belge.ToString(), null);
    }
}
