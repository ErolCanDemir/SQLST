using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v13-S3 — dosyadan tabloya aktarım motoru, GERÇEK LocalDB'ye karşı: TXT string değerlerinin
/// TR KÜLTÜRÜYLE tipe çevrimi ("1.250,75" → 1250.75; "24.07.2026" → tarih; boş → NULL) ·
/// çevrilemeyen değerin atla-politikasına düşmesi · Ekle/Güncelle (UPSERT) kipi.
/// </summary>
public class DosyaAktarimTests
{
    private static ConnectionProfile Profil() => new()
    {
        Ad = "localdb", Sunucu = @"(localdb)\MSSQLLocalDB",
        Kimlik = KimlikTuru.Windows, BaglantiTimeoutSn = 60,
    };

    [Fact]
    public void Tip_haritasi_motor_basina_dogru_sql_tipini_verir()
    {
        // v13-S4: motor farkları TEK haritada (çoklu-motor kuralı) — örnekleme yeter.
        var metin17 = new DosyaKolonu("Sehir", DosyaTipi.Metin, 17, false);
        var dev = new DosyaKolonu("Aciklama", DosyaTipi.Metin, 4500, false);
        var para = new DosyaKolonu("Tutar", DosyaTipi.Ondalik, 8, true);
        var bosKolon = new DosyaKolonu("Bos", DosyaTipi.Metin, 0, true);

        Assert.Equal("nvarchar(50)", DosyaTipiEslemesi.SqlTipi("mssql", metin17));   // 17 → 50'ye yuvarlanır
        Assert.Equal("nvarchar(max)", DosyaTipiEslemesi.SqlTipi("mssql", dev));
        Assert.Equal("decimal(18,4)", DosyaTipiEslemesi.SqlTipi("mssql", para));
        Assert.Equal("nvarchar(100)", DosyaTipiEslemesi.SqlTipi("mssql", bosKolon)); // hep boş → 100
        Assert.Equal("numeric(18,4)", DosyaTipiEslemesi.SqlTipi("postgres", para));
        Assert.Equal("text", DosyaTipiEslemesi.SqlTipi("postgres", dev));
        Assert.Equal("tinyint(1)", DosyaTipiEslemesi.SqlTipi("mysql", new("A", DosyaTipi.Bool, 5, false)));
        Assert.Equal("NCLOB", DosyaTipiEslemesi.SqlTipi("oracle", dev));             // NVARCHAR2 sınırı 2000
        Assert.Equal("DATE", DosyaTipiEslemesi.SqlTipi("oracle", new("T", DosyaTipi.Tarih, 10, false)));
    }

    [Fact]
    public async Task Tr_donusumu_bos_null_atla_politikasi_ve_upsert()
    {
        var executor = new SqlExecutor(new DpapiSecretProtector());
        var servis = new DosyaAktarimServisi(new LehceSaglayici(new DpapiSecretProtector()));

        const string kur = """
            IF OBJECT_ID('tempdb.dbo.SqlstDosyaHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstDosyaHedef;
            CREATE TABLE tempdb.dbo.SqlstDosyaHedef
              (Id int PRIMARY KEY, Ad nvarchar(30), Tutar decimal(10,2) NULL, Kayit datetime2 NULL);
            """;
        QueryResult hazir = await executor.ExecuteAsync(Profil(), kur,
            new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        Assert.True(hazir.Basarili, hazir.Hata?.Mesaj);

        IReadOnlyList<DosyaKolonu> kolonlar =
        [
            new("No", DosyaTipi.TamSayi, 2, false),
            new("AdSoyad", DosyaTipi.Metin, 10, false),
            new("Tutar", DosyaTipi.Ondalik, 8, true),
            new("Tarih", DosyaTipi.Tarih, 10, true),
        ];
        IReadOnlyList<AktarimEslesmesi> eslesmeler =
            [new("No", "Id"), new("AdSoyad", "Ad"), new("Tutar", "Tutar"), new("Tarih", "Kayit")];
        var istek = new DosyaAktarimIstegi(
            Profil(), "tempdb", "tempdb.dbo.SqlstDosyaHedef", eslesmeler, kolonlar, "tr-TR");

        try
        {
            // 1) TR dönüşümü + boş → NULL; "abc" tam sayıya çevrilemez → AtlaVeRaporla'da atlanır.
            List<object?[]> satirlar =
            [
                ["1", "Ali Yılmaz", "1.250,75", "24.07.2026"],
                ["2", "Ayşe", "", ""],                       // boşlar NULL olur
                ["abc", "Bozuk", "1,00", "01.01.2025"],      // Id çevrilemez → satır hatası
                ["3", "Can", "12,50", "15.03.2026"],
            ];
            AktarimSonucu sonuc = await servis.AktarAsync(
                istek with { HataPolitikasi = AktarimHataPolitikasi.AtlaVeRaporla },
                satirlar, null, CancellationToken.None);

            Assert.True(sonuc.Basarili, sonuc.Hata);
            Assert.Equal(4, sonuc.Okunan);
            Assert.Equal(3, sonuc.Yazilan);
            Assert.Equal(1, sonuc.Atlanan);
            Assert.Contains("Satır 3", Assert.Single(sonuc.HataOrnekleri));
            Assert.Contains("tam sayı", sonuc.HataOrnekleri[0]);

            QueryResult kontrol = await executor.ExecuteAsync(Profil(),
                "SELECT COUNT(*), SUM(Tutar), SUM(CASE WHEN Tutar IS NULL THEN 1 ELSE 0 END) "
                + "FROM tempdb.dbo.SqlstDosyaHedef",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
            object?[] satir = kontrol.ResultSetler[0].Satirlar[0];
            Assert.Equal(3, Convert.ToInt32(satir[0]));
            Assert.Equal(1263.25m, Convert.ToDecimal(satir[1])); // 1250,75 + 12,50 — TR parse kanıtı
            Assert.Equal(1, Convert.ToInt32(satir[2]));          // Ayşe'nin Tutar'ı NULL

            // 2) Ekle/Güncelle: Id 1 değişir, Id 9 eklenir.
            AktarimSonucu upsert = await servis.AktarAsync(
                istek with { YazmaKipi = AktarimYazmaKipi.EkleGuncelle, AnahtarKolonlar = ["Id"] },
                [["1", "Ali GÜNCEL", "5,00", ""], ["9", "Yeni", "1,00", ""]],
                null, CancellationToken.None);

            Assert.True(upsert.Basarili, upsert.Hata);
            Assert.Equal(1, upsert.Yazilan);
            Assert.Equal(1, upsert.Guncellenen);

            // 3) IlkHatadaDur: çevrilemeyen değer AÇIK hatayla durdurur (satır+kolon+değer).
            AktarimSonucu dur = await servis.AktarAsync(
                istek, [["x", "Kim", "1,00", ""]], null, CancellationToken.None);
            Assert.False(dur.Basarili);
            Assert.Contains("'x'", dur.Hata!);
            Assert.Contains("No", dur.Hata!);

            // 4) UÇTAN UCA: gerçek TXT dosyası → DosyaOkuyucu.MetinAkis → tabloya (ekranın yolu).
            string dosya = Path.Combine(Path.GetTempPath(), $"sqlst-e2e-{Guid.NewGuid():N}.txt");
            File.WriteAllText(dosya,
                "No;AdSoyad;Tutar;Tarih\r\n50;Uçtan Uca;9.999,99;01.07.2026\r\n",
                new System.Text.UTF8Encoding(false));
            try
            {
                AktarimSonucu e2e = await servis.AktarAsync(istek,
                    DosyaOkuyucu.MetinAkis(dosya, ';', new System.Text.UTF8Encoding(false), ilkSatirBaslik: true),
                    null, CancellationToken.None);
                Assert.True(e2e.Basarili, e2e.Hata);
                Assert.Equal(1, e2e.Yazilan);

                QueryResult e2eKontrol = await executor.ExecuteAsync(Profil(),
                    "SELECT Ad, Tutar FROM tempdb.dbo.SqlstDosyaHedef WHERE Id = 50",
                    new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
                Assert.Equal("Uçtan Uca", e2eKontrol.ResultSetler[0].Satirlar[0][0]);
                Assert.Equal(9999.99m, Convert.ToDecimal(e2eKontrol.ResultSetler[0].Satirlar[0][1]));
            }
            finally
            {
                File.Delete(dosya);
            }

            // 5) YENİ TABLO yolu (v13-S4): OnceDdl motor tarafından aktarımdan önce çalıştırılır.
            var yeniIstek = istek with
            {
                HedefTablo = "tempdb.[dbo].[SqlstDosyaYeni]",
                OnceDdl = "CREATE TABLE tempdb.dbo.SqlstDosyaYeni "
                    + "(Id bigint NULL, Ad nvarchar(50) NULL, Tutar decimal(18,4) NULL, Kayit datetime2 NULL)",
                Eslesmeler = eslesmeler,
            };
            try
            {
                AktarimSonucu yeni = await servis.AktarAsync(yeniIstek,
                    [["7", "Yeni Tablo", "1,25", "05.05.2026"]], null, CancellationToken.None);
                Assert.True(yeni.Basarili, yeni.Hata);
                Assert.Equal(1, yeni.Yazilan);

                QueryResult yeniKontrol = await executor.ExecuteAsync(Profil(),
                    "SELECT Ad FROM tempdb.dbo.SqlstDosyaYeni WHERE Id = 7",
                    new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
                Assert.Equal("Yeni Tablo", yeniKontrol.ResultSetler[0].Satirlar[0][0]);
            }
            finally
            {
                await executor.ExecuteAsync(Profil(),
                    "IF OBJECT_ID('tempdb.dbo.SqlstDosyaYeni') IS NOT NULL DROP TABLE tempdb.dbo.SqlstDosyaYeni;",
                    new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
            }
        }
        finally
        {
            await executor.ExecuteAsync(Profil(),
                "IF OBJECT_ID('tempdb.dbo.SqlstDosyaHedef') IS NOT NULL DROP TABLE tempdb.dbo.SqlstDosyaHedef;",
                new ExecuteOptions { VeritabaniOverride = "tempdb" }, CancellationToken.None);
        }
    }
}
