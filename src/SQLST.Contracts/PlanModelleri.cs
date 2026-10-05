namespace SQLST.Contracts;

/// <summary>
/// Execution plan'daki tek bir operatör (V5-S1). <b>Motor-nötrdür:</b> SQL Server showplan
/// XML'i, PostgreSQL <c>EXPLAIN … FORMAT JSON</c>, MySQL <c>EXPLAIN FORMAT=JSON</c>,
/// Oracle <c>PLAN_TABLE</c> ve Mongo <c>explain</c> çıktıları hep bu ortak ağaca çevrilir —
/// görünüm tek, okuyucu motor başına ayrıdır (ÇOKLU MOTOR KURALI).
/// </summary>
/// <param name="Islem">Operatörün adı: "Clustered Index Scan", "Hash Match", "Seq Scan"…</param>
/// <param name="Ayrinti">Nesne/index adı ya da kısa açıklama — operatörün "neyin üstünde" çalıştığı.</param>
/// <param name="MaliyetYuzdesi">
/// Bu operatörün KENDİ maliyetinin toplam plan maliyetine oranı (0-100). Alt ağaç maliyeti
/// DEĞİL: SSMS'teki gibi düğümün kendi payı (alt ağaç − çocukların alt ağaçları).
/// Sıralama/vurgu içindir, "şu kadar sürer" vaadi değildir.
/// </param>
/// <param name="TahminiSatir">
/// Optimizer'ın beklediği satır sayısı. <b><c>null</c> = bu motor tahmin ÜRETMİYOR</b>
/// (MongoDB'nin maliyet/kardinalite modeli yoktur). Sıfır yazmak yanlıştı: "sıfır satır
/// bekleniyordu" ile "beklenti diye bir şey yok" farklı şeylerdir ve sıfır yazıldığında
/// <see cref="SapmaVar"/> sağlıklı her sorguda yanıyordu (A1/B1 bulgusu, 2026-07-19).
/// </param>
/// <param name="GercekSatir">
/// Yalnız GERÇEK planda dolu olur. Tahminden çok saparsa (bkz. <see cref="SapmaVar"/>)
/// istatistikler eskimiş demektir — planın en işe yarar sinyali budur.
/// </param>
/// <param name="Uyarilar">Sunucunun bu operatör için bildirdiği uyarılar (tempdb'ye taşma, tip dönüşümü…).</param>
public sealed record PlanDugumu(
    string Islem,
    string? Ayrinti,
    double MaliyetYuzdesi,
    double? TahminiSatir,
    double? GercekSatir,
    IReadOnlyList<string> Uyarilar,
    IReadOnlyList<PlanDugumu> Cocuklar)
{
    /// <summary>Bu motor tahmin üretiyor mu — görünüm "tahmin" sütununu buna göre gizler.</summary>
    public bool TahminVar => TahminiSatir is not null;

    /// <summary>
    /// Tahmin/gerçek sapması dikkat çekecek düzeyde mi — 10 kat ve üzeri (ve mutlak fark
    /// anlamlıysa). Küçük sayılarda oran yanıltıcıdır (1 → 10 satır "10 kat"tır ama önemsiz).
    ///
    /// <b>Tahmin yoksa sapma da YOKTUR.</b> Karşılaştıracak bir tahmin olmadan "sapma var"
    /// demek uydurma bir sinyaldir — MongoDB'de tam bu oluyordu: tahmin 0 yazıldığı için
    /// 100+ belge dönen her sorgu, mükemmel index'li olsa bile "istatistikler eskimiş
    /// olabilir" uyarısı alıyordu (A1/B1 bulgusu, 2026-07-19).
    /// </summary>
    public bool SapmaVar => GercekSatir is { } g && TahminiSatir is { } t
        && Math.Max(g, t) >= 100
        && (g > t * 10 || t > g * 10);

    /// <summary>Ağaçtaki tüm düğümler (kendisi dahil) — özet/uyarı toplamak için.</summary>
    public IEnumerable<PlanDugumu> Hepsi()
    {
        yield return this;
        foreach (PlanDugumu c in Cocuklar)
        {
            foreach (PlanDugumu torun in c.Hepsi())
                yield return torun;
        }
    }
}

/// <summary>
/// <b>Planın</b> önerdiği eksik index. Yönetim Paneli'ndeki <c>EksikIndexOnerisi</c>'nden
/// AYRI bir tiptir ve bilinçli öyle bırakıldı: o, sunucu genelindeki DMV birikiminden gelir
/// (skor, kullanım sayısı, isteyen sorgu); bu ise ŞU sorgunun planından gelir ve yalnız
/// etki yüzdesi taşır. Aynı isimde tek tip yapmak, iki farklı kaynağı karıştırırdı.
/// </summary>
/// <param name="Etki">Sunucunun bildirdiği tahmini iyileşme yüzdesi — SIRALAMA içindir, vaat değil.</param>
public sealed record PlanEksikIndexi(
    double Etki, string Tablo, IReadOnlyList<string> EsitlikKolonlari,
    IReadOnlyList<string> AralikKolonlari, IReadOnlyList<string> DahilKolonlar)
{
    /// <summary>İncelenmek üzere sekmede açılacak CREATE INDEX script'i (asla kendiliğinden çalıştırılmaz).</summary>
    public string Script()
    {
        IEnumerable<string> anahtar = [.. EsitlikKolonlari, .. AralikKolonlari];
        string dahil = DahilKolonlar.Count > 0
            ? $"{Environment.NewLine}INCLUDE ({string.Join(", ", DahilKolonlar)})"
            : "";
        return $"""
            -- Tahmini etki: %{Etki:F1} — sunucunun ÖNERİSİDİR, ölçmeden uygulamayın.
            -- Her index yazma maliyeti ekler; önce mevcut index'lerle örtüşüyor mu bakın.
            CREATE NONCLUSTERED INDEX [IX_Onerilen] ON {Tablo} ({string.Join(", ", anahtar)}){dahil};
            """;
    }
}

/// <summary>
/// Tek bir ifadenin planı. Bir batch birden çok ifade içerebilir → <see cref="SorguPlani"/> listesi.
/// </summary>
public sealed record IfadePlani(
    string IfadeMetni,
    double ToplamMaliyet,
    PlanDugumu? Kok,
    IReadOnlyList<PlanEksikIndexi> EksikIndexler);

/// <summary>
/// Çözümlenmiş execution plan (V5-S1). <paramref name="Gercek"/> true ise sorgu ÇALIŞTIRILMIŞ
/// ve gerçek satır sayıları vardır; false ise yalnız derlenmiş tahmini plandır (veri değişmez).
///
/// <paramref name="YazmaOlduguIcinCalistirilmadi"/> (kullanıcı kararı 2026-07-20): plan
/// tahminî ise bunun İKİ AYRI sebebi olabilir ve kullanıcıya doğrusu söylenmelidir —
/// (a) motor ölçümlü plan veremiyor (MySQL/Oracle), (b) motor verebiliyor ama ifade
/// <b>yazma/DDL</b> olduğu için araç sorguyu bilerek ÇALIŞTIRMADI. İkisini tek bir "tahmini"
/// etiketiyle geçmek, MSSQL kullanıcısına "bu motor ölçümlü plan vermiyor" gibi <b>yanlış</b>
/// bir bilgi verirdi.
/// </summary>
public sealed record SorguPlani(
    bool Gercek, IReadOnlyList<IfadePlani> Ifadeler, bool YazmaOlduguIcinCalistirilmadi = false)
{
    public IEnumerable<PlanDugumu> TumDugumler =>
        Ifadeler.Where(i => i.Kok is not null).SelectMany(i => i.Kok!.Hepsi());
}
