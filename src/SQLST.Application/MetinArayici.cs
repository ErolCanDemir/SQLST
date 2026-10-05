using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Bir nesne tanımındaki tek eşleşme: satır numarası + o satırın metni.</summary>
/// <param name="Satir">1'den başlayan satır numarası — sekmede açılınca imleç buraya gider.</param>
public sealed record AramaEslesmesi(int Satir, string SatirMetni);

/// <summary>
/// Aramada eşleşen bir nesne. <paramref name="Tanim"/> saklanır ki kullanıcı sonuca çift
/// tıkladığında tanım YENİDEN SORGULANMADAN sekmede açılabilsin.
/// </summary>
public sealed record AramaSonucu(
    string Sema,
    string Ad,
    SemaNesneTuru Tur,
    string Tanim,
    IReadOnlyList<AramaEslesmesi> Eslesmeler)
{
    public string TamAd => $"{Sema}.{Ad}";
    public int EslesmeSayisi => Eslesmeler.Count;
}

/// <summary>
/// Sunucuda metin arama sonuçlarının MOTOR-NÖTR işlenmesi (V5-S2).
///
/// Süzme sunucuda yapılır (<see cref="ILehce.MetinAramaSorgusu"/>); burada yalnız dönen
/// tanım metninde <b>satır numarası ve bağlam</b> çıkarılır. Bu mantık motora bağlı
/// olmadığından bir kez yazılır — beş motor da aynı kodu kullanır.
///
/// <b>Kapsam (kullanıcı kararı 2026-07-19):</b> yalnız nesne tanımları, yalnız seçili
/// veritabanı. Veri içinde arama YOKTUR: <c>LIKE '%x%'</c> baştan joker olduğu için index
/// kullanılamaz ve her tablo baştan sona taranırdı.
/// </summary>
public static class MetinArayici
{
    /// <summary>
    /// Kanonik sonuç kümesini (sema, ad, tür-kodu, tanım) arama sonuçlarına çevirir.
    /// Tanımı okunamayan (NULL/boş) nesneler atlanır — şifreli SP'ler böyledir.
    /// </summary>
    public static IReadOnlyList<AramaSonucu> Cozumle(
        QueryResult sonuc, ILehce lehce, string aranan, bool buyukKucukDuyarli = false)
    {
        if (string.IsNullOrEmpty(aranan) || sonuc.ResultSetler.Count == 0)
            return [];

        var liste = new List<AramaSonucu>();

        foreach (object?[] satir in sonuc.ResultSetler[0].Satirlar)
        {
            if (satir.Length < 4)
                continue;

            string tanim = satir[3]?.ToString() ?? "";
            if (tanim.Length == 0)
                continue;                       // şifreli/okunamayan tanım

            IReadOnlyList<AramaEslesmesi> eslesmeler = Eslesmeler(tanim, aranan, buyukKucukDuyarli);
            if (eslesmeler.Count == 0)
                continue;                       // sunucu süzdü ama istemci ölçütü daha darsa

            liste.Add(new AramaSonucu(
                Sema: satir[0]?.ToString() ?? "",
                Ad: satir[1]?.ToString() ?? "",
                Tur: TuruCoz(lehce, satir[2]?.ToString()),
                Tanim: tanim,
                Eslesmeler: eslesmeler));
        }

        // Çok eşleşmeli nesneler üstte: kullanıcı genelde "en çok geçtiği yer"i arar
        return [.. liste.OrderByDescending(s => s.EslesmeSayisi).ThenBy(s => s.TamAd)];
    }

    /// <summary>
    /// Tanım metnindeki eşleşmelerin satır numaraları. Aynı satırda birden çok geçiş varsa
    /// satır BİR KEZ raporlanır — kullanıcı satıra gidiyor, karaktere değil.
    /// </summary>
    public static IReadOnlyList<AramaEslesmesi> Eslesmeler(
        string tanim, string aranan, bool buyukKucukDuyarli = false)
    {
        if (string.IsNullOrEmpty(aranan))
            return [];

        StringComparison kiyas = buyukKucukDuyarli
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var bulunan = new List<AramaEslesmesi>();
        string[] satirlar = tanim.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int i = 0; i < satirlar.Length; i++)
        {
            if (satirlar[i].Contains(aranan, kiyas))
                bulunan.Add(new AramaEslesmesi(i + 1, Kirp(satirlar[i])));
        }

        return bulunan;
    }

    /// <summary>Uzun satırlar listeyi bozmasın: baştaki girinti atılır, sonu kırpılır.</summary>
    private static string Kirp(string satir)
    {
        string s = satir.Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    /// <summary>
    /// Tür kodu motora özgüdür (<c>sys.objects.type</c>, PG'de kendi ürettiğimiz 'P'/'V'…).
    /// Lehçe tanımıyorsa arama yine çalışmalı → bilinmeyen kod StoredProcedure sayılır,
    /// çünkü sonuç listesinde tür yalnız SİMGE içindir, davranışı belirlemez.
    /// </summary>
    private static SemaNesneTuru TuruCoz(ILehce lehce, string? kod)
    {
        if (string.IsNullOrWhiteSpace(kod))
            return SemaNesneTuru.StoredProcedure;

        try
        {
            return lehce.TurCevir(kod);
        }
        catch (ArgumentOutOfRangeException)
        {
            return SemaNesneTuru.StoredProcedure;
        }
    }
}
