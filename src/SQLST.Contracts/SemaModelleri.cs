namespace SQLST.Contracts;

public enum SemaNesneTuru
{
    Tablo,
    View,
    StoredProcedure,
    Fonksiyon,
    /// <summary>MongoDB koleksiyonu (V3-S2). SQL menü öğeleri (ALTER/script/tarihçe…) tür
    /// süzgeciyle kendiliğinden gizlenir; koleksiyona özgü öğeler bu türe bağlanır.</summary>
    Koleksiyon,
    /// <summary>
    /// Index tanımı (B5/A6). Şu an yalnız MongoDB metin aramasında kullanılır: orada
    /// "nesne tanımı"nın gerçek karşılıklarından biri index anahtar belgesidir. Nesne
    /// gezgininde bu tür ÜRETİLMEZ — ağaç yapısı değişmedi.
    /// </summary>
    Index,
}

/// <param name="Tip">Görüntülük tam tip adı, ör. "nvarchar(50)", "decimal(18,2)".</param>
/// <param name="FkMi">Kolon bir yabancı anahtarın KAYNAĞI mı (v20-S21 saha m.17). Şema sorgusu
/// doldurmaz; önbellek kurulurken FK listesinden işaretlenir (<c>FkIsaretleyici</c>).</param>
public sealed record SemaKolonu(string Ad, string Tip, bool NullOlabilir, bool PkMi, bool FkMi = false)
{
    /// <summary>Ağaçtaki tek satırlık gösterim (FG-2.2; m.17: FK kolonunda 🔗 — harita ile aynı
    /// glif; PK+FK birlikte olabilir, ikisi de görünür). Yalnız ikon, hedef YAZILMAZ (kullanıcı
    /// 2026-08-14: "nereye bağlı olduğu sağ tık → Kolon Bağları'nda zaten var").</summary>
    public string Gosterim =>
        $"{(PkMi ? "🔑 " : "")}{(FkMi ? "🔗 " : "")}{Ad}  ·  {Tip}{(NullOlabilir ? ", null" : "")}";
}

/// <param name="Ad">"@MusteriId" — başındaki @ ile.</param>
/// <param name="CikisMi">OUTPUT parametresi mi (EXEC iskeletinde işaretlenir — FG-5.6).</param>
public sealed record SemaParametresi(string Ad, string Tip, bool CikisMi);

public sealed record SemaNesnesi(
    string Veritabani,
    string Sema,
    string Ad,
    SemaNesneTuru Tur,
    IReadOnlyList<SemaKolonu> Kolonlar,
    IReadOnlyList<SemaParametresi> Parametreler)
{
    public string TamAd => $"{Sema}.{Ad}";

    /// <summary>Script'lere giden güvenli ad: [şema].[ad] (QUOTENAME mantığı — 00 §4 ders 4).</summary>
    public string TamAdKoseli => $"[{Koseli(Sema)}].[{Koseli(Ad)}]";

    private static string Koseli(string ad) => ad.Replace("]", "]]");
}

/// <summary>
/// Bellekteki şema önbelleği (02-mimari §4.2): ağaç ve anlık arama buradan beslenir,
/// arama ağ gerektirmez. Yenile = yeniden yükle.
/// </summary>
public sealed class SemaOnbellegi
{
    public required IReadOnlyList<SemaNesnesi> Nesneler { get; init; }
    public required DateTime YuklenmeZamaniUtc { get; init; }

    /// <summary>
    /// Veritabanının yabancı anahtar bağları (v2b #8, 2026-07-29). Şemayla birlikte önbelleğe alınır
    /// ki oto-tamamlama JOIN … ON bağlamında "a.XId = b.Id" eşleşmesini ağ gitmeden önersin. Varsayılan
    /// boş — bu bilgiyi doldurmayan yollar (ör. yalnız nesne listeleyen testler) bozulmaz; Mongo'da FK yok.
    /// </summary>
    public IReadOnlyList<YabanciAnahtar> YabanciAnahtarlar { get; init; } = [];
}

/// <param name="SistemMi">master/model/msdb/tempdb — UI bunları "Sistem Veritabanları" düğümünde katlar.</param>
public sealed record VeritabaniBilgisi(string Ad, bool SistemMi);

/// <summary>
/// Bir yabancı anahtar bağı (v6-S2): kaynak tablonun kolonları hedef tablonun kolonlarına
/// işaret eder. Bileşik anahtarda kolon listeleri SIRALI ve aynı uzunluktadır. Görsel Sorgu
/// Tasarımcısı, iki tabloyu bağlarken ON eşleşmesini bundan otomatik önerir.
/// </summary>
public sealed record YabanciAnahtar(
    string KaynakSema, string KaynakTablo, IReadOnlyList<string> KaynakKolonlar,
    string HedefSema, string HedefTablo, IReadOnlyList<string> HedefKolonlar,
    string? Ad = null); // v19-S4: kısıt ADI — hedefteki fazla FK'nın DROP'u için (eski çağıranlar null bırakır)

/// <summary>
/// Bir tablo indeksi (v7-S1 index farkı). PK'yı destekleyen indeks HARİÇ (PK zaten kolon farkında
/// görünür); yalnız anahtar kolonlar, sırayla. Kıyas imzası = benzersizlik + kolon listesidir —
/// indeks ADLARI ortamlar arası değişse de yapısal fark yakalanır.
/// </summary>
public sealed record Indeks(string Sema, string Tablo, string Ad, bool Benzersiz, IReadOnlyList<string> Kolonlar)
{
    public string TamTablo => $"{Sema}.{Tablo}";
}

public interface ISchemaService
{
    /// <summary>Sunucudaki erişilebilir (ONLINE + HAS_DBACCESS) veritabanlarını listeler (FG-2.1 kök düzeyi).</summary>
    Task<IReadOnlyList<VeritabaniBilgisi>> VeritabanlariAsync(ConnectionProfile profil, CancellationToken ct);

    /// <summary>
    /// Verilen veritabanının kullanıcı nesnelerini tek toplu sorguyla okur (FG-2.5).
    /// <paramref name="veritabani"/> null ise profilin bağlandığı veritabanı kullanılır.
    /// </summary>
    Task<SemaOnbellegi> YukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct);

    /// <summary>
    /// Yalnız üst-düzey nesne ADLARINI (alan/kolon envanteri OLMADAN) hızlıca döner — Mongo'da $sample
    /// alan süpürmesinden ÖNCE ağacı hızla doldurmak için (madde 1, 2026-07-30: "koleksiyonlar çok geç
    /// yükleniyor"). SQL ailesinde <see cref="YukleAsync"/> zaten tek batch olduğundan varsayılan onu
    /// çağırır (ayrı hızlı yol gereksiz); yalnız MongoDB gerçek bir hafif yol sunar.
    /// </summary>
    Task<SemaOnbellegi> AdlariYukleAsync(ConnectionProfile profil, string? veritabani, CancellationToken ct)
        => YukleAsync(profil, veritabani, ct);

    /// <summary>
    /// SP/view/fonksiyon gövdesini OBJECT_DEFINITION ile okur (FG-5.1).
    /// Şifreli (WITH ENCRYPTION) veya bulunamayan nesnede null döner.
    /// </summary>
    Task<string?> TanimGetirAsync(ConnectionProfile profil, SemaNesnesi nesne, CancellationToken ct);

    /// <summary>
    /// Edit modu (V2-S5) kolon üst verisi: PK, identity, computed, rowversion.
    /// Tablo bulunamazsa InvalidOperationException.
    /// </summary>
    Task<DuzenlemeMetasi> DuzenlemeMetaAsync(ConnectionProfile profil, SemaNesnesi tablo, CancellationToken ct);

    /// <summary>
    /// Veritabanındaki yabancı anahtar bağları (v6-S2). Görsel Sorgu Tasarımcısı iki tabloyu
    /// bağlarken ON kolonlarını buradan otomatik önerir. MongoDB'de FK yoktur → boş liste.
    /// </summary>
    Task<IReadOnlyList<YabanciAnahtar>> YabanciAnahtarlarAsync(ConnectionProfile profil, string veritabani, CancellationToken ct);

    /// <summary>
    /// Veritabanındaki tablo indeksleri (v7 — şema karşılaştırmada index farkı). PK-destekli indeks
    /// hariç. Motor <see cref="ILehce.IndeksSorgusu"/> vermiyorsa (ya da Mongo) boş liste döner.
    /// </summary>
    Task<IReadOnlyList<Indeks>> IndekslerAsync(ConnectionProfile profil, string veritabani, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Indeks>>([]);
}
