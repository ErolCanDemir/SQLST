using System.IO;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v20-S21 saha m.16 (2026-08-14) — "F5 çalışmıyor": F5/Ctrl+E/Ctrl+Enter/Ctrl+Shift+P
/// MainViewModel'deki TEK paylaşılan AsyncRelayCommand'a bağlıdır ve varsayılan davranış,
/// komutun Task'i uçuştayken CanExecute=false tutmaktır — A sekmesinde sorgu sürerken
/// (ya da asılı kalmışken) BAŞKA sekmede F5 ölüyordu; ▶ butonu sekmenin kendi komutuna
/// bağlı olduğundan çalışıyordu (kullanıcının "buton çalışıyor, F5 çalışmıyor" algısı).
/// Bu sınıf düzeltmeyi (AllowConcurrentExecutions=true) ve aynı sekmedeki çifte-koşu
/// bekçisinin hâlâ yerinde olduğunu sabitler.
/// </summary>
public class F5KisayoluTests : IDisposable
{
    private readonly string _klasor = Path.Combine(
        Path.GetTempPath(), "sqlst-f5-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly MainViewModel _vm;
    private readonly AsiliOturumFabrikasi _fabrika = new();

    public F5KisayoluTests()
    {
        Directory.CreateDirectory(_klasor);
        var depo = new YerelDepo(Path.Combine(_klasor, "test.db"));
        var koruyucu = new DpapiSecretProtector();
        var saglayici = new LehceSaglayici(koruyucu);
        var executor = new SahteExecutor();

        _vm = new MainViewModel(
            new BosSemaServisi(), new QueryService(), _fabrika,
            new SqliteSorguGecmisiDeposu(depo), new SqliteOturumDeposu(depo),
            new SqliteAyarDeposu(depo), new TeshisServisi(executor),
            new MongoTeshisServisi(koruyucu), new SqliteTarihceDeposu(depo),
            executor, saglayici, new SqliteSnippetDeposu(depo), koruyucu,
            new AsistanYonlendirici(koruyucu));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_klasor, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task F5_baska_sekmedeki_asili_sorguya_rehin_dusmez()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel a = _vm.SekmeAc("a.sql", "SELECT 1 -- ASILI", null);
        SorguSekmesiViewModel b = _vm.SekmeAc("b.sql", "SELECT 2", null);
        // AvalonEdit TextDocument thread-affine — testte Belge yerine delege (await sonrası
        // xunit farklı havuz thread'ine düşer, Belge.Text erişimi VerifyAccess'te patlar).
        a.MetinSaglayici = () => "SELECT 1 -- ASILI";
        b.MetinSaglayici = () => "SELECT 2";

        _vm.SeciliSekme = a;
        Task f5 = _vm.SeciliyiCalistirCommand.ExecuteAsync(null); // A: hiç dönmeyen sorgu

        for (int i = 0; i < 100 && !a.CalisiyorMu; i++)
            await Task.Delay(20);
        Assert.True(a.CalisiyorMu, "asılı sorgu koşuya hiç girmedi");

        // Kullanıcı B sekmesine geçip F5'e basar — kısayol CANLI olmalı ve gerçekten koşmalı.
        _vm.SeciliSekme = b;
        Assert.True(_vm.SeciliyiCalistirCommand.CanExecute(null),
            "F5 başka sekmede ölü — paylaşılan komut A'nın asılı Task'ine rehin");
        await _vm.SeciliyiCalistirCommand.ExecuteAsync(null);
        Assert.Equal(SekmeDurumu.Tamamlandi, b.Durum);

        _ = f5; // asılı koşu kasıtlı olarak tamamlanmaz (sahte sonsuz bekler)
    }

    [Fact]
    public async Task Ayni_sekmede_cifte_F5_ikinci_kosu_baslatmaz()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel a = _vm.SekmeAc("a.sql", "SELECT 1 -- ASILI", null);
        a.MetinSaglayici = () => "SELECT 1 -- ASILI"; // Belge thread-affine — delege kullan
        _vm.SeciliSekme = a;

        // NOT: bir F5, sorgudan ÖNCE oturuma NOLOCK SET'i de gönderir (kirli okuma açık) —
        //      bu yüzden ham çağrı sayısı değil, ASIL SORGU ("ASILI") sayısı ölçülür.
        Task ilk = _vm.SeciliyiCalistirCommand.ExecuteAsync(null);
        // Koşu Task.Run içinde sahteye ulaşana dek bekle (Durum senkron atanır ama oturum
        // çağrısı havuzda — sayacı beklemeden assert etmek yarışa girer).
        for (int i = 0; i < 100 && _fabrika.Kosulanlar.Count(s => s.Contains("ASILI")) == 0; i++)
            await Task.Delay(20);
        Assert.Equal(1, _fabrika.Kosulanlar.Count(s => s.Contains("ASILI")));
        Assert.True(a.CalisiyorMu);

        // İkinci F5: komut artık eşzamanlıya izin verir ama sekmenin CalisiyorMu bekçisi
        // çifte koşuyu keser — asıl sorgu bir daha KOŞMAMALI.
        await _vm.SeciliyiCalistirCommand.ExecuteAsync(null);
        await Task.Delay(200); // olası (hatalı) ikinci koşuya sahteye ulaşma fırsatı tanı
        Assert.True(_fabrika.Kosulanlar.Count(s => s.Contains("ASILI")) == 1,
            $"asılı sorgu bir kez koşmalıydı: [{string.Join(" | ", _fabrika.Kosulanlar)}]");

        _ = ilk;
    }

    /// <summary>"ASILI" işaretli SQL'de <see cref="AsiliKilit"/> açılana dek dönmeyen (iptali de
    /// dinlemeyen), diğerlerinde anında başaran sahte oturum. Olusturma sayısı Durdur'un oturumu
    /// gerçekten düşürdüğünü kanıtlamak için sayılır.</summary>
    private sealed class AsiliOturumFabrikasi : IOturumFabrikasi
    {
        public int KosuSayisi;
        public int OlusturSayisi;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Kosulanlar = new();
        public readonly TaskCompletionSource AsiliKilit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDbOturum Olustur(ConnectionProfile profil)
        {
            Interlocked.Increment(ref OlusturSayisi);
            return new AsiliOturum(this, profil);
        }

        private sealed class AsiliOturum(AsiliOturumFabrikasi sahip, ConnectionProfile profil) : IDbOturum
        {
            public ConnectionProfile Profil => profil;

            public async Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
            {
                Interlocked.Increment(ref sahip.KosuSayisi);
                sahip.Kosulanlar.Enqueue(sql);
                if (sql.Contains("ASILI"))
                    await sahip.AsiliKilit.Task; // iptali dinlemez — kilit açılana dek asılı
                return new QueryResult { Basarili = true };
            }

            public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
                => Task.FromResult(IslemDurumu.Yok);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // ── v20-S21 canlı bulgu (2026-08-14, ara madde): Durdur ANINDA kesmeli ─────────────

    [Fact]
    public async Task Durdur_aninda_keser_yeni_sorgu_taze_oturumla_kosar()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel a = _vm.SekmeAc("a.sql", "SELECT 1 -- ASILI", null);
        string sql = "SELECT 1 -- ASILI";
        a.MetinSaglayici = () => sql; // Belge thread-affine — delege kullan
        _vm.SeciliSekme = a;
        Task f5 = _vm.SeciliyiCalistirCommand.ExecuteAsync(null);
        for (int i = 0; i < 100 && _fabrika.Kosulanlar.Count(s => s.Contains("ASILI")) == 0; i++)
            await Task.Delay(20);
        Assert.True(a.CalisiyorMu);

        a.IptalCommand.Execute(null); // ■ Durdur

        // ANINDA boşalır — sunucudan yanıt beklenmez.
        Assert.False(a.CalisiyorMu);
        Assert.Equal(SekmeDurumu.IptalEdildi, a.Durum);
        Assert.Contains("Durduruldu", a.Mesajlar);

        // Aynı sekmede yeni sorgu: asılı oturumun kilidine TAKILMADAN, taze oturumda koşar
        // (eski davranışta SemaphoreSlim'e kuyruklanıp sonsuza dek bekliyordu).
        sql = "SELECT 2";
        await a.CalistirAsync();
        Assert.Equal(SekmeDurumu.Tamamlandi, a.Durum);
        Assert.Equal(2, _fabrika.OlusturSayisi); // Durdur oturumu düşürdü → ikinci oturum açıldı

        _ = f5; // bayat koşu kilit açılmadıkça asılı kalır (kasıtlı)
    }

    [Fact]
    public async Task Geciken_bayat_kosu_yeni_sonucun_ustune_yazamaz()
    {
        await _vm.ProfilYukleAsync(Profiller.Yap());
        SorguSekmesiViewModel a = _vm.SekmeAc("a.sql", "SELECT 1 -- ASILI", null);
        string sql = "SELECT 1 -- ASILI";
        a.MetinSaglayici = () => sql; // Belge thread-affine — delege kullan
        _vm.SeciliSekme = a;
        Task f5 = _vm.SeciliyiCalistirCommand.ExecuteAsync(null);
        for (int i = 0; i < 100 && _fabrika.Kosulanlar.Count(s => s.Contains("ASILI")) == 0; i++)
            await Task.Delay(20);

        a.IptalCommand.Execute(null);
        sql = "SELECT 2";
        await a.CalistirAsync();
        Assert.Equal(SekmeDurumu.Tamamlandi, a.Durum);

        _fabrika.AsiliKilit.SetResult(); // asılı koşu ŞİMDİ dönüyor — ama nesli bayat
        await f5;                        // bayat koşu tamamlansın (geçmişe iptal olarak düşer)

        Assert.Equal(SekmeDurumu.Tamamlandi, a.Durum); // yeni sonucun durumu EZİLMEDİ
        Assert.DoesNotContain("Beklenmeyen hata", a.Mesajlar); // bayat istisna UI'ya sızmadı
    }

    /// <summary>Şema okumayan sahte — bu testler yalnız kısayol/komut davranışıyla ilgilenir.</summary>
    private sealed class BosSemaServisi : ISchemaService
    {
        public Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VeritabaniBilgisi>>([]);

        public Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
            => Task.FromResult(new SemaOnbellegi { Nesneler = [], YuklenmeZamaniUtc = DateTime.UtcNow });

        public Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<DuzenlemeMetasi> DuzenlemeMetaAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct)
            => Task.FromResult(new DuzenlemeMetasi("db", "dbo", nesne.Ad, []));

        public Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<YabanciAnahtar>>([]);
    }
}
