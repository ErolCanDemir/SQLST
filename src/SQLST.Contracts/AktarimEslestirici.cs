
namespace SQLST.Contracts;

/// <summary>
/// Paket Aktarım SAF mantığı (v12-S1): otomatik kolon eşleştirme + SELECT/INSERT metin kurucuları.
/// UI/IO yok — tümü birim testli. Tırnaklama motorun işidir (<see cref="ILehce.TirnaklaTanimlayici"/>
/// delege ile geçilir); parametre öneki motor kimliğinden türetilir (yalnız Oracle ':' ister).
/// </summary>
public static class AktarimEslestirici
{
    /// <summary>
    /// Otomatik eşleştirme ("akıllı öneri, son karar kullanıcıda"): AYNI ADLI kolonlar eşlenir
    /// (büyük/küçük harf duyarsız). Tip dönüşümü zorlanmaz — uyuşmazlık aktarım sırasında hata
    /// politikasına düşer (ADO.NET çoğu doğal dönüşümü kendisi yapar).
    /// </summary>
    public static IReadOnlyList<AktarimEslesmesi> OtomatikEsle(
        IReadOnlyList<SemaKolonu> kaynak, IReadOnlyList<SemaKolonu> hedef)
    {
        var hedefAdlar = hedef.ToDictionary(k => k.Ad, k => k.Ad, StringComparer.OrdinalIgnoreCase);
        return [.. kaynak
            .Where(k => hedefAdlar.ContainsKey(k.Ad))
            .Select(k => new AktarimEslesmesi(k.Ad, hedefAdlar[k.Ad]))];
    }

    /// <summary>
    /// "şema.ad" biçimindeki tablo adını PARÇA PARÇA tırnaklar (inceleme 2026-07-30, KRİTİK):
    /// ad ham gidince PG/Oracle büyük-küçük harfi katlıyordu — "önce temizle" DELETE'i ve INSERT'ler
    /// aynı adlı KÜÇÜK HARF ikizine (yanlış tabloya) gidebiliyordu. Ad katalogdan geldiği için
    /// tırnaklamak daima doğrudur. Nokta yoksa tek parça tırnaklanır.
    /// </summary>
    public static string TabloYaz(Func<string, string> tirnakla, string tablo)
        => tablo.IndexOfAny(['[', '"', '`']) >= 0
            ? tablo // ZATEN tırnaklı (v13-S4 yeni-tablo yolu önceden tırnaklar) — dokunma, ikilemek adı bozar
            : string.Join(".", tablo.Split('.').Select(tirnakla)); // "db.şema.ad" üç parçalı da olabilir

    /// <summary>Tablo kipinin kaynak SELECT'i: yalnız eşlenen kolonlar, tırnaklı.</summary>
    public static string KaynakSelect(
        Func<string, string> tirnakla, string tablo, IReadOnlyList<AktarimEslesmesi> eslesmeler)
        => $"SELECT {string.Join(", ", eslesmeler.Select(e => tirnakla(e.KaynakKolon)))} FROM {TabloYaz(tirnakla, tablo)}";

    /// <summary>Parametre öneki: Oracle ':' ister; MSSQL/PG(Npgsql)/MySQL '@' kabul eder.</summary>
    public static string ParametreOneki(string motorId)
        => motorId == "oracle" ? ":" : "@";

    /// <summary>
    /// Hedef INSERT şablonu: <c>INSERT INTO t (c1, c2) VALUES (@p0, @p1)</c>. Parametre adları
    /// p0..pN — değerler her satırda aynı hazır komuta yeniden atanır (toplu akış).
    /// </summary>
    public static string HedefInsert(
        Func<string, string> tirnakla, string tablo,
        IReadOnlyList<AktarimEslesmesi> eslesmeler, string parametreOneki)
    {
        string kolonlar = string.Join(", ", eslesmeler.Select(e => tirnakla(e.HedefKolon)));
        string degerler = string.Join(", ", eslesmeler.Select((_, i) => $"{parametreOneki}p{i}"));
        return $"INSERT INTO {TabloYaz(tirnakla, tablo)} ({kolonlar}) VALUES ({degerler})";
    }

    /// <summary>
    /// UPSERT'in UPDATE ayağı (v12-S4): <c>UPDATE t SET c=@p0 WHERE k=@p1 AND ...</c>.
    /// Parametre İNDEKSİ eşleşme sırasıdır (p{i}) — INSERT'le aynı satır değeri aynı ada gider;
    /// komuta parametre EKLEME sırası ise metindeki geçiş sırası olmalıdır (SET'ler, sonra
    /// WHERE'ler): adla bağlamayan sürücü (Oracle varsayılanı) de doğru eşlesin.
    /// Anahtar dışı kolon yoksa null döner — UPDATE kurulamaz, istek UI'da engellenir.
    /// </summary>
    public static string? HedefUpdate(
        Func<string, string> tirnakla, string tablo, IReadOnlyList<AktarimEslesmesi> eslesmeler,
        IReadOnlyList<string> anahtarKolonlar, string parametreOneki)
    {
        var anahtar = new HashSet<string>(anahtarKolonlar, StringComparer.OrdinalIgnoreCase);
        var setler = new List<string>();
        var kosullar = new List<string>();
        for (int i = 0; i < eslesmeler.Count; i++)
        {
            (anahtar.Contains(eslesmeler[i].HedefKolon) ? kosullar : setler)
                .Add($"{tirnakla(eslesmeler[i].HedefKolon)} = {parametreOneki}p{i}");
        }

        if (setler.Count == 0 || kosullar.Count == 0)
            return null;
        return $"UPDATE {TabloYaz(tirnakla, tablo)} SET {string.Join(", ", setler)} WHERE {string.Join(" AND ", kosullar)}";
    }
}
