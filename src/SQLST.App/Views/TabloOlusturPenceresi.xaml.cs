using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Views;

/// <summary>
/// Görsel tasarımcıyı "Oluştur ve Aktar" moduna geçiren veri paketi (v20-S12, REST "Tabloya kaydet"):
/// dolu olduğunda pencere script'i sekmeye açmak yerine tabloyu OLUŞTURUR (onayınızla) ve
/// <paramref name="Kaynak"/> satırlarını AYNI bağlantıda aktarır — Excel/TXT içe aktarmanın CREATE+INSERT
/// hattını (<see cref="DosyaAktarimServisi"/>) aynen kullanır. null ise pencere klasik "script üret"
/// davranışındadır. <paramref name="Kaynak"/> kolon envanteri + (bellekteki) satırlardır.
/// </summary>
public sealed record TabloVeriAktarimi(
    ConnectionProfile Hedef,
    string? HedefVeritabani,
    DosyaOnizleme Kaynak,
    DosyaAktarimServisi Aktarim);

/// <summary>
/// "Tablo oluştur" sihirbazı (v8-S1): şema + tablo adı + kolonlar girilir, <see cref="TabloOlusturucu"/>
/// ile <c>CREATE TABLE</c> üretilip yeni bir sorgu sekmesinde açılır (çalıştırmaz — proje kuralı).
/// MSSQL kapsamlı; çağıran bu pencereyi yalnız SQL Server'da açar.
/// v20-S12: <see cref="TabloVeriAktarimi"/> verilirse aynı görsel tasarımcı, REST JSON yanıtından
/// çıkarılmış kolonlarla ÖN-DOLU açılır ve "Oluştur ve Aktar" tabloyu oluşturup satırları aktarır.
/// </summary>
public partial class TabloOlusturPenceresi : Window
{
    private readonly ILehce _lehce;
    private readonly Action<string, string> _sekmeyeAc;
    private readonly TabloVeriAktarimi? _veri;
    private readonly Func<string, Task<AsistanCevabi>>? _aiSemaOner; // v20-S13 madde 6: CREATE TABLE öner

    public TabloOlusturPenceresi(
        ILehce lehce, string sema, Action<string, string> sekmeyeAc, TabloVeriAktarimi? veri = null,
        Func<string, Task<AsistanCevabi>>? aiSemaOner = null)
    {
        _lehce = lehce;
        _sekmeyeAc = sekmeyeAc;
        _veri = veri;
        _aiSemaOner = aiSemaOner;

        InitializeComponent();

        SemaKutusu.Text = sema;
        KolonGrid.ItemsSource = Kolonlar;

        if (veri is null)
        {
            Kolonlar.Add(new KolonGirdisi { Ad = "Id", Tip = "int", PkMi = true, IdentityMi = true });
            return;
        }

        // JSON→tablo modu: yalnız CREATE anlamlı (var olana veri aktarımı bu yolun konusu değil).
        KipPaneli.Visibility = Visibility.Collapsed;
        KipOlustur.IsChecked = true;
        UretDugmesi.Content = "▶ Oluştur ve Aktar";
        UretDugmesi.ToolTip = "Tabloyu OLUŞTURUR (script'i önce onaylatır) ve JSON satırlarını aktarır.";
        Title = "SQLST — JSON yanıtından tablo oluştur";

        // Çıkarılan kolonlar önerilen SQL tipleriyle doldurulur (kullanıcı düzenler); HEP NULL izinli —
        // örneklem yanılabilir, NOT NULL aktarımı yarıda kırardı (DosyaTipiEslemesi ile aynı ilke).
        foreach (DosyaKolonu k in veri.Kaynak.Kolonlar)
        {
            Kolonlar.Add(new KolonGirdisi
            {
                Ad = k.Ad,
                Tip = DosyaTipiEslemesi.SqlTipi(lehce.MotorId, k),
                NullOlabilir = true,
                KaynakAd = k.Ad, // hedef ad değişse de veri eşlemesi orijinal ada bağlı kalır
            });
        }

        if (_aiSemaOner is not null)
            AiOnerDugmesi.Visibility = Visibility.Visible; // madde 6: AI şema önerisi (yalnız JSON→tablo modu)
    }

    public ObservableCollection<KolonGirdisi> Kolonlar { get; } = [];

    private void KolonEkle_Click(object sender, RoutedEventArgs e) => Kolonlar.Add(new KolonGirdisi());

    private void KolonSil_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KolonGirdisi k)
            Kolonlar.Remove(k);
    }

    private async void Uret_Click(object sender, RoutedEventArgs e)
    {
        // Grid hücresindeki düzenleme henüz commit edilmemiş olabilir — zorla.
        KolonGrid.CommitEdit(DataGridEditingUnit.Row, true);

        // JSON→tablo modu (v20-S12): script'i sekmeye açmak yerine tabloyu oluştur + satırları aktar.
        if (_veri is { } veri)
        {
            await OlusturVeAktarAsync(veri);
            return;
        }

        TabloModu mod = KipEkle.IsChecked == true ? TabloModu.Ekle : TabloModu.Olustur;
        var tablo = new YeniTablo(
            SemaKutusu.Text?.Trim() is { Length: > 0 } s ? s : null,
            AdKutusu.Text?.Trim() ?? "",
            [.. Kolonlar.Select(k => k.Yap())],
            mod);

        (string? sql, string? hata) = TabloOlusturucu.Uret(tablo, _lehce);
        if (sql is null)
        {
            HataMetni.Text = $"⚠ {hata}";
            return;
        }

        string on = mod == TabloModu.Ekle ? "ALTER" : "CREATE";
        _sekmeyeAc($"{on} {tablo.Ad}.sql", sql);
        Close();
    }

    /// <summary>
    /// JSON→tablo modu (v20-S12): grid'deki kolonlardan <c>CREATE TABLE</c> üretir, ONAYLATIR
    /// (v8/v13 ilkesi: hiçbir DDL kendiliğinden koşmaz), sonra <see cref="DosyaAktarimServisi"/> ile
    /// tabloyu oluşturur (OnceDdl) ve JSON satırlarını AYNI bağlantıda aktarır. Eşleme her grid
    /// satırının KAYNAK adına (<see cref="KolonGirdisi.KaynakAd"/>) dayanır — kullanıcı hedef adı/tipi
    /// değiştirse de veri doğru kolona gider; elle eklenen (kaynağı olmayan) ve IDENTITY kolonlar
    /// yalnız CREATE'e girer, INSERT'e girmez (identity motorca üretilir).
    /// </summary>
    private async Task OlusturVeAktarAsync(TabloVeriAktarimi veri)
    {
        var tablo = new YeniTablo(
            SemaKutusu.Text?.Trim() is { Length: > 0 } s ? s : null,
            AdKutusu.Text?.Trim() ?? "",
            [.. Kolonlar.Select(k => k.Yap())],
            TabloModu.Olustur);

        (string? sql, string? hata) = TabloOlusturucu.Uret(tablo, _lehce);
        if (sql is null)
        {
            HataMetni.Text = $"⚠ {hata}";
            return;
        }

        // Hedef kolon = grid Ad; kaynak = orijinal JSON alanı. Kaynağı olmayan/identity kolon INSERT dışı.
        var kaynakAdlar = new HashSet<string>(
            veri.Kaynak.Kolonlar.Select(c => c.Ad), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<AktarimEslesmesi> eslesmeler = [.. Kolonlar
            .Where(k => !k.IdentityMi && k.KaynakAd is { Length: > 0 } ka && kaynakAdlar.Contains(ka))
            .Select(k => new AktarimEslesmesi(k.KaynakAd!, k.Ad.Trim()))];
        if (eslesmeler.Count == 0)
        {
            HataMetni.Text = "⚠ Aktarılacak en az bir kaynak kolon gerekli (bir JSON alanı eşlenmeli).";
            return;
        }

        if (new DdlOnayPenceresi(sql) { Owner = this }.ShowDialog() != true)
            return;

        // INSERT hedefi tırnaklı tam ad — Türkçe/boşluklu adlar da güvenli (v13-S4 ile aynı).
        string hedefTabloAdi = string.IsNullOrWhiteSpace(tablo.Sema)
            ? _lehce.TirnaklaTanimlayici(tablo.Ad)
            : _lehce.TamAdYaz(tablo.Sema, tablo.Ad);

        // KulturAdi=null → Invariant: JSON sayıları zaten .NET tipiyle geldi, tarih/metin string kaldı.
        var istek = new DosyaAktarimIstegi(
            veri.Hedef, veri.HedefVeritabani, hedefTabloAdi, eslesmeler, veri.Kaynak.Kolonlar,
            KulturAdi: null, OnceTemizle: false, AktarimHataPolitikasi.IlkHatadaDur,
            AktarimYazmaKipi.YalnizEkle, AnahtarKolonlar: null, OnceDdl: sql);

        UretDugmesi.IsEnabled = false;
        HataMetni.Text = "Tablo oluşturuluyor ve satırlar aktarılıyor…";
        try
        {
            // Task.Run: CREATE + INSERT senkron ADO IO'su UI thread'ini dondurmasın (import ile aynı).
            AktarimSonucu sonuc = await Task.Run(() =>
                veri.Aktarim.AktarAsync(istek, veri.Kaynak.Satirlar, null, CancellationToken.None));

            if (sonuc.Basarili)
            {
                Iletisim.Bilgi(this, "SQLST — Tabloya kaydet", $"✔ '{tablo.Ad}' oluşturuldu",
                    $"{sonuc.Yazilan:N0} satır aktarıldı ({sonuc.Sure.TotalSeconds:F1} sn).");
                Close();
            }
            else
            {
                HataMetni.Text = $"⚠ {sonuc.Hata}";
            }
        }
        catch (Exception ex)
        {
            HataMetni.Text = $"⚠ {ex.Message}";
        }
        finally
        {
            UretDugmesi.IsEnabled = true;
        }
    }

    /// <summary>
    /// v20-S13 madde 6: örnek veriden (kolon adları + ilk satırlar) AI'a uygun SQL tipi + PK + null
    /// önerttir (<see cref="AiSemaAyristirici"/>); öneriyi MEVCUT kolonlara ADA GÖRE eşleyip Tip/PK/NULL'u
    /// günceller — kolon adları ve <see cref="KolonGirdisi.KaynakAd"/> (veri eşlemesi) KORUNUR. Tablo adı
    /// boşsa AI önerisini yazar. AI hatası ya da eşleşmeyen (uydurulmuş) kolon güvenle atlanır.
    /// </summary>
    private async void AiSemaOner_Click(object sender, RoutedEventArgs e)
    {
        if (_aiSemaOner is null || _veri is not { } veri)
            return;

        KolonGrid.CommitEdit(DataGridEditingUnit.Row, true);
        AiOnerDugmesi.IsEnabled = false;
        HataMetni.Text = "AI şema önerisi hazırlanıyor…";
        try
        {
            AsistanCevabi cevap = await _aiSemaOner(VeriOzeti(veri.Kaynak));
            if (!cevap.Basarili)
            {
                HataMetni.Text = $"AI: {cevap.Metin}";
                return;
            }

            AiSema? sema = AiSemaAyristirici.Ayristir(cevap.Metin, out string? hata);
            if (sema is null)
            {
                HataMetni.Text = $"⚠ {hata}";
                return;
            }

            int uygulanan = 0;
            foreach (AiSemaKolonu oneri in sema.Kolonlar)
            {
                KolonGirdisi? mevcut = Kolonlar.FirstOrDefault(
                    k => k.Ad.Equals(oneri.Ad, StringComparison.OrdinalIgnoreCase));
                if (mevcut is null)
                    continue; // AI yeni ad uydurmuşsa yok say — veri eşlemesini bozmayalım
                mevcut.Tip = oneri.Tip;
                mevcut.PkMi = oneri.Pk;
                mevcut.NullOlabilir = oneri.NullOlabilir;
                uygulanan++;
            }

            if (sema.TabloAdi is { Length: > 0 } tabloAdi && string.IsNullOrWhiteSpace(AdKutusu.Text))
                AdKutusu.Text = tabloAdi;

            HataMetni.Text = uygulanan > 0
                ? $"AI önerisi uygulandı — {uygulanan} kolon (tip/PK/null) güncellendi; adlar korundu."
                : "AI önerisi mevcut kolonlarla eşleşmedi.";
        }
        catch (Exception ex) { HataMetni.Text = $"AI hatası: {ex.Message}"; }
        finally { AiOnerDugmesi.IsEnabled = true; }
    }

    /// <summary>AI'a gidecek kompakt veri özeti: kolon adları + ilk 5 örnek satır (InvariantCulture).</summary>
    private static string VeriOzeti(DosyaOnizleme onizleme)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Kolonlar: ").AppendLine(string.Join(", ", onizleme.Kolonlar.Select(k => k.Ad)));
        sb.AppendLine("Örnek satırlar:");
        foreach (object?[] satir in onizleme.Satirlar.Take(5))
            sb.AppendLine(string.Join(" | ", satir.Select(h =>
                h is null or DBNull ? "" : Convert.ToString(h, System.Globalization.CultureInfo.InvariantCulture) ?? "")));
        return sb.ToString();
    }

    private void Iptal_Click(object sender, RoutedEventArgs e) => Close();
}
