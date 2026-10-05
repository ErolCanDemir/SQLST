using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>Panelde bir bölüm: başlık + açıklama + o bölümün sonuç kümeleri.</summary>
public sealed class TeshisBolumGorunumu
{
    public required string Baslik { get; init; }
    public required string Aciklama { get; init; }
    /// <summary>
    /// "Bu alandan hangi bilgi takip edilecek?" (kullanıcı isteği 2026-07-19) — Açıklama
    /// bölümün NE OLDUĞUNU, bu ise HANGİ KOLONA BAKILACAĞINI ve eşiği söyler.
    /// </summary>
    public required string Takip { get; init; }
    public ObservableCollection<SonucSetiGorunumu> Setler { get; } = [];
    /// <summary>Bölüm çalışmadıysa nedeni (ör. sunucuda performance_schema kapalı).</summary>
    public string? Hata { get; set; }
    public bool HataVar => !string.IsNullOrEmpty(Hata);
}

/// <summary>
/// SQL ailesi motorları için GENEL Yönetim Paneli (V3 — kullanıcı isteği 2026-07-18:
/// "yönetim paneli … motora göre de çalışmalı, o motorun özelliği gelmeli").
///
/// Bölümleri <see cref="ILehce.TeshisBolumleri"/> verir; bu sınıf yalnız çalıştırır ve
/// gösterir. Böylece PostgreSQL/MySQL/Oracle kendi araçlarını (kullanılmayan index,
/// tablo boyutları, canlı oturumlar, kilitler…) tek ortak ekranda sunar.
/// SQL Server'ın kendi zengin paneli (V2-S7) ve MongoDB'nin kendi paneli ayrıdır.
/// </summary>
public partial class LehceTeshisSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly ISqlExecutor _executor;
    private readonly ILehce _lehce;
    private readonly Func<ConnectionProfile?> _profilGetir;
    private bool _yuklendi;

    public LehceTeshisSekmesiViewModel(
        ISqlExecutor executor, ILehce lehce,
        Func<ConnectionProfile?> profilGetir,
        ObservableCollection<string> veritabaniAdlari,
        ISorguGecmisiDeposu gecmis)
    {
        _executor = executor;
        _lehce = lehce;
        _profilGetir = profilGetir;
        VeritabaniAdlari = veritabaniAdlari;
        DenetimIzi = new DenetimIziViewModel(gecmis);
    }

    public string Baslik => "🛠 Yönetim Paneli";

    public ObservableCollection<string> VeritabaniAdlari { get; }
    public ObservableCollection<TeshisBolumGorunumu> Bolumler { get; } = [];

    /// <summary>Denetim izi (v10-S4): "kim ne iş yapmış" — dört motorda paylaşımlı bileşen.</summary>
    public DenetimIziViewModel DenetimIzi { get; }

    [ObservableProperty] private SekmeDurumu _durum = SekmeDurumu.Bosta;
    [ObservableProperty] private string _sunucuBandi = "";
    [ObservableProperty] private string _bilgi = "";
    [ObservableProperty] private string? _secilenVeritabani;

    public bool CalisiyorMu => Durum == SekmeDurumu.Calisiyor;

    [RelayCommand]
    public async Task YenileAsync()
    {
        ConnectionProfile? profil = _profilGetir();
        if (profil is null || CalisiyorMu)
            return;

        Durum = SekmeDurumu.Calisiyor;
        Bilgi = "Motorun durum sorguları çalıştırılıyor…";
        SunucuBandi = $"{MotorAdi(profil.Motor)} · {profil.Sunucu}"
                    + (SecilenVeritabani is { Length: > 0 } vt ? $" · {vt}" : "")
                    + " — salt okunur panel; hiçbir şey otomatik değiştirilmez.";
        Bolumler.Clear();

        int calisan = 0, hatali = 0;
        foreach (TeshisBolumu bolum in _lehce.TeshisBolumleri)
        {
            var gorunum = new TeshisBolumGorunumu
            {
                Baslik = bolum.Baslik, Aciklama = bolum.Aciklama, Takip = bolum.Takip,
            };
            Bolumler.Add(gorunum);

            if (bolum.VeritabaniGerekir && string.IsNullOrWhiteSpace(SecilenVeritabani))
            {
                gorunum.Hata = "Bu bölüm için üstteki listeden bir veritabanı seçin.";
                continue;
            }

            QueryResult sonuc = await _executor.ExecuteAsync(
                profil, bolum.Sorgu,
                new ExecuteOptions { SatirSiniri = 5000, VeritabaniOverride = SecilenVeritabani },
                CancellationToken.None);

            if (!sonuc.Basarili)
            {
                // Bölüm bazlı hata paneli bozmaz: sunucuda o görünüm kapalı/yetkisiz olabilir.
                gorunum.Hata = sonuc.Hata?.Mesaj ?? "Bilinmeyen hata";
                hatali++;
                continue;
            }

            foreach (ResultSetData set in sonuc.ResultSetler)
            {
                SonucSeti cevrilmis = SonucBicimleyici.TabloyaCevir(set);
                gorunum.Setler.Add(new SonucSetiGorunumu
                {
                    Set = cevrilmis,
                    Baslik = $"{bolum.Baslik}  ({cevrilmis.SatirSayisi:N0} satır)",
                    BaslikGorunur = true,
                });
            }
            calisan++;
        }

        await DenetimIzi.YukleAsync(profil); // Aktivite/Denetim (v10-S4) — motor-bağımsız denetim izi

        Durum = hatali > 0 && calisan == 0 ? SekmeDurumu.Hata : SekmeDurumu.Tamamlandi;
        Bilgi = $"{calisan} bölüm yüklendi"
              + (hatali > 0 ? $" · {hatali} bölüm okunamadı (sunucu ayarı/yetki — bölüm başlığında ayrıntı)" : "");
        _yuklendi = true;
    }

    private static string MotorAdi(MotorTuru motor) => motor switch
    {
        MotorTuru.Postgres => "PostgreSQL",
        MotorTuru.MySql => "MySQL / MariaDB",
        MotorTuru.Oracle => "Oracle",
        MotorTuru.Mssql => "SQL Server",
        _ => motor.ToString(),
    };

    partial void OnSecilenVeritabaniChanged(string? value)
    {
        if (_yuklendi)
            _ = YenileAsync();
    }

    public Task KapatAsync() => Task.CompletedTask; // salt okunur, kalıcı bağlantı yok
}
