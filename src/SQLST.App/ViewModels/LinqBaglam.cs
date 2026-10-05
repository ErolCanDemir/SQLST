using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// 🔁 LINQ→SQL penceresinin çeviri bağlamı (v20-S11): hedef veritabanının şeması + FK grafı
/// (navigation çözümü) + motorun lehçesi. <see cref="MainViewModel.LinqBaglamiAsync"/> üretir.
/// </summary>
public sealed record LinqBaglam(
    IReadOnlyList<SemaNesnesi> Nesneler,
    IReadOnlyList<YabanciAnahtar> Fkler,
    ILehce Lehce,
    string Veritabani,
    MotorTuru Motor)
{
    /// <summary>SQL editörünün önerileri için şema önbelleği görünümü (OtoTamamlama girdisi).</summary>
    public SemaOnbellegi Onbellek => new()
    {
        Nesneler = Nesneler,
        YabanciAnahtarlar = Fkler,
        YuklenmeZamaniUtc = DateTime.UtcNow,
    };
}
