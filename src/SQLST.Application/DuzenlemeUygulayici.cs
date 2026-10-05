using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Edit modu değişiklik uygulayıcısı (V2-S5): komutlar TEK transaction'da, TEK TEK
/// çalışır; her komutun tam 0 satır etkilemesi ÇAKIŞMA demektir (07-r2 §4:
/// etkilenen satır 0 → satır başkası tarafından değişti) — tümü geri alınır.
///
/// V4-S1: işlem ifadeleri motordan gelir. <see cref="ILehce.IslemBaslatSql"/> boşsa
/// (Oracle) işlem ilk DML ile örtük başlar ve ayrı bir "begin" gönderilmez. COMMIT
/// öncesi işlem sağlığı denetimi yalnız durumu GERÇEKTEN bilen motorlarda yapılır
/// (<see cref="ILehce.IslemDurumuBilinir"/>) — bilmeyen motorda "Yok" cevabı
/// "işlem öldü" sanılıp sağlam bir işlem geri alınmamalı.
/// </summary>
public static class DuzenlemeUygulayici
{
    public static async Task<(bool Basarili, string Mesaj)> UygulaAsync(
        QueryService queryService, ILehce lehce, IDbOturum oturum, IReadOnlyList<string> komutlar,
        ExecuteOptions opts, CancellationToken ct)
    {
        if (komutlar.Count == 0)
            return (true, "Uygulanacak değişiklik yok.");

        if (lehce.IslemBaslatSql is { Length: > 0 } baslat)
        {
            QueryResult tran = await queryService.RunAsync(oturum, baslat, opts, ct);
            if (tran.Hata is not null || tran.IptalEdildi)
                return (false, $"İşlem açılamadı: {tran.Hata?.Mesaj ?? "iptal edildi"}");
        }

        for (int i = 0; i < komutlar.Count; i++)
        {
            QueryResult parca = await queryService.RunAsync(oturum, komutlar[i], opts, ct);

            if (parca.Hata is { } hata)
                return (false, await GeriAlAsync(queryService, lehce, oturum, opts,
                    $"{i + 1}. komut hatası: {hata.Mesaj}"));
            if (parca.IptalEdildi)
                return (false, await GeriAlAsync(queryService, lehce, oturum, opts, "İptal edildi"));
            if (parca.EtkilenenSatir is 0)
            {
                return (false, await GeriAlAsync(queryService, lehce, oturum, opts,
                    $"Çakışma ({i + 1}. komut, 0 satır): satır başkası tarafından değiştirilmiş ya da silinmiş olabilir — grid'i yenileyip tekrar deneyin"));
            }
        }

        if (lehce.IslemDurumuBilinir && await oturum.IslemDurumuAsync(ct) != IslemDurumu.Acik)
            return (false, "İşlem beklenmedik şekilde kapandı (bağlantı kopması olabilir) — değişiklikler uygulanmadı; grid'i yenileyin.");

        QueryResult commit = await queryService.RunAsync(oturum, lehce.CommitSql, opts, CancellationToken.None);
        return commit.Hata is { } commitHatasi
            ? (false, await GeriAlAsync(queryService, lehce, oturum, opts, $"COMMIT hatası: {commitHatasi.Mesaj}"))
            : (true, $"✔ {komutlar.Count} değişiklik uygulandı (tek işlemde).");
    }

    private static async Task<string> GeriAlAsync(
        QueryService queryService, ILehce lehce, IDbOturum oturum, ExecuteOptions opts, string neden)
    {
        // Geri alma iptal edilemez olmalı — yarım iş bırakılmaz
        await queryService.RunAsync(oturum, lehce.GeriAlSql(), opts, CancellationToken.None);
        return $"{neden} — TÜM değişiklikler geri alındı, veri değişmedi.";
    }
}
