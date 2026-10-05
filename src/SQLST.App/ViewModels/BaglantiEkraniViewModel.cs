using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.ViewModels;

/// <summary>
/// Bağlantı ekranının durumu ve eylemleri (03-ui-tasarim §3.1).
/// Parola, WPF PasswordBox'tan yalnız metot parametresi olarak girer;
/// VM'de düz metin parola ALANI tutulmaz (FG-1.3).
/// </summary>
public partial class BaglantiEkraniViewModel : ObservableObject
{
    private readonly IProfileStore _store;
    private readonly ISecretProtector _protector;
    private readonly ISqlExecutor _executor;
    private readonly IAyarDeposu _ayar;

    public BaglantiEkraniViewModel(
        IProfileStore store, ISecretProtector protector, ISqlExecutor executor, IAyarDeposu ayar)
    {
        _store = store;
        _protector = protector;
        _executor = executor;
        _ayar = ayar;
    }

    /// <summary>Yeni profil için varsayılan sorgu timeout'u (FG-3.15) — ayar deposundan; 0 = sınırsız.</summary>
    public int VarsayilanKomutTimeoutSn { get; private set; }

    public ObservableCollection<ConnectionProfile> Profiller { get; } = [];

    public IReadOnlyList<KimlikTuru> KimlikTurleri { get; } = [KimlikTuru.Windows, KimlikTuru.Sql];

    /// <summary>Motor seçenekleri (V3-S1): görünen ad + enum değeri; ComboBox SelectedValuePath=Deger.</summary>
    public sealed record MotorSecenegi(MotorTuru Deger, string Ad)
    {
        public override string ToString() => Ad;
    }

    public IReadOnlyList<MotorSecenegi> Motorlar { get; } =
    [
        new(MotorTuru.Mssql, "SQL Server"),
        new(MotorTuru.Postgres, "PostgreSQL"),
        new(MotorTuru.MySql, "MySQL / MariaDB"),
        new(MotorTuru.Oracle, "Oracle"),
        new(MotorTuru.Mongo, "MongoDB"),
    ];

    // --- Form alanları (seçili karttan ya da boş "Yeni"den dolar) ---
    [ObservableProperty] private Guid? _duzenlenenId;
    [ObservableProperty] private string _ad = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MssqlMu))]
    [NotifyPropertyChangedFor(nameof(SaltOkunurAciklamasi))]
    private MotorTuru _motor = MotorTuru.Mssql;
    [ObservableProperty] private string _sunucu = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SqlKimlikMi))] private KimlikTuru _kimlik = KimlikTuru.Windows;
    [ObservableProperty] private string _kullaniciAdi = "";
    [ObservableProperty] private bool _saltOkunur;
    /// <summary>Bağlantı zaman aşımı, sn (FG-1.8).</summary>
    [ObservableProperty] private int _baglantiTimeoutSn = 15;
    /// <summary>Sorgu (komut) zaman aşımı, sn; 0 = sınırsız (FG-1.8/3.15).</summary>
    [ObservableProperty] private int _komutTimeoutSn;

    [ObservableProperty] private bool _mesgul;
    [ObservableProperty] private string _durum = "";
    [ObservableProperty] private bool _durumHata;

    /// <summary>Kayıtlı parolası olan profil düzenlenirken kutu boş bırakılırsa eski parola korunur.</summary>
    [ObservableProperty] private bool _kayitliParolaVar;

    public bool SqlKimlikMi => Kimlik == KimlikTuru.Sql;

    /// <summary>Connection string içe aktarma yalnız MSSQL biçimini çözer — diğer motorlarda gizlenir.</summary>
    public bool MssqlMu => Motor == MotorTuru.Mssql;

    /// <summary>
    /// Salt-okunur seçilirse NELERİN gönderilmeyeceği (kullanıcı isteği 2026-07-19).
    /// Motora göre değişir: SQL ailesinde anahtar sözcükler, MongoDB'de komut adları —
    /// orada sorgu metni JSON'dur, "DELETE" diye bir sözcük yoktur.
    ///
    /// Koruma <b>BEŞ MOTORDA DA</b> çalışır (2026-07-19 gözden geçirmesi), bu yüzden kutu
    /// hiçbir motorda gizlenmez.
    /// </summary>
    public string SaltOkunurAciklamasi => Motor == MotorTuru.Mongo
        ? "insert · update · delete · findAndModify · drop · dropDatabase · dropIndexes · "
          + "create · createIndexes · createUser · renameCollection · collMod · bulkWrite "
          + "komutları ve $out/$merge içeren aggregate gönderilmez"
        : "UPDATE · DELETE · INSERT · MERGE · CREATE · ALTER · DROP · TRUNCATE · EXEC "
          + "gönderilmez";

    public async Task YukleAsync()
    {
        VarsayilanKomutTimeoutSn = await _ayar.IntOkuAsync(AyarAnahtari.KomutTimeoutSn, 0);
        Profiller.Clear();
        foreach (ConnectionProfile p in await _store.GetAllAsync())
            Profiller.Add(p);
    }

    public void YeniForm()
    {
        DuzenlenenId = null;
        Ad = ""; Sunucu = "";
        Motor = MotorTuru.Mssql;
        Kimlik = KimlikTuru.Windows; KullaniciAdi = "";
        SaltOkunur = false;
        BaglantiTimeoutSn = 15; KomutTimeoutSn = VarsayilanKomutTimeoutSn;
        KayitliParolaVar = false;
        Durum = "";
    }

    public void FormaYukle(ConnectionProfile p)
    {
        DuzenlenenId = p.Id;
        Ad = p.Ad; Sunucu = p.Sunucu;
        Motor = p.Motor;
        Kimlik = p.Kimlik; KullaniciAdi = p.KullaniciAdi ?? "";
        SaltOkunur = p.SaltOkunur;
        BaglantiTimeoutSn = p.BaglantiTimeoutSn; KomutTimeoutSn = p.KomutTimeoutSn;
        KayitliParolaVar = p.ParolaSifreli is not null;
        Durum = "";
    }

    /// <summary>Formdan profil nesnesi kurar; parola boşsa ve düzenlenen profilde kayıtlı parola varsa onu korur.</summary>
    public ConnectionProfile FormdanProfil(string parola)
    {
        ConnectionProfile? mevcut = DuzenlenenId is null
            ? null
            : Profiller.FirstOrDefault(p => p.Id == DuzenlenenId);

        return new ConnectionProfile
        {
            Id = DuzenlenenId ?? Guid.NewGuid(),
            Ad = Ad.Trim(),
            Motor = Motor,
            Sunucu = Sunucu.Trim(),
            Kimlik = Kimlik,
            KullaniciAdi = SqlKimlikMi ? KullaniciAdi.Trim() : null,
            ParolaSifreli = !SqlKimlikMi ? null
                : parola.Length > 0 ? _protector.Sifrele(parola)
                : mevcut?.ParolaSifreli,
            SaltOkunur = SaltOkunur,
            BaglantiTimeoutSn = BaglantiTimeoutSn,
            KomutTimeoutSn = KomutTimeoutSn,
        };
    }

    public async Task<ConnectionProfile?> KaydetAsync(string parola)
    {
        ConnectionProfile profil = FormdanProfil(parola);
        IReadOnlyList<string> hatalar = ProfilDogrulayici.Dogrula(profil);
        if (hatalar.Count > 0)
        {
            DurumYaz(string.Join(" ", hatalar), hata: true);
            return null;
        }

        await _store.SaveAsync(profil);
        await YukleAsync();
        DuzenlenenId = profil.Id;
        KayitliParolaVar = profil.ParolaSifreli is not null;
        DurumYaz($"'{profil.Ad}' kaydedildi.", hata: false);
        return profil;
    }

    public async Task SilAsync(ConnectionProfile profil)
    {
        await _store.DeleteAsync(profil.Id);
        await YukleAsync();
        if (DuzenlenenId == profil.Id)
            YeniForm();
        DurumYaz($"'{profil.Ad}' silindi.", hata: false);
    }

    public async Task<bool> TestEtAsync(string parola, CancellationToken ct = default)
    {
        ConnectionProfile profil = FormdanProfil(parola);
        IReadOnlyList<string> hatalar = ProfilDogrulayici.Dogrula(profil);
        if (hatalar.Count > 0)
        {
            DurumYaz(string.Join(" ", hatalar), hata: true);
            return false;
        }

        Mesgul = true;
        DurumYaz("Bağlantı deneniyor…", hata: false);
        try
        {
            (bool basarili, string? hataMesaji) = await _executor.TestConnectionAsync(profil, ct);
            DurumYaz(basarili ? "Bağlantı başarılı. ✔" : $"Bağlantı başarısız: {hataMesaji}", hata: !basarili);
            return basarili;
        }
        finally
        {
            Mesgul = false;
        }
    }

    /// <summary>Bağlan: doğrula + sına + kaydet; başarılıysa kalıcı profili döner.</summary>
    public async Task<ConnectionProfile?> BaglanAsync(string parola, CancellationToken ct = default)
    {
        if (!await TestEtAsync(parola, ct))
            return null;
        return await KaydetAsync(parola);
    }

    /// <summary>
    /// FG-3.14 (V2-S10): connection string'den formu doldurur; parola düz metin DÖNER
    /// (View, PasswordBox'a koyar — VM'de alan olarak tutulmaz, FG-1.3 korunur).
    /// </summary>
    public (bool Basarili, string? Parola) ConnectionStringdenDoldur(string connectionString)
    {
        // V4-S4: çözücü beş motoru anlar ve motoru dizeden TANIR; tanıyamazsa formdaki
        // seçimi varsayar (ve bunu not olarak söyler) — sessizce yanlış motor seçilmez.
        (ConnectionStringCozucu.Cozum? cozum, string? hata) =
            ConnectionStringCozucu.Coz(connectionString, Motor);
        if (cozum is null)
        {
            DurumYaz($"Connection string çözümlenemedi: {hata}", hata: true);
            return (false, null);
        }

        DuzenlenenId = null; // her zaman YENİ profil olarak dolar — mevcut ezilmez
        Motor = cozum.Motor;
        Ad = cozum.OnerilenAd;
        Sunucu = cozum.Sunucu;
        Kimlik = cozum.Kimlik;
        KullaniciAdi = cozum.KullaniciAdi ?? "";
        if (cozum.BaglantiTimeoutSn is { } sn)
            BaglantiTimeoutSn = sn;
        KayitliParolaVar = false;
        SaltOkunur = false;

        DurumYaz("Form connection string'den dolduruldu."
            + (cozum.Notlar.Count > 0 ? " ⚠ " + string.Join(" ", cozum.Notlar) : ""), hata: false);
        return (true, cozum.Parola);
    }

    private void DurumYaz(string mesaj, bool hata)
    {
        Durum = mesaj;
        DurumHata = hata;
    }
}
