using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>Seçili DB dışına yazma kapısı (2026-07-17 kullanıcı isteği): yalnız YAZAN cross-DB yakalanır.</summary>
public class FarkliVeritabaniTests
{
    [Theory]
    [InlineData("CREATE TABLE tempdb.dbo.X (a INT);")]                    // kullanıcının ekran görüntüsü
    [InlineData("INSERT INTO tempdb.dbo.X VALUES (1);")]
    [InlineData("UPDATE tempdb.dbo.X SET a = 1 WHERE a = 0;")]
    [InlineData("DELETE FROM [tempdb].[dbo].[X] WHERE a = 1;")]           // köşeli adlar
    [InlineData("DROP TABLE tempdb.dbo.X;")]
    [InlineData("SELECT 1; INSERT INTO tempdb.dbo.X VALUES (1);")]        // ilk token SELECT olsa da yakalanır
    public void Baska_dbye_yazan_sorgu_yakalanir(string sql)
    {
        IReadOnlyList<string> dbler = SqlCozumleyici.FarkliVeritabaniYazmasi(sql, "LstQmsDb");
        Assert.Equal(["tempdb"], dbler);
    }

    [Fact]
    public void Use_ile_gecis_de_yakalanir()
    {
        IReadOnlyList<string> dbler = SqlCozumleyici.FarkliVeritabaniYazmasi(
            "USE tempdb;\nINSERT INTO dbo.X VALUES (1);", "LstQmsDb");
        Assert.Equal(["tempdb"], dbler);
    }

    [Theory]
    [InlineData("SELECT * FROM tempdb.sys.objects;")]                     // salt-okuma cross-DB serbest
    [InlineData("SELECT a.name FROM master.sys.databases a JOIN msdb.dbo.backupset b ON 1=1;")]
    [InlineData("INSERT INTO dbo.Yerel VALUES (1);")]                     // iki parçalı ad — seçili DB
    [InlineData("INSERT INTO LstQmsDb.dbo.X VALUES (1);")]                // üç parçalı ama AYNI db
    [InlineData("insert into LSTQMSDB.dbo.X values (1);")]                // harf duyarsız
    [InlineData("bozuk sorgu ((")]                                        // çözümlenemeyen → engelleme yok
    public void Mesru_durumlar_yakalanmaz(string sql)
    {
        Assert.Empty(SqlCozumleyici.FarkliVeritabaniYazmasi(sql, "LstQmsDb"));
    }

    [Fact]
    public void Secili_db_yoksa_kapi_calismaz()
    {
        Assert.Empty(SqlCozumleyici.FarkliVeritabaniYazmasi(
            "INSERT INTO tempdb.dbo.X VALUES (1);", null));
    }

    [Fact]
    public void Birden_cok_farkli_db_tekil_listelenir()
    {
        IReadOnlyList<string> dbler = SqlCozumleyici.FarkliVeritabaniYazmasi(
            "INSERT INTO tempdb.dbo.X VALUES (1); UPDATE msdb.dbo.Y SET a = 1 WHERE 1 = 0; DELETE FROM tempdb.dbo.Z WHERE 1 = 0;",
            "LstQmsDb");
        Assert.Equal(2, dbler.Count);
        Assert.Contains("tempdb", dbler);
        Assert.Contains("msdb", dbler);
    }
}
