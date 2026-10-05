using System.IO;
using System.Windows;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// 📤 Şema Kopyalama penceresi (kullanıcı kararı 2026-07-31: öneri A, yalnız şema): kaynak DB'nin
/// şemasını kayıtlı başka bir SQL Server bağlantısındaki hedefe kurar. İş SAF serviste
/// (<see cref="SemaKopyalamaServisi"/>); pencere yalnız girdi + ilerleme + rapor.
/// </summary>
public partial class SemaKopyalamaPenceresi : Window
{
    private readonly SemaKopyalamaServisi _servis;
    private readonly ConnectionProfile _kaynak;
    private readonly string _kaynakDb;
    private CancellationTokenSource? _cts;

    public SemaKopyalamaPenceresi(
        SemaKopyalamaServisi servis, ConnectionProfile kaynak, string kaynakDb,
        IReadOnlyList<ConnectionProfile> profiller)
    {
        _servis = servis;
        _kaynak = kaynak;
        _kaynakDb = kaynakDb;
        InitializeComponent();

        KaynakEtiket.Text = $"{kaynak.Ad} · [{kaynakDb}]";
        // Hedef adayları: kayıtlı MSSQL profilleri (kaynağın kendisi dahil — aynı sunucuda kopya).
        _profiller = [.. profiller.Where(p => p.Motor == MotorTuru.Mssql)];
        HedefProfil.ItemsSource = _profiller.Select(x => $"{x.Ad} ({x.Sunucu})").ToList();
        HedefProfil.SelectedIndex = _profiller.Count > 0 ? 0 : -1;
        HedefDb.Text = $"{kaynakDb}_Kopya";
    }

    private readonly List<ConnectionProfile> _profiller;

    private SemaKopyaKapsami Kapsam() => new(
        Indexler: KapsamIndex.IsChecked == true,
        Fkler: KapsamFk.IsChecked == true,
        Viewlar: KapsamView.IsChecked == true,
        Spler: KapsamSp.IsChecked == true,
        Fonksiyonlar: KapsamFn.IsChecked == true);

    private async void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        if (HedefProfil.SelectedIndex < 0 || HedefDb.Text.Trim() is not { Length: > 0 } hedefDb)
        {
            Durum.Text = "Hedef bağlantı ve veritabanı adı gerekli.";
            return;
        }
        ConnectionProfile hedef = _profiller[HedefProfil.SelectedIndex];
        if (hedef.Id == _kaynak.Id && hedefDb.Equals(_kaynakDb, StringComparison.OrdinalIgnoreCase))
        {
            Durum.Text = "Hedef, kaynağın kendisi olamaz — farklı bir veritabanı adı verin.";
            return;
        }

        KopyalaDugmesi.IsEnabled = false;
        DurdurDugmesi.IsEnabled = true;
        Rapor.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        try
        {
            var ilerleme = new Progress<string>(m => Durum.Text = m);
            SemaKopyaSonucu sonuc = await _servis.KopyalaAsync(
                _kaynak, _kaynakDb, hedef, hedefDb, Kapsam(), ilerleme, _cts.Token);

            Durum.Text = sonuc switch
            {
                { GenelHata: { } g } => $"⚠ {g}",
                { Basarili: true } => $"✔ Tamamlandı — {sonuc.BasariliAdim}/{sonuc.ToplamAdim} adım kuruldu.",
                _ => $"⚠ {sonuc.BasariliAdim}/{sonuc.ToplamAdim} adım kuruldu — {sonuc.Hatalar.Count} sorun (rapor aşağıda).",
            };
            if (sonuc.Hatalar.Count > 0)
            {
                Rapor.Text = string.Join(Environment.NewLine, sonuc.Hatalar);
                Rapor.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            Durum.Text = "■ Durduruldu — o ana dek kurulan nesneler hedefte kaldı.";
        }
        finally
        {
            _cts = null;
            KopyalaDugmesi.IsEnabled = true;
            DurdurDugmesi.IsEnabled = false;
        }
    }

    /// <summary>Bonus C: kopyalamadan tüm şema script'ini tek .sql dosyasına yazar.</summary>
    private async void ScriptKaydet_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_kaynakDb}-sema.sql",
            Filter = "SQL script (*.sql)|*.sql",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        KopyalaDugmesi.IsEnabled = false;
        try
        {
            var uyarilar = new List<string>();
            var ilerleme = new Progress<string>(m => Durum.Text = m);
            IReadOnlyList<SemaKopyaAdimi> plan = await _servis.PlanUretAsync(
                _kaynak, _kaynakDb, Kapsam(), uyarilar, ilerleme, CancellationToken.None);
            await File.WriteAllTextAsync(dlg.FileName,
                SemaKopyalamaServisi.TekScript(plan, _kaynakDb), new System.Text.UTF8Encoding(true));
            Durum.Text = $"🧾 {plan.Count} adımlık script kaydedildi: {Path.GetFileName(dlg.FileName)}";
            if (uyarilar.Count > 0)
            {
                Rapor.Text = string.Join(Environment.NewLine, uyarilar);
                Rapor.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Durum.Text = $"⚠ Script üretilemedi: {ex.Message}";
        }
        finally
        {
            KopyalaDugmesi.IsEnabled = true;
        }
    }

    private void Durdur_Click(object sender, RoutedEventArgs e)
        => IptalYardimcisi.ArkaPlandaIptal(_cts); // m.15: Cancel UI'da bloklayabilir — havuzda
}
