using System.Data;
using System.Net.Sockets;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// V4-S1 CANLI KANITI: Edit modunun PostgreSQL'de gerçekten çalıştığını gösterir —
/// V4-S1'e kadar <c>PostgresLehcesi.DuzenlemeMetaSorgusu</c> NotSupportedException atıyordu.
///
/// Birim testleri yalnız ÜRETİLEN METNİ kanıtlar; burada meta gerçekten okunur, DML gerçek
/// sunucuda çalışır ve çakışma davranışı gerçek satırlarla doğrulanır. Test tablosu her
/// koşumda benzersiz adla yaratılıp düşürülür (demo veriye dokunulmaz).
///
/// Ortam-kapılı: 127.0.0.1:5433'te PostgreSQL + sqlst_demo yoksa test ATLANIR.
/// </summary>
public class DuzenlemePostgresCanliTests : IAsyncLifetime
{
    private const string Host = "127.0.0.1";
    private const int Port = 5433;
    private const string Db = "sqlst_demo";

    private static ConnectionProfile Profil() => new()
    {
        Ad = "pg-duzenleme",
        Motor = MotorTuru.Postgres,
        Sunucu = $"{Host}:{Port}",
        Kimlik = KimlikTuru.Sql,   // trust auth — parola yok sayılır
        KullaniciAdi = "postgres",
        BaglantiTimeoutSn = 10,
    };

    private static readonly ExecuteOptions Demo = new() { VeritabaniOverride = Db };

    private readonly string _tablo = $"duzenleme_e2e_{Guid.NewGuid():N}";
    private readonly PostgresLehcesi _lehce = new(new DpapiSecretProtector());
    private readonly SqlExecutor _executor;
    private readonly SchemaService _schema;

    public DuzenlemePostgresCanliTests()
    {
        _executor = new SqlExecutor(_lehce);
        _schema = new SchemaService(_executor, _lehce);
    }

    private static bool Erisilebilir()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch { return false; }
    }

    private SemaNesnesi Nesne() => new(Db, "public", _tablo, SemaNesneTuru.Tablo, [], []);

    public async Task InitializeAsync()
    {
        if (!Erisilebilir()) return;
        // identity + üretilmiş kolon + boolean + NULL'lanabilir numeric: meta bayraklarının tamamı
        QueryResult r = await _executor.ExecuteAsync(Profil(), $"""
            CREATE TABLE public.{_tablo} (
                id        INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                ad        VARCHAR(50) NOT NULL,
                bakiye    NUMERIC(18,2) NULL,
                aktif     BOOLEAN NOT NULL DEFAULT TRUE,
                katlanmis INT GENERATED ALWAYS AS (id * 2) STORED
            );
            INSERT INTO public.{_tablo} (ad, bakiye) VALUES ('Ali Çelik', 10.50), ('Ayşe Öz', NULL);
            """, Demo, CancellationToken.None);
        Assert.True(r.Basarili, r.Hata?.Mesaj);
    }

    public async Task DisposeAsync()
    {
        if (!Erisilebilir()) return;
        await _executor.ExecuteAsync(Profil(),
            $"DROP TABLE IF EXISTS public.{_tablo};", Demo, CancellationToken.None);
    }

    [Fact]
    public async Task Meta_bayraklari_postgreste_dogru_okunur()
    {
        if (!Erisilebilir()) return;

        DuzenlemeMetasi meta = await _schema.DuzenlemeMetaAsync(Profil(), Nesne(), CancellationToken.None);

        Assert.True(meta.DuzenlenebilirMi);   // PK var → grid düzenlenebilir

        DuzenlemeKolonu id = meta.Kolonlar.Single(k => k.Ad == "id");
        Assert.True(id.PkMi);
        Assert.True(id.IdentityMi);           // GENERATED AS IDENTITY
        Assert.False(id.Yazilabilir);         // sunucu üretir → SET/INSERT'e girmez

        // Üretilmiş kolon da sunucu malıdır (PG 12+ attgenerated)
        Assert.True(meta.Kolonlar.Single(k => k.Ad == "katlanmis").ComputedMi);
        Assert.False(meta.Kolonlar.Single(k => k.Ad == "katlanmis").Yazilabilir);

        DuzenlemeKolonu bakiye = meta.Kolonlar.Single(k => k.Ad == "bakiye");
        Assert.True(bakiye.NullOlabilir);
        Assert.True(bakiye.KiyasGuvenliMi);   // numeric kırılgan değil → eski-değer kıyasına girer

        Assert.True(meta.Kolonlar.Single(k => k.Ad == "ad").Yazilabilir);
        Assert.False(meta.Kolonlar.Single(k => k.Ad == "ad").NullOlabilir);

        // PG'de rowversion yoktur → kimlik PK + eski değer kıyasıyla korunur
        Assert.Null(meta.Rowversion);
    }

    [Fact]
    public async Task Guncelle_sil_ekle_tek_islemde_uygulanir()
    {
        if (!Erisilebilir()) return;

        var servis = new QueryService();
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        DuzenlemeMetasi meta = await _schema.DuzenlemeMetaAsync(Profil(), Nesne(), CancellationToken.None);
        DataTable veri = await YukleAsync(oturum, meta);
        Assert.Equal(2, veri.Rows.Count);

        veri.Rows[0]["bakiye"] = 99.99m;          // Ali → UPDATE (numeric)
        veri.Rows[1].Delete();                     // Ayşe → DELETE
        DataRow yeni = veri.NewRow();
        yeni["ad"] = "Veli'nin Oğlu";              // tek tırnak → kaçış kanıtı
        yeni["aktif"] = false;                     // boolean → PG'de FALSE literali
        veri.Rows.Add(yeni);                       // id identity, katlanmis üretilmiş → atlanır

        IReadOnlyList<string> komutlar = DmlUretici.Uret(_lehce, meta, veri);
        Assert.Equal(3, komutlar.Count);
        // Üretilen metin gerçekten PG lehçesinde olmalı (T-SQL kalıntısı yok)
        Assert.All(komutlar, k => Assert.DoesNotContain("N'", k));
        Assert.All(komutlar, k => Assert.DoesNotContain("[", k));

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            servis, _lehce, oturum, komutlar, Demo, CancellationToken.None);
        Assert.True(basarili, mesaj);

        QueryResult son = await oturum.CalistirAsync(
            $"SELECT ad, bakiye, aktif, katlanmis FROM public.{_tablo} ORDER BY id;",
            Demo, CancellationToken.None);
        Assert.True(son.Basarili, son.Hata?.Mesaj);
        Assert.Equal(2, son.ResultSetler[0].Satirlar.Count);

        object?[] ali = son.ResultSetler[0].Satirlar[0];
        Assert.Equal("Ali Çelik", ali[0]);
        Assert.Equal(99.99m, ali[1]);
        Assert.Equal(2, Convert.ToInt32(ali[3]));       // üretilmiş kolon: id(1) * 2

        object?[] veliSatir = son.ResultSetler[0].Satirlar[1];
        Assert.Equal("Veli'nin Oğlu", veliSatir[0]);    // tek tırnak doğru kaçırıldı
        Assert.Equal(DBNull.Value, veliSatir[1]);       // bakiye verilmedi → NULL
        Assert.False((bool)veliSatir[2]!);              // aktif = FALSE yazıldı
    }

    [Fact]
    public async Task Baskasi_degistirdiyse_cakisma_hepsi_geri_alinir()
    {
        if (!Erisilebilir()) return;

        var servis = new QueryService();
        var fabrika = new OturumFabrikasi(_lehce);
        await using IDbOturum oturum = fabrika.Olustur(Profil());

        DuzenlemeMetasi meta = await _schema.DuzenlemeMetaAsync(Profil(), Nesne(), CancellationToken.None);
        DataTable veri = await YukleAsync(oturum, meta);
        veri.Rows[0]["ad"] = "Bizim Değişiklik";
        veri.Rows[1]["ad"] = "İkinci Değişiklik";   // bu da geri alınmalı (tümü tek işlem)

        // Araya "başkası" girer: aynı satırı değiştirir → eski-değer kıyası artık tutmaz
        QueryResult rakip = await _executor.ExecuteAsync(Profil(),
            $"UPDATE public.{_tablo} SET ad = 'Rakip' WHERE id = (SELECT MIN(id) FROM public.{_tablo});",
            Demo, CancellationToken.None);
        Assert.True(rakip.Basarili, rakip.Hata?.Mesaj);

        (bool basarili, string mesaj) = await DuzenlemeUygulayici.UygulaAsync(
            servis, _lehce, oturum, DmlUretici.Uret(_lehce, meta, veri), Demo, CancellationToken.None);

        Assert.False(basarili);
        Assert.Contains("Çakışma", mesaj);

        QueryResult son = await oturum.CalistirAsync(
            $"SELECT ad FROM public.{_tablo} ORDER BY id;", Demo, CancellationToken.None);
        Assert.Equal("Rakip", son.ResultSetler[0].Satirlar[0][0]);        // rakibinki durdu
        Assert.Equal("Ayşe Öz", son.ResultSetler[0].Satirlar[1][0]);      // bizim 2. yazmamız da geri alındı
    }

    /// <summary>VM'in tipli tablo kurulumunun testteki karşılığı; satır sınırı motorun söz diziminde.</summary>
    private async Task<DataTable> YukleAsync(IDbOturum oturum, DuzenlemeMetasi meta)
    {
        QueryResult sonuc = await oturum.CalistirAsync(
            _lehce.IlkNSatirSorgusu(Nesne(), 200), Demo, CancellationToken.None);
        Assert.True(sonuc.Basarili, sonuc.Hata?.Mesaj);

        var tablo = new DataTable();
        foreach (KolonBilgisi k in sonuc.ResultSetler[0].Kolonlar)
            tablo.Columns.Add(k.Ad, k.ClrTip ?? typeof(object));
        foreach (object?[] satir in sonuc.ResultSetler[0].Satirlar)
            tablo.Rows.Add(satir);
        tablo.AcceptChanges();
        return tablo;
    }
}
