using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 💾 Yedek al penceresi VM'i (v23-S26 S1) sahte köprüyle: yükleme → dosya önerisi/görünürlükler, canlı script,
/// SIMPLE'da günlük engeli, ▶ Yedekle → ilerleme yoklaması + başarı/hata/iptal yolları, "Script olarak al".
/// </summary>
public class YedekPenceresiVmTests
{
    private sealed class SahteSunucu
    {
        public string Kurtarma = "SIMPLE";
        public bool SikistirmaVar;
        public bool Izin = true;
        public TaskCompletionSource<QueryResult> Yedek = new();
        public int YoklamaSayisi;
        public List<(string Sql, string? Db, int Timeout)> Cagrilar = [];
        public double Yuzde = 40;

        public Task<QueryResult> Kos(string sql, string? db, int timeout, CancellationToken ct)
        {
            Cagrilar.Add((sql, db, timeout));
            if (sql.Contains("SERVERPROPERTY"))
                return Task.FromResult(new QueryResult
                {
                    Basarili = true,
                    ResultSetler = [new ResultSetData
                    {
                        Kolonlar = [new("surum", "x"), new("edition", "x"), new("engine_edition", "x"), new("kurtarma", "x"),
                                    new("yedek_klasoru", "x"), new("veri_klasoru", "x"), new("sikistirma_var", "x"),
                                    new("sikistirma_varsayilan", "x"), new("yedek_izni", "x"), new("gunluk_izni", "x")],
                        Satirlar = [["16.0.1", "Developer", 3, Kurtarma, DBNull.Value, @"C:\Data\", SikistirmaVar ? 1 : 0, 1, Izin ? 1 : 0, 1]],
                    }],
                });
            if (sql.Contains("backupset"))
                return Task.FromResult(new QueryResult
                {
                    Basarili = true,
                    ResultSetler = [new ResultSetData
                    {
                        Kolonlar = [new("backup_start_date", "x"), new("backup_finish_date", "x"), new("type", "x"), new("backup_size", "x"),
                                    new("compressed_backup_size", "x"), new("dosya", "x"), new("kullanici", "x"), new("is_copy_only", "x")],
                        Satirlar = Yedek.Task.IsCompletedSuccessfully
                            ? [[new DateTime(2026, 10, 12, 17, 15, 0), new DateTime(2026, 10, 12, 17, 16, 0), "D", 1_000_000m, DBNull.Value, @"C:\Data\Satis.bak", "lst", false]]
                            : [],
                    }],
                });
            if (sql.Contains("dm_exec_requests"))
            {
                YoklamaSayisi++;
                return Task.FromResult(new QueryResult
                {
                    Basarili = true,
                    ResultSetler = [new ResultSetData
                    {
                        Kolonlar = [new("percent_complete", "x"), new("estimated_completion_time", "x")],
                        Satirlar = [[Yuzde, 41_000L]],
                    }],
                });
            }
            if (sql.StartsWith("BACKUP"))
            {
                ct.Register(() => Yedek.TrySetResult(new QueryResult { IptalEdildi = true }));
                return Yedek.Task;
            }
            throw new InvalidOperationException("beklenmeyen sql: " + sql);
        }
    }

    private static (YedekPenceresiViewModel Vm, SahteSunucu Sunucu, List<(string, string)> Acilan) Kur(Action<SahteSunucu>? ayarla = null)
    {
        var sunucu = new SahteSunucu();
        ayarla?.Invoke(sunucu);
        List<(string, string)> acilan = [];
        var vm = new YedekPenceresiViewModel("Satis", "Local", sunucu.Kos, (b, s) => acilan.Add((b, s))) { IlerlemeAraligiMs = 20 };
        return (vm, sunucu, acilan);
    }

    [Fact]
    public async Task Yukleme_dosya_onerir_gorunurlukleri_kurar_script_canli()
    {
        (YedekPenceresiViewModel vm, SahteSunucu s, _) = Kur();

        await vm.YukleAsync();

        Assert.NotNull(vm.Bilgi);
        Assert.StartsWith(@"C:\Data\Satis_tam_", vm.Dosya);           // yedek klasörü boş → veri klasörü (K4)
        Assert.EndsWith(".bak", vm.Dosya);
        Assert.False(vm.GunlukSecilebilir);                            // SIMPLE
        Assert.False(vm.SikistirmaGorunur);                            // Express/LocalDB
        Assert.True(vm.YedeklenebilirMi);
        Assert.Contains("tam yedeği yok", vm.SonTamYedekNotu);
        Assert.Contains("BACKUP DATABASE [Satis]", vm.Script);
        Assert.Contains("CHECKSUM", vm.Script);
        Assert.Contains("kurtarma modeli SIMPLE", vm.SunucuOzeti);
        Assert.Contains(s.Cagrilar, c => c.Db == "msdb");               // tarihçe msdb'de

        vm.KopyaOnly = true;
        vm.Checksum = false;
        vm.Aciklama = "deneme";
        Assert.Contains("COPY_ONLY", vm.Script);
        Assert.DoesNotContain("CHECKSUM", vm.Script);
        Assert.Contains("DESCRIPTION = N'deneme'", vm.Script);

        // tür değişince otomatik dosya adı ve ad türe uyar
        vm.Tur = YedekTuru.Fark;
        Assert.Contains("_fark_", vm.Dosya);
        Assert.Contains("DIFFERENTIAL", vm.Script);
        Assert.StartsWith("Satis fark yedek", vm.Ad);

        // günlük: SIMPLE'da engel bandı, düğme pasif
        vm.Tur = YedekTuru.Gunluk;
        Assert.Contains("SIMPLE", vm.Engel);
        Assert.False(vm.YedeklenebilirMi);
        vm.Tur = YedekTuru.Tam;
        Assert.Equal("", vm.Engel);

        // elle yazılmış yol tür değişiminde korunur
        vm.Dosya = @"D:\Benim\ozel.bak";
        vm.Tur = YedekTuru.Fark;
        Assert.Equal(@"D:\Benim\ozel.bak", vm.Dosya);
    }

    [Fact]
    public async Task Izin_yoksa_dugme_pasif_ve_neden()
    {
        (YedekPenceresiViewModel vm, _, _) = Kur(s => s.Izin = false);
        await vm.YukleAsync();
        Assert.False(vm.YedeklenebilirMi);
        Assert.Contains("izniniz yok", vm.Engel);
    }

    [Fact]
    public async Task Yedekle_ilerleme_yoklar_basarida_tarihce_tazelenir()
    {
        (YedekPenceresiViewModel vm, SahteSunucu s, _) = Kur();
        await vm.YukleAsync();

        Task kosu = vm.YedekleAsync();
        // sabit bekleme yük altında yetmiyordu (tam süitte 1 kez düştü) → yoklama görülene kadar bekle (en çok 3 sn)
        for (int i = 0; i < 150 && s.YoklamaSayisi < 2; i++)
            await Task.Delay(20);
        Assert.True(vm.Calisiyor);
        Assert.True(s.YoklamaSayisi >= 2, "ilerleme yoklanmadı");
        Assert.Equal(40, vm.Yuzde);
        Assert.Contains("%40", vm.IlerlemeMetni);
        Assert.Contains("0:41", vm.IlerlemeMetni);
        (string sql, string? db, int timeout) = s.Cagrilar.First(c => c.Sql.StartsWith("BACKUP"));
        Assert.Equal("Satis", db);                                      // DB bağlamı → DMV database_id eşleşir
        Assert.Equal(0, timeout);                                       // sınırsız

        s.Yedek.SetResult(new QueryResult { Basarili = true, Mesajlar = ["10 percent processed.", "BACKUP DATABASE successfully processed 100 pages"] });
        await kosu;

        Assert.False(vm.Calisiyor);
        Assert.True(vm.Bitti);
        Assert.True(vm.Basarili);
        Assert.Equal(100, vm.Yuzde);
        Assert.StartsWith("✔ Yedek alındı", vm.Durum);
        Assert.Contains("successfully", vm.Log);
        Assert.Single(vm.Tarihce);                                      // bitişte tarihçe yeniden okundu
        Assert.Contains("Son tam yedek", vm.SonTamYedekNotu);
    }

    [Fact]
    public async Task Hata_turkce_iptal_yarim_dosya_notu()
    {
        (YedekPenceresiViewModel vm, SahteSunucu s, _) = Kur();
        await vm.YukleAsync();
        Task kosu = vm.YedekleAsync();
        s.Yedek.SetResult(new QueryResult { Hata = new SqlHata("Cannot open backup device", 3201, 1, 16) });
        await kosu;
        Assert.False(vm.Basarili);
        Assert.Contains("açılamadı", vm.Durum);

        (vm, s, _) = Kur();
        await vm.YukleAsync();
        kosu = vm.YedekleAsync();
        await Task.Delay(40);
        vm.IptalCommand.Execute(null);
        await kosu;
        Assert.False(vm.Basarili);
        Assert.Contains("İptal edildi", vm.Durum);
        Assert.Contains("yarım dosya", vm.Durum);
    }

    [Fact]
    public async Task Script_olarak_al_sekmeye_gider()
    {
        (YedekPenceresiViewModel vm, _, List<(string, string)> acilan) = Kur();
        await vm.YukleAsync();
        vm.ScriptiAlCommand.Execute(null);
        (string baslik, string script) = Assert.Single(acilan);
        Assert.Equal("yedek-Satis.sql", baslik);
        Assert.Equal(vm.Script, script);
    }
}
