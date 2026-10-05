using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// Güvenli Yazma Modu'nun (V2-S4, Ö1 ⭐) yürütme çekirdeği — 07-r2 §3 kuralları:
/// gerçek DML açık işlemde çalışır (kesin satır sayısı), karar kullanıcının;
/// hata/iptalde işlem hemen geri alınır — yarım iş asla açık bırakılmaz.
/// UI'sız ve saftır; banner/geri sayım ViewModel'dedir.
///
/// V4-S2: motor bazlıdır. İşlem ifadeleri <see cref="ILehce"/>'den gelir
/// (Oracle'da "begin" yoktur — işlem ilk DML ile örtük başlar). COMMIT öncesi işlem
/// sağlığı denetimi yalnız durumu GERÇEKTEN bilen motorda yapılır
/// (<see cref="ILehce.IslemDurumuBilinir"/>) — bilmeyen motorda "Yok" cevabı
/// "işlem öldü" sanılıp sağlam bir işlem geri alınmamalı.
/// </summary>
public static class GuvenliYazmaYurutucu
{
    /// <summary>
    /// Batch'leri açık işlem içinde çalıştırır. Başarıda işlem AÇIK bırakılır
    /// (kullanıcı COMMIT/ROLLBACK'e karar verir); hata ya da iptalde hemen geri alınır.
    /// </summary>
    public static async Task<QueryResult> CalistirAsync(
        QueryService queryService, ILehce lehce, IDbOturum oturum, IReadOnlyList<SqlBatch> batchler,
        ExecuteOptions opts, CancellationToken ct, int satirTabani = 1)
    {
        if (lehce.IslemBaslatSql is { Length: > 0 } baslat)
        {
            QueryResult tran = await queryService.RunAsync(oturum, baslat, opts, ct);
            if (tran.Hata is not null || tran.IptalEdildi)
                return tran;
        }

        QueryResult sonuc;
        try
        {
            sonuc = await BatchYurutucu.CalistirAsync(
                queryService, oturum, batchler, opts, ct, satirTabani);
        }
        catch (Exception)
        {
            // İnceleme bulgusu 2026-07-30 (KRİTİK): BEGIN TRAN'dan sonra FIRLAYAN istisna (ör. kilit
            // beklerken iptal, OOM) işlemi AÇIK bırakıyordu — sonraki sorgular görünmez işlemin içinde
            // koşup sekme kapanınca sessizce ROLLBACK oluyordu. Güvenli modun sözü: yarım iş asla
            // açık kalmaz — geri al, sonra istisnayı yükselt (VM olağan hata yoluna düşürür).
            await queryService.RunAsync(oturum, lehce.GeriAlSql(), opts, CancellationToken.None);
            throw;
        }

        if (sonuc.Basarili)
            return sonuc; // işlem açık — karar bandı devreye girer

        // Güvenli modun sözü: hatalı/iptal edilen yazma yarım bırakılmaz (iptal
        // token'ı kullanılamaz — geri alma iptal edilemez olmalı).
        QueryResult geriAl = await queryService.RunAsync(oturum, lehce.GeriAlSql(), opts, CancellationToken.None);
        // İnceleme bulgusu 2026-07-30: geri almanın SONUCU denetlenmeden "veri değişmedi" deniyordu —
        // ROLLBACK başarısızsa (kopan bağlantı vb.) dürüst uyarı bas, yalan güvence verme.
        string not = geriAl.Hata is null
            ? "🛡 Güvenli Yazma: işlem geri alındı — veri değişmedi."
            : $"⚠ Güvenli Yazma: GERİ ALMA BAŞARISIZ ({geriAl.Hata.Mesaj}) — işlem durumu belirsiz; bağlantıyı/sunucuyu kontrol edin.";
        return sonuc with { Mesajlar = [.. sonuc.Mesajlar, not] };
    }

    /// <summary>
    /// Kullanıcı kararını uygular. Durumu bilen motorda (MSSQL) COMMIT istense bile önce
    /// <c>XACT_STATE</c>'e bakılır: işlem mahkumsa yalnız ROLLBACK mümkündür, işlem
    /// kalmamışsa durum dürüstçe bildirilir (07-r2 §2). Durumu bilmeyen motorlarda bu
    /// yoklama ATLANIR ve karar doğrudan uygulanır — sunucu hata verirse mesaj kullanıcıya
    /// olduğu gibi taşınır.
    /// </summary>
    public static async Task<string> KararUygulaAsync(
        QueryService queryService, ILehce lehce, IDbOturum oturum, bool commit,
        ExecuteOptions opts, CancellationToken ct = default)
    {
        if (lehce.IslemDurumuBilinir)
        {
            IslemDurumu durum = await oturum.IslemDurumuAsync(ct);
            switch (durum)
            {
                case IslemDurumu.Yok:
                    return "Açık işlem kalmamış (bağlantı kopması/zaman aşımı sunucuda geri almış olabilir) — değişiklik uygulanmadı.";

                case IslemDurumu.Mahkum:
                    await queryService.RunAsync(oturum, lehce.GeriAlSql(), opts, ct);
                    return commit
                        ? "İşlem hasarlı (XACT_STATE = -1) — COMMIT mümkün değil; geri alındı, veri değişmedi."
                        : "↩ ROLLBACK — işlem geri alındı (zaten hasarlıydı).";
            }
        }

        QueryResult karar = await queryService.RunAsync(
            oturum, commit ? lehce.CommitSql : lehce.GeriAlSql(), opts, ct);
        if (karar.Hata is { } hata)
            return $"Karar uygulanamadı: {hata.Mesaj}";
        return commit
            ? "✔ COMMIT — değişiklikler kalıcı."
            : "↩ ROLLBACK — işlem geri alındı, veri değişmedi.";
    }
}
