using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>v10-S1 denetim çekirdeği: işlem türü sınıflandırma ("ne iş yapılmış").</summary>
public class IslemSiniflayiciTests
{
    [Theory]
    [InlineData("SELECT * FROM t", IslemTuru.Select)]
    [InlineData("  select 1", IslemTuru.Select)]
    [InlineData("INSERT INTO t VALUES (1)", IslemTuru.Insert)]
    [InlineData("UPDATE t SET x = 1 WHERE id = 2", IslemTuru.Update)]
    [InlineData("DELETE FROM t WHERE id = 1", IslemTuru.Delete)]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET x = 1", IslemTuru.Update)]
    [InlineData("CREATE TABLE t (id int)", IslemTuru.Ddl)]
    [InlineData("ALTER TABLE t ADD x int", IslemTuru.Ddl)]
    [InlineData("DROP TABLE t", IslemTuru.Ddl)]
    [InlineData("TRUNCATE TABLE t", IslemTuru.Ddl)]
    [InlineData("EXEC sp_who", IslemTuru.Exec)]
    [InlineData("EXECUTE dbo.p 1, 2", IslemTuru.Exec)]
    [InlineData("PRINT 'merhaba'", IslemTuru.Diger)]
    public void Temel_islem_turleri(string sql, IslemTuru beklenen)
        => Assert.Equal(beklenen, IslemSiniflayici.Sinifla(sql));

    [Fact]
    public void Yorum_atlanir_ilk_gercek_anahtar_bulunur()
    {
        Assert.Equal(IslemTuru.Update, IslemSiniflayici.Sinifla("-- düzeltme\nUPDATE t SET x = 1"));
        Assert.Equal(IslemTuru.Select, IslemSiniflayici.Sinifla("/* rapor */ SELECT 1"));
    }

    [Fact]
    public void String_icindeki_anahtar_yanlis_siniflamaz()
    {
        // 'DELETE FROM x' bir string literalidir → işlem SELECT'tir
        Assert.Equal(IslemTuru.Select, IslemSiniflayici.Sinifla("SELECT 'DELETE FROM x' AS not_"));
    }

    [Fact]
    public void CTE_gizli_DML_dogru_yakalanir() // asıl kritik durum
    {
        // WITH içindeki SELECT depth-1; asıl işlem depth-0 INSERT
        Assert.Equal(IslemTuru.Insert,
            IslemSiniflayici.Sinifla("WITH a AS (SELECT 1 x) INSERT INTO t (x) SELECT x FROM a"));
        // CTE + SELECT → Select
        Assert.Equal(IslemTuru.Select,
            IslemSiniflayici.Sinifla("WITH a AS (SELECT 1 x) SELECT * FROM a"));
        // WITH + DELETE
        Assert.Equal(IslemTuru.Delete,
            IslemSiniflayici.Sinifla("WITH a AS (SELECT 1 x) DELETE FROM t WHERE x IN (SELECT x FROM a)"));
    }

    [Theory]
    [InlineData("{ \"find\": \"musteri\", \"filter\": {} }", IslemTuru.Select)]
    [InlineData("{ \"aggregate\": \"m\", \"pipeline\": [] }", IslemTuru.Select)]
    [InlineData("{ \"insert\": \"m\", \"documents\": [] }", IslemTuru.Insert)]
    [InlineData("{ \"update\": \"m\", \"updates\": [] }", IslemTuru.Update)]
    [InlineData("{ \"delete\": \"m\", \"deletes\": [] }", IslemTuru.Delete)]
    [InlineData("{ \"createIndexes\": \"m\" }", IslemTuru.Ddl)]
    [InlineData("bozuk json", IslemTuru.Diger)]
    public void Mongo_komut_belgesinden_turetir(string json, IslemTuru beklenen)
        => Assert.Equal(beklenen, IslemSiniflayici.Sinifla(json, MotorTuru.Mongo));

    [Fact]
    public void Bos_metin_diger()
        => Assert.Equal(IslemTuru.Diger, IslemSiniflayici.Sinifla("   "));
}
