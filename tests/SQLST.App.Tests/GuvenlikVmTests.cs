using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 🔐 Güvenlik sekmesi VM'i (v23-S29 S1) sahte köprüyle: yükleme (özet/login/eşleme/DB seçimi) → liste, rozetler, ilk login
/// seçili → rol çipleri + eşlemeler + yetkiler; süzgeç/arama; rol çipi tık → onay (sysadmin Tehlike) → ALTER SERVER ROLE;
/// kendi login'ini sysadmin'den çıkarma/kapatma engeli; aç/kapat; kullanıcı oluştur; Kullanıcılar görünümü → yetim rozeti →
/// 🔗 bağla; script sekmeye (parola yok); izin yoksa işlemler kapalı; salt-okunur profil.
/// </summary>
public class GuvenlikVmTests
{
    private sealed class Sahte
    {
        public bool Izin = true, Sysadmin = true, TumunuGorur = true;
        public bool OnayCevabi = true;
        public string KendiLogin = @"DESKTOP\lstuser";
        public HashSet<string> RaporRolleri = ["public"];
        public bool RaporKapali;
        public bool YetimBagli;
        public List<string> Sqller = [];
        public List<OnayIstegi> Onaylar = [];

        private static ResultSetData Rs(string[] k, params object?[][] s) => new() { Kolonlar = [.. k.Select(x => new KolonBilgisi(x, "x"))], Satirlar = s };

        public Task<QueryResult> Kos(string sql, string? db, int timeout, CancellationToken ct)
        {
            Sqller.Add(sql);
            if (sql.Contains("IsIntegratedSecurityOnly"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["yalniz_windows", "surum", "edition", "engine_edition", "tumunu_gorur", "login_degistirir", "sysadmin", "kendi_login", "veritabanlari"],
                    [0, "16.0.1", "Developer", 3, TumunuGorur ? 1 : 0, Izin ? 1 : 0, Sysadmin ? 1 : 0, KendiLogin, "master|Satis|Mersis"])] });
            if (sql.Contains("FROM sys.server_principals p"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["name", "type", "is_disabled", "default_database_name", "create_date", "sid", "politika", "kilitli", "suresi_dolmus", "parola_degisti", "roller"],
                    [KendiLogin, "U", false, "master", DateTime.Now, "0x01", false, 0, 0, DBNull.Value, "public|sysadmin"],
                    ["rapor_okuyucu", "S", RaporKapali, "Satis", DateTime.Now, "0x0A", true, 0, 0, DateTime.Now, string.Join('|', RaporRolleri)],
                    ["sa", "S", true, "master", DateTime.Now, "0x02", true, 0, 0, DateTime.Now, "public|sysadmin"],
                    ["entegrasyon", "S", false, "Mersis", DateTime.Now, "0x0C", true, 1, 0, DateTime.Now, "public"])] });
            if (sql.Contains("dm_exec_sessions"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["login_name", "oturum"], ["rapor_okuyucu", 2], [KendiLogin, 3])] });
            if (sql.Contains("type = 'R' ORDER BY name"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["name"], ["bulkadmin"], ["dbcreator"], ["public"], ["securityadmin"], ["sysadmin"])] });
            if (sql.Contains("sys.server_permissions"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["sinif", "nesne", "izin", "durum", "veren"], ["SERVER", "", "CONNECT SQL", "GRANT", "sa"])] });
            if (sql.Contains("UNION ALL") || sql.Contains("AS veritabani"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["veritabani", "kullanici", "sid", "roller"],
                    ["Satis", "rapor_okuyucu", "0x0A", "db_datareader|public"], ["Mersis", "rapor_okuyucu", "0x0A", "db_datareader"], ["Satis", "eski_rapor", "0xFF", "db_datareader"], ["Satis", "dbo", "0x01", "db_owner"])] });
            if (sql.Contains("FROM sys.database_principals dp"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["name", "type", "kimlik", "login", "sema", "create_date", "sid", "yetim", "roller"],
                    ["dbo", "U", "WINDOWS", KendiLogin, "dbo", DateTime.Now, "0x01", 0, "db_owner"],
                    ["eski_rapor", "S", "INSTANCE", YetimBagli ? "eski_rapor" : DBNull.Value, "dbo", DateTime.Now, "0xFF", YetimBagli ? 0 : 1, "db_datareader"],
                    ["bilerek_loginsiz", "S", "NONE", DBNull.Value, "dbo", DateTime.Now, "0xEE", 0, ""],
                    ["rapor_okuyucu", "S", "INSTANCE", "rapor_okuyucu", "dbo", DateTime.Now, "0x0A", 0, "db_datareader|public"])] });
            if (sql.Contains("is_fixed_role"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["name"], ["db_owner"], ["db_datareader"], ["db_datawriter"], ["public"], ["rapor_rolu"])] });
            if (sql.Contains("sys.database_permissions"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["sinif", "nesne", "izin", "durum", "veren"], ["OBJECT_OR_COLUMN", "dbo.Fatura", "DELETE", "DENY", "dbo"], ["DATABASE", "Satis", "CONNECT", "GRANT", "dbo"])] });
            // DDL
            if (sql.StartsWith("ALTER SERVER ROLE"))
            {
                string rol = sql.Split('[', ']')[1];
                if (sql.Contains(" ADD ")) RaporRolleri.Add(rol); else RaporRolleri.Remove(rol);
                return Task.FromResult(new QueryResult { Basarili = true });
            }
            if (sql.StartsWith("ALTER LOGIN")) { RaporKapali = sql.Contains("DISABLE"); return Task.FromResult(new QueryResult { Basarili = true }); }
            if (sql.StartsWith("ALTER USER")) { YetimBagli = true; return Task.FromResult(new QueryResult { Basarili = true }); }
            if (sql.StartsWith("ALTER ROLE") || sql.StartsWith("CREATE USER")) return Task.FromResult(new QueryResult { Basarili = true });
            throw new InvalidOperationException("beklenmeyen sql: " + sql);
        }

        public bool Onay(OnayIstegi i) { Onaylar.Add(i); return OnayCevabi; }
    }

    private static (GuvenlikSekmesiViewModel Vm, Sahte S, List<(string Baslik, string Sql, string? Db)> Sekmeler) Kur(Action<Sahte>? ayarla = null, bool saltOkunur = false)
    {
        var s = new Sahte();
        ayarla?.Invoke(s);
        List<(string, string, string?)> sekmeler = [];
        var vm = new GuvenlikSekmesiViewModel("Local", s.Kos, (b, q, d) => sekmeler.Add((b, q, d)), s.Onay, saltOkunur);
        return (vm, s, sekmeler);
    }

    [Fact]
    public async Task Yukleme_liste_rozetler_detay_suzgec_arama()
    {
        (GuvenlikSekmesiViewModel vm, _, _) = Kur();
        await vm.YenileAsync();
        await Task.Delay(50);

        Assert.Equal("🔐 Güvenlik · Local", vm.Baslik);
        Assert.Contains("Karma kimlik", vm.DurumRozeti);
        Assert.Contains("4 login · 2 sysadmin · sa kapalı · 1 yetim kullanıcı", vm.Meta);
        Assert.False(vm.UyariVar);
        Assert.Equal("Satis", vm.SeciliDb);                      // sistem DB'leri atlanır
        Assert.Equal(4, vm.Loginler.Count);
        LoginGorunum sa = vm.Loginler.Single(l => l.Ad == "sa");
        Assert.Equal("kapalı", sa.DurumMetni); Assert.True(sa.Sysadmin);
        Assert.Equal("kilitli", vm.Loginler.Single(l => l.Ad == "entegrasyon").DurumMetni);
        LoginGorunum rapor = vm.Loginler.Single(l => l.Ad == "rapor_okuyucu");
        Assert.Equal(2, rapor.Oturum); Assert.Equal("Satis, Mersis", rapor.DbMetni); Assert.Equal("—", vm.Loginler.Single(l => l.Ad == "sa").DbMetni);

        // ilk login seçili (DESKTOP\lstuser) → detay: rol çipleri (public yok), eşleme, yetki
        Assert.Equal(vm.Loginler[0].Ad, vm.SeciliLogin!.Ad);
        Assert.Equal(["bulkadmin", "dbcreator", "securityadmin", "sysadmin"], vm.RolCipleri.Select(c => c.Ad));
        Assert.True(vm.RolCipleri.Single(c => c.Ad == "sysadmin").Uye);
        Assert.True(vm.RolCipleri.Single(c => c.Ad == "sysadmin").Tehlikeli);
        Assert.Single(vm.Yetkiler); Assert.Equal("CONNECT SQL", vm.Yetkiler[0].Izin);

        vm.SeciliLogin = rapor;
        await Task.Delay(50);
        Assert.Equal(["Satis", "Mersis"], vm.Eslemeler.Where(e => e.KullaniciVar).Select(e => e.Veritabani));
        Assert.DoesNotContain(vm.Eslemeler, e => e.Veritabani == "master"); // sistem DB'leri eşleme listesine girmez
        Assert.Contains("2 açık oturum", vm.SeciliMeta);
        Assert.False(vm.KullaniciOlusturAcik);                   // Satis'te zaten kullanıcı var

        // süzgeç + arama
        vm.SuzgecKapali = true;
        Assert.Equal(["sa"], vm.Loginler.Select(l => l.Ad));
        vm.SuzgecSorunlu = true;
        Assert.Equal(["entegrasyon"], vm.Loginler.Select(l => l.Ad));
        vm.SuzgecTumu = true; vm.AramaMetni = "rapor";
        Assert.Single(vm.Loginler);
        Assert.Equal(1, vm.KapaliSayisi); Assert.Equal(1, vm.SorunluSayisi); Assert.Equal(3, vm.SqlSayisi); Assert.Equal(1, vm.WindowsSayisi);
    }

    [Fact]
    public async Task Rol_cipi_onay_tehlike_kendi_login_engeli_ac_kapat()
    {
        (GuvenlikSekmesiViewModel vm, Sahte s, _) = Kur();
        await vm.YenileAsync();
        await Task.Delay(50);
        vm.SeciliLogin = vm.Loginler.Single(l => l.Ad == "rapor_okuyucu");
        await Task.Delay(50);

        // dbcreator ekle: Soru onayı, reddet → değişmez; kabul → ALTER SERVER ROLE ADD, çip işaretli
        RolCipi dbcreator = vm.RolCipleri.Single(c => c.Ad == "dbcreator");
        s.OnayCevabi = false;
        await vm.RolDegistirCommand.ExecuteAsync(dbcreator);
        Assert.False(s.RaporRolleri.Contains("dbcreator"));
        Assert.False(s.Onaylar[^1].Tehlikeli);
        s.OnayCevabi = true;
        await vm.RolDegistirCommand.ExecuteAsync(dbcreator);
        await Task.Delay(80);
        Assert.Contains("ALTER SERVER ROLE [dbcreator] ADD MEMBER [rapor_okuyucu];", s.Sqller);
        Assert.True(vm.RolCipleri.Single(c => c.Ad == "dbcreator").Uye);
        Assert.Contains("eklendi: dbcreator", vm.Bilgi);
        // sysadmin ekle → Tehlike onayı
        await vm.RolDegistirCommand.ExecuteAsync(vm.RolCipleri.Single(c => c.Ad == "sysadmin"));
        Assert.True(s.Onaylar[^1].Tehlikeli);
        Assert.Contains("ALTER SERVER ROLE [sysadmin] ADD MEMBER [rapor_okuyucu];", s.Sqller);

        // kendi login'ini sysadmin'den çıkarma engeli — komut KOŞMAZ
        await Task.Delay(80);
        vm.SeciliLogin = vm.Loginler.Single(l => l.Ad == s.KendiLogin);
        await Task.Delay(50);
        int once = s.Sqller.Count;
        await vm.RolDegistirCommand.ExecuteAsync(vm.RolCipleri.Single(c => c.Ad == "sysadmin"));
        Assert.Contains("Kendi login'inizi", vm.Bilgi);
        Assert.DoesNotContain(s.Sqller.Skip(once), q => q.StartsWith("ALTER SERVER ROLE"));
        // kendi login'ini kapatma engeli
        await vm.AcKapatCommand.ExecuteAsync(null);
        Assert.Contains("kapatamazsınız", vm.Bilgi);

        // rapor_okuyucu'yu kapat → açık oturum uyarısı mesajda, ALTER LOGIN DISABLE, rozet kapalı; sonra aç
        vm.SeciliLogin = vm.Loginler.Single(l => l.Ad == "rapor_okuyucu");
        await Task.Delay(50);
        Assert.Equal("⏸ Kapat", vm.AcKapatMetni);
        await vm.AcKapatCommand.ExecuteAsync(null);
        await Task.Delay(80);
        Assert.Contains("2 açık oturumu", s.Onaylar[^1].Mesaj);
        Assert.Contains("ALTER LOGIN [rapor_okuyucu] DISABLE;", s.Sqller);
        Assert.Equal("kapalı", vm.Loginler.Single(l => l.Ad == "rapor_okuyucu").DurumMetni);
        Assert.Equal("▶ Aç", vm.AcKapatMetni);
        // sa'yı aç → Tehlike
        vm.SeciliLogin = vm.Loginler.Single(l => l.Ad == "sa");
        await Task.Delay(50);
        s.OnayCevabi = false;
        await vm.AcKapatCommand.ExecuteAsync(null);
        Assert.True(s.Onaylar[^1].Tehlikeli);
        Assert.Contains("güvenlik riski", s.Onaylar[^1].Mesaj);
    }

    [Fact]
    public async Task Kullanicilar_gorunumu_yetim_bagla_db_rolu_kullanici_olustur_script()
    {
        (GuvenlikSekmesiViewModel vm, Sahte s, List<(string Baslik, string Sql, string? Db)> sekmeler) = Kur();
        await vm.YenileAsync();
        await Task.Delay(50);
        vm.GorunumKullanicilar = true;
        await Task.Delay(80);
        Assert.Equal(4, vm.Kullanicilar.Count);
        KullaniciGorunum yetim = vm.Kullanicilar.Single(k => k.Ad == "eski_rapor");
        Assert.True(yetim.Yetim); Assert.Equal("yetim", yetim.LoginMetni); Assert.Equal("hata", yetim.LoginTuru);
        Assert.Equal("login'siz (bilerek)", vm.Kullanicilar.Single(k => k.Ad == "bilerek_loginsiz").LoginMetni); // WITHOUT LOGIN ≠ yetim
        Assert.Equal(1, vm.YetimSayisi);
        Assert.Equal(1, vm.RolsuzSayisi); // yalnız bilerek_loginsiz rolsüz
        vm.KSuzgecYetim = true;
        Assert.Single(vm.Kullanicilar);
        vm.KSuzgecTumu = true;

        // DB rol çipleri seçili kullanıcı için
        vm.SeciliKullanici = vm.Kullanicilar.Single(k => k.Ad == "rapor_okuyucu");
        await Task.Delay(50);
        Assert.Equal(["db_owner", "db_datareader", "db_datawriter", "rapor_rolu"], vm.RolCipleri.Select(c => c.Ad));
        Assert.True(vm.RolCipleri.Single(c => c.Ad == "db_datareader").Uye);
        Assert.Contains(vm.Yetkiler, y => y.Izin == "DELETE" && y.DurumTuru == "hata");
        await vm.RolDegistirCommand.ExecuteAsync(vm.RolCipleri.Single(c => c.Ad == "db_datawriter"));
        Assert.Contains("ALTER ROLE [db_datawriter] ADD MEMBER [rapor_okuyucu];", s.Sqller);
        Assert.Contains("VERİTABANI ROLLERİ (Satis)", vm.RolBasligi);

        // yetim bağla: aynı adlı login yok → script sekmeye + bilgi; sonra login varmış gibi
        await Task.Delay(80);
        vm.SeciliKullanici = vm.Kullanicilar.Single(k => k.Ad == "eski_rapor");
        await Task.Delay(50);
        Assert.True(vm.YetimBaglaAcik);
        Assert.Equal("🔗 Login'e bağla…", vm.YetimBaglaMetni);
        await vm.YetimBaglaCommand.ExecuteAsync(null);
        Assert.Contains("login yok", vm.Bilgi);
        Assert.Contains("ALTER USER [eski_rapor] WITH LOGIN = [<login adı>];", sekmeler[^1].Sql);

        // kullanıcı script'i: WITHOUT LOGIN + rol + DENY
        vm.ScriptAlCommand.Execute(null);
        Assert.Contains("CREATE USER [eski_rapor] WITHOUT LOGIN", sekmeler[^1].Sql);
        Assert.Equal("Satis", sekmeler[^1].Db);

        // login görünümünde kullanıcı oluştur (Mersis'te kullanıcı yok olan sa için)
        vm.GorunumLoginler = true;
        await Task.Delay(50);
        vm.SeciliLogin = vm.Loginler.Single(l => l.Ad == "sa");
        await Task.Delay(50);
        vm.SeciliDb = "Mersis";
        await Task.Delay(80);
        Assert.True(vm.KullaniciOlusturAcik);
        await vm.KullaniciOlusturCommand.ExecuteAsync(null);
        Assert.Contains("CREATE USER [sa] FOR LOGIN [sa];", s.Sqller);
        // login script'i parola yazmaz
        vm.ScriptAlCommand.Execute(null);
        Assert.Contains("N'<parola>'", sekmeler[^1].Sql);
        Assert.DoesNotContain("Pass", sekmeler[^1].Sql);
    }

    [Fact]
    public async Task Izin_yok_tumunu_goremez_salt_okunur()
    {
        (GuvenlikSekmesiViewModel vm, _, _) = Kur(x => { x.Izin = false; x.Sysadmin = false; });
        await vm.YenileAsync();
        Assert.False(vm.IslemYapilabilir);
        Assert.Contains("ALTER ANY LOGIN", vm.Uyari);
        Assert.False(vm.AcKapatAcik);

        (vm, _, _) = Kur(x => { x.TumunuGorur = false; x.Sysadmin = false; });
        await vm.YenileAsync();
        Assert.Contains("yalnız KENDİ login'inizi", vm.Uyari);

        (vm, _, _) = Kur(saltOkunur: true);
        await vm.YenileAsync();
        Assert.False(vm.IslemYapilabilir);
        Assert.Contains("Salt-okunur", vm.Uyari);
    }
}
