using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// Aktivite/Denetim ekranında bir geçmiş kaydının "kim ne iş yapmış" gösterimi (v10-S3):
/// <see cref="GecmisKaydi"/>'yi işlem türüyle (<see cref="IslemSiniflayici"/>) zenginleştirir.
/// Grid kolonları ve süzgeç bunun üstünden çalışır.
/// </summary>
public sealed class DenetimKaydiGorunumu(GecmisKaydi kayit, IslemTuru tur)
{
    public string Zaman => kayit.BaslangicUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    public string Kullanici => kayit.Kullanici ?? "?";
    public string Tur => IslemSiniflayici.Etiket(tur);

    /// <summary>Yazma işlemi mi (INSERT/UPDATE/DELETE/DDL) — "yalnız yazma" süzgeci için.</summary>
    public bool Yazma => tur is IslemTuru.Insert or IslemTuru.Update or IslemTuru.Delete or IslemTuru.Ddl;

    public string Veritabani => kayit.Veritabani ?? "";

    public string Durum => kayit.Durum switch
    {
        GecmisDurumu.Basarili => "✔",
        GecmisDurumu.Hata => "✖ hata",
        _ => "iptal",
    };

    public int SureMs => kayit.SureMs;
    public int SatirSayisi => kayit.SatirSayisi;
    public string Sql => kayit.Sql;

    /// <summary>Grid için tek satıra indirgenmiş kısa SQL (tam metni tooltip'te).</summary>
    public string SqlKisa
    {
        get
        {
            string tek = kayit.Sql.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return tek.Length <= 140 ? tek : tek[..140] + "…";
        }
    }

    /// <summary>Serbest metin süzgeci: kullanıcı/tür/db/SQL içinde geçiyor mu (büyük/küçük duyarsız).</summary>
    public bool Eslesir(string q)
        => Kullanici.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Tur.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Veritabani.Contains(q, StringComparison.OrdinalIgnoreCase)
        || kayit.Sql.Contains(q, StringComparison.OrdinalIgnoreCase);
}
