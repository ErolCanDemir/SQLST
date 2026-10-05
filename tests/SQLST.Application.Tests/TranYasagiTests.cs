using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// v20-S21 canlı bulgu (2026-08-14) — Güvenli Yazma AÇIKKEN <c>ALTER DATABASE … SET AUTO_CLOSE OFF</c>
/// çalıştırılamıyordu: BEGIN TRAN sarmalayınca SQL Server 226 ("işlem içinde yasak") ile reddediyor.
/// <see cref="QueryService.TranIcindeYasakMi"/> bu ifadeleri yakalar; Güvenli Yazma onlarda devre
/// dışı kalır ve ifade doğrudan koşar.
/// </summary>
public class TranYasagiTests
{
    [Theory]
    [InlineData("ALTER DATABASE [KdsMetadata] SET AUTO_CLOSE OFF;")] // kullanıcının birebir ifadesi
    [InlineData("alter database X set recovery simple")]              // küçük harf
    [InlineData("CREATE DATABASE Yeni")]
    [InlineData("DROP DATABASE Eski")]
    [InlineData("BACKUP DATABASE X TO DISK = 'c:\\y.bak'")]
    [InlineData("RESTORE DATABASE X FROM DISK = 'c:\\y.bak'")]
    [InlineData("RECONFIGURE")]
    [InlineData("-- açıklama satırı\nALTER DATABASE X SET AUTO_CLOSE OFF")] // yorumdan sonra
    [InlineData("UPDATE T SET A = 1;\nALTER DATABASE X SET AUTO_CLOSE OFF")] // batch ortasında
    public void Islem_icinde_yasak_ifadeler_yakalanir(string sql)
        => Assert.True(QueryService.TranIcindeYasakMi(sql));

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("UPDATE T SET A = 1 WHERE Id = 3")]      // sıradan DML Güvenli Yazma'da KALIR
    [InlineData("ALTER TABLE T ADD X int")]              // tablo DDL'i işlemde geçerlidir
    [InlineData("CREATE TABLE T (Id int)")]
    [InlineData("DROP TABLE T")]
    [InlineData("-- ALTER DATABASE yorumda, kod değil\nSELECT 2")]
    public void Siradan_ifadeler_yasak_sayilmaz(string sql)
        => Assert.False(QueryService.TranIcindeYasakMi(sql));
}
