using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Asistan İSTEM kurucuları (v11-S1) — SAF: UI/IO yok, tümü test edilebilir. "Show Prompt"
/// rayının tek kaynağı burasıdır: kullanıcının "gönderilecek içerik" olarak gördüğü metin,
/// modele giden metnin TA KENDİSİDİR (ayrı bir gölge kopya yok — güven bunu gerektirir).
///
/// GİZLİLİK KURALI (v11 planı, 2026-07-25): şema özetine yalnız TABLO/KOLON adları ve tipleri
/// girer — satır verisi, bağlantı bilgisi, sunucu adı ASLA girmez.
/// </summary>
public static class AsistanIstemleri
{
    /// <summary>Şema özetinde en fazla kaç nesne listelenir (ücretsiz katman jeton bütçesi).</summary>
    public const int EnFazlaNesne = 80;

    /// <summary>
    /// 🚪 ŞEMA KAPISI (v22-S7) — serbest soruya şema EKLENSİN Mİ?
    ///
    /// NEDEN VAR (24 Ağu 2026, kullanıcı: <i>"merhaba yazıyorum 1 dakikada cevap veriyor,
    /// bu çok yavaş"</i>). ÖLÇÜLDÜ: bu makinede model istemi <b>15 token/saniye OKUYOR</b>
    /// (GPU yok — Ollama %100 CPU; <c>num_gpu=0</c> hiçbir şey değiştirmiyor). Sonuç:
    ///   şemasız "merhaba" (59 token)              → <b>0,7 sn</b>
    ///   kompakt şemayla (1.434 token)             → <b>86–128 sn</b>
    /// Yani selamın 90 saniyesinin 85'i, o selamla birlikte gönderilen tablo listesini OKUMAK.
    /// Cevap 12 token; sürenin %97'si boşa. Bu CPU'da tek gerçek kaldıraç DAHA AZ TOKEN göndermek.
    ///
    /// KURAL: soru veriyle ilgili bir işaret taşıyorsa şema gider, taşımıyorsa GİTMEZ.
    /// Liste bilerek CÖMERT ve önek eşleşmeli (Türkçe ekli hâlleri de yakalasın: "sayısı" → "say").
    /// Yanlış POZİTİF ucuz (eski davranış: şema gider, yavaş); yanlış NEGATİF pahalı (model şemayı
    /// göremez) — bu yüzden şüphede kalırsak şema GÖNDERİLİR.
    /// </summary>
    public static bool SemaIsteniyorMu(string? soru)
    {
        if (string.IsNullOrWhiteSpace(soru))
            return false;

        string sade = Sadelestir(soru);
        foreach (string kelime in Sade(sade))
        {
            foreach (string isaret in VeriIsaretleri)
            {
                if (kelime.StartsWith(isaret, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Veriyle ilgili olma işaretleri — ASCII ve küçük harf (girdi <see cref="Sadelestir"/>'den geçer).
    /// Kısa/çakışmaya açık kökler (ör. "ara" → "araç") bilerek YOK: onlar yanlış pozitif üretir,
    /// listenin geri kalanı zaten geniş.
    /// </summary>
    private static readonly string[] VeriIsaretleri =
    [
        // SQL / motor
        "select", "insert", "update", "delete", "join", "where", "group", "order", "having",
        "index", "view", "procedure", "trigger", "sql", "query", "primary", "foreign", "null",
        // Türkçe veri dili (ekli hâlleri önek eşleşmeyle yakalanır)
        "tablo", "kolon", "sutun", "alan", "sorgu", "veri", "kayit", "satir", "sema",
        "listele", "getir", "goster", "filtre", "sirala", "grupla", "say", "kac", "adet",
        "toplam", "ortalama", "maksimum", "minimum", "rapor", "iliski", "birlestir",
        "veritabani", "yavas", "performans", "plan", "tip", "deger", "ozet", "analiz",
    ];

    /// <summary>Kelimelere ayırır (harf/rakam dışı her şey sınırdır).</summary>
    private static IEnumerable<string> Sade(string metin)
    {
        var sb = new StringBuilder();
        foreach (char c in metin)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }

    /// <summary>
    /// Türkçe harfleri ASCII karşılığına indirir, sonra küçük harfe çevirir. Sıra ÖNEMLİ:
    /// 'İ' önce 'I'ya eşlenir; doğrudan <c>ToLowerInvariant</c> uygulansaydı 'İ' birleşik
    /// noktalı 'i̇' üretip önek eşleşmesini bozardı.
    /// </summary>
    private static string Sadelestir(string metin)
    {
        var sb = new StringBuilder(metin.Length);
        foreach (char c in metin)
        {
            sb.Append(c switch
            {
                'ı' or 'İ' or 'I' => 'i',
                'ş' or 'Ş' => 's',
                'ğ' or 'Ğ' => 'g',
                'ü' or 'Ü' => 'u',
                'ö' or 'Ö' => 'o',
                'ç' or 'Ç' => 'c',
                _ => c,
            });
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Aktif veritabanının şema özeti: "şema.tablo (kolon tip, …)" satırları. Nesne sayısı
    /// <see cref="EnFazlaNesne"/>'yi aşarsa kırpılır ve bu AÇIKÇA yazılır (sessiz kırpma yok).
    /// </summary>
    public static string SemaOzeti(SemaOnbellegi? onbellek)
    {
        if (onbellek is null || onbellek.Nesneler.Count == 0)
            return "(şema bilgisi yok)";

        var sb = new StringBuilder();
        List<SemaNesnesi> tablolar = [.. onbellek.Nesneler
            .Where(n => n.Tur is SemaNesneTuru.Tablo or SemaNesneTuru.View or SemaNesneTuru.Koleksiyon)
            .OrderBy(n => n.TamAd, StringComparer.OrdinalIgnoreCase)];

        foreach (SemaNesnesi t in tablolar.Take(EnFazlaNesne))
        {
            sb.Append(t.TamAd).Append(" (");
            sb.Append(string.Join(", ", t.Kolonlar.Select(k =>
                $"{k.Ad} {k.Tip}{(k.PkMi ? " PK" : "")}")));
            sb.AppendLine(")");
        }
        if (tablolar.Count > EnFazlaNesne)
            sb.AppendLine($"… (+{tablolar.Count - EnFazlaNesne} nesne daha — özet kırpıldı)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Şema özeti — YALNIZ ilgili tablolarla (v21 canlı bulgu 2026-08-09: KdsDemo gibi büyük
    /// veritabanında TÜM şema 6374 token ediyor ve modelin bağlamını aşıyordu). <paramref name="ilgiliTablolar"/>
    /// doluysa yalnız o tablolar yazılır; BOŞSA (genel soru, tablo adı geçmiyor) tam şemaya DÜŞÜLÜR —
    /// böylece davranış gerilemez. Gizlilik kuralı aynı: yalnız ad/tip, satır verisi yok.
    /// </summary>
    public static string SemaOzeti(SemaOnbellegi? onbellek, IReadOnlyCollection<string>? ilgiliTablolar)
    {
        if (ilgiliTablolar is null || ilgiliTablolar.Count == 0)
            return SemaOzetiKompakt(onbellek); // genel soru (tablo adı yok) → KOMPAKT (yalnız adlar), istem küçük kalsın
        if (onbellek is null || onbellek.Nesneler.Count == 0)
            return "(şema bilgisi yok)";

        var ilgiliSet = new HashSet<string>(ilgiliTablolar, StringComparer.OrdinalIgnoreCase);
        List<SemaNesnesi> tablolar = [.. onbellek.Nesneler
            .Where(n => n.Tur is SemaNesneTuru.Tablo or SemaNesneTuru.View or SemaNesneTuru.Koleksiyon)
            .Where(n => ilgiliSet.Contains(n.Ad))
            .OrderBy(n => n.TamAd, StringComparer.OrdinalIgnoreCase)];

        if (tablolar.Count == 0)
            return SemaOzetiKompakt(onbellek); // eşleşme yok → kompakt (yalnız adlar)

        var sb = new StringBuilder();
        foreach (SemaNesnesi t in tablolar.Take(EnFazlaNesne))
        {
            sb.Append(t.TamAd).Append(" (");
            sb.Append(string.Join(", ", t.Kolonlar.Select(k =>
                $"{k.Ad} {k.Tip}{(k.PkMi ? " PK" : "")}")));
            sb.AppendLine(")");
        }
        if (tablolar.Count > EnFazlaNesne)
            sb.AppendLine($"… (+{tablolar.Count - EnFazlaNesne} nesne daha — özet kırpıldı)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Genel sorular için KOMPAKT şema: yalnızca tablo ADLARI (kolonsuz). İlgili tablo bulunamayınca
    /// (selamlaşma/genel soru) tam kolon dökümü (~6374 token) yerine bu (~10× küçük) gönderilir → istem
    /// küçük kalır → CPU'da (GPU yok makinelerde) yanıt dakikalar yerine saniyelere iner. Tablo adı geçen
    /// SQL soruları yine tam kolonlu gelir (canlı bulgu 2026-08-09: "Merhaba" bile tüm şemayı gönderip
    /// CPU'da 5 dk takılıyordu). Gizlilik aynı: yalnız ad, satır verisi yok.
    /// </summary>
    public static string SemaOzetiKompakt(SemaOnbellegi? onbellek)
    {
        if (onbellek is null || onbellek.Nesneler.Count == 0)
            return "(şema bilgisi yok)";
        List<SemaNesnesi> tablolar = [.. onbellek.Nesneler
            .Where(n => n.Tur is SemaNesneTuru.Tablo or SemaNesneTuru.View or SemaNesneTuru.Koleksiyon)
            .OrderBy(n => n.TamAd, StringComparer.OrdinalIgnoreCase)];
        if (tablolar.Count == 0)
            return "(şema bilgisi yok)";
        var sb = new StringBuilder();
        sb.AppendLine("Tablolar/görünümler (yalnız adlar; belirli bir tablonun kolonları için sorunda o tablonun adını yazın):");
        sb.Append(string.Join(", ", tablolar.Take(EnFazlaNesne).Select(t => t.TamAd)));
        if (tablolar.Count > EnFazlaNesne)
            sb.Append($" … (+{tablolar.Count - EnFazlaNesne} nesne daha)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Metinde (sorgu + soru) whole-word geçen tablo/görünüm/koleksiyon adlarını döndürür — AI istemine
    /// yalnız İLGİLİ tabloların şeması girsin diye. Lehçe-nötr (ScriptDom gerektirmez; her motor + serbest
    /// soru için çalışır): önbellekteki adları metinde arar. Eşleşme yoksa boş → çağıran kompakt şemaya düşer.
    /// </summary>
    public static IReadOnlyCollection<string> IlgiliTablolar(string? metin, SemaOnbellegi? onbellek)
    {
        if (string.IsNullOrWhiteSpace(metin) || onbellek is null || onbellek.Nesneler.Count == 0)
            return [];
        var bulunan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SemaNesnesi n in onbellek.Nesneler)
        {
            if (n.Tur is (SemaNesneTuru.Tablo or SemaNesneTuru.View or SemaNesneTuru.Koleksiyon)
                && KelimeGecer(metin, n.Ad))
                bulunan.Add(n.Ad);
        }
        return bulunan;
    }

    /// <summary><paramref name="kelime"/> metinde tanımlayıcı SINIRLARIYLA (whole word) geçiyor mu —
    /// "Kisiler" araması "KisilerAdres"e yanlış eşleşmesin diye. Büyük/küçük harf duyarsız.</summary>
    private static bool KelimeGecer(string metin, string kelime)
    {
        if (string.IsNullOrEmpty(kelime))
            return false;
        int i = 0;
        while ((i = metin.IndexOf(kelime, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool solTemiz = i == 0 || !KelimeKarakteri(metin[i - 1]);
            int son = i + kelime.Length;
            bool sagTemiz = son >= metin.Length || !KelimeKarakteri(metin[son]);
            if (solTemiz && sagTemiz)
                return true;
            i = son;
        }
        return false;
    }

    private static bool KelimeKarakteri(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Motor adı istem dilinde ("SQL Server" / "PostgreSQL" …) — üretilen sorgu lehçeye uysun.</summary>
    public static string MotorAdi(MotorTuru? motor) => motor switch
    {
        MotorTuru.Mssql => "Microsoft SQL Server (T-SQL)",
        MotorTuru.Postgres => "PostgreSQL",
        MotorTuru.MySql => "MySQL/MariaDB",
        MotorTuru.Oracle => "Oracle",
        MotorTuru.Mongo => "MongoDB (find/aggregate JSON — SQL değil)",
        _ => "bilinmeyen motor",
    };

    /// <summary>Söz dizim notunu (varsa) isteme ekler — boş motorlarda satır atlanır.</summary>
    private static void SozDizimNotuYaz(StringBuilder sb, MotorTuru? motor)
    {
        string not = MotorSozDizimNotu(motor);
        if (not.Length > 0)
            sb.AppendLine(not);
    }

    /// <summary>
    /// Motorun EN SIK KAÇIRILAN söz dizim kuralı (v21-S2, POC bulgusu: küçük model satır-sınırını
    /// yanlış yazıyor — 3B <c>LIMIT</c>, 7B yanlış konumlu <c>TOP</c>). İstemlere eklenir ki üretilen
    /// SQL doğrudan çalışsın; ScriptDom kapısı (SqlDogrulayici) yine son güvence.
    /// </summary>
    public static string MotorSozDizimNotu(MotorTuru? motor) => motor switch
    {
        MotorTuru.Mssql =>
            "SÖZ DİZİMİ (T-SQL, KRİTİK): Satır sınırlama LIMIT DEĞİL, SELECT'ten HEMEN SONRA gelen "
            + "TOP (n)'dir: \"SELECT TOP (5) …\". LIMIT ya da sonda TOP KULLANMA. Dize öneki N'…', "
            + "tanımlayıcılar [köşeli] ayraç.",
        MotorTuru.Postgres => "SÖZ DİZİMİ: Satır sınırlama sonda LIMIT n. TOP yoktur.",
        MotorTuru.MySql => "SÖZ DİZİMİ: Satır sınırlama sonda LIMIT n. TOP yoktur; tanımlayıcı `ters tırnak`.",
        MotorTuru.Oracle => "SÖZ DİZİMİ: Satır sınırlama FETCH FIRST n ROWS ONLY. TOP/LIMIT yoktur.",
        _ => "",
    };

    /// <summary>
    /// 🛡 Düzeltme turu istemi (v21-S2): AI'ın ürettiği SQL ScriptDom'da parse hatası verdiyse,
    /// modele SADECE söz dizimini düzelttirmek için gider. Tek ``` blokta, açıklamasız sonuç ister.
    /// </summary>
    public static string SqlDuzeltmeTuru(string bozukSql, string parseHatasi, MotorTuru? motor)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Aşağıdaki {MotorAdi(motor)} sorgusu SÖZ DİZİMİ hatası veriyor:");
        sb.AppendLine(parseHatasi.Trim());
        sb.AppendLine(MotorSozDizimNotu(motor));
        sb.AppendLine("Sorgunun MANTIĞINI koru, YALNIZCA söz dizimini düzelt. Açıklama YAZMA;");
        sb.AppendLine("yalnız düzeltilmiş sorguyu tek ``` kod bloğu içinde ver.");
        sb.AppendLine();
        sb.AppendLine("SORGU:");
        sb.Append(bozukSql.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// "Sorgumu değerlendir" istemi (v11-S2, kullanıcı isteği 1): editördeki sorgu + şema + motor →
    /// hata/doğruluk/risk değerlendirmesi. Yönerge kısa-madde-madde cevap ister; düzeltme varsa
    /// düzeltilmiş sorgunun TAMAMI istenir (kullanıcı kopyalayıp sekmede İNCELEYEREK çalıştırır).
    /// </summary>
    public static string SorguDegerlendir(string sorgu, string semaOzeti, string motorAdi, MotorTuru? motor = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe ve madde madde cevap ver.");
        sb.AppendLine($"Bağlı motor: {motorAdi}. Değerlendirmeyi BU motorun söz dizimine göre yap.");
        SozDizimNotuYaz(sb, motor);
        sb.AppendLine("Aşağıdaki sorguyu değerlendir:");
        sb.AppendLine("1) Söz dizimi hatası var mı? 2) Şemaya göre mantık hatası var mı (yanlış tablo/kolon,");
        sb.AppendLine("eksik JOIN koşulu, yanlış GROUP BY)? 3) Risk var mı (WHERE'siz UPDATE/DELETE, kartesyen");
        sb.AppendLine("çarpım)? 4) Kısa iyileştirme önerisi. Düzeltme gerekiyorsa DÜZELTİLMİŞ SORGUNUN TAMAMINI");
        sb.AppendLine("``` kod bloğu içinde ver. Yalnız şemadaki tablo/kolon adlarını kullan; uydurma.");
        sb.AppendLine();
        sb.AppendLine("VERİTABANI ŞEMASI:");
        sb.AppendLine(semaOzeti);
        sb.AppendLine();
        sb.AppendLine("DEĞERLENDİRİLECEK SORGU:");
        sb.Append(sorgu.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// "Hatayı çözdür" istemi (v11-S4): sorgu + sunucunun hata mesajı + şema → nedenin düz Türkçe
    /// açıklaması + düzeltilmiş sorgunun tamamı (``` blokta — "Sekmede aç" ayıklar).
    /// </summary>
    public static string HataCozdur(string sorgu, string hata, string semaOzeti, string motorAdi, MotorTuru? motor = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe ve kısa cevap ver.");
        sb.AppendLine($"Bağlı motor: {motorAdi}.");
        SozDizimNotuYaz(sb, motor);
        sb.AppendLine("Aşağıdaki sorgu şu HATAYI verdi. 1) Hatanın nedenini bir-iki cümleyle açıkla.");
        sb.AppendLine("2) DÜZELTİLMİŞ SORGUNUN TAMAMINI ``` kod bloğu içinde ver.");
        sb.AppendLine("Yalnız şemadaki tablo/kolon adlarını kullan; uydurma.");
        sb.AppendLine();
        sb.AppendLine("HATA MESAJI:");
        sb.AppendLine(hata.Trim());
        sb.AppendLine();
        sb.AppendLine("VERİTABANI ŞEMASI:");
        sb.AppendLine(semaOzeti);
        sb.AppendLine();
        sb.AppendLine("HATA VEREN SORGU:");
        sb.Append(sorgu.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// "Sorguyu açıkla" istemi (v11-S5): devralınan sorgu ne yapıyor — adım adım, düz Türkçe
    /// (bakım senaryosu). Sorgu üretmesi istenmez; yalnız anlatır.
    /// </summary>
    public static string SorguAcikla(string sorgu, string semaOzeti, string motorAdi)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe cevap ver.");
        sb.AppendLine($"Bağlı motor: {motorAdi}.");
        sb.AppendLine("Aşağıdaki sorgunun NE YAPTIĞINI düz Türkçe, adım adım açıkla:");
        sb.AppendLine("hangi tablolardan ne okunuyor, JOIN'ler neyi eşliyor, süzgeçler ne eliyor,");
        sb.AppendLine("gruplamalar/sıralamalar ne üretiyor. Sonunda TEK cümlelik özet ver.");
        sb.AppendLine();
        sb.AppendLine("VERİTABANI ŞEMASI:");
        sb.AppendLine(semaOzeti);
        sb.AppendLine();
        sb.AppendLine("AÇIKLANACAK SORGU:");
        sb.Append(sorgu.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// 🔥 ISITILAN ÖNEK (v22-S8; KDS S34'ün karşılığı) — istemin BAĞLANTI BOYUNCA DEĞİŞMEYEN başı:
    /// sistem yönergeleri + motor söz dizimi notu + KOMPAKT şema (yalnız tablo adları).
    ///
    /// NEDEN AYRI: bu makinede istem işleme 15 tok/sn — 1.267 token'lık önek soğukta ~70 sn.
    /// Ama Ollama AYNI öneki ikinci kez İŞLEMİYOR: ölçüldü, ısıtma sonrası 6 farklı soruda
    /// istem-eval 0,4–1,2 sn (6/6 isabet). Bu yüzden önek, kullanıcı sekmeyi açar açmaz
    /// <c>num_predict=1</c> ile bir kez işletilir; kullanıcı sorusunu yazarken önbelleğe girer.
    ///
    /// ⚠ BAYT BAYT AYNILIK KURALI: ısıtılan metin, gerçek istemin BAŞLANGICIYLA birebir aynı
    /// olmalı. Tek karakterlik fark önbelleği ıskalatır ve ürün DOĞRU çalışmaya devam eder —
    /// yalnız yavaşlar, yani kimse fark etmez. Bu sessiz kaybı
    /// <c>Isitma_onegi_gercek_istemin_BASLANGICIYLA_birebir_ayni</c> testi yakalar.
    /// Değişken her şey (ilgili tabloların kolonları, editördeki sorgu, sorunun kendisi) ÖNEKTEN
    /// SONRA gelir — sıra bozulursa ısıtma işlevsiz kalır.
    /// </summary>
    public static string IstemOnegi(string kompaktSema, string motorAdi, MotorTuru? motor = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe, kısa ve net cevap ver.");
        sb.AppendLine($"Bağlı motor: {motorAdi}. Sorgu örneği verirken BU motorun söz dizimini kullan.");

        // 🚪 ŞEMA KAPISI (v22-S7): kompaktSema BOŞSA şema bölümü + ona bağlı yönergeler HİÇ yazılmaz.
        // "Yalnız şemadaki adları kullan" yönergesini şemasız yazmak yanlış olurdu — model olmayan
        // bir listeye atıf yapardı.
        if (kompaktSema.Length > 0)
        {
            SozDizimNotuYaz(sb, motor);
            sb.AppendLine("Sorgu üretirken yalnız aşağıdaki şemadaki tablo/kolon adlarını kullan; uydurma.");
            sb.AppendLine("Sorgu verirken sorguyu ``` kod bloğu içinde ver (araç bloktan ayıklar).");
            sb.AppendLine();
            sb.AppendLine("VERİTABANI ŞEMASI:");
            sb.AppendLine(kompaktSema);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serbest soru istemi (S1 paneli) = <see cref="IstemOnegi"/> (SABİT, ısıtılır) + değişken kısım.
    ///
    /// v22-S8'de SIRA DEĞİŞTİ: eskiden ilgili tabloların kolon dökümü kompakt listenin YERİNE
    /// geçiyordu, yani önek her soruda başkalaşıyor ve ısıtma hiçbir işe yaramıyordu. Artık kompakt
    /// liste hep önekte kalır, kolon ayrıntısı ONUN ARDINA eklenir → önek önbelleğe isabet eder,
    /// yalnız ayrıntı yeniden işlenir.
    /// </summary>
    public static string SerbestSoru(
        string soru, string kompaktSema, string motorAdi, string? editordekiSorgu = null,
        MotorTuru? motor = null, string? ilgiliKolonlar = null)
    {
        var sb = new StringBuilder();
        sb.Append(IstemOnegi(kompaktSema, motorAdi, motor)); // ⚠ ÖNEK — buradan öncesi değişemez

        if (!string.IsNullOrWhiteSpace(ilgiliKolonlar))
        {
            sb.AppendLine();
            sb.AppendLine("SORUYLA İLGİLİ TABLOLARIN KOLONLARI:");
            sb.AppendLine(ilgiliKolonlar.Trim());
        }
        if (!string.IsNullOrWhiteSpace(editordekiSorgu))
        {
            sb.AppendLine();
            sb.AppendLine("KULLANICININ ÜZERİNDE ÇALIŞTIĞI SORGU:");
            sb.AppendLine(editordekiSorgu.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("SORU:");
        sb.Append(soru.Trim());
        return sb.ToString();
    }

    /// <summary>Execution plan'ı isteme uygun düz metne çevirir (v11-S5b): girintili ağaç —
    /// işlem · pay% · tahmini/gerçek satır · uyarılar. Sayı uydurulmaz; ne varsa o yazılır.</summary>
    public static string PlanMetni(SorguPlani plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(plan.Gercek ? "(GERÇEK plan — ölçülmüş satır sayıları var)" : "(TAHMİNİ plan)");
        foreach (IfadePlani ifade in plan.Ifadeler)
        {
            sb.AppendLine($"İFADE: {ifade.IfadeMetni}");
            if (ifade.Kok is not null)
                Dugum(ifade.Kok, 0);
            foreach (PlanEksikIndexi ex in ifade.EksikIndexler)
                sb.AppendLine($"  EKSİK INDEX önerisi (etki %{ex.Etki:F0}): {ex.Tablo} ({string.Join(", ", ex.EsitlikKolonlari)})");
        }
        return sb.ToString().TrimEnd();

        void Dugum(PlanDugumu d, int derinlik)
        {
            sb.Append(new string(' ', derinlik * 2))
              .Append($"- {d.Islem}{(d.Ayrinti is { Length: > 0 } a ? $" [{a}]" : "")} · pay %{d.MaliyetYuzdesi:F0}");
            if (d.TahminiSatir is { } t) sb.Append($" · tahmin {t:F0} satır");
            if (d.GercekSatir is { } g) sb.Append($" · gerçek {g:F0} satır");
            if (d.Uyarilar.Count > 0) sb.Append($" · UYARI: {string.Join("; ", d.Uyarilar)}");
            sb.AppendLine();
            foreach (PlanDugumu c in d.Cocuklar) Dugum(c, derinlik + 1);
        }
    }

    /// <summary>"Planı yorumlat" istemi (v11-S5b): neden yavaş, hangi adım pahalı, ne yardım eder.</summary>
    public static string PlanYorumla(string planMetni, string motorAdi)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe ve madde madde cevap ver.");
        sb.AppendLine($"Bağlı motor: {motorAdi}.");
        sb.AppendLine("Aşağıdaki execution plan'ı yorumla: 1) En pahalı adımlar hangileri ve neden?");
        sb.AppendLine("2) Tahmin/gerçek sapması varsa ne anlama gelir? 3) Hangi index/yeniden yazım yardım eder?");
        sb.AppendLine("Sayı uydurma; yalnız plandaki verilere dayan.");
        sb.AppendLine();
        sb.AppendLine("EXECUTION PLAN:");
        sb.Append(planMetni.Trim());
        return sb.ToString();
    }

    /// <summary>"Profiler olayını yorumla" istemi (v23-S4): seçili olay/grup/kilitlenme özeti
    /// modele gider — muhtemel neden + somut iyileştirme (index/sorgu) istenir. Metni VM kurar
    /// (olay metrikleri ya da deadlock özeti); modele ham XML değil ÖZET gider (istem şişmesin).</summary>
    public static string ProfilerYorumla(string olayOzeti)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe ve kısa cevap ver.");
        sb.AppendLine("Aşağıda SQL Server'da canlı izlemeden (Profiler) yakalanan bir olay var.");
        sb.AppendLine("1) Metriklere ve sorguya bakarak muhtemel PERFORMANS/HATA nedenini açıkla.");
        sb.AppendLine("2) SOMUT bir iyileştirme öner (index, sorgu düzeltme, kilitlenme için sıra/kapsam).");
        sb.AppendLine("Uydurma; yalnız verilen bilgiye dayan. Emin olmadığını belirt.");
        sb.AppendLine();
        sb.AppendLine("OLAY:");
        sb.Append(olayOzeti.Trim());
        return sb.ToString();
    }

    /// <summary>"Log gruplarını özetle" istemi (v11-S6): en çok tekrarlayan hatalar ne anlatıyor.</summary>
    public static string LogOzetle(string gruplarMetni)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe ve kısa cevap ver.");
        sb.AppendLine("Aşağıda bir uygulamanın log tablosundan EN ÇOK TEKRARLAYAN mesaj grupları var");
        sb.AppendLine("(sayı × örnek mesaj). 1) En kritik görünen 2-3 grubu seç ve muhtemel KÖK NEDENİNİ");
        sb.AppendLine("açıkla. 2) Her biri için pratik bir sonraki adım öner. Uydurma; mesaja dayan.");
        sb.AppendLine();
        sb.AppendLine("LOG GRUPLARI:");
        sb.Append(gruplarMetni.Trim());
        return sb.ToString();
    }

    /// <summary>"SOAP yanıtını yorumla" istemi (v14-S3): istek+yanıt zarfından başarı/veri/hata
    /// özeti — Fault'ta kök neden ve muhtemel çözüm istenir. Yalnız kullanıcının zaten ekranda
    /// gördüğü zarflar gider (gizlilik rayları değişmez).</summary>
    public static string SoapYorumla(string istekZarfi, string yanitZarfi, bool faultMu)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe ve kısa cevap ver.");
        sb.AppendLine("Aşağıda bir SOAP servis çağrısının İSTEK ve YANIT zarfları var.");
        sb.AppendLine(faultMu
            ? "Yanıt bir SOAP FAULT: 1) Hatanın nedenini düz Türkçe açıkla. 2) İstekteki hangi alan/değerin sorumlu olabileceğini söyle. 3) Pratik bir düzeltme öner. Uydurma."
            : "1) Çağrının ne yaptığını ve yanıtta dönen veriyi 2-3 cümleyle özetle. 2) Dikkat çekmesi gereken (boş/şüpheli) alan varsa belirt. Uydurma; zarflara dayan.");
        sb.AppendLine();
        sb.AppendLine("İSTEK:");
        sb.AppendLine(istekZarfi.Trim());
        sb.AppendLine();
        sb.AppendLine("YANIT:");
        sb.Append(yanitZarfi.Trim());
        return sb.ToString();
    }

    /// <summary>"REST yanıtını yorumla" istemi (v20-S8): istek özeti + yanıt gövdesi + HTTP durumundan
    /// başarı/veri/hata özeti — 4xx/5xx ya da ağ hatasında kök neden ve pratik düzeltme istenir. Yalnız
    /// kullanıcının ekranda gördüğü veri gider (gizlilik rayları değişmez).</summary>
    public static string RestYorumla(string istekOzeti, string yanitGovdesi, int httpDurum)
    {
        bool hataKodu = httpDurum == 0 || httpDurum >= 400;
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe ve kısa cevap ver.");
        sb.AppendLine($"Aşağıda bir REST/HTTP çağrısının İSTEĞİ ve YANITI var (HTTP {httpDurum}).");
        sb.AppendLine(hataKodu
            ? "Yanıt bir HATA (4xx/5xx ya da ağ): 1) Nedenini düz Türkçe açıkla. 2) İstekteki hangi başlık/alan/gövdenin sorumlu olabileceğini söyle. 3) Pratik bir düzeltme öner. Uydurma."
            : "1) Çağrının ne yaptığını ve yanıtta dönen veriyi 2-3 cümleyle özetle. 2) Boş/şüpheli alan varsa belirt. Uydurma; yanıta dayan.");
        sb.AppendLine();
        sb.AppendLine("İSTEK:");
        sb.AppendLine(istekOzeti.Trim());
        sb.AppendLine();
        sb.AppendLine("YANIT:");
        sb.Append(yanitGovdesi.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// "Tarifle → REST isteği üret" istemi (v20-S13): kullanıcının doğal dil isteğini yapılandırılmış bir
    /// HTTP isteğine çevirtir. Model YALNIZCA tek bir JSON nesnesi dönsün ister (metod/url/basliklar/govde)
    /// — <see cref="AiIstekAyristirici"/> bunu (çit/önsöz olsa bile) ilk dengeli {…} bloğunu alarak çözer.
    /// </summary>
    public static string RestIstekUret(string aciklama)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Kullanıcının doğal dil isteğini bir HTTP/REST çağrısına çevir.");
        sb.AppendLine("YALNIZCA şu şemada TEK bir JSON nesnesi döndür; öncesinde/sonrasında açıklama, kod bloğu işareti veya başka metin OLMASIN:");
        sb.AppendLine("{\"metod\":\"GET|POST|PUT|PATCH|DELETE\",\"url\":\"https://...\",\"basliklar\":{\"Ad\":\"Deger\"},\"govde\":\"...\"}");
        sb.AppendLine("Kurallar: gövde gerekmiyorsa \"govde\":\"\"; başlık gerekmiyorsa \"basliklar\":{}. URL ve gövdeyi isteğe göre doldur. Bilmediğin alanı UYDURMA, boş bırak.");
        sb.AppendLine();
        sb.AppendLine("İSTEK:");
        sb.Append(aciklama.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// "Yanıttan CREATE TABLE öner" istemi (v20-S13, madde 6 — madde 1 görsel tasarımcısıyla birleşir):
    /// örnek veriden her kolon için o motora UYGUN SQL tipi + PK + null önerir. Kolon ADLARINI DEĞİŞTİRMEZ
    /// (veri eşlemesi buna bağlı) — yalnız tip/pk/null iyileştirir + tablo adı önerir. Model tek JSON döner;
    /// <see cref="AiSemaAyristirici"/> çözer.
    /// </summary>
    public static string SemaOner(string veriOzeti, string motorAdi)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sen SQLST veritabanı aracının yardımcısısın. Aşağıdaki örnek veri için bir {motorAdi} tablosu tasarla.");
        sb.AppendLine("Her kolon için o motora UYGUN SQL tipini, birincil anahtar (pk) ve null olabilirliğini öner.");
        sb.AppendLine("ÖNEMLİ: kolon ADLARINI aynen koru; yeni kolon ekleme, kolon çıkarma (veri eşlemesi buna bağlı).");
        sb.AppendLine("YALNIZCA şu şemada TEK JSON döndür; öncesinde/sonrasında başka metin OLMASIN:");
        sb.AppendLine("{\"tabloAdi\":\"onerilen_ad\",\"kolonlar\":[{\"ad\":\"mevcut_kolon\",\"tip\":\"SQL_TIPI\",\"pk\":false,\"null\":true}]}");
        sb.AppendLine("Kimlik/anahtar gibi görünen kolonu pk yap ve null=false ver. Tipleri örnek değerlere göre seç (tam sayı, ondalık, tarih, metin uzunluğu).");
        sb.AppendLine();
        sb.Append(veriOzeti.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// "İki REST yanıtını/hatayı diff yorumla" istemi (v20-S13, madde 7): sabitlenen (ÖNCEKİ) ile ŞİMDİKİ
    /// yanıtı karşılaştırıp farkları düz Türkçe açıklatır — durum/hata farkı, hangi alan değişti/eklendi/
    /// silindi, olası neden. Yalnız kullanıcının elindeki iki yanıt gider (gizlilik rayları değişmez).
    /// </summary>
    public static string RestDiffYorumla(string onceki, string simdiki)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen SQLST adlı veritabanı aracının yardımcısısın. Türkçe ve kısa cevap ver.");
        sb.AppendLine("Aşağıda AYNI/benzer bir REST çağrısının İKİ yanıtı var: ÖNCEKİ ve ŞİMDİKİ.");
        sb.AppendLine("1) Durum kodu/hata farkını söyle. 2) Hangi alanlar DEĞİŞTİ, EKLENDİ, SİLİNDİ — madde madde. 3) Olası nedeni. Uydurma; iki yanıta dayan.");
        sb.AppendLine();
        sb.AppendLine("ÖNCEKİ:");
        sb.AppendLine(onceki.Trim());
        sb.AppendLine();
        sb.AppendLine("ŞİMDİKİ:");
        sb.Append(simdiki.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// Model cevabından SORGUYU ayıklar (v11-S3 — "↗ Sekmede aç"): önce ``` kod blokları
    /// (en UZUNU alınır — düzeltilmiş tam sorgu genelde odur); blok yoksa cevabın tamamı bir
    /// sorgu gibi başlıyorsa (SELECT/WITH/… ya da Mongo <c>{</c>) o; değilse null (düğme çıkmaz).
    /// </summary>
    public static string? CevaptanSorguAyikla(string? cevap)
    {
        if (string.IsNullOrWhiteSpace(cevap))
            return null;

        var bloklar = new List<string>();
        int i = 0;
        while ((i = cevap.IndexOf("```", i, StringComparison.Ordinal)) >= 0)
        {
            int icBas = cevap.IndexOf('\n', i);
            if (icBas < 0)
                break;
            int son = cevap.IndexOf("```", icBas, StringComparison.Ordinal);
            if (son < 0)
                break;
            string blok = cevap[(icBas + 1)..son].Trim();
            if (blok.Length > 0)
                bloklar.Add(blok);
            i = son + 3;
        }
        if (bloklar.Count > 0)
            return bloklar.OrderByDescending(b => b.Length).First();

        string kirpik = cevap.Trim();
        string[] baslar = ["SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "EXEC", "{"];
        return baslar.Any(b => kirpik.StartsWith(b, StringComparison.OrdinalIgnoreCase)) ? kirpik : null;
    }
}
