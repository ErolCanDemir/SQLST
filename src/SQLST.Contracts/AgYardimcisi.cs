using System.Net;

namespace SQLST.Contracts;

/// <summary>
/// Ağ yardımcıları (v20-S14) — SAF, birim testli. <see cref="YerelMi"/>: bir URI'nin kurumsal proxy'den
/// MUAF tutulması gereken yerel/intranet adres olup olmadığı. REST + SOAP istemcileri PAYLAŞIR (önceden
/// her ikisinde ayrı ayrı kopyalıydı; kullanıcı bulgusu 2026-08-10: WSDL'den gelen LAN IP'li SOAP adresi
/// proxy'de "düşmüyor"/asılı kalıyor). Yerel sayılanlar: loopback (localhost / 127.* / ::1); noktasız host
/// (intranet makine adı); ÖZEL/RFC1918 IPv4 aralıkları (10/8, 172.16-31/12, 192.168/16); IPv4 link-local
/// (169.254/16); IPv6 link-local (fe80::/10) ve unique-local (fc00::/7). Bunlar proxy'siz gider.
/// </summary>
public static class AgYardimcisi
{
    public static bool YerelMi(Uri u)
    {
        if (u.IsLoopback)
            return true;

        string host = u.Host;
        // Noktasız ve iki-nokta-üst-üstesiz host = intranet makine adı (IPv6 literal değil).
        if (!host.Contains('.') && !host.Contains(':'))
            return true;

        return IPAddress.TryParse(host, out IPAddress? ip) && OzelMi(ip);
    }

    private static bool OzelMi(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4) // IPv4
            return b[0] == 10                                // 10.0.0.0/8
                || (b[0] == 172 && b[1] is >= 16 and <= 31)  // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)              // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254);             // 169.254.0.0/16 (link-local)

        if (b.Length == 16) // IPv6
            return ip.IsIPv6LinkLocal                        // fe80::/10
                || (b[0] & 0xFE) == 0xFC;                    // fc00::/7 (unique local)

        return false;
    }
}
