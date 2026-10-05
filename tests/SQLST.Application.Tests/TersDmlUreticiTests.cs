using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>V15-S4 (BF-1): paketten ters DML üretimi. Üretici SALT METİN döner — çalıştırmaz.</summary>
public class TersDmlUreticiTests
{
    private static GeriAlPaketi Paket(
        string fiil, string kolonlarJson, string pkJson, string satirlarJson, string tablo = "dbo.Musteri") => new()
    {
        TarihUtc = new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
        Sunucu = "s", Veritabani = "db", Tablo = tablo, Fiil = fiil, SqlMetni = "-",
        SatirSayisi = 1, KolonlarJson = kolonlarJson, PkJson = pkJson, SatirlarJson = satirlarJson,
    };

    [Fact]
    public void Update_paketi_pk_ile_eski_degerleri_geri_yazar()
    {
        GeriAlPaketi paket = Paket("UPDATE",
            """[{"Ad":"Id","Tip":"int"},{"Ad":"Ad","Tip":"nvarchar"},{"Ad":"Durum","Tip":"nvarchar"}]""",
            """["Id"]""",
            """[["1","Ahmet Çelik","Aktif"],["2","Ay'şe","Aktif"]]""");

        (string? script, string? hata) = TersDmlUretici.Uret(paket);

        Assert.Null(hata);
        Assert.Contains(
            "UPDATE dbo.Musteri SET [Ad] = N'Ahmet Çelik', [Durum] = N'Aktif' WHERE [Id] = 1;", script);
        Assert.Contains(
            "UPDATE dbo.Musteri SET [Ad] = N'Ay''şe', [Durum] = N'Aktif' WHERE [Id] = 2;", script); // ' kaçışlandı
        Assert.DoesNotContain("[Id] = N'", script); // int tırnaksız
    }

    [Fact]
    public void Delete_paketi_insert_uretir_ve_null_korunur()
    {
        GeriAlPaketi paket = Paket("DELETE",
            """[{"Ad":"Id","Tip":"int"},{"Ad":"Ad","Tip":"nvarchar"},{"Ad":"Tutar","Tip":"decimal"}]""",
            """["Id"]""",
            """[["7","Veli",null],["8",null,"1250.75"]]""");

        (string? script, string? hata) = TersDmlUretici.Uret(paket);

        Assert.Null(hata);
        Assert.Contains("INSERT INTO dbo.Musteri ([Id], [Ad], [Tutar]) VALUES (7, N'Veli', NULL);", script);
        Assert.Contains("INSERT INTO dbo.Musteri ([Id], [Ad], [Tutar]) VALUES (8, NULL, 1250.75);", script);
    }

    [Fact]
    public void Pksiz_update_paketi_reddedilir()
    {
        GeriAlPaketi paket = Paket("UPDATE",
            """[{"Ad":"Ad","Tip":"nvarchar"}]""", """[]""", """[["Ahmet"]]""");

        (string? script, string? hata) = TersDmlUretici.Uret(paket);

        Assert.Null(script);
        Assert.Contains("birincil anahtar yok", hata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bos_paket_anlasilir_mesaj_verir()
    {
        (string? script, string? hata) = TersDmlUretici.Uret(
            Paket("UPDATE", """[{"Ad":"Id","Tip":"int"}]""", """["Id"]""", """[]"""));
        Assert.Null(script);
        Assert.Contains("geri yazılacak satır yok", hata, StringComparison.OrdinalIgnoreCase);
    }

    // --- İnceleme 2026-07-30 bekleyeni: binary/datetime literalleri ---

    [Fact]
    public void Binary_kolon_base64ten_0x_literaline_cevrilir()
    {
        // HucreYaz byte[] → Base64 yazar; N'…' metin literali varbinary'ye METİN yazardı (sessiz bozulma).
        string b64 = Convert.ToBase64String([0x1A, 0x2B, 0xFF]);
        GeriAlPaketi paket = Paket("DELETE",
            """[{"Ad":"Id","Tip":"int"},{"Ad":"Imza","Tip":"binary"}]""",
            """["Id"]""",
            $"""[["5","{b64}"]]""");

        (string? script, string? hata) = TersDmlUretici.Uret(paket);

        Assert.Null(hata);
        Assert.Contains("VALUES (5, 0x1A2BFF);", script);
        Assert.DoesNotContain("N'" + b64, script);
    }

    [Fact]
    public void Datetime_kolon_datetime2_cast_koprusuyle_yazilir()
    {
        // "O" biçimi 7 hane kesir taşır; düz datetime'a N'…' ataması "Conversion failed" verirdi.
        GeriAlPaketi paket = Paket("UPDATE",
            """[{"Ad":"Id","Tip":"int"},{"Ad":"Zaman","Tip":"datetime"}]""",
            """["Id"]""",
            """[["3","2026-07-30T13:45:12.1234567"]]""");

        (string? script, string? hata) = TersDmlUretici.Uret(paket);

        Assert.Null(hata);
        Assert.Contains("[Zaman] = CAST(N'2026-07-30T13:45:12.1234567' AS datetime2)", script);
    }

    [Fact]
    public void Bozuk_base64_null_ve_acik_uyariya_donusur()
    {
        GeriAlPaketi paket = Paket("DELETE",
            """[{"Ad":"Id","Tip":"int"},{"Ad":"Imza","Tip":"binary"}]""",
            """["Id"]""",
            """[["5","bu-base64-degil!"]]""");

        (string? script, string? hata) = TersDmlUretici.Uret(paket);

        Assert.Null(hata);
        Assert.Contains("⚠ ikili değer çözülemedi", script);
    }
}
