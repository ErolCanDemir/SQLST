namespace SQLST.Infrastructure;

/// <summary>
/// Profil "Sunucu" alanındaki "host[:port]" ayrımı (PG/MySQL/Mongo ortak deseni).
/// IPv6 bekleyeni (inceleme 2026-07-30): çıplak "::1" adresinde son iki nokta port sanılıyordu
/// ("host=':', port=1"). Kural: köşeli parantez RFC 3986 biçimidir ("[::1]:5432"); parantezsiz
/// dizede BİRDEN ÇOK iki nokta varsa tamamı IPv6 host kabul edilir, port ayrılmaz.
/// </summary>
public static class HostPortAyristirici
{
    public static (string Host, int? Port) Ayir(string sunucu)
    {
        string s = sunucu.Trim();

        if (s.StartsWith('['))
        {
            int kapanis = s.IndexOf(']');
            if (kapanis < 0)
                return (s, null); // bozuk giriş — sürücü kendi hatasını versin
            string host = s[1..kapanis];
            string kalan = s[(kapanis + 1)..];
            return kalan.StartsWith(':') && int.TryParse(kalan[1..], out int p6) && p6 is > 0 and <= 65535
                ? (host, p6)
                : (host, null);
        }

        // Parantezsiz: tek iki nokta → host:port; birden çok → çıplak IPv6, dokunma.
        int ilk = s.IndexOf(':');
        if (ilk < 0 || ilk != s.LastIndexOf(':'))
            return (s, null);
        return ilk > 0 && int.TryParse(s[(ilk + 1)..], out int p) && p is > 0 and <= 65535
            ? (s[..ilk], p)
            : (s, null);
    }
}
