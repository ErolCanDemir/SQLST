using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

public class GecmisVeOturumDepoTests : IDisposable
{
    private readonly string _dosya = Path.Combine(Path.GetTempPath(), $"sqlst-{Guid.NewGuid():N}.db");

    private YerelDepo Depo() => new(_dosya);

    private static GecmisKaydi Kayit(string sql, GecmisDurumu durum = GecmisDurumu.Basarili) => new()
    {
        Sunucu = @"(localdb)\MSSQLLocalDB",
        Veritabani = "master",
        Sql = sql,
        BaslangicUtc = new DateTime(2026, 7, 16, 9, 30, 0, DateTimeKind.Utc),
        SureMs = 42,
        SatirSayisi = 7,
        Durum = durum,
    };

    [Fact]
    public async Task Gecmis_ekle_ve_yeniden_eskiye_listele()
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        await gecmis.EkleAsync(Kayit("SELECT 1"));
        await gecmis.EkleAsync(Kayit("SELECT 2", GecmisDurumu.Hata) with { HataMesaji = "patladı" });

        IReadOnlyList<GecmisKaydi> hepsi = await gecmis.AraAsync(null);

        Assert.Equal(2, hepsi.Count);
        Assert.Equal("SELECT 2", hepsi[0].Sql); // en yeni önce
        Assert.Equal(GecmisDurumu.Hata, hepsi[0].Durum);
        Assert.Equal("patladı", hepsi[0].HataMesaji);
        Assert.Equal("master", hepsi[0].Veritabani);
        // UTC gidip UTC dönmeli (gösterim katmanı yerelleştirir, depo değil — FOG-9)
        Assert.Equal(DateTimeKind.Utc, hepsi[0].BaslangicUtc.Kind);
        Assert.Equal(new DateTime(2026, 7, 16, 9, 30, 0, DateTimeKind.Utc), hepsi[0].BaslangicUtc);
    }

    [Fact]
    public async Task Gecmis_Kullanici_gidip_gelir() // v10-S2 denetim "kim" + göç (Kullanici sütunu)
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        await gecmis.EkleAsync(Kayit("SELECT 1") with { Kullanici = @"DOMAIN\ali" });
        await gecmis.EkleAsync(Kayit("SELECT 2")); // Kullanici null (eski/yakalanmayan kayıt)

        IReadOnlyList<GecmisKaydi> hepsi = await gecmis.AraAsync(null);

        Assert.Null(hepsi[0].Kullanici);              // en yeni ("SELECT 2") — null korunur
        Assert.Equal(@"DOMAIN\ali", hepsi[1].Kullanici); // en eski ("SELECT 1")
    }

    [Fact]
    public async Task Gecmis_SekmeAdi_gidip_gelir() // 2026-07-23: geçmişte hangi sekmeden çalıştığı
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        await gecmis.EkleAsync(Kayit("SELECT 1") with { SekmeAdi = "SQLST3" });
        await gecmis.EkleAsync(Kayit("SELECT 2")); // eski kayıt — sekme adı yok

        IReadOnlyList<GecmisKaydi> hepsi = await gecmis.AraAsync(null);

        Assert.Null(hepsi[0].SekmeAdi);
        Assert.Equal("SQLST3", hepsi[1].SekmeAdi);
    }

    // ── Profil bazlı OTURUM SEKMELERİ (V3, kullanıcı bulgusu 2026-07-18) ────

    [Fact]
    public async Task Oturum_sekmeleri_profillere_gore_yalitilir()
    {
        IOturumDeposu oturum = new SqliteOturumDeposu(Depo());
        Guid mssql = Guid.NewGuid(), mongo = Guid.NewGuid();

        await oturum.KaydetAsync([Sekme("SELECT 1", "master")], mssql);
        await oturum.KaydetAsync([Sekme("""{ "find": "musteri" }""", "demo")], mongo);

        OturumSekmeKaydi mssqlSekme = Assert.Single(await oturum.YukleAsync(mssql));
        Assert.Equal("SELECT 1", mssqlSekme.Sql);
        OturumSekmeKaydi mongoSekme = Assert.Single(await oturum.YukleAsync(mongo));
        Assert.Contains("find", mongoSekme.Sql);
    }

    [Fact]
    public async Task Bir_profilin_oturumu_kaydedilince_digerininki_silinmez()
    {
        IOturumDeposu oturum = new SqliteOturumDeposu(Depo());
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await oturum.KaydetAsync([Sekme("SELECT 'a1'"), Sekme("SELECT 'a2'")], a);
        await oturum.KaydetAsync([Sekme("SELECT 'b1'")], b);

        // a'yı yeniden kaydet (sil+yaz) — b etkilenmemeli
        await oturum.KaydetAsync([Sekme("SELECT 'a-yeni'")], a);

        Assert.Equal("SELECT 'a-yeni'", Assert.Single(await oturum.YukleAsync(a)).Sql);
        Assert.Single(await oturum.YukleAsync(b));
    }

    [Fact]
    public async Task Profilsiz_eski_oturum_kayitlari_profile_sizmaz()
    {
        IOturumDeposu oturum = new SqliteOturumDeposu(Depo());
        await oturum.KaydetAsync([Sekme("SELECT 'v2-eski'")]);   // profilsiz (v2 davranışı)
        Guid yeni = Guid.NewGuid();

        Assert.Empty(await oturum.YukleAsync(yeni));             // yeni profil temiz başlar
        Assert.Single(await oturum.YukleAsync());                // eski kayıt duruyor (silinmedi)
    }

    private static OturumSekmeKaydi Sekme(string sql, string? vt = null) => new()
    {
        Baslik = "sekme",
        Veritabani = vt,
        Sql = sql,
    };

    // ── Profil bazlı geçmiş (V3, kullanıcı isteği 2026-07-18) ───────────────

    [Fact]
    public async Task Gecmis_profillere_gore_yalitilir()
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await gecmis.EkleAsync(Kayit("SELECT 'a-1'") with { ProfilId = a });
        await gecmis.EkleAsync(Kayit("SELECT 'b-1'") with { ProfilId = b });
        await gecmis.EkleAsync(Kayit("SELECT 'a-2'") with { ProfilId = a });

        IReadOnlyList<GecmisKaydi> aGecmis = await gecmis.AraAsync(null, profilId: a);
        Assert.Equal(2, aGecmis.Count);
        Assert.All(aGecmis, k => Assert.Equal(a, k.ProfilId));
        Assert.Equal("SELECT 'a-2'", aGecmis[0].Sql);

        Assert.Equal("SELECT 'b-1'", Assert.Single(await gecmis.AraAsync(null, profilId: b)).Sql);
        // Kapsam HER ZAMAN tek profildir: profil verilmezse yalnız profilsiz (v2) kayıtlar —
        // "tüm profiller" diye bir kaçış yolu YOK (V3 denetimi).
        Assert.Empty(await gecmis.AraAsync(null));
    }

    [Fact]
    public async Task Gecmis_temizleme_yalniz_verilen_profili_siler()
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await gecmis.EkleAsync(Kayit("SELECT 'a'") with { ProfilId = a });
        await gecmis.EkleAsync(Kayit("SELECT 'b'") with { ProfilId = b });

        await gecmis.TemizleAsync(a);

        Assert.Empty(await gecmis.AraAsync(null, profilId: a));
        Assert.Single(await gecmis.AraAsync(null, profilId: b)); // diğer profil korunur
    }

    [Fact]
    public async Task Budama_profil_basina_isler_baska_profili_supurmez()
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        Guid yogun = Guid.NewGuid(), seyrek = Guid.NewGuid();
        await gecmis.EkleAsync(Kayit("SELECT 'seyrek'") with { ProfilId = seyrek }, enCok: 2);
        for (int i = 0; i < 5; i++)
            await gecmis.EkleAsync(Kayit($"SELECT {i}") with { ProfilId = yogun }, enCok: 2);

        Assert.Equal(2, (await gecmis.AraAsync(null, profilId: yogun)).Count);  // kendi sınırına budandı
        Assert.Single(await gecmis.AraAsync(null, profilId: seyrek));           // komşu profil sağlam
    }

    [Fact]
    public async Task Eski_v2_kayitlari_profilsiz_kalir_ve_profil_suzgecine_takilmaz()
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        await gecmis.EkleAsync(Kayit("SELECT 'eski'"));            // ProfilId yok (v2 davranışı)
        Guid yeni = Guid.NewGuid();
        await gecmis.EkleAsync(Kayit("SELECT 'yeni'") with { ProfilId = yeni });

        Assert.Null((await gecmis.AraAsync("eski")).Single().ProfilId);
        Assert.Equal("SELECT 'yeni'", Assert.Single(await gecmis.AraAsync(null, profilId: yeni)).Sql);
    }

    [Fact]
    public async Task Gecmis_arama_sql_icinde_buyuk_kucuk_duyarsiz_suzer()
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        await gecmis.EkleAsync(Kayit("SELECT * FROM Musteri"));
        await gecmis.EkleAsync(Kayit("UPDATE Siparis SET X = 1"));

        Assert.Single(await gecmis.AraAsync("musteri"));
        Assert.Single(await gecmis.AraAsync("SIPARIS"));
        Assert.Empty(await gecmis.AraAsync("fatura"));
        Assert.Equal(2, (await gecmis.AraAsync("  ")).Count); // boşluk = süzgeç yok
    }

    [Fact]
    public async Task Gecmis_budama_son_n_kaydi_tutar()
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        for (int i = 1; i <= 5; i++)
            await gecmis.EkleAsync(Kayit($"SELECT {i}"), enCok: 3);

        IReadOnlyList<GecmisKaydi> kalanlar = await gecmis.AraAsync(null);

        Assert.Equal(3, kalanlar.Count);
        Assert.Equal("SELECT 5", kalanlar[0].Sql);
        Assert.Equal("SELECT 3", kalanlar[^1].Sql); // 1 ve 2 budandı
    }

    [Fact]
    public async Task Gecmis_temizle_hepsini_siler()
    {
        ISorguGecmisiDeposu gecmis = new SqliteSorguGecmisiDeposu(Depo());
        await gecmis.EkleAsync(Kayit("SELECT 1"));

        await gecmis.TemizleAsync();

        Assert.Empty(await gecmis.AraAsync(null));
    }

    [Fact]
    public async Task Oturum_kaydet_yukle_sira_ve_alanlar_korunur()
    {
        IOturumDeposu oturum = new SqliteOturumDeposu(Depo());
        OturumSekmeKaydi[] sekmeler =
        [
            new() { Baslik = "SELECT Musteri", Sql = "SELECT * FROM Musteri", Veritabani = "lstqmsdb", OtomatikAd = true },
            new() { Baslik = "dbo.spRapor", Sql = "EXEC dbo.spRapor", Veritabani = null, SeciliMi = true, OtomatikAd = false },
        ];

        await oturum.KaydetAsync(sekmeler);
        IReadOnlyList<OturumSekmeKaydi> geri = await oturum.YukleAsync();

        Assert.Equal(2, geri.Count);
        Assert.Equal(sekmeler[0], geri[0]); // record eşitliği tüm alanları doğrular
        Assert.Equal(sekmeler[1], geri[1]);
    }

    [Fact]
    public async Task Oturum_yeniden_kaydet_oncekini_tumden_degistirir()
    {
        IOturumDeposu oturum = new SqliteOturumDeposu(Depo());
        await oturum.KaydetAsync([new OturumSekmeKaydi { Baslik = "eski", Sql = "SELECT 0" }]);

        await oturum.KaydetAsync([new OturumSekmeKaydi { Baslik = "yeni", Sql = "SELECT 1" }]);
        IReadOnlyList<OturumSekmeKaydi> geri = await oturum.YukleAsync();

        Assert.Single(geri);
        Assert.Equal("yeni", geri[0].Baslik);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string ek in new[] { "", "-wal", "-shm" })
        {
            string y = _dosya + ek;
            if (File.Exists(y)) File.Delete(y);
        }
    }
}
