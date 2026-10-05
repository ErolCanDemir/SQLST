using System.Net.Sockets;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V4-S4 CANLI KANITI: çözücünün ürettiği profil GERÇEKTEN bağlanıyor mu?
/// Birim testleri yalnız "beklediğim metni üretti mi" der; burada çözülen dizeyle
/// profil kurulup gerçek PostgreSQL'e sorgu çalıştırılır. Sunucu alanının biçimi
/// (host:port) lehçenin beklentisiyle uyuşmazsa bu test yakalar.
///
/// Ortam-kapılı: 127.0.0.1:5433'te PostgreSQL yoksa ATLANIR.
/// </summary>
public class ConnectionStringCozucuCanliTests
{
    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 5433).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    [Fact]
    public async Task Cozulen_postgres_uri_ile_gercekten_baglanilir()
    {
        if (!Erisilebilir()) return;

        (ConnectionStringCozucu.Cozum? cozum, string? hata) = ConnectionStringCozucu.Coz(
            "postgresql://postgres@127.0.0.1:5433/sqlst_demo");
        Assert.Null(hata);
        Assert.Equal(MotorTuru.Postgres, cozum!.Motor);

        // Formun yaptığının aynısı: çözümden profil kur
        var profil = new ConnectionProfile
        {
            Ad = cozum.OnerilenAd,
            Motor = cozum.Motor,
            Sunucu = cozum.Sunucu,
            Kimlik = cozum.Kimlik,
            KullaniciAdi = cozum.KullaniciAdi ?? "",
            BaglantiTimeoutSn = 10,
        };

        ILehce lehce = new PostgresLehcesi(new DpapiSecretProtector());
        var executor = new SqlExecutor(lehce);
        QueryResult sonuc = await executor.ExecuteAsync(profil, "SELECT 1 AS x;",
            new ExecuteOptions { VeritabaniOverride = "sqlst_demo" }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.Equal(1, Convert.ToInt32(sonuc.ResultSetler[0].Satirlar[0][0]));
    }

    [Fact]
    public async Task Cozulen_anahtar_deger_dizesiyle_de_baglanilir()
    {
        if (!Erisilebilir()) return;

        (ConnectionStringCozucu.Cozum? cozum, _) = ConnectionStringCozucu.Coz(
            "Host=127.0.0.1;Port=5433;Database=sqlst_demo;Username=postgres");
        Assert.Equal(MotorTuru.Postgres, cozum!.Motor);
        Assert.Equal("127.0.0.1:5433", cozum.Sunucu);

        var profil = new ConnectionProfile
        {
            Motor = cozum.Motor,
            Sunucu = cozum.Sunucu,
            Kimlik = cozum.Kimlik,
            KullaniciAdi = cozum.KullaniciAdi ?? "",
            BaglantiTimeoutSn = 10,
        };

        var executor = new SqlExecutor(new PostgresLehcesi(new DpapiSecretProtector()));
        QueryResult sonuc = await executor.ExecuteAsync(profil, "SELECT current_database();",
            new ExecuteOptions { VeritabaniOverride = "sqlst_demo" }, CancellationToken.None);

        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);
        Assert.Equal("sqlst_demo", sonuc.ResultSetler[0].Satirlar[0][0]);
    }
}

public class ConnectionStringCozucuTests
{
    [Fact]
    public void Windows_kimlikli_dize_cozulur()
    {
        (ConnectionStringCozucu.Cozum? s, string? hata) = ConnectionStringCozucu.Coz(
            @"Server=(localdb)\MSSQLLocalDB;Database=LstQmsDb;Integrated Security=true;Connect Timeout=45");

        Assert.Null(hata);
        Assert.Equal(@"(localdb)\MSSQLLocalDB", s!.Sunucu);
        Assert.Equal(KimlikTuru.Windows, s.Kimlik);
        Assert.Null(s.KullaniciAdi);
        Assert.Equal(45, s.BaglantiTimeoutSn);
        Assert.Contains(s.Notlar, n => n.Contains("LstQmsDb")); // DB profile alınmaz notu (FG-1.1)
        Assert.Contains("LstQmsDb", s.OnerilenAd);
    }

    [Fact]
    public void Sql_kimlikli_dize_kullanici_ve_parolayla_cozulur()
    {
        (ConnectionStringCozucu.Cozum? s, _) = ConnectionStringCozucu.Coz(
            "Data Source=SRV01;User ID=mersis_user;Password=Gz!23;TrustServerCertificate=True");

        Assert.Equal(KimlikTuru.Sql, s!.Kimlik);
        Assert.Equal("mersis_user", s.KullaniciAdi);
        Assert.Equal("Gz!23", s.Parola);
        Assert.Null(s.BaglantiTimeoutSn); // dizede yok — profildeki varsayılan korunur
    }

    [Fact]
    public void Parolasiz_sql_kimliginde_uyari_notu()
    {
        (ConnectionStringCozucu.Cozum? s, _) = ConnectionStringCozucu.Coz("Server=SRV;User Id=sa");
        Assert.Contains(s!.Notlar, n => n.Contains("Parola"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bu bir connection string değil !!;;=")]
    [InlineData("Database=X;Integrated Security=true")] // sunucu yok
    public void Bozuk_dizeler_durust_hata(string cs)
    {
        (ConnectionStringCozucu.Cozum? s, string? hata) = ConnectionStringCozucu.Coz(cs);
        Assert.Null(s);
        Assert.NotNull(hata);
    }

    // ── V4-S4: motor farkındalığı ───────────────────────────────────────────

    [Fact]
    public void Postgres_uri_cozulur_ve_motor_taninir()
    {
        (ConnectionStringCozucu.Cozum? s, string? hata) = ConnectionStringCozucu.Coz(
            "postgresql://raporcu:Gz%2123@db.example.com:5433/sqlst_demo?sslmode=require");

        Assert.Null(hata);
        Assert.Equal(MotorTuru.Postgres, s!.Motor);
        Assert.Equal("db.example.com:5433", s.Sunucu);
        Assert.Equal("raporcu", s.KullaniciAdi);
        Assert.Equal("Gz!23", s.Parola);                 // yüzde-kaçış çözülür
        Assert.Contains(s.Notlar, n => n.Contains("sqlst_demo"));
    }

    [Fact]
    public void Postgres_anahtar_deger_cozulur()
    {
        (ConnectionStringCozucu.Cozum? s, _) = ConnectionStringCozucu.Coz(
            "Host=pg01;Port=5433;Database=demo;Username=postgres;Password=sifre");

        Assert.Equal(MotorTuru.Postgres, s!.Motor);       // "Host" anahtarı PG'ye özgü
        Assert.Equal("pg01:5433", s.Sunucu);
        Assert.Equal("postgres", s.KullaniciAdi);
        Assert.Equal("sifre", s.Parola);
    }

    [Fact]
    public void MySql_anahtar_deger_ve_uri_cozulur()
    {
        (ConnectionStringCozucu.Cozum? a, _) = ConnectionStringCozucu.Coz(
            "Server=maria01;Port=3307;Database=demo;Uid=root;Pwd=gizli;AllowPublicKeyRetrieval=True");
        Assert.Equal(MotorTuru.MySql, a!.Motor);
        Assert.Equal("maria01:3307", a.Sunucu);
        Assert.Equal("root", a.KullaniciAdi);

        (ConnectionStringCozucu.Cozum? b, _) = ConnectionStringCozucu.Coz(
            "mysql://root:gizli@127.0.0.1:3306/demo");
        Assert.Equal(MotorTuru.MySql, b!.Motor);
        Assert.Equal("127.0.0.1:3306", b.Sunucu);
    }

    [Fact]
    public void Oracle_easy_connect_ve_tns_taninir()
    {
        // Çıplak Easy Connect: host:port/servis
        (ConnectionStringCozucu.Cozum? a, _) = ConnectionStringCozucu.Coz("srv01:1521/XEPDB1");
        Assert.Equal(MotorTuru.Oracle, a!.Motor);
        Assert.Equal("srv01:1521/XEPDB1", a.Sunucu);

        // Anahtar=değer: Data Source servis yolu taşıyor
        (ConnectionStringCozucu.Cozum? b, _) = ConnectionStringCozucu.Coz(
            "Data Source=srv01:1521/XEPDB1;User Id=hr;Password=hr123");
        Assert.Equal(MotorTuru.Oracle, b!.Motor);
        Assert.Equal("srv01:1521/XEPDB1", b.Sunucu);      // port ayrıca eklenmez
        Assert.Equal("hr", b.KullaniciAdi);

        // TNS tanımlayıcısı olduğu gibi alınır
        (ConnectionStringCozucu.Cozum? c, _) = ConnectionStringCozucu.Coz(
            "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=srv01)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=XEPDB1)))");
        Assert.Equal(MotorTuru.Oracle, c!.Motor);
        Assert.StartsWith("(DESCRIPTION=", c.Sunucu);
    }

    [Fact]
    public void Mongo_uri_ve_srv_cozulur()
    {
        (ConnectionStringCozucu.Cozum? a, _) = ConnectionStringCozucu.Coz(
            "mongodb://uygulama:parola@localhost:27017/demo");
        Assert.Equal(MotorTuru.Mongo, a!.Motor);
        Assert.Equal("localhost:27017", a.Sunucu);
        Assert.Equal("uygulama", a.KullaniciAdi);
        Assert.Equal("parola", a.Parola);

        // +srv'de port YOKTUR (SRV kaydı çözer) — eklenirse bağlantı bozulur
        (ConnectionStringCozucu.Cozum? b, _) = ConnectionStringCozucu.Coz(
            "mongodb+srv://uygulama:parola@cluster0.abcd.mongodb.net/demo");
        Assert.Equal(MotorTuru.Mongo, b!.Motor);
        Assert.Equal("mongodb+srv://cluster0.abcd.mongodb.net", b.Sunucu);
        Assert.DoesNotContain(":27017", b.Sunucu);
    }

    [Fact]
    public void Parola_asla_sunucu_alanina_gomulmez()
    {
        // GÜVENLİK: URI'deki parola Sunucu'ya kalsaydı profiles.json'a DÜZ METİN yazılırdı;
        // parola alana ayrıştırılır ve kaydetmede DPAPI'den geçer.
        foreach (string cs in new[]
        {
            "postgresql://kullanici:CokGizli123@pg01:5432/demo",
            "mysql://kullanici:CokGizli123@my01:3306/demo",
            "mongodb://kullanici:CokGizli123@mongo01:27017/demo",
            "mongodb+srv://kullanici:CokGizli123@cluster0.abcd.mongodb.net/demo",
        })
        {
            (ConnectionStringCozucu.Cozum? s, _) = ConnectionStringCozucu.Coz(cs);
            Assert.Equal("CokGizli123", s!.Parola);
            Assert.DoesNotContain("CokGizli123", s.Sunucu);
            Assert.DoesNotContain("kullanici:", s.Sunucu);
        }
    }

    [Fact]
    public void Ayirt_edilemeyen_dizede_formdaki_motor_varsayilir_ve_soylenir()
    {
        // "Server=...;Uid=...;Pwd=..." SQL Server ile MySQL arasında GERÇEKTEN ayırt edilemez.
        // Sessizce yanlış motor seçmek yerine formdaki seçim kullanılır ve not düşülür.
        const string ortak = "Server=srv01;Database=demo;Uid=kullanici;Pwd=sifre";

        (ConnectionStringCozucu.Cozum? my, _) = ConnectionStringCozucu.Coz(ortak, MotorTuru.MySql);
        Assert.Equal(MotorTuru.MySql, my!.Motor);
        Assert.Contains(my.Notlar, n => n.Contains("varsayıldı"));

        (ConnectionStringCozucu.Cozum? ms, _) = ConnectionStringCozucu.Coz(ortak, MotorTuru.Mssql);
        Assert.Equal(MotorTuru.Mssql, ms!.Motor);
    }

    [Fact]
    public void Kesin_isaretli_dizede_formdaki_motor_ezilir()
    {
        // Integrated Security yalnız SQL Server'da vardır → form Postgres dese bile MSSQL kazanır
        (ConnectionStringCozucu.Cozum? s, _) = ConnectionStringCozucu.Coz(
            @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true", MotorTuru.Postgres);

        Assert.Equal(MotorTuru.Mssql, s!.Motor);
        Assert.Equal(KimlikTuru.Windows, s.Kimlik);
    }

    [Fact]
    public void Portsuz_uri_sunucuya_port_eklemez()
    {
        (ConnectionStringCozucu.Cozum? s, _) = ConnectionStringCozucu.Coz("postgresql://pg01/demo");
        Assert.Equal("pg01", s!.Sunucu);
    }
}
