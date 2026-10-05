using SQLST.App;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// v20-S21 m.10 fikir 2 — lookup sözlüğü önbelleği: tooltip HER hücrede tetikleneceği için tanım
/// tablosu YALNIZ BİR KEZ okunmalı. Bu sınıf sözü sabitler: ikinci hücre sorgu ATMAZ, eşzamanlı
/// istekler tek yüklemeyi paylaşır, hata/tavan aşımı sessizce yutulur ve tekrar denenmez.
/// </summary>
public class LookupOnbellegiTests
{
    private const string A = "db|dbo.Durum|Id|Ad";

    private static ResultSetData Set(params object?[][] satirlar) => new()
    {
        Kolonlar = [new KolonBilgisi("Id", "int"), new KolonBilgisi("Ad", "nvarchar")],
        Satirlar = satirlar,
    };

    [Fact]
    public async Task Tanim_tablosu_bir_kez_okunur_sonraki_hucreler_sorgusuz()
    {
        int sorguSayisi = 0;
        var onbellek = new LookupOnbellegi();
        Task<ResultSetData?> Getir()
        {
            Interlocked.Increment(ref sorguSayisi);
            return Task.FromResult<ResultSetData?>(Set([1, "Beklemede"], [3, "Onaylandı"]));
        }

        await onbellek.YukleAsync(A, Getir);          // ilk hücre
        Assert.Equal("Onaylandı", onbellek.Bul(A, 3));

        await onbellek.YukleAsync(A, Getir);          // sonraki hücreler
        await onbellek.YukleAsync(A, Getir);
        Assert.Equal(1, sorguSayisi);                  // ⚠ asıl vaat: tek sorgu
        Assert.Equal("Beklemede", onbellek.Bul(A, "1"));  // metin/sayı fark etmez
        Assert.Null(onbellek.Bul(A, 99));              // karşılığı yok → tooltip yok
    }

    [Fact]
    public async Task Escszamanli_istekler_tek_yuklemeyi_paylasir()
    {
        int sorguSayisi = 0;
        using var kapi = new SemaphoreSlim(0);
        var onbellek = new LookupOnbellegi();
        async Task<ResultSetData?> Getir()
        {
            Interlocked.Increment(ref sorguSayisi);
            await kapi.WaitAsync();                    // yükleme sürerken ikinci istek gelsin
            return Set([1, "Bir"]);
        }

        Task ilk = onbellek.YukleAsync(A, Getir);
        Task ikinci = onbellek.YukleAsync(A, Getir);
        kapi.Release();
        await Task.WhenAll(ilk, ikinci);

        Assert.Equal(1, sorguSayisi);
        Assert.Equal("Bir", onbellek.Bul(A, 1));
    }

    [Fact]
    public async Task Hata_sessizce_yutulur_ve_tekrar_denenmez()
    {
        int sorguSayisi = 0;
        var onbellek = new LookupOnbellegi();
        Task<ResultSetData?> Patla()
        {
            Interlocked.Increment(ref sorguSayisi);
            throw new InvalidOperationException("yetki yok");
        }

        await onbellek.YukleAsync(A, Patla);           // fırlatmamalı — tooltip işi bölmez
        Assert.Null(onbellek.Bul(A, 1));
        Assert.True(onbellek.Yuklendi(A));

        await onbellek.YukleAsync(A, Patla);
        Assert.Equal(1, sorguSayisi);                  // başarısızlık da işaretlendi → tekrar yok
    }

    [Fact]
    public async Task Temizle_baglanti_degisiminde_sozlukleri_atar()
    {
        var onbellek = new LookupOnbellegi();
        await onbellek.YukleAsync(A, () => Task.FromResult<ResultSetData?>(Set([1, "Bir"])));
        Assert.True(onbellek.Yuklendi(A));

        onbellek.Temizle();

        Assert.False(onbellek.Yuklendi(A));            // başka bağlantıda aynı ad başka veri olabilir
        Assert.Null(onbellek.Bul(A, 1));
    }
}
