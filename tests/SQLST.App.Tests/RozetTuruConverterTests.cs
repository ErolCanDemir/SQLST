using SQLST.App.Converters;

namespace SQLST.App.Tests;

/// <summary>
/// v23-S21 sonuç rozetleri: ekranların ZATEN ürettiği durum metinleri doğru renk türüne düşer — denetim kaydı
/// (✔ / ✖ hata), FTS doldurma ve katalog durumları, Profiler olay türleri, REST karşılaştırma, FTS etkin.
/// </summary>
public class RozetTuruConverterTests
{
    [Theory]
    [InlineData("✔", "ok")]
    [InlineData("✖ hata", "hata")]
    [InlineData("Boşta (dolu)", "ok")]
    [InlineData("Boşta", "ok")]
    [InlineData("Tam doldurma sürüyor", "calis")]
    [InlineData("Artımlı doldurma sürüyor", "calis")]
    [InlineData("İzleme yayılıyor", "calis")]
    [InlineData("Durdurulmuş/azaltılmış", "uyari")]
    [InlineData("Disk dolu — duraklatıldı", "uyari")]
    [InlineData("Batch", "calis")]
    [InlineData("RPC", "gri")]
    [InlineData("Hata", "hata")]
    [InlineData("Deadlock", "hata")]
    [InlineData("✓ eşit", "ok")]
    [InlineData("≠ farklı", "uyari")]
    [InlineData("◀ yalnız API", "uyari")]
    [InlineData("— (devre dışı)", "uyari")]
    [InlineData("", "gri")]
    [InlineData(null, "gri")]
    public void Durum_metni_rozet_turune(string? metin, string beklenen)
        => Assert.Equal(beklenen, RozetTuruConverter.Tur(metin));
}
