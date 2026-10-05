using System.Text;
using System.Text.Json;

namespace SQLST.Application;

/// <summary>Mongo "find yardımcısı" (v8) alan kümesi: koleksiyon + filter/sort/projection + limit/skip.</summary>
public sealed record MongoBulIstegi(
    string Koleksiyon, string? Filtre = null, string? Sirala = null,
    string? Yansit = null, int? Limit = null, int? Atla = null);

/// <summary>
/// Mongo filtre/sıralama paneli (v8 ikinci kalem — kullanıcı fikri 2026-07-20): Compass'taki gibi
/// ayrı alanlardan (Filter · Sort · Project · Limit · Skip) SQLST'nin çalıştırdığı <c>find</c> JSON
/// belgesini ÜRETİR (<c>{ "find": …, "filter": …, "sort": …, "projection": …, "skip": …, "limit": … }</c>).
/// SAF mantık — üretilen JSON editöre basılır, kullanıcı F5 ile çalıştırır (elle JSON düzenlemeye gerek yok).
///
/// <b>İki yön</b>: <see cref="Uret"/> (alanlar → JSON) ve <see cref="Ayristir"/> (JSON → alanlar, borç
/// kapanışı) — editördeki mevcut <c>find</c> belgesi panele geri alınıp görsel düzenlenebilir.
/// Filter/Sort/Project girişleri JSON NESNESİ olmalıdır; geçersizse hangi alanın bozuk olduğu söylenir.
/// </summary>
public static class MongoBulYazici
{
    /// <summary>
    /// Bir <c>find</c> belgesini (Uret'in ürettiği ya da elle yazılan) panel alanlarına GERİ ÇÖZER.
    /// Kök bir JSON nesnesi ve <c>"find": "koleksiyon"</c> taşımalıdır; filter/sort/projection alt
    /// nesneleri ham metinleriyle, skip/limit tam sayı olarak geri verilir. Geçersizse (hata, kök nesne
    /// değil, find yok/dize değil) <c>Istek=null</c> ve nedeni döner — panel boş açılır, kullanıcı bilir.
    /// </summary>
    public static (MongoBulIstegi? Istek, string? Hata) Ayristir(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, "Boş metin.");

        JsonDocument belge;
        try
        {
            belge = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return (null, $"Geçerli JSON değil: {ex.Message}");
        }

        using (belge)
        {
            JsonElement kok = belge.RootElement;
            if (kok.ValueKind != JsonValueKind.Object)
                return (null, "Kök bir JSON nesnesi ({…}) olmalı.");
            if (!kok.TryGetProperty("find", out JsonElement bul) || bul.ValueKind != JsonValueKind.String)
                return (null, "Bu bir 'find' belgesi değil (\"find\": \"koleksiyon\" beklenir).");

            string? Nesne(string ad) =>
                kok.TryGetProperty(ad, out JsonElement e) && e.ValueKind == JsonValueKind.Object
                    ? e.GetRawText() : null;
            int? Sayi(string ad) =>
                kok.TryGetProperty(ad, out JsonElement e) && e.ValueKind == JsonValueKind.Number
                && e.TryGetInt32(out int v) ? v : null;

            return (new MongoBulIstegi(
                Koleksiyon: bul.GetString()!,
                Filtre: Nesne("filter"),
                Sirala: Nesne("sort"),
                Yansit: Nesne("projection"),
                Limit: Sayi("limit"),
                Atla: Sayi("skip")), null);
        }
    }

    public static (string? Json, string? Hata) Uret(MongoBulIstegi istek)
    {
        if (string.IsNullOrWhiteSpace(istek.Koleksiyon))
            return (null, "Koleksiyon adı gerekli.");
        if (istek.Limit is < 0)
            return (null, "Limit negatif olamaz.");
        if (istek.Atla is < 0)
            return (null, "Skip negatif olamaz.");

        var sb = new StringBuilder();
        sb.Append("{ \"find\": ").Append(JsonSerializer.Serialize(istek.Koleksiyon.Trim()));

        foreach ((string ad, string? deger) in
            new[] { ("filter", istek.Filtre), ("sort", istek.Sirala), ("projection", istek.Yansit) })
        {
            if (string.IsNullOrWhiteSpace(deger))
                continue;
            (string? nesne, string? hata) = NesneyeNormalle(deger);
            if (nesne is null)
                return (null, $"{ad} geçerli bir JSON nesnesi ({{…}}) olmalı: {hata}");
            sb.Append(", \"").Append(ad).Append("\": ").Append(nesne);
        }

        if (istek.Atla is { } atla)
            sb.Append(", \"skip\": ").Append(atla);
        if (istek.Limit is { } limit)
            sb.Append(", \"limit\": ").Append(limit);
        sb.Append(" }");

        // Okunur olsun diye JSON biçimlendiriciden geçir (varsa); başarısızsa ham metin yeterli.
        (string? guzel, _) = JsonBicimleyici.Bicimlendir(sb.ToString());
        return (guzel ?? sb.ToString(), null);
    }

    /// <summary>
    /// Kutu içeriğini ANINDA normalleştirme (v12-S6, kullanıcı isteği 2026-07-26: "sorttaki json
    /// işini tüm alanlara ekleyelim"): kutudan odak çıkınca çağrılır — geçerliyse içerik Sort'taki
    /// gibi süslü parantezli, girintili JSON'a çevrilir; geçersizse hata ÜRET'İ BEKLEMEDEN görünür.
    /// Boş metin geçerli sayılır (kutu isteğe bağlı). Ayrıştırma toleransı <see cref="NesneyeNormalle"/>
    /// ile birebir aynı — Üret'in kabul ettiğini bu da kabul eder.
    /// </summary>
    public static (string? Guzel, string? Hata) KutuGuzellestir(string? metin)
    {
        if (string.IsNullOrWhiteSpace(metin))
            return (null, null);

        (string? nesne, string? hata) = NesneyeNormalle(metin);
        if (nesne is null)
            return (null, hata);

        (string? guzel, _) = JsonBicimleyici.Bicimlendir(nesne);
        return (guzel ?? nesne, null);
    }

    /// <summary>
    /// Metni JSON NESNESİNE normaller (kullanıcı bulgusu 2026-07-25: otomatik doldurma
    /// <c>"_id": -1</c> üretir — süslü parantez kullanıcıya yazdırılmaz): metin zaten geçerli
    /// nesneyse aynen; değilse <c>{ metin }</c> olarak sarılıp yeniden denenir (elle
    /// <c>"ad": 1, "tarih": -1</c> yazan da kazanır).
    ///
    /// v19-S14 (canlı test 2026-08-04 — "süslü parantezsiz çalışmıyor; kullanıcı yalnız değer
    /// girsin"): sarma da yetmezse metin ONARILIP yeniden denenir — tırnaksız anahtarlar
    /// (<c>KullaniciId:</c> → <c>"KullaniciId":</c>, <c>$ne:</c> → <c>"$ne":</c>) tırnaklanır ve
    /// eksik kapanış ayraçları (<c>}</c>/<c>]</c>) dengelenir. Böylece
    /// <c>KullaniciId: { $ne: -666</c> gibi yarım girişler de geçerli nesneye döner.
    /// Hepsi başarısızsa null + İLK ayrıştırma nedeni.
    /// </summary>
    private static (string? Nesne, string? Hata) NesneyeNormalle(string metin)
    {
        string kirpik = metin.Trim();
        if (GecerliJsonNesnesi(kirpik, out string? hata))
            return (kirpik, null);

        string sarili = "{ " + kirpik + " }";
        if (GecerliJsonNesnesi(sarili, out _))
            return (sarili, null);

        string onarilmis = AyraclariDengele(AnahtarlariTirnakla(kirpik));
        if (GecerliJsonNesnesi(onarilmis, out _))
            return (onarilmis, null);

        string onarilmisSarili = "{ " + onarilmis + " }";
        if (GecerliJsonNesnesi(onarilmisSarili, out _))
            return (onarilmisSarili, null);

        return (null, hata);
    }

    /// <summary>
    /// Tırnak DIŞINDA kalan ve ':' ile izlenen çıplak sözcükleri (<c>ad</c>, <c>$ne</c>,
    /// <c>adres.il</c>) çift tırnağa alır — JSON anahtarı yapar. Dize içleri aynen korunur.
    /// </summary>
    internal static string AnahtarlariTirnakla(string metin)
    {
        var sb = new StringBuilder(metin.Length + 8);
        bool tirnakta = false;
        int i = 0;
        while (i < metin.Length)
        {
            char c = metin[i];
            if (tirnakta)
            {
                sb.Append(c);
                if (c == '"' && metin[i - 1] != '\\')
                    tirnakta = false;
                i++;
                continue;
            }
            if (c == '"')
            {
                sb.Append(c);
                tirnakta = true;
                i++;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                int bas = i;
                while (i < metin.Length && (char.IsLetterOrDigit(metin[i]) || metin[i] is '_' or '$' or '.'))
                    i++;
                string sozcuk = metin[bas..i];
                int j = i;
                while (j < metin.Length && char.IsWhiteSpace(metin[j]))
                    j++;
                // yalnız ANAHTAR konumundaki sözcük tırnaklanır (':' izliyor); true/false/null
                // gibi değerler ':' izlemediğinden dokunulmaz.
                sb.Append(j < metin.Length && metin[j] == ':' ? $"\"{sozcuk}\"" : sozcuk);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Tırnak dışında AÇIK kalan <c>{</c>/<c>[</c> ayraçlarını sondan uygun kapanışlarla
    /// (<c>}</c>/<c>]</c>) tamamlar — kapanışı kullanıcıya yazdırmama sözünün onarım ayağı.
    /// Fazla kapanış varsa metne dokunmaz (ayrıştırıcı hatayı zaten söyler).
    /// </summary>
    internal static string AyraclariDengele(string metin)
    {
        var acik = new Stack<char>();
        bool tirnakta = false;
        for (int i = 0; i < metin.Length; i++)
        {
            char c = metin[i];
            if (tirnakta)
            {
                if (c == '"' && metin[i - 1] != '\\')
                    tirnakta = false;
                continue;
            }
            switch (c)
            {
                case '"': tirnakta = true; break;
                case '{': acik.Push('}'); break;
                case '[': acik.Push(']'); break;
                case '}' or ']':
                    if (acik.Count > 0 && acik.Peek() == c)
                        acik.Pop();
                    break;
            }
        }
        if (acik.Count == 0)
            return metin;
        var sb = new StringBuilder(metin.TrimEnd());
        while (acik.Count > 0)
            sb.Append(' ').Append(acik.Pop());
        return sb.ToString();
    }

    private static bool GecerliJsonNesnesi(string json, out string? hata)
    {
        hata = null;
        try
        {
            using JsonDocument d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Object)
            {
                hata = "nesne değil";
                return false;
            }
            return true;
        }
        catch (JsonException ex)
        {
            hata = ex.Message;
            return false;
        }
    }
}
