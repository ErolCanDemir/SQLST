using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V3 sistem denetimi (kullanıcı kuralı 2026-07-18): "motorda çalışmayacak özellikler
/// kapatılmalı" + "tüm tutulan veriler profil bazlı". Buradaki testler kapıların
/// gerçekten kapalı olduğunu ve motora göre doğru davranıldığını kanıtlar.
/// </summary>
public class MotorKapilamaTests
{
    // ── Salt-okunur koruması TÜM motorlarda (eski hal: yalnız MSSQL — veri kaybı riski) ──

    [Theory]
    [InlineData(MotorTuru.Mssql)]
    [InlineData(MotorTuru.Postgres)]
    [InlineData(MotorTuru.MySql)]
    [InlineData(MotorTuru.Oracle)]
    public void Salt_okunur_yazma_sorgusunu_her_sql_motorunda_yakalar(MotorTuru motor)
    {
        Assert.True(QueryService.YazmaSorgusuMu("DELETE FROM musteri", motor));
        Assert.True(QueryService.YazmaSorgusuMu("  UPDATE t SET x = 1", motor));
        Assert.True(QueryService.YazmaSorgusuMu("DROP TABLE t", motor));
        Assert.False(QueryService.YazmaSorgusuMu("SELECT * FROM musteri", motor));
    }

    [Theory]
    [InlineData("""{ "insert": "musteri", "documents": [] }""", true)]
    [InlineData("""{ "delete": "musteri", "deletes": [] }""", true)]
    [InlineData("""{ "drop": "musteri" }""", true)]
    [InlineData("""{ "update": "musteri", "updates": [] }""", true)]
    [InlineData("""{ "find": "musteri", "limit": 10 }""", false)]
    [InlineData("""{ "aggregate": "musteri", "pipeline": [ { "$match": {} } ] }""", false)]
    [InlineData("""{ "aggregate": "musteri", "pipeline": [ { "$out": "yedek" } ] }""", true)]
    public void Salt_okunur_mongo_json_komutunu_dogru_siniflar(string json, bool beklenenYazma)
        => Assert.Equal(beklenenYazma, QueryService.YazmaSorgusuMu(json, MotorTuru.Mongo));

    // ── Filtresiz yazma sigortası her motorda ateşlenir ──────────────────────

    [Theory]
    [InlineData(MotorTuru.Postgres)]
    [InlineData(MotorTuru.MySql)]
    [InlineData(MotorTuru.Oracle)]
    public void Filtresiz_dml_sql_ailesinde_yakalanir(MotorTuru motor)
    {
        Assert.NotEmpty(YazmaSigortasi.WheresizYazmalar("DELETE FROM musteri", motor));
        Assert.NotEmpty(YazmaSigortasi.WheresizYazmalar("UPDATE musteri SET aktif = false", motor));
        Assert.Empty(YazmaSigortasi.WheresizYazmalar("DELETE FROM musteri WHERE id = 1", motor));
        Assert.Empty(YazmaSigortasi.WheresizYazmalar("SELECT * FROM musteri", motor));
    }

    [Fact]
    public void Filtresiz_dml_postgres_ozel_sozdiziminde_de_yakalanir()
    {
        // ScriptDom bunları ayrıştıramazdı → eskiden uyarı HİÇ çıkmıyordu
        Assert.NotEmpty(YazmaSigortasi.WheresizYazmalar(
            "UPDATE musteri SET aktif = false RETURNING *", MotorTuru.Postgres));
        Assert.Empty(YazmaSigortasi.WheresizYazmalar(
            "UPDATE musteri SET aktif = false WHERE id = 1 RETURNING *", MotorTuru.Postgres));
    }

    [Fact]
    public void Filtresiz_mongo_silme_yakalanir()
    {
        // Boş q ({}), koleksiyonun TAMAMINI siler — en tehlikeli sessiz durumdu
        IReadOnlyList<string> bulgu = YazmaSigortasi.WheresizYazmalar(
            """{ "delete": "musteri", "deletes": [ { "q": {}, "limit": 0 } ] }""", MotorTuru.Mongo);
        Assert.Single(bulgu);
        Assert.Contains("musteri", bulgu[0]);

        Assert.Empty(YazmaSigortasi.WheresizYazmalar(
            """{ "delete": "musteri", "deletes": [ { "q": { "_id": 1 }, "limit": 1 } ] }""", MotorTuru.Mongo));
    }

    [Fact]
    public void Filtresiz_mongo_guncelleme_yakalanir()
    {
        Assert.Single(YazmaSigortasi.WheresizYazmalar(
            """{ "update": "musteri", "updates": [ { "q": {}, "u": { "$set": { "aktif": false } }, "multi": true } ] }""",
            MotorTuru.Mongo));
    }

    [Fact]
    public void Yorum_ve_literaller_yanlis_alarm_uretmez()
    {
        Assert.Empty(YazmaSigortasi.WheresizYazmalar(
            "SELECT 'DELETE FROM x' AS metin FROM t", MotorTuru.Postgres));
        Assert.Empty(YazmaSigortasi.WheresizYazmalar(
            "-- DELETE FROM x\nSELECT 1", MotorTuru.Postgres));
    }

    // ── Oracle: sondaki ';' sunucuya gönderilmez (ORA-00933) ────────────────

    [Fact]
    public void Oracle_sondaki_noktali_virgul_kirpilir_plsql_korunur()
    {
        Assert.Equal("SELECT * FROM dual", QueryService.MotoraUyarla("SELECT * FROM dual;", MotorTuru.Oracle));
        Assert.Equal("SELECT * FROM dual", QueryService.MotoraUyarla("SELECT * FROM dual", MotorTuru.Oracle));
        // PL/SQL bloğunun kendi sonlandırıcısı gerekli
        Assert.EndsWith("END;", QueryService.MotoraUyarla("BEGIN NULL; END;", MotorTuru.Oracle));
        // Diğer motorlar etkilenmez
        Assert.Equal("SELECT 1;", QueryService.MotoraUyarla("SELECT 1;", MotorTuru.Postgres));
    }

    // ── Yönetim Paneli: her motor kendi bölümlerini bildirir ────────────────

    [Fact]
    public void Her_sql_motoru_kendi_teshis_bolumlerini_bildirir()
    {
        var protector = new DpapiSecretProtector();
        var saglayici = new LehceSaglayici(protector);

        // MSSQL'in kendi özel paneli var → genel bölüm bildirmez
        Assert.Empty(saglayici.Getir(MotorTuru.Mssql).TeshisBolumleri);

        foreach (MotorTuru motor in new[] { MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Oracle })
        {
            IReadOnlyList<TeshisBolumu> bolumler = saglayici.Getir(motor).TeshisBolumleri;
            Assert.NotEmpty(bolumler);
            Assert.All(bolumler, b =>
            {
                Assert.False(string.IsNullOrWhiteSpace(b.Baslik));
                Assert.False(string.IsNullOrWhiteSpace(b.Aciklama));
                Assert.False(string.IsNullOrWhiteSpace(b.Sorgu));
            });
            // Her motorda index sağlığı bölümü olmalı (kullanıcının asıl istediği)
            Assert.Contains(bolumler, b => b.Baslik.Contains("Index", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Oracle_teshis_sorgularinda_noktali_virgul_olmaz()
    {
        IReadOnlyList<TeshisBolumu> bolumler =
            new OracleLehcesi(new DpapiSecretProtector()).TeshisBolumleri;
        Assert.All(bolumler, b => Assert.DoesNotContain(";", b.Sorgu));
    }

    // ── "İlk N satır" motora göre üretilir (kullanıcı bulgusu: PG'de hata veriyordu) ──

    [Fact]
    public void Ilk_n_satir_sorgusu_her_motorun_kendi_sozdiziminde()
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        var tablo = new SemaNesnesi("db", "satis", "musteri", SemaNesneTuru.Tablo, [], []);

        Assert.Equal("SELECT TOP 200 * FROM [satis].[musteri];",
            saglayici.Getir(MotorTuru.Mssql).IlkNSatirSorgusu(tablo, 200));

        Assert.Equal("SELECT * FROM \"satis\".\"musteri\" LIMIT 200;",
            saglayici.Getir(MotorTuru.Postgres).IlkNSatirSorgusu(tablo, 200));

        Assert.Equal("SELECT * FROM `musteri` LIMIT 200;",   // MySQL'de şema = veritabanı
            saglayici.Getir(MotorTuru.MySql).IlkNSatirSorgusu(tablo, 200));

        string oracle = saglayici.Getir(MotorTuru.Oracle).IlkNSatirSorgusu(tablo, 200);
        Assert.Equal("SELECT * FROM \"satis\".\"musteri\" FETCH FIRST 200 ROWS ONLY", oracle);
        Assert.DoesNotContain(";", oracle);                  // Oracle sondaki ';' kabul etmez

        // Hiçbirinde T-SQL kalıntısı olmamalı
        foreach (MotorTuru m in new[] { MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Oracle })
        {
            string s = saglayici.Getir(m).IlkNSatirSorgusu(tablo, 10);
            Assert.DoesNotContain("TOP", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[", s);
        }
    }

    // ── Ayarlar profil kapsamlı ────────────────────────────────────────────

    [Fact]
    public async Task Ayarlar_profil_bazli_calisir_ve_genele_duser()
    {
        string dosya = Path.Combine(Path.GetTempPath(), $"sqlst-ayar-{Guid.NewGuid():N}.db");
        try
        {
            var depo = new YerelDepo(dosya);
            IAyarDeposu ayar = new SqliteAyarDeposu(depo);
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();

            // GENEL değer: profili olmayan herkes bunu görür
            await ayar.YazAsync(AyarAnahtari.GuvenliYazmaAcik, "1");
            Assert.True(await ayar.BoolOkuAsync(AyarAnahtari.GuvenliYazmaAcik, false, a));
            Assert.True(await ayar.BoolOkuAsync(AyarAnahtari.GuvenliYazmaAcik, false, b));

            // A profili kendi değerini yazar — yalnız A değişir
            await ayar.YazAsync(AyarAnahtari.GuvenliYazmaAcik, "0", a);
            Assert.False(await ayar.BoolOkuAsync(AyarAnahtari.GuvenliYazmaAcik, true, a));
            Assert.True(await ayar.BoolOkuAsync(AyarAnahtari.GuvenliYazmaAcik, false, b));  // B genele düşer
            Assert.True(await ayar.BoolOkuAsync(AyarAnahtari.GuvenliYazmaAcik, false));     // genel bozulmadı
        }
        finally
        {
            // SQLite havuzu dosyayı tutabilir — silmeden önce bağlantıları bırak
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dosya); } catch (IOException) { /* geçici dosya; temizlik kritik değil */ }
        }
    }
}
