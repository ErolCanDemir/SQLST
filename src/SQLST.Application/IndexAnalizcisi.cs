using System.Text;

namespace SQLST.Application;

/// <summary>
/// Eksik index analiz çekirdeği (V2-S7; 06-r1 R1.1 + 07-r2 §6 kuralları):
/// skor kovalama (yalnız sıralama), örtüşme analizi (kolon ROLÜNE göre — ham metin
/// karşılaştırma yasak) ve uyarı yorumlu CREATE INDEX script üretimi (tek tuşla
/// ÇALIŞTIRILMAZ — incele + sekmeye kopyala).
/// </summary>
public static class IndexAnalizcisi
{
    /// <summary>sp_BlitzIndex ~500k eşiği referans (06-r1 R1.1 kural 1); düşükler UI'da varsayılan gizli.</summary>
    public static OneriGucu SkorKovasi(double skor) => skor switch
    {
        >= 5_000_000 => OneriGucu.Yuksek,
        >= 500_000 => OneriGucu.Orta,
        _ => OneriGucu.Dusuk,
    };

    /// <summary>last_user_seek 30 günden eskiyse öneri soluklaştırılır (R1.1 kural 4).</summary>
    public static bool SolukMu(DateTime? sonKullanim, DateTime simdiUtc)
        => sonKullanim is null || (simdiUtc - sonKullanim.Value).TotalDays > 30;

    /// <summary>
    /// Örtüşme analizi (07-r2 §6): öncü kolonu aynı mevcut index aranır.
    /// Mevcut index önerinin anahtar-önekini + include'larını kapsıyorsa "zaten var";
    /// anahtar öneki tutuyor ama include eksikse "mevcudu genişlet" önerilir.
    /// Yalnız KESİN kapsama otomatik; kısmi örtüşme insana bırakılır (not yok).
    /// </summary>
    public static OrtusmeSonucu OrtusmeBul(EksikIndexOnerisi oneri, IReadOnlyList<MevcutIndex> mevcutlar)
    {
        IReadOnlyList<string> anahtar = oneri.AnahtarKolonlar;
        if (anahtar.Count == 0)
            return OrtusmeSonucu.Yok;

        foreach (MevcutIndex m in mevcutlar)
        {
            if (!m.Tablo.Equals(oneri.Tablo, StringComparison.OrdinalIgnoreCase)
                || m.AnahtarKolonlar.Count == 0
                || !Ayni(m.AnahtarKolonlar[0], anahtar[0]))
                continue; // kolon SIRASI belirleyici: öncü kolon farklıysa aday bile değil (07-r2 §6)

            bool anahtarOnek = anahtar.Count <= m.AnahtarKolonlar.Count
                && anahtar.Where((k, i) => Ayni(m.AnahtarKolonlar[i], k)).Count() == anahtar.Count;
            if (!anahtarOnek)
                continue;

            var mevcutKapsam = new HashSet<string>(
                m.AnahtarKolonlar.Concat(m.IncludeKolonlar), StringComparer.OrdinalIgnoreCase);
            List<string> eksikInclude = [.. oneri.IncludeKolonlar.Where(k => !mevcutKapsam.Contains(k))];

            return eksikInclude.Count == 0
                ? new OrtusmeSonucu($"zaten kapsanıyor: [{m.Ad}] — yeni index YERİNE bunu kullanın")
                : new OrtusmeSonucu(
                    $"[{m.Ad}] anahtarı kapsıyor — yeni index yerine INCLUDE ekleyin: {string.Join(", ", eksikInclude)}");
        }

        return OrtusmeSonucu.Yok;
    }

    /// <summary>
    /// İncelenmek üzere CREATE INDEX iskeleti (R1.1 kural 3): zorunlu uyarılar yorum
    /// satırlarında — kolon sırası DMV'den gelmez, INCLUDE maliyeti analiz edilmemiştir.
    /// </summary>
    public static string CreateIndexScripti(EksikIndexOnerisi oneri, DateTime uptimeBaslangici)
    {
        string adParcasi = string.Join("_", oneri.AnahtarKolonlar.Take(3));
        string tabloDuz = oneri.Tablo.Replace("[", "").Replace("]", "").Replace(".", "_");
        var sb = new StringBuilder();
        sb.AppendLine("-- SQLST eksik index önerisi — İNCELEMEDEN ÇALIŞTIRMAYIN (R1.1):");
        sb.AppendLine("-- 1) Kolon SIRASI DMV'den gelmez — en seçici kolonu öne alın.");
        sb.AppendLine("-- 2) INCLUDE listesi maliyet analizi yapılmamıştır (\"obez index\" riski) — daraltmayı düşünün.");
        sb.AppendLine($"-- 3) Skor yalnız sıralama içindir; veriler {uptimeBaslangici:dd.MM.yyyy HH:mm}'den beri birikiyor.");
        sb.AppendLine($"USE [{oneri.Veritabani.Replace("]", "]]")}];");
        sb.AppendLine($"CREATE NONCLUSTERED INDEX [IX_{tabloDuz}_{adParcasi}]");
        sb.Append($"ON {oneri.Tablo} ({string.Join(", ", oneri.AnahtarKolonlar.Select(Koseli))})");
        if (oneri.IncludeKolonlar.Count > 0)
        {
            sb.AppendLine();
            sb.Append($"INCLUDE ({string.Join(", ", oneri.IncludeKolonlar.Select(Koseli))})");
        }
        sb.AppendLine(";");
        return sb.ToString();
    }

    /// <summary>DISABLE script'i (R1.1 kural 5: SİL değil — geri dönüşü REBUILD ile kolay).</summary>
    public static string DisableScripti(string tablo, string indexAdi) =>
        $"""
        -- SQLST: bu index restart'tan beri hiç OKUNMAMIŞ ama güncelleme maliyeti ödüyor.
        -- SİLMEYİN — önce DISABLE edin (geri almak: ALTER INDEX ... REBUILD):
        ALTER INDEX [{indexAdi.Replace("]", "]]")}] ON {tablo} DISABLE;
        """;

    private static bool Ayni(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string Koseli(string ad) => $"[{ad.Replace("]", "]]")}]";
}
