using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Geçmiş panelindeki tek satır (V2-S2): depo kaydını Türkçe/yerel gösterime çevirir
/// (FOG-9 — depo UTC saklar, yerelleştirme yalnız burada).
/// </summary>
public sealed class GecmisGorunumu
{
    public GecmisGorunumu(GecmisKaydi kayit)
    {
        Kayit = kayit;
        ZamanMetni = kayit.BaslangicUtc.ToLocalTime().ToString("g"); // tr-TR: 16.07.2026 12:30
        SureMetni = SonucBicimleyici.SureFormatla(TimeSpan.FromMilliseconds(kayit.SureMs));
        string tekSatir = string.Join(" ",
            kayit.Sql.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        SqlOzeti = tekSatir.Length <= 90 ? tekSatir : tekSatir[..89] + "…";
        // Simgenin rengi görünümde (MainWindow) DynamicResource tetikleyicilerle verilir —
        // VM'den sabit fırça çıkarmak koyu temada takassız/sönük kalıyordu (3. tur, 2026-07-18).
        DurumSimge = kayit.Durum switch
        {
            GecmisDurumu.Basarili => "✔",
            GecmisDurumu.IptalEdildi => "■",
            _ => "✖",
        };
    }

    public GecmisKaydi Kayit { get; }
    public string ZamanMetni { get; }
    public string SureMetni { get; }
    public string SqlOzeti { get; }
    public string DurumSimge { get; }
    public string? Veritabani => Kayit.Veritabani;
    /// <summary>"· SekmeAdi" eki (kullanıcı isteği 2026-07-23): hangi sekmeden çalıştı; eski kayıtlarda boş.</summary>
    public string SekmeEki => string.IsNullOrEmpty(Kayit.SekmeAdi) ? "" : $"  ·  {Kayit.SekmeAdi}";
    /// <summary>Fareyle üzerine gelince tam metin (+ varsa hata) görünür.</summary>
    public string Ipucu => Kayit.HataMesaji is null ? Kayit.Sql : $"{Kayit.Sql}\n\n⚠ {Kayit.HataMesaji}";
}
