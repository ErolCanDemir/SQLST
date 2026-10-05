using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Find yardımcısındaki hangi JSON kutusu için öneri istendiği.</summary>
public enum MongoKutu { Filter, Sort, Project }

/// <summary>
/// Tek öneri: listede görünen etiket, uygulanınca eklenecek metin, kısa açıklama.
/// <paramref name="ImlecGeri"/> = ekleme sonrası imlecin SONDAN kaç karakter geri alınacağı
/// (v22-S4 saha turu-4 m.5 — kullanıcı: "kapatma kelebeğini de unutmuş"): açılan her ayraç
/// KAPANIŞIYLA birlikte yazılır (<c>"alan": {  }</c>) ve imleç ARAYA konur; böylece metin
/// yazarken de geçerli JSON kalır, kullanıcı kapanışı elle tamamlamak zorunda kalmaz.
/// </summary>
public sealed record MongoOneri(string Goster, string Ekle, string? Aciklama = null, int ImlecGeri = 0);

/// <summary>Öneri sonucu: yazılmakta olan parçanın başlangıcı (değiştirilecek aralık) + liste.</summary>
public sealed record MongoTamamlamaSonucu(int ParcaBas, IReadOnlyList<MongoOneri> Oneriler);

/// <summary>
/// Mongo find yardımcısı otomatik doldurma (v11-öncesi #3, kullanıcı isteği 2026-07-25 —
/// "Compass'taki gibi: Sort alanına yazarken koleksiyondaki _id alanı oraya gelsin"). SAF metin
/// mantığı: imlecin JSON içindeki KONUMUNU (anahtar mı, değer mi, tırnak içinde mi) çözer ve
/// koleksiyonun ALAN ENVANTERİNDEN (şema önbelleği — ağ yok) öneri üretir. UI (popup) App'tedir.
///
/// Üretilen anahtarlar HEP TIRNAKLI — <see cref="MongoBulYazici.Uret"/> katı JSON doğrular
/// (System.Text.Json), tırnaksız anahtar orada reddedilir.
/// </summary>
public static class MongoBulTamamlama
{
    /// <summary>Filter'da $ ile önerilen operatörler (kısa Türkçe açıklamalı, sık kullanılan alt küme).</summary>
    private static readonly MongoOneri[] Operatorler =
    [
        new("$eq", "\"$eq\": ", "eşit"),
        new("$ne", "\"$ne\": ", "eşit değil"),
        new("$gt", "\"$gt\": ", "büyük"),
        new("$gte", "\"$gte\": ", "büyük ya da eşit"),
        new("$lt", "\"$lt\": ", "küçük"),
        new("$lte", "\"$lte\": ", "küçük ya da eşit"),
        // m.5: açılan ayraç KAPANIŞIYLA yazılır, imleç araya konur (ImlecGeri)
        new("$in", "\"$in\": [  ]", "listedekilerden biri", 2),
        new("$nin", "\"$nin\": [  ]", "listedekilerin hiçbiri", 2),
        new("$regex", "\"$regex\": ", "düzenli ifade eşleşmesi"),
        new("$exists", "\"$exists\": ", "alan var mı"),
        new("$and", "\"$and\": [  ]", "tüm koşullar", 2),
        new("$or", "\"$or\": [  ]", "koşullardan biri", 2),
        new("$not", "\"$not\": ", "koşulun tersi"),
        new("$elemMatch", "\"$elemMatch\": {  }", "dizide eşleşen eleman", 2),
        new("$expr", "\"$expr\": {  }", "ifade (alanlar arası karşılaştırma)", 2),
    ];

    /// <summary>
    /// İmleç konumuna göre öneri üretir; öneri yoksa null. <paramref name="alanlar"/> seçili
    /// koleksiyonun alan envanteri (bilinmiyorsa boş verilir — o zaman yalnız operatör/değer önerilir).
    /// </summary>
    public static MongoTamamlamaSonucu? Oner(
        MongoKutu kutu, string metin, int caret, IReadOnlyList<string> alanlar)
    {
        caret = Math.Clamp(caret, 0, metin.Length);
        (bool tirnakta, bool anahtarMi, int parcaBas, string? kapsamAnahtari) = BaglamCoz(metin, caret);
        string parca = metin[parcaBas..caret];

        if (anahtarMi)
        {
            // Anahtar konumu: alan adları; Filter'da $ yazılırsa operatörler.
            bool dolarla = parca.StartsWith('$');

            // v22-S4 saha turu-4 m.5 (kullanıcı: "alanı seçtim, HÂLÂ kolon öneriyor; burada
            // önermesin"): alan seçilince kutu "alan": { açar — oranın anahtarları ALAN DEĞİL
            // OPERATÖRdür ({ "$gte": … }). Eski kural yalnız "$ yazılırsa" operatör veriyordu,
            // yani açılışta koleksiyonun alanlarını listeliyordu (ekran görüntüsü: KayitTarihi'nin
            // içinde _id/KullaniciId/IpAddress…). Kural artık KAPSAMA bakıyor: iç nesneyi açan
            // anahtar bir ALAN ise operatör önerilir. $or/$and dizilerinin eleman nesnelerinde
            // kapsam anahtarı null'dır (diziyi '[' açar) → orada alan önerisi DOĞRUdur ve korunur;
            // $elemMatch/$expr gövdelerinde de kapsam $ ile başladığı için alanlar gelir.
            bool alanIcindeyiz = AlanKapsamiMi(kapsamAnahtari);
            if (kutu == MongoKutu.Filter && (dolarla || alanIcindeyiz))
                return Sonuc(parcaBas, Operatorler.Where(o => Uyar(o.Goster, parca)), tirnakta);

            // v19-S14 (canlı test 2026-08-04 — "alan adını yazayım, iki noktayı ve süslüyü kendisi
            // tamamlasın; kullanıcı yalnız DEĞERLERİ girsin"): Filter'da alan seçilince operatör
            // nesnesi de AÇILIR ("alan": { ) — sırada $ne önerisi; kapanış } kullanıcıya
            // yazdırılmaz, Üret/odak-çıkışı eksik ayraçları kendisi dengeler (MongoBulYazici).
            IEnumerable<MongoOneri> adaylar = alanlar
                .Where(a => Uyar(a, parca))
                .Select(a => kutu == MongoKutu.Filter
                    // m.5: kapanış } DA yazılır, imleç araya konur — eskiden açık kalıyordu
                    ? new MongoOneri(a, $"\"{a}\": {{  }}", "alan", 2)
                    : new MongoOneri(a, $"\"{a}\": ", "alan"));
            return GurultuSuz(Sonuc(parcaBas, adaylar, tirnakta), parca);
        }

        // v19-S14: Filter'da DEĞER konumunda $ yazılırsa da operatör önerilir — alanı elle
        // ": " ile yazan kullanıcı iç nesneyi açmak zorunda kalmasın; öneri "{ "$ne": " ekler.
        if (kutu == MongoKutu.Filter && !tirnakta && parca.StartsWith('$'))
            return Sonuc(parcaBas,
                Operatorler.Where(o => Uyar(o.Goster, parca))
                    .Select(o => o with { Ekle = "{ " + o.Ekle }), tirnakta: false);

        // Değer konumu: kutuya göre sabit öneriler (parçayla süzülür).
        MongoOneri[] degerler = kutu switch
        {
            MongoKutu.Sort =>
            [
                new("1", "1", "artan (A→Z, eski→yeni)"),
                new("-1", "-1", "azalan (Z→A, yeni→eski)"),
            ],
            MongoKutu.Project =>
            [
                new("1", "1", "alanı dahil et"),
                new("0", "0", "alanı hariç tut"),
            ],
            _ =>
            [
                new("true", "true"),
                new("false", "false"),
                new("null", "null"),
            ],
        };
        return Sonuc(parcaBas, degerler.Where(o => Uyar(o.Goster, parca)), tirnakta: false);
    }

    /// <summary>Editörde önerilen KÖK anahtarlar (find/aggregate belgesinin üst düzeyi).</summary>
    private static readonly MongoOneri[] KokAnahtarlar =
    [
        new("find", "\"find\": ", "koleksiyondan oku"),
        new("filter", "\"filter\": { ", "süzgeç koşulları"),
        new("sort", "\"sort\": { ", "sıralama (1/-1)"),
        new("projection", "\"projection\": { ", "alan seçimi (1/0)"),
        new("limit", "\"limit\": ", "en fazla belge"),
        new("skip", "\"skip\": ", "atlanacak belge"),
        new("aggregate", "\"aggregate\": ", "boru hattı koleksiyonu"),
        new("pipeline", "\"pipeline\": [ ", "aggregate adımları"),
    ];

    /// <summary>Pipeline adım operatörleri ($ ile önerilir).</summary>
    private static readonly MongoOneri[] AdimOperatorleri =
    [
        new("$match", "\"$match\": { ", "süz (filter karşılığı)"),
        new("$project", "\"$project\": { ", "alan seç"),
        new("$sort", "\"$sort\": { ", "sırala"),
        new("$limit", "\"$limit\": ", "en fazla"),
        new("$skip", "\"$skip\": ", "atla"),
        new("$group", "\"$group\": { ", "grupla"),
        new("$count", "\"$count\": ", "say"),
        new("$unwind", "\"$unwind\": ", "diziyi aç"),
        new("$lookup", "\"$lookup\": { ", "başka koleksiyonla birleştir"),
    ];

    /// <summary>
    /// SORGU EDİTÖRÜ tamamlaması (C7, 2026-07-25 — Find yardımcısındaki deneyimin editöre
    /// genellenmesi): imlecin TAM find/aggregate belgesi içindeki yerine göre öneri üretir —
    /// kökte anahtarlar (find/filter/sort/…), find/aggregate değerinde KOLEKSİYON adları,
    /// filter/sort/projection içinde alanlar + operatörler + 1/-1/1-0, pipeline'da adım
    /// operatörleri. Koleksiyonun alanları, metindeki <c>"find"/"aggregate"</c> değerinden çözülür.
    /// </summary>
    public static MongoTamamlamaSonucu? EditorOner(
        string metin, int caret, IReadOnlyList<SemaNesnesi> koleksiyonlar)
    {
        caret = Math.Clamp(caret, 0, metin.Length);
        (bool tirnakta, bool anahtarMi, int parcaBas, List<string?> yol, string? sonAnahtar) =
            EditorBaglamCoz(metin, caret);
        string parca = metin[parcaBas..caret];

        // Bölüm: yolun kökten ilk adlandırılmış üyesi (filter/sort/…); pipeline içinde
        // $match/$sort/$project görülürse o anlamlara eşlenir (en derin kazanır).
        string? bolum = yol.FirstOrDefault(a => a is not null);
        foreach (string? a in yol)
        {
            if (a is "$match") bolum = "filter";
            else if (a is "$sort") bolum = "sort";
            else if (a is "$project") bolum = "projection";
        }

        IReadOnlyList<string> alanlar = AlanlariCoz(metin, koleksiyonlar);

        // KÖK düzeyi (ya da hiç { açılmamış boş belge)
        if (bolum is null && yol.Count <= 1)
        {
            if (anahtarMi)
                return GurultuSuz(Sonuc(parcaBas, KokAnahtarlar.Where(o => Uyar(o.Goster, parca)), tirnakta), parca);
            if (sonAnahtar is "find" or "aggregate")
            {
                IEnumerable<MongoOneri> adlar = koleksiyonlar
                    .Where(k => Uyar(k.Ad, parca))
                    .Select(k => new MongoOneri(k.Ad, $"\"{k.Ad}\"", "koleksiyon"));
                // Değer konumunda tırnak içindeysek kapanış tırnağı da eklenmez — yalnız ad.
                if (tirnakta)
                    adlar = koleksiyonlar.Where(k => Uyar(k.Ad, parca))
                        .Select(k => new MongoOneri(k.Ad, k.Ad, "koleksiyon"));
                return GurultuSuz(Sonuc(parcaBas, adlar, tirnakta: false), parca);
            }
            return null; // limit/skip değeri vb. — önerilecek şey yok
        }

        switch (bolum)
        {
            case "filter":
                return Oner(MongoKutu.Filter, metin, caret, alanlar);
            case "sort":
                return Oner(MongoKutu.Sort, metin, caret, alanlar);
            case "projection":
                return Oner(MongoKutu.Project, metin, caret, alanlar);
            case "pipeline" when anahtarMi:
                // Adım nesnesinin anahtarı: $ yazılıyorsa adım operatörleri; değilse alanlar
                // ($group/$lookup gövdeleri gibi durumlar için alan da işe yarar).
                IEnumerable<MongoOneri> adaylar = parca.StartsWith('$')
                    ? AdimOperatorleri.Where(o => Uyar(o.Goster, parca))
                    : alanlar.Where(a => Uyar(a, parca)).Select(a => new MongoOneri(a, $"\"{a}\": ", "alan"));
                return GurultuSuz(Sonuc(parcaBas, adaylar, tirnakta), parca);
            default:
                return null;
        }
    }

    /// <summary>Metindeki "find"/"aggregate" değerinden koleksiyonu bulup alan envanterini verir.</summary>
    private static IReadOnlyList<string> AlanlariCoz(
        string metin, IReadOnlyList<SemaNesnesi> koleksiyonlar)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            metin, "\"(?:find|aggregate)\"\\s*:\\s*\"([^\"]+)\"");
        if (!m.Success)
            return [];
        string ad = m.Groups[1].Value;
        return koleksiyonlar.FirstOrDefault(k => k.Ad.Equals(ad, StringComparison.OrdinalIgnoreCase))
            ?.Kolonlar.Select(k => k.Ad).ToList() ?? [];
    }

    /// <summary>
    /// Editör bağlam çözümü: tırnak/anahtar-değer durumu + iç içe yapı YOLU (her '{'/'[' girişinde
    /// o kapsamın anahtarı — kök '{' için null, "filter" nesnesi için "filter", pipeline adım
    /// nesnesi için null…) + kök düzeyde son görülen anahtar (find değerindeyiz ayrımı için).
    /// </summary>
    private static (bool Tirnakta, bool AnahtarMi, int ParcaBas, List<string?> Yol, string? SonAnahtar)
        EditorBaglamCoz(string metin, int caret)
    {
        var yol = new List<string?>();
        bool tirnakta = false, anahtarKonumu = true;
        int tirnakBas = -1;
        string? sonAnahtar = null;
        var token = new System.Text.StringBuilder();

        for (int i = 0; i < caret; i++)
        {
            char c = metin[i];
            if (tirnakta)
            {
                if (c == '"' && metin[i - 1] != '\\')
                {
                    tirnakta = false;
                    if (anahtarKonumu)
                        sonAnahtar = token.ToString();
                }
                else
                {
                    token.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    tirnakta = true;
                    tirnakBas = i;
                    token.Clear();
                    break;
                case '{' or '[':
                    yol.Add(sonAnahtar);
                    anahtarKonumu = true;
                    sonAnahtar = null;
                    break;
                case '}' or ']':
                    if (yol.Count > 0)
                        yol.RemoveAt(yol.Count - 1);
                    anahtarKonumu = false;
                    break;
                case ':':
                    anahtarKonumu = false;
                    break;
                case ',':
                    anahtarKonumu = true;
                    sonAnahtar = null;
                    break;
            }
        }

        int parcaBas;
        if (tirnakta)
        {
            parcaBas = tirnakBas + 1;
        }
        else
        {
            parcaBas = caret;
            while (parcaBas > 0 && (char.IsLetterOrDigit(metin[parcaBas - 1])
                || metin[parcaBas - 1] is '_' or '$' or '.' or '-'))
                parcaBas--;
        }
        return (tirnakta, anahtarKonumu, parcaBas, yol, sonAnahtar);
    }

    /// <summary>Koleksiyon kutusu: düz ad tamamlama (JSON bağlamı yok, parça = tüm metin).</summary>
    public static MongoTamamlamaSonucu? KoleksiyonOner(
        string metin, IReadOnlyList<string> koleksiyonlar)
    {
        IReadOnlyList<MongoOneri> liste =
            [.. koleksiyonlar.Where(k => Uyar(k, metin)).Select(k => new MongoOneri(k, k, "koleksiyon"))];
        return GurultuSuz(liste.Count == 0 ? null : new MongoTamamlamaSonucu(0, liste), metin);
    }

    /// <summary>
    /// Öneriyi uygular: [parcaBas, caret) aralığı Ekle ile değişir; yeni metin + imleç döner.
    /// <paramref name="imlecGeri"/> (m.5) imleci eklenen metnin SONUNDAN geri alır — kapanış
    /// ayracı da yazıldığı için imleç ayraçların ARASINA konur.
    /// </summary>
    public static (string Metin, int Caret) Uygula(
        string metin, int parcaBas, int caret, string ekle, int imlecGeri = 0)
    {
        caret = Math.Clamp(caret, 0, metin.Length);
        parcaBas = Math.Clamp(parcaBas, 0, caret);
        string yeni = metin[..parcaBas] + ekle + metin[caret..];
        int son = parcaBas + ekle.Length - Math.Clamp(imlecGeri, 0, ekle.Length);
        return (yeni, son);
    }

    // ---- iç mantık ----

    private static MongoTamamlamaSonucu? Sonuc(int parcaBas, IEnumerable<MongoOneri> oneriler, bool tirnakta)
    {
        // Parça tırnak İÇİNDE yazılıyorsa açılış tırnağı zaten var — eklenen metnin baştaki
        // tırnağı düşürülür ("alan": → alan": ) ki çift tırnak oluşmasın.
        IReadOnlyList<MongoOneri> liste = [.. oneriler.Select(o =>
            tirnakta && o.Ekle.StartsWith('"') ? o with { Ekle = o.Ekle[1..] } : o)];
        return liste.Count == 0 ? null : new MongoTamamlamaSonucu(parcaBas, liste);
    }

    /// <summary>Tek öneri kalmış ve yazılanla birebir aynıysa gürültüdür — popup açılmaz.</summary>
    private static MongoTamamlamaSonucu? GurultuSuz(MongoTamamlamaSonucu? sonuc, string parca)
        => sonuc is { Oneriler: [{ } tek] } && tek.Goster.Equals(parca, StringComparison.Ordinal)
            ? null : sonuc;

    /// <summary>
    /// Belgenin YAPISAL anahtarları — bunlar alan adı değildir (m.5). Ayrım gerekli: aynı
    /// <see cref="Oner"/> hem find yardımcısının kutularından (metin = yalnız o kutunun içeriği,
    /// kök kapsam anahtarsız) hem EDİTÖRDEN (metin = tüm find/aggregate belgesi, kapsam anahtarı
    /// "filter"/"sort"/… olur) çağrılır. Bu küme olmasaydı editörde <c>"filter": { "Me</c>
    /// yazarken kapsam "filter" olduğu için alan yerine operatör önerilirdi.
    /// </summary>
    private static readonly HashSet<string> YapisalAnahtarlar =
        new(StringComparer.OrdinalIgnoreCase)
        { "find", "aggregate", "filter", "sort", "projection", "limit", "skip", "pipeline" };

    /// <summary>İmlecin bulunduğu nesneyi bir ALAN mı açtı (→ anahtarları operatördür)?</summary>
    private static bool AlanKapsamiMi(string? kapsamAnahtari)
        => kapsamAnahtari is { Length: > 0 } k
        && !k.StartsWith('$')                 // $elemMatch/$expr/$match gövdesinde alanlar gelir
        && !YapisalAnahtarlar.Contains(k);    // "filter"/"sort"… bölüm adıdır, alan değil

    /// <summary>Önek eşleşmesi (kültür bağımsız, harf duyarsız); boş parça herkesi geçirir.</summary>
    private static bool Uyar(string aday, string parca)
        => parca.Length == 0 || aday.StartsWith(parca, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// İmlece kadar tek geçiş: tırnak içinde miyiz (escape'li tırnak sayılmaz), tırnak DIŞINDA
    /// görülen son anlamlı ayraç ({ , : [) neydi → anahtar mı değer mi, ve yazılmakta olan
    /// parçanın başlangıcı. Boş metin/başlangıç = anahtar konumu ('{' yazılmamış olsa da —
    /// kutular zaten nesne bekler, kullanıcı sıfırdan yazarken de öneri gelsin).
    /// </summary>
    private static (bool Tirnakta, bool AnahtarMi, int ParcaBas, string? KapsamAnahtari)
        BaglamCoz(string metin, int caret)
    {
        bool tirnakta = false;
        int tirnakBas = -1;
        char sonAyrac = '{'; // başlangıç varsayımı: anahtar konumu

        // m.5: iç içe kapsamların ANAHTARI ("alan": { … } → "alan"). İmlecin bulunduğu nesneyi
        // hangi anahtarın açtığını bilmeden "burada alan mı operatör mü önerilir" sorusu
        // yanıtlanamıyordu. Kapsamı '[' açtıysa anahtar null'dır (dizi elemanı).
        var kapsamlar = new Stack<string?>();
        string? sonAnahtar = null;
        var token = new System.Text.StringBuilder();

        for (int i = 0; i < caret; i++)
        {
            char c = metin[i];
            if (c == '"' && (i == 0 || metin[i - 1] != '\\'))
            {
                if (tirnakta)
                    sonAnahtar = token.ToString(); // kapanan tırnak: son okunan sözcük
                else
                    token.Clear();
                tirnakta = !tirnakta;
                tirnakBas = tirnakta ? i : -1;
                continue;
            }
            if (tirnakta)
            {
                token.Append(c);
                continue;
            }
            if (c is '{' or '[')
            {
                kapsamlar.Push(c == '{' ? sonAnahtar : null);
                sonAnahtar = null;
            }
            else if (c is '}' or ']')
            {
                if (kapsamlar.Count > 0)
                    kapsamlar.Pop();
                sonAnahtar = null;
            }
            else if (c == ',')
            {
                sonAnahtar = null;
            }
            if (c is '{' or ',' or ':' or '[' or '}' or ']')
                sonAyrac = c;
        }

        // '}' / ']' sonrası: bir değer kapandı, sıradaki şey ',' ister — öneri konumu yine anahtar
        // sayılır ama parça boştur; ',' yazılınca normal akış. ':' → değer; { , [ → anahtar.
        bool anahtarMi = sonAyrac is '{' or ',' or '}' or ']';

        int parcaBas;
        if (tirnakta)
        {
            parcaBas = tirnakBas + 1;
        }
        else
        {
            parcaBas = caret;
            while (parcaBas > 0 && (char.IsLetterOrDigit(metin[parcaBas - 1])
                || metin[parcaBas - 1] is '_' or '$' or '.' or '-'))
                parcaBas--;
        }
        return (tirnakta, anahtarMi, parcaBas, kapsamlar.Count > 0 ? kapsamlar.Peek() : null);
    }
}
