using System.Text;

namespace SQLST.Contracts;

/// <summary>
/// TXT/CSV ayrıştırıcı (v13-S1) — SAF: <see cref="TextReader"/> üzerinden AKIŞLI okur (dev dosya
/// belleğe alınmaz), satırları alan dizisi olarak verir. Kurallar (RFC 4180 uyumlu alt küme):
///  • Kolon ayracı çağırandan gelir (';' ',' '\t' '|' …).
///  • Satır sonu CRLF / LF / CR — OTOMATİK; <paramref name="satirAyraci"/> verilirse o karakter
///    CRLF/LF'ye EK satır sonudur (kullanıcı kuralı 2026-07-26: TXT'de satır ayracı da seçilir —
///    "kayıt1|kayıt2|…" gibi tek fiziksel satırlık ihraç dosyaları böyle açılır). Tırnak
///    İÇİNDEKİ satır sonu/ayraç alana dahildir.
///  • Alan çift tırnakla başlıyorsa tırnaklıdır: içindeki ayraç/satır sonu bölmez,
///    <c>""</c> tek tırnak demektir. Tırnaksız alan olduğu gibi alınır (trim YAPILMAZ —
///    boşluk veridir; kırpma kararı çağıranın).
///  • Boş satırlar atlanır (dosya sonundaki son boş satır dahil).
/// </summary>
public static class MetinTabloAyristirici
{
    public static IEnumerable<string[]> Ayristir(TextReader okuyucu, char ayrac, char? satirAyraci = null)
    {
        var alanlar = new List<string>();
        var alan = new StringBuilder();
        bool tirnakta = false, alanBasi = true, satirdaVeriVar = false;

        int c;
        while ((c = okuyucu.Read()) >= 0)
        {
            char k = (char)c;

            if (tirnakta)
            {
                if (k == '"')
                {
                    if (okuyucu.Peek() == '"')
                    {
                        okuyucu.Read();
                        alan.Append('"'); // "" → tek tırnak
                    }
                    else
                    {
                        tirnakta = false; // kapanış — ayraç/satır sonu beklenir
                    }
                }
                else
                {
                    alan.Append(k);
                }

                continue;
            }

            if (k == '"' && alanBasi)
            {
                tirnakta = true;
                alanBasi = false;
                satirdaVeriVar = true;
                continue;
            }

            if (k == ayrac)
            {
                alanlar.Add(alan.ToString());
                alan.Clear();
                alanBasi = true;
                satirdaVeriVar = true;
                continue;
            }

            if (k is '\r' or '\n' || k == satirAyraci)
            {
                if (k == '\r' && okuyucu.Peek() == '\n')
                    okuyucu.Read(); // CRLF tek satır sonu

                if (satirdaVeriVar || alan.Length > 0)
                {
                    alanlar.Add(alan.ToString());
                    yield return [.. alanlar];
                }

                alanlar.Clear();
                alan.Clear();
                alanBasi = true;
                satirdaVeriVar = false;
                continue;
            }

            alan.Append(k);
            alanBasi = false;
            satirdaVeriVar = true;
        }

        // Son satır (dosya satır sonu olmadan bitebilir)
        if (satirdaVeriVar || alan.Length > 0)
        {
            alanlar.Add(alan.ToString());
            yield return [.. alanlar];
        }
    }
}
