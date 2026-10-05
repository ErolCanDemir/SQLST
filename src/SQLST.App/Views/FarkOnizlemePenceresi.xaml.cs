using System.Data;
using System.Windows;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Views;

/// <summary>
/// BF-3 (2026-07-27): tam eşitleme fark önizlemesi — YENİ/DEĞİŞEN/SİLİNMİŞ kovaları grid'lerde,
/// "silinenleri de uygula" onay kutusu (varsayılan KAPALI: silme opt-in, sessiz veri kaybı olmasın).
/// "Script Üret" <see cref="FarkDmlUretici"/> çıktısını Güvenli Yazma sekmesine açar — kullanıcı orada
/// çalıştırıp satır sayısını görür ve COMMIT/ROLLBACK verir. Pencere veritabanına DOKUNMAZ: fark
/// zaten hesaplanmış gelir, script üretimi saftır (ayrı yürütme yolu yok — edit modu ile aynı bant).
/// </summary>
public partial class FarkOnizlemePenceresi : Window
{
    private readonly TabloFarki _fark;
    private readonly ILehce _lehce;
    private readonly string _sema;
    private readonly string _tablo;
    private readonly IReadOnlyList<string> _anahtarlar;
    private readonly Action<string> _scriptiAc;
    private readonly DuzenlemeMetasi? _meta; // computed/rowversion/identity bilgisi (kod inceleme 2026-07-27)

    public FarkOnizlemePenceresi(
        TabloFarki fark, ILehce lehce, string sema, string tablo,
        IReadOnlyList<string> anahtarlar, Action<string> scriptiAc, DuzenlemeMetasi? meta = null)
    {
        _fark = fark;
        _lehce = lehce;
        _sema = sema;
        _tablo = tablo;
        _anahtarlar = anahtarlar;
        _scriptiAc = scriptiAc;
        _meta = meta;

        InitializeComponent();

        Ozet.Text = $"'{lehce.TamAdYaz(sema, tablo)}' — Excel ile karşılaştırıldı: "
            + $"{fark.Yeniler.Count} yeni, {fark.Degisenler.Count} değişen, "
            + $"{fark.Silinenler.Count} yalnız tabloda. İnceleyin; script Güvenli Yazma sekmesinde "
            + "açılır, orada çalıştırıp COMMIT/ROLLBACK verirsiniz.";

        YeniSekme.Header = $"➕ Yeni ({fark.Yeniler.Count})";
        DegisenSekme.Header = $"✏ Değişen ({fark.Degisenler.Count})";
        SilinenSekme.Header = $"🗑 Silinecek ({fark.Silinenler.Count})";

        YeniGrid.ItemsSource = SatirTablosu(fark.Yeniler).DefaultView;
        SilinenGrid.ItemsSource = SatirTablosu(fark.Silinenler).DefaultView;
        DegisenGrid.ItemsSource = DegisenTablo(fark.Degisenler).DefaultView;

        SilmeyiUygula.IsEnabled = fark.Silinenler.Count > 0;
        SilmeyiUygula.Checked += (_, _) => SilmeUyari.Visibility = Visibility.Visible;
        SilmeyiUygula.Unchecked += (_, _) => SilmeUyari.Visibility = Visibility.Collapsed;
        // Tam eşitleme kararı (kullanıcı 2026-07-26): "Güncelle, Excel'de olmayan satırı OTOMATİK
        // siler" → silinecek satır varsa silme VARSAYILAN AÇIK. Emniyet Güvenli Yazma'dır (kullanıcı
        // satır sayısını görüp ROLLBACK verebilir); yine de görünür bir kapatma kutusu bırakılır.
        // (IsChecked=true, Checked handler'ını tetikleyip uyarı bandını da açar.)
        SilmeyiUygula.IsChecked = fark.Silinenler.Count > 0;
    }

    /// <summary>YENİ/SİLİNMİŞ kovası → düz DataTable (kolon sırası ilk satırdan).</summary>
    private static DataTable SatirTablosu(IReadOnlyList<FarkSatiri> satirlar)
    {
        var tablo = new DataTable();
        if (satirlar.Count == 0)
            return tablo;

        foreach (string kolon in satirlar[0].Degerler.Keys)
            tablo.Columns.Add(kolon, typeof(object));

        foreach (FarkSatiri s in satirlar)
            tablo.Rows.Add(tablo.Columns.Cast<DataColumn>()
                .Select(c => s.Degerler.GetValueOrDefault(c.ColumnName) ?? DBNull.Value).ToArray());

        return tablo;
    }

    /// <summary>DEĞİŞEN kovası → hücre başına satır: [anahtar…, Kolon, Eski (tablo), Yeni (Excel)].</summary>
    private DataTable DegisenTablo(IReadOnlyList<DegisenSatir> satirlar)
    {
        var tablo = new DataTable();
        foreach (string a in _anahtarlar)
            tablo.Columns.Add(a, typeof(object));
        tablo.Columns.Add("Kolon", typeof(string));
        tablo.Columns.Add("Eski (tablo)", typeof(object));
        tablo.Columns.Add("Yeni (Excel)", typeof(object));

        foreach (DegisenSatir d in satirlar)
            foreach ((string kolon, (object? eski, object? yeni)) in d.Degisenler)
            {
                DataRow r = tablo.NewRow();
                foreach (string a in _anahtarlar)
                    r[a] = d.Anahtar.GetValueOrDefault(a) ?? DBNull.Value;
                r["Kolon"] = kolon;
                r["Eski (tablo)"] = eski ?? DBNull.Value;
                r["Yeni (Excel)"] = yeni ?? DBNull.Value;
                tablo.Rows.Add(r);
            }

        return tablo;
    }

    private void ScriptUret_Click(object sender, RoutedEventArgs e)
    {
        bool silme = SilmeyiUygula.IsChecked == true;

        // Sunucunun yönettiği kolonları (computed/rowversion) INSERT/UPDATE dışında tut; anahtar
        // IDENTITY ise INSERT'i IDENTITY_INSERT ile sar (yalnız MSSQL) — kod inceleme 2026-07-27.
        IReadOnlySet<string>? yazilamaz = _meta is null ? null
            : _meta.Kolonlar.Where(k => k.ComputedMi || k.RowversionMi).Select(k => k.Ad)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool identityInsert = _meta is not null && _lehce.MotorId == "mssql"
            && _meta.Kolonlar.Any(k => k.IdentityMi
                && _anahtarlar.Contains(k.Ad, StringComparer.OrdinalIgnoreCase));

        IReadOnlyList<string> komutlar =
            FarkDmlUretici.Uret(_lehce, _sema, _tablo, _anahtarlar, _fark, silme, yazilamaz, identityInsert);
        if (komutlar.Count == 0)
        {
            MessageBox.Show(this,
                "Uygulanacak fark yok — tablo Excel ile zaten aynı (silme kapalıysa yalnız silinecek satır olabilir).",
                "SQLST — Tam eşitleme", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _scriptiAc(FarkDmlUretici.Onizle(komutlar));
        Close(); // DialogResult'a gerek yok — çağıran dönüşü kullanmaz, Show() ile de test edilebilir kalır
    }

    private void Kapat_Click(object sender, RoutedEventArgs e) => Close();
}
