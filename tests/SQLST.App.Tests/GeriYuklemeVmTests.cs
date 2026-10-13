using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// ⏪ Geri yükle penceresi VM'i (v23-S26 S2) sahte köprüyle: yükleme (bilgi + tarihçe), 🔎 Oku → setler ve en yeni seçili →
/// zincir + MOVE önerisi, hedef adı değişince yol/not değişir, REPLACE engeli, Tehlike onayı reddi/kabulü, ilerleme, hata,
/// iptal ikinci onayı, tarihçeden seçim, script sekmeye.
/// </summary>
public class GeriYuklemeVmTests
{
    private sealed class Sahte
    {
        public TaskCompletionSource<QueryResult> Restore = new();
        public List<(string Sql, string? Db, int Timeout)> Cagrilar = [];
        public List<(string Baslik, string Onay)> Onaylar = [];
        public bool OnayCevabi = true;
        public string[] Dbler = ["master", "Satis"];

        private static ResultSetData Rs(string[] kolonlar, params object?[][] satirlar)
            => new() { Kolonlar = [.. kolonlar.Select(k => new KolonBilgisi(k, "x"))], Satirlar = satirlar };

        public Task<QueryResult> Kos(string sql, string? db, int timeout, CancellationToken ct)
        {
            Cagrilar.Add((sql, db, timeout));
            if (sql.Contains("ProductMajorVersion"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["surum_major", "surum", "veri_klasoru", "gunluk_klasoru", "veritabanlari", "olusturma_izni", "sysadmin"],
                    [16, "16.0.1", @"C:\Data\", @"C:\Log\", string.Join('|', Dbler), 1, 0])] });
            if (sql.Contains("backupset"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["backup_start_date", "backup_finish_date", "type", "backup_size", "compressed_backup_size", "dosya", "kullanici", "is_copy_only", "veritabani", "konum"],
                    [new DateTime(2026, 10, 11, 2, 0, 0), new DateTime(2026, 10, 11, 2, 3, 0), "D", 1_900_000_000m, DBNull.Value, @"D:\Y\s.bak", "sa", false, "Satis", 1],
                    [new DateTime(2026, 10, 11, 14, 0, 0), new DateTime(2026, 10, 11, 14, 1, 0), "I", 120_000_000m, DBNull.Value, @"D:\Y\s.bak", "sa", false, "Satis", 2])] });
            if (sql.StartsWith("RESTORE HEADERONLY"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["BackupName", "BackupType", "Position", "DatabaseName", "BackupFinishDate", "BackupSize", "FirstLSN", "LastLSN", "CheckpointLSN", "DatabaseBackupLSN", "SoftwareVersionMajor", "SoftwareVersionMinor", "IsCopyOnly", "RecoveryModel"],
                    ["tam", (byte)1, (short)1, "Satis", new DateTime(2026, 10, 11, 2, 3, 0), 1_900_000_000m, 90m, 110m, 100m, 0m, 16, 0, false, "FULL"],
                    ["fark", (byte)5, (short)2, "Satis", new DateTime(2026, 10, 11, 14, 1, 0), 120_000_000m, 150m, 160m, 155m, 100m, 16, 0, false, "FULL"])] });
            if (sql.StartsWith("RESTORE FILELISTONLY"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["LogicalName", "PhysicalName", "Type", "Size"],
                    ["Satis", @"D:\Eski\Satis.mdf", "D", 2_400_000_000m], ["Satis_log", @"D:\Eski\Satis_log.ldf", "L", 512_000_000m])] });
            if (sql.StartsWith("RESTORE VERIFYONLY"))
                return Task.FromResult(new QueryResult { Basarili = true, Mesajlar = ["The backup set on file 2 is valid."] });
            if (sql.Contains("dm_exec_requests"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["percent_complete", "estimated_completion_time"], [35.0, 20_000L])] });
            if (sql.StartsWith("USE [master]"))
            {
                ct.Register(() => Restore.TrySetResult(new QueryResult { IptalEdildi = true }));
                return Restore.Task;
            }
            throw new InvalidOperationException("beklenmeyen sql: " + sql);
        }

        public bool Onay(string baslik, string mesaj, string onay)
        {
            Onaylar.Add((baslik, onay));
            return OnayCevabi;
        }
    }

    private static (GeriYuklemeViewModel Vm, Sahte S, List<(string, string)> Acilan) Kur()
    {
        var s = new Sahte();
        List<(string, string)> acilan = [];
        var vm = new GeriYuklemeViewModel("Satis", "Local", s.Kos, (b, sc) => acilan.Add((b, sc)), s.Onay) { IlerlemeAraligiMs = 20 };
        return (vm, s, acilan);
    }

    [Fact]
    public async Task Oku_setler_en_yeni_secili_zincir_move_ve_hedef_ad_degisimi()
    {
        (GeriYuklemeViewModel vm, Sahte s, _) = Kur();
        await vm.YukleAsync();
        Assert.Equal(2, vm.Tarihce.Count);
        Assert.Contains("dbcreator", vm.YetkiNotu);
        Assert.Contains("dosyası seçin", vm.Engel);            // dosya yok
        Assert.False(vm.GeriYuklenebilirMi);

        vm.Dosya = @"D:\Y\s.bak";
        Assert.Contains("Oku", vm.Engel);                      // dosya var, set yok
        await vm.OkuAsync(null);
        Assert.Equal(2, vm.Setler.Count);
        Assert.Equal(2, vm.SeciliSet!.Konum);                  // en yeni (fark)
        await Task.Delay(50);
        Assert.Contains("Tam #1 → Fark #2", vm.ZincirNotu);
        Assert.Equal(2, vm.Dosyalar.Count);
        // hedef = kaynak (Satis) → mevcut → yerinde yollar, REPLACE engeli
        Assert.True(vm.HedefMevcut);
        Assert.Equal(@"D:\Eski\Satis.mdf", vm.Dosyalar[0].Hedef);
        Assert.Contains("üzerine yaz", vm.Engel);
        Assert.Contains("üzerine yazılacak", vm.HedefNotu);
        vm.Replace = true;
        Assert.Equal("", vm.Engel);
        Assert.True(vm.GeriYuklenebilirMi);
        Assert.Contains("REPLACE", vm.Script);
        Assert.Contains("FILE = 1", vm.Script);
        Assert.Contains("FILE = 2", vm.Script);

        // yeni ad → kopya: yollar yeniden adlandırılır, REPLACE gerekmez
        vm.HedefDb = "Satis_Test";
        Assert.False(vm.HedefMevcut);
        Assert.Equal(@"C:\Data\Satis_Test.mdf", vm.Dosyalar[0].Hedef);
        Assert.Equal(@"C:\Log\Satis_Test_log.ldf", vm.Dosyalar[1].Hedef);
        Assert.Contains("yeni veritabanı", vm.HedefNotu);
        vm.Replace = false;
        Assert.Equal("", vm.Engel);
        Assert.Contains("RESTORE DATABASE [Satis_Test]", vm.Script);
        Assert.Contains("MOVE N'Satis' TO N'C:\\Data\\Satis_Test.mdf'", vm.Script);

        // elle yol düzenleme script'e yansır; boşaltınca engel
        vm.Dosyalar[1].Hedef = @"E:\Log\x.ldf";
        Assert.Contains("E:\\Log\\x.ldf", vm.Script);
        vm.Dosyalar[1].Hedef = "";
        Assert.Contains("hedef yolu boş", vm.Engel);
        vm.Dosyalar[1].Hedef = @"E:\Log\x.ldf";

        // set değişince (tam) zincir tek
        vm.SeciliSet = vm.Setler[0];
        await Task.Delay(50);
        Assert.Equal("", vm.ZincirNotu);
        Assert.DoesNotContain("FILE = 2", vm.Script);

        // doğrula
        await vm.DogrulaAsync();
        Assert.StartsWith("✔ Yedek seti geçerli", vm.Durum);
        Assert.Contains(s.Cagrilar, c => c.Sql.StartsWith("RESTORE VERIFYONLY") && c.Sql.Contains("FILE = 1"));
    }

    [Fact]
    public async Task Onay_reddi_kosmaz_kabulde_ilerleme_ve_basari()
    {
        (GeriYuklemeViewModel vm, Sahte s, _) = Kur();
        await vm.YukleAsync();
        vm.Dosya = @"D:\Y\s.bak";
        await vm.OkuAsync(1);
        await Task.Delay(50);
        vm.HedefDb = "Satis_Test";

        s.OnayCevabi = false;
        await vm.GeriYukleAsync();
        Assert.Single(s.Onaylar);
        Assert.Equal("⏪ Geri yükle", s.Onaylar[0].Onay);
        Assert.False(vm.Calisiyor);
        Assert.DoesNotContain(s.Cagrilar, c => c.Sql.StartsWith("USE [master]"));

        s.OnayCevabi = true;
        Task kosu = vm.GeriYukleAsync();
        await Task.Delay(120);
        Assert.True(vm.Calisiyor);
        Assert.Equal(35, vm.Yuzde);
        Assert.Contains("%35", vm.IlerlemeMetni);
        (string sql, string? db, int timeout) = s.Cagrilar.First(c => c.Sql.StartsWith("USE [master]"));
        Assert.Equal("master", db);
        Assert.Equal(0, timeout);
        s.Restore.SetResult(new QueryResult { Basarili = true, Mesajlar = ["RESTORE DATABASE successfully processed 245760 pages"] });
        await kosu;
        Assert.True(vm.Basarili);
        Assert.Contains("kullanıma hazır", vm.Durum);
        Assert.Contains("⟳", vm.Durum); // yeni DB → ağaç tazele notu
    }

    [Fact]
    public async Task Hata_turkce_iptal_ikinci_onay_ve_restoring_notu()
    {
        (GeriYuklemeViewModel vm, Sahte s, _) = Kur();
        await vm.YukleAsync();
        vm.Dosya = @"D:\Y\s.bak";
        await vm.OkuAsync(1);
        await Task.Delay(50);
        vm.Replace = true; vm.BaglantilariKes = true;
        Assert.Contains("SINGLE_USER", vm.Script);
        Task kosu = vm.GeriYukleAsync();
        s.Restore.SetResult(new QueryResult { Hata = new SqlHata("Exclusive access could not be obtained", 3101, 1, 16) });
        await kosu;
        Assert.Contains("bağlantıları kes", vm.Durum);

        (vm, s, _) = Kur();
        await vm.YukleAsync();
        vm.Dosya = @"D:\Y\s.bak";
        await vm.OkuAsync(1);
        await Task.Delay(50);
        vm.Replace = true;
        kosu = vm.GeriYukleAsync();
        await Task.Delay(40);
        s.OnayCevabi = false;
        vm.IptalCommand.Execute(null);                        // ikinci onay reddedildi → sürüyor
        Assert.True(vm.Calisiyor);
        s.OnayCevabi = true;
        vm.IptalCommand.Execute(null);
        await kosu;
        Assert.Equal("Yine de iptal et", s.Onaylar[^1].Onay);
        Assert.Contains("RESTORING", vm.Durum);
    }

    [Fact]
    public async Task Tarihceden_secim_ve_script_sekmeye()
    {
        (GeriYuklemeViewModel vm, _, List<(string, string)> acilan) = Kur();
        await vm.YukleAsync();
        await vm.TarihcedenSecAsync(vm.Tarihce[1]); // fark, konum 2
        await Task.Delay(50);
        Assert.Equal(@"D:\Y\s.bak", vm.Dosya);
        Assert.Equal(2, vm.SeciliSet!.Konum);
        vm.HedefDb = "Kopya";
        vm.ScriptiAlCommand.Execute(null);
        (string baslik, string script) = Assert.Single(acilan);
        Assert.Equal("geri-yukle-Kopya.sql", baslik);
        Assert.Equal(vm.Script, script);
    }
}
