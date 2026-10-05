using System.Globalization;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v7 veri karşılaştırmanın CANLI kanıtı — LocalDB (kullanıcı isteği 2026-07-21: "bize benzer bir DB
/// kur ve test et"). İki gerçek veritabanı kurulur (birinde satır DEĞİŞİK, biri YOK, biri EKSTRA),
/// gerçek <see cref="SchemaService"/> PK'yı okur, gerçek <see cref="ISqlExecutor"/> motor hash sorgusunu
/// (<c>BINARY_CHECKSUM</c>) iki tarafta çalıştırır, <see cref="VeriFarkHesabi"/> farkı bulur. Sahte
/// executor "mantık doğru" der; bu test "gerçekten çalışıyor" der (nvarchar(max)/decimal/datetime dahil).
/// </summary>
public sealed class VeriKarsilastirmaCanliTests : IDisposable
{
    private readonly string _kaynak = $"sqlst_cmp_k_{Guid.NewGuid():N}";
    private readonly string _hedef = $"sqlst_cmp_h_{Guid.NewGuid():N}";
    private static readonly SqlExecutor Executor = new(new DpapiSecretProtector());

    private static ConnectionProfile Localdb() => new()
    {
        Ad = "localdb", Motor = MotorTuru.Mssql, Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    public VeriKarsilastirmaCanliTests()
    {
        Calistir($"CREATE DATABASE [{_kaynak}]", null);
        Calistir($"CREATE DATABASE [{_hedef}]", null);
        const string tablo = """
            CREATE TABLE dbo.Musteri (
                Id int NOT NULL PRIMARY KEY,
                Ad nvarchar(100) NOT NULL,
                Tutar decimal(18,2) NULL,
                Notlar nvarchar(max) NULL,
                Tarih datetime NULL);
            """;
        Calistir(tablo, _kaynak);
        Calistir(tablo, _hedef);
        Calistir("INSERT INTO dbo.Musteri VALUES (1,'Ali',100.5,'n1','20240101'),(2,'Veli',200,'n2','20240202'),(3,'Ayse',300,NULL,'20240303')", _kaynak);
        Calistir("INSERT INTO dbo.Musteri VALUES (1,'Ali',100.5,'n1','20240101'),(2,'Veli',999.99,'DEGISTI','20240202'),(4,'Fatma',400,'n4','20240404')", _hedef);
    }

    [Fact]
    public async Task Uctan_uca_veri_farki_hash_ile_bulunur()
    {
        var lehceler = new LehceSaglayici(new DpapiSecretProtector());
        ILehce lehce = lehceler.Getir(MotorTuru.Mssql);
        var sema = new SchemaService(Executor, lehceler);

        // Gerçek şemadan tablo + PK oku (VM bunu böyle yapıyor).
        SemaOnbellegi onbellek = await sema.YukleAsync(Localdb(), _kaynak, CancellationToken.None);
        SemaNesnesi t = onbellek.Nesneler.Single(n => n.Ad == "Musteri" && n.Tur == SemaNesneTuru.Tablo);
        IReadOnlyList<string> pk = [.. t.Kolonlar.Where(k => k.PkMi).Select(k => k.Ad)];
        Assert.Equal(["Id"], pk);

        // Motor hash sorgusu (üreteç) + iki tarafta çalıştır → (anahtar, hash) sözlükleri.
        string sql = lehce.SatirHashSorgusu("dbo", "Musteri", pk, [.. t.Kolonlar.Select(k => k.Ad)], null)!;
        Dictionary<string, string> solH = await HashSozlukAsync(sql, _kaynak);
        Dictionary<string, string> sagH = await HashSozlukAsync(sql, _hedef);
        Assert.Equal(3, solH.Count);
        Assert.Equal(3, sagH.Count);

        IReadOnlyList<VeriFarkKaydi> farklar = VeriFarkHesabi.Hesapla(solH, sagH);

        Assert.Equal(VeriFarkTuru.Farkli, farklar.Single(f => f.Anahtar == "2").Tur);    // Tutar/Notlar değişti
        Assert.Equal(VeriFarkTuru.YalnizSol, farklar.Single(f => f.Anahtar == "3").Tur); // hedefte yok
        Assert.Equal(VeriFarkTuru.YalnizSag, farklar.Single(f => f.Anahtar == "4").Tur); // kaynakta yok
        Assert.DoesNotContain(farklar, f => f.Anahtar == "1");                           // eş → fark yok
    }

    private static async Task<Dictionary<string, string>> HashSozlukAsync(string sql, string db)
    {
        QueryResult r = await Executor.ExecuteAsync(Localdb(), sql,
            new ExecuteOptions { VeritabaniOverride = db, SatirSiniri = 1_000_000 }, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (object?[] row in r.ResultSetler[0].Satirlar)
            d[Convert.ToString(row[0], CultureInfo.InvariantCulture)!] = Convert.ToString(row[1], CultureInfo.InvariantCulture)!;
        return d;
    }

    private static void Calistir(string sql, string? db)
    {
        QueryResult r = Executor.ExecuteAsync(Localdb(), sql,
            new ExecuteOptions { VeritabaniOverride = db }, CancellationToken.None).GetAwaiter().GetResult();
        if (!r.Basarili)
            throw new InvalidOperationException(r.Hata?.Mesaj ?? "kurulum sorgusu başarısız");
    }

    public void Dispose()
    {
        foreach (string db in new[] { _kaynak, _hedef })
        {
            try
            {
                Calistir($"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];", null);
            }
            catch (InvalidOperationException) { /* zaten yok/kilitli — test temizliği kritik değil */ }
        }
    }
}
