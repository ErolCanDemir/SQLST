using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// BF-3: Excel/tablo farkından (<see cref="TabloFarki"/>) motor-doğru INSERT/UPDATE/DELETE üretir.
/// SAF: veritabanına dokunmaz — üretilen metin "Show Script" ekranına ve Güvenli Yazma bandına
/// (BEGIN TRAN … kullanıcı COMMIT/ROLLBACK) gider; böylece "tam eşitleme" öncesi kullanıcı tam
/// olarak ne yazılacağını görür ve geri alabilir. Ayrı bir yürütme yolu yok — <see cref="DmlUretici"/>
/// (edit modu) ile aynı Güvenli Yazma altyapısına oturur.
///
/// Sıra DELETE → UPDATE → INSERT: <see cref="DmlUretici"/> ile aynı kural — silinen bir anahtar aynı
/// batch'te yeniden eklenebilsin (ör. anahtarı aynı ama tümü yeni bir satır). Literaller
/// <see cref="LiteralYazici"/> ile motorun kurallarına göre; NULL kıyası daima <c>IS NULL</c>
/// (<c>= NULL</c> sessizce 0 satır — 07-r2 §4).
///
/// Tasarım kararı (2026-07-27): iyimser eski-değer kıyası (edit modundaki gibi) BF-3'te WHERE'e
/// GİRMEZ. Fark zaten canlı tabloya karşı hesaplandı ve kullanıcı Güvenli Yazma bandında etkilenen
/// satır sayısını görüp COMMIT'ler; niyet "Excel kazansın (tam eşitleme)" olduğundan anahtar tek
/// başına kimliktir. Eski-değer guard'ı eklemek, satır diff'ten sonra değiştiyse UPDATE'i 0 satıra
/// düşürüp "neden güncellemedi?" karışıklığı yaratırdı.
/// </summary>
public static class FarkDmlUretici
{
    /// <param name="yazilamazKolonlar">
    /// COMPUTED/ROWVERSION gibi sunucunun yönettiği, YAZILAMAZ kolonlar — INSERT ve UPDATE'ten çıkarılır
    /// (kod inceleme 2026-07-27: bunlar SET/INSERT'e girerse "computed column güncellenemez" hatası).
    /// null → hiçbiri çıkarılmaz (geriye uyumlu). Kümenin karşılaştırıcısı OrdinalIgnoreCase olmalı.
    /// </param>
    /// <param name="identityInsert">
    /// Anahtar bir IDENTITY kolonu mu — öyleyse INSERT bloğu <c>SET IDENTITY_INSERT … ON/OFF</c> ile sarılır
    /// (yalnız SQL Server; çağıran motoru denetler). Aksi halde identity PK'li tabloya açık ID INSERT'i
    /// "IDENTITY_INSERT OFF" hatası verirdi (kod inceleme 2026-07-27).
    /// </param>
    public static IReadOnlyList<string> Uret(
        ILehce lehce, string sema, string tablo,
        IReadOnlyList<string> anahtarKolonlar, TabloFarki fark, bool silmeDahil,
        IReadOnlySet<string>? yazilamazKolonlar = null, bool identityInsert = false)
    {
        if (anahtarKolonlar.Count == 0)
            throw new ArgumentException("En az bir anahtar kolon gerekir.", nameof(anahtarKolonlar));

        string tamAd = lehce.TamAdYaz(sema, tablo);
        string son = lehce.IfadeSonu;
        bool Yazilabilir(string kolon) => yazilamazKolonlar is null || !yazilamazKolonlar.Contains(kolon);
        var komutlar = new List<string>();

        // DELETE — yalnız tam eşitlemede: hedefte olup Excel'de olmayan satırlar.
        if (silmeDahil)
            foreach (FarkSatiri s in fark.Silinenler)
                komutlar.Add($"DELETE FROM {tamAd} WHERE {AnahtarKosulu(lehce, anahtarKolonlar, s.Degerler)}{son}");

        // UPDATE — anahtar eşleşti, en az bir YAZILABİLİR kolon değişti (computed/rowversion SET'e girmez).
        foreach (DegisenSatir d in fark.Degisenler)
        {
            List<KeyValuePair<string, (object? Eski, object? Yeni)>> setler =
                [.. d.Degisenler.Where(kv => Yazilabilir(kv.Key))];
            if (setler.Count == 0)
                continue; // yalnız yazılamaz kolon "değişmiş" → UPDATE üretme

            var sb = new StringBuilder();
            sb.Append($"UPDATE {tamAd} SET ");
            sb.AppendJoin(", ", setler.Select(kv =>
                $"{lehce.TirnaklaTanimlayici(kv.Key)} = {Literal(lehce, kv.Value.Yeni)}"));
            sb.Append($" WHERE {AnahtarKosulu(lehce, anahtarKolonlar, d.Anahtar)}");
            sb.Append(son);
            komutlar.Add(sb.ToString());
        }

        // INSERT — Excel'de olup hedefte olmayan satırlar: maplı YAZILABİLİR kolonlar.
        var eklemeler = new List<string>();
        foreach (FarkSatiri y in fark.Yeniler)
        {
            List<KeyValuePair<string, object?>> kolonlar = [.. y.Degerler.Where(kv => Yazilabilir(kv.Key))];
            if (kolonlar.Count == 0)
                continue; // yazılabilir kolon kalmadı → sunucu DEFAULT'una bırak (boş INSERT üretme)

            eklemeler.Add(
                $"INSERT INTO {tamAd} ("
                + string.Join(", ", kolonlar.Select(kv => lehce.TirnaklaTanimlayici(kv.Key)))
                + ") VALUES ("
                + string.Join(", ", kolonlar.Select(kv => Literal(lehce, kv.Value)))
                + $"){son}");
        }

        // Identity anahtar → INSERT bloğunu IDENTITY_INSERT ile sar (tek tablo, tek blok — MSSQL).
        if (eklemeler.Count > 0 && identityInsert)
        {
            komutlar.Add($"SET IDENTITY_INSERT {tamAd} ON{son}");
            komutlar.AddRange(eklemeler);
            komutlar.Add($"SET IDENTITY_INSERT {tamAd} OFF{son}");
        }
        else
        {
            komutlar.AddRange(eklemeler);
        }

        return komutlar;
    }

    /// <summary>Önizleme metni ("Show Script") — komutlar satır satır.</summary>
    public static string Onizle(IReadOnlyList<string> komutlar)
        => string.Join(Environment.NewLine, komutlar);

    /// <summary>Anahtar WHERE koşulu: her anahtar kolon = değeri (NULL ise IS NULL), AND'li.</summary>
    private static string AnahtarKosulu(
        ILehce lehce, IReadOnlyList<string> anahtarKolonlar, IReadOnlyDictionary<string, object?> satir)
        => string.Join(" AND ", anahtarKolonlar.Select(k =>
        {
            object? deger = satir.GetValueOrDefault(k);
            return deger is null or DBNull
                ? $"{lehce.TirnaklaTanimlayici(k)} IS NULL"
                : $"{lehce.TirnaklaTanimlayici(k)} = {Literal(lehce, deger)}";
        }));

    private static string Literal(ILehce lehce, object? deger)
        => LiteralYazici.Yaz(deger, lehce.LiteralKurallari);
}
