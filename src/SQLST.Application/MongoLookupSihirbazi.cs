using System.Text.Json;

namespace SQLST.Application;

/// <summary>
/// v20-S21 m.26 fikir 7 ("$lookup sihirbazı"): iki koleksiyonu birleştiren pipeline — Mongo'nun
/// JOIN karşılığı, elle yazması en zahmetli adım. Bu sınıf saf üreticidir: alanlar seçilir,
/// pipeline metni çıkar. SQLST'nin çalıştırdığı biçimde yazılır ({"aggregate": …, "pipeline": […]}),
/// böylece "Sekmede aç" doğrudan koşar.
/// </summary>
public static class MongoLookupSihirbazi
{
    /// <summary>
    /// Önizleme satır tavanı (v22-S1 saha turu-2 m.13, kullanıcı: "sihirbazın ürettiği sorgu çok yavaş").
    /// ÖLÇÜM (yerel MongoDB; ana 50.000 · hedef 10.000 belge — kullanıcının koleksiyonlarının çok
    /// altında): <b>limitsiz sorgu 60 sn tavanına dayanıp iptal oldu (0 satır)</b>; aynı sorgu
    /// <c>$limit 200</c> ile 16,9 sn; hedef alan index'liyken <b>143 ms</b>. Sihirbaz bir KEŞİF
    /// aracıdır — varsayılanı bütün koleksiyonu birleştirmek değil, ilk N eşleşmeyi göstermek olmalı.
    /// </summary>
    public const int VarsayilanOnizleme = 200;

    /// <param name="duzlestir">Tek eşleşmede diziyi belgeye indirger ($unwind) — JOIN'e en yakın hâl.</param>
    /// <param name="eslesmeyenlerDeGelsin">LEFT JOIN karşılığı: eşleşmesi olmayan ana belgeler de kalır
    /// (<c>preserveNullAndEmptyArrays</c>). Yalnız düzleştirmeyle anlamlıdır — düzleştirme yoksa dizi
    /// zaten boş gelir ve belge zaten korunur.</param>
    /// <param name="onizleme">Sona eklenecek <c>$limit</c> (v22-S1 m.13). null/0 → sınırsız (kullanıcı
    /// bilerek seçerse). Limit SONA konur — <c>$lookup</c>'tan ÖNCE konulsaydı yalnız ilk N ANA belge
    /// birleştirilir, eşleşmeler sessizce kaybolurdu (ölçümde 200 yerine 40 satır); sonda ise anlam
    /// korunur ve sunucu ilk N eşleşmede durur.</param>
    public static string PipelineYaz(
        string anaKoleksiyon, string baglanacak, string yerelAlan, string hedefAlan,
        string sonucAlani, bool duzlestir, bool eslesmeyenlerDeGelsin,
        int? onizleme = VarsayilanOnizleme)
    {
        var adimlar = new List<string>
        {
            $$"""
                { "$lookup": { "from": {{M(baglanacak)}}, "localField": {{M(yerelAlan)}},
                    "foreignField": {{M(hedefAlan)}}, "as": {{M(sonucAlani)}} } }
              """.Trim(),
        };

        if (duzlestir)
        {
            adimlar.Add(eslesmeyenlerDeGelsin
                ? $$"""{ "$unwind": { "path": {{M("$" + sonucAlani)}}, "preserveNullAndEmptyArrays": true } }"""
                : $$"""{ "$unwind": {{M("$" + sonucAlani)}} }""");
        }

        if (onizleme is > 0)
            adimlar.Add($$"""{ "$limit": {{onizleme}} }""");

        return $$"""
            { "aggregate": {{M(anaKoleksiyon)}}, "pipeline": [
              {{string.Join(",\n  ", adimlar)}}
            ] }
            """;
    }

    /// <summary>Sonuç alanı için makul varsayılan: bağlanan koleksiyonun adı (çoğul eki kabaca atılır).</summary>
    public static string VarsayilanSonucAlani(string baglanacak)
    {
        if (string.IsNullOrWhiteSpace(baglanacak))
            return "eslesen";
        string ad = baglanacak.Trim();
        foreach (string ek in new[] { "lar", "ler" })
        {
            if (ad.Length > ek.Length + 2 && ad.EndsWith(ek, StringComparison.OrdinalIgnoreCase))
                return ad[..^ek.Length];
        }
        return ad.EndsWith('s') && ad.Length > 3 ? ad[..^1] : ad;
    }

    private static string M(string deger) => JsonSerializer.Serialize(deger);
}
