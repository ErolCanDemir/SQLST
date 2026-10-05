using System.Text;
using System.Xml.Linq;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// WSDL 1.1 çözümleyici (v14-S1) — SAF: indirme yok, kendisine verilen ana WSDL + (WCF'nin
/// çok parçalı yayınında) ek XSD belgelerinden <see cref="SoapServis"/> çıkarır ve her operasyon
/// için document/literal-wrapped <b>örnek SOAP 1.1 zarfı</b> üretir.
///
/// Hedef, SP saran WCF servislerinin standardı: basicHttpBinding + document/literal.
/// Anlaşılamayan uçlar (girift XSD, rpc stili…) SESSİZCE atlanmaz — <see cref="SoapServis.Uyarilar"/>
/// listesine yazılır; zarf ekranda elle düzenlenebildiği için bunlar çoğu zaman engel değildir.
/// Örnek değerler tip bazlıdır (int→0, dateTime→2026-01-01…); dizilerde tek örnek eleman +
/// "tekrarlayabilirsiniz" yorumu bırakılır; iç DataContract tipleri kendi namespace'iyle nitelenir
/// (WCF şemaları elementFormDefault=qualified yayınlar).
/// </summary>
public static class WsdlCozumleyici
{
    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace Soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";
    private const int EnDerin = 4; // iç içe karmaşık tip örnekleme sınırı (döngü/şişme koruması)

    public static SoapServis Cozumle(string anaWsdl, IReadOnlyList<string>? ekBelgeler = null)
    {
        var uyarilar = new List<string>();
        XDocument ana = XDocument.Parse(anaWsdl);
        XElement kok = ana.Root ?? throw new InvalidOperationException("WSDL boş.");
        string tns = kok.Attribute("targetNamespace")?.Value ?? "";

        // Şema envanteri: ana belgedeki + ek belgelerdeki TÜM xsd:schema'lar.
        var semalar = new List<XElement>();
        semalar.AddRange(kok.Descendants(Xsd + "schema"));
        foreach (string ek in ekBelgeler ?? [])
        {
            XElement? ekKok = XDocument.Parse(ek).Root;
            if (ekKok is null)
                continue;
            semalar.AddRange(ekKok.Name == Xsd + "schema"
                ? [ekKok] : ekKok.Descendants(Xsd + "schema"));
        }

        var envanter = new SemaEnvanteri(semalar);

        // message adı → tek part'ın element QName'i (doc/literal: parts tek ve element'lidir)
        var mesajlar = new Dictionary<string, XName>(StringComparer.Ordinal);
        foreach (XElement mesaj in kok.Elements(Wsdl + "message"))
        {
            XElement? part = mesaj.Element(Wsdl + "part");
            string? elementRef = part?.Attribute("element")?.Value;
            if (mesaj.Attribute("name")?.Value is { } ad && elementRef is not null)
                mesajlar[ad] = QNameCoz(elementRef, part!);
        }

        // portType: operasyon → girdi mesajı
        var girdiler = new Dictionary<string, XName>(StringComparer.Ordinal);
        foreach (XElement pt in kok.Elements(Wsdl + "portType"))
        foreach (XElement op in pt.Elements(Wsdl + "operation"))
        {
            string? ad = op.Attribute("name")?.Value;
            string? mesajRef = op.Element(Wsdl + "input")?.Attribute("message")?.Value;
            if (ad is null || mesajRef is null)
                continue;
            string yerel = mesajRef.Contains(':') ? mesajRef[(mesajRef.IndexOf(':') + 1)..] : mesajRef;
            if (mesajlar.TryGetValue(yerel, out XName girdi))
                girdiler[ad] = girdi;
        }

        // binding: soapAction'lar (SOAP 1.1 öncelik, yoksa 1.2)
        var aksiyonlar = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement b in kok.Elements(Wsdl + "binding"))
        foreach (XElement op in b.Elements(Wsdl + "operation"))
        {
            string? ad = op.Attribute("name")?.Value;
            string? aksiyon = op.Element(Soap + "operation")?.Attribute("soapAction")?.Value
                ?? op.Element(Soap12 + "operation")?.Attribute("soapAction")?.Value;
            if (ad is not null && aksiyon is not null && !aksiyonlar.ContainsKey(ad))
                aksiyonlar[ad] = aksiyon;
        }

        // service/port: soap:address (ilk bulunan)
        string adres = kok.Elements(Wsdl + "service")
            .SelectMany(s => s.Elements(Wsdl + "port"))
            .Select(p => p.Element(Soap + "address") ?? p.Element(Soap12 + "address"))
            .FirstOrDefault(a => a is not null)?.Attribute("location")?.Value ?? "";
        string servisAdi = kok.Elements(Wsdl + "service").FirstOrDefault()
            ?.Attribute("name")?.Value ?? "Servis";

        var operasyonlar = new List<SoapOperasyon>();
        foreach ((string ad, XName girdiOgesi) in girdiler.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            (IReadOnlyList<SoapParametre> parametreler, string gövdeXml) =
                envanter.GovdeUret(girdiOgesi, uyarilar);
            operasyonlar.Add(new SoapOperasyon(
                ad,
                aksiyonlar.GetValueOrDefault(ad, ""),
                parametreler,
                Zarf(gövdeXml)));
        }

        if (operasyonlar.Count == 0)
            uyarilar.Add("WSDL'de document/literal operasyon bulunamadı (rpc stili desteklenmez).");

        return new SoapServis(servisAdi, adres, operasyonlar, uyarilar);
    }

    private static string Zarf(string govde) =>
        "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">\n"
        + "  <soapenv:Header/>\n"
        + "  <soapenv:Body>\n"
        + govde
        + "  </soapenv:Body>\n"
        + "</soapenv:Envelope>";

    private static XName QNameCoz(string deger, XElement baglam)
    {
        int i = deger.IndexOf(':');
        if (i < 0)
            return XName.Get(deger, baglam.GetDefaultNamespace().NamespaceName);
        XNamespace? ns = baglam.GetNamespaceOfPrefix(deger[..i]);
        return XName.Get(deger[(i + 1)..], ns?.NamespaceName ?? "");
    }

    /// <summary>Toplanan şemalardan element/tip arayan ve örnek gövde üreten iç yardımcı.</summary>
    private sealed class SemaEnvanteri(IReadOnlyList<XElement> semalar)
    {
        public (IReadOnlyList<SoapParametre>, string) GovdeUret(XName kokOge, List<string> uyarilar)
        {
            XElement? eleman = ElementBul(kokOge);
            var parametreler = new List<SoapParametre>();
            var sb = new StringBuilder();
            sb.Append($"    <{kokOge.LocalName} xmlns=\"{kokOge.NamespaceName}\">\n");
            if (eleman is null)
            {
                uyarilar.Add($"'{kokOge.LocalName}' öğesi şemada bulunamadı — zarfı elle doldurun.");
                sb.Append("      <!-- parametreler şemadan çözülemedi -->\n");
            }
            else
            {
                foreach (XElement cocuk in SiraliCocuklar(eleman))
                {
                    string ad = cocuk.Attribute("name")?.Value ?? "?";
                    string tipAdi = cocuk.Attribute("type")?.Value ?? "(iç tanım)";
                    bool dizi = Dizi(cocuk);
                    parametreler.Add(new SoapParametre(ad, TipYerel(tipAdi), dizi, Secimlik(cocuk)));
                    OgeYaz(sb, cocuk, kokOge.NamespaceName, girinti: 3, derinlik: 1, uyarilar);
                }
            }

            sb.Append($"    </{kokOge.LocalName}>\n");
            return (parametreler, sb.ToString());
        }

        private XElement? ElementBul(XName qname) => semalar
            .Where(s => (s.Attribute("targetNamespace")?.Value ?? "") == qname.NamespaceName)
            .SelectMany(s => s.Elements(Xsd + "element"))
            .FirstOrDefault(e => e.Attribute("name")?.Value == qname.LocalName);

        private XElement? TipBul(XName qname) => semalar
            .Where(s => (s.Attribute("targetNamespace")?.Value ?? "") == qname.NamespaceName)
            .SelectMany(s => s.Elements(Xsd + "complexType"))
            .FirstOrDefault(t => t.Attribute("name")?.Value == qname.LocalName);

        private static IEnumerable<XElement> SiraliCocuklar(XElement elementYaDaTip)
        {
            XElement? tip = elementYaDaTip.Name == Xsd + "element"
                ? elementYaDaTip.Element(Xsd + "complexType") : elementYaDaTip;
            return tip?.Element(Xsd + "sequence")?.Elements(Xsd + "element") ?? [];
        }

        /// <summary>Bir şema öğesini örnek değerle yazar; karmaşık tipe derinlik sınırıyla iner.</summary>
        private void OgeYaz(StringBuilder sb, XElement oge, string ustNs, int girinti, int derinlik, List<string> uyarilar)
        {
            string pad = new(' ', girinti * 2);
            string ad = oge.Attribute("name")?.Value ?? "?";
            string? tipRef = oge.Attribute("type")?.Value;
            if (Dizi(oge))
                sb.Append($"{pad}<!-- '{ad}' listedir: öğeyi gerektiği kadar tekrarlayın -->\n");

            XName? tipQ = tipRef is null ? null : QNameCoz(tipRef, oge);
            bool basit = tipQ?.NamespaceName == Xsd.NamespaceName;
            if (basit || tipRef is null && oge.Element(Xsd + "complexType") is null)
            {
                sb.Append($"{pad}<{ad}>{OrnekDeger(tipQ?.LocalName)}</{ad}>\n");
                return;
            }

            // Karmaşık tip: adlı tip başka (DataContract) şemasında olabilir — kendi ns'iyle nitele.
            XElement? tip = tipQ is null ? oge : TipBul(tipQ);
            string icNs = tipQ?.NamespaceName ?? ustNs;
            string nsNitelik = icNs == ustNs ? "" : $" xmlns=\"{icNs}\"";
            sb.Append($"{pad}<{ad}{nsNitelik}>\n");
            if (tip is null)
            {
                uyarilar.Add($"'{tipQ!.LocalName}' tipi şemalarda bulunamadı — '{ad}' içeriğini elle doldurun.");
                sb.Append($"{pad}  <!-- içerik çözülemedi -->\n");
            }
            else if (derinlik >= EnDerin)
            {
                sb.Append($"{pad}  <!-- derinlik sınırı: içeriği elle doldurun -->\n");
            }
            else
            {
                foreach (XElement cocuk in SiraliCocuklar(tip))
                    OgeYaz(sb, cocuk, icNs, girinti + 1, derinlik + 1, uyarilar);
            }

            sb.Append($"{pad}</{ad}>\n");
        }

        private static bool Dizi(XElement oge) =>
            oge.Attribute("maxOccurs")?.Value is { } m && m != "0" && m != "1";

        private static bool Secimlik(XElement oge) =>
            oge.Attribute("minOccurs")?.Value == "0" || oge.Attribute("nillable")?.Value == "true";

        private static string TipYerel(string tipRef)
            => tipRef.Contains(':') ? tipRef[(tipRef.IndexOf(':') + 1)..] : tipRef;

        private static string OrnekDeger(string? xsdTip) => xsdTip switch
        {
            "int" or "long" or "short" or "byte" or "integer"
                or "unsignedInt" or "unsignedLong" or "unsignedShort" => "0",
            "decimal" or "double" or "float" => "0.0",
            "boolean" => "false",
            "dateTime" => "2026-01-01T00:00:00",
            "date" => "2026-01-01",
            "time" => "00:00:00",
            "base64Binary" or "hexBinary" => "",
            "guid" => "00000000-0000-0000-0000-000000000000",
            _ => "?",
        };
    }
}
