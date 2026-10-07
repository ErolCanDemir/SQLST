using System.Data;
using System.Windows;
using System.Windows.Controls;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// ⏪ Geri Al Paketleri penceresi (V15-S4, BF-1 "uçuş kaydedici"): COMMIT edilen UPDATE/DELETE'lerin
/// etkilenen satırlarının ESKİ hali listelenir; seçilen paketten <see cref="TersDmlUretici"/> ile
/// ters DML üretilip yeni sekmede açılır (çalıştırmaz — Güvenli Yazma raylarında kullanıcı koşar).
/// </summary>
public partial class GeriAlPenceresi : Window
{
    private readonly IGeriAlDeposu _depo;
    private readonly Action<string, string, string?> _sekmeAc; // (başlık, sql, db) — MainViewModel.SekmeAc
    private GeriAlPaketi? _seciliTam;                            // satırlarıyla birlikte tam paket

    /// <summary>Liste öğesi görünümü: yerel saat + satır bilgisi (SatirlarJson listede boştur).</summary>
    private sealed record PaketGorunumu(GeriAlPaketi Paket)
    {
        public long Id => Paket.Id;
        public string Fiil => Paket.Fiil;
        public string Tablo => Paket.Tablo;
        public string Veritabani => Paket.Veritabani;
        public int SatirSayisi => Paket.SatirSayisi;
        public string YerelZaman => Paket.TarihUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    }

    public GeriAlPenceresi(IGeriAlDeposu depo, Action<string, string, string?> sekmeAc)
    {
        _depo = depo;
        _sekmeAc = sekmeAc;
        InitializeComponent();
        Loaded += async (_, _) => await ListeyiYukleAsync();
    }

    private async Task ListeyiYukleAsync()
    {
        try
        {
            IReadOnlyList<GeriAlPaketi> paketler = await _depo.ListeleAsync();
            PaketListesi.ItemsSource = paketler.Select(p => new PaketGorunumu(p)).ToList();
            Durum.Text = paketler.Count == 0
                ? "Henüz geri al paketi yok — Güvenli Yazma ile COMMIT ettiğiniz UPDATE/DELETE'ler burada birikir."
                : $"{paketler.Count} paket.";
        }
        catch (Exception ex)
        {
            Durum.Text = $"Paketler yüklenemedi: {ex.Message}";
        }
    }

    private async void Paket_Secildi(object sender, SelectionChangedEventArgs e)
    {
        _seciliTam = null;
        DoguranSql.Text = "";
        EskiHalGrid.ItemsSource = null;
        if (PaketListesi.SelectedItem is not PaketGorunumu secili)
            return;

        try
        {
            _seciliTam = await _depo.GetirAsync(secili.Id);
            if (_seciliTam is null)
                return;

            DoguranSql.Text = _seciliTam.SqlMetni;
            EskiHalGrid.ItemsSource = EskiHalTablosu(_seciliTam).DefaultView;
        }
        catch (Exception ex)
        {
            Durum.Text = $"Paket okunamadı: {ex.Message}";
        }
    }

    /// <summary>Serileştirilmiş eski satırları DataGrid'in bağlanacağı DataTable'a çevirir.</summary>
    private static DataTable EskiHalTablosu(GeriAlPaketi paket)
    {
        var tablo = new DataTable();
        IReadOnlyList<GeriAlSerilestirici.KolonTanimi> kolonlar = GeriAlSerilestirici.KolonlariOku(paket);
        foreach (GeriAlSerilestirici.KolonTanimi k in kolonlar)
            tablo.Columns.Add(k.Ad, typeof(string));

        foreach (string?[] satir in GeriAlSerilestirici.SatirlariOku(paket))
            tablo.Rows.Add(satir.Select(h => (object?)h ?? DBNull.Value).ToArray());
        return tablo;
    }

    private void ScriptUret_Click(object sender, RoutedEventArgs e)
    {
        if (_seciliTam is null)
        {
            Durum.Text = "Önce bir paket seçin.";
            return;
        }

        (string? script, string? hata) = TersDmlUretici.Uret(_seciliTam);
        if (script is null)
        {
            Iletisim.Uyari(this, "SQLST — Geri Al", "Geri alma script'i üretilemedi", hata ?? "");
            return;
        }

        // Sekme adı boş → SekmeAc otomatik SQLST-N adı verir; DB paketin veritabanı.
        _sekmeAc("", script, _seciliTam.Veritabani);
        Durum.Text = "Geri alma script'i yeni sekmede açıldı — inceleyip Güvenli Yazma ile çalıştırın.";
        Close();
    }

    private async void Sil_Click(object sender, RoutedEventArgs e)
    {
        if (PaketListesi.SelectedItem is not PaketGorunumu secili)
        {
            Durum.Text = "Önce silinecek paketi seçin.";
            return;
        }
        if (!Iletisim.Sor(this, "SQLST — Geri Al", "Paket silinsin mi?",
                $"{secili.Fiil} · {secili.Tablo} geri alma paketi silinecek. Bu işlem geri alınamaz.", "🗑 Sil", IletisimTuru.Tehlike))
            return;

        try
        {
            await _depo.SilAsync(secili.Id);
            await ListeyiYukleAsync();
        }
        catch (Exception ex)
        {
            Durum.Text = $"Silinemedi: {ex.Message}";
        }
    }

    private void Kapat_Click(object sender, RoutedEventArgs e) => Close();
}
