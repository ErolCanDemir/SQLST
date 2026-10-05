using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// Kullanıcı isteği (2026-07-19): "Yönetim paneli altında bulunan alanlara bu alanlardan
/// hangi bilgilerin takip edileceği not olarak eklensin."
///
/// <b>Neden test?</b> <see cref="TeshisBolumu.Takip"/> zorunlu bir kurucu parametresidir,
/// yani derleyici notu UNUTMAYI zaten engeller. Ama derleyici <c>""</c> ya da <c>"—"</c>
/// yazmayı engellemez: alan doldurulmuş görünür, kullanıcı hiçbir şey öğrenmez. Bu testler
/// notun GERÇEKTEN bir şey söylediğini savunur.
///
/// Bilerek İÇERİK iddiası da var (eşik/kolon adı geçmeli): "boş değil" testi geçen bir
/// "TAKİP: bu bölümü takip edin" notu kullanıcıya sıfır fayda sağlardı.
/// </summary>
public class PanelTakipNotlariTests
{
    private static readonly ISecretProtector Koruyucu = new DpapiSecretProtector();

    public static TheoryData<string, ILehce> Lehceler => new()
    {
        { "PostgreSQL", new PostgresLehcesi(Koruyucu) },
        { "MySQL", new MySqlLehcesi(Koruyucu) },
        { "Oracle", new OracleLehcesi(Koruyucu) },
    };

    /// <summary>Panelli her motorun HER bölümünde anlamlı bir takip notu olmalı.</summary>
    [Theory]
    [MemberData(nameof(Lehceler))]
    public void Her_bolumun_takip_notu_vardir_ve_anlamlidir(string motor, ILehce lehce)
    {
        Assert.NotEmpty(lehce.TeshisBolumleri);

        foreach (TeshisBolumu bolum in lehce.TeshisBolumleri)
        {
            string yer = $"{motor} · '{bolum.Baslik}'";

            Assert.False(string.IsNullOrWhiteSpace(bolum.Takip), $"{yer}: takip notu boş");

            // Yer tutucu bir not ("—", "yok", "TODO") boş nottan daha zararlıdır: doldurulmuş
            // görünür ama hiçbir şey söylemez, dolayısıyla kimse gelip düzeltmez.
            Assert.True(bolum.Takip.Length >= 80,
                $"{yer}: takip notu çok kısa ({bolum.Takip.Length} karakter) — hangi kolona "
                + "bakılacağını ve eşiği söylemesi gerekir");

            Assert.StartsWith("TAKİP", bolum.Takip, StringComparison.Ordinal);

            // Not, bölümün SORGUSUNDAKİ en az bir kolon/kavram adını anmalı: kullanıcıyı
            // ekranda göreceği somut bir sütuna yönlendirmeyen not, genel geçer laftır.
            //
            // KAPSAM DIŞI: kolon listesi olmayan bölümler (MySQL "Sunucu durumu" =
            // "SHOW GLOBAL STATUS"). Orada sorgu metninde eşleşecek kolon adı YOKTUR —
            // dönen satırların kendisi sayaç adlarıdır. İddiayı bu bölüme zorlamak, testi
            // geçirmek için nota sorgudan kelime serpmeye iterdi; testin amacı notu
            // iyileştirmek, kendini tatmin etmek değil.
            if (bolum.Sorgu.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains(bolum.Takip.Split([' ', ',', '.', '(', ')', ';', ':', '/'],
                                    StringSplitOptions.RemoveEmptyEntries),
                    kelime => kelime.Length > 4
                           && bolum.Sorgu.Contains(kelime, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    /// <summary>
    /// Not, açıklamanın kopyası olmamalı. İkisi FARKLI soruya cevap verir: Açıklama
    /// "bu bölüm nedir", Takip "hangi kolona bakayım, ne zaman kötüdür, ne yapayım".
    /// </summary>
    [Theory]
    [MemberData(nameof(Lehceler))]
    public void Takip_notu_aciklamanin_kopyasi_degildir(string motor, ILehce lehce)
    {
        foreach (TeshisBolumu bolum in lehce.TeshisBolumleri)
            Assert.False(bolum.Aciklama == bolum.Takip,
                $"{motor} · '{bolum.Baslik}': takip notu açıklamanın aynısı");
    }
}
