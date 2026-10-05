using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// <b>Bu test bir davranışı değil, bir VERİ KAYBINI önler.</b>
///
/// Ürün adı 2026-07-20'de MiniSSMS → SQLST oldu ve 197 dosyada 643 geçiş değiştirildi.
/// <see cref="DpapiSecretProtector"/> içindeki entropy sabiti (<c>"MiniSSMS.v1"</c>) o
/// taramada değiştirilmemesi gereken tek dizeydi: DPAPI, şifrelerken kullanılan entropy ile
/// çözerken kullanılanın aynı olmasını şart koşar. Değişseydi kullanıcının kayıtlı bütün
/// bağlantı parolaları <b>kalıcı olarak</b> çözülemez olurdu — geri dönüşü yok.
///
/// Sabitin adı artık ürün adıyla uyuşmadığı için ilerideki bir "tutarlılık" düzeltmesinde
/// gözden geçme riski YÜKSEK. Bu test o düzeltmeyi derleme anında değil, test anında yakalar
/// ve nedenini söyler.
/// </summary>
public class DpapiEntropyTests
{
    /// <summary>
    /// 0.5.0 ve öncesinde üretilmiş gerçek bir şifreli değer. Entropy değişirse bu değer
    /// çözülemez ve test düşer — asıl kilit budur.
    ///
    /// <b>Neden sabit bir blob değil de round-trip?</b> DPAPI çıktısı kullanıcıya/makineye
    /// özgüdür; başka makinede üretilmiş bir blob burada zaten çözülemezdi. Bu yüzden test
    /// entropy'yi DOĞRUDAN okur (aşağıda) ve ayrıca round-trip'in çalıştığını doğrular.
    /// </summary>
    [Fact]
    public void Entropy_sabiti_DEGISMEMELI()
    {
        // Yansımayla okunur: sabit private'tır ve öyle kalmalı (dışarıdan değiştirilemesin).
        object? deger = typeof(DpapiSecretProtector)
            .GetField("Entropy", System.Reflection.BindingFlags.NonPublic
                               | System.Reflection.BindingFlags.Static)
            ?.GetValue(null);

        Assert.True(deger is byte[], "Entropy alanı bulunamadı — adı mı değişti?");
        string entropy = System.Text.Encoding.UTF8.GetString((byte[])deger!);

        Assert.Equal("MiniSSMS.v1", entropy);

        // Not: burada gördüğün "MiniSSMS" bir unutulmuş yeniden adlandırma DEĞİLDİR.
        // Ürün adı SQLST'dir; bu dize bilerek korunmuştur. Değiştirmek isteyen önce
        // eskisiyle çözüp yenisiyle yeniden şifreleyen bir GÖÇ yazmalıdır.
    }

    /// <summary>Şifrele → çöz turu çalışmalı; Türkçe karakter ve boşluk dahil.</summary>
    [Theory]
    [InlineData("basit123")]
    [InlineData("şifreÇĞİÖÜ")]
    [InlineData("içinde boşluk ve 'tırnak' var")]
    public void Sifrele_ve_coz_turu_ayni_metni_verir(string parola)
    {
        ISecretProtector koruyucu = new DpapiSecretProtector();

        string sifreli = koruyucu.Sifrele(parola);

        Assert.NotEqual(parola, sifreli);                 // düz metin saklanmıyor
        Assert.Equal(parola, koruyucu.Coz(sifreli));
    }
}
