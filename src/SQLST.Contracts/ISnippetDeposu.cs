namespace SQLST.Contracts;

/// <summary>
/// Kullanıcı tanımlı kod parçası (V5-S4). Kısayol yazılıp tamamlama listesinden seçilince
/// <see cref="Govde"/> editöre girer.
///
/// <b>Motor kapsamı (çoklu motor kuralı):</b> <see cref="Motor"/> doluysa snippet YALNIZ o
/// motorda önerilir; <c>null</c> ise her motorda. T-SQL kalıbının Mongo sekmesinde çıkması
/// yanlış olurdu — kullanıcı onu seçtiğinde çalışmayan bir metin yapıştırılırdı.
/// </summary>
/// <param name="Kisayol">Editörde yazılan kısa ad (ör. <c>sel100</c>). Motor kapsamında tekildir.</param>
/// <param name="Govde">Genişleyecek metin. <see cref="ImlecIsareti"/> imlecin nereye gideceğini belirler.</param>
public sealed record Snippet(
    long Id,
    string Kisayol,
    string Baslik,
    string Govde,
    MotorTuru? Motor,
    bool Yerlesik)
{
    /// <summary>
    /// Gövdede imlecin konumunu gösteren işaret. Genişletmeden sonra işaret SİLİNİR ve
    /// imleç oraya taşınır; hiç yoksa imleç gövdenin sonunda kalır.
    /// </summary>
    public const string ImlecIsareti = "$0";

    /// <summary>Tamamlama listesinde görünen açıklama — hangi motora ait olduğu da yazar.</summary>
    public string Aciklama => Motor is { } m ? $"{Baslik} ({m})" : Baslik;

    /// <summary>
    /// Gövdeyi imleç işaretinden ayırır: (işaretsiz metin, imlecin metin içindeki ofseti).
    /// İşaret yoksa ofset metnin sonudur.
    /// </summary>
    public (string Metin, int ImlecOfseti) Coz()
    {
        int yer = Govde.IndexOf(ImlecIsareti, StringComparison.Ordinal);
        return yer < 0
            ? (Govde, Govde.Length)
            : (Govde.Remove(yer, ImlecIsareti.Length), yer);
    }
}

/// <summary>Snippet'lerin kalıcı deposu (SQLite). Motor süzmesi ÇAĞIRANIN değil deponun işidir.</summary>
public interface ISnippetDeposu
{
    /// <summary>
    /// Belirtilen motorda kullanılabilir snippet'ler: o motora özel olanlar + motorsuz
    /// (her motorda geçerli) olanlar. Kısayola göre sıralı.
    /// </summary>
    Task<IReadOnlyList<Snippet>> ListeleAsync(MotorTuru motor, CancellationToken ct = default);

    /// <summary>Yönetim ekranı için TÜM snippet'ler (motor süzmesi olmadan).</summary>
    Task<IReadOnlyList<Snippet>> TumunuListeleAsync(CancellationToken ct = default);

    /// <summary>Yeni snippet ekler ve Id'sini döndürür.</summary>
    Task<long> EkleAsync(Snippet snippet, CancellationToken ct = default);

    Task GuncelleAsync(Snippet snippet, CancellationToken ct = default);

    Task SilAsync(long id, CancellationToken ct = default);
}
