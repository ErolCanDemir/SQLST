using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// 🔐 Güvenlik — gerçek LocalDB (v23-S29 S1). Geçici SQL login + geçici DB'de kullanıcı/rol/GRANT/DENY/yetim döngüsü:
/// özet (karma kimlik, izinler, DB listesi), login listesi (politika, kilitli/süresi dolmuş, roller, SID), sunucu rol
/// ekle-çıkar, aç/kapat, DB eşlemeleri (SID), kullanıcı listesi (yetim tespiti, roller), DB yetkileri (GRANT+DENY), DB rol
/// ekle-çıkar, kullanıcı oluştur, yetim bağla (ALTER USER WITH LOGIN) → yetim değil. Temizlik finally'de (login + DB).
/// </summary>
public class GuvenlikLocalDbTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB", Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());

    private static Task<QueryResult> Kos(string sql, string? db = "master")
        => Executor.ExecuteAsync(Profil(), sql, new ExecuteOptions { VeritabaniOverride = db, KomutTimeoutSnOverride = 60 }, CancellationToken.None);

    private static async Task<T> Oku<T>(string sql, string? db, Func<QueryResult, T> oku)
    {
        QueryResult r = await Kos(sql, db);
        Assert.Null(r.Hata);
        return oku(r);
    }

    [Fact]
    public async Task Login_kullanici_rol_yetki_yetim_dongusu()
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string login = "sqlst_guv_" + id, db = "SqlstGuv_" + id, yetim = "sqlst_yetim_" + id;
        Assert.Null((await Kos($"CREATE LOGIN [{login}] WITH PASSWORD = N'Guv!Pass#2026x', CHECK_POLICY = OFF, DEFAULT_DATABASE = [master]; CREATE LOGIN [{yetim}] WITH PASSWORD = N'Guv!Pass#2026y', CHECK_POLICY = OFF;")).Hata);
        Assert.Null((await Kos($"CREATE DATABASE [{db}];")).Hata);
        try
        {
            // gerçek yetim: login'e bağlı kullanıcı yarat, sonra login'i SİL (geri yükleme/taşıma senaryosu). "WITHOUT LOGIN"
            // kullanıcısı yetim DEĞİLDİR — ALTER USER … WITH LOGIN onu bağlayamaz (33016; ilk sürüm bunu yetim sayıyordu).
            Assert.Null((await Kos($"CREATE TABLE dbo.T (Id int); CREATE USER [{login}] FOR LOGIN [{login}]; ALTER ROLE db_datareader ADD MEMBER [{login}]; GRANT UPDATE ON dbo.T TO [{login}]; DENY DELETE ON dbo.T TO [{login}]; CREATE USER [{yetim}] FOR LOGIN [{yetim}]; CREATE USER [bilerek_loginsiz] WITHOUT LOGIN;", db)).Hata);
            Assert.Null((await Kos($"DROP LOGIN [{yetim}];")).Hata);

            // özet
            GuvenlikOzet ozet = (await Oku(GuvenlikSorgulari.OzetSql(), "master", GuvenlikSorgulari.OzetOku))!;
            Assert.True(ozet.KarmaKimlik);
            Assert.True(ozet.Sysadmin);
            Assert.Contains(db, ozet.Veritabanlari);
            Assert.False(string.IsNullOrEmpty(ozet.KendiLogin));

            // login listesi + oturumlar
            var oturumlar = GuvenlikSorgulari.OturumlariOku(await Kos(GuvenlikSorgulari.OturumSayilariSql()));
            Assert.True(oturumlar.Count > 0);
            IReadOnlyList<Login> loginler = await Oku(GuvenlikSorgulari.LoginlerSql(), "master", r => GuvenlikSorgulari.LoginleriOku(r, oturumlar));
            Login l = Assert.Single(loginler, x => x.Ad == login);
            Assert.Equal("SQL", l.Tur); Assert.False(l.Kapali); Assert.False(l.PolitikaAcik); Assert.False(l.Kilitli);
            Assert.Equal("master", l.VarsayilanDb); Assert.NotNull(l.ParolaDegisti); Assert.StartsWith("0x", l.Sid);
            Assert.Contains(loginler, x => x.Tur == "Windows" && x.SunucuRolleri.Contains("sysadmin"));
            IReadOnlyList<string> sunucuRolleri = await Oku(GuvenlikSorgulari.SunucuRolleriSql(), "master", GuvenlikSorgulari.AdlariOku);
            Assert.Contains("sysadmin", sunucuRolleri); Assert.Contains("dbcreator", sunucuRolleri);
            IReadOnlyList<Yetki> sy = await Oku(GuvenlikSorgulari.SunucuYetkileriSql(login), "master", GuvenlikSorgulari.YetkileriOku);
            Assert.Contains(sy, y => y.Izin == "CONNECT SQL" && y.Durum == "GRANT");

            // sunucu rolü ekle / çıkar
            Assert.Null((await Kos(GuvenlikSorgulari.SunucuRolSql("dbcreator", login, true))).Hata);
            Assert.Contains("dbcreator", (await Oku(GuvenlikSorgulari.LoginlerSql(), "master", r => GuvenlikSorgulari.LoginleriOku(r))).Single(x => x.Ad == login).SunucuRolleri);
            Assert.Null((await Kos(GuvenlikSorgulari.SunucuRolSql("dbcreator", login, false))).Hata);
            // aç / kapat
            Assert.Null((await Kos(GuvenlikSorgulari.LoginAcKapatSql(login, true))).Hata);
            Assert.True((await Oku(GuvenlikSorgulari.LoginlerSql(), "master", r => GuvenlikSorgulari.LoginleriOku(r))).Single(x => x.Ad == login).Kapali);
            Assert.Null((await Kos(GuvenlikSorgulari.LoginAcKapatSql(login, false))).Hata);

            // DB eşlemeleri (SID)
            var es = await Oku(GuvenlikSorgulari.EslemeSql([db, "master"]), "master", GuvenlikSorgulari.EslemeleriOku);
            LoginEslemesi e = Assert.Single(es[l.Sid]);
            Assert.Equal(db, e.Veritabani); Assert.Equal(login, e.Kullanici); Assert.Contains("db_datareader", e.Roller);

            // kullanıcılar: bizimki bağlı, yetim yetim
            IReadOnlyList<Kullanici> ks = await Oku(GuvenlikSorgulari.KullanicilarSql(), db, GuvenlikSorgulari.KullanicilariOku);
            Kullanici k = Assert.Single(ks, x => x.Ad == login);
            Assert.False(k.Yetim); Assert.Equal(login, k.BagliLogin); Assert.Equal("INSTANCE", k.Kimlik); Assert.Contains("db_datareader", k.Roller);
            Kullanici y1 = Assert.Single(ks, x => x.Ad == yetim);
            Assert.True(y1.Yetim); Assert.Null(y1.BagliLogin); Assert.Equal("INSTANCE", y1.Kimlik);
            Kullanici bl = Assert.Single(ks, x => x.Ad == "bilerek_loginsiz");
            Assert.False(bl.Yetim); Assert.Equal("NONE", bl.Kimlik); Assert.Null(bl.BagliLogin);
            Assert.DoesNotContain(ks, x => x.Ad is "guest" or "sys" or "INFORMATION_SCHEMA");
            IReadOnlyList<string> dbRolleri = await Oku(GuvenlikSorgulari.DbRolleriSql(), db, GuvenlikSorgulari.AdlariOku);
            Assert.Contains("db_owner", dbRolleri); Assert.Contains("db_datawriter", dbRolleri);

            // DB yetkileri: GRANT UPDATE + DENY DELETE + CONNECT
            IReadOnlyList<Yetki> dy = await Oku(GuvenlikSorgulari.DbYetkileriSql(login), db, GuvenlikSorgulari.YetkileriOku);
            Assert.Contains(dy, y => y.Sinif == "OBJECT_OR_COLUMN" && y.Nesne == "dbo.T" && y.Izin == "UPDATE" && y.Durum == "GRANT");
            Assert.Contains(dy, y => y.Nesne == "dbo.T" && y.Izin == "DELETE" && y.Deny);
            Assert.Contains(dy, y => y.Sinif == "DATABASE" && y.Izin == "CONNECT");

            // DB rolü ekle / çıkar + kullanıcı script'i koşabilir mi (yeniden kurma)
            Assert.Null((await Kos(GuvenlikSorgulari.DbRolSql("db_datawriter", login, true), db)).Hata);
            Assert.Contains("db_datawriter", (await Oku(GuvenlikSorgulari.KullanicilarSql(), db, GuvenlikSorgulari.KullanicilariOku)).Single(x => x.Ad == login).Roller);
            Assert.Null((await Kos(GuvenlikSorgulari.DbRolSql("db_datawriter", login, false), db)).Hata);

            // yetim bağla: aynı adla login yeniden yaratılır (yeni SID) → ALTER USER … WITH LOGIN → artık yetim değil
            Assert.Null((await Kos($"CREATE LOGIN [{yetim}] WITH PASSWORD = N'Guv!Pass#2026y', CHECK_POLICY = OFF;")).Hata);
            Assert.Null((await Kos(GuvenlikSorgulari.YetimBaglaSql(yetim, yetim), db)).Hata);
            Kullanici y2 = (await Oku(GuvenlikSorgulari.KullanicilarSql(), db, GuvenlikSorgulari.KullanicilariOku)).Single(x => x.Ad == yetim);
            Assert.False(y2.Yetim); Assert.Equal(yetim, y2.BagliLogin);

            // login için başka DB'de kullanıcı oluştur (master'da) → eşlemede 2 DB
            Assert.Null((await Kos(GuvenlikSorgulari.KullaniciOlusturSql(login), "master")).Hata);
            try
            {
                var es2 = await Oku(GuvenlikSorgulari.EslemeSql([db, "master"]), "master", GuvenlikSorgulari.EslemeleriOku);
                Assert.Equal(2, es2[l.Sid].Count);
            }
            finally
            {
                await Kos($"DROP USER [{login}];", "master");
            }

            // login script'i yeniden kurulabilir (parola yer tutucusu dışında) — söz dizimi NOEXEC ile
            string script = GuvenlikSorgulari.LoginScripti(l, [e], DateTime.Now, "localdb").Replace("<parola>", "Guv!Pass#2026z").Replace($"[{login}]", $"[{login}_k]");
            QueryResult noexec = await Kos("SET NOEXEC ON;\n" + script + "\nSET NOEXEC OFF;");
            Assert.Null(noexec.Hata);
        }
        finally
        {
            await Kos($"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
            await Kos($"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}]; IF SUSER_ID(N'{yetim}') IS NOT NULL DROP LOGIN [{yetim}];");
        }
    }
}
