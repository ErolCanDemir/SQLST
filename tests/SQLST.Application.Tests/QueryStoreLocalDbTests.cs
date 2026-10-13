using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 📈 Query Store — gerçek LocalDB (v23-S28 S1). Geçici DB'de QS kapalı → durum KAPALI; önerilen script ile açılır →
/// AÇIK; sorgu koşturulup flush edilince en pahalı listesinde ve planlarında görünür; plan XML'i mevcut çözümleyiciyle
/// (MssqlPlanOkuyucu) açılır; plan zorlanır → is_forced_plan → zorlanmış listesinde; kaldırılır. Gerileyen sorgusu hatasız
/// koşar (gerileme üretilemez — pencere yapay). Temizlik finally'de.
/// </summary>
public class QueryStoreLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB", Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());

    private static Task<QueryResult> Kos(string sql, string? db = null, int timeout = 120)
        => Executor.ExecuteAsync(Profil(), sql, new ExecuteOptions { VeritabaniOverride = db, KomutTimeoutSnOverride = timeout }, CancellationToken.None);

    [Fact]
    public async Task Kapali_ac_yakala_listele_plan_zorla_kaldir()
    {
        string db = "SqlstQs_" + Guid.NewGuid().ToString("N")[..8];
        Assert.Null((await Kos($"CREATE DATABASE [{db}];")).Hata);
        try
        {
            // kapalı
            QueryResult s = await Kos(QueryStoreSorgulari.SurumSql(), db);
            Assert.Null(s.Hata);
            QsDurum on = QueryStoreSorgulari.DurumOku(s, null)!;
            Assert.True(on.Destekli);
            Assert.True(on.OkumaIzni && on.AlterIzni);
            // SQL Server 2022+ yeni veritabanında Query Store VARSAYILAN AÇIK (model'den) — kapalı yolu sınamak için kapat
            QueryResult d = await Kos(QueryStoreSorgulari.DurumSql(), db);
            Assert.Null(d.Hata);
            if (!QueryStoreSorgulari.DurumOku(s, d)!.Kapali)
                Assert.Null((await Kos($"ALTER DATABASE [{db}] SET QUERY_STORE = OFF;", "master")).Hata);
            QsDurum kapali = QueryStoreSorgulari.DurumOku(s, await Kos(QueryStoreSorgulari.DurumSql(), db))!;
            Assert.True(kapali.Kapali);

            // aç (K2 script'i) → açık, AUTO, 1000 MB, 60 dk
            Assert.Null((await Kos(QueryStoreSorgulari.AcSql(db), "master")).Hata);
            QsDurum acik = QueryStoreSorgulari.DurumOku(s, await Kos(QueryStoreSorgulari.DurumSql(), db))!;
            Assert.True(acik.Acik);
            Assert.Equal("AUTO", acik.YakalamaModu);
            Assert.Equal(1000, acik.MaxMb);
            Assert.Equal(60, acik.AralikDk);
            // AUTO yakalama tek seferlik sorguları atlayabilir → test için ALL
            Assert.Null((await Kos($"ALTER DATABASE [{db}] SET QUERY_STORE (QUERY_CAPTURE_MODE = ALL);", "master")).Hata);

            // sorgu koştur + flush
            Assert.Null((await Kos("CREATE TABLE dbo.QsT (Id int PRIMARY KEY, Ad nvarchar(50)); INSERT dbo.QsT VALUES (1, N'a'), (2, N'b');", db)).Hata);
            for (int i = 0; i < 3; i++)
                Assert.Null((await Kos("SELECT COUNT(*) AS n FROM dbo.QsT WHERE Id > 0 /* sqlst-qs-test */;", db)).Hata);
            Assert.Null((await Kos(QueryStoreSorgulari.FlushSql(), db)).Hata);

            // en pahalı: bizim sorgu listede, 3 yürütme, 1 plan
            QueryResult l = await Kos(QueryStoreSorgulari.EnPahaliSql(QsMetrik.Yurutme, QsIstatistik.Toplam, QsPencere.Saat24), db);
            Assert.Null(l.Hata);
            IReadOnlyList<QsSorgu> liste = QueryStoreSorgulari.SorgulariOku(l);
            // Query Store metni basit parametreleştirmeyle tutar: "(@1 tinyint)SELECT COUNT(*) [n] FROM [dbo].[QsT] WHERE [Id]>@1" — yorum gider
            QsSorgu biz = Assert.Single(liste, q => q.Sql.Contains("[QsT]") && q.Sql.Contains("COUNT"));
            Assert.Equal(3, biz.Yurutme);
            Assert.Equal(1, biz.PlanSayisi);
            Assert.True(biz.OrtMs >= 0);
            Assert.False(biz.Zorlanmis);
            // CPU/ortalama sıralaması ve tüm zamanlar da hatasız
            Assert.Null((await Kos(QueryStoreSorgulari.EnPahaliSql(QsMetrik.Cpu, QsIstatistik.Ortalama, QsPencere.Tumu), db)).Hata);
            Assert.Null((await Kos(QueryStoreSorgulari.EnPahaliSql(QsMetrik.Okuma, QsIstatistik.EnYuksek, QsPencere.Gun30), db)).Hata);
            // gerileyen: hatasız koşar (yapay veride gerileme beklenmez)
            QueryResult g = await Kos(QueryStoreSorgulari.GerilenSql(QsPencere.Saat24), db);
            Assert.Null(g.Hata);

            // planlar + XML → mevcut çözümleyici
            QueryResult p = await Kos(QueryStoreSorgulari.PlanlarSql(biz.QueryId, QsPencere.Saat24), db);
            Assert.Null(p.Hata);
            QsPlan plan = Assert.Single(QueryStoreSorgulari.PlanlariOku(p));
            Assert.Equal(3, plan.Yurutme);
            Assert.False(plan.Zorlanmis);
            string xml = QueryStoreSorgulari.PlanXmlOku(await Kos(QueryStoreSorgulari.PlanXmlSql(plan.PlanId), db))!;
            Assert.Contains("ShowPlanXML", xml);
            SorguPlani cozulen = MssqlPlanOkuyucu.Coz(xml, false);
            Assert.NotNull(cozulen);

            // zorla → zorlanmış listede ve plan satırında
            Assert.Null((await Kos(QueryStoreSorgulari.ZorlaSql(biz.QueryId, plan.PlanId), db)).Hata);
            QsPlan zorlu = Assert.Single(QueryStoreSorgulari.PlanlariOku(await Kos(QueryStoreSorgulari.PlanlarSql(biz.QueryId, QsPencere.Tumu), db)));
            Assert.True(zorlu.Zorlanmis);
            Assert.Null(zorlu.ZorlamaHatasi);
            QueryResult z = await Kos(QueryStoreSorgulari.ZorlanmisSql(), db);
            Assert.Null(z.Hata);
            Assert.Contains(QueryStoreSorgulari.SorgulariOku(z), q => q.QueryId == biz.QueryId && q.Zorlanmis);
            // kaldır
            Assert.Null((await Kos(QueryStoreSorgulari.ZorlamaKaldirSql(biz.QueryId, plan.PlanId), db)).Hata);
            Assert.False(Assert.Single(QueryStoreSorgulari.PlanlariOku(await Kos(QueryStoreSorgulari.PlanlarSql(biz.QueryId, QsPencere.Tumu), db))).Zorlanmis);
            Assert.DoesNotContain(QueryStoreSorgulari.SorgulariOku(await Kos(QueryStoreSorgulari.ZorlanmisSql(), db)), q => q.QueryId == biz.QueryId);
        }
        finally
        {
            await Kos($"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
        }
    }
}
