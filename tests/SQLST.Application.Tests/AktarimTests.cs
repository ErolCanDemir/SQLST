using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Paket Aktarım S1 (v12, 2026-07-26): SAF kurucular + GERÇEK LocalDB aktarımı
/// (akışlı okuma → parametreli toplu INSERT; önce-temizle; atla-ve-raporla politikası).
/// </summary>
public class AktarimTests
{
    private static string T(string ad) => $"[{ad}]"; // sahte tırnaklayıcı

    [Fact]
    public void Otomatik_esleme_ayni_adlari_harf_duyarsiz_esler()
    {
        IReadOnlyList<SemaKolonu> kaynak =
            [new("Id", "int", false, true), new("AD", "nvarchar", true, false), new("Fazla", "int", true, false)];
        IReadOnlyList<SemaKolonu> hedef =
            [new("id", "bigint", false, true), new("Ad", "varchar", true, false), new("Baska", "int", true, false)];

        IReadOnlyList<AktarimEslesmesi> e = AktarimEslestirici.OtomatikEsle(kaynak, hedef);

        Assert.Equal(2, e.Count);
        Assert.Equal(new AktarimEslesmesi("Id", "id"), e[0]);   // hedefin gerçek adı kullanılır
        Assert.Equal(new AktarimEslesmesi("AD", "Ad"), e[1]);
    }

    [Fact]
    public void Select_ve_insert_kuruculari()
    {
        IReadOnlyList<AktarimEslesmesi> e = [new("Id", "Id"), new("Ad", "AdSoyad")];

        // Tablo adı artık PARÇA PARÇA tırnaklı (inceleme 2026-07-30: ham ad PG/Oracle'da
        // büyük-küçük katlanıp yanlış tabloya gidebiliyordu).
        Assert.Equal("SELECT [Id], [Ad] FROM [dbo].[Kaynak]",
            AktarimEslestirici.KaynakSelect(T, "dbo.Kaynak", e));
        Assert.Equal("INSERT INTO [dbo].[Hedef] ([Id], [AdSoyad]) VALUES (@p0, @p1)",
            AktarimEslestirici.HedefInsert(T, "dbo.Hedef", e, "@"));
        Assert.Equal("INSERT INTO [Hedef] ([Id], [AdSoyad]) VALUES (:p0, :p1)",
            AktarimEslestirici.HedefInsert(T, "Hedef", e, AktarimEslestirici.ParametreOneki("oracle")));
    }

    [Fact]
    public void Update_kurucusu_set_where_ayrimi_ve_gecersiz_durumlar()
    {
        // v12-S4: p{i} İNDEKSİ eşleşme sırası — INSERT'le aynı satır değeri aynı ada gider.
        IReadOnlyList<AktarimEslesmesi> e = [new("Id", "Id"), new("Ad", "AdSoyad"), new("P", "Puan")];

        Assert.Equal("UPDATE [dbo].[Hedef] SET [AdSoyad] = @p1, [Puan] = @p2 WHERE [Id] = @p0",
            AktarimEslestirici.HedefUpdate(T, "dbo.Hedef", e, ["Id"], "@"));
        Assert.Equal("UPDATE [H] SET [Puan] = :p2 WHERE [Id] = :p0 AND [AdSoyad] = :p1",
            AktarimEslestirici.HedefUpdate(T, "H", e, ["id", "adsoyad"], ":")); // harf duyarsız
        Assert.Null(AktarimEslestirici.HedefUpdate(T, "H", e, [], "@"));                    // anahtar yok
        Assert.Null(AktarimEslestirici.HedefUpdate(T, "H", e, ["Id", "AdSoyad", "Puan"], "@")); // SET boş
    }

    [Fact]
    public async Task Sorgu_kolon_kesfi_satir_cekmeden_ad_ve_tip_verir()
    {
        // v12-S3: SchemaOnly — sorgu ÇALIŞTIRILMADAN sonuç kolonları keşfedilir.
        var servis = new AktarimServisi(new LehceSaglayici(new DpapiSecretProtector()));

        IReadOnlyList<SemaKolonu> kolonlar = await servis.KaynakKolonlariAsync(
            Profil(), "tempdb",
            "SELECT CAST(1 AS int) AS Numara, CAST(N'x' AS nvarchar(20)) AS Etiket",
            CancellationToken.None);

        Assert.Equal(2, kolonlar.Count);
        Assert.Equal("Numara", kolonlar[0].Ad);
        Assert.Equal("int", kolonlar[0].Tip);
        Assert.Equal("Etiket", kolonlar[1].Ad);
        Assert.Contains("nvarchar", kolonlar[1].Tip);
    }

    // ---- Canlı LocalDB (MSSQL→MSSQL, aynı sunucu iki tablo) ----

    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    private static SqlExecutor Yeni() => new(new DpapiSecretProtector());

    /// <summary>
    /// v22-S16 (kullanıcı: "hataları loglayalım"): hata örneği tavanı 20 → 10.000'e çıktı — 25
    /// hatalı satırın HEPSİNİN ayrıntısı toplanmalı. Eski dünyada 21. satırdan itibaren mesajlar
    /// sessizce düşerdi; "⬇ Hata raporunu kaydet" dosyası bu listeden beslenir.
    /// </summary>
    [Fact]
    public async Task Yirmiden_fazla_hatanin_ayrintisi_kaybolmaz()
    {
        var executor = Yeni();
        var servis = new AktarimServisi(new LehceSaglayici(new DpapiSecretProtector()));

        const string kur = """
            IF OBJECT_ID('tempdb.dbo.SqlstAkCokHataK') IS NOT NULL DROP TABLE tempdb.dbo.SqlstAkCokHataK;
            IF OBJECT_ID('tempdb.dbo.SqlstAkCokHataH') IS NOT NULL DROP TABLE tempdb.dbo.SqlstAkCokHataH;
            CREATE TABLE tempdb.dbo.SqlstAkCokHataK (Id int, Ad nvarchar(50));
            CREATE TABLE tempdb.dbo.SqlstAkCokHataH (Id int, Ad nvarchar(8));
            INSERT INTO tempdb.dbo.SqlstAkCokHataK
            SELECT n, CASE WHEN n <= 25 THEN N'bu ad hedefe kesinlikle sigmayacak kadar uzun'
                           ELSE N'kisa' END
            FROM (SELECT TOP (30) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n
                  FROM sys.objects a CROSS JOIN sys.objects b) t;
            """;
        QueryResult hazir = await executor.ExecuteAsync(
            Profil(), kur, new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        Assert.True(hazir.Basarili, hazir.Hata?.Mesaj);

        try
        {
            IReadOnlyList<AktarimEslesmesi> eslesmeler = [new("Id", "Id"), new("Ad", "Ad")];
            var istek = new AktarimIstegi(
                Profil(), "tempdb",
                AktarimEslestirici.KaynakSelect(a => $"[{a}]", "tempdb.dbo.SqlstAkCokHataK", eslesmeler),
                Profil(), "tempdb", "tempdb.dbo.SqlstAkCokHataH",
                eslesmeler, OnceTemizle: false, AktarimHataPolitikasi.AtlaVeRaporla);

            AktarimSonucu sonuc = await servis.AktarAsync(istek, null, CancellationToken.None);

            Assert.True(sonuc.Basarili, sonuc.Hata);
            Assert.Equal(25, sonuc.Atlanan);
            Assert.Equal(25, sonuc.HataOrnekleri.Count); // eski 20 tavanı: 21-25 sessizce kaybolurdu
            Assert.Equal(5, sonuc.Yazilan);
        }
        finally
        {
            await executor.ExecuteAsync(Profil(),
                "DROP TABLE tempdb.dbo.SqlstAkCokHataK; DROP TABLE tempdb.dbo.SqlstAkCokHataH;",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Aktarim_uctan_uca_temizle_ve_atla_politikasiyla()
    {
        var executor = Yeni();
        var lehceler = new LehceSaglayici(new DpapiSecretProtector());
        var servis = new AktarimServisi(lehceler);

        const string kur = """
            IF OBJECT_ID('tempdb.dbo.SqlstAkKaynak') IS NOT NULL DROP TABLE tempdb.dbo.SqlstAkKaynak;
            IF OBJECT_ID('tempdb.dbo.SqlstAkHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstAkHedef;
            CREATE TABLE tempdb.dbo.SqlstAkKaynak (Id int, Ad nvarchar(50), Puan int NULL);
            CREATE TABLE tempdb.dbo.SqlstAkHedef  (Id int, Ad nvarchar(8), Eski nvarchar(10) NULL);
            INSERT INTO tempdb.dbo.SqlstAkKaynak VALUES
              (1, N'Ali', 10), (2, N'Ayşe', NULL), (3, N'Bu ad kesinlikle çok uzun', 30),
              (4, N'Can', 40), (5, N'Ece', 50);
            INSERT INTO tempdb.dbo.SqlstAkHedef VALUES (99, N'silinsin', N'x');
            """;
        QueryResult hazir = await executor.ExecuteAsync(
            Profil(), kur, new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        Assert.True(hazir.Basarili, hazir.Hata?.Mesaj);

        try
        {
            IReadOnlyList<AktarimEslesmesi> eslesmeler = [new("Id", "Id"), new("Ad", "Ad")];
            var ilerlemeler = new List<AktarimIlerleme>();

            // Önce temizle + AtlaVeRaporla: uzun ad (satır 3) hedefin nvarchar(8)'ine sığmaz → atlanır.
            var istek = new AktarimIstegi(
                Profil(), "tempdb",
                AktarimEslestirici.KaynakSelect(a => $"[{a}]", "tempdb.dbo.SqlstAkKaynak", eslesmeler),
                Profil(), "tempdb", "tempdb.dbo.SqlstAkHedef",
                eslesmeler, OnceTemizle: true, AktarimHataPolitikasi.AtlaVeRaporla);

            AktarimSonucu sonuc = await servis.AktarAsync(
                istek, new Progress<AktarimIlerleme>(ilerlemeler.Add), CancellationToken.None);

            Assert.True(sonuc.Basarili, sonuc.Hata);
            Assert.Equal(4, sonuc.Yazilan);
            Assert.Equal(1, sonuc.Atlanan);
            Assert.Contains("Satır 3", Assert.Single(sonuc.HataOrnekleri));

            // Hedefi doğrula: eski satır silindi, NULL Puan sorunsuz, 4 satır var.
            QueryResult kontrol = await executor.ExecuteAsync(Profil(),
                "SELECT COUNT(*), SUM(CASE WHEN Ad = N'silinsin' THEN 1 ELSE 0 END) FROM tempdb.dbo.SqlstAkHedef",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
            Assert.Equal(4, Convert.ToInt32(kontrol.ResultSetler[0].Satirlar[0][0]));
            Assert.Equal(0, Convert.ToInt32(kontrol.ResultSetler[0].Satirlar[0][1]));

            // IlkHatadaDur: temizlemeden tekrar koş → satır 3'te durur; 1-2 zaten yazılmıştı (parti içinde
            // geri alınır → yazılan 0 raporlanır; commit'lenen yok çünkü parti 500).
            AktarimSonucu dur = await servis.AktarAsync(istek with
            {
                OnceTemizle = false,
                HataPolitikasi = AktarimHataPolitikasi.IlkHatadaDur,
            }, null, CancellationToken.None);
            Assert.False(dur.Basarili);
            Assert.Contains("Satır 3", dur.Hata!);
            Assert.Equal(0, dur.Yazilan); // aktif parti geri alındı — dürüst sayım
        }
        finally
        {
            await executor.ExecuteAsync(Profil(), """
                IF OBJECT_ID('tempdb.dbo.SqlstAkKaynak') IS NOT NULL DROP TABLE tempdb.dbo.SqlstAkKaynak;
                IF OBJECT_ID('tempdb.dbo.SqlstAkHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstAkHedef;
                """, new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Upsert_var_olani_gunceller_olmayani_ekler()
    {
        // v12-S4: EkleGuncelle — Id 1-2 hedefte VAR (güncellenir), 3 yok (eklenir).
        var executor = Yeni();
        var servis = new AktarimServisi(new LehceSaglayici(new DpapiSecretProtector()));

        const string kur = """
            IF OBJECT_ID('tempdb.dbo.SqlstUpKaynak') IS NOT NULL DROP TABLE tempdb.dbo.SqlstUpKaynak;
            IF OBJECT_ID('tempdb.dbo.SqlstUpHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstUpHedef;
            CREATE TABLE tempdb.dbo.SqlstUpKaynak (Id int, Ad nvarchar(50));
            CREATE TABLE tempdb.dbo.SqlstUpHedef  (Id int PRIMARY KEY, Ad nvarchar(50));
            INSERT INTO tempdb.dbo.SqlstUpKaynak VALUES (1, N'Ali'), (2, N'Ayşe'), (3, N'Can');
            INSERT INTO tempdb.dbo.SqlstUpHedef  VALUES (1, N'eski1'), (2, N'eski2');
            """;
        QueryResult hazir = await executor.ExecuteAsync(
            Profil(), kur, new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        Assert.True(hazir.Basarili, hazir.Hata?.Mesaj);

        try
        {
            IReadOnlyList<AktarimEslesmesi> eslesmeler = [new("Id", "Id"), new("Ad", "Ad")];
            var istek = new AktarimIstegi(
                Profil(), "tempdb",
                AktarimEslestirici.KaynakSelect(a => $"[{a}]", "tempdb.dbo.SqlstUpKaynak", eslesmeler),
                Profil(), "tempdb", "tempdb.dbo.SqlstUpHedef",
                eslesmeler, YazmaKipi: AktarimYazmaKipi.EkleGuncelle, AnahtarKolonlar: ["Id"]);

            AktarimSonucu sonuc = await servis.AktarAsync(istek, null, CancellationToken.None);

            Assert.True(sonuc.Basarili, sonuc.Hata);
            Assert.Equal(3, sonuc.Okunan);
            Assert.Equal(1, sonuc.Yazilan);      // Id 3 eklendi
            Assert.Equal(2, sonuc.Guncellenen);  // Id 1-2 güncellendi

            QueryResult kontrol = await executor.ExecuteAsync(Profil(),
                "SELECT COUNT(*), MIN(CASE WHEN Id = 1 THEN Ad END) FROM tempdb.dbo.SqlstUpHedef",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
            Assert.Equal(3, Convert.ToInt32(kontrol.ResultSetler[0].Satirlar[0][0]));
            Assert.Equal("Ali", kontrol.ResultSetler[0].Satirlar[0][1]); // eski1 → Ali

            // Anahtar dışı kolon kalmayınca kip kurulamaz — motor açık hatayla reddeder.
            AktarimSonucu red = await servis.AktarAsync(istek with
            {
                Eslesmeler = [new("Id", "Id")],
            }, null, CancellationToken.None);
            Assert.False(red.Basarili);
            Assert.Contains("anahtar dışında", red.Hata!);
        }
        finally
        {
            await executor.ExecuteAsync(Profil(), """
                IF OBJECT_ID('tempdb.dbo.SqlstUpKaynak') IS NOT NULL DROP TABLE tempdb.dbo.SqlstUpKaynak;
                IF OBJECT_ID('tempdb.dbo.SqlstUpHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstUpHedef;
                """, new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        }
    }
}
