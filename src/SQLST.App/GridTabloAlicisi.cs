using System.Data;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App;

/// <summary>
/// 🧱 v22-S3 (saha turu-3; kullanıcı üçüncü kez çökme bildirdi: "mantığımızı MSSQL'deki gibi yapalım,
/// gereksiz yüklerden arındıralım"): okunan satırları ARA LİSTE OLMADAN doğrudan grid tablosuna yazar.
///
/// Eskiden aynı veri iki kez yaşıyordu — okuyucunun <c>List&lt;object?[]&gt;</c>'i ve onun ÜSTÜNE
/// kurulan DataTable. ÖLÇÜM (200.000 satır × 10 kolon; 4 sayı + 1 tarih + 5 metin):
/// <list type="bullet">
///   <item>iki kopya + object kolonlar (eski): <b>708 bayt/satır</b> → 1 GB'da ~1,5M satır</item>
///   <item>tek kopya + GERÇEK tipli kolonlar (bu sınıf): <b>457 bayt/satır</b> → ~2,3M satır</item>
/// </list>
/// Ayrıca her satırda bellek tavanı sorulabildiği için kesinti ARTIK TABLO KURULURKEN de olur:
/// koruma eskiden yalnız okuma fazındaydı, oysa tepe bellek tablo kurulurken oluşuyordu.
/// </summary>
public sealed class GridTabloAlicisi : ISonucAlici
{
    private readonly List<SonucSeti> _setler = [];
    private readonly Func<bool> _durmaliMi;
    private DataTable? _tablo;
    private Dictionary<string, string>? _tipler;
    private int _sayac;

    /// <param name="durmaliMi">Her 4.096 satırda bir sorulur; true → yükleme durur (tavan).</param>
    public GridTabloAlicisi(Func<bool>? durmaliMi = null)
        => _durmaliMi = durmaliMi ?? BellekNobetcisi.OkumaKesilmeli; // v22-S4: bkz. OkumaKesilmeli

    /// <summary>Kurulan tablolar (küme sırasıyla).</summary>
    public IReadOnlyList<SonucSeti> Setler => _setler;

    /// <summary>Bellek tavanı yüzünden en az bir küme yarım kaldı mı.</summary>
    public bool Kesildi { get; private set; }

    public void KumeBasladi(int kumeIndex, IReadOnlyList<KolonBilgisi> kolonlar)
    {
        _tablo = new DataTable();
        _tipler = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _sayac = 0;

        // Kolon adları tekilleştirilir (boş → adsız, çakışan → ad_2) — SonucBicimleyici ile aynı kural.
        var kullanilan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KolonBilgisi kolon in kolonlar)
        {
            string ad = string.IsNullOrWhiteSpace(kolon.Ad) ? SonucBicimleyici.AdsizKolon : kolon.Ad;
            string aday = ad;
            for (int i = 2; !kullanilan.Add(aday); i++)
                aday = $"{ad}_{i}";
            // GERÇEK tip: DataTable sayıyı/tarihi kutulamadan saklar (ölçüm: 595 → 457 bayt/satır).
            DataColumn yeniKolon = _tablo.Columns.Add(aday, kolon.ClrTip ?? typeof(object));
            // v23-S13: SQL tipi (kesir ölçeğiyle) kolonla birlikte taşınır — ham gösterim/dışa aktarma.
            string tip = HamDeger.GorunumTipi(kolon.TipAdi, kolon.Olcek);
            _tipler[aday] = tip;
            yeniKolon.ExtendedProperties[HamDeger.TipAnahtari] = tip;
        }
        _tablo.BeginLoadData();
    }

    public bool Satir(object?[] satir)
    {
        if (_tablo is null)
            return true; // küme başlamadan satır gelmez; gelirse sessizce yut (okuma bozulmasın)
        if ((++_sayac & 4095) == 0 && _durmaliMi())
        {
            Kesildi = true;
            return false; // okuyucuya "DUR" — çökmeden önce temiz kesinti
        }

        // Şemasız kaynakta (Mongo) alan tipi belgeden belgeye değişebilir: tipli kolon değeri
        // reddederse o KOLON object'e çevrilip satır yeniden eklenir (gösterim asla çökmez).
        try
        {
            _tablo.Rows.Add(satir);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or FormatException)
        {
            ObjectKolonlaraDus();
            _tablo.Rows.Add(satir);
        }
        return true;
    }

    public void KumeBitti(int kumeIndex)
    {
        if (_tablo is null || _tipler is null)
            return;
        _tablo.EndLoadData();
        _setler.Add(new SonucSeti(_tablo, _tipler) { Kesildi = Kesildi });
        _tablo = null;
        _tipler = null;
    }

    /// <summary>Tipli kolonlar veriyi reddetti → tabloyu object kolonlarla YENİDEN kurar (satırlar korunur).</summary>
    private void ObjectKolonlaraDus()
    {
        DataTable eski = _tablo!;
        var yeni = new DataTable();
        foreach (DataColumn k in eski.Columns)
            yeni.Columns.Add(k.ColumnName, typeof(object)).ExtendedProperties[HamDeger.TipAnahtari] =
                k.ExtendedProperties[HamDeger.TipAnahtari]; // ham gösterim tipi object kolonda da kalır
        yeni.BeginLoadData();
        foreach (DataRow satir in eski.Rows)
            yeni.Rows.Add(satir.ItemArray);
        _tablo = yeni;
        eski.Dispose();
    }
}
