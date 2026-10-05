using System.Data.Common;

namespace SQLST.Contracts;

/// <summary>
/// SQL ailesi motor lehçesi (V3-S1 / Faz 0 — 08-v3r1 §4).
///
/// Veri erişim gövdesi (SonucOkuyucu/SqlExecutor/DbOturum) <see cref="DbConnection"/>
/// gibi ADO.NET SOYUT tipleriyle çalışır; motordan motora değişen her şey bu arayüzün
/// arkasındadır. Yeni bir ilişkisel motor eklemek = yeni bir ILehce yazmak, gövdeye
/// dokunmamak.
///
/// Kapsam notu: MongoDB bu arayüzü UYGULAMAZ — ne ADO.NET ne SQL konuşur, ayrı ailedir
/// (08-v3r1 §5). Lehçe yalnız SQL ailesinin (MSSQL/PostgreSQL/MySQL/Oracle) sözleşmesidir.
/// </summary>
public interface ILehce
{
    /// <summary>"mssql" | "postgres" | "mysql" | "oracle" — profil/geçmiş kaydında motoru işaretler.</summary>
    string MotorId { get; }

    /// <summary>Tanımlayıcıyı motorun tırnağıyla sarar: [ad] · "ad" · `ad` (kaçış dahil).</summary>
    string TirnaklaTanimlayici(string ad);

    /// <summary>
    /// Profilden bağlantı nesnesi üretir (dize kurulumu + sağlayıcı seçenekleri dahil).
    /// <paramref name="havuz"/>: kalıcı oturumda false — açık transaction'lı bağlantı
    /// havuza sızmamalı (07-r2 §2).
    /// </summary>
    DbConnection BaglantiOlustur(ConnectionProfile profil, string? veritabaniOverride, bool havuz);

    /// <summary>
    /// Komut çalıştırılmadan önce sağlayıcıya özgü ayar (Faz 2 bulgusu): Oracle'da LONG/CLOB
    /// kolonlarının düz string dönmesi için Initial(LONG|LOB)FetchSize=-1 komut düzeyinde
    /// verilir; diğer motorlarda no-op.
    /// </summary>
    void KomutuAyarla(DbCommand komut);

    /// <summary>
    /// Sunucudan gelen bilgi mesajlarına (PRINT, "N satır etkilendi") abone olur.
    /// Sağlayıcıya özgüdür — SqlClient InfoMessage, Npgsql Notice. Dönen nesne
    /// Dispose edilince abonelik kalkar.
    /// </summary>
    IDisposable BilgiMesajlariniDinle(DbConnection baglanti, Action<string> topla);

    /// <summary>
    /// Sağlayıcı istisnasını ortak hata modeline çevirir (SQL Server Msg/severity,
    /// Postgres SQLSTATE...). Tanımadığı istisnada da mesajı kaybetmez.
    /// </summary>
    SqlHata HataYorumla(Exception ex);

    /// <summary>
    /// İstisnadaki TÜM hataları çevirir (kullanıcı isteği 2026-07-30: canlı denetimde "bütün
    /// hatalar gelsin"). Varsayılan tek hata döner; SQL Server SqlException.Errors ile hepsini verir.
    /// </summary>
    IReadOnlyList<SqlHata> HatalariYorumla(Exception ex) => [HataYorumla(ex)];

    /// <summary>
    /// Açık bağlantıda veritabanı değiştirilebilir mi? (Faz 1 bulgusu)
    /// MSSQL: true — <c>USE [db]</c> yeter. PostgreSQL: false — veritabanı bağlantı
    /// dizesine gömülüdür, değiştirmek YENİDEN BAĞLANMAYI gerektirir; oturum bunu görüp
    /// bağlantıyı tazeler.
    /// </summary>
    bool AcikBaglantidaVeritabaniDegisir { get; }

    /// <summary>
    /// Aktif veritabanını değiştiren ifade (MSSQL: <c>USE [db];</c>).
    /// Yalnız <see cref="AcikBaglantidaVeritabaniDegisir"/> true iken çağrılır.
    /// </summary>
    string VeritabaniSecSql(string veritabani);

    /// <summary>
    /// Yeni veritabanı oluşturma SQL'i (kullanıcı isteği 2026-07-31: "yeni database oluşturacağımız
    /// alan yok"). Null = bu motorda uygulama içinden DB oluşturma YOK ve UI özelliği gizler
    /// (çoklu-motor kuralı). Oracle null: orada "database" örnek düzeyidir — şema/kullanıcı
    /// oluşturmak (CREATE USER) ayrı bir iştir, sessiz yanlış üretmeyelim.
    /// </summary>
    string? VeritabaniOlusturSql(string ad) => null;

    /// <summary>
    /// Bağlantıdaki açık transaction durumunu belirler (Güvenli Yazma bandı — V2-S4).
    /// Tek bir SQL dizesiyle modellenemez: MSSQL <c>XACT_STATE()</c> ile öğrenir,
    /// PostgreSQL'de protokol düzeyinde bir kavramdır ve sorgu ile yoklanır (Faz 1 bulgusu).
    /// </summary>
    Task<IslemDurumu> IslemDurumuAsync(DbConnection baglanti, CancellationToken ct);

    /// <summary>
    /// <see cref="IslemDurumuAsync"/> gerçek bir cevap veriyor mu (V4-S1). Bugün yalnız
    /// MSSQL <c>XACT_STATE()</c> ile bilir; diğerleri her zaman <see cref="IslemDurumu.Yok"/>
    /// döner. Çağıranlar bunu ayırt etmezse "Yok" yanlışlıkla "işlem öldü" sanılır ve
    /// sağlam bir işlem gereksiz yere geri alınır — Edit modu commit öncesi denetimi
    /// bu bayrağa bakar.
    /// </summary>
    bool IslemDurumuBilinir { get; }

    /// <summary>Kapanışta açık transaction'ı geri alan ifade (MSSQL: IF @@TRANCOUNT > 0 ROLLBACK;).</summary>
    string GeriAlSql();

    // ── Katalog (nesne gezgini — FG-2.x) ─────────────────────────────────────
    // SchemaService bu SQL'leri çalıştırıp KANONİK biçimli sonuç kümelerini ayrıştırır;
    // motordan motora değişen tek şey SQL metni + tip/tür yorumudur. Kanonik biçim her
    // motorda AYNI kolon sırası/anlamı üretmek zorundadır (08-v3r1 §4).

    /// <summary>
    /// Erişilebilir veritabanlarını listeler. Kanonik sonuç: kolon0 = ad (string),
    /// kolon1 = sistem mi (0/1). MSSQL sys.databases; PostgreSQL pg_database.
    /// </summary>
    string VeritabanlariSorgusu { get; }

    /// <summary>
    /// Şema yükleme sorguları. Sorgular sırayla çalıştırılır ve ürettikleri sonuç kümeleri
    /// SIRAYLA birleştirilir; toplamda DÖRT kanonik küme oluşmalıdır:
    /// [0] aktif db adı; [1] nesneler (sema, ad, tür-kodu); [2] kolonlar
    /// (sema, ad, kolon, tip, uzunluk, kesinlik, ölçek, null-olabilir truthy, pk 0/1);
    /// [3] parametreler (sema, ad, param, tip, uzunluk, kesinlik, ölçek, çıkış truthy).
    /// MSSQL/PostgreSQL tek batch'te dördünü döndürür (tek eleman — tek round-trip);
    /// Oracle tek komutta tek SELECT kabul ettiğinden dört ayrı sorgu verir (Faz 2 bulgusu).
    /// "truthy": bool VEYA 0/1 sayısal — motorların boolean'sızlığı için (MySQL 1/0 döner).
    /// </summary>
    IReadOnlyList<string> SemaSorgulari { get; }

    /// <summary>
    /// Yabancı anahtar bağlarını KANONİK, satır-başına-kolon düzeninde okur (v6-S2). Kolon
    /// sırası: [0] fk-kimliği (gruplama anahtarı — bileşik anahtarın satırları bununla toplanır),
    /// [1] sıra (bileşik anahtarda kolon sırası), [2] kaynak şema, [3] kaynak tablo,
    /// [4] kaynak kolon, [5] hedef şema, [6] hedef tablo, [7] hedef kolon.
    ///
    /// <b>STRING_AGG/LISTAGG KULLANILMAZ</b> (eski sunucu uyumu — STRING_AGG dersi 2026-07-20):
    /// kolonlar birleştirilmez, satır-satır döner; <see cref="ISchemaService"/> fk-kimliğine
    /// göre gruplayıp <see cref="YabanciAnahtar"/>'a çevirir.
    /// </summary>
    string YabanciAnahtarSorgusu { get; }

    /// <summary>
    /// Tablo indekslerini KANONİK, satır-başına-kolon düzeninde okur (v7 index farkı). Kolon sırası:
    /// [0] şema, [1] tablo, [2] indeks adı, [3] benzersiz truthy, [4] kolon sırası (anahtar içi),
    /// [5] kolon adı. <b>PK-destekli indeks HARİÇ</b> (PK zaten kolon farkında görünür). STRING_AGG
    /// KULLANILMAZ (eski sunucu dersi) — satır-satır döner, <see cref="ISchemaService"/> gruplar.
    /// <b>Boş dize</b> = bu motorda index okuma yok (şema farkında index satırı üretilmez).
    /// </summary>
    string IndeksSorgusu => "";

    /// <summary>
    /// Nesne gövdesi (SP/view/fonksiyon) sorgusu. Kanonik sonuç: BİR VEYA ÇOK satır, ilk
    /// kolon = tanım parçası; okuyucu satırları sırayla birleştirir (MSSQL/PG tek satır;
    /// Oracle ALL_SOURCE satır-satır döndürür — Faz 2 bulgusu). Hiç satır yoksa/NULL ise
    /// tanım yok sayılır. Ad literale gömülür (executor parametre almaz) — kaçış motorun işi.
    /// </summary>
    string TanimSorgusu(SemaNesnesi nesne);

    /// <summary>Tür kodunu (sys.objects.type / pg prokind vb.) ortak türe çevirir.</summary>
    SemaNesneTuru TurCevir(string kod);

    /// <summary>Kanonik kolon parçalarından görüntülük tam tip adı: nvarchar(50), varchar, numeric(18,2)…</summary>
    string TipYaz(string tip, int uzunluk, int kesinlik, int olcek);

    /// <summary>
    /// "İlk N satır" sorgusu (FG-2.6/5.7) — satır sınırlama ve tırnaklama motora göre değişir:
    /// MSSQL <c>SELECT TOP n</c>, PostgreSQL/MySQL <c>LIMIT n</c>, Oracle <c>FETCH FIRST n ROWS ONLY</c>
    /// (ve sonda ';' yok). Kullanıcı bulgusu 2026-07-18: menü her motorda görünüyordu ama
    /// T-SQL script üretip hata veriyordu.
    /// </summary>
    string IlkNSatirSorgusu(SemaNesnesi nesne, int n);

    /// <summary>
    /// ÖZEL kurulan SELECT'lerde satır sınırının BAŞ parçası (özellik eşitliği 2026-08-03 —
    /// Veri Arama/Kayıt Haritası artık motor-parametrik): MSSQL <c>"TOP (n) "</c>, diğerlerinde boş.
    /// DEFAULT interface üyesidir — yalnız ILehce referansından çağrılır (CLAUDE.md dersi).
    /// </summary>
    string SatirSinirBasi(int n) => "";

    /// <summary>… ve KUYRUK parçası: PG/MySQL <c>" LIMIT n"</c> (varsayılan), Oracle
    /// <c>" FETCH FIRST n ROWS ONLY"</c>, MSSQL boş. Noktalı virgülden ÖNCE eklenir.</summary>
    string SatirSinirSonu(int n) => $" LIMIT {n}";

    // ── Edit modu (V2-S5 → V4-S1'de motor bazlı) ─────────────────────────────
    // V4-S1 kararı: Edit modu SQL ailesinin DÖRT motorunda da desteklenir. Fark eden
    // parçalar burada toplanmıştır: kolon üst verisi sorgusu, literal kuralları,
    // tam ad yazımı, "hepsi DEFAULT" satır ekleme, ifade sonlandırıcı ve işlem ifadeleri.
    // (MongoDB bu arayüzü uygulamaz — belge düzenleme ayrı bir tasarımdır, 08-v3r1 §5.)

    /// <summary>
    /// Edit modu (V2-S5) kolon üst verisi sorgusu. Kanonik sonuç (kolon başına bir satır):
    /// ad, sysTip, uzunluk, kesinlik, ölçek, null-olabilir truthy, identity truthy,
    /// computed truthy, sürüm-kolonu 0/1, pk 0/1. Motor Edit modunu desteklemiyorsa
    /// (<see cref="DuzenlemeDestekler"/> false) NotSupportedException fırlatabilir.
    /// </summary>
    string DuzenlemeMetaSorgusu(SemaNesnesi tablo);

    /// <summary>
    /// Edit modu bu motorda kullanılabilir mi (V4-S1). Arayüz kapıları bunu okur —
    /// "yarım özellik yok" ilkesi: desteklemiyorsa menü öğesi hiç görünmez.
    /// </summary>
    bool DuzenlemeDestekler { get; }

    /// <summary>Bu motorun literal yazım kuralları (metin/bool/binary/tarih biçimleri).</summary>
    LiteralKurallari LiteralKurallari { get; }

    /// <summary>
    /// Şema + tablo tam adı, motorun tırnaklamasıyla: <c>[şema].[tablo]</c> · <c>"şema"."tablo"</c> ·
    /// MySQL'de şema = veritabanı olduğundan yalnız <c>`tablo`</c>.
    /// </summary>
    string TamAdYaz(string sema, string tablo);

    /// <summary>
    /// Tüm kolonları varsayılana bırakan satır ekleme ifadesi. MSSQL/PostgreSQL
    /// <c>DEFAULT VALUES</c>; MySQL <c>() VALUES ()</c>; Oracle'da karşılığı yoktur
    /// (NotSupportedException — kullanıcıya en az bir kolon doldurması söylenir).
    /// </summary>
    string BosSatirEkleSql(string tamAd);

    /// <summary>İfade sonlandırıcı: çoğu motorda <c>";"</c>, Oracle'da boş (ORA-00933).</summary>
    string IfadeSonu { get; }

    /// <summary>
    /// Açık işlem başlatan ifade (MSSQL <c>BEGIN TRAN;</c>, PostgreSQL <c>BEGIN;</c>,
    /// MySQL <c>START TRANSACTION;</c>). <b>Boş dize</b> = motorda işlem örtük başlar
    /// (Oracle) — çağıran bu ifadeyi atlar.
    /// </summary>
    string IslemBaslatSql { get; }

    /// <summary>Kalıcılaştırma ifadesi (Oracle'da sonda ';' yoktur).</summary>
    string CommitSql { get; }

    // ── Güvenli Yazma Modu (V2-S4 → V4-S2'de motor bazlı) ────────────────────

    /// <summary>
    /// Güvenli Yazma Modu bu motorda anlamlı mı (V4-S2). Modun sözü şudur: yazma açık
    /// işlemde çalışır, kullanıcı "N satır etkilendi" bandını görür ve ROLLBACK derse
    /// <b>veri hiç değişmemiş olur</b>. Motor bu sözü tutamıyorsa özellik açılmaz —
    /// hiçbir şey yapmayan bir ROLLBACK düğmesi göstermek sessiz yalandır.
    /// SQL ailesinin dördü de DML'de tutar; ayrım <see cref="OrtukCommitYaparMi"/>'dedir.
    /// </summary>
    bool GuvenliYazmaDestekler { get; }

    /// <summary>
    /// Bu ifade motorda <b>örtük COMMIT</b> tetikliyor mu (V4-S2) — yani açık bir işlem
    /// içinde bile geri alınamaz mı?
    ///
    /// MySQL/MariaDB ve Oracle'da DDL (CREATE/ALTER/DROP/TRUNCATE/RENAME/GRANT…) çalıştığı
    /// anda işlemi kalıcılaştırır; böyle bir ifadeye Güvenli Yazma bandı açmak, geri
    /// alamayacağı bir şey için "geri al" düğmesi göstermek olurdu. SQL Server ve
    /// PostgreSQL'de <b>DDL de işlemseldir</b> → daima false.
    /// </summary>
    bool OrtukCommitYaparMi(string sql);

    // ── Execution plan (V5-S1) ───────────────────────────────────────────────

    /// <summary>
    /// Execution plan görüntüleme bu motorda hazır mı (V5-S1). <b>Beş motorda da AÇIK:</b>
    /// SQL Server (showplan XML) · PostgreSQL (<c>EXPLAIN … FORMAT JSON</c>) · MySQL
    /// (<c>EXPLAIN FORMAT=JSON</c>) · Oracle (<c>EXPLAIN PLAN</c> + <c>PLAN_TABLE</c>) ·
    /// MongoDB (<c>explain</c>; o ayrı ailedir ve bu sözleşmeyi uygulamaz).
    /// Kapalıyken düğme arayüzde GÖRÜNMEZ.
    /// </summary>
    bool PlanDestekler { get; }

    /// <summary>
    /// Plan toplamayı açan OTURUM ifadesi (MSSQL <c>SET SHOWPLAN_XML ON</c>).
    /// <b>Boş dize = oturum düzeyinde açma gerekmez</b> — çağıran bu adımı atlar
    /// (PostgreSQL planı sorgunun kendisini sarar, oturum ayarı kullanmaz;
    /// <see cref="IslemBaslatSql"/>'deki aynı desen).
    /// Desteklemeyen motorda <see cref="NotSupportedException"/>.
    /// </summary>
    string PlanAcSql(bool gercek);

    /// <summary>Plan toplamayı kapatan ifade; boşsa atlanır (oturum kalıcı olduğundan MUTLAKA kapatılır).</summary>
    string PlanKapatSql(bool gercek);

    /// <summary>
    /// Kullanıcının sorgusunu plan isteğine çevirir. MSSQL'de metin AYNEN gider (plan
    /// oturum ayarıyla toplanır); PostgreSQL'de sorgu <c>EXPLAIN (…) &lt;sql&gt;</c> ile SARILIR.
    ///
    /// <b>DİKKAT — güvenlik:</b> sarma sonrası ifadenin ilk anahtar sözcüğü <c>EXPLAIN</c>
    /// olur ve salt-okunur kapısı onu yazma saymaz. Çağıran bu yüzden yazma denetimini
    /// <b>HAM sql üzerinde, sarmadan ÖNCE</b> yapmak zorundadır (canlı kanıt: PG'de
    /// <c>EXPLAIN (ANALYZE) DELETE</c> satırları gerçekten siler).
    /// </summary>
    string PlanSorgusuYaz(string sql, bool gercek);

    /// <summary>
    /// Plan isteği <b>iki adımlıysa</b> ikinci adımın sorgusu; tek adımlıysa boş dize (V5-S1d).
    ///
    /// Oracle'da <c>EXPLAIN PLAN FOR …</c> hiç satır döndürmez, planı <c>PLAN_TABLE</c>'a
    /// YAZAR — plan ancak ikinci bir sorguyla okunur. Boş değilse çağıran bu sorguyu çalıştırır
    /// ve <see cref="PlanCoz"/>'a ONUN sonucunu verir.
    /// (<see cref="PlanAcSql"/>/<see cref="PlanKapatSql"/> ile aynı "boş = adımı atla" deseni.)
    /// </summary>
    string PlanOkumaSql(bool gercek);

    /// <summary>
    /// Sunucu cevabından planı çıkarır ve çözümler. Plan çıktısının nerede/hangi kolon adıyla
    /// geldiği motora özgüdür (MSSQL "…XML Showplan" kolonu, PostgreSQL "QUERY PLAN"),
    /// bu yüzden bulma ve çözme birlikte lehçededir.
    ///
    /// <b>Çok ifadeli batch'te TÜM ifadelerin planı toplanmalıdır</b> — ilk planda durup
    /// gerisini atmak kullanıcıya eksiksiz görünen yanlış bir plan gösterir (MSSQL'de tam
    /// bu oluyordu; A1/B1, 2026-07-19). Plan yoksa <see cref="InvalidOperationException"/>.
    /// </summary>
    SorguPlani PlanCoz(QueryResult sonuc, bool gercek);

    /// <summary>
    /// Plan isteği tek bir ifade gerektiriyor mu. PostgreSQL'de <c>EXPLAIN</c> tek ifade sarar;
    /// çok ifadeli metin gönderilirse anlamsız bir sözdizimi hatası döner — çağıran önce
    /// açık bir mesaj verir.
    /// </summary>
    bool PlanTekIfadeIster { get; }

    /// <summary>
    /// <b>GERÇEK</b> plan (sorguyu çalıştırıp ölçülen satır sayılarını getirme) bu motorda
    /// destekleniyor mu (V5-S1c). Tahmini plan desteklenip gerçek plan desteklenmeyebilir:
    /// MySQL ile MariaDB burada AYRIŞIR — MariaDB <c>ANALYZE FORMAT=JSON</c> ile aynı JSON'u
    /// döndürür, MySQL 8 ise <c>EXPLAIN ANALYZE</c> ile <b>JSON değil TREE metni</b> verir.
    /// Aynı lehçenin arkasındaki iki sunucu farklı çıktı verdiğinden ve canlı doğrulama
    /// yapılamadığından bu motorda gerçek plan AÇILMADI ("yarım özellik yok").
    /// Kapalıysa "Gerçek plan" düğmesi görünmez.
    /// </summary>
    bool PlanGercekDestekler { get; }

    /// <summary>
    /// Bir tipin Güvenli Yazma "eski değer" kıyasına güvenle girip giremeyeceği (07-r2 §4):
    /// float/text/xml gibi kırılgan tipler ve max-uzunluk kolonları dışlanır.
    /// </summary>
    bool KiyasGuvenliMi(string tip, int uzunluk);

    // ── Sunucuda metin arama (FG-7.1 → V5-S2) ───────────────────────────────

    /// <summary>
    /// Nesne TANIMLARINDA metin arama sorgusu (V5-S2). Kanonik sonuç (eşleşen nesne başına
    /// bir satır): <c>sema, ad, tur-kodu, tanim</c>.
    ///
    /// <b>Süzme SUNUCUDA yapılır</b> — tüm tanımları çekip istemcide aramak büyük bir
    /// veritabanında megabaytlarca metin taşırdı. Satır/bağlam çıkarımı ise motor-nötrdür
    /// ve dönen tanım metni üzerinde Application katmanında yapılır (bir kez yazılır).
    ///
    /// <b>Kapsam:</b> yalnız seçili veritabanı ve yalnız nesne tanımları. Veri içinde arama
    /// bilinçli olarak yoktur: <c>LIKE '%x%'</c> baştan joker olduğundan index kullanılamaz
    /// ve her tablo baştan sona taranırdı.
    ///
    /// <paramref name="aranan"/> ham kullanıcı metnidir; <b>kaçış lehçenin sorumluluğudur</b>
    /// (executor parametre almaz — katalog sorgularındaki desen).
    ///
    /// <b>TAVAN ZORUNLUDUR (B4/A5, 2026-07-19):</b> sorgu en çok <see cref="AramaTavani"/>
    /// nesne döndürmelidir. Tavan yokken çok yaygın bir metin (ör. <c>SELECT</c>) arandığında
    /// veritabanındaki TÜM nesne tanımları ağdan geçip belleğe alınıyordu; binlerce SP'li
    /// kurumsal bir veritabanında bu fark edilir bir gecikmedir. Çağıran, dönen nesne sayısı
    /// tavana EŞİTSE kullanıcıya "ilk N gösteriliyor" uyarısı basar — sessizce kırpmak,
    /// kullanıcıya eksik listeyi tam sanmasına yol açardı.
    /// </summary>
    string MetinAramaSorgusu(string aranan);

    /// <summary>
    /// Metin aramasında döndürülecek en fazla nesne sayısı. Motordan bağımsız sabittir:
    /// arayüzdeki uyarı metni ve testler bu tek değere dayanır.
    /// </summary>
    const int AramaTavani = 200;

    /// <summary>
    /// Yönetim Paneli bölümleri (V3 — kullanıcı isteği 2026-07-18: panel her motorda O MOTORUN
    /// araçlarını göstermeli). Boş liste = bu motor için genel panel yok (MSSQL'in kendi
    /// özel paneli, MongoDB'nin kendi ailesi vardır). Salt okunur sorgulardır.
    /// </summary>
    IReadOnlyList<TeshisBolumu> TeshisBolumleri { get; }

    // ── Veri karşılaştırma (v7-S2) ───────────────────────────────────────────

    /// <summary>
    /// Bir tablonun her satırı için <c>(anahtar kolonlar…, satır parmak izi)</c> döndüren sorgu.
    /// İki veritabanının aynı tablosunu satır bazında kıyaslamak için: (anahtar, hash) çiftleri iki
    /// taraftan çekilip karşılaştırılır (yalnız solda / yalnız sağda / farklı). Kanonik sonuç: önce
    /// anahtar kolonlar (verilen sırada), en SON kolon parmak izi (<c>__hash</c>).
    ///
    /// <b>Varsayılan null</b> = bu motorda veri karşılaştırma henüz yok (UI o motorda gizler/uyarır).
    /// Anahtar kolon listesi boşsa null. <paramref name="whereKosulu"/> ham WHERE (WHERE'siz);
    /// iki tarafta AYNEN uygulanır — kaçış/güven çağıranındır (katalog sorgu deseni).
    /// <paramref name="tumKolonlar"/>: tablonun TÜM kolonları — satır parmak izini kolon
    /// birleştirerek üreten motorlar (MySQL/Oracle) için; MSSQL <c>BINARY_CHECKSUM(*)</c> ve
    /// PostgreSQL <c>md5(satır::text)</c> tüm kolonları kendiliğinden kapsadığından bunu kullanmaz.
    /// </summary>
    string? SatirHashSorgusu(
        string? sema, string tablo, IReadOnlyList<string> anahtarKolonlar,
        IReadOnlyList<string> tumKolonlar, string? whereKosulu)
        => null;
}

/// <summary>
/// Yönetim Panelinde bir sekme: başlık + ne anlama geldiğini anlatan kısa açıklama +
/// çalıştırılacak salt-okunur sorgu. <paramref name="VeritabaniGerekir"/> true ise sorgu
/// seçili veritabanına bağlıdır (panel DB seçicisi bunu besler).
///
/// <paramref name="Takip"/> (kullanıcı isteği 2026-07-19): "bu alandan HANGİ BİLGİ takip
/// edilecek?" — <see cref="Aciklama"/> bölümün <i>ne olduğunu</i> anlatır, Takip ise
/// <b>hangi kolona bakılacağını, hangi eşiğin kötü sayıldığını ve ne yapılacağını</b> söyler.
/// Panelde ayrı bir "👁 Neyi takip edin" bandında gösterilir.
///
/// <b>Varsayılanı YOKTUR — bilerek.</b> Varsayılan verilseydi yeni eklenen bir bölüm notsuz
/// kalabilir ve bunu kimse fark etmezdi; zorunlu olduğu için derleyici her yeni bölümde
/// notu sorar. (<c>PanelTakipNotlariTests</c> ayrıca boş/kısa metni de yakalar.)
/// </summary>
public sealed record TeshisBolumu(
    string Baslik, string Aciklama, string Sorgu, string Takip, bool VeritabaniGerekir = false);

/// <summary>
/// Profilin motoruna göre lehçe çözer (V3-S1 motor seçimi): executor/oturum/şema tek
/// örnek kalır, motor farkı çağrı anında profilden gelir. Yeni motor eklemek =
/// sağlayıcı kaydına bir lehçe daha eklemek.
/// </summary>
public interface ILehceSaglayici
{
    /// <summary>Kayıtlı olmayan motorda NotSupportedException fırlatır (sessiz yanlış motor olmaz).</summary>
    ILehce Getir(MotorTuru motor);
}
