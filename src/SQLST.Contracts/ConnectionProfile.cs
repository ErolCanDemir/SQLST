namespace SQLST.Contracts;

public enum KimlikTuru
{
    Windows,
    Sql
}

/// <summary>
/// Bağlantı motoru (V3-S1 — 08-v3r1). Sıralama SABİT kalmalı: 0 = Mssql, eski
/// profiles.json dosyalarında alan hiç yokken varsayılan bu değere düşer (geriye uyum);
/// sonrakiler yalnız SONA eklenir.
/// </summary>
public enum MotorTuru
{
    Mssql,
    Postgres,
    MySql,
    Oracle,
    Mongo,
}

/// <summary>
/// Kayıtlı bağlantı profili (02-mimari §3.1). Parola asla düz metin tutulmaz;
/// <see cref="ParolaSifreli"/> DPAPI-CurrentUser ile şifrelenmiş Base64 blob'tur.
/// Not: "Ortam" kavramı 2026-07-15'te kullanıcı kararıyla şimdilik kaldırıldı
/// (roadmap karar günlüğü) — ihtiyaç doğarsa geri gelecek.
/// </summary>
public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Ad { get; set; } = "";
    /// <summary>Hangi motora bağlanılacağı (V3-S1); eski profillerde alan yok → Mssql.</summary>
    public MotorTuru Motor { get; set; } = MotorTuru.Mssql;
    /// <summary>Bağlantı yalnız sunucuyadır; veritabanı profilde tutulmaz, ağaçtan seçilir (FG-1.1, 2026-07-15 kullanıcı kararı). PostgreSQL'de "host" veya "host:port".</summary>
    public string Sunucu { get; set; } = "";
    public KimlikTuru Kimlik { get; set; } = KimlikTuru.Windows;
    public string? KullaniciAdi { get; set; }
    public string? ParolaSifreli { get; set; }
    /// <summary>İşaretliyse yazma/DDL sınıfı batch'ler hiç gönderilmez (FG-1.5).</summary>
    public bool SaltOkunur { get; set; }
    public int BaglantiTimeoutSn { get; set; } = 15;
    /// <summary>0 = sınırsız.</summary>
    public int KomutTimeoutSn { get; set; } = 0;
}
