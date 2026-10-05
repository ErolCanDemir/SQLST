namespace SQLST.Contracts;

/// <summary>Kaydedilmiş oturumdaki bir sekmenin anlık görüntüsü (FG-3.6).</summary>
public sealed record OturumSekmeKaydi
{
    public required string Baslik { get; init; }
    public string? Veritabani { get; init; }
    public required string Sql { get; init; }
    public bool SeciliMi { get; init; }
    /// <summary>Başlık kullanıcı sorgusundan türetilmeye devam edilsin mi (akıllı adlandırma).</summary>
    public bool OtomatikAd { get; init; } = true;
}

/// <summary>
/// Oturum kurtarma deposu (V2-S2, FG-3.6): kapanışta açık sekmeler kaydedilir,
/// sonraki açılışta geri gelir — çökme/kapanma sorgu metni kaybettirmez.
/// V3: sekmeler bağlantı PROFİLİNE bağlıdır (kullanıcı isteği 2026-07-18) — MSSQL'de
/// açtığın sekmeler Mongo'ya geçince görünmez; her profil kendi çalışma alanını korur.
/// </summary>
public interface IOturumDeposu
{
    /// <summary>Verilen profilin oturumunu tümüyle değiştirir (o profilin kayıtlarını sil + yaz).</summary>
    Task KaydetAsync(
        IReadOnlyList<OturumSekmeKaydi> sekmeler, Guid? profilId = null, CancellationToken ct = default);

    /// <summary>Yalnız verilen profilin sekmeleri (profilsiz eski kayıtlar dönmez).</summary>
    Task<IReadOnlyList<OturumSekmeKaydi>> YukleAsync(Guid? profilId = null, CancellationToken ct = default);
}
