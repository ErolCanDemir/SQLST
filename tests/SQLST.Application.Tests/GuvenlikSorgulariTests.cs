using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>
/// 🔐 Güvenlik saf çekirdeği (v23-S29 S1): DDL script'leri (kaçışlı), login/kullanıcı yeniden kurma script'i (parola yazılmaz —
/// K4), tehlikeli rol sınıflaması, eşleme SQL'i (DB başına UNION), ayrıştırma (özet, login, oturum, kullanıcı, yetki, eşleme),
/// hata çevirisi. Gerçek LocalDB turu ayrı dosyada.
/// </summary>
public class GuvenlikSorgulariTests
{
    private static ResultSetData Rs(string[] k, params object?[][] s) => new() { Kolonlar = [.. k.Select(x => new KolonBilgisi(x, "x"))], Satirlar = s };
    private static readonly DateTime Z = new(2026, 10, 13, 11, 0, 0);

    [Fact]
    public void Ddl_scriptleri_kacisli()
    {
        Assert.Equal("ALTER LOGIN [ra]]por] DISABLE;", GuvenlikSorgulari.LoginAcKapatSql("ra]por", true));
        Assert.Equal("ALTER LOGIN [x] ENABLE;", GuvenlikSorgulari.LoginAcKapatSql("x", false));
        Assert.Equal("ALTER SERVER ROLE [dbcreator] ADD MEMBER [rapor_okuyucu];", GuvenlikSorgulari.SunucuRolSql("dbcreator", "rapor_okuyucu", true));
        Assert.Equal("ALTER ROLE [db_datareader] DROP MEMBER [u];", GuvenlikSorgulari.DbRolSql("db_datareader", "u", false));
        Assert.Equal("CREATE USER [rapor_okuyucu] FOR LOGIN [rapor_okuyucu];", GuvenlikSorgulari.KullaniciOlusturSql("rapor_okuyucu"));
        Assert.Equal("CREATE USER [u2] FOR LOGIN [l];", GuvenlikSorgulari.KullaniciOlusturSql("l", "u2"));
        Assert.Equal("ALTER USER [eski_rapor] WITH LOGIN = [eski_rapor];", GuvenlikSorgulari.YetimBaglaSql("eski_rapor", "eski_rapor"));
        Assert.True(GuvenlikSorgulari.TehlikeliRol("sysadmin")); Assert.True(GuvenlikSorgulari.TehlikeliRol("db_owner")); Assert.False(GuvenlikSorgulari.TehlikeliRol("dbcreator"));
    }

    [Fact]
    public void Login_scripti_parola_yazmaz_roller_ve_eslemeler()
    {
        var l = new Login("rapor_okuyucu", "SQL", true, false, false, true, Z, "Satis", ["public", "dbcreator"], 0, Z, "0x01");
        string s = GuvenlikSorgulari.LoginScripti(l, [new("Satis", "rapor_okuyucu", ["db_datareader", "public"])], Z, "Local");
        Assert.Contains("CREATE LOGIN [rapor_okuyucu] WITH PASSWORD = N'<parola>', DEFAULT_DATABASE = [Satis], CHECK_POLICY = ON;", s);
        Assert.Contains("ALTER LOGIN [rapor_okuyucu] DISABLE;", s);
        Assert.Contains("ALTER SERVER ROLE [dbcreator] ADD MEMBER [rapor_okuyucu];", s);
        Assert.DoesNotContain("[public]", s);
        Assert.Contains("USE [Satis];\nCREATE USER [rapor_okuyucu] FOR LOGIN [rapor_okuyucu];\nALTER ROLE [db_datareader] ADD MEMBER [rapor_okuyucu];", s);
        string w = GuvenlikSorgulari.LoginScripti(l with { Tur = "Windows", Kapali = false }, [], Z, "Local");
        Assert.Contains("CREATE LOGIN [rapor_okuyucu] FROM WINDOWS WITH DEFAULT_DATABASE = [Satis];", w);
        Assert.DoesNotContain("PASSWORD", w);
    }

    [Fact]
    public void Kullanici_scripti_loginli_loginsiz_grant_deny()
    {
        var k = new Kullanici("u", "SQL", "INSTANCE", "l", false, ["db_datareader"], "dbo", "0x", null);
        Yetki[] y = [new("OBJECT_OR_COLUMN", "dbo.T", "UPDATE", "GRANT", "dbo"), new("OBJECT_OR_COLUMN", "dbo.T (Ad)", "SELECT", "DENY", "dbo"),
                     new("SCHEMA", "satis", "EXECUTE", "GRANT_WITH_GRANT_OPTION", "dbo"), new("DATABASE", "Satis", "CONNECT", "GRANT", "dbo")];
        string s = GuvenlikSorgulari.KullaniciScripti(k, "Satis", y, Z, "Local");
        Assert.StartsWith("USE [Satis];\nCREATE USER [u] FOR LOGIN [l] WITH DEFAULT_SCHEMA = [dbo];\nALTER ROLE [db_datareader] ADD MEMBER [u];\n", s);
        Assert.Contains("GRANT UPDATE ON OBJECT::[dbo].[T] TO [u];", s);
        Assert.Contains("DENY SELECT ON OBJECT::[dbo].[T] (Ad) TO [u];", s);
        Assert.Contains("GRANT EXECUTE ON SCHEMA::[satis] TO [u] WITH GRANT OPTION;", s);
        Assert.Contains("GRANT CONNECT TO [u];", s);
        Assert.Contains("CREATE USER [u] WITHOUT LOGIN", GuvenlikSorgulari.KullaniciScripti(k with { BagliLogin = null }, "Satis", [], Z, "L"));
    }

    [Fact]
    public void Esleme_sqli_db_basina_union()
    {
        string s = GuvenlikSorgulari.EslemeSql(["Satis", "Mer]sis"]);
        Assert.Contains("FROM [Satis].sys.database_principals", s);
        Assert.Contains("FROM [Mer]]sis].sys.database_principals", s);
        Assert.Contains("UNION ALL", s);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(s, "UNION ALL"));
        Assert.Contains("TOP (0)", GuvenlikSorgulari.EslemeSql([]));
        Assert.Contains("WHERE pr.name = N'ra''por'", GuvenlikSorgulari.SunucuYetkileriSql("ra'por"));
        Assert.Contains("WHERE pr.name = N'u'", GuvenlikSorgulari.DbYetkileriSql("u"));
    }

    [Fact]
    public void Ayristirma_ozet_login_oturum_kullanici_yetki_esleme()
    {
        GuvenlikOzet o = GuvenlikSorgulari.OzetOku(new QueryResult { ResultSetler = [Rs(["yalniz_windows", "surum", "edition", "engine_edition", "tumunu_gorur", "login_degistirir", "sysadmin", "kendi_login", "veritabanlari"],
            [0, "16.0.1", "Developer", 3, 1, 1, 1, @"DESKTOP\lstuser", "Satis|Mersis"])] })!;
        Assert.True(o.KarmaKimlik); Assert.Equal(["Satis", "Mersis"], o.Veritabanlari); Assert.False(o.Azure);

        var oturum = GuvenlikSorgulari.OturumlariOku(new QueryResult { ResultSetler = [Rs(["login_name", "oturum"], ["rapor_okuyucu", 2])] });
        IReadOnlyList<Login> l = GuvenlikSorgulari.LoginleriOku(new QueryResult { ResultSetler = [Rs(["name", "type", "is_disabled", "default_database_name", "create_date", "sid", "politika", "kilitli", "suresi_dolmus", "parola_degisti", "roller"],
            ["rapor_okuyucu", "S ", false, "Satis", Z, "0x0A", true, 0, 0, Z, "public"],
            ["sa", "S", true, "master", Z, "0x01", true, 0, 0, Z, "public|sysadmin"],
            [@"LST\Ekip", "G", false, "master", Z, "0x0B", false, 0, 0, DBNull.Value, DBNull.Value],
            ["entegrasyon", "S", false, "Depo", Z, "0x0C", true, 1, 0, Z, DBNull.Value])] }, oturum);
        Assert.Equal("SQL", l[0].Tur); Assert.Equal(2, l[0].OturumSayisi); Assert.True(l[0].PolitikaAcik);
        Assert.True(l[1].Kapali); Assert.Contains("sysadmin", l[1].SunucuRolleri);
        Assert.Equal("Windows grup", l[2].Tur); Assert.Empty(l[2].SunucuRolleri); Assert.Null(l[2].ParolaDegisti); Assert.False(l[2].SqlLogin);
        Assert.True(l[3].Kilitli); Assert.True(l[3].Sorunlu);
        Assert.Empty(GuvenlikSorgulari.OturumlariOku(new QueryResult { Hata = new SqlHata("izin yok", 300, 1, 14) })); // VIEW SERVER STATE yoksa sayılar boş

        IReadOnlyList<Kullanici> k = GuvenlikSorgulari.KullanicilariOku(new QueryResult { ResultSetler = [Rs(["name", "type", "kimlik", "login", "sema", "create_date", "sid", "yetim", "roller"],
            ["eski_rapor", "S", "NONE", DBNull.Value, "dbo", Z, "0xFF", 1, "db_datareader"], ["dbo", "U", "WINDOWS", @"DESKTOP\lstuser", "dbo", Z, "0x01", 0, "db_owner"])] });
        Assert.True(k[0].Yetim); Assert.Null(k[0].BagliLogin); Assert.Equal(["db_datareader"], k[0].Roller);
        Assert.False(k[1].Yetim); Assert.Equal("Windows", k[1].Tur);

        IReadOnlyList<Yetki> y = GuvenlikSorgulari.YetkileriOku(new QueryResult { ResultSetler = [Rs(["sinif", "nesne", "izin", "durum", "veren"], ["OBJECT_OR_COLUMN", "dbo.T", "DELETE", "DENY", "dbo"])] });
        Assert.True(y[0].Deny);

        var e = GuvenlikSorgulari.EslemeleriOku(new QueryResult { ResultSetler = [Rs(["veritabani", "kullanici", "sid", "roller"], ["Satis", "rapor_okuyucu", "0x0A", "db_datareader|public"], ["Mersis", "rapor_okuyucu", "0x0A", DBNull.Value], ["Satis", "eski_rapor", "0xFF", ""])] });
        Assert.Equal(2, e["0x0A"].Count); Assert.Equal(["db_datareader", "public"], e["0x0A"][0].Roller); Assert.Empty(e["0x0A"][1].Roller);
        Assert.Single(e["0xFF"]);
        Assert.Equal(["x"], GuvenlikSorgulari.AdlariOku(new QueryResult { ResultSetler = [Rs(["name"], ["x"])] }));
    }

    [Fact]
    public void Hata_metinleri()
    {
        Assert.Contains("açık oturumu", GuvenlikSorgulari.HataMetni(new SqlHata("x", 15434, 1, 16)));
        Assert.Contains("zaten var", GuvenlikSorgulari.HataMetni(new SqlHata("x", 15023, 1, 16)));
        Assert.Contains("izniniz yok", GuvenlikSorgulari.HataMetni(new SqlHata("x", 15247, 1, 16)));
        Assert.Contains("dbo", GuvenlikSorgulari.HataMetni(new SqlHata("x", 15405, 1, 16)));
        Assert.Equal("Azure AD grup", GuvenlikSorgulari.TurAdi("X"));
    }
}
