using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Kopyalanacak kapsam (v1 hepsi açık gelir; tablolar+PK her zaman dahildir).</summary>
public sealed record SemaKopyaKapsami(
    bool Indexler = true, bool Fkler = true, bool Viewlar = true, bool Spler = true, bool Fonksiyonlar = true);

/// <summary>Plan adımı: başlık (rapor/ilerleme) + SQL + yeniden-denenebilirlik (view/SP bağımlılık sırası).</summary>
public sealed record SemaKopyaAdimi(string Baslik, string Sql, bool YenidenDenenebilir);

public sealed record SemaKopyaSonucu(
    bool Basarili, int ToplamAdim, int BasariliAdim, IReadOnlyList<string> Hatalar, string? GenelHata = null);

/// <summary>
/// 📤 Şema Kopyalama (kullanıcı kararı 2026-07-31: "yalnız şema, öneri A"): kaynak veritabanının
/// ŞEMASINI (tablo+PK+index+FK+view/SP/fonksiyon) başka bir bağlantıdaki hedefe kurar. VERİ KOPYALANMAZ
/// (v1); trigger/kullanıcı/izin kapsam dışıdır (bilinçli sınır). v1 MSSQL→MSSQL'dir — tanımlar
/// OBJECT_DEFINITION'dan T-SQL gelir, başka motora taşınamaz (UI kapılar).
///
/// Sıra: tablolar (CREATE TABLE + PK + index) → FK'lar → view/fonksiyon/SP tanımları. Tanımlar
/// birbirine bağımlı olabilir (view'ın view'ı) — deterministik sıralama yerine İKİ TUR yeniden deneme:
/// ilk turda kalanlar bağımlılıkları kurulunca ikinci turda geçer; yine kalan dürüstçe raporlanır.
/// Hedef veritabanı yoksa lehçenin CREATE DATABASE'iyle oluşturulur.
/// </summary>
public class SemaKopyalamaServisi(
    ISchemaService semaServisi, TeshisServisi teshisServisi, ISqlExecutor executor, ILehceSaglayici lehceler)
{
    /// <summary>
    /// Kaynaktan sıralı script planı üretir (yürütme YOK — "Script'i kaydet" de bunu kullanır).
    /// Şifreli/okunamayan tanımlar plana giremez; <paramref name="uyarilar"/>'a düşer.
    /// </summary>
    public async Task<IReadOnlyList<SemaKopyaAdimi>> PlanUretAsync(
        ConnectionProfile kaynak, string kaynakDb, SemaKopyaKapsami kapsam,
        List<string> uyarilar, IProgress<string>? ilerleme, CancellationToken ct)
    {
        var plan = new List<SemaKopyaAdimi>();

        ilerleme?.Report($"[{kaynakDb}] şeması okunuyor…");
        SemaOnbellegi sema = await semaServisi.YukleAsync(kaynak, kaynakDb, ct);
        IReadOnlyList<MevcutIndex> indexler = kapsam.Indexler
            ? await teshisServisi.MevcutIndexlerAsync(kaynak, kaynakDb, ct)
            : [];

        // 1) Tablolar: CREATE TABLE + PK (+ index) — kolon üst verisi (identity/computed) meta'dan.
        IReadOnlyList<SemaNesnesi> tablolar = [.. sema.Nesneler.Where(n => n.Tur == SemaNesneTuru.Tablo)];
        int i = 0;
        foreach (SemaNesnesi t in tablolar)
        {
            ct.ThrowIfCancellationRequested();
            ilerleme?.Report($"Tablo okunuyor ({++i}/{tablolar.Count}): {t.TamAd}");
            DuzenlemeMetasi meta = await semaServisi.DuzenlemeMetaAsync(kaynak, t, ct);
            plan.Add(new SemaKopyaAdimi($"Tablo {t.TamAd}",
                NesneScriptleyici.CreateTableScripti(meta, indexler), YenidenDenenebilir: false));
        }

        // 2) FK'lar — tüm tablolar kurulduktan sonra.
        if (kapsam.Fkler)
        {
            IReadOnlyList<YabanciAnahtar> fkler = await semaServisi.YabanciAnahtarlarAsync(kaynak, kaynakDb, ct);
            for (int f = 0; f < fkler.Count; f++)
                plan.Add(new SemaKopyaAdimi($"FK {fkler[f].KaynakTablo} → {fkler[f].HedefTablo}",
                    NesneScriptleyici.FkScripti(fkler[f], f + 1), YenidenDenenebilir: false));
        }

        // 3) View / fonksiyon / SP tanımları (ham CREATE) — bağımlılık için yeniden-denenebilir.
        foreach (SemaNesnesi n in sema.Nesneler)
        {
            bool dahil = n.Tur switch
            {
                SemaNesneTuru.View => kapsam.Viewlar,
                SemaNesneTuru.Fonksiyon => kapsam.Fonksiyonlar,
                SemaNesneTuru.StoredProcedure => kapsam.Spler,
                _ => false,
            };
            if (!dahil)
                continue;
            ct.ThrowIfCancellationRequested();
            string? tanim = await semaServisi.TanimGetirAsync(kaynak, n, ct);
            if (tanim is null)
                uyarilar.Add($"{n.Tur} {n.TamAd}: tanım okunamadı (şifreli olabilir) — plana alınmadı.");
            else
                plan.Add(new SemaKopyaAdimi($"{n.Tur} {n.TamAd}", tanim, YenidenDenenebilir: true));
        }

        return plan;
    }

    /// <summary>Planı hedefte çalıştırır. Hedef DB yoksa oluşturur; adım hataları toplanır (ilk 30).</summary>
    public async Task<SemaKopyaSonucu> KopyalaAsync(
        ConnectionProfile kaynak, string kaynakDb, ConnectionProfile hedef, string hedefDb,
        SemaKopyaKapsami kapsam, IProgress<string>? ilerleme, CancellationToken ct)
    {
        var hatalar = new List<string>();
        IReadOnlyList<SemaKopyaAdimi> plan;
        try
        {
            plan = await PlanUretAsync(kaynak, kaynakDb, kapsam, hatalar, ilerleme, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return new SemaKopyaSonucu(false, 0, 0, hatalar, $"Kaynak şeması okunamadı: {ex.Message}");
        }

        // Hedef DB yoksa oluştur (＋ Yeni veritabanı altyapısı — 2026-07-31).
        try
        {
            IReadOnlyList<VeritabaniBilgisi> mevcutlar = await semaServisi.VeritabanlariAsync(hedef, ct);
            if (!mevcutlar.Any(v => v.Ad.Equals(hedefDb, StringComparison.OrdinalIgnoreCase)))
            {
                if (lehceler.Getir(hedef.Motor).VeritabaniOlusturSql(hedefDb) is not { } olusturSql)
                    return new SemaKopyaSonucu(false, plan.Count, 0, hatalar,
                        "Hedef motorda veritabanı oluşturma desteklenmiyor — hedef DB'yi önceden açın.");
                ilerleme?.Report($"Hedefte [{hedefDb}] oluşturuluyor…");
                QueryResult olustur = await executor.ExecuteAsync(hedef, olusturSql, new ExecuteOptions(), ct);
                if (olustur.Hata is { } oh)
                    return new SemaKopyaSonucu(false, plan.Count, 0, hatalar, $"Hedef DB oluşturulamadı: {oh.Mesaj}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return new SemaKopyaSonucu(false, plan.Count, 0, hatalar, $"Hedefe bağlanılamadı: {ex.Message}");
        }

        // Yürütme: tek tur + (yalnız yeniden-denenebilirler için) ikinci tur.
        int basarili = 0;
        var kalanlar = new List<SemaKopyaAdimi>();
        var opts = new ExecuteOptions { VeritabaniOverride = hedefDb };
        int adimNo = 0;
        foreach (SemaKopyaAdimi adim in plan)
        {
            ct.ThrowIfCancellationRequested();
            ilerleme?.Report($"({++adimNo}/{plan.Count}) {adim.Baslik}");
            QueryResult s = await executor.ExecuteAsync(hedef, adim.Sql, opts, ct);
            if (s.Hata is null)
                basarili++;
            else if (adim.YenidenDenenebilir)
                kalanlar.Add(adim); // bağımlılık sırası — 2. turda dene
            else if (hatalar.Count < 30)
                hatalar.Add($"{adim.Baslik}: {s.Hata.Mesaj}");
        }

        foreach (SemaKopyaAdimi adim in kalanlar)
        {
            ct.ThrowIfCancellationRequested();
            ilerleme?.Report($"(2. tur) {adim.Baslik}");
            QueryResult s = await executor.ExecuteAsync(hedef, adim.Sql, opts, ct);
            if (s.Hata is null)
                basarili++;
            else if (hatalar.Count < 30)
                hatalar.Add($"{adim.Baslik}: {s.Hata.Mesaj}");
        }

        return new SemaKopyaSonucu(hatalar.Count == 0, plan.Count, basarili, hatalar);
    }

    /// <summary>Planı tek .sql metnine çevirir (bonus C: "Script'i kaydet" — incele/başka yerde çalıştır).</summary>
    public static string TekScript(IReadOnlyList<SemaKopyaAdimi> plan, string kaynakDb)
        => $"-- SQLST şema kopyası · kaynak: [{kaynakDb}] · {plan.Count} adım\n\n"
         + string.Join("\nGO\n\n", plan.Select(a => $"-- {a.Baslik}\n{a.Sql.TrimEnd()}"));
}
