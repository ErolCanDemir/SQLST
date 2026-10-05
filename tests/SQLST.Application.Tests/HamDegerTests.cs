using System.Globalization;
using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// 🧾 Ham değer sözleşmesi (v23-S13 — kullanıcı kararı 5 Eki 2026: "hiçbir alanı formatlama,
/// db nasıl ise öyle kalsın"): her tipte SSMS'in gösterdiği metin; iş parçacığı kültürü
/// tr-TR iken bile "05.10.2026" / "1250,75" ÜRETİLMEZ.
/// </summary>
public class HamDegerTests
{
    private static readonly DateTime An = new(2026, 10, 5, 14, 23, 11, 123);

    /// <summary>Uygulama tr-TR kültürle çalışır (FOG-9) — testler de o koşulda koşar.</summary>
    private static T TrKulturde<T>(Func<T> is_)
    {
        CultureInfo eski = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try { return is_(); }
        finally { CultureInfo.CurrentCulture = eski; }
    }

    [Theory]
    [InlineData("datetime", "2026-10-05 14:23:11.123")]
    [InlineData("datetime2", "2026-10-05 14:23:11.1230000")]      // ölçek yok → SQL varsayılanı 7
    [InlineData("datetime2(3)", "2026-10-05 14:23:11.123")]
    [InlineData("datetime2(0)", "2026-10-05 14:23:11")]
    [InlineData("smalldatetime", "2026-10-05 14:23:11")]
    [InlineData("DATETIME", "2026-10-05 14:23:11.123")]            // büyük/küçük harf duyarsız
    public void Tarih_tipleri_ssms_bicimi_tr_kulturde_bile(string tip, string beklenen)
        => Assert.Equal(beklenen, TrKulturde(() => HamDeger.Metin(An, tip)));

    [Fact]
    public void Date_saatsiz_oracle_date_saatli_kalir()
    {
        Assert.Equal("2026-10-05", HamDeger.Metin(An.Date, "date"));
        Assert.Equal("2026-10-05 14:23:11.123", HamDeger.Metin(An, "date")); // Oracle DATE saat taşır
    }

    [Fact]
    public void Tip_bilinmiyorsa_iso_ve_kesir_yalniz_varsa()
    {
        Assert.Equal("2026-10-05 14:23:11.123", TrKulturde(() => HamDeger.Metin(An)));
        Assert.Equal("2026-10-05 00:00:00", TrKulturde(() => HamDeger.Metin(An.Date)));
    }

    [Fact]
    public void Sayilar_nokta_ondalikli_binlik_ayracsiz()
    {
        TrKulturde(() =>
        {
            Assert.Equal("1250.75", HamDeger.Metin(1250.75m, "decimal"));
            Assert.Equal("980.00", HamDeger.Metin(980.00m, "decimal"));          // ölçek korunur
            Assert.Equal("1250.7500", HamDeger.Metin(1250.75m, "money"));        // SSMS money 4 hane
            Assert.Equal("3.14", HamDeger.Metin(3.14d, "float"));
            Assert.Equal("1234567", HamDeger.Metin(1234567, "int"));             // "1.234.567" DEĞİL
            Assert.Equal("-42", HamDeger.Metin(-42L, "bigint"));
            return 0;
        });
    }

    [Fact]
    public void Bit_guid_binary_ssms_gibi()
    {
        Assert.Equal("1", HamDeger.Metin(true, "bit"));
        Assert.Equal("0", HamDeger.Metin(false, "bit"));
        Assert.Equal("true", HamDeger.Metin(true, "boolean"));   // PostgreSQL araçları böyle gösterir
        var g = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        Assert.Equal("6F9619FF-8B86-D011-B42D-00C04FC964FF", HamDeger.Metin(g, "uniqueidentifier"));
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", HamDeger.Metin(g, "uuid"));
        Assert.Equal("0x0AFF", HamDeger.Metin(new byte[] { 0x0A, 0xFF }, "varbinary"));
    }

    [Fact]
    public void Time_ve_datetimeoffset_olcekli()
    {
        var saat = new TimeSpan(0, 14, 23, 11, 123);
        Assert.Equal("14:23:11.1230000", HamDeger.Metin(saat, "time"));
        Assert.Equal("14:23:11.123", HamDeger.Metin(saat, "time(3)"));
        Assert.Equal("1.02:00:00", HamDeger.Metin(new TimeSpan(26, 0, 0), "TIME")); // MySQL 24 saati aşar
        var o = new DateTimeOffset(An, TimeSpan.FromHours(3));
        Assert.Equal("2026-10-05 14:23:11.1230000 +03:00", HamDeger.Metin(o, "datetimeoffset"));
    }

    [Fact]
    public void Null_ve_metin_aynen()
    {
        Assert.Equal("NULL", HamDeger.Metin(DBNull.Value, "datetime"));
        Assert.Equal("NULL", HamDeger.Metin(null));
        Assert.Equal("05.10.2026", HamDeger.Metin("05.10.2026", "nvarchar")); // METİN kolonu aynen kalır
    }

    [Theory]
    [InlineData("datetime2", 3, "datetime2(3)")]
    [InlineData("time", 7, "time(7)")]
    [InlineData("datetime", 3, "datetime")]       // datetime'ın kesri sabit — ada eklenmez
    [InlineData("decimal", 2, "decimal")]
    [InlineData("datetime2", null, "datetime2")]
    public void Gorunum_tipi_yalniz_tarih_saat_ailesine_olcek_ekler(string tip, int? olcek, string beklenen)
        => Assert.Equal(beklenen, HamDeger.GorunumTipi(tip, olcek));
}
