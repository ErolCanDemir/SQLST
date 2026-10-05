using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.Application.Tests;

/// <summary>V16 (kullanıcı isteği 2026-07-27): SP parametre penceresinden doldurulan EXEC üretimi.</summary>
public class ExecDoluTests
{
    private static SemaNesnesi Sp(params SemaParametresi[] p) => new(
        "DemoDb", "dbo", "MusteriGetir", SemaNesneTuru.StoredProcedure, [], p);

    [Fact]
    public void Tip_duyarli_sarim_sayisal_tirnaksiz_metin_tirnakli()
    {
        SemaNesnesi sp = Sp(
            new SemaParametresi("@id", "int", false),
            new SemaParametresi("@ad", "nvarchar(50)", false));

        string exec = NesneScriptleyici.ExecDolu(sp,
            new Dictionary<string, string?> { ["@id"] = "42", ["@ad"] = "Ali'nin" });

        Assert.Contains("EXEC [dbo].[MusteriGetir]", exec);
        Assert.Contains("@id = 42", exec);              // sayısal tırnaksız
        Assert.Contains("@ad = N'Ali''nin'", exec);     // metin N'…' + ' kaçışlı
    }

    [Fact]
    public void Bos_giris_null_olur()
    {
        SemaNesnesi sp = Sp(new SemaParametresi("@ad", "nvarchar(50)", false));
        string exec = NesneScriptleyici.ExecDolu(sp, new Dictionary<string, string?> { ["@ad"] = "" });
        Assert.Contains("@ad = NULL", exec);
    }

    [Fact]
    public void Cikis_parametresi_degisken_ve_geri_okuma_uretir()
    {
        SemaNesnesi sp = Sp(
            new SemaParametresi("@id", "int", false),
            new SemaParametresi("@sonuc", "int", CikisMi: true));

        string exec = NesneScriptleyici.ExecDolu(sp, new Dictionary<string, string?> { ["@id"] = "1" });

        Assert.Contains("DECLARE @out_sonuc int;", exec);        // çıkış değişkeni bildirildi
        Assert.Contains("@sonuc = @out_sonuc OUTPUT", exec);     // OUTPUT ile geçildi
        Assert.Contains("SELECT @out_sonuc AS [sonuc];", exec);  // sonda geri okundu
    }

    [Fact]
    public void Parametresiz_sp_tek_satir_exec()
        => Assert.Equal("EXEC [dbo].[MusteriGetir];",
            NesneScriptleyici.ExecDolu(Sp(), new Dictionary<string, string?>()));
}
