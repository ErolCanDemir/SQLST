namespace SQLST.Application;

/// <summary>Grid hücre seçiminin özeti (V2-S6): Excel durum çubuğu deseni.</summary>
public sealed record SecimIstatistigi(
    int Adet, int SayisalAdet, decimal? Toplam, decimal? Ortalama, decimal? EnKucuk, decimal? EnBuyuk)
{
    /// <summary>Durum metni — mevcut kültürle (tr-TR: 1.234,56) biçimlenir.</summary>
    public string Metin
    {
        get
        {
            if (Adet < 2)
                return ""; // tek hücre için istatistik gürültü
            string ozet = $"seçim: {Adet} hücre";
            if (SayisalAdet > 0)
            {
                ozet += $"  ·  SUM {Bicimle(Toplam)}  ·  AVG {Bicimle(Ortalama)}"
                      + $"  ·  MIN {Bicimle(EnKucuk)}  ·  MAX {Bicimle(EnBuyuk)}";
            }
            return ozet;
        }
    }

    private static string Bicimle(decimal? d) => d?.ToString("#,0.####") ?? "";
}

/// <summary>Seçili hücrelerden bellek-içi istatistik (COUNT/SUM/AVG/MIN/MAX — FG kapsamı V2-S6).</summary>
public static class SecimIstatistikcisi
{
    public static SecimIstatistigi Hesapla(IEnumerable<object?> degerler)
    {
        int adet = 0, sayisal = 0;
        decimal toplam = 0, enKucuk = decimal.MaxValue, enBuyuk = decimal.MinValue;

        foreach (object? deger in degerler)
        {
            adet++;
            if (SayiyaCevir(deger) is not { } sayi)
                continue;
            sayisal++;
            toplam += sayi;
            if (sayi < enKucuk) enKucuk = sayi;
            if (sayi > enBuyuk) enBuyuk = sayi;
        }

        return sayisal == 0
            ? new SecimIstatistigi(adet, 0, null, null, null, null)
            : new SecimIstatistigi(adet, sayisal, toplam, toplam / sayisal, enKucuk, enBuyuk);
    }

    private static decimal? SayiyaCevir(object? deger)
    {
        try
        {
            return deger switch
            {
                sbyte or byte or short or ushort or int or uint or long or ulong or decimal
                    => Convert.ToDecimal(deger),
                float f => (decimal)f,
                double d => (decimal)d,
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null; // decimal aralığı dışındaki double — istatistiğe girmesin
        }
    }
}
