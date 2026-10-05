using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

public class WheresizDmlTests
{
    [Theory]
    [InlineData("UPDATE dbo.Musteri SET Ad = 'x'")]
    [InlineData("DELETE FROM dbo.Siparis")]
    [InlineData("delete dbo.Siparis")]
    [InlineData("IF 1 = 1\nBEGIN\n    UPDATE t SET x = 1\nEND")] // iç blokta da yakalanır
    public void Wheresiz_update_delete_yakalanir(string sql)
    {
        IReadOnlyList<string> bulgular = SqlCozumleyici.WheresizDmlBul(sql);

        Assert.Single(bulgular);
        Assert.Contains("WHERE yok", bulgular[0]);
        Assert.Contains("Satır", bulgular[0]);
    }

    [Theory]
    [InlineData("UPDATE dbo.Musteri SET Ad = 'x' WHERE Id = 1")]
    [InlineData("DELETE FROM dbo.Siparis WHERE Tarih < '2020-01-01'")]
    [InlineData("UPDATE TOP (10) dbo.Musteri SET Ad = 'x'")] // TOP bilinçli sınır — uyarma
    [InlineData("DELETE TOP (5) FROM dbo.Siparis")]
    [InlineData("SELECT * FROM dbo.Musteri")]
    [InlineData("INSERT INTO dbo.Musteri (Ad) VALUES ('x')")]
    [InlineData("SELECT 'UPDATE t SET x = 1' AS metin")] // string içi DML kod değildir
    [InlineData("UPDATE bozuk syntax hata")]              // çözümlemeyen metni engelleme
    public void Guvenli_metinler_uyari_uretmez(string sql)
    {
        Assert.Empty(SqlCozumleyici.WheresizDmlBul(sql));
    }

    [Fact]
    public void Birden_cok_wheresiz_dml_ayri_ayri_raporlanir()
    {
        IReadOnlyList<string> bulgular = SqlCozumleyici.WheresizDmlBul(
            "UPDATE a SET x = 1;\nDELETE FROM b;\nUPDATE c SET y = 2 WHERE id = 3;");

        Assert.Equal(2, bulgular.Count);
        Assert.Contains("UPDATE a", bulgular[0]);
        Assert.Contains("DELETE b", bulgular[1]);
    }
}

public class SqlAnahtarTests
{
    [Theory]
    [InlineData("UPDATE t SET x = 1", "UPDATE")]
    [InlineData("   \n\t create table t (id int)", "CREATE")]
    [InlineData("-- yorum satırı\nDROP TABLE t", "DROP")]
    [InlineData("/* blok\n   yorum */ ALTER TABLE t ADD x INT", "ALTER")]
    [InlineData("-- bir\n-- iki\n/* üç */ TRUNCATE TABLE t", "TRUNCATE")]
    [InlineData("(SELECT 1)", "")]          // parantezle başlayan ifade anahtar sözcük değil
    [InlineData("", "")]
    [InlineData("-- yalnız yorum", "")]     // kapanmamış → anahtar yok
    [InlineData("/* kapanmamış", "")]
    public void Ilk_kelime_yorumlari_atlayarak_bulunur(string sql, string beklenen)
        => Assert.Equal(beklenen, SqlAnahtar.IlkKelime(sql));
}

public class GuvenliYazmaYurutucuTests
{
    private static readonly QueryService Servis = new();
    private static readonly LehceSaglayici Lehceler = new(new DpapiSecretProtector());
    private static ILehce Mssql => Lehceler.Getir(MotorTuru.Mssql);

    private static IReadOnlyList<SqlBatch> Bol(string sql) => SqlCozumleyici.BatchlereBol(sql);

    [Fact]
    public async Task Basarili_yazma_tran_icinde_calisir_ve_tran_acik_birakilir()
    {
        var oturum = new TranliOturum();

        QueryResult sonuc = await GuvenliYazmaYurutucu.CalistirAsync(
            Servis, Mssql, oturum, Bol("UPDATE t SET x = 1 WHERE id = 5"),
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(sonuc.Basarili);
        Assert.Equal(2, oturum.Gonderilenler.Count);              // BEGIN TRAN + DML
        Assert.Contains("BEGIN TRAN", oturum.Gonderilenler[0]);
        Assert.Contains("UPDATE t", oturum.Gonderilenler[1]);
        // Not: "COMMIT;" aranır — izolasyon ön ekindeki "READ COMMITTED" yanlış eşleşmesin
        Assert.DoesNotContain(oturum.Gonderilenler, s => s.Contains("ROLLBACK") || s.Contains("COMMIT;"));
    }

    [Fact]
    public async Task Hatali_yazma_hemen_geri_alinir_ve_mesaj_eklenir()
    {
        var oturum = new TranliOturum(hataVerenCagri: 2); // 1=BEGIN TRAN, 2=DML

        QueryResult sonuc = await GuvenliYazmaYurutucu.CalistirAsync(
            Servis, Mssql, oturum, Bol("UPDATE t SET x = 1 WHERE id = 5"),
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.False(sonuc.Basarili);
        Assert.Contains("ROLLBACK", oturum.Gonderilenler[^1]);
        Assert.Contains(sonuc.Mesajlar, m => m.Contains("geri alındı"));
    }

    [Fact]
    public async Task Iptal_edilen_yazma_da_geri_alinir()
    {
        var oturum = new TranliOturum(iptalEdilenCagri: 2);

        QueryResult sonuc = await GuvenliYazmaYurutucu.CalistirAsync(
            Servis, Mssql, oturum, Bol("DELETE FROM t WHERE id = 5"),
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(sonuc.IptalEdildi);
        Assert.Contains("ROLLBACK", oturum.Gonderilenler[^1]);
    }

    [Fact]
    public async Task Karar_commit_acik_islemde_commit_gonderir()
    {
        var oturum = new TranliOturum { Durum = IslemDurumu.Acik };

        string mesaj = await GuvenliYazmaYurutucu.KararUygulaAsync(
            Servis, Mssql, oturum, commit: true, opts: ExecuteOptions.Varsayilan);

        Assert.Contains("COMMIT", oturum.Gonderilenler[^1]);
        Assert.Contains("kalıcı", mesaj);
    }

    [Fact]
    public async Task Karar_rollback_acik_islemde_rollback_gonderir()
    {
        var oturum = new TranliOturum { Durum = IslemDurumu.Acik };

        string mesaj = await GuvenliYazmaYurutucu.KararUygulaAsync(
            Servis, Mssql, oturum, commit: false, opts: ExecuteOptions.Varsayilan);

        Assert.Contains("ROLLBACK", oturum.Gonderilenler[^1]);
        Assert.Contains("veri değişmedi", mesaj);
    }

    [Fact]
    public async Task Karar_mahkum_islemde_commit_istese_de_rollback_yapilir()
    {
        // 07-r2 §2: XACT_STATE = -1 (doomed) → COMMIT denenmez
        var oturum = new TranliOturum { Durum = IslemDurumu.Mahkum };

        string mesaj = await GuvenliYazmaYurutucu.KararUygulaAsync(
            Servis, Mssql, oturum, commit: true, opts: ExecuteOptions.Varsayilan);

        Assert.Contains("ROLLBACK", oturum.Gonderilenler[^1]);
        Assert.DoesNotContain(oturum.Gonderilenler, s => s.Contains("COMMIT;"));
        Assert.Contains("hasarlı", mesaj);
    }

    [Fact]
    public async Task Karar_islem_yoksa_hicbir_sey_gonderilmez_durum_bildirilir()
    {
        var oturum = new TranliOturum { Durum = IslemDurumu.Yok };

        string mesaj = await GuvenliYazmaYurutucu.KararUygulaAsync(
            Servis, Mssql, oturum, commit: true, opts: ExecuteOptions.Varsayilan);

        Assert.Empty(oturum.Gonderilenler);
        Assert.Contains("kalmamış", mesaj);
    }

    // ── V4-S2: Güvenli Yazma SQL ailesinin dördünde ─────────────────────────

    [Fact]
    public async Task Her_motor_kendi_islem_ifadesiyle_baslar()
    {
        var pg = new TranliOturum(motor: MotorTuru.Postgres) { Durum = IslemDurumu.Yok };
        await GuvenliYazmaYurutucu.CalistirAsync(Servis, Lehceler.Getir(MotorTuru.Postgres), pg,
            Bol("UPDATE t SET x = 1 WHERE id = 5"), ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.Equal("BEGIN;", pg.Gonderilenler[0]);

        var my = new TranliOturum(motor: MotorTuru.MySql) { Durum = IslemDurumu.Yok };
        await GuvenliYazmaYurutucu.CalistirAsync(Servis, Lehceler.Getir(MotorTuru.MySql), my,
            Bol("UPDATE t SET x = 1 WHERE id = 5"), ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.Equal("START TRANSACTION;", my.Gonderilenler[0]);
    }

    [Fact]
    public async Task Oracle_ayri_begin_gondermez_dogrudan_dml_ile_baslar()
    {
        var oturum = new TranliOturum(motor: MotorTuru.Oracle) { Durum = IslemDurumu.Yok };

        await GuvenliYazmaYurutucu.CalistirAsync(Servis, Lehceler.Getir(MotorTuru.Oracle), oturum,
            Bol("UPDATE t SET x = 1 WHERE id = 5"), ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.Single(oturum.Gonderilenler);                        // yalnız DML
        Assert.StartsWith("UPDATE t", oturum.Gonderilenler[0]);
    }

    [Fact]
    public async Task Durumu_bilmeyen_motorda_karar_dogrudan_uygulanir()
    {
        // REGRESYON: PG/MySQL/Oracle her zaman "Yok" döner. MSSQL'in yorumu uygulansaydı
        // ("açık işlem kalmamış") kullanıcının COMMIT'i sessizce YUTULURDU.
        var oturum = new TranliOturum(motor: MotorTuru.Postgres) { Durum = IslemDurumu.Yok };

        string mesaj = await GuvenliYazmaYurutucu.KararUygulaAsync(
            Servis, Lehceler.Getir(MotorTuru.Postgres), oturum, commit: true,
            opts: ExecuteOptions.Varsayilan);

        Assert.Equal("COMMIT;", oturum.Gonderilenler[^1]);
        Assert.Contains("kalıcı", mesaj);
        Assert.DoesNotContain("kalmamış", mesaj);
    }

    [Fact]
    public async Task Oracle_kararinda_commit_noktali_virgulsuz_gonderilir()
    {
        var oturum = new TranliOturum(motor: MotorTuru.Oracle) { Durum = IslemDurumu.Yok };

        await GuvenliYazmaYurutucu.KararUygulaAsync(
            Servis, Lehceler.Getir(MotorTuru.Oracle), oturum, commit: true,
            opts: ExecuteOptions.Varsayilan);

        Assert.Equal("COMMIT", oturum.Gonderilenler[^1]);   // ORA-00933
    }

    [Fact]
    public void Guvenli_yazma_sql_ailesinin_dordunde_desteklenir()
    {
        foreach (MotorTuru motor in new[]
                 { MotorTuru.Mssql, MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Oracle })
            Assert.True(Lehceler.Getir(motor).GuvenliYazmaDestekler, $"{motor} desteklemeli");
    }

    [Theory]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("ALTER TABLE t ADD x INT")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("  /* yorum */ CREATE INDEX ix ON t (x)")]   // yorum atlanır
    [InlineData("-- not\nDROP TABLE t")]
    public void MySql_ve_oracleda_ddl_ortuk_commit_sayilir(string sql)
    {
        // Bu ifadelerde bant AÇILMAMALI: motor DDL'i çalıştığı anda kalıcılaştırır,
        // "geri al" düğmesi hiçbir şey yapmayan bir yalan olurdu.
        Assert.True(Lehceler.Getir(MotorTuru.MySql).OrtukCommitYaparMi(sql));
        Assert.True(Lehceler.Getir(MotorTuru.Oracle).OrtukCommitYaparMi(sql));
    }

    [Theory]
    [InlineData("UPDATE t SET x = 1 WHERE id = 5")]
    [InlineData("DELETE FROM t WHERE id = 5")]
    [InlineData("INSERT INTO t (x) VALUES (1)")]
    public void Dml_hicbir_motorda_ortuk_commit_degildir(string sql)
    {
        foreach (MotorTuru motor in new[]
                 { MotorTuru.Mssql, MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Oracle })
            Assert.False(Lehceler.Getir(motor).OrtukCommitYaparMi(sql), $"{motor}: DML geri alınabilir");
    }

    [Theory]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("DROP TABLE t")]
    [InlineData("UPDATE t SET x = 1")]
    public void Mssql_ve_postgreste_ddl_de_islemseldir(string sql)
    {
        // PostgreSQL'in gerçek gücü: CREATE/DROP bile geri alınabilir → bant her yazmada açılır
        Assert.False(Lehceler.Getir(MotorTuru.Mssql).OrtukCommitYaparMi(sql));
        Assert.False(Lehceler.Getir(MotorTuru.Postgres).OrtukCommitYaparMi(sql));
    }

    /// <summary>Gönderilen SQL'leri kaydeden, XACT_STATE'i kurgulanabilen sahte oturum.</summary>
    private sealed class TranliOturum(
        int hataVerenCagri = 0, int iptalEdilenCagri = 0, MotorTuru motor = MotorTuru.Mssql) : IDbOturum
    {
        private int _cagri;

        // Motor profilden gelir: QueryService izolasyon ön ekini/Oracle ';' kırpmasını buna göre uygular
        public ConnectionProfile Profil { get; } = new() { Motor = motor };
        public List<string> Gonderilenler { get; } = [];
        public IslemDurumu Durum { get; set; } = IslemDurumu.Acik;

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
        {
            // İzolasyon komutu sayıma ve listeye GİRMEZ (2026-07-19 düzeltmesi: artık
            // kullanıcının SQL'iyle aynı batch'te değil, ayrı komut olarak gidiyor).
            // Bu testlerin ölçtüğü şey işlem/yazma komutlarının SIRASI.
            if (sql.StartsWith("SET TRANSACTION ISOLATION LEVEL", StringComparison.Ordinal))
                return Task.FromResult(new QueryResult { Basarili = true });

            _cagri++;
            Gonderilenler.Add(sql);

            if (_cagri == iptalEdilenCagri)
                return Task.FromResult(new QueryResult { IptalEdildi = true });
            if (_cagri == hataVerenCagri)
                return Task.FromResult(new QueryResult { Hata = new SqlHata("sahte", 547, 1, 16) });

            return Task.FromResult(new QueryResult { Basarili = true, EtkilenenSatir = 3 });
        }

        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => Task.FromResult(Durum);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
