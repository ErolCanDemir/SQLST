using System.Data;
using System.Globalization;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

public class LiteralYaziciTests
{
    [Theory]
    [InlineData(null, "NULL")]
    [InlineData("abc", "N'abc'")]
    [InlineData("O'Brien", "N'O''Brien'")]      // tek tırnak ikileme
    [InlineData("türkçe İĞŞ", "N'türkçe İĞŞ'")]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    [InlineData(42, "42")]
    [InlineData(-7L, "-7")]
    public void Temel_tipler(object? deger, string beklenen)
        => Assert.Equal(beklenen, LiteralYazici.Yaz(deger));

    [Fact]
    public void DBNull_null_yazilir()
        => Assert.Equal("NULL", LiteralYazici.Yaz(DBNull.Value));

    [Fact]
    public void Ondalik_ve_kayan_nokta_daima_invariant()
    {
        // 07-r2 §4 KRİTİK: tr-TR'de ToString "3,14" üretir — felaket. Nokta şart.
        CultureInfo eski = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("3.14", LiteralYazici.Yaz(3.14m));
            Assert.Equal("2.5", LiteralYazici.Yaz(2.5));
            Assert.Equal("1.5", LiteralYazici.Yaz(1.5f));
            Assert.Equal("-1234567.89", LiteralYazici.Yaz(-1234567.89m));

            // V4-S1: invariant kuralı HER motorda geçerli olmalı
            foreach (LiteralKurallari k in TumKurallar)
                Assert.Equal("3.14", LiteralYazici.Yaz(3.14m, k));
        }
        finally
        {
            CultureInfo.CurrentCulture = eski;
        }
    }

    [Fact]
    public void Tarih_iso8601_saatli_ve_saatsiz()
    {
        Assert.Equal("'2026-07-17'", LiteralYazici.Yaz(new DateTime(2026, 7, 17)));
        Assert.Equal("'2026-07-17T14:30:05.1230000'",
            LiteralYazici.Yaz(new DateTime(2026, 7, 17, 14, 30, 5, 123)));
    }

    [Fact]
    public void Binary_hex_guid_tirnakli()
    {
        Assert.Equal("0x0AFF", LiteralYazici.Yaz(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("0x00", LiteralYazici.Yaz(Array.Empty<byte>()));
        var g = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("'11111111-2222-3333-4444-555555555555'", LiteralYazici.Yaz(g));
    }

    [Fact]
    public void Bilinmeyen_tip_sessizce_yazilmaz_acik_hata()
        => Assert.Throws<NotSupportedException>(() => LiteralYazici.Yaz(new object()));

    // ── V4-S1: motor bazlı literal kuralları ────────────────────────────────

    private static readonly LiteralKurallari[] TumKurallar =
        [LiteralKurallari.TSql, LiteralKurallari.Postgres, LiteralKurallari.MySql, LiteralKurallari.Oracle];

    [Fact]
    public void Unicode_oneki_yalniz_tsqlde()
    {
        Assert.Equal("N'abc'", LiteralYazici.Yaz("abc", LiteralKurallari.TSql));
        Assert.Equal("'abc'", LiteralYazici.Yaz("abc", LiteralKurallari.Postgres));
        Assert.Equal("'abc'", LiteralYazici.Yaz("abc", LiteralKurallari.MySql));
        Assert.Equal("'abc'", LiteralYazici.Yaz("abc", LiteralKurallari.Oracle));
    }

    [Fact]
    public void Tek_tirnak_her_motorda_ikilenir()
    {
        foreach (LiteralKurallari k in TumKurallar)
            Assert.Contains("O''Brien", LiteralYazici.Yaz("O'Brien", k));
    }

    [Fact]
    public void MySqlde_ters_bolen_kacirilir_digerlerinde_kacirilmaz()
    {
        // MySQL/MariaDB varsayılanında '\' kaçış karakteridir: ikilenmezse değer sessizce bozulur
        Assert.Equal(@"'C:\\yol'", LiteralYazici.Yaz(@"C:\yol", LiteralKurallari.MySql));
        Assert.Equal(@"'C:\yol'", LiteralYazici.Yaz(@"C:\yol", LiteralKurallari.Postgres));
        Assert.Equal(@"N'C:\yol'", LiteralYazici.Yaz(@"C:\yol", LiteralKurallari.TSql));
    }

    [Fact]
    public void Bool_postgreste_gercek_boolean_digerlerinde_1_0()
    {
        Assert.Equal("TRUE", LiteralYazici.Yaz(true, LiteralKurallari.Postgres));
        Assert.Equal("FALSE", LiteralYazici.Yaz(false, LiteralKurallari.Postgres));
        Assert.Equal("1", LiteralYazici.Yaz(true, LiteralKurallari.TSql));
        Assert.Equal("1", LiteralYazici.Yaz(true, LiteralKurallari.MySql));
        Assert.Equal("1", LiteralYazici.Yaz(true, LiteralKurallari.Oracle));  // Oracle'da BOOLEAN kolon tipi yok
    }

    [Fact]
    public void Ikili_veri_her_motorun_kendi_bicimiyle()
    {
        byte[] b = [0x0A, 0xFF];
        Assert.Equal("0x0AFF", LiteralYazici.Yaz(b, LiteralKurallari.TSql));
        Assert.Equal(@"'\x0AFF'::bytea", LiteralYazici.Yaz(b, LiteralKurallari.Postgres));
        Assert.Equal("X'0AFF'", LiteralYazici.Yaz(b, LiteralKurallari.MySql));
        Assert.Equal("HEXTORAW('0AFF')", LiteralYazici.Yaz(b, LiteralKurallari.Oracle));
    }

    [Fact]
    public void Oracleda_bos_ikili_literal_sessizce_uretilmez()
        // HEXTORAW('') Oracle'da NULL'a düşüp hata verir → açık ret (yanlış veri yazmaktansa hiç yazma)
        => Assert.Throws<NotSupportedException>(
            () => LiteralYazici.Yaz(Array.Empty<byte>(), LiteralKurallari.Oracle));

    [Fact]
    public void Tarih_motorun_bekledigi_bicimde()
    {
        var saatli = new DateTime(2026, 7, 18, 14, 30, 5, 123);
        var saatsiz = new DateTime(2026, 7, 18);

        Assert.Equal("'2026-07-18T14:30:05.1230000'", LiteralYazici.Yaz(saatli, LiteralKurallari.TSql));
        Assert.Equal("'2026-07-18T14:30:05.1230000'", LiteralYazici.Yaz(saatli, LiteralKurallari.Postgres));

        // MySQL: boşluk ayraçlı ve EN ÇOK 6 kesir hanesi (7 hane hata verir)
        Assert.Equal("'2026-07-18 14:30:05.123000'", LiteralYazici.Yaz(saatli, LiteralKurallari.MySql));

        // Oracle: örtük dönüşüm yok — açık TO_DATE/TO_TIMESTAMP
        Assert.Equal("TO_DATE('2026-07-18', 'YYYY-MM-DD')", LiteralYazici.Yaz(saatsiz, LiteralKurallari.Oracle));
        Assert.StartsWith("TO_TIMESTAMP('2026-07-18 14:30:05.1230000'", LiteralYazici.Yaz(saatli, LiteralKurallari.Oracle));
    }
}

public class DmlUreticiTests
{
    private static readonly LehceSaglayici Lehceler = new(new DpapiSecretProtector());
    private static ILehce Mssql => Lehceler.Getir(MotorTuru.Mssql);

    private static DuzenlemeKolonu Kolon(string ad, string tip = "int", bool pk = false,
        bool identity = false, bool rowversion = false, bool kiyas = true)
        => new(ad, tip, tip, NullOlabilir: !pk, PkMi: pk,
            IdentityMi: identity, ComputedMi: false, RowversionMi: rowversion, KiyasGuvenliMi: kiyas);

    private static DuzenlemeMetasi Meta(params DuzenlemeKolonu[] kolonlar)
        => new("db", "dbo", "Musteri", kolonlar);

    private static DataTable Tablo(DuzenlemeMetasi meta, params object?[][] satirlar)
    {
        var t = new DataTable();
        foreach (DuzenlemeKolonu k in meta.Kolonlar)
            t.Columns.Add(k.Ad, typeof(object));
        foreach (object?[] s in satirlar)
            t.Rows.Add(s);
        t.AcceptChanges(); // yüklenmiş kabul et — sonrası değişiklik sayılır
        return t;
    }

    [Fact]
    public void Hucre_degisikligi_update_pk_ve_eski_deger_kiyasiyla()
    {
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true), Kolon("Ad", "nvarchar"));
        DataTable t = Tablo(meta, [1, "eski"]);
        t.Rows[0]["Ad"] = "yeni";

        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);

        Assert.Single(k);
        Assert.Equal("UPDATE [dbo].[Musteri] SET [Ad] = N'yeni' WHERE [Id] = 1 AND [Ad] = N'eski';", k[0]);
    }

    [Fact]
    public void Bekleyen_proposed_duzenleme_endedit_ile_gorunur()
    {
        // Kullanıcı bulgusu 2026-07-27: gridde hücreyi değiştirip Uygula deyince "değişiklik yok"
        // çıkıyordu, oysa "1 bekleyen değişiklik" görünüyordu. Kök neden: DataGrid satırı hâlâ
        // BeginEdit'te (Proposed sürüm), DmlUretici DataRowVersion.Current'ı okuyor → eski değer.
        // Çözüm (ViewModel.BekleyenleriIsle): Uygula'dan önce bekleyen satır EndEdit ile kesinleşir.
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true), Kolon("Aktif", "bit"));
        DataTable t = Tablo(meta, [1, true]);
        DataRow row = t.Rows[0];
        row.BeginEdit();
        row["Aktif"] = false;   // Proposed — henüz EndEdit yok (gridde düzenleme sürüyor)

        Assert.Empty(DmlUretici.Uret(Mssql, meta, t)); // BUG hâli: Current eski → değişiklik görünmez

        row.EndEdit();          // ViewModel.BekleyenleriIsle bunu yapar
        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);
        Assert.Single(k);
        Assert.Contains("[Aktif] = 0", k[0]);
    }

    [Fact]
    public void Rowversion_varsa_eski_deger_kiyasina_gerek_yok()
    {
        DuzenlemeMetasi meta = Meta(
            Kolon("Id", pk: true), Kolon("Ad", "nvarchar"),
            Kolon("Rv", "rowversion", rowversion: true));
        DataTable t = Tablo(meta, [1, "eski", new byte[] { 0x01 }]);
        t.Rows[0]["Ad"] = "yeni";

        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);

        Assert.Equal("UPDATE [dbo].[Musteri] SET [Ad] = N'yeni' WHERE [Id] = 1 AND [Rv] = 0x01;", k[0]);
    }

    [Fact]
    public void Kirilgan_tip_kiyasa_girmez_ama_set_edilir()
    {
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true), Kolon("Oran", "float", kiyas: false));
        DataTable t = Tablo(meta, [1, 1.5]);
        t.Rows[0]["Oran"] = 2.5;

        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);

        Assert.Equal("UPDATE [dbo].[Musteri] SET [Oran] = 2.5 WHERE [Id] = 1;", k[0]);
    }

    [Fact]
    public void Null_eski_deger_is_null_ile_kiyaslanir()
    {
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true), Kolon("Not", "nvarchar"));
        DataTable t = Tablo(meta, [5, DBNull.Value]);
        t.Rows[0]["Not"] = "dolu";

        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);

        Assert.Equal("UPDATE [dbo].[Musteri] SET [Not] = N'dolu' WHERE [Id] = 5 AND [Not] IS NULL;", k[0]);
    }

    [Fact]
    public void Silme_pk_uzerinden_delete_uretir()
    {
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true), Kolon("Ad", "nvarchar"));
        DataTable t = Tablo(meta, [7, "x"]);
        t.Rows[0].Delete();

        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);

        Assert.Equal("DELETE FROM [dbo].[Musteri] WHERE [Id] = 7;", k[0]);
    }

    [Fact]
    public void Ekleme_identity_atlar_bos_kolon_default_icin_atlanir()
    {
        DuzenlemeMetasi meta = Meta(
            Kolon("Id", pk: true, identity: true),
            Kolon("Ad", "nvarchar"), Kolon("Kayit", "datetime2"));
        DataTable t = Tablo(meta);
        DataRow yeni = t.NewRow();
        yeni["Ad"] = "Veli";
        t.Rows.Add(yeni); // Kayit boş — sunucu DEFAULT'una kalır, Id identity

        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);

        Assert.Equal("INSERT INTO [dbo].[Musteri] ([Ad]) VALUES (N'Veli');", k[0]);
    }

    [Fact]
    public void Coklu_pk_ve_koseli_ad_kacisi()
    {
        DuzenlemeMetasi meta = new("db", "dbo", "Sipa]ris",
            [Kolon("A", pk: true), Kolon("B", pk: true), Kolon("Deger", "nvarchar")]);
        DataTable t = Tablo(meta, [1, 2, "v"]);
        t.Rows[0].Delete();

        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);

        Assert.Equal("DELETE FROM [dbo].[Sipa]]ris] WHERE [A] = 1 AND [B] = 2;", k[0]);
    }

    [Fact]
    public void Sira_delete_update_insert_ve_ayni_degere_yazma_uretmez()
    {
        DuzenlemeMetasi meta = Meta(Kolon("Id", pk: true), Kolon("Ad", "nvarchar"));
        DataTable t = Tablo(meta, [1, "a"], [2, "b"], [3, "c"]);
        t.Rows[2].Delete();                 // 3 → DELETE
        t.Rows[0]["Ad"] = "a";              // aynı değer — komut üretmemeli
        t.Rows[1]["Ad"] = "B";              // → UPDATE
        DataRow yeni = t.NewRow(); yeni["Id"] = 9; yeni["Ad"] = "y"; t.Rows.Add(yeni); // → INSERT

        IReadOnlyList<string> k = DmlUretici.Uret(Mssql, meta, t);

        Assert.Equal(3, k.Count);
        Assert.StartsWith("DELETE", k[0]);
        Assert.StartsWith("UPDATE", k[1]);
        Assert.StartsWith("INSERT", k[2]);
    }

    [Fact]
    public void Pk_olmayan_tabloda_uretim_reddedilir()
    {
        DuzenlemeMetasi meta = Meta(Kolon("Ad", "nvarchar"));
        DataTable t = Tablo(meta, ["x"]);
        t.Rows[0]["Ad"] = "y";

        Assert.Throws<InvalidOperationException>(() => DmlUretici.Uret(Mssql, meta, t));
    }

    // ── V4-S1: aynı düzenleme, dört motorda dört ayrı doğru metin ───────────

    private static (DuzenlemeMetasi Meta, DataTable Tablo) BasitGuncelleme()
    {
        DuzenlemeMetasi meta = new("db", "satis", "musteri",
            [Kolon("id", pk: true), Kolon("ad", "varchar")]);
        DataTable t = Tablo(meta, [1, "eski"]);
        t.Rows[0]["ad"] = "yeni";
        return (meta, t);
    }

    [Fact]
    public void Postgres_cift_tirnak_ve_N_oneksiz_literal()
    {
        (DuzenlemeMetasi meta, DataTable t) = BasitGuncelleme();

        string sql = DmlUretici.Uret(Lehceler.Getir(MotorTuru.Postgres), meta, t)[0];

        Assert.Equal("""UPDATE "satis"."musteri" SET "ad" = 'yeni' WHERE "id" = 1 AND "ad" = 'eski';""", sql);
    }

    [Fact]
    public void MySql_ters_tirnak_ve_sema_yazilmaz()
    {
        (DuzenlemeMetasi meta, DataTable t) = BasitGuncelleme();

        string sql = DmlUretici.Uret(Lehceler.Getir(MotorTuru.MySql), meta, t)[0];

        // MySQL'de şema = veritabanı; oturum zaten oradadır → tam ad yalnız tablo
        Assert.Equal("UPDATE `musteri` SET `ad` = 'yeni' WHERE `id` = 1 AND `ad` = 'eski';", sql);
        Assert.DoesNotContain("satis", sql);
    }

    [Fact]
    public void Oracle_sonda_noktali_virgul_olmaz()
    {
        (DuzenlemeMetasi meta, DataTable t) = BasitGuncelleme();

        string sql = DmlUretici.Uret(Lehceler.Getir(MotorTuru.Oracle), meta, t)[0];

        Assert.Equal("""UPDATE "satis"."musteri" SET "ad" = 'yeni' WHERE "id" = 1 AND "ad" = 'eski'""", sql);
        Assert.DoesNotContain(";", sql);   // ORA-00933
    }

    [Fact]
    public void Hicbir_motorda_tsql_kalintisi_kalmaz()
    {
        foreach (MotorTuru motor in new[] { MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Oracle })
        {
            (DuzenlemeMetasi meta, DataTable t) = BasitGuncelleme();
            string sql = DmlUretici.Uret(Lehceler.Getir(motor), meta, t)[0];

            Assert.DoesNotContain("[", sql);      // T-SQL köşeli tırnak
            Assert.DoesNotContain("N'", sql);     // T-SQL unicode öneki
        }
    }

    [Fact]
    public void Tum_kolonlar_default_ekleme_motora_gore()
    {
        DuzenlemeMetasi meta = new("db", "satis", "musteri", [Kolon("id", pk: true, identity: true)]);
        DataTable t = Tablo(meta);
        t.Rows.Add(t.NewRow());   // identity dışında doldurulacak kolon yok

        Assert.Equal("INSERT INTO [satis].[musteri] DEFAULT VALUES;",
            DmlUretici.Uret(Mssql, meta, t)[0]);
        Assert.Equal("""INSERT INTO "satis"."musteri" DEFAULT VALUES;""",
            DmlUretici.Uret(Lehceler.Getir(MotorTuru.Postgres), meta, t)[0]);
        // MySQL'de DEFAULT VALUES yoktur — boş kolon listesi aynı işi görür
        Assert.Equal("INSERT INTO `musteri` () VALUES ();",
            DmlUretici.Uret(Lehceler.Getir(MotorTuru.MySql), meta, t)[0]);
        // Oracle'da karşılığı yok → sessiz yanlış yerine açık hata
        Assert.Throws<NotSupportedException>(
            () => DmlUretici.Uret(Lehceler.Getir(MotorTuru.Oracle), meta, t));
    }

    [Fact]
    public void Edit_modu_sql_ailesinin_dordunde_de_desteklenir()
    {
        foreach (MotorTuru motor in new[]
                 { MotorTuru.Mssql, MotorTuru.Postgres, MotorTuru.MySql, MotorTuru.Oracle })
        {
            Assert.True(Lehceler.Getir(motor).DuzenlemeDestekler, $"{motor} Edit modunu desteklemeli");
            // Meta sorgusu artık NotSupportedException atmamalı ve tabloyu adlandırmalı
            string sorgu = Lehceler.Getir(motor).DuzenlemeMetaSorgusu(
                new SemaNesnesi("db", "satis", "musteri", SemaNesneTuru.Tablo, [], []));
            Assert.False(string.IsNullOrWhiteSpace(sorgu));
            Assert.Contains("musteri", sorgu, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Oracle_duzenleme_meta_sorgusunda_noktali_virgul_olmaz()
    {
        string sorgu = Lehceler.Getir(MotorTuru.Oracle).DuzenlemeMetaSorgusu(
            new SemaNesnesi("db", "satis", "musteri", SemaNesneTuru.Tablo, [], []));
        Assert.DoesNotContain(";", sorgu);
    }
}

public class DuzenlemeUygulayiciTests
{
    private static readonly QueryService Servis = new();
    private static readonly LehceSaglayici Lehceler = new(new DpapiSecretProtector());
    private static ILehce Mssql => Lehceler.Getir(MotorTuru.Mssql);

    [Fact]
    public async Task Basarili_komutlar_tranda_calisir_ve_commit_edilir()
    {
        var oturum = new UygulayiciOturumu();

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            Servis, Mssql, oturum, ["UPDATE t SET x = 1 WHERE id = 1;", "DELETE FROM t WHERE id = 2;"],
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(basarili, mesaj);
        Assert.Contains("BEGIN TRAN", oturum.Gonderilenler[0]);
        Assert.Contains("COMMIT;", oturum.Gonderilenler[^1]);
        Assert.Contains("2 değişiklik", mesaj);
    }

    [Fact]
    public async Task Sifir_satir_etkileyen_komut_cakisma_hepsi_geri_alinir()
    {
        var oturum = new UygulayiciOturumu(sifirSatirliCagri: 3); // 1=TRAN, 2=ilk komut, 3=ikinci

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            Servis, Mssql, oturum, ["UPDATE 1;", "UPDATE 2;", "UPDATE 3;"],
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.False(basarili);
        Assert.Contains("Çakışma", mesaj);
        Assert.Contains("geri alındı", mesaj);
        Assert.Contains("ROLLBACK", oturum.Gonderilenler[^1]);
        Assert.DoesNotContain(oturum.Gonderilenler, s => s.Contains("UPDATE 3"));
    }

    [Fact]
    public async Task Hatali_komut_hepsi_geri_alinir()
    {
        var oturum = new UygulayiciOturumu(hataVerenCagri: 2);

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            Servis, Mssql, oturum, ["UPDATE 1;"], ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.False(basarili);
        Assert.Contains("1. komut hatası", mesaj);
        Assert.Contains("ROLLBACK", oturum.Gonderilenler[^1]);
    }

    [Fact]
    public async Task Bos_liste_hicbir_sey_gondermez()
    {
        var oturum = new UygulayiciOturumu();

        (bool basarili, string _) = await DuzenlemeUygulayici.UygulaAsync(
            Servis, Mssql, oturum, [], ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(basarili);
        Assert.Empty(oturum.Gonderilenler);
    }

    // ── V4-S1: işlem ifadeleri motora göre ──────────────────────────────────

    [Fact]
    public async Task Postgres_ve_mysql_kendi_islem_ifadelerini_gonderir()
    {
        var pg = new UygulayiciOturumu(islemDurumu: IslemDurumu.Yok, motor: MotorTuru.Postgres);
        await DuzenlemeUygulayici.UygulaAsync(Servis, Lehceler.Getir(MotorTuru.Postgres), pg,
            ["UPDATE t SET x = 1;"], ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.Equal("BEGIN;", pg.Gonderilenler[0]);
        Assert.Equal("COMMIT;", pg.Gonderilenler[^1]);

        var my = new UygulayiciOturumu(islemDurumu: IslemDurumu.Yok, motor: MotorTuru.MySql);
        await DuzenlemeUygulayici.UygulaAsync(Servis, Lehceler.Getir(MotorTuru.MySql), my,
            ["UPDATE t SET x = 1;"], ExecuteOptions.Varsayilan, CancellationToken.None);
        Assert.Equal("START TRANSACTION;", my.Gonderilenler[0]);
    }

    [Fact]
    public async Task Oracle_ayri_begin_gondermez_islem_ortuk_baslar()
    {
        var oturum = new UygulayiciOturumu(islemDurumu: IslemDurumu.Yok, motor: MotorTuru.Oracle);

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            Servis, Lehceler.Getir(MotorTuru.Oracle), oturum,
            ["UPDATE t SET x = 1"], ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(basarili, mesaj);
        Assert.Equal("UPDATE t SET x = 1", oturum.Gonderilenler[0]); // doğrudan DML — begin yok
        Assert.Equal("COMMIT", oturum.Gonderilenler[^1]);            // Oracle'da ';' yok
    }

    [Fact]
    public async Task Islem_durumunu_bilmeyen_motorda_saglam_islem_bosuna_geri_alinmaz()
    {
        // REGRESYON: MySQL/Oracle/PG her zaman "Yok" döner. Bu "işlem öldü" sanılırsa
        // sorunsuz bir düzenleme sessizce reddedilirdi (V4-S1 IslemDurumuBilinir bayrağı).
        var oturum = new UygulayiciOturumu(islemDurumu: IslemDurumu.Yok, motor: MotorTuru.Postgres);

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            Servis, Lehceler.Getir(MotorTuru.Postgres), oturum,
            ["UPDATE t SET x = 1;"], ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.True(basarili, mesaj);
        Assert.Contains("COMMIT;", oturum.Gonderilenler[^1]);
    }

    [Fact]
    public async Task Mssqlde_olen_islem_hala_yakalanir()
    {
        // Karşı taraf: MSSQL durumu GERÇEKTEN bilir → "Yok" gerçekten "işlem öldü" demektir
        var oturum = new UygulayiciOturumu(islemDurumu: IslemDurumu.Yok);

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            Servis, Mssql, oturum, ["UPDATE t SET x = 1;"],
            ExecuteOptions.Varsayilan, CancellationToken.None);

        Assert.False(basarili);
        Assert.Contains("beklenmedik şekilde kapandı", mesaj);
    }

    private sealed class UygulayiciOturumu(
        int hataVerenCagri = 0, int sifirSatirliCagri = 0, IslemDurumu islemDurumu = IslemDurumu.Acik,
        MotorTuru motor = MotorTuru.Mssql) : IDbOturum
    {
        private int _cagri;

        // Motor profilden gelir: QueryService izolasyon ön ekini/Oracle ';' kırpmasını
        // buna göre uygular — sahte oturum gerçek motorunu söylemezse T-SQL ön eki sızar.
        public ConnectionProfile Profil { get; } = new() { Motor = motor };
        public List<string> Gonderilenler { get; } = [];

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
        {
            // İzolasyon komutu sayıma ve listeye GİRMEZ (2026-07-19 düzeltmesi: artık ayrı
            // komut olarak gidiyor). Bu testlerin ölçtüğü şey üretilen DML'in SIRASI.
            if (sql.StartsWith("SET TRANSACTION ISOLATION LEVEL", StringComparison.Ordinal))
                return Task.FromResult(new QueryResult { Basarili = true });

            _cagri++;
            Gonderilenler.Add(sql);
            if (_cagri == hataVerenCagri)
                return Task.FromResult(new QueryResult { Hata = new SqlHata("sahte", 547, 1, 16) });
            // İşlem ifadeleri satır etkilemez; kalanlar "komut" sayılır
            bool komutMu = !sql.Contains("BEGIN TRAN") && !sql.Contains("BEGIN;")
                        && !sql.Contains("START TRANSACTION") && !sql.StartsWith("COMMIT")
                        && !sql.Contains("ROLLBACK");
            return Task.FromResult(new QueryResult
            {
                Basarili = true,
                EtkilenenSatir = komutMu ? (_cagri == sifirSatirliCagri ? 0 : 1) : null,
            });
        }

        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => Task.FromResult(islemDurumu);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
