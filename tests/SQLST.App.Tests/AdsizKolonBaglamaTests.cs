using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S4 saha turu-4 m.9 — kullanıcı: "bir tablonun count'u gelmiyor"
/// (<c>SELECT COUNT(*) FROM …</c> → grid "1 satır" diyor, kolon başlığı "(adsız)", HÜCRE BOŞ).
///
/// Şüpheli: adsız kolona verilen "(adsız)" yer tutucusu. WPF <c>PropertyPath</c> içinde
/// PARANTEZ özel anlamlıdır (eklenmiş özellik: <c>(Grid.Row)</c>) — düz yol olarak verilirse
/// bağlama sessizce düşer ve hücre boş görünür. Bu sınıf tahmini ÖLÇÜME çevirir: aynı yol
/// kurgusuyla gerçek bir bağlama kurulur ve sonucu okunur.
/// </summary>
public class AdsizKolonBaglamaTests
{
    private static readonly bool Etkin = OperatingSystem.IsWindows();

    /// <summary>MainWindow.GuvenliBindingYolu ile AYNI kural (kopya değil, davranışı ölçmek için).</summary>
    private static string GuvenliYol(string kolonAdi)
        => kolonAdi.All(c => char.IsLetterOrDigit(c) || c == '_')
            ? kolonAdi
            : "[" + kolonAdi.Replace("^", "^^").Replace("]", "^]") + "]";

    private static string Bagla(string kolonAdi, object deger)
    {
        string sonuc = "";
        StaOrtak.Sta().Invoke(() =>
        {
            var tablo = new DataTable();
            tablo.Columns.Add(kolonAdi, deger.GetType());
            tablo.Rows.Add(deger);

            var metin = new TextBlock { DataContext = tablo.DefaultView[0] };
            metin.SetBinding(TextBlock.TextProperty, new Binding(GuvenliYol(kolonAdi)));
            StaOrtak.Pump(TimeSpan.FromMilliseconds(50));
            sonuc = metin.Text;
        });
        return sonuc;
    }

    [Fact]
    public void Normal_kolon_adi_baglanir()
    {
        if (!Etkin)
            return;
        Assert.Equal("42", Bagla("Sayi", 42));
    }

    [Fact]
    public void Noktali_kolon_adi_baglanir()
    {
        if (!Etkin)
            return;
        Assert.Equal("42", Bagla("Musteri.Ad", 42)); // indexer kaçışının zaten çözdüğü hâl
    }

    /// <summary>
    /// ÖLÇÜM SONUCU: '(' ile BAŞLAYAN ad bağlanmıyor — WPF PropertyPath onu eklenmiş özellik
    /// sanıyor, indexer kaçışı da kurtarmıyor. Eski yer tutucumuz tam olarak böyleydi ("(adsız)"),
    /// COUNT(*) hücresinin boş görünmesinin nedeni buydu. Bu test kuralı KAYIT ALTINA alır:
    /// yer tutucu bir daha parantezle başlatılırsa aşağıdaki test kırmızı yanar.
    /// </summary>
    [Fact]
    public void Parantezle_BASLAYAN_ad_baglanamaz_WPF_sinirI()
    {
        if (!Etkin)
            return;
        Assert.Equal("", Bagla("(adsız)", 42));
    }

    /// <summary>Kullanılan yer tutucu bağlanmalı — m.9'un asıl iddiası.</summary>
    [Fact]
    public void Kullanilan_adsiz_yer_tutucusu_BAGLANIR()
    {
        if (!Etkin)
            return;
        Assert.Equal("42", Bagla(SQLST.Application.SonucBicimleyici.AdsizKolon, 42));
        Assert.False(SQLST.Application.SonucBicimleyici.AdsizKolon.StartsWith('('),
            "adsız kolon yer tutucusu '(' ile BAŞLAYAMAZ — WPF bağlaması sessizce düşer");
    }

    /// <summary>
    /// Sorun yalnız yer tutucuda değil: PARANTEZLİ GERÇEK kolon adları da aynı yoldan geçer.
    /// Bunlar uydurma değil — SQL yönetim panelindeki "Yavaş sorgular" listesi bu adları üretir
    /// (<c>AS [ort süre (ms)]</c>), yani o ekranın sayı kolonları da boş görünüyor olmalı.
    /// </summary>
    [Theory]
    [InlineData("ort süre (ms)")]
    [InlineData("en uzun (ms)")]
    [InlineData("ort mantıksal okuma")]
    [InlineData("Tutar $")]
    public void Ozel_karakterli_gercek_kolon_adlari_baglanir(string ad)
    {
        if (!Etkin)
            return;
        Assert.Equal("42", Bagla(ad, 42));
    }
}
