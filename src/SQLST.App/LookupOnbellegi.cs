using System.Collections.Concurrent;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App;

/// <summary>
/// v20-S21 m.10 fikir 2: tanım tablosu sözlüklerinin oturum önbelleği — "3 → Onaylandı" tooltip'i
/// için. Bir tanım tablosu YALNIZ BİR KEZ okunur (aynı anda birden çok hücre isterse tek yükleme
/// paylaşılır); sonraki tüm hücreler sorgusuz çözülür. Tavanı aşan (dev) tabloda sözlük <c>null</c>
/// kalır — o kolonda tooltip bir daha denenmez.
/// </summary>
internal sealed class LookupOnbellegi
{
    private readonly ConcurrentDictionary<string, Dictionary<string, string>?> _sozlukler =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _yuklemeler = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sözlük anahtarı: hangi veritabanında, hangi tablonun hangi iki kolonu.</summary>
    public static string SozlukAnahtari(string? veritabani, string sema, string tablo, string anahtarKolon, string aciklamaKolon)
        => $"{veritabani}|{sema}.{tablo}|{anahtarKolon}|{aciklamaKolon}";

    /// <summary>Sözlük yüklendi mi (başarısız/tavan aşımı da "yüklendi" sayılır — tekrar denenmez).</summary>
    public bool Yuklendi(string sozlukAnahtari) => _sozlukler.ContainsKey(sozlukAnahtari);

    /// <summary>Değerin açıklaması; sözlük yoksa/değer yoksa null.</summary>
    public string? Bul(string sozlukAnahtari, object? deger)
        => _sozlukler.TryGetValue(sozlukAnahtari, out Dictionary<string, string>? sozluk) && sozluk is not null
           && sozluk.TryGetValue(LookupSozlugu.Anahtar(deger), out string? aciklama)
            ? aciklama
            : null;

    /// <summary>Sözlüğün TAMAMI (m.10 fikir 10: WHERE'de değer önerisi) — yoksa boş liste.</summary>
    public IReadOnlyList<(string Kod, string Aciklama)> Tumu(string sozlukAnahtari)
        => _sozlukler.TryGetValue(sozlukAnahtari, out Dictionary<string, string>? sozluk) && sozluk is not null
            ? [.. sozluk.Select(p => (p.Key, p.Value))]
            : [];

    /// <summary>
    /// Sözlüğü (gerekiyorsa) yükler; aynı sözlük için eşzamanlı çağrılar TEK yüklemeyi paylaşır.
    /// <paramref name="getir"/> sorguyu koşar; null/hata dönerse sözlük "yok" olarak işaretlenir
    /// (kullanıcıya hata gösterilmez — tooltip sessizce çıkmaz).
    /// </summary>
    public Task YukleAsync(string sozlukAnahtari, Func<Task<ResultSetData?>> getir)
    {
        if (_sozlukler.ContainsKey(sozlukAnahtari))
            return Task.CompletedTask;

        return _yuklemeler.GetOrAdd(sozlukAnahtari, _ => YuklemeyiKosAsync(sozlukAnahtari, getir));
    }

    private async Task YuklemeyiKosAsync(string sozlukAnahtari, Func<Task<ResultSetData?>> getir)
    {
        Dictionary<string, string>? sozluk = null;
        try
        {
            if (await getir() is { } veri)
                sozluk = LookupSozlugu.Coz(veri);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            // Tanım tablosu okunamadı (yetki/ad değişikliği) — tooltip sessizce çıkmaz, iş akışı bozulmaz.
            Serilog.Log.Debug(ex, "Lookup sözlüğü yüklenemedi: {Anahtar}", sozlukAnahtari);
        }
        finally
        {
            _sozlukler[sozlukAnahtari] = sozluk; // başarısızlık da işaretlenir → tekrar denenmez
            _yuklemeler.TryRemove(sozlukAnahtari, out _);
        }
    }

    /// <summary>Bağlantı/şema değişince sözlükler bayatlar (aynı adlı tablo başka veriyi tutabilir).</summary>
    public void Temizle()
    {
        _sozlukler.Clear();
        _yuklemeler.Clear();
    }
}
