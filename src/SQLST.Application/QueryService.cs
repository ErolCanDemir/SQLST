using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Sorgu yürütmenin uygulama katmanı: koruma rayları (FG-6) burada işler,
/// yürütmenin kendisi ISqlExecutor'a devredilir. ViewModel'ler yalnız bunu görür.
/// </summary>
public sealed class QueryService
{
    private static readonly string[] YazmaAnahtarlari =
        ["UPDATE", "DELETE", "INSERT", "ALTER", "DROP", "TRUNCATE", "CREATE", "EXEC", "EXECUTE", "MERGE"];

    /// <summary>
    /// Kirli okuma ayarı (FG-6.4). Kapalıyken de açıkça READ COMMITTED yazılır: havuzdan
    /// gelen bağlantı önceki sorgunun izolasyon seviyesini taşımasın (belirlenimci davranış).
    ///
    /// <b>AYRI KOMUT olarak gönderilir — kullanıcının SQL'iyle AYNI BATCH'E KONMAZ
    /// (kullanıcı bulgusu, 2026-07-19).</b> Eskiden metnin başına ön ek olarak ekleniyordu
    /// ve bu, T-SQL'in <i>"batch'in ilk ifadesi olmalı"</i> kuralına takılan HER ŞEYİ
    /// bozuyordu: <c>CREATE/ALTER PROCEDURE|VIEW|FUNCTION|TRIGGER</c> ve <c>SET SHOWPLAN_XML</c>.
    ///
    /// Kullanıcı bunu İKİ KEZ buldu: önce tahmini planda (o zaman özellik kaldırıldı ama
    /// KÖK NEDEN düzeltilmedi), sonra "Script as DROP + CREATE" çıktısında —
    /// <c>Msg 111: 'CREATE/ALTER PROCEDURE' must be the first statement in a query batch</c>.
    /// Canlı kanıt (LocalDB): ön ekle Msg 111, ön eksiz aynı ifade sorunsuz çalışıyor.
    ///
    /// Ek maliyeti bir gidiş-dönüştür ve <c>SET</c> I/O yapmaz; buna karşılık durum
    /// TUTULMADIĞI için "kirli okuma açık sanılıp aslında kapalı çalışmak" gibi sessiz bir
    /// sapma imkânsızdır (kullanıcı kararı 2026-07-19: A seçeneği).
    /// </summary>
    internal const string KirliOkumaSql = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;";
    internal const string TemizOkumaSql = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;";

    /// <summary>Bu çalıştırma için gönderilecek izolasyon ifadesi (ayrı komut).</summary>
    public static string IzolasyonSql(bool kirliOkuma) => kirliOkuma ? KirliOkumaSql : TemizOkumaSql;

    /// <summary>
    /// Sekmenin kalıcı oturumunda sorguyu çalıştırır (V2-S1): koruma rayları burada
    /// işler (salt-okunur engeli + kirli okuma ön eki), yürütme oturuma devredilir.
    /// İzolasyon ön eki T-SQL'dir ve YALNIZ MSSQL'de eklenir (V3 Faz 3 bulgusu):
    /// diğer motorlarda sorgu metni olduğu gibi gider (Mongo'da JSON'dur, SQL değil).
    /// </summary>
    public async Task<QueryResult> RunAsync(
        IDbOturum oturum, string sql, ExecuteOptions opts, CancellationToken ct)
    {
        // Salt-okunur koruması TÜM motorlarda işler (V3 denetimi 2026-07-18): eskiden yalnız
        // MSSQL'de çalışıyordu; PostgreSQL/MySQL/Oracle/Mongo'da kullanıcı korunduğunu
        // sanırken DELETE sessizce gidiyordu — veri kaybı riskiydi.
        // TEMKİNLİ denetim (2026-07-19 gözden geçirmesi): ilk-kelime mantığı CTE'ye gizlenmiş
        // DML'i ve blok yorumla başlayan script'i kaçırıyordu — kullanıcı korunduğunu sanırken
        // DELETE gidebiliyordu. Salt-okunur açık bir güvenlik tercihidir; temkinli taraf doğru.
        if (oturum.Profil.SaltOkunur && PlanIcinYazmaSayilir(sql, oturum.Profil.Motor))
        {
            return new QueryResult
            {
                Hata = new SqlHata(
                    "Bu profil salt-okunur olarak işaretli; yazma/DDL sorgusu gönderilmedi (FG-1.5).",
                    Numara: -1, Satir: 1, Onem: 0),
            };
        }

        if (oturum.Profil.Motor != MotorTuru.Mssql)
            return await oturum.CalistirAsync(MotoraUyarla(sql, oturum.Profil.Motor), opts, ct);

        // İzolasyon AYRI komut — kullanıcının metnine DOKUNULMAZ (bkz. IzolasyonSql).
        QueryResult izolasyon = await oturum.CalistirAsync(IzolasyonSql(opts.KirliOkuma), opts, ct);
        if (izolasyon.Hata is not null || izolasyon.IptalEdildi)
            return izolasyon;   // seviye kurulamadıysa kullanıcının sorgusu GÖNDERİLMEZ

        return await oturum.CalistirAsync(sql, opts, ct);
    }

    /// <summary>
    /// Motorun kabul etmediği yüzeysel sözdizimini düzeltir (V3 denetimi):
    /// Oracle düz SQL'de SONDAKİ noktalı virgülü reddeder (ORA-00933) — SSMS alışkanlığıyla
    /// yazılan <c>SELECT … ;</c> hata veriyordu. PL/SQL blokları (BEGIN…END;) korunur.
    /// </summary>
    public static string MotoraUyarla(string sql, MotorTuru motor)
    {
        if (motor != MotorTuru.Oracle)
            return sql;

        string kirpik = sql.TrimEnd();
        if (!kirpik.EndsWith(';'))
            return sql;

        // PL/SQL bloğunun kendi ';' sonlandırıcısı gereklidir — dokunma.
        string bas = kirpik.TrimStart();
        bool plsql = bas.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
            || bas.StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase)
            || kirpik.EndsWith("END;", StringComparison.OrdinalIgnoreCase);
        return plsql ? sql : kirpik[..^1];
    }

    /// <summary>MongoDB'de yazma sayılan komutlar (sorgu metni JSON belgesidir).</summary>
    private static readonly string[] MongoYazmaKomutlari =
    [
        "insert", "update", "delete", "findandmodify", "drop", "dropdatabase", "dropindexes",
        "create", "createindexes", "createuser", "renamecollection", "collmod", "bulkwrite",
    ];

    /// <summary>
    /// Sorgunun yazma/DDL olup olmadığını MOTORA GÖRE sınıflar (V3): SQL ailesinde ilk
    /// anlamlı token, MongoDB'de JSON belgesinin komut adı. Amaç %100 parse değil,
    /// kaza sigortası (02-mimari §4.4).
    /// </summary>
    public static bool YazmaSorgusuMu(string sql, MotorTuru motor)
        => motor == MotorTuru.Mongo ? MongoYazmaMi(sql) : YazmaSorgusuMu(sql);

    /// <summary>Mongo JSON'unda ilk anahtar komut adıdır: { "insert": "koleksiyon", … }.</summary>
    private static bool MongoYazmaMi(string metin)
    {
        try
        {
            using System.Text.Json.JsonDocument belge = System.Text.Json.JsonDocument.Parse(
                metin, new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true });
            foreach (System.Text.Json.JsonProperty alan in belge.RootElement.EnumerateObject())
            {
                // İlk alan komut adıdır; aggregate ise $out/$merge yazma yapabilir.
                if (MongoYazmaKomutlari.Contains(alan.Name.ToLowerInvariant()))
                    return true;
                if (alan.NameEquals("aggregate"))
                    return metin.Contains("$out", StringComparison.OrdinalIgnoreCase)
                        || metin.Contains("$merge", StringComparison.OrdinalIgnoreCase);
                return false; // find/count/distinct… → okuma
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return true; // çözümlenemeyen metni salt-okunur profilde göndermeyiz (temkinli)
        }
        return false;
    }

    /// <summary>
    /// <b>TEMKİNLİ</b> yazma denetimi (V5-S1b'de plan yolu için yazıldı; 2026-07-19'da
    /// <b>salt-okunur profil kapısına da bağlandı</b>).
    ///
    /// <see cref="YazmaSorgusuMu(string)"/> yalnız İLK anahtar sözcüğe bakar; çalıştırma
    /// yolunda bu yeterlidir ama plan yolunda YETMEZ, çünkü orada yanılmanın bedeli farklıdır:
    /// kullanıcı "yalnız plana bakıyorum" sanırken <c>EXPLAIN ANALYZE</c> sorguyu GERÇEKTEN
    /// çalıştırır. En sinsi örnek PostgreSQL'de veri değiştiren CTE'dir:
    /// <code>WITH silinen AS (DELETE FROM t RETURNING *) SELECT count(*) FROM silinen</code>
    /// İlk sözcük <c>WITH</c> olduğundan ilk-kelime mantığı bunu OKUMA sayar ve tüm tabloyu
    /// sildirir. Burada yorum ve metin sabitleri ayıklandıktan sonra <b>herhangi bir yerde</b>
    /// yazma anahtarı aranır: yanlış pozitifin bedeli "sorgu reddedildi", yanlış negatifin
    /// bedeli VERİ KAYBIdır — bu yüzden temkinli taraf seçildi.
    ///
    /// <b>SALT-OKUNUR PROFİL DE BUNU KULLANIR (2026-07-19 gözden geçirmesi).</b> Eskiden
    /// ilk-kelime denetimine bağlıydı ve iki açık kapı vardı:
    /// <list type="number">
    ///   <item>Veri değiştiren CTE (<c>WITH … DELETE … RETURNING</c>) — ilk sözcük WITH.</item>
    ///   <item>Blok yorumla başlayan script (<c>/* not */ DELETE FROM t</c>) — ilk "sözcük"
    ///         <c>/*</c> olduğundan yazma sayılmıyordu.</item>
    /// </list>
    /// Yani kullanıcı korunduğunu sanırken DELETE gidebiliyordu. Salt-okunur AÇIK bir güvenlik
    /// tercihi olduğundan artık temkinli tarafta: nadiren bir SELECT'i de reddedebilir
    /// (ör. <c>SELECT [delete] FROM t</c>), o durumda kullanıcı kutuyu kaldırır — tersi
    /// veri kaybıdır.
    /// </summary>
    public static bool PlanIcinYazmaSayilir(string sql, MotorTuru motor)
    {
        if (YazmaSorgusuMu(sql, motor))
            return true;
        if (motor == MotorTuru.Mongo)
            return false;   // Mongo'da metin JSON'dur; komut adı denetimi zaten yapıldı

        string temiz = YazmaSigortasi.YorumVeMetinleriAyikla(sql);
        foreach (string anahtar in YazmaAnahtarlari)
        {
            if (TamKelimeVarMi(temiz, anahtar))
                return true;
        }
        return false;
    }

    private static bool TamKelimeVarMi(string metin, string kelime)
    {
        int i = 0;
        while ((i = metin.IndexOf(kelime, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool solTemiz = i == 0 || (!char.IsLetterOrDigit(metin[i - 1]) && metin[i - 1] != '_');
            int son = i + kelime.Length;
            bool sagTemiz = son >= metin.Length || (!char.IsLetterOrDigit(metin[son]) && metin[son] != '_');
            if (solTemiz && sagTemiz)
                return true;
            i = son;
        }
        return false;
    }

    /// <summary>
    /// Batch'in ilk anlamlı token'ını sınıflar. Amaç %100 SQL parse değil,
    /// kaza sigortası (02-mimari §4.4); tam lexer v2'de zenginleşir.
    /// </summary>
    public static bool YazmaSorgusuMu(string sql)
    {
        foreach (string satir in sql.Split('\n'))
        {
            string s = satir.Trim();
            if (s.Length == 0 || s.StartsWith("--"))
                continue;
            string ilkKelime = s.Split(' ', '\t', '(', '\r')[0].ToUpperInvariant();
            return YazmaAnahtarlari.Contains(ilkKelime);
        }

        return false;
    }

    /// <summary>
    /// Batch bir saklı yordam çağrısı (EXEC/EXECUTE) mı — ilk anlamlı token'a bakar (kaza sigortası).
    /// Kullanıcı bulgusu 2026-07-27: Güvenli Yazma EXEC'i BEGIN TRAN'a sarınca, saklı yordamın KENDİ
    /// işlem yönetimiyle (@@TRANCOUNT / iç COMMIT-ROLLBACK) çakışıyor ve değişiklik kalıcı olmuyordu.
    /// EXEC bu yüzden Güvenli Yazma'ya alınmaz — SP kendi işlemini yönetir.
    /// </summary>
    public static bool ExecMi(string sql)
    {
        foreach (string satir in sql.Split('\n'))
        {
            string s = satir.Trim();
            if (s.Length == 0 || s.StartsWith("--"))
                continue;
            string ilk = s.Split(' ', '\t', '(', '\r')[0].ToUpperInvariant();
            return ilk is "EXEC" or "EXECUTE";
        }

        return false;
    }

    /// <summary>
    /// SQL Server'da işlem (BEGIN TRAN) İÇİNDE YASAK ifadeler (v20-S21 canlı bulgu 2026-08-14:
    /// Güvenli Yazma ALTER DATABASE'i işleme alınca sunucu 226 ile reddediyordu — kullanıcı
    /// "çalıştıramıyoruz" görüyordu). Batch'in HERHANGİ bir satırı yasak fiille başlıyorsa
    /// Güvenli Yazma devre dışı kalır (ifade doğrudan çalışır, nedeni kullanıcıya söylenir).
    /// Pratik çekirdek liste: veritabanı-düzeyi DDL + yedekleme/sunucu yapılandırması.
    /// </summary>
    public static bool TranIcindeYasakMi(string sql)
    {
        foreach (string satir in sql.Split('\n'))
        {
            string s = satir.Trim();
            if (s.Length == 0 || s.StartsWith("--"))
                continue;
            string[] k = s.ToUpperInvariant().Split(
                [' ', '\t', '(', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (k.Length == 0)
                continue;
            if (k[0] is "BACKUP" or "RESTORE" or "RECONFIGURE")
                return true;
            if (k[0] is "ALTER" or "CREATE" or "DROP" && k.Length > 1 && k[1] is "DATABASE" or "FULLTEXT")
                return true;
        }

        return false;
    }
}
